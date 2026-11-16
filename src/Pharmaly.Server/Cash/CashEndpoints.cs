using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Access;
using Pharmaly.Server.Closures;
using Pharmaly.Server.Operations;

namespace Pharmaly.Server.Cash;

/// <summary>Règles communes à tout ce qui touche à la caisse d'un poste.</summary>
public static class CashSessions
{
    public const int MaxReasonLength = 500;

    private const string InfoSql =
        """
        SELECT json_build_object(
            'id', cs.id, 'registerAccountId', cs.register_account_id, 'registerName', ra.display_name,
            'storeId', cs.store_id, 'status', cs.status, 'openingFloat', cs.opening_float,
            'openedById', cs.opened_by, 'openedByName', e.full_name, 'openedAt', cs.opened_at)::text
        FROM cash_sessions cs
        JOIN register_accounts ra ON ra.id = cs.register_account_id
        JOIN employees e ON e.id = cs.opened_by
        """;

    /// <summary>
    /// Session ouverte du compte de caisse du poste, verrouillée en partage jusqu'à la fin de
    /// la transaction : une clôture ne peut pas passer pendant qu'un encaissement s'y inscrit.
    /// À appeler AVANT tout autre verrou (vente, créance, stock).
    /// </summary>
    public static async Task<Guid> RequireOpenAsync(OperationContext operation) =>
        await operation.Connection.ExecuteScalarAsync<Guid?>(
            "SELECT id FROM cash_sessions WHERE register_account_id = @RegisterAccountId AND status = 'OUVERTE' FOR SHARE",
            new { operation.Identity.RegisterAccountId },
            operation.Transaction)
        ?? throw new BusinessException(
            CashErrorCodes.NoOpenSession,
            "La caisse de ce poste n'est pas ouverte. Ouvrez la caisse avant de vendre ou d'encaisser.",
            StatusCodes.Status409Conflict);

    /// <summary>
    /// Verrouille en partage la session d'un paiement en attente et exige qu'elle soit encore ouverte.
    /// </summary>
    public static async Task RequireStillOpenAsync(OperationContext operation, Guid sessionId)
    {
        var status = await operation.Connection.ExecuteScalarAsync<string?>(
            "SELECT status FROM cash_sessions WHERE id = @sessionId FOR SHARE", new { sessionId }, operation.Transaction);
        if (status != "OUVERTE")
        {
            throw BusinessException.Conflict("La session de caisse de ce paiement est fermée.");
        }
    }

    public static Task<CashSessionInfo?> ReadOpenAsync(NpgsqlConnection connection, Guid registerAccountId) =>
        SqlJson.ReadSingleAsync<CashSessionInfo>(
            connection,
            InfoSql + " WHERE cs.register_account_id = @registerAccountId AND cs.status = 'OUVERTE'",
            new { registerAccountId });

    public static async Task<CashSessionInfo> ReadAsync(OperationContext operation, Guid sessionId) =>
        (await SqlJson.ReadSingleAsync<CashSessionInfo>(
            operation.Connection, InfoSql + " WHERE cs.id = @sessionId", new { sessionId }, operation.Transaction))!;

    public static string CleanReason(string? reason, string what)
    {
        var cleaned = (reason ?? string.Empty).Trim();
        return cleaned.Length is 0 or > MaxReasonLength
            ? throw BusinessException.Invalid($"Le motif {what} est obligatoire ({MaxReasonLength} caractères au plus).")
            : cleaned;
    }
}

public static class CashAuditActions
{
    public const string SessionOpened = "CAISSE_OUVERTE";
    public const string CashIn = "CAISSE_ENTREE_ESPECES";
    public const string CashOut = "CAISSE_SORTIE_ESPECES";
}

/// <summary>Caisse du poste : ouverture, état, totaux, entrées et sorties d'espèces.</summary>
public static class CashEndpoints
{
    public const string OpenOperation = "CAISSE_OUVRIR";
    public const string MovementOperation = "CAISSE_MOUVEMENT";

    public static void MapCashEndpoints(this WebApplication app)
    {
        // État de la caisse du poste : nécessaire à tout employé identifié (l'écran de vente en dépend).
        app.MapGet(CashRoutes.CurrentSession, async (HttpContext context, NpgsqlDataSource dataSource) =>
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            return Results.Ok(new CurrentCashSessionResponse(
                await CashSessions.ReadOpenAsync(connection, context.GetIdentity().RegisterAccountId)));
        }).RequireEmployee();

        app.MapGet(CashRoutes.SessionSummary, ReadSummaryAsync).RequirePermission(Permissions.RegisterReportsView);
        app.MapPost(CashRoutes.OpenSession, OpenAsync).RequirePermission(Permissions.RegisterOpen);

        // Pas de droit dédié dans le cahier des charges : entrées et sorties d'espèces
        // demandent « encaisser les paiements ».
        app.MapPost(CashRoutes.Movements, MoveAsync).RequirePermission(Permissions.PaymentsCollect);
    }

    private static Task<IResult> OpenAsync(OpenCashSessionRequest request, HttpContext context, OperationRunner runner) =>
        runner.RunAsync(request.OperationId, OpenOperation, context.GetIdentity(), request, async operation =>
        {
            if (!SaleMath.IsValidAmount(request.OpeningFloat))
            {
                throw BusinessException.Invalid("Le fond de caisse doit être positif ou nul, avec au plus deux décimales.");
            }

            var alreadyOpen = new BusinessException(
                CashErrorCodes.SessionAlreadyOpen, "La caisse de ce poste est déjà ouverte.", StatusCodes.Status409Conflict);
            var registerAccountId = operation.Identity.RegisterAccountId;

            // Une ouverture pendant la clôture de la journée attend sa fin : la nouvelle session
            // appartient alors à la journée suivante.
            await BusinessDayLock.SharedAsync(operation, operation.Identity.StoreId);

            // Le compte de caisse est verrouillé : deux ouvertures simultanées passent l'une après l'autre.
            await operation.Connection.ExecuteAsync(
                "SELECT 1 FROM register_accounts WHERE id = @registerAccountId FOR NO KEY UPDATE",
                new { registerAccountId },
                operation.Transaction);
            if (await operation.Connection.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM cash_sessions WHERE register_account_id = @registerAccountId AND status = 'OUVERTE')",
                    new { registerAccountId },
                    operation.Transaction))
            {
                throw alreadyOpen;
            }

            Guid sessionId;
            try
            {
                sessionId = await operation.Connection.ExecuteScalarAsync<Guid>(
                    """
                    INSERT INTO cash_sessions (register_account_id, store_id, opening_float, opened_by, operation_id)
                    VALUES (@registerAccountId, @StoreId, @OpeningFloat, @EmployeeId, @OperationId)
                    RETURNING id
                    """,
                    new { registerAccountId, operation.Identity.StoreId, request.OpeningFloat, operation.EmployeeId, operation.OperationId },
                    operation.Transaction);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Dernier rempart : l'index unique « une seule session ouverte par compte de caisse ».
                throw alreadyOpen;
            }

            await operation.AuditAsync(
                CashAuditActions.SessionOpened, "cash_session", sessionId.ToString(), new { fondDeCaisse = request.OpeningFloat });
            return await CashSessions.ReadAsync(operation, sessionId);
        });

    private static Task<IResult> MoveAsync(CashMovementRequest request, HttpContext context, OperationRunner runner) =>
        runner.RunAsync(request.OperationId, MovementOperation, context.GetIdentity(), request, async operation =>
        {
            if (request.Kind is not (CashMovementKinds.In or CashMovementKinds.Out))
            {
                throw BusinessException.Invalid("Indiquez une entrée (ENTREE) ou une sortie (SORTIE) d'espèces.");
            }

            if (request.Amount <= 0 || !SaleMath.IsValidAmount(request.Amount))
            {
                throw BusinessException.Invalid("Le montant doit être positif, avec au plus deux décimales.");
            }

            var reason = CashSessions.CleanReason(request.Reason, "du mouvement d'espèces");
            var sessionId = await CashSessions.RequireOpenAsync(operation);
            var created = await operation.Connection.QuerySingleAsync<(Guid Id, DateTime OccurredAt)>(
                """
                INSERT INTO cash_movements
                    (cash_session_id, store_id, register_account_id, employee_id, kind, amount, reason, operation_id)
                VALUES (@sessionId, @StoreId, @RegisterAccountId, @EmployeeId, @Kind, @Amount, @reason, @OperationId)
                RETURNING id, occurred_at
                """,
                new
                {
                    sessionId,
                    operation.Identity.StoreId,
                    operation.Identity.RegisterAccountId,
                    operation.EmployeeId,
                    request.Kind,
                    request.Amount,
                    reason,
                    operation.OperationId,
                },
                operation.Transaction);

            await operation.AuditAsync(
                request.Kind == CashMovementKinds.In ? CashAuditActions.CashIn : CashAuditActions.CashOut,
                "cash_movement", created.Id.ToString(),
                new { session = sessionId, montant = request.Amount, motif = reason });
            return new CashMovementResult(
                created.Id, sessionId, request.Kind, request.Amount, reason, operation.EmployeeId,
                new DateTimeOffset(DateTime.SpecifyKind(created.OccurredAt, DateTimeKind.Utc)));
        });

    private static async Task<IResult> ReadSummaryAsync(HttpContext context, NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        // Une seule photographie pour l'en-tête et les totaux.
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        var session = await CashSessions.ReadOpenAsync(connection, context.GetIdentity().RegisterAccountId);
        if (session is null)
        {
            return Results.Json(
                new ApiError(CashErrorCodes.NoOpenSession, "La caisse de ce poste n'est pas ouverte."),
                statusCode: StatusCodes.Status409Conflict);
        }

        var totals = await connection.QuerySingleAsync<TotalsRow>(
            """
            SELECT
              (SELECT COALESCE(sum(amount), 0) FROM payments
                WHERE cash_session_id = @Id AND method = 'ESPECES' AND status IN ('CONFIRME', 'REMBOURSE')) AS SaleCash,
              (SELECT COALESCE(sum(amount), 0) FROM payments
                WHERE cash_session_id = @Id AND method = 'TPE' AND status IN ('CONFIRME', 'REMBOURSE')) AS SaleCard,
              (SELECT COALESCE(sum(amount), 0) FROM receivable_settlements
                WHERE cash_session_id = @Id AND method = 'ESPECES' AND status = 'CONFIRME') AS SettlementCash,
              (SELECT COALESCE(sum(amount), 0) FROM receivable_settlements
                WHERE cash_session_id = @Id AND method = 'TPE' AND status = 'CONFIRME') AS SettlementCard,
              (SELECT COALESCE(sum(amount), 0) FROM cash_movements WHERE cash_session_id = @Id AND kind = 'ENTREE') AS CashIn,
              (SELECT COALESCE(sum(amount), 0) FROM cash_movements WHERE cash_session_id = @Id AND kind = 'SORTIE') AS CashOut,
              (SELECT COALESCE(sum(amount), 0) FROM cash_movements
                WHERE cash_session_id = @Id AND kind = 'REMBOURSEMENT_VENTE') AS CashRefunds,
              (SELECT COALESCE(sum(refund_amount), 0) FROM sale_cancellations
                WHERE cash_session_id = @Id AND refund_method = 'TPE') AS CardRefunds,
              (SELECT COALESCE(sum(refund_amount), 0) FROM returns
                WHERE cash_session_id = @Id AND refund_method = 'ESPECES') AS ReturnCashRefunds,
              (SELECT COALESCE(sum(refund_amount), 0) FROM returns
                WHERE cash_session_id = @Id AND refund_method = 'TPE') AS ReturnCardRefunds,
              (SELECT count(*) FROM payments WHERE cash_session_id = @Id AND status = 'EN_ATTENTE')
              + (SELECT count(*) FROM receivable_settlements WHERE cash_session_id = @Id AND status = 'EN_ATTENTE') AS PendingCount,
              (SELECT COALESCE(sum(amount), 0) FROM payments WHERE cash_session_id = @Id AND status = 'EN_ATTENTE')
              + (SELECT COALESCE(sum(amount), 0) FROM receivable_settlements
                  WHERE cash_session_id = @Id AND status = 'EN_ATTENTE') AS PendingAmount
            """,
            new { session.Id },
            transaction);

        return Results.Ok(new CashSessionSummary(
            session, totals.SaleCash, totals.SaleCard, totals.SettlementCash, totals.SettlementCard,
            totals.CashIn, totals.CashOut, totals.CashRefunds, totals.CardRefunds,
            ClosureReports.ExpectedCash(
                session.OpeningFloat, totals.SaleCash, totals.SettlementCash, totals.CashIn, totals.CashOut,
                totals.CashRefunds, totals.ReturnCashRefunds),
            (int)totals.PendingCount, totals.PendingAmount, totals.ReturnCashRefunds, totals.ReturnCardRefunds));
    }

    private sealed class TotalsRow
    {
        public decimal SaleCash { get; init; }
        public decimal SaleCard { get; init; }
        public decimal SettlementCash { get; init; }
        public decimal SettlementCard { get; init; }
        public decimal CashIn { get; init; }
        public decimal CashOut { get; init; }
        public decimal CashRefunds { get; init; }
        public decimal CardRefunds { get; init; }
        public decimal ReturnCashRefunds { get; init; }
        public decimal ReturnCardRefunds { get; init; }
        public long PendingCount { get; init; }
        public decimal PendingAmount { get; init; }
    }
}
