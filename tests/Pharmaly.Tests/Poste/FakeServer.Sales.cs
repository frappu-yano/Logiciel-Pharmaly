using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Tests.Poste;

/// <summary>Ce qui arrive à l'envoi d'une vente quand le réseau est en panne.</summary>
public enum SaleSendFailure
{
    None,

    /// <summary>La demande n'atteint pas le serveur : rien n'est enregistré.</summary>
    RequestLost,

    /// <summary>Le serveur enregistre la vente, mais sa réponse se perd.</summary>
    AnswerLost,
}

/// <summary>
/// Ventes : mêmes règles, mêmes codes et mêmes messages que le vrai serveur, y compris
/// l'idempotence (rejouer un identifiant d'opération renvoie le ticket déjà enregistré).
/// </summary>
internal sealed partial class FakeServer : ISalesClient
{
    private readonly Dictionary<Guid, SaleDetail> salesByOperation = [];
    private int ticketSequence;

    /// <summary>Toutes les demandes de vente reçues, dans l'ordre, y compris celles refusées ou rejouées.</summary>
    public List<CreateSaleRequest> SaleRequests { get; } = [];

    public List<(Guid SaleId, CardResultRequest Request)> CardRequests { get; } = [];

    public List<(Guid SaleId, CancelSaleRequest Request)> CancelRequests { get; } = [];

    /// <summary>Tickets enregistrés, dans l'ordre de création.</summary>
    public List<SaleDetail> Tickets { get; } = [];

    /// <summary>Pannes de réseau à appliquer aux prochains envois de vente, dans l'ordre.</summary>
    public Queue<SaleSendFailure> SaleFailures { get; } = new();

    /// <summary>Pannes de réseau (demande perdue) pour les prochaines réponses de TPE et annulations.</summary>
    public int FailNextTicketWrites { get; set; }

    /// <summary>Quand il est défini, chaque envoi de vente attend qu'il soit terminé : le test tient l'envoi « en cours ».</summary>
    public TaskCompletionSource? SaleGate { get; set; }

    /// <summary>Tickets dont la journée est clôturée : leur annulation est refusée.</summary>
    public HashSet<Guid> ClosedDaySales { get; } = [];

    /// <summary>Produits vendus au détail (fraction permise) ; les autres se vendent à l'unité.</summary>
    public HashSet<Guid> FractionalProducts { get; } = [];

    public int SoldByUnitReads { get; private set; }

    public async Task<SaleDetail> CreateSaleAsync(CreateSaleRequest request, CancellationToken cancellationToken)
    {
        SaleRequests.Add(request);
        if (SaleGate is { } gate)
        {
            await gate.Task;
        }

        var failure = SaleFailures.Count > 0 ? SaleFailures.Dequeue() : SaleSendFailure.None;
        if (failure == SaleSendFailure.RequestLost)
        {
            throw new HttpRequestException("réseau coupé");
        }

        EnsurePermission(Permissions.SalesCreate);
        var collects = request.PaymentMode is SalePaymentModes.Cash or SalePaymentModes.Card;
        if (collects)
        {
            EnsurePermission(Permissions.PaymentsCollect);
        }

        if (request.DiscountKind is not null && request.DiscountValue is not null and not 0m)
        {
            EnsurePermission(Permissions.DiscountsApply);
        }

        if (!salesByOperation.TryGetValue(request.OperationId, out var sale))
        {
            sale = Record(request);
            salesByOperation[request.OperationId] = sale;
        }

        if (failure == SaleSendFailure.AnswerLost)
        {
            throw new TaskCanceledException("délai dépassé");
        }

        return Tickets.Single(ticket => ticket.Id == sale.Id);
    }

    private SaleDetail Record(CreateSaleRequest request)
    {
        if (request.Lines.Count == 0)
        {
            throw new ServerRefusedException(ApiErrorCodes.InvalidRequest, "Une vente contient de 1 à 200 lignes.");
        }

        if (request.Lines.Select(line => line.ProductId).Distinct().Count() != request.Lines.Count)
        {
            throw new ServerRefusedException(
                ApiErrorCodes.InvalidRequest, "Le même produit figure sur plusieurs lignes : regroupez les quantités.");
        }

        if (CashSession is not { } session)
        {
            throw new ServerRefusedException(
                CashErrorCodes.NoOpenSession, "La caisse de ce poste n'est pas ouverte. Ouvrez la caisse avant de vendre ou d'encaisser.");
        }

        var customer = request.CustomerId is { } customerId ? Customers.SingleOrDefault(known => known.Id == customerId) : null;
        if (request.PaymentMode == SalePaymentModes.Unpaid && customer is null)
        {
            throw new ServerRefusedException(
                SaleErrorCodes.CustomerRequired, "Une vente impayée exige un client : choisissez-le ou créez-le.");
        }

        var lines = request.Lines.Select((line, index) =>
        {
            var product = Summarize(Find(line.ProductId));
            if (line.Quantity != decimal.Truncate(line.Quantity) && !FractionalProducts.Contains(product.Id))
            {
                throw new ServerRefusedException(
                    ApiErrorCodes.FractionalQuantity, $"« {product.Name} » se vend à l'unité : quantité entière obligatoire.");
            }

            return new SaleLineDetail(
                index + 1, product.Id, product.Name, line.Quantity, product.SalePrice, SaleMath.LineTotal(line.Quantity, product.SalePrice));
        }).ToList();
        var subtotal = lines.Sum(line => line.LineTotal);
        var discount = request.DiscountKind switch
        {
            DiscountKinds.Percent => SaleMath.PercentDiscount(subtotal, request.DiscountValue ?? 0),
            DiscountKinds.Amount => request.DiscountValue ?? 0,
            _ => 0m,
        };
        if (discount > subtotal)
        {
            throw new ServerRefusedException(
                SaleErrorCodes.InvalidDiscount,
                $"La remise ({Money.ToDisplay(discount)}) dépasse le total de la vente ({Money.ToDisplay(subtotal)}).");
        }

        var total = subtotal - discount;
        if (request.ExpectedTotal is { } expected && expected != total)
        {
            throw new ServerRefusedException(
                SaleErrorCodes.TotalMismatch,
                $"Le total a changé : {Money.ToDisplay(total)} au lieu de {Money.ToDisplay(expected)}. Vérifiez les prix avant d'encaisser.");
        }

        if (request.PaymentMode == SalePaymentModes.Cash && (request.AmountReceived ?? -1) < total)
        {
            throw new ServerRefusedException(
                SaleErrorCodes.InsufficientAmountReceived,
                $"Montant reçu insuffisant : {Money.ToDisplay(request.AmountReceived ?? 0)} pour un total de {Money.ToDisplay(total)}.");
        }

        foreach (var line in lines)
        {
            var available = StockLevels.GetValueOrDefault(line.ProductId);
            if (line.Quantity > available)
            {
                throw new ServerRefusedException(
                    ApiErrorCodes.InsufficientStock,
                    $"Stock insuffisant pour « {line.ProductName} » : {line.Quantity:0.###} demandé, {available:0.###} disponible.");
            }
        }

        foreach (var line in lines)
        {
            StockLevels[line.ProductId] -= line.Quantity;
        }

        var id = Guid.NewGuid();
        var now = DateTimeOffset.Now;
        var isCard = request.PaymentMode == SalePaymentModes.Card;
        var payment = request.PaymentMode == SalePaymentModes.Unpaid
            ? null
            : new SalePaymentDetail(
                Guid.NewGuid(), request.PaymentMode, isCard ? PaymentStatuses.Pending : PaymentStatuses.Confirmed, total,
                request.AmountReceived, request.AmountReceived - total, session.Id, EmployeeId, EmployeeName, now, isCard ? null : now);
        var receivable = request.PaymentMode == SalePaymentModes.Unpaid
            ? new SaleReceivableDetail(Guid.NewGuid(), ReceivableStatuses.Open, total, null)
            : null;
        var sale = new SaleDetail(
            id, $"D1-{++ticketSequence:000000}", $"TK{ticketSequence:0000000000}", session.StoreId, "Pharmaly 1 — Didouche",
            session.RegisterName, session.Id, EmployeeId, EmployeeName, now,
            isCard ? SaleStatuses.CardPending : SaleStatuses.Completed, request.PaymentMode, receivable is not null, subtotal,
            discount == 0 ? null : request.DiscountKind, discount == 0 ? null : request.DiscountValue, discount, total,
            customer?.Id, customer?.Name, lines, payment, receivable, null);
        Tickets.Add(sale);
        return sale;
    }

    private SaleDetail Ticket(Guid saleId) =>
        Tickets.SingleOrDefault(ticket => ticket.Id == saleId)
        ?? throw new ServerRefusedException(ApiErrorCodes.NotFound, "Ticket introuvable.");

    private SaleDetail Replace(SaleDetail updated)
    {
        Tickets[Tickets.FindIndex(ticket => ticket.Id == updated.Id)] = updated;
        return updated;
    }

    private void FailTicketWriteIfAsked()
    {
        if (FailNextTicketWrites > 0)
        {
            FailNextTicketWrites--;
            throw new HttpRequestException("réseau coupé");
        }
    }

    private SaleDetail Cancelled(SaleDetail sale, string kind, string reason, string? refundMethod, decimal refundAmount)
    {
        foreach (var line in sale.Lines)
        {
            StockLevels[line.ProductId] += line.Quantity;
        }

        return Replace(sale with
        {
            Status = SaleStatuses.Cancelled,
            IsUnpaid = false,
            Receivable = sale.Receivable is null ? null : sale.Receivable with { Status = ReceivableStatuses.Cancelled },
            Cancellation = new SaleCancellationDetail(kind, reason, EmployeeId, EmployeeName, DateTimeOffset.Now, refundMethod, refundAmount),
        });
    }

    public Task<SaleDetail> ResolveCardAsync(Guid saleId, CardResultRequest request, CancellationToken cancellationToken)
    {
        CardRequests.Add((saleId, request));
        FailTicketWriteIfAsked();
        EnsurePermission(Permissions.PaymentsCollect);
        var sale = Ticket(saleId);
        if (sale.Status != SaleStatuses.CardPending)
        {
            throw new ServerRefusedException(SaleErrorCodes.CardNotPending, "Ce ticket n'attend pas de réponse du TPE.");
        }

        return Task.FromResult(request.Accepted
            ? Replace(sale with
            {
                Status = SaleStatuses.Completed,
                Payment = sale.Payment! with { Status = PaymentStatuses.Confirmed, ConfirmedAt = DateTimeOffset.Now },
            })
            : Cancelled(
                sale with { Payment = sale.Payment! with { Status = PaymentStatuses.Failed } },
                SaleCancellationKinds.CardRefused, "Paiement par carte refusé", null, 0m));
    }

    public Task<SaleDetail> CancelSaleAsync(Guid saleId, CancelSaleRequest request, CancellationToken cancellationToken)
    {
        CancelRequests.Add((saleId, request));
        FailTicketWriteIfAsked();
        EnsurePermission(Permissions.SalesCancel);
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new ServerRefusedException(ApiErrorCodes.InvalidRequest, "Le motif de l'annulation est obligatoire (500 caractères au plus).");
        }

        var sale = Ticket(saleId);
        switch (sale.Status)
        {
            case SaleStatuses.Cancelled:
                throw new ServerRefusedException(SaleErrorCodes.AlreadyCancelled, $"Le ticket {sale.TicketNumber} est déjà annulé.");
            case SaleStatuses.CardPending:
                throw new ServerRefusedException(
                    SaleErrorCodes.CardPending, "Le paiement par carte attend sa réponse : indiquez d'abord « accepté » ou « refusé ».");
        }

        if (ClosedDaySales.Contains(saleId))
        {
            throw new ServerRefusedException(
                SaleErrorCodes.DayClosed, "La journée de cette vente est clôturée : elle ne peut plus être annulée. Passez par un retour.");
        }

        if (sale.Receivable?.Status == ReceivableStatuses.Settled)
        {
            throw new ServerRefusedException(
                SaleErrorCodes.ReceivableAlreadySettled,
                $"Le ticket {sale.TicketNumber} a déjà été réglé : il ne peut plus être annulé. Passez par un retour.");
        }

        var refunded = sale.Payment is null ? sale : sale with { Payment = sale.Payment with { Status = PaymentStatuses.Refunded } };
        return Task.FromResult(Cancelled(
            refunded, SaleCancellationKinds.Cancelled, request.Reason.Trim(), sale.Payment?.Method, sale.Payment?.Amount ?? 0m));
    }

    /// <summary>Simule le règlement de la créance d'un ticket impayé par le module Caisse.</summary>
    public void SettleReceivable(Guid saleId)
    {
        var sale = Ticket(saleId);
        Replace(sale with
        {
            IsUnpaid = false,
            Receivable = sale.Receivable! with { Status = ReceivableStatuses.Settled, SettledAt = DateTimeOffset.Now },
        });
    }

    public List<SalesHistoryQuery> HistoryQueries { get; } = [];

    public Task<IReadOnlyList<SaleSummary>> ListSalesAsync(SalesHistoryQuery query, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.SalesHistoryView);
        HistoryQueries.Add(query);
        var found = Tickets
            .Where(ticket => query.Status is null || ticket.Status == query.Status)
            .Where(ticket => query.From is null || DateOnly.FromDateTime(ticket.CreatedAt.LocalDateTime) >= query.From)
            .Where(ticket => query.To is null || DateOnly.FromDateTime(ticket.CreatedAt.LocalDateTime) <= query.To)
            .Where(ticket => query.Ticket is null
                || ticket.TicketNumber.Contains(query.Ticket, StringComparison.OrdinalIgnoreCase)
                || ticket.TicketBarcode.Contains(query.Ticket, StringComparison.OrdinalIgnoreCase))
            .Reverse()
            .Select(ticket => new SaleSummary(
                ticket.Id, ticket.TicketNumber, ticket.TicketBarcode, ticket.CreatedAt, ticket.Status, ticket.PaymentMode, ticket.IsUnpaid,
                ticket.Total, ticket.EmployeeId, ticket.EmployeeName, ticket.CustomerName))
            .ToList();
        return Task.FromResult<IReadOnlyList<SaleSummary>>(found);
    }

    public Task<SaleDetail?> GetSaleAsync(Guid saleId, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.SalesHistoryView);
        return Task.FromResult(Tickets.SingleOrDefault(ticket => ticket.Id == saleId));
    }

    public Task<SaleDetail?> FindSaleByTicketAsync(string numberOrBarcode, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.SalesHistoryView);
        var code = numberOrBarcode.Trim().ToUpperInvariant();
        return Task.FromResult(Tickets.SingleOrDefault(ticket => ticket.TicketNumber == code || ticket.TicketBarcode == code));
    }

    public Task<bool> IsSoldByUnitAsync(Guid productId, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.ProductsView);
        SoldByUnitReads++;
        return Task.FromResult(!FractionalProducts.Contains(productId));
    }
}
