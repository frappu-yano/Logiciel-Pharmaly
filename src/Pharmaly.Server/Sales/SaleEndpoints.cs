using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Access;
using Pharmaly.Server.Cash;
using Pharmaly.Server.Closures;
using Pharmaly.Server.Operations;
using Pharmaly.Server.Stock;

namespace Pharmaly.Server.Sales;

public static class SaleAuditActions
{
    public const string SaleCreated = "VENTE_ENREGISTREE";
    public const string SaleCancelled = "VENTE_ANNULEE";
    public const string CardAccepted = "VENTE_TPE_ACCEPTE";
    public const string CardRefused = "VENTE_TPE_REFUSE";
}

/// <summary>
/// Ventes : création (espèces, TPE, impayée), réponse du TPE, annulation et historique.
/// <para>
/// Ordre des verrous, le même partout pour éviter les interblocages : session de caisse
/// (partagé) → ticket → créance → soldes de stock (par le moteur) → compteur de tickets.
/// </para>
/// </summary>
public static class SaleEndpoints
{
    public const string CreateOperation = "VENTE_CREER";
    public const string CancelOperation = "VENTE_ANNULER";
    public const string CardResultOperation = "VENTE_TPE_REPONSE";

    /// <summary>Type de document inscrit sur les mouvements de stock d'une vente.</summary>
    public const string SaleSource = "VENTE";

    /// <summary>Motif inscrit quand un paiement par carte refusé annule la vente.</summary>
    public const string CardRefusedReason = "Paiement par carte refusé";

    private const int MaxLines = 200;
    private const int TicketDigits = 6;
    private const int BarcodeDigits = 10;

    public static void MapSaleEndpoints(this WebApplication app)
    {
        app.MapPost(SaleRoutes.Sales, CreateAsync).RequirePermission(Permissions.SalesCreate);
        app.MapPost(SaleRoutes.Sales + "/{id:guid}/" + SaleRoutes.CardResultSuffix, ResolveCardAsync)
            .RequirePermission(Permissions.PaymentsCollect);
        app.MapPost(SaleRoutes.Sales + "/{id:guid}/" + SaleRoutes.CancellationSuffix, CancelAsync)
            .RequirePermission(Permissions.SalesCancel);

        app.MapGet(SaleRoutes.Sales, async (
            DateOnly? du, DateOnly? au, Guid? employe, string? statut, string? mode, string? numero, int? limite,
            HttpContext context, NpgsqlDataSource dataSource) =>
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            return Results.Ok(await SaleQueries.ListAsync(connection, new SaleFilter(
                context.GetIdentity().StoreId,
                du?.ToDateTime(TimeOnly.MinValue),
                au?.ToDateTime(TimeOnly.MinValue),
                employe,
                string.IsNullOrWhiteSpace(statut) ? null : statut.Trim().ToUpperInvariant(),
                string.IsNullOrWhiteSpace(mode) ? null : mode.Trim().ToUpperInvariant(),
                SaleQueries.ToTicketPattern(numero),
                Math.Clamp(limite ?? SaleQueries.DefaultLimit, 1, SaleQueries.MaxLimit))));
        }).RequirePermission(Permissions.SalesHistoryView);

        app.MapGet(SaleRoutes.Sales + "/{id:guid}", async (Guid id, HttpContext context, NpgsqlDataSource dataSource) =>
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            return TicketOrNotFound(await SaleQueries.ReadDetailAsync(connection, id, context.GetIdentity().StoreId));
        }).RequirePermission(Permissions.SalesHistoryView);

        app.MapGet(SaleRoutes.SaleByTicket + "/{code}", async (string code, HttpContext context, NpgsqlDataSource dataSource) =>
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            return TicketOrNotFound(await SaleQueries.FindByTicketAsync(connection, code, context.GetIdentity().StoreId));
        }).RequirePermission(Permissions.SalesHistoryView);
    }

    private static IResult TicketOrNotFound(SaleDetail? sale) =>
        sale is not null
            ? Results.Ok(sale)
            : Results.Json(new ApiError(ApiErrorCodes.NotFound, "Ticket introuvable."), statusCode: StatusCodes.Status404NotFound);

    // ---------- Création ----------

    private static async Task<IResult> CreateAsync(
        CreateSaleRequest request, HttpContext context, NpgsqlDataSource dataSource, OperationRunner runner, StockEngine engine)
    {
        // Les droits qui dépendent du contenu de la demande sont vérifiés avant toute écriture.
        var collects = request.PaymentMode is SalePaymentModes.Cash or SalePaymentModes.Card;
        if (collects && await PermissionGuard.RefuseWithoutAnyAsync(dataSource, context, Permissions.PaymentsCollect) is { } noCollect)
        {
            return noCollect;
        }

        var hasDiscount = request.DiscountKind is not null && request.DiscountValue is not null and not 0m;
        if (hasDiscount && await PermissionGuard.RefuseWithoutAnyAsync(dataSource, context, Permissions.DiscountsApply) is { } noDiscount)
        {
            return noDiscount;
        }

        return await runner.RunAsync(request.OperationId, CreateOperation, context.GetIdentity(), request, async operation =>
        {
            var lines = request.Lines ?? [];
            if (lines.Count is 0 or > MaxLines)
            {
                throw BusinessException.Invalid($"Une vente contient de 1 à {MaxLines} lignes.");
            }

            if (lines.Any(line => line.Quantity <= 0))
            {
                throw BusinessException.Invalid("Chaque quantité vendue doit être positive.");
            }

            if (lines.Select(line => line.ProductId).Distinct().Count() != lines.Count)
            {
                throw BusinessException.Invalid("Le même produit figure sur plusieurs lignes : regroupez les quantités.");
            }

            if (request.PaymentMode is not (SalePaymentModes.Cash or SalePaymentModes.Card or SalePaymentModes.Unpaid))
            {
                throw BusinessException.Invalid("Mode de paiement inconnu : ESPECES, TPE ou IMPAYE.");
            }

            var storeId = operation.Identity.StoreId;
            // Toute vente, même impayée, est rattachée à la session ouverte du poste.
            var sessionId = await CashSessions.RequireOpenAsync(operation);

            // Vente impayée : client obligatoire (D5).
            if (request.PaymentMode == SalePaymentModes.Unpaid && request.CustomerId is null)
            {
                throw new BusinessException(
                    SaleErrorCodes.CustomerRequired, "Une vente impayée exige un client : choisissez-le ou créez-le.");
            }

            if (request.CustomerId is { } customerId
                && !await operation.Connection.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM customers WHERE id = @customerId)", new { customerId }, operation.Transaction))
            {
                throw BusinessException.NotFound("Client introuvable.");
            }

            // Prix, désignation et état lus sur le serveur, maintenant : jamais fournis par le poste.
            var productIds = lines.Select(line => line.ProductId).ToArray();
            var products = (await operation.Connection.QueryAsync<(Guid Id, string Name, decimal SalePrice, bool IsActive)>(
                "SELECT id, name, sale_price, is_active FROM products WHERE id = ANY(@productIds)",
                new { productIds },
                operation.Transaction)).ToDictionary(product => product.Id);
            if (products.Count != productIds.Length)
            {
                throw BusinessException.NotFound("Produit introuvable.");
            }

            if (products.Values.Where(product => !product.IsActive).Select(product => product.Name).FirstOrDefault() is { } inactive)
            {
                throw new BusinessException(
                    SaleErrorCodes.InactiveProduct, $"« {inactive} » est désactivé : il ne peut pas être vendu.",
                    StatusCodes.Status409Conflict);
            }

            var priced = lines.Select((line, index) =>
            {
                var product = products[line.ProductId];
                return (
                    Number: index + 1, line.ProductId, product.Name, line.Quantity, UnitPrice: product.SalePrice,
                    Total: SaleMath.LineTotal(line.Quantity, product.SalePrice));
            }).ToList();
            var subtotal = priced.Sum(line => line.Total);
            if (subtotal > SaleMath.MaxAmount)
            {
                throw BusinessException.Invalid("Le total de la vente est trop grand.");
            }

            var (discountKind, discountValue, discountAmount) = ComputeDiscount(request, subtotal);
            var total = subtotal - discountAmount;
            if (request.ExpectedTotal is { } expected && expected != total)
            {
                throw new BusinessException(
                    SaleErrorCodes.TotalMismatch,
                    $"Le total a changé : {Money.ToDisplay(total)} au lieu de {Money.ToDisplay(expected)}. Vérifiez les prix avant d'encaisser.",
                    StatusCodes.Status409Conflict);
            }

            decimal? amountReceived = null;
            decimal? changeGiven = null;
            switch (request.PaymentMode)
            {
                case SalePaymentModes.Cash:
                    amountReceived = request.AmountReceived
                        ?? throw BusinessException.Invalid("Indiquez le montant reçu en espèces.");
                    if (!SaleMath.IsValidAmount(amountReceived.Value))
                    {
                        throw BusinessException.Invalid("Le montant reçu doit être positif, avec au plus deux décimales.");
                    }

                    if (amountReceived < total)
                    {
                        throw new BusinessException(
                            SaleErrorCodes.InsufficientAmountReceived,
                            $"Montant reçu insuffisant : {Money.ToDisplay(amountReceived.Value)} pour un total de {Money.ToDisplay(total)}.");
                    }

                    changeGiven = amountReceived - total;
                    break;
                case SalePaymentModes.Card when total == 0:
                    throw BusinessException.Invalid("Le total est nul : il n'y a rien à payer par carte.");
                case SalePaymentModes.Unpaid when total == 0:
                    throw BusinessException.Invalid("Le total est nul : il n'y a rien à laisser impayé.");
            }

            var locationId = await ReadSellableLocationAsync(operation, storeId);
            var saleId = Guid.NewGuid();

            // Sortie de stock : un mouvement VENTE par ligne. Le moteur verrouille les soldes et
            // refuse la vente entière si un seul produit manque.
            await engine.ApplyAsync(
                operation.Connection, operation.Transaction, operation.EmployeeId, operation.OperationId,
                priced.Select(line =>
                    StockChange.Move(line.ProductId, locationId, -line.Quantity, StockMovementTypes.Sale)
                        with { SourceType = SaleSource, SourceId = saleId.ToString() }).ToList());

            // Numéro de ticket : pris en dernier, donc jamais consommé par une vente refusée.
            var ticket = await operation.Connection.QuerySingleAsync<(long Sequence, string StoreCode, long Barcode)>(
                """
                WITH counter AS (
                    INSERT INTO ticket_counters (store_id, last_number) VALUES (@storeId, 1)
                    ON CONFLICT (store_id) DO UPDATE SET last_number = ticket_counters.last_number + 1
                    RETURNING last_number)
                SELECT counter.last_number, (SELECT code FROM stores WHERE id = @storeId), nextval('ticket_barcode_seq')
                FROM counter
                """,
                new { storeId },
                operation.Transaction);
            var ticketNumber = $"{ticket.StoreCode}-{ticket.Sequence.ToString().PadLeft(TicketDigits, '0')}";
            var ticketBarcode = "TK" + ticket.Barcode.ToString().PadLeft(BarcodeDigits, '0');
            var isCard = request.PaymentMode == SalePaymentModes.Card;

            await operation.Connection.ExecuteAsync(
                """
                INSERT INTO sales
                    (id, store_id, ticket_seq, ticket_number, ticket_barcode, register_account_id, cash_session_id,
                     employee_id, customer_id, payment_mode, status, subtotal, discount_kind, discount_value,
                     discount_amount, total, operation_id)
                VALUES
                    (@saleId, @storeId, @Sequence, @ticketNumber, @ticketBarcode, @RegisterAccountId, @sessionId,
                     @EmployeeId, @CustomerId, @PaymentMode, @status, @subtotal, @discountKind, @discountValue,
                     @discountAmount, @total, @OperationId)
                """,
                new
                {
                    saleId,
                    storeId,
                    ticket.Sequence,
                    ticketNumber,
                    ticketBarcode,
                    operation.Identity.RegisterAccountId,
                    sessionId,
                    operation.EmployeeId,
                    request.CustomerId,
                    request.PaymentMode,
                    status = isCard ? SaleStatuses.CardPending : SaleStatuses.Completed,
                    subtotal,
                    discountKind,
                    discountValue,
                    discountAmount,
                    total,
                    operation.OperationId,
                },
                operation.Transaction);

            await operation.Connection.ExecuteAsync(
                """
                INSERT INTO sale_lines (sale_id, line_number, product_id, product_name, location_id, quantity, unit_price, line_total)
                SELECT @saleId, l.line_number, l.product_id, l.product_name, @locationId, l.quantity, l.unit_price, l.line_total
                FROM unnest(@numbers, @productIds, @names, @quantities, @prices, @totals)
                     AS l (line_number, product_id, product_name, quantity, unit_price, line_total)
                ORDER BY l.line_number
                """,
                new
                {
                    saleId,
                    locationId,
                    numbers = priced.Select(line => line.Number).ToArray(),
                    productIds = priced.Select(line => line.ProductId).ToArray(),
                    names = priced.Select(line => line.Name).ToArray(),
                    quantities = priced.Select(line => line.Quantity).ToArray(),
                    prices = priced.Select(line => line.UnitPrice).ToArray(),
                    totals = priced.Select(line => line.Total).ToArray(),
                },
                operation.Transaction);

            if (request.PaymentMode == SalePaymentModes.Unpaid)
            {
                // Créance ouverte, aucun paiement : rien n'est encaissé.
                await operation.Connection.ExecuteAsync(
                    "INSERT INTO receivables (sale_id, customer_id, store_id, amount) VALUES (@saleId, @CustomerId, @storeId, @total)",
                    new { saleId, request.CustomerId, storeId, total },
                    operation.Transaction);
            }
            else
            {
                // Espèces : encaissé tout de suite. TPE : en attente de la réponse du terminal.
                await operation.Connection.ExecuteAsync(
                    """
                    INSERT INTO payments
                        (sale_id, method, status, amount, amount_received, change_given, cash_session_id, store_id,
                         register_account_id, employee_id, operation_id, confirmed_at)
                    VALUES
                        (@saleId, @PaymentMode, @status, @total, @amountReceived, @changeGiven, @sessionId, @storeId,
                         @RegisterAccountId, @EmployeeId, @OperationId, CASE WHEN @isCard THEN NULL ELSE now() END)
                    """,
                    new
                    {
                        saleId,
                        request.PaymentMode,
                        status = isCard ? PaymentStatuses.Pending : PaymentStatuses.Confirmed,
                        total,
                        amountReceived,
                        changeGiven,
                        sessionId,
                        storeId,
                        operation.Identity.RegisterAccountId,
                        operation.EmployeeId,
                        operation.OperationId,
                        isCard,
                    },
                    operation.Transaction);
            }

            await operation.AuditAsync(
                SaleAuditActions.SaleCreated, "sale", saleId.ToString(),
                new
                {
                    ticket = ticketNumber,
                    mode = request.PaymentMode,
                    sousTotal = subtotal,
                    remise = discountAmount,
                    typeRemise = discountKind,
                    valeurRemise = discountValue,
                    total,
                    client = request.CustomerId,
                    session = sessionId,
                    lignes = priced.Select(line => new { produit = line.ProductId, quantite = line.Quantity, prix = line.UnitPrice }),
                });
            return await ReadAsync(operation, saleId);
        });
    }

    /// <summary>Remise sur la vente entière (D12) : montant ou pourcentage, jamais supérieure au total.</summary>
    private static (string? Kind, decimal? Value, decimal Amount) ComputeDiscount(CreateSaleRequest request, decimal subtotal)
    {
        static BusinessException Refuse(string message) => new(SaleErrorCodes.InvalidDiscount, message);

        if (request.DiscountKind is null)
        {
            return request.DiscountValue is null or 0m
                ? (null, null, 0m)
                : throw Refuse("Précisez si la remise est un montant ou un pourcentage.");
        }

        if (request.DiscountKind is not (DiscountKinds.Amount or DiscountKinds.Percent))
        {
            throw Refuse("Type de remise inconnu : MONTANT ou POURCENTAGE.");
        }

        var value = request.DiscountValue ?? throw Refuse("Indiquez la valeur de la remise.");
        if (value == 0)
        {
            return (null, null, 0m);
        }

        if (value < 0 || decimal.Round(value, 2) != value)
        {
            throw Refuse("La remise doit être positive, avec au plus deux décimales.");
        }

        if (request.DiscountKind == DiscountKinds.Percent)
        {
            return value > 100
                ? throw Refuse("Une remise ne dépasse pas 100 %.")
                : (DiscountKinds.Percent, value, SaleMath.PercentDiscount(subtotal, value));
        }

        return value > subtotal
            ? throw Refuse($"La remise ({Money.ToDisplay(value)}) dépasse le total de la vente ({Money.ToDisplay(subtotal)}).")
            : (DiscountKinds.Amount, value, value);
    }

    /// <summary>Emplacement vendable du site du poste : l'emplacement principal s'il existe.</summary>
    private static async Task<Guid> ReadSellableLocationAsync(OperationContext operation, Guid storeId) =>
        await SiteLocations.ReadSellableAsync(operation.Connection, storeId, operation.Transaction)
        ?? throw BusinessException.Conflict("Ce site n'a aucun emplacement de stock vendable.");

    private static async Task<SaleDetail> ReadAsync(OperationContext operation, Guid saleId) =>
        (await SaleQueries.ReadDetailAsync(operation.Connection, saleId, operation.Identity.StoreId, operation.Transaction))!;

    // ---------- Réponse du TPE ----------

    /// <summary>
    /// « Accepté » : le paiement est confirmé et la vente validée. « Refusé » : la marchandise
    /// n'est pas partie, donc la vente est annulée et son stock restitué dans la même
    /// transaction ; pour payer autrement, le caissier refait la vente.
    /// </summary>
    private static Task<IResult> ResolveCardAsync(
        Guid id, CardResultRequest request, HttpContext context, OperationRunner runner, StockEngine engine) =>
        runner.RunAsync(request.OperationId, CardResultOperation, context.GetIdentity(), new { id, request }, async operation =>
        {
            var storeId = operation.Identity.StoreId;
            // La session est immuable sur le ticket : lue sans verrou, puis verrouillée en premier.
            var sessionId = await operation.Connection.ExecuteScalarAsync<Guid?>(
                "SELECT cash_session_id FROM sales WHERE id = @id AND store_id = @storeId", new { id, storeId }, operation.Transaction)
                ?? throw BusinessException.NotFound("Ticket introuvable.");
            await CashSessions.RequireStillOpenAsync(operation, sessionId);

            var status = await operation.Connection.ExecuteScalarAsync<string>(
                "SELECT status FROM sales WHERE id = @id FOR UPDATE", new { id }, operation.Transaction);
            if (status != SaleStatuses.CardPending)
            {
                throw new BusinessException(
                    SaleErrorCodes.CardNotPending, "Ce ticket n'attend pas de réponse du TPE.", StatusCodes.Status409Conflict);
            }

            var resolved = await operation.Connection.ExecuteAsync(
                """
                UPDATE payments
                SET status = @status, confirmed_at = CASE WHEN @Accepted THEN now() END,
                    resolved_by = @EmployeeId, resolved_at = now(), resolve_operation_id = @OperationId
                WHERE sale_id = @id AND status = 'EN_ATTENTE'
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
            if (resolved != 1)
            {
                throw new InvalidOperationException($"Le ticket {id} est en attente de TPE sans paiement en attente.");
            }

            if (request.Accepted)
            {
                await operation.Connection.ExecuteAsync(
                    "UPDATE sales SET status = @status WHERE id = @id", new { id, status = SaleStatuses.Completed }, operation.Transaction);
            }
            else
            {
                await RestoreStockAsync(operation, engine, id);
                await MarkCancelledAsync(operation, id, SaleCancellationKinds.CardRefused, CardRefusedReason, null, null, 0m);
            }

            await operation.AuditAsync(
                request.Accepted ? SaleAuditActions.CardAccepted : SaleAuditActions.CardRefused, "sale", id.ToString(),
                new { session = sessionId, stockRestitue = !request.Accepted });
            return await ReadAsync(operation, id);
        });

    // ---------- Annulation ----------

    private static Task<IResult> CancelAsync(
        Guid id, CancelSaleRequest request, HttpContext context, OperationRunner runner, StockEngine engine) =>
        runner.RunAsync(request.OperationId, CancelOperation, context.GetIdentity(), new { id, request }, async operation =>
        {
            var reason = CashSessions.CleanReason(request.Reason, "de l'annulation");
            var storeId = operation.Identity.StoreId;
            // Une vente impayée s'annule sans caisse ouverte : le verrou de la journée empêche
            // qu'une clôture fige la journée pendant l'annulation. Pris avant tout autre verrou.
            await BusinessDayLock.SharedAsync(operation, storeId);

            // Le mode de paiement est immuable : lu sans verrou pour savoir si un remboursement
            // sortira de la caisse de ce poste (session à verrouiller en premier).
            var paymentMode = await operation.Connection.ExecuteScalarAsync<string?>(
                "SELECT payment_mode FROM sales WHERE id = @id AND store_id = @storeId", new { id, storeId }, operation.Transaction)
                ?? throw BusinessException.NotFound("Ticket introuvable.");
            Guid? refundSessionId = paymentMode == SalePaymentModes.Unpaid ? null : await CashSessions.RequireOpenAsync(operation);

            var sale = await operation.Connection.QuerySingleAsync<(string Status, DateTime? BusinessDate, string TicketNumber)>(
                "SELECT status, business_date, ticket_number FROM sales WHERE id = @id FOR UPDATE", new { id }, operation.Transaction);
            switch (sale.Status)
            {
                case SaleStatuses.Cancelled:
                    throw new BusinessException(
                        SaleErrorCodes.AlreadyCancelled, $"Le ticket {sale.TicketNumber} est déjà annulé.", StatusCodes.Status409Conflict);
                case SaleStatuses.CardPending:
                    throw new BusinessException(
                        SaleErrorCodes.CardPending,
                        "Le paiement par carte attend sa réponse : indiquez d'abord « accepté » ou « refusé ».",
                        StatusCodes.Status409Conflict);
            }

            // D13 : une vente dont la journée est clôturée ne s'annule plus.
            if (sale.BusinessDate is not null)
            {
                throw new BusinessException(
                    SaleErrorCodes.DayClosed,
                    "La journée de cette vente est clôturée : elle ne peut plus être annulée. Passez par un retour.",
                    StatusCodes.Status409Conflict);
            }

            // Un retour a déjà rendu de la marchandise et de l'argent : annuler la vente entière
            // les compterait deux fois. Le reste se traite par d'autres retours.
            if (await operation.Connection.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM returns WHERE sale_id = @id)", new { id }, operation.Transaction))
            {
                throw new BusinessException(
                    ReturnErrorCodes.SaleHasReturns,
                    $"Le ticket {sale.TicketNumber} a déjà fait l'objet d'un retour : il ne peut plus être annulé. Passez par un retour.",
                    StatusCodes.Status409Conflict);
            }

            string? refundMethod = null;
            var refundAmount = 0m;
            if (paymentMode == SalePaymentModes.Unpaid)
            {
                await CancelReceivableAsync(operation, id, sale.TicketNumber);
            }
            else
            {
                var paid = await operation.Connection.ExecuteScalarAsync<decimal?>(
                    """
                    UPDATE payments SET status = @refunded WHERE sale_id = @id AND status = @confirmed
                    RETURNING amount
                    """,
                    new { id, refunded = PaymentStatuses.Refunded, confirmed = PaymentStatuses.Confirmed },
                    operation.Transaction)
                    ?? throw new InvalidOperationException($"Le ticket {id} est validé sans paiement confirmé.");
                if (paid > 0)
                {
                    refundMethod = paymentMode;
                    refundAmount = paid;
                }

                // Espèces : le remboursement est une sortie de la caisse du poste qui annule.
                if (refundMethod == PaymentMethods.Cash)
                {
                    await operation.Connection.ExecuteAsync(
                        """
                        INSERT INTO cash_movements
                            (cash_session_id, store_id, register_account_id, employee_id, kind, amount, reason, sale_id, operation_id)
                        VALUES (@refundSessionId, @storeId, @RegisterAccountId, @EmployeeId, @kind, @refundAmount, @reason, @id, @OperationId)
                        """,
                        new
                        {
                            refundSessionId,
                            storeId,
                            operation.Identity.RegisterAccountId,
                            operation.EmployeeId,
                            kind = CashMovementKinds.SaleRefund,
                            refundAmount,
                            reason = $"Annulation du ticket {sale.TicketNumber} : {reason}",
                            id,
                            operation.OperationId,
                        },
                        operation.Transaction);
                }
            }

            await RestoreStockAsync(operation, engine, id);
            await MarkCancelledAsync(
                operation, id, SaleCancellationKinds.Cancelled, reason, refundMethod is null ? null : refundSessionId, refundMethod, refundAmount);

            await operation.AuditAsync(
                SaleAuditActions.SaleCancelled, "sale", id.ToString(),
                new { ticket = sale.TicketNumber, motif = reason, mode = paymentMode, remboursement = refundAmount, moyenRemboursement = refundMethod });
            return await ReadAsync(operation, id);
        });

    /// <summary>Annule la créance d'une vente impayée, sauf si elle est réglée ou en cours de règlement.</summary>
    private static async Task CancelReceivableAsync(OperationContext operation, Guid saleId, string ticketNumber)
    {
        var receivable = await operation.Connection.QuerySingleAsync<(Guid Id, string Status)>(
            "SELECT id, status FROM receivables WHERE sale_id = @saleId FOR UPDATE", new { saleId }, operation.Transaction);
        if (receivable.Status == ReceivableStatuses.Settled)
        {
            throw new BusinessException(
                SaleErrorCodes.ReceivableAlreadySettled,
                $"Le ticket {ticketNumber} a déjà été réglé : il ne peut plus être annulé. Passez par un retour.",
                StatusCodes.Status409Conflict);
        }

        if (await operation.Connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM receivable_settlement_items WHERE receivable_id = @Id AND is_active)",
                new { receivable.Id },
                operation.Transaction))
        {
            throw new BusinessException(
                ReceivableErrorCodes.SettlementPending,
                $"Un règlement par carte est en attente sur le ticket {ticketNumber} : répondez-y avant d'annuler la vente.",
                StatusCodes.Status409Conflict);
        }

        await operation.Connection.ExecuteAsync(
            "UPDATE receivables SET status = @cancelled, cancelled_at = now() WHERE id = @Id AND status = @open",
            new { receivable.Id, cancelled = ReceivableStatuses.Cancelled, open = ReceivableStatuses.Open },
            operation.Transaction);
    }

    /// <summary>Mouvements ANNULATION_VENTE : chaque ligne revient à l'emplacement d'où elle est sortie.</summary>
    private static async Task RestoreStockAsync(OperationContext operation, StockEngine engine, Guid saleId)
    {
        var lines = await operation.Connection.QueryAsync<(Guid ProductId, Guid LocationId, decimal Quantity)>(
            "SELECT product_id, location_id, quantity FROM sale_lines WHERE sale_id = @saleId ORDER BY line_number",
            new { saleId },
            operation.Transaction);
        await engine.ApplyAsync(
            operation.Connection, operation.Transaction, operation.EmployeeId, operation.OperationId,
            lines.Select(line =>
                StockChange.Move(line.ProductId, line.LocationId, line.Quantity, StockMovementTypes.SaleCancellation)
                    with { SourceType = SaleSource, SourceId = saleId.ToString() }).ToList());
    }

    /// <summary>
    /// Marque le ticket annulé et l'associe à l'opération compensatoire. La clé primaire de
    /// « sale_cancellations » interdit une seconde annulation.
    /// </summary>
    private static async Task MarkCancelledAsync(
        OperationContext operation, Guid saleId, string kind, string reason, Guid? refundSessionId, string? refundMethod, decimal refundAmount)
    {
        await operation.Connection.ExecuteAsync(
            """
            INSERT INTO sale_cancellations
                (sale_id, kind, reason, employee_id, register_account_id, store_id, cash_session_id, refund_method,
                 refund_amount, operation_id)
            VALUES (@saleId, @kind, @reason, @EmployeeId, @RegisterAccountId, @StoreId, @refundSessionId, @refundMethod,
                    @refundAmount, @OperationId)
            """,
            new
            {
                saleId,
                kind,
                reason,
                operation.EmployeeId,
                operation.Identity.RegisterAccountId,
                operation.Identity.StoreId,
                refundSessionId,
                refundMethod,
                refundAmount,
                operation.OperationId,
            },
            operation.Transaction);
        await operation.Connection.ExecuteAsync(
            "UPDATE sales SET status = @status WHERE id = @saleId", new { saleId, status = SaleStatuses.Cancelled }, operation.Transaction);
    }
}
