using System.Data;
using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Access;
using Pharmaly.Server.Operations;

namespace Pharmaly.Server.SupplierNotes;

/// <summary>Réglages des bons fournisseurs.</summary>
public sealed class SupplierNoteOptions
{
    public const string LeaseSecondsSetting = "Bons:DureeBailVerrouSecondes";
    public static readonly TimeSpan DefaultLockLease = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Durée du bail du verrou d'un bon. Le poste le renouvelle par un battement tant que
    /// l'écran est ouvert ; passé ce délai sans battement, un autre employé peut prendre le bon.
    /// </summary>
    public TimeSpan LockLease { get; set; } = DefaultLockLease;

    public static SupplierNoteOptions From(IConfiguration configuration) =>
        configuration.GetValue<double?>(LeaseSecondsSetting) is > 0 and var seconds
            ? new SupplierNoteOptions { LockLease = TimeSpan.FromSeconds(seconds) }
            : new SupplierNoteOptions();
}

public static class SupplierNoteAuditActions
{
    public const string SupplierCreated = "FOURNISSEUR_CREE";
    public const string NoteCreated = "BON_CREE";
    public const string LineAdded = "BON_LIGNE_AJOUTEE";
    public const string LineUpdated = "BON_LIGNE_MODIFIEE";
    public const string LineCancelled = "BON_LIGNE_ANNULEE";
    public const string NoteValidated = "BON_VALIDE";
    public const string NoteCancelled = "BON_ANNULE";
    public const string LockForced = "BON_VERROU_FORCE";
}

public static class SupplierNoteServices
{
    public static IServiceCollection AddSupplierNotes(this IServiceCollection services) =>
        services
            .AddSingleton(provider => SupplierNoteOptions.From(provider.GetRequiredService<IConfiguration>()))
            .AddSingleton<SupplierNoteQueries>()
            .AddSingleton<SupplierNoteLocks>()
            .AddSingleton<SupplierNoteCommands>();
}

/// <summary>Droits sur les bons que l'employé possède pour cette demande, et état du bon lu avant la commande.</summary>
internal sealed record NoteGate(string Status, IReadOnlySet<string> Granted);

/// <summary>
/// Contrôle des droits sur les bons. Un droit peut être limité à un site : il doit couvrir
/// le site du poste ET celui de l'emplacement du bon. Tout refus est audité.
/// </summary>
internal static class NoteAccess
{
    public static readonly string[] WriteRights =
        [Permissions.NotesEnter, Permissions.NotesEdit, Permissions.NotesValidate, Permissions.NotesCancel];

    /// <summary>Qui peut écrire un bon peut le lire : la lecture accepte n'importe quel droit sur les bons.</summary>
    public static readonly string[] ReadRights = [Permissions.NotesView, .. WriteRights];

    /// <summary>Saisie ordinaire tant que le bon est en saisie ; ensuite, c'est une correction.</summary>
    public static string[] LineRights(string status) =>
        status == SupplierNoteStatuses.Validated ? [Permissions.NotesEdit] : [Permissions.NotesEnter];

    public static async Task<IReadOnlySet<string>> GrantedAsync(
        IDbConnection connection, RequestIdentity identity, Guid? noteStoreId, IDbTransaction? transaction = null) =>
        (await connection.QueryAsync<string>(
            """
            SELECT ep.permission_code
            FROM employee_permissions ep
            WHERE ep.employee_id = @employeeId AND ep.permission_code = ANY(@codes)
            GROUP BY ep.permission_code
            HAVING bool_or(ep.store_id IS NULL OR ep.store_id = @posteStoreId)
               AND bool_or(ep.store_id IS NULL OR ep.store_id = @noteStoreId)
            """,
            new
            {
                employeeId = identity.EmployeeId,
                codes = ReadRights,
                posteStoreId = identity.StoreId,
                noteStoreId = noteStoreId ?? identity.StoreId,
            },
            transaction)).ToHashSet();

    /// <summary>Refus audité si l'employé n'a aucun des droits demandés ; sinon null.</summary>
    public static async Task<IResult?> RefuseUnlessAnyAsync(
        NpgsqlConnection connection, HttpContext context, IReadOnlySet<string> granted, string[] anyOf)
    {
        if (anyOf.Any(granted.Contains))
        {
            return null;
        }

        var identity = context.GetIdentity();
        await AuditLog.WriteAsync(
            connection,
            new AuditEntry(
                AuditActions.PermissionDenied, identity.EmployeeId, identity.RegisterAccountId, identity.StoreId,
                Details: new { permission = string.Join(" ou ", anyOf), chemin = context.Request.Path.Value }));
        return Results.Json(Denied().ToApiError(), statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>Contrôle sans bon (liste, fournisseurs, recherche de produit) : droit sur le site du poste.</summary>
    public static async Task<IResult?> RefuseUnlessAnyAsync(NpgsqlDataSource dataSource, HttpContext context, string[] anyOf)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        return await RefuseUnlessAnyAsync(connection, context, await GrantedAsync(connection, context.GetIdentity(), null), anyOf);
    }

    /// <summary>
    /// Lit l'état du bon et les droits de l'employé sur son site, et refuse (avec audit) si
    /// aucun des droits voulus pour cet état n'est accordé.
    /// </summary>
    public static async Task<(NoteGate? Gate, IResult? Refusal)> OpenAsync(
        NpgsqlDataSource dataSource, HttpContext context, Guid noteId, Func<string, string[]> anyOfForStatus)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        var found = (await connection.QueryAsync<(string Status, Guid StoreId)>(
            """
            SELECT n.status, l.store_id
            FROM supplier_notes n JOIN stock_locations l ON l.id = n.location_id
            WHERE n.id = @noteId
            """,
            new { noteId })).AsList();
        if (found.Count == 0)
        {
            return (null, Results.Json(NoteNotFound().ToApiError(), statusCode: StatusCodes.Status404NotFound));
        }

        var note = found[0];
        var granted = await GrantedAsync(connection, context.GetIdentity(), note.StoreId);
        return await RefuseUnlessAnyAsync(connection, context, granted, anyOfForStatus(note.Status)) is { } refusal
            ? (null, refusal)
            : (new NoteGate(note.Status, granted), null);
    }

    public static BusinessException Denied() =>
        new(ApiErrorCodes.PermissionDenied, "Vous n'avez pas le droit d'effectuer cette action.", StatusCodes.Status403Forbidden);

    public static BusinessException NoteNotFound() => BusinessException.NotFound("Bon introuvable.");
}

/// <summary>Variation de marge d'une ligne : calcul exact en décimal.</summary>
public static class MarginCalculator
{
    public static MarginVariation Compute(decimal salePrice, decimal? previousPurchasePrice, decimal newPurchasePrice)
    {
        decimal? marginBefore = previousPurchasePrice is { } previous ? salePrice - previous : null;
        var marginAfter = salePrice - newPurchasePrice;
        return new MarginVariation(
            salePrice, previousPurchasePrice, newPurchasePrice,
            marginBefore, marginBefore is { } before ? Percent(before, salePrice) : null,
            marginAfter, Percent(marginAfter, salePrice));
    }

    public static decimal LineTotal(decimal quantity, decimal purchasePrice) =>
        decimal.Round(quantity * purchasePrice, 2, MidpointRounding.AwayFromZero);

    private static decimal? Percent(decimal margin, decimal salePrice) =>
        salePrice == 0 ? null : decimal.Round(margin / salePrice * 100m, 2, MidpointRounding.AwayFromZero);
}
