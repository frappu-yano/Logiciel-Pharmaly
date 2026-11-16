using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Access;
using Pharmaly.Server.Cash;
using Pharmaly.Server.Operations;
using Pharmaly.Server.Sales;
using Pharmaly.Server.Stock;

namespace Pharmaly.Server.Returns;

public static class ReturnAuditActions
{
    public const string ReturnCreated = "RETOUR_ENREGISTRE";
    public const string ReasonCreated = "MOTIF_RETOUR_CREE";
    public const string ReasonUpdated = "MOTIF_RETOUR_MODIFIE";
}

/// <summary>
/// Retours clients : à partir d'un ticket, sur une ou plusieurs de ses lignes.
/// <para>
/// Un retour est un document compensatoire, jamais modifié. Il appartient à la session de
/// caisse du poste qui le fait et à la journée où il est fait : il ne réécrit ni la vente
/// d'origine ni son jour. Le motif décide de la destination de la marchandise.
/// </para>
/// <para>
/// Ordre des verrous, le même que pour les ventes : session de caisse (partagé) → ticket →
/// créance (partagé) → soldes de stock (par le moteur) → compteur de retours.
/// </para>
/// </summary>
public static class ReturnEndpoints
{
    public const string CreateOperation = "RETOUR_CREER";
    public const string CreateReasonOperation = "MOTIF_RETOUR_CREER";
    public const string UpdateReasonOperation = "MOTIF_RETOUR_MODIFIER";

    /// <summary>Type de document inscrit sur les mouvements de stock d'un retour.</summary>
    public const string ReturnSource = "RETOUR";

    private const int ReturnDigits = 6;
    private const int MaxLabelLength = 100;
    private const int MaxNoteLength = 500;

    public static void MapReturnEndpoints(this WebApplication app)
    {
        // ---------- Motifs ----------

        app.MapGet(ReturnRoutes.Reasons, async (bool? tous, NpgsqlDataSource dataSource) =>
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            return Results.Ok(await ReturnQueries.ListReasonsAsync(connection, tous ?? false));
        }).RequireEmployee();

        // Pas de droit dédié dans le cahier des charges : la liste des motifs s'administre
        // avec « administrer les utilisateurs et permissions ».
        app.MapPost(ReturnRoutes.Reasons, CreateReasonAsync).RequirePermission(Permissions.UsersAdminister);
        app.MapPut(ReturnRoutes.Reasons + "/{id:guid}", UpdateReasonAsync).RequirePermission(Permissions.UsersAdminister);

        // ---------- Retours ----------

        app.MapGet(ReturnRoutes.ReturnableByTicket + "/{code}", async (string code, HttpContext context, NpgsqlDataSource dataSource) =>
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            // Une seule photographie pour le ticket, sa créance et ses retours.
            await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
            var sale = await ReturnRules.ReadSaleAsync(
                connection, transaction, context.GetIdentity().StoreId, null, SaleQueries.NormalizeTicketCode(code), lockSale: false);
            return sale is null ? NotFound("Ticket introuvable.") : Results.Ok(ToReturnable(sale));
        }).RequirePermission(Permissions.ReturnsCreate);

        app.MapPost(ReturnRoutes.Preview, async (PreviewReturnRequest request, HttpContext context, NpgsqlDataSource dataSource) =>
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
            try
            {
                var computed = await ReturnRules.ComputeAsync(
                    connection, transaction, context.GetIdentity().StoreId, request.SaleId, request.ReasonId, request.Lines, lockSale: false);
                var restocks = computed.Reason.StockDestination != ReturnStockDestinations.None;
                return Results.Ok(new ReturnPreview(
                    computed.Sale.Id, computed.Sale.TicketNumber, computed.Reason.Label, computed.Reason.StockDestination,
                    computed.RefundMethod, computed.RefundAmount,
                    computed.Lines.Select(line => new ReturnPreviewLine(
                        line.SaleLine.ProductId, line.SaleLine.ProductName, line.Quantity, line.Refund,
                        restocks ? line.Quantity : 0m)).ToList()));
            }
            catch (BusinessException exception)
            {
                return Results.Json(exception.ToErrorBody(), statusCode: exception.StatusCode);
            }
        }).RequirePermission(Permissions.ReturnsCreate);

        app.MapPost(ReturnRoutes.Returns, CreateAsync).RequirePermission(Permissions.ReturnsCreate);

        // Historique : ouvert à qui fait des retours ou consulte l'historique des ventes.
        app.MapGet(ReturnRoutes.Returns, async (
            DateOnly? du, DateOnly? au, string? ticket, Guid? vente, int? limite, HttpContext context, NpgsqlDataSource dataSource) =>
        {
            if (await RefuseWithoutHistoryRightAsync(dataSource, context) is { } refusal)
            {
                return refusal;
            }

            await using var connection = await dataSource.OpenConnectionAsync();
            return Results.Ok(await ReturnQueries.ListAsync(connection, new ReturnFilter(
                context.GetIdentity().StoreId,
                du?.ToDateTime(TimeOnly.MinValue),
                au?.ToDateTime(TimeOnly.MinValue),
                vente,
                SaleQueries.ToTicketPattern(ticket),
                Math.Clamp(limite ?? ReturnQueries.DefaultLimit, 1, ReturnQueries.MaxLimit))));
        }).RequireEmployee();

        app.MapGet(ReturnRoutes.Returns + "/{id:guid}", async (Guid id, HttpContext context, NpgsqlDataSource dataSource) =>
        {
            if (await RefuseWithoutHistoryRightAsync(dataSource, context) is { } refusal)
            {
                return refusal;
            }

            await using var connection = await dataSource.OpenConnectionAsync();
            return await ReturnQueries.ReadDetailAsync(connection, id, context.GetIdentity().StoreId) is { } detail
                ? Results.Ok(detail)
                : NotFound("Retour introuvable.");
        }).RequireEmployee();
    }

    private static Task<IResult?> RefuseWithoutHistoryRightAsync(NpgsqlDataSource dataSource, HttpContext context) =>
        PermissionGuard.RefuseWithoutAnyAsync(dataSource, context, Permissions.ReturnsCreate, Permissions.SalesHistoryView);

    private static IResult NotFound(string message) =>
        Results.Json(new ApiError(ApiErrorCodes.NotFound, message), statusCode: StatusCodes.Status404NotFound);

    private static ReturnableSale ToReturnable(ReturnSaleState sale) =>
        new(
            sale.Id, sale.TicketNumber, sale.TicketBarcode,
            new DateTimeOffset(DateTime.SpecifyKind(sale.CreatedAt, DateTimeKind.Utc)),
            sale.Status, sale.PaymentMode, sale.Subtotal, sale.DiscountAmount, sale.Total, sale.CustomerName,
            sale.Blocked is null && sale.Lines.Any(line => line.Returned < line.Quantity),
            sale.Blocked?.Code, sale.Blocked?.Message, sale.RefundMethod,
            sale.Lines.Sum(line => line.Refunded),
            sale.Lines.Select(line => new ReturnableLine(
                line.LineNumber, line.ProductId, line.ProductName, line.Quantity, line.Returned, line.Quantity - line.Returned,
                line.UnitPrice, line.Paid, line.Refunded,
                ReturnMath.RefundForLine(line.Paid, line.Quantity, line.Returned, line.Quantity - line.Returned))).ToList());

    // ---------- Enregistrement ----------

    private static Task<IResult> CreateAsync(
        CreateReturnRequest request, HttpContext context, OperationRunner runner, StockEngine engine) =>
        runner.RunAsync(request.OperationId, CreateOperation, context.GetIdentity(), request, async operation =>
        {
            var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
            if (note?.Length > MaxNoteLength)
            {
                throw BusinessException.Invalid($"La remarque ne dépasse pas {MaxNoteLength} caractères.");
            }

            var storeId = operation.Identity.StoreId;
            // Tout retour est rattaché à la session ouverte du poste : aucun n'échappe à la clôture,
            // et un remboursement en espèces sort de cette caisse.
            var sessionId = await CashSessions.RequireOpenAsync(operation);

            // Le ticket est verrouillé : les quantités déjà retournées ne changent plus d'ici la fin.
            var computed = await ReturnRules.ComputeAsync(
                operation.Connection, operation.Transaction, storeId, request.SaleId, request.ReasonId, request.Lines, lockSale: true);
            if (request.ExpectedRefund is { } expected && expected != computed.RefundAmount)
            {
                throw new BusinessException(
                    ReturnErrorCodes.RefundMismatch,
                    $"Le remboursement a changé : {Money.ToDisplay(computed.RefundAmount)} au lieu de {Money.ToDisplay(expected)}. "
                    + "Vérifiez avant de rembourser.",
                    StatusCodes.Status409Conflict);
            }

            var destination = computed.Reason.StockDestination;
            var locationId = await ReadDestinationLocationAsync(operation, storeId, destination);
            var returnId = Guid.NewGuid();

            // Mouvements RETOUR, distincts de ceux de la vente. Motif sans réintégration : aucun
            // mouvement, la quantité sortie du circuit reste inscrite sur les lignes du retour.
            if (locationId is { } target)
            {
                await engine.ApplyAsync(
                    operation.Connection, operation.Transaction, operation.EmployeeId, operation.OperationId,
                    computed.Lines.Select(line =>
                        StockChange.Move(line.SaleLine.ProductId, target, line.Quantity, StockMovementTypes.Return)
                            with { SourceType = ReturnSource, SourceId = returnId.ToString(), Note = computed.Reason.Label }).ToList());
            }

            var number = await operation.Connection.QuerySingleAsync<(long Sequence, string StoreCode)>(
                """
                WITH counter AS (
                    INSERT INTO return_counters (store_id, last_number) VALUES (@storeId, 1)
                    ON CONFLICT (store_id) DO UPDATE SET last_number = return_counters.last_number + 1
                    RETURNING last_number)
                SELECT counter.last_number, (SELECT code FROM stores WHERE id = @storeId) FROM counter
                """,
                new { storeId },
                operation.Transaction);
            var returnNumber = $"{number.StoreCode}-R{number.Sequence.ToString().PadLeft(ReturnDigits, '0')}";

            await operation.Connection.ExecuteAsync(
                """
                INSERT INTO returns
                    (id, store_id, return_seq, return_number, sale_id, reason_id, reason_label, stock_destination,
                     destination_location_id, note, employee_id, register_account_id, cash_session_id, refund_method,
                     refund_amount, operation_id)
                VALUES
                    (@returnId, @storeId, @Sequence, @returnNumber, @saleId, @reasonId, @reasonLabel, @destination,
                     @locationId, @note, @EmployeeId, @RegisterAccountId, @sessionId, @RefundMethod,
                     @RefundAmount, @OperationId)
                """,
                new
                {
                    returnId,
                    storeId,
                    number.Sequence,
                    returnNumber,
                    saleId = computed.Sale.Id,
                    reasonId = computed.Reason.Id,
                    reasonLabel = computed.Reason.Label,
                    destination,
                    locationId,
                    note,
                    operation.EmployeeId,
                    operation.Identity.RegisterAccountId,
                    sessionId,
                    computed.RefundMethod,
                    computed.RefundAmount,
                    operation.OperationId,
                },
                operation.Transaction);

            await operation.Connection.ExecuteAsync(
                """
                INSERT INTO return_lines
                    (return_id, line_number, sale_line_id, product_id, product_name, quantity, unit_price, refund_amount,
                     restocked_quantity)
                SELECT @returnId, l.line_number, l.sale_line_id, l.product_id, l.product_name, l.quantity, l.unit_price,
                       l.refund_amount, CASE WHEN @restocks THEN l.quantity ELSE 0 END
                FROM unnest(@numbers, @saleLineIds, @productIds, @names, @quantities, @prices, @refunds)
                     AS l (line_number, sale_line_id, product_id, product_name, quantity, unit_price, refund_amount)
                ORDER BY l.line_number
                """,
                new
                {
                    returnId,
                    restocks = locationId is not null,
                    numbers = Enumerable.Range(1, computed.Lines.Count).ToArray(),
                    saleLineIds = computed.Lines.Select(line => line.SaleLine.SaleLineId).ToArray(),
                    productIds = computed.Lines.Select(line => line.SaleLine.ProductId).ToArray(),
                    names = computed.Lines.Select(line => line.SaleLine.ProductName).ToArray(),
                    quantities = computed.Lines.Select(line => line.Quantity).ToArray(),
                    prices = computed.Lines.Select(line => line.SaleLine.UnitPrice).ToArray(),
                    refunds = computed.Lines.Select(line => line.Refund).ToArray(),
                },
                operation.Transaction);

            await operation.AuditAsync(
                ReturnAuditActions.ReturnCreated, "return", returnId.ToString(),
                new
                {
                    retour = returnNumber,
                    ticket = computed.Sale.TicketNumber,
                    vente = computed.Sale.Id,
                    motif = computed.Reason.Label,
                    destination,
                    emplacement = locationId,
                    remboursement = computed.RefundAmount,
                    moyenRemboursement = computed.RefundMethod,
                    session = sessionId,
                    remarque = note,
                    lignes = computed.Lines.Select(line => new
                    {
                        produit = line.SaleLine.ProductId,
                        quantite = line.Quantity,
                        remboursement = line.Refund,
                        reintegre = locationId is not null,
                    }),
                });
            return (await ReturnQueries.ReadDetailAsync(operation.Connection, returnId, storeId, operation.Transaction))!;
        });

    /// <summary>Emplacement du site où le motif envoie la marchandise ; null si elle n'est pas réintégrée.</summary>
    private static async Task<Guid?> ReadDestinationLocationAsync(OperationContext operation, Guid storeId, string destination)
    {
        if (destination == ReturnStockDestinations.None)
        {
            return null;
        }

        var setAside = destination == ReturnStockDestinations.SetAside;
        return await operation.Connection.ExecuteScalarAsync<Guid?>(
            """
            SELECT id FROM stock_locations
            WHERE store_id = @storeId AND kind = @kind AND is_active
            ORDER BY (code = @preferredCode) DESC, code
            LIMIT 1
            """,
            new
            {
                storeId,
                kind = setAside ? StockLocationKinds.SetAside : StockLocationKinds.Sellable,
                preferredCode = setAside ? SiteSetup.SetAsideLocationCode : SiteSetup.MainLocationCode,
            },
            operation.Transaction)
            ?? throw new BusinessException(
                ReturnErrorCodes.NoDestinationLocation,
                setAside
                    ? "Ce site n'a aucun emplacement « mis à l'écart » : le retour ne peut pas être rangé."
                    : "Ce site n'a aucun emplacement de stock vendable : le retour ne peut pas être rangé.",
                StatusCodes.Status409Conflict);
    }

    // ---------- Administration des motifs ----------

    private static (string Label, string Destination) CleanReason(string? label, string? stockDestination)
    {
        var cleaned = (label ?? string.Empty).Trim();
        if (cleaned.Length is 0 or > MaxLabelLength)
        {
            throw BusinessException.Invalid($"Le libellé du motif est obligatoire ({MaxLabelLength} caractères au plus).");
        }

        var destination = (stockDestination ?? string.Empty).Trim().ToUpperInvariant();
        return ReturnStockDestinations.IsKnown(destination)
            ? (cleaned, destination)
            : throw BusinessException.Invalid("Destination du stock inconnue : VENDABLE, ECARTE ou AUCUNE.");
    }

    private static BusinessException DuplicateLabel(string label) =>
        BusinessException.Conflict($"Un motif « {label} » existe déjà.");

    private static Task<IResult> CreateReasonAsync(CreateReturnReasonRequest request, HttpContext context, OperationRunner runner) =>
        runner.RunAsync(request.OperationId, CreateReasonOperation, context.GetIdentity(), request, async operation =>
        {
            var (label, destination) = CleanReason(request.Label, request.StockDestination);
            // La table est verrouillée le temps de vérifier le libellé : pas de doublon, pas d'erreur en concurrence.
            await operation.Connection.ExecuteAsync("LOCK TABLE return_reasons IN SHARE ROW EXCLUSIVE MODE", transaction: operation.Transaction);
            if (await operation.Connection.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM return_reasons WHERE lower(label) = lower(@label))", new { label }, operation.Transaction))
            {
                throw DuplicateLabel(label);
            }

            var id = await operation.Connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO return_reasons (label, stock_destination, sort_order)
                VALUES (@label, @destination, (SELECT COALESCE(max(sort_order), 0) + 1 FROM return_reasons))
                RETURNING id
                """,
                new { label, destination },
                operation.Transaction);
            await operation.AuditAsync(ReturnAuditActions.ReasonCreated, "return_reason", id.ToString(), new { libelle = label, destination });
            return await ReadReasonAsync(operation, id);
        });

    private static Task<IResult> UpdateReasonAsync(Guid id, UpdateReturnReasonRequest request, HttpContext context, OperationRunner runner) =>
        runner.RunAsync(request.OperationId, UpdateReasonOperation, context.GetIdentity(), new { id, request }, async operation =>
        {
            var (label, destination) = CleanReason(request.Label, request.StockDestination);
            await operation.Connection.ExecuteAsync("LOCK TABLE return_reasons IN SHARE ROW EXCLUSIVE MODE", transaction: operation.Transaction);
            var before = await operation.Connection.QuerySingleOrDefaultAsync<ReturnReason>(
                ReturnQueries.ReasonSql + " WHERE id = @id", new { id }, operation.Transaction)
                ?? throw BusinessException.NotFound("Motif de retour introuvable.");
            if (await operation.Connection.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM return_reasons WHERE lower(label) = lower(@label) AND id <> @id)",
                    new { label, id },
                    operation.Transaction))
            {
                throw DuplicateLabel(label);
            }

            // Les retours déjà enregistrés gardent le libellé et la destination de leur époque.
            await operation.Connection.ExecuteAsync(
                """
                UPDATE return_reasons
                SET label = @label, stock_destination = @destination, is_active = @IsActive, updated_at = now()
                WHERE id = @id
                """,
                new { id, label, destination, request.IsActive },
                operation.Transaction);
            await operation.AuditAsync(
                ReturnAuditActions.ReasonUpdated, "return_reason", id.ToString(),
                new
                {
                    avant = new { libelle = before.Label, destination = before.StockDestination, actif = before.IsActive },
                    apres = new { libelle = label, destination, actif = request.IsActive },
                });
            return await ReadReasonAsync(operation, id);
        });

    private static Task<ReturnReason> ReadReasonAsync(OperationContext operation, Guid id) =>
        operation.Connection.QuerySingleAsync<ReturnReason>(
            ReturnQueries.ReasonSql + " WHERE id = @id", new { id }, operation.Transaction);
}
