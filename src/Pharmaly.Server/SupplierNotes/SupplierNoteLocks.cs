using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Server.Access;
using Pharmaly.Server.Operations;

namespace Pharmaly.Server.SupplierNotes;

/// <summary>Bon lu et verrouillé (FOR UPDATE) dans la transaction d'une commande.</summary>
internal sealed class HeldNote
{
    public Guid Id { get; init; }
    public long Number { get; init; }
    public Guid SupplierId { get; init; }
    public Guid LocationId { get; init; }
    public DateTime NoteDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public Guid? LockEmployeeId { get; init; }
    public string? LockEmployeeName { get; init; }
    public string? LockRegisterName { get; init; }

    /// <summary>Le bail court encore.</summary>
    public bool LockActive { get; init; }
}

/// <summary>
/// Verrou à bail d'un bon : un seul employé à la fois.
/// <para>
/// Le verrou appartient à un employé (le poste n'est noté que pour l'affichage) : le même
/// employé reprend son bon à tout moment, depuis n'importe quel poste. Un autre employé
/// ne peut le prendre qu'après l'échéance du bail, ou après une libération. Tant que
/// personne d'autre ne l'a pris, l'employé qui le tenait peut encore écrire : son écriture
/// renouvelle le bail.
/// </para>
/// <para>
/// Toute écriture commence par verrouiller la ligne du bon dans la base : les commandes
/// sur un même bon passent une par une, et le contrôle du verrou est exact.
/// </para>
/// </summary>
public sealed class SupplierNoteLocks(NpgsqlDataSource dataSource, SupplierNoteOptions options)
{
    internal static async Task<HeldNote> ReadForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid noteId) =>
        await connection.QuerySingleOrDefaultAsync<HeldNote>(
            """
            SELECT n.id AS Id, n.number AS Number, n.supplier_id AS SupplierId, n.location_id AS LocationId,
                   n.note_date::timestamp AS NoteDate, n.status AS Status, n.lock_employee_id AS LockEmployeeId,
                   holder.full_name AS LockEmployeeName, ra.display_name AS LockRegisterName,
                   COALESCE(n.lock_expires_at > clock_timestamp(), false) AS LockActive
            FROM supplier_notes n
            LEFT JOIN employees holder ON holder.id = n.lock_employee_id
            LEFT JOIN register_accounts ra ON ra.id = n.lock_register_account_id
            WHERE n.id = @noteId
            FOR UPDATE OF n
            """,
            new { noteId },
            transaction) ?? throw NoteAccess.NoteNotFound();

    /// <summary>Exige que l'employé tienne le bon, et renouvelle son bail.</summary>
    internal async Task RequireHeldAsync(OperationContext operation, HeldNote note)
    {
        RequireHolder(note, operation.EmployeeId);
        await TakeAsync(operation.Connection, operation.Transaction, note, operation.Identity);
    }

    internal static Task ReleaseAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid noteId) =>
        connection.ExecuteAsync(
            """
            UPDATE supplier_notes
            SET lock_employee_id = NULL, lock_register_account_id = NULL, lock_acquired_at = NULL, lock_expires_at = NULL
            WHERE id = @noteId
            """,
            new { noteId },
            transaction);

    /// <summary>Donne le bon à l'employé de la demande ; l'heure de prise est gardée s'il le tenait déjà.</summary>
    internal Task TakeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, HeldNote note, RequestIdentity identity) =>
        connection.ExecuteAsync(
            """
            UPDATE supplier_notes
            SET lock_acquired_at = CASE WHEN lock_employee_id = @employeeId THEN lock_acquired_at ELSE clock_timestamp() END,
                lock_employee_id = @employeeId,
                lock_register_account_id = @RegisterAccountId,
                lock_expires_at = clock_timestamp() + make_interval(secs => @leaseSeconds)
            WHERE id = @Id
            """,
            new { note.Id, employeeId = identity.RequireEmployeeId(), identity.RegisterAccountId, leaseSeconds = options.LockLease.TotalSeconds },
            transaction);

    /// <summary>Prendre ou reprendre le bon. Renvoie le bon entier, tel qu'il est à cet instant.</summary>
    public Task<IResult> AcquireAsync(Guid noteId, RequestIdentity identity) =>
        RunAsync(async (connection, transaction) =>
        {
            var note = await ReadForUpdateAsync(connection, transaction, noteId);
            if (note.Status == SupplierNoteStatuses.Cancelled)
            {
                throw NoteCancelled();
            }

            if (note.LockEmployeeId is { } holder && holder != identity.EmployeeId && note.LockActive)
            {
                throw LockedByOther(note);
            }

            await TakeAsync(connection, transaction, note, identity);
            return Results.Ok(await SupplierNoteQueries.ReadDetailAsync(connection, transaction, noteId, identity.RequireEmployeeId()));
        });

    /// <summary>Battement : prolonge le bail de l'employé qui tient le bon.</summary>
    public Task<IResult> RenewAsync(Guid noteId, RequestIdentity identity) =>
        RunAsync(async (connection, transaction) =>
        {
            var note = await ReadForUpdateAsync(connection, transaction, noteId);
            RequireHolder(note, identity.RequireEmployeeId());
            await TakeAsync(connection, transaction, note, identity);
            var summary = await SupplierNoteQueries.ReadSummaryAsync(connection, transaction, noteId, identity.RequireEmployeeId());
            return Results.Ok(summary!.Lock);
        });

    /// <summary>Libération par l'employé qui tient le bon. Sans effet si le bon est déjà libre.</summary>
    public Task<IResult> ReleaseAsync(Guid noteId, RequestIdentity identity) =>
        RunAsync(async (connection, transaction) =>
        {
            var note = await ReadForUpdateAsync(connection, transaction, noteId);
            if (note.LockEmployeeId == identity.EmployeeId)
            {
                await ReleaseAsync(connection, transaction, noteId);
            }
            else if (note.LockEmployeeId is not null && note.LockActive)
            {
                throw LockedByOther(note);
            }

            return Results.NoContent();
        });

    /// <summary>Libération d'un bon tenu par quelqu'un d'autre : réservée à « bons.modifier », auditée.</summary>
    public Task<IResult> ForceReleaseAsync(Guid noteId, RequestIdentity identity) =>
        RunAsync(async (connection, transaction) =>
        {
            var note = await ReadForUpdateAsync(connection, transaction, noteId);
            if (note.LockEmployeeId is { } holder)
            {
                await ReleaseAsync(connection, transaction, noteId);
                var description = $"Verrou de {note.LockEmployeeName} (poste « {note.LockRegisterName} ») libéré de force.";
                var details = new { employeDessaisi = holder, nomEmployeDessaisi = note.LockEmployeeName, poste = note.LockRegisterName, bailEnCours = note.LockActive };
                await connection.ExecuteAsync(
                    """
                    INSERT INTO supplier_note_history (note_id, event, status_before, status_after, description, details, employee_id)
                    VALUES (@noteId, @event, @Status, @Status, @description, @details::jsonb, @employeeId)
                    """,
                    new
                    {
                        noteId,
                        @event = SupplierNoteEvents.LockForced,
                        note.Status,
                        description,
                        details = System.Text.Json.JsonSerializer.Serialize(details),
                        employeeId = identity.RequireEmployeeId(),
                    },
                    transaction);
                await AuditLog.WriteAsync(
                    connection,
                    new AuditEntry(
                        SupplierNoteAuditActions.LockForced, identity.EmployeeId, identity.RegisterAccountId, identity.StoreId,
                        "supplier_note", noteId.ToString(), details),
                    transaction);
            }

            return Results.Ok(await SupplierNoteQueries.ReadSummaryAsync(connection, transaction, noteId, identity.RequireEmployeeId()));
        });

    internal static BusinessException NoteCancelled() =>
        new(SupplierNoteErrorCodes.NoteCancelled, "Ce bon est annulé : il ne peut plus être modifié.", StatusCodes.Status409Conflict);

    private static void RequireHolder(HeldNote note, Guid employeeId)
    {
        if (note.LockEmployeeId == employeeId)
        {
            return;
        }

        throw note.LockEmployeeId is not null && note.LockActive
            ? LockedByOther(note)
            : new BusinessException(
                SupplierNoteErrorCodes.LockNotHeld,
                "Vous ne tenez pas ce bon. Reprenez-le avant de le modifier.",
                StatusCodes.Status409Conflict);
    }

    private static BusinessException LockedByOther(HeldNote note) =>
        new(
            SupplierNoteErrorCodes.LockedByOther,
            $"Ce bon est en cours de saisie par {note.LockEmployeeName} (poste « {note.LockRegisterName} »).",
            StatusCodes.Status409Conflict);

    private async Task<IResult> RunAsync(Func<NpgsqlConnection, NpgsqlTransaction, Task<IResult>> action)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var result = await action(connection, transaction);
            await transaction.CommitAsync();
            return result;
        }
        catch (BusinessException exception)
        {
            await transaction.RollbackAsync();
            return Results.Json(exception.ToApiError(), statusCode: exception.StatusCode);
        }
    }
}
