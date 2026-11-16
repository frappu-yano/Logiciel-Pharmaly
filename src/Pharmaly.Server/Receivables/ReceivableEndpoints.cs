using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Access;
using Pharmaly.Server.Cash;
using Pharmaly.Server.Operations;
using Pharmaly.Server.Sales;

namespace Pharmaly.Server.Receivables;

public static class ReceivableAuditActions
{
    public const string Settled = "IMPAYES_REGLES";
    public const string CardStarted = "IMPAYES_REGLEMENT_TPE_LANCE";
    public const string CardAccepted = "IMPAYES_REGLEMENT_TPE_ACCEPTE";
    public const string CardRefused = "IMPAYES_REGLEMENT_TPE_REFUSE";
}

/// <summary>
/// Impayés : tableau, recherche par ticket et règlement intégral, éventuellement groupé.
/// <para>
/// Un règlement ne touche jamais au stock. Il appartient à la session de caisse du poste
/// qui encaisse : c'est de la trésorerie du jour du règlement, pas du chiffre d'affaires.
/// </para>
/// <para>Ordre des verrous : session de caisse (partagé) → règlement → créances par identifiant croissant.</para>
/// </summary>
public static class ReceivableEndpoints
{
    public const string SettleOperation = "IMPAYES_REGLER";
    public const string CardResultOperation = "IMPAYES_TPE_REPONSE";

    private const int MaxReceivablesPerSettlement = 100;

    public static void MapReceivableEndpoints(this WebApplication app)
    {
        app.MapGet(ReceivableRoutes.Receivables, async (
            string? statut, Guid? client, DateOnly? du, DateOnly? au, string? q, int? limite, NpgsqlDataSource dataSource) =>
        {
            var status = string.IsNullOrWhiteSpace(statut) ? null : statut.Trim().ToUpperInvariant();
            if (status is not null && !ReceivableStatuses.IsKnown(status))
            {
                return Results.Json(
                    new ApiError(ApiErrorCodes.InvalidRequest, "Statut inconnu : OUVERTE, SOLDEE ou ANNULEE."),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            await using var connection = await dataSource.OpenConnectionAsync();
            // Indicateurs et lignes lus sur la même photographie de la base.
            await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
            return Results.Ok(new ReceivablesTable(
                await ReceivableQueries.ReadIndicatorsAsync(connection),
                await ReceivableQueries.ListAsync(connection, new ReceivableFilter(
                    status,
                    client,
                    du?.ToDateTime(TimeOnly.MinValue),
                    au?.ToDateTime(TimeOnly.MinValue),
                    SaleQueries.ToTicketPattern(q),
                    Math.Clamp(limite ?? ReceivableQueries.DefaultLimit, 1, ReceivableQueries.MaxLimit)))));
        }).RequirePermission(Permissions.ReceivablesView);

        app.MapGet(ReceivableRoutes.ReceivableByTicket + "/{code}", async (string code, NpgsqlDataSource dataSource) =>
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            return await ReceivableQueries.FindByTicketAsync(connection, SaleQueries.NormalizeTicketCode(code)) is { } line
                ? Results.Ok(line)
                : NotFound("Aucun impayé ne correspond à ce ticket.");
        }).RequirePermission(Permissions.ReceivablesView);

        app.MapGet(ReceivableRoutes.Settlements + "/{id:guid}", async (Guid id, NpgsqlDataSource dataSource) =>
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            return await ReceivableQueries.ReadSettlementAsync(connection, id) is { } settlement
                ? Results.Ok(settlement)
                : NotFound("Règlement introuvable.");
        }).RequirePermission(Permissions.ReceivablesView);

        app.MapPost(ReceivableRoutes.Settlements, SettleAsync).RequirePermission(Permissions.ReceivablesSettle);
        app.MapPost(ReceivableRoutes.Settlements + "/{id:guid}/" + ReceivableRoutes.CardResultSuffix, ResolveCardAsync)
            .RequirePermission(Permissions.ReceivablesSettle);
    }

    private static IResult NotFound(string message) =>
        Results.Json(new ApiError(ApiErrorCodes.NotFound, message), statusCode: StatusCodes.Status404NotFound);

    // ---------- Règlement ----------

    private static Task<IResult> SettleAsync(SettleReceivablesRequest request, HttpContext context, OperationRunner runner) =>
        runner.RunAsync(request.OperationId, SettleOperation, context.GetIdentity(), request, async operation =>
        {
            var ids = (request.ReceivableIds ?? []).ToArray();
            if (ids.Length is 0 or > MaxReceivablesPerSettlement)
            {
                throw BusinessException.Invalid($"Un règlement porte sur 1 à {MaxReceivablesPerSettlement} tickets impayés.");
            }

            if (ids.Distinct().Count() != ids.Length)
            {
                throw BusinessException.Invalid("Le même ticket impayé figure plusieurs fois dans le règlement.");
            }

            if (!PaymentMethods.IsKnown(request.Method))
            {
                throw BusinessException.Invalid("Moyen de règlement inconnu : ESPECES ou TPE.");
            }

            if (request.Amount <= 0 || !SaleMath.IsValidAmount(request.Amount))
            {
                throw BusinessException.Invalid("Le montant du règlement doit être positif, avec au plus deux décimales.");
            }

            var sessionId = await CashSessions.RequireOpenAsync(operation);

            // Toutes les créances sont verrouillées, dans un ordre unique : un second règlement
            // de l'une d'elles attend ici, puis la trouve soldée.
            var receivables = (await operation.Connection.QueryAsync<(Guid Id, Guid CustomerId, decimal Amount, string Status, string TicketNumber)>(
                """
                SELECT r.id, r.customer_id, r.amount, r.status, s.ticket_number
                FROM receivables r JOIN sales s ON s.id = r.sale_id
                WHERE r.id = ANY(@ids)
                ORDER BY r.id
                FOR UPDATE OF r
                """,
                new { ids },
                operation.Transaction)).AsList();
            if (receivables.Count != ids.Length)
            {
                throw BusinessException.NotFound("Ticket impayé introuvable.");
            }

            if (receivables.Select(receivable => receivable.CustomerId).Distinct().Count() > 1)
            {
                throw new BusinessException(
                    ReceivableErrorCodes.DifferentCustomers,
                    "Ces tickets appartiennent à des clients différents : un règlement groupé concerne un seul client. Rien n'a été encaissé.",
                    StatusCodes.Status409Conflict);
            }

            if (receivables.Find(receivable => receivable.Status != ReceivableStatuses.Open) is { TicketNumber: not null } closed)
            {
                throw new BusinessException(
                    ReceivableErrorCodes.NotOpen,
                    $"Le ticket {closed.TicketNumber} est déjà {(closed.Status == ReceivableStatuses.Settled ? "soldé" : "annulé")}. Rien n'a été encaissé.",
                    StatusCodes.Status409Conflict);
            }

            var pendingTicket = await operation.Connection.ExecuteScalarAsync<string?>(
                """
                SELECT s.ticket_number
                FROM receivable_settlement_items i
                JOIN receivables r ON r.id = i.receivable_id
                JOIN sales s ON s.id = r.sale_id
                WHERE i.receivable_id = ANY(@ids) AND i.is_active
                ORDER BY s.ticket_number LIMIT 1
                """,
                new { ids },
                operation.Transaction);
            if (pendingTicket is not null)
            {
                throw PendingSettlement(pendingTicket);
            }

            // Règlement partiel interdit : le montant est exactement la somme due.
            var due = receivables.Sum(receivable => receivable.Amount);
            if (request.Amount != due)
            {
                throw new BusinessException(
                    ReceivableErrorCodes.AmountMismatch,
                    $"Le montant à encaisser est exactement {Money.ToDisplay(due)} ; {Money.ToDisplay(request.Amount)} a été saisi. Un ticket impayé se règle en entier.");
            }

            var isCash = request.Method == PaymentMethods.Cash;
            decimal? amountReceived = null;
            decimal? changeGiven = null;
            if (isCash)
            {
                amountReceived = request.AmountReceived ?? throw BusinessException.Invalid("Indiquez le montant reçu en espèces.");
                if (!SaleMath.IsValidAmount(amountReceived.Value))
                {
                    throw BusinessException.Invalid("Le montant reçu doit être positif, avec au plus deux décimales.");
                }

                if (amountReceived < due)
                {
                    throw new BusinessException(
                        SaleErrorCodes.InsufficientAmountReceived,
                        $"Montant reçu insuffisant : {Money.ToDisplay(amountReceived.Value)} pour {Money.ToDisplay(due)} dus.");
                }

                changeGiven = amountReceived - due;
            }

            var settlementId = await operation.Connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO receivable_settlements
                    (customer_id, method, status, amount, amount_received, change_given, cash_session_id, store_id,
                     register_account_id, employee_id, operation_id, confirmed_at)
                VALUES (@customerId, @Method, @status, @due, @amountReceived, @changeGiven, @sessionId, @StoreId,
                        @RegisterAccountId, @EmployeeId, @OperationId, CASE WHEN @isCash THEN now() END)
                RETURNING id
                """,
                new
                {
                    customerId = receivables[0].CustomerId,
                    request.Method,
                    // Espèces : encaissé. TPE : rien n'est soldé avant « accepté ».
                    status = isCash ? PaymentStatuses.Confirmed : PaymentStatuses.Pending,
                    due,
                    amountReceived,
                    changeGiven,
                    sessionId,
                    operation.Identity.StoreId,
                    operation.Identity.RegisterAccountId,
                    operation.EmployeeId,
                    operation.OperationId,
                    isCash,
                },
                operation.Transaction);

            try
            {
                await operation.Connection.ExecuteAsync(
                    """
                    INSERT INTO receivable_settlement_items (settlement_id, receivable_id, amount)
                    SELECT @settlementId, r.id, r.amount FROM receivables r WHERE r.id = ANY(@ids)
                    """,
                    new { settlementId, ids },
                    operation.Transaction);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Dernier rempart : l'index unique « une créance, un seul règlement actif ».
                throw PendingSettlement(receivables[0].TicketNumber);
            }

            if (isCash)
            {
                await MarkSettledAsync(operation, ids);
            }

            await operation.AuditAsync(
                isCash ? ReceivableAuditActions.Settled : ReceivableAuditActions.CardStarted,
                "receivable_settlement", settlementId.ToString(),
                new
                {
                    client = receivables[0].CustomerId,
                    moyen = request.Method,
                    montant = due,
                    session = sessionId,
                    tickets = receivables.Select(receivable => receivable.TicketNumber).Order(),
                });
            return (await ReceivableQueries.ReadSettlementAsync(operation.Connection, settlementId, operation.Transaction))!;
        });

    private static BusinessException PendingSettlement(string ticketNumber) =>
        new(
            ReceivableErrorCodes.SettlementPending,
            $"Un règlement par carte est déjà en attente sur le ticket {ticketNumber}. Répondez-y (accepté ou refusé) avant de recommencer.",
            StatusCodes.Status409Conflict);

    private static async Task MarkSettledAsync(OperationContext operation, Guid[] ids)
    {
        var settled = await operation.Connection.ExecuteAsync(
            "UPDATE receivables SET status = @settled, settled_at = now() WHERE id = ANY(@ids) AND status = @open",
            new { ids, settled = ReceivableStatuses.Settled, open = ReceivableStatuses.Open },
            operation.Transaction);
        if (settled != ids.Length)
        {
            throw new InvalidOperationException("Une créance verrouillée n'était plus ouverte au moment de la solder.");
        }
    }

    // ---------- Réponse du TPE ----------

    /// <summary>
    /// « Accepté » : les créances du règlement sont soldées, toutes ensemble. « Refusé » : le
    /// règlement échoue et les créances, restées ouvertes et intactes, redeviennent réglables.
    /// C'est aussi la façon de débloquer un règlement par carte resté en attente.
    /// </summary>
    private static Task<IResult> ResolveCardAsync(Guid id, CardResultRequest request, HttpContext context, OperationRunner runner) =>
        runner.RunAsync(request.OperationId, CardResultOperation, context.GetIdentity(), new { id, request }, async operation =>
        {
            var sessionId = await operation.Connection.ExecuteScalarAsync<Guid?>(
                "SELECT cash_session_id FROM receivable_settlements WHERE id = @id AND store_id = @StoreId",
                new { id, operation.Identity.StoreId },
                operation.Transaction)
                ?? throw BusinessException.NotFound("Règlement introuvable.");
            await CashSessions.RequireStillOpenAsync(operation, sessionId);

            var status = await operation.Connection.ExecuteScalarAsync<string>(
                "SELECT status FROM receivable_settlements WHERE id = @id FOR UPDATE", new { id }, operation.Transaction);
            if (status != PaymentStatuses.Pending)
            {
                throw new BusinessException(
                    ReceivableErrorCodes.SettlementNotPending, "Ce règlement n'attend pas de réponse du TPE.",
                    StatusCodes.Status409Conflict);
            }

            var ids = (await operation.Connection.QueryAsync<Guid>(
                """
                SELECT r.id FROM receivables r
                WHERE r.id IN (SELECT receivable_id FROM receivable_settlement_items WHERE settlement_id = @id)
                ORDER BY r.id
                FOR UPDATE OF r
                """,
                new { id },
                operation.Transaction)).ToArray();

            await operation.Connection.ExecuteAsync(
                """
                UPDATE receivable_settlements
                SET status = @status, confirmed_at = CASE WHEN @Accepted THEN now() END,
                    resolved_by = @EmployeeId, resolved_at = now(), resolve_operation_id = @OperationId
                WHERE id = @id
                """,
                new
                {
                    id,
                    status = request.Accepted ? PaymentStatuses.Confirmed : PaymentStatuses.Failed,
                    request.Accepted,
                    operation.EmployeeId,
                    operation.OperationId,
                },
                operation.Transaction);

            if (request.Accepted)
            {
                await MarkSettledAsync(operation, ids);
            }
            else
            {
                await operation.Connection.ExecuteAsync(
                    "UPDATE receivable_settlement_items SET is_active = false WHERE settlement_id = @id", new { id }, operation.Transaction);
            }

            await operation.AuditAsync(
                request.Accepted ? ReceivableAuditActions.CardAccepted : ReceivableAuditActions.CardRefused,
                "receivable_settlement", id.ToString(), new { session = sessionId, creances = ids });
            return (await ReceivableQueries.ReadSettlementAsync(operation.Connection, id, operation.Transaction))!;
        });
}
