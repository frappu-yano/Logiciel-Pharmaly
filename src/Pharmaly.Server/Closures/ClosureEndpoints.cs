using System.Text.Json;
using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Access;
using Pharmaly.Server.Operations;

namespace Pharmaly.Server.Closures;

public static class ClosureAuditActions
{
    public const string SessionClosed = "CAISSE_FERMEE";
    public const string DayClosed = "JOURNEE_CLOTUREE";

    /// <summary>Procédure exceptionnelle : clôture malgré des opérations de secours non synchronisées.</summary>
    public const string DayClosedForced = "JOURNEE_CLOTUREE_FORCEE";
}

/// <summary>
/// Fermeture d'une caisse (comptage des espèces) et clôture quotidienne du magasin (D11).
/// <para>
/// Fermer une caisse verrouille sa session en exclusif : les encaissements en cours, qui la
/// tiennent en partage, se terminent d'abord ; les suivants trouvent la caisse fermée.
/// </para>
/// <para>
/// Clôturer la journée prend le verrou de la journée du magasin en exclusif, exige que toutes
/// les caisses soient fermées et qu'aucune opération ne soit en attente, calcule le rapport,
/// le stocke, puis renseigne « business_date » sur toutes les opérations de la journée.
/// </para>
/// </summary>
public static class ClosureEndpoints
{
    public const string CloseSessionOperation = "CAISSE_FERMER";
    public const string CloseDayOperation = "JOURNEE_CLOTURER";

    public const int DefaultLimit = 60;
    public const int MaxLimit = 400;
    private const int MaxReasonLength = 500;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Tables dont les lignes portent la journée comptable d'un magasin.</summary>
    private static readonly string[] DayTables =
    [
        "sales", "payments", "receivable_settlements", "cash_movements", "sale_cancellations", "returns", "cash_sessions",
    ];

    private const string ClosureSql =
        """
        SELECT json_build_object(
            'id', dc.id, 'storeId', dc.store_id, 'storeName', st.name, 'businessDate', dc.business_date,
            'closedAt', dc.closed_at, 'closedById', dc.closed_by, 'closedByName', e.full_name,
            'forced', dc.forced, 'forceReason', dc.force_reason, 'report', dc.report)::text
        FROM daily_closures dc
        JOIN stores st ON st.id = dc.store_id
        JOIN employees e ON e.id = dc.closed_by
        """;

    public static void MapClosureEndpoints(this WebApplication app)
    {
        app.MapPost(ClosureRoutes.CloseSession, CloseSessionAsync).RequirePermission(Permissions.RegisterClose);
        app.MapPost(ClosureRoutes.Closures, CloseDayAsync).RequirePermission(Permissions.RegisterClose);

        app.MapGet(ClosureRoutes.CurrentDay, async (HttpContext context, NpgsqlDataSource dataSource, StoreClock clock) =>
        {
            var identity = context.GetIdentity();
            await using var connection = await dataSource.OpenConnectionAsync();
            // Une seule photographie pour les caisses, les blocages et les totaux.
            await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
            var today = clock.Today();
            var lastClosed = await ReadLastClosedDateAsync(connection, transaction, identity.StoreId);
            var blockers = await ClosureReports.ReadBlockersAsync(connection, transaction, identity.StoreId);
            var offline = await OfflineOperations.CountPendingAsync(connection, transaction, identity.StoreId);
            if (offline > 0)
            {
                blockers.Add(ClosureReports.OfflineBlocker(offline));
            }

            return Results.Ok(new CurrentDayStatus(
                identity.StoreId, identity.StoreName, today, lastClosed,
                blockers.Count == 0 && (lastClosed is null || lastClosed < today),
                blockers, offline,
                await ClosureReports.ComputeOpenDayAsync(connection, transaction, identity.StoreId, offline)));
        }).RequirePermission(Permissions.RegisterReportsView);

        app.MapGet(ClosureRoutes.Closures, async (
            DateOnly? du, DateOnly? au, int? limite, HttpContext context, NpgsqlDataSource dataSource) =>
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            return Results.Ok(await SqlJson.ReadListAsync<DailyClosureSummary>(
                connection,
                """
                SELECT json_build_object(
                    'id', dc.id, 'storeId', dc.store_id, 'businessDate', dc.business_date, 'closedAt', dc.closed_at,
                    'closedById', dc.closed_by, 'closedByName', e.full_name, 'forced', dc.forced,
                    'netRevenue', (dc.report #>> '{revenue,netRevenue}')::numeric,
                    'netCash', (dc.report #>> '{treasury,netCash}')::numeric,
                    'netCard', (dc.report #>> '{treasury,netCard}')::numeric,
                    'cashDifference', (dc.report #>> '{treasury,cashDifference}')::numeric)::text
                FROM daily_closures dc JOIN employees e ON e.id = dc.closed_by
                WHERE dc.store_id = @storeId
                  AND (@from::date IS NULL OR dc.business_date >= @from::date)
                  AND (@to::date IS NULL OR dc.business_date <= @to::date)
                ORDER BY dc.business_date DESC
                LIMIT @limit
                """,
                new
                {
                    storeId = context.GetIdentity().StoreId,
                    from = du?.ToDateTime(TimeOnly.MinValue),
                    to = au?.ToDateTime(TimeOnly.MinValue),
                    limit = Math.Clamp(limite ?? DefaultLimit, 1, MaxLimit),
                }));
        }).RequirePermission(Permissions.RegisterReportsView);

        app.MapGet(ClosureRoutes.Closures + "/{id:guid}", async (Guid id, HttpContext context, NpgsqlDataSource dataSource) =>
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            return await SqlJson.ReadSingleAsync<DailyClosureDetail>(
                    connection, ClosureSql + " WHERE dc.id = @id AND dc.store_id = @storeId",
                    new { id, storeId = context.GetIdentity().StoreId }) is { } closure
                ? Results.Ok(closure)
                : Results.Json(new ApiError(ApiErrorCodes.NotFound, "Clôture introuvable."), statusCode: StatusCodes.Status404NotFound);
        }).RequirePermission(Permissions.RegisterReportsView);
    }

    private static async Task<DateOnly?> ReadLastClosedDateAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid storeId) =>
        await connection.ExecuteScalarAsync<DateTime?>(
            "SELECT max(business_date) FROM daily_closures WHERE store_id = @storeId", new { storeId }, transaction) is { } last
            ? DateOnly.FromDateTime(last)
            : null;

    // ---------- Fermeture d'une caisse ----------

    private static Task<IResult> CloseSessionAsync(CloseCashSessionRequest request, HttpContext context, OperationRunner runner) =>
        runner.RunAsync(request.OperationId, CloseSessionOperation, context.GetIdentity(), request, async operation =>
        {
            if (!SaleMath.IsValidAmount(request.CountedCash))
            {
                throw BusinessException.Invalid("Le montant compté doit être positif ou nul, avec au plus deux décimales.");
            }

            // Verrou exclusif : attend la fin des encaissements en cours sur cette caisse.
            var sessionId = await operation.Connection.ExecuteScalarAsync<Guid?>(
                "SELECT id FROM cash_sessions WHERE register_account_id = @RegisterAccountId AND status = 'OUVERTE' FOR UPDATE",
                new { operation.Identity.RegisterAccountId },
                operation.Transaction)
                ?? throw new BusinessException(
                    CashErrorCodes.NoOpenSession, "La caisse de ce poste n'est pas ouverte : il n'y a rien à fermer.",
                    StatusCodes.Status409Conflict);

            // D23 : pas de fermeture tant qu'un paiement par carte attend sa réponse.
            var blockers = await ClosureReports.ReadBlockersAsync(
                operation.Connection, operation.Transaction, operation.Identity.StoreId, sessionId);
            if (blockers.Count > 0)
            {
                throw new ClosureBlockedException(
                    ClosureErrorCodes.CardPending,
                    $"La caisse ne peut pas être fermée : {blockers.Count} paiement(s) par carte en attente de réponse. "
                    + string.Join(" ", blockers.Select(blocker => blocker.Message)),
                    blockers);
            }

            var totals = await ClosureReports.ReadSessionAsync(operation.Connection, operation.Transaction, sessionId);
            var difference = request.CountedCash - totals.ExpectedCash;
            await operation.Connection.ExecuteAsync(
                """
                UPDATE cash_sessions
                SET status = 'FERMEE', closed_by = @EmployeeId, closed_at = now(), counted_cash = @CountedCash,
                    expected_cash = @ExpectedCash, cash_difference = @difference, close_operation_id = @OperationId
                WHERE id = @sessionId
                """,
                new { sessionId, operation.EmployeeId, request.CountedCash, totals.ExpectedCash, difference, operation.OperationId },
                operation.Transaction);

            await operation.AuditAsync(
                ClosureAuditActions.SessionClosed, "cash_session", sessionId.ToString(),
                new
                {
                    fondDeCaisse = totals.OpeningFloat,
                    especesAttendues = totals.ExpectedCash,
                    especesComptees = request.CountedCash,
                    ecart = difference,
                });
            return await ClosureReports.ReadSessionAsync(operation.Connection, operation.Transaction, sessionId);
        });

    // ---------- Clôture de la journée ----------

    private static async Task<IResult> CloseDayAsync(
        CloseDayRequest request, HttpContext context, NpgsqlDataSource dataSource, OperationRunner runner, StoreClock clock)
    {
        // Procédure exceptionnelle : faute de droit dédié, elle exige en plus le droit d'administration.
        if (request.ForceWithUnknownOperations
            && await PermissionGuard.RefuseWithoutAnyAsync(dataSource, context, Permissions.UsersAdminister) is { } refusal)
        {
            return refusal;
        }

        return await runner.RunAsync(request.OperationId, CloseDayOperation, context.GetIdentity(), request, async operation =>
        {
            var storeId = operation.Identity.StoreId;
            var today = clock.Today();
            var date = request.BusinessDate ?? today;
            if (date > today)
            {
                throw new BusinessException(
                    ClosureErrorCodes.InvalidDate, $"Le {Display(date)} n'est pas encore arrivé : cette journée ne peut pas être clôturée.");
            }

            string? forceReason = null;
            if (request.ForceWithUnknownOperations)
            {
                forceReason = (request.ForceReason ?? string.Empty).Trim();
                if (forceReason.Length is 0 or > MaxReasonLength)
                {
                    throw BusinessException.Invalid(
                        $"Une clôture forcée exige un motif ({MaxReasonLength} caractères au plus).");
                }
            }

            // À partir d'ici, plus rien ne peut s'ajouter à la journée de ce magasin.
            await BusinessDayLock.ExclusiveAsync(operation, storeId);

            if (await ReadLastClosedDateAsync(operation.Connection, operation.Transaction, storeId) is { } last && last >= date)
            {
                throw AlreadyClosed(date, last);
            }

            var blockers = await ClosureReports.ReadBlockersAsync(operation.Connection, operation.Transaction, storeId);
            if (blockers.Count > 0)
            {
                throw new ClosureBlockedException(
                    ClosureErrorCodes.DayBlocked,
                    "La journée ne peut pas être clôturée. " + string.Join(" ", blockers.Select(blocker => blocker.Message)),
                    blockers);
            }

            // Garde-fou du mode secours : une journée n'est jamais consolidée avec des opérations inconnues,
            // sauf procédure exceptionnelle explicite, motivée et auditée.
            var offline = await OfflineOperations.CountPendingAsync(operation.Connection, operation.Transaction, storeId);
            if (offline > 0 && !request.ForceWithUnknownOperations)
            {
                var blocker = ClosureReports.OfflineBlocker(offline);
                throw new ClosureBlockedException(ClosureErrorCodes.OfflineOperationsPending, blocker.Message, [blocker]);
            }

            if (offline == 0 && request.ForceWithUnknownOperations)
            {
                throw BusinessException.Invalid(
                    "Aucune opération de secours n'est en attente : la clôture normale suffit, sans forçage.");
            }

            var forced = offline > 0;
            var report = await ClosureReports.ComputeOpenDayAsync(operation.Connection, operation.Transaction, storeId, offline, forced);

            // La journée est figée : chaque opération reçoit sa date, une fois pour toutes.
            foreach (var table in DayTables)
            {
                await operation.Connection.ExecuteAsync(
                    $"UPDATE {table} SET business_date = @date::date WHERE store_id = @storeId AND business_date IS NULL",
                    new { storeId, date = date.ToDateTime(TimeOnly.MinValue) },
                    operation.Transaction);
            }

            Guid closureId;
            try
            {
                closureId = await operation.Connection.ExecuteScalarAsync<Guid>(
                    """
                    INSERT INTO daily_closures
                        (store_id, business_date, closed_by, register_account_id, operation_id, forced, force_reason,
                         unknown_offline_operations, report)
                    VALUES (@storeId, @date::date, @EmployeeId, @RegisterAccountId, @OperationId, @forced, @reason,
                            @offline, @report::jsonb)
                    RETURNING id
                    """,
                    new
                    {
                        storeId,
                        date = date.ToDateTime(TimeOnly.MinValue),
                        operation.EmployeeId,
                        operation.Identity.RegisterAccountId,
                        operation.OperationId,
                        forced,
                        reason = forced ? forceReason : null,
                        offline,
                        report = JsonSerializer.Serialize(report, JsonOptions),
                    },
                    operation.Transaction);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Dernier rempart : la contrainte « une seule clôture par magasin et par jour ».
                throw AlreadyClosed(date, date);
            }

            await operation.AuditAsync(
                forced ? ClosureAuditActions.DayClosedForced : ClosureAuditActions.DayClosed, "daily_closure", closureId.ToString(),
                new
                {
                    journee = date.ToString("yyyy-MM-dd"),
                    chiffreAffaires = report.Revenue.NetRevenue,
                    especes = report.Treasury.NetCash,
                    carte = report.Treasury.NetCard,
                    ecartEspeces = report.Treasury.CashDifference,
                    caisses = report.Sessions.Count,
                    forcee = forced,
                    motifForcage = forced ? forceReason : null,
                    operationsInconnues = offline,
                });
            return (await SqlJson.ReadSingleAsync<DailyClosureDetail>(
                operation.Connection, ClosureSql + " WHERE dc.id = @closureId", new { closureId }, operation.Transaction))!;
        });
    }

    private static string Display(DateOnly date) => date.ToString("dd'/'MM'/'yyyy", System.Globalization.CultureInfo.InvariantCulture);

    private static BusinessException AlreadyClosed(DateOnly date, DateOnly last) =>
        new(
            ClosureErrorCodes.DayAlreadyClosed,
            last == date
                ? $"La journée du {Display(date)} est déjà clôturée pour ce magasin. Les opérations saisies depuis appartiennent à la journée suivante."
                : $"La journée du {Display(date)} ne peut plus être clôturée : ce magasin est déjà clôturé jusqu'au {Display(last)}.",
            StatusCodes.Status409Conflict);
}
