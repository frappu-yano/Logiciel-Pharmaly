using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Tests.Poste;

/// <summary>
/// Retours clients : mêmes règles, mêmes codes et mêmes messages que le vrai serveur, y compris
/// le remboursement (calculé avec <see cref="ReturnMath"/>) et l'idempotence.
/// </summary>
internal sealed partial class FakeServer : IReturnsClient
{
    /// <summary>Les quatre motifs créés par le serveur (D8).</summary>
    public static readonly ReturnReason Resellable = new(Guid.NewGuid(), "Retour revendable", ReturnStockDestinations.Sellable, true, 1);

    public static readonly ReturnReason Damaged = new(Guid.NewGuid(), "Produit endommagé", ReturnStockDestinations.None, true, 2);

    public static readonly ReturnReason Defective = new(Guid.NewGuid(), "Produit défectueux", ReturnStockDestinations.SetAside, true, 3);

    public static readonly ReturnReason SaleMistake = new(Guid.NewGuid(), "Erreur de vente", ReturnStockDestinations.Sellable, true, 4);

    private readonly Dictionary<Guid, ReturnDetail> returnsByOperation = [];
    private readonly Dictionary<Guid, ReturnReason> reasonsByOperation = [];
    private int returnSequence;

    /// <summary>Motifs du serveur, dans leur ordre.</summary>
    public List<ReturnReason> ReturnReasons { get; } = [Resellable, Damaged, Defective, SaleMistake];

    /// <summary>Retours enregistrés, dans l'ordre.</summary>
    public List<ReturnDetail> RecordedReturns { get; } = [];

    /// <summary>Toutes les demandes de retour reçues, y compris refusées ou rejouées.</summary>
    public List<CreateReturnRequest> ReturnRequests { get; } = [];

    public List<PreviewReturnRequest> PreviewRequests { get; } = [];

    public List<CreateReturnReasonRequest> ReasonCreateRequests { get; } = [];

    public List<(Guid Id, UpdateReturnReasonRequest Request)> ReasonUpdateRequests { get; } = [];

    /// <summary>Pannes de réseau à appliquer aux prochains envois de retour, dans l'ordre.</summary>
    public Queue<SaleSendFailure> ReturnFailures { get; } = new();

    /// <summary>Quand il est défini, chaque envoi de retour attend qu'il soit terminé.</summary>
    public TaskCompletionSource? ReturnGate { get; set; }

    /// <summary>Ajouté au remboursement calculé : simule un changement côté serveur entre l'aperçu et l'envoi.</summary>
    public decimal RefundDrift { get; set; }

    /// <summary>Stock « mis à l'écart », par produit.</summary>
    public Dictionary<Guid, decimal> SetAsideStock { get; } = [];

    /// <summary>Pannes de réseau (demande perdue) pour les prochaines écritures de motifs.</summary>
    public int FailNextReasonWrites { get; set; }

    // ---------- Ticket ----------

    public Task<ReturnableSale?> FindReturnableAsync(string numberOrBarcode, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.ReturnsCreate);
        var code = numberOrBarcode.Trim().ToUpperInvariant();
        var sale = Tickets.SingleOrDefault(ticket => ticket.TicketNumber == code || ticket.TicketBarcode == code);
        return Task.FromResult(sale is null ? null : ToReturnable(sale));
    }

    private decimal ReturnedOf(Guid saleId, Guid productId) =>
        RecordedReturns.Where(done => done.SaleId == saleId).SelectMany(done => done.Lines)
            .Where(line => line.ProductId == productId).Sum(line => line.Quantity);

    private decimal RefundedOf(Guid saleId, Guid productId) =>
        RecordedReturns.Where(done => done.SaleId == saleId).SelectMany(done => done.Lines)
            .Where(line => line.ProductId == productId).Sum(line => line.RefundAmount);

    private IReadOnlyList<decimal> PaidPerLine(SaleDetail sale) =>
        ReturnMath.PaidPerLine(sale.Lines.Select(line => line.LineTotal).ToList(), sale.Subtotal, sale.Total);

    /// <summary>Pourquoi le ticket n'est pas retournable (même texte que le serveur), et le moyen de remboursement.</summary>
    private static (ServerRefusedException? Blocked, string? RefundMethod) StateOf(SaleDetail sale)
    {
        if (sale.Status != SaleStatuses.Completed)
        {
            return (new ServerRefusedException(
                ReturnErrorCodes.SaleNotReturnable,
                sale.Status == SaleStatuses.Cancelled
                    ? $"Le ticket {sale.TicketNumber} est annulé : il n'y a rien à retourner."
                    : $"Le paiement par carte du ticket {sale.TicketNumber} attend sa réponse : indiquez d'abord « accepté » ou « refusé »."),
                null);
        }

        if (sale.PaymentMode == SalePaymentModes.Unpaid)
        {
            return sale.Receivable?.Status == ReceivableStatuses.Settled
                ? (null, PaymentMethods.Cash)
                : (new ServerRefusedException(
                    ReturnErrorCodes.ReceivableOpen,
                    $"Le ticket {sale.TicketNumber} est impayé : aucun retour tant que sa créance n'est pas réglée en entier. "
                    + "Si la marchandise revient avant le règlement, annulez la vente (possible tant que sa journée n'est pas clôturée) ; "
                    + "sinon, encaissez d'abord le ticket, puis faites le retour."),
                    null);
        }

        return (null, sale.PaymentMode);
    }

    private ReturnableSale ToReturnable(SaleDetail sale)
    {
        var (blocked, refundMethod) = StateOf(sale);
        var paid = PaidPerLine(sale);
        var lines = sale.Lines.Select((line, index) =>
        {
            var returned = ReturnedOf(sale.Id, line.ProductId);
            return new ReturnableLine(
                line.LineNumber, line.ProductId, line.ProductName, line.Quantity, returned, line.Quantity - returned, line.UnitPrice,
                paid[index], RefundedOf(sale.Id, line.ProductId),
                ReturnMath.RefundForLine(paid[index], line.Quantity, returned, line.Quantity - returned));
        }).ToList();
        return new ReturnableSale(
            sale.Id, sale.TicketNumber, sale.TicketBarcode, sale.CreatedAt, sale.Status, sale.PaymentMode, sale.Subtotal,
            sale.DiscountAmount, sale.Total, sale.CustomerName,
            blocked is null && lines.Any(line => line.ReturnableQuantity > 0), blocked?.Code,
            blocked?.Message, refundMethod, lines.Sum(line => line.RefundedAmount), lines);
    }

    // ---------- Calcul commun à l'aperçu et à l'enregistrement ----------

    private (SaleDetail Sale, ReturnReason Reason, string? Method, decimal Refund, List<(ReturnLineRequest Request, SaleLineDetail Line, decimal Refund)> Lines)
        Compute(Guid saleId, Guid reasonId, IReadOnlyList<ReturnLineRequest>? requestedLines)
    {
        var requested = requestedLines ?? [];
        if (requested.Count is 0 or > 200)
        {
            throw new ServerRefusedException(ApiErrorCodes.InvalidRequest, "Un retour contient de 1 à 200 lignes.");
        }

        if (requested.Any(line => line.Quantity <= 0 || decimal.Round(line.Quantity, 3) != line.Quantity))
        {
            throw new ServerRefusedException(
                ApiErrorCodes.InvalidRequest, "Chaque quantité retournée doit être positive, avec au plus 3 décimales.");
        }

        if (requested.Select(line => line.ProductId).Distinct().Count() != requested.Count)
        {
            throw new ServerRefusedException(
                ApiErrorCodes.InvalidRequest, "Le même produit figure sur plusieurs lignes du retour : regroupez les quantités.");
        }

        var sale = Tickets.SingleOrDefault(ticket => ticket.Id == saleId)
            ?? throw new ServerRefusedException(ApiErrorCodes.NotFound, "Ticket introuvable.");
        var (blocked, method) = StateOf(sale);
        if (blocked is not null)
        {
            throw blocked;
        }

        var reason = ReturnReasons.SingleOrDefault(known => known.Id == reasonId);
        if (reason is not { IsActive: true })
        {
            throw new ServerRefusedException(ReturnErrorCodes.InvalidReason, "Choisissez un motif de retour dans la liste des motifs actifs.");
        }

        var paid = PaidPerLine(sale);
        var lines = requested.Select(line =>
        {
            var index = sale.Lines.ToList().FindIndex(sold => sold.ProductId == line.ProductId);
            if (index < 0)
            {
                throw new ServerRefusedException(
                    ApiErrorCodes.InvalidRequest, $"Un des produits ne figure pas sur le ticket {sale.TicketNumber}.");
            }

            var sold = sale.Lines[index];
            if (line.Quantity != decimal.Truncate(line.Quantity) && !FractionalProducts.Contains(sold.ProductId))
            {
                throw new ServerRefusedException(
                    ApiErrorCodes.FractionalQuantity,
                    $"« {sold.ProductName} » se vend à l'unité : la quantité {line.Quantity:0.###} n'est pas entière.");
            }

            var returned = ReturnedOf(sale.Id, sold.ProductId);
            var returnable = sold.Quantity - returned;
            if (line.Quantity > returnable)
            {
                throw new ServerRefusedException(
                    ReturnErrorCodes.QuantityExceeded,
                    $"« {sold.ProductName} » : {line.Quantity:0.###} demandé en retour, {returnable:0.###} encore retournable "
                    + $"({sold.Quantity:0.###} vendu, {returned:0.###} déjà retourné).");
            }

            return (line, sold, ReturnMath.RefundForLine(paid[index], sold.Quantity, returned, line.Quantity));
        }).ToList();

        var refund = lines.Sum(line => line.Item3) + RefundDrift;
        return (sale, reason, refund > 0 ? method : null, refund, lines);
    }

    public Task<ReturnPreview> PreviewAsync(PreviewReturnRequest request, CancellationToken cancellationToken)
    {
        PreviewRequests.Add(request);
        EnsurePermission(Permissions.ReturnsCreate);
        var computed = Compute(request.SaleId, request.ReasonId, request.Lines);
        var restocks = computed.Reason.StockDestination != ReturnStockDestinations.None;
        return Task.FromResult(new ReturnPreview(
            computed.Sale.Id, computed.Sale.TicketNumber, computed.Reason.Label, computed.Reason.StockDestination, computed.Method,
            computed.Refund,
            computed.Lines.Select(line => new ReturnPreviewLine(
                line.Line.ProductId, line.Line.ProductName, line.Request.Quantity, line.Refund, restocks ? line.Request.Quantity : 0m)).ToList()));
    }

    // ---------- Enregistrement ----------

    public async Task<ReturnDetail> CreateReturnAsync(CreateReturnRequest request, CancellationToken cancellationToken)
    {
        ReturnRequests.Add(request);
        if (ReturnGate is { } gate)
        {
            await gate.Task;
        }

        var failure = ReturnFailures.Count > 0 ? ReturnFailures.Dequeue() : SaleSendFailure.None;
        if (failure == SaleSendFailure.RequestLost)
        {
            throw new HttpRequestException("réseau coupé");
        }

        EnsurePermission(Permissions.ReturnsCreate);
        if (!returnsByOperation.TryGetValue(request.OperationId, out var recorded))
        {
            recorded = Record(request);
            returnsByOperation[request.OperationId] = recorded;
        }

        if (failure == SaleSendFailure.AnswerLost)
        {
            throw new TaskCanceledException("délai dépassé");
        }

        return recorded;
    }

    private ReturnDetail Record(CreateReturnRequest request)
    {
        if (CashSession is not { } session)
        {
            throw new ServerRefusedException(
                CashErrorCodes.NoOpenSession, "La caisse de ce poste n'est pas ouverte. Ouvrez la caisse avant de vendre ou d'encaisser.");
        }

        var computed = Compute(request.SaleId, request.ReasonId, request.Lines);
        if (request.ExpectedRefund is { } expected && expected != computed.Refund)
        {
            throw new ServerRefusedException(
                ReturnErrorCodes.RefundMismatch,
                $"Le remboursement a changé : {Money.ToDisplay(computed.Refund)} au lieu de {Money.ToDisplay(expected)}. Vérifiez avant de rembourser.");
        }

        var destination = computed.Reason.StockDestination;
        foreach (var line in computed.Lines)
        {
            switch (destination)
            {
                case ReturnStockDestinations.Sellable:
                    StockLevels[line.Line.ProductId] = StockLevels.GetValueOrDefault(line.Line.ProductId) + line.Request.Quantity;
                    break;
                case ReturnStockDestinations.SetAside:
                    SetAsideStock[line.Line.ProductId] = SetAsideStock.GetValueOrDefault(line.Line.ProductId) + line.Request.Quantity;
                    break;
            }
        }

        var restocks = destination != ReturnStockDestinations.None;
        var detail = new ReturnDetail(
            Guid.NewGuid(), $"D1-R{++returnSequence:000000}", computed.Sale.Id, computed.Sale.TicketNumber, session.StoreId,
            session.RegisterName, session.Id, EmployeeId, EmployeeName, DateTimeOffset.Now, computed.Reason.Id, computed.Reason.Label,
            destination, restocks ? Guid.NewGuid() : null,
            destination switch
            {
                ReturnStockDestinations.Sellable => "Pharmaly 1 — rayon",
                ReturnStockDestinations.SetAside => "Pharmaly 1 — mis à l'écart",
                _ => null,
            },
            string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(), computed.Method, computed.Refund,
            computed.Lines.Select((line, index) => new ReturnLineDetail(
                index + 1, line.Line.ProductId, line.Line.ProductName, line.Request.Quantity, line.Line.UnitPrice, line.Refund,
                restocks ? line.Request.Quantity : 0m, restocks ? 0m : line.Request.Quantity)).ToList());
        RecordedReturns.Add(detail);
        return detail;
    }

    // ---------- Historique ----------

    public List<ReturnsHistoryQuery> ReturnHistoryQueries { get; } = [];

    public Task<IReadOnlyList<ReturnSummary>> ListReturnsAsync(ReturnsHistoryQuery query, CancellationToken cancellationToken)
    {
        EnsureEmployee();
        if (!EmployeePermissions.Contains(Permissions.ReturnsCreate) && !EmployeePermissions.Contains(Permissions.SalesHistoryView))
        {
            throw new ServerRefusedException(ApiErrorCodes.PermissionDenied, "Vous n'avez pas le droit d'effectuer cette action.");
        }

        ReturnHistoryQueries.Add(query);
        var found = RecordedReturns
            .Where(done => query.From is null || DateOnly.FromDateTime(done.CreatedAt.LocalDateTime) >= query.From)
            .Where(done => query.To is null || DateOnly.FromDateTime(done.CreatedAt.LocalDateTime) <= query.To)
            .Where(done => query.SaleId is null || done.SaleId == query.SaleId)
            .Where(done => query.Ticket is null
                || done.TicketNumber.Contains(query.Ticket, StringComparison.OrdinalIgnoreCase)
                || done.ReturnNumber.Contains(query.Ticket, StringComparison.OrdinalIgnoreCase)
                || Tickets.Any(ticket => ticket.Id == done.SaleId && ticket.TicketBarcode.Contains(query.Ticket, StringComparison.OrdinalIgnoreCase)))
            .Reverse()
            .Select(done => new ReturnSummary(
                done.Id, done.ReturnNumber, done.SaleId, done.TicketNumber, done.CreatedAt, done.ReasonLabel, done.StockDestination,
                done.RefundMethod, done.RefundAmount, done.EmployeeId, done.EmployeeName))
            .ToList();
        return Task.FromResult<IReadOnlyList<ReturnSummary>>(found);
    }

    public Task<ReturnDetail?> GetReturnAsync(Guid returnId, CancellationToken cancellationToken)
    {
        EnsureEmployee();
        return Task.FromResult(RecordedReturns.SingleOrDefault(done => done.Id == returnId));
    }

    // ---------- Motifs ----------

    public Task<IReadOnlyList<ReturnReason>> ListReasonsAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        EnsureEmployee();
        return Task.FromResult<IReadOnlyList<ReturnReason>>(
            ReturnReasons.Where(reason => reason.IsActive || includeInactive).OrderBy(reason => reason.SortOrder).ToList());
    }

    private void FailReasonWriteIfAsked()
    {
        if (FailNextReasonWrites > 0)
        {
            FailNextReasonWrites--;
            throw new HttpRequestException("réseau coupé");
        }
    }

    private static (string Label, string Destination) CleanReason(string? label, string? destination)
    {
        var cleaned = (label ?? string.Empty).Trim();
        if (cleaned.Length is 0 or > 100)
        {
            throw new ServerRefusedException(ApiErrorCodes.InvalidRequest, "Le libellé du motif est obligatoire (100 caractères au plus).");
        }

        var code = (destination ?? string.Empty).Trim().ToUpperInvariant();
        return ReturnStockDestinations.IsKnown(code)
            ? (cleaned, code)
            : throw new ServerRefusedException(ApiErrorCodes.InvalidRequest, "Destination du stock inconnue : VENDABLE, ECARTE ou AUCUNE.");
    }

    public Task<ReturnReason> CreateReasonAsync(CreateReturnReasonRequest request, CancellationToken cancellationToken)
    {
        ReasonCreateRequests.Add(request);
        FailReasonWriteIfAsked();
        EnsurePermission(Permissions.UsersAdminister);
        if (reasonsByOperation.TryGetValue(request.OperationId, out var replayed))
        {
            return Task.FromResult(replayed);
        }

        var (label, destination) = CleanReason(request.Label, request.StockDestination);
        if (ReturnReasons.Any(reason => string.Equals(reason.Label, label, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ServerRefusedException(ApiErrorCodes.Conflict, $"Un motif « {label} » existe déjà.");
        }

        var created = new ReturnReason(Guid.NewGuid(), label, destination, true, ReturnReasons.Max(reason => reason.SortOrder) + 1);
        ReturnReasons.Add(created);
        reasonsByOperation[request.OperationId] = created;
        return Task.FromResult(created);
    }

    public Task<ReturnReason> UpdateReasonAsync(Guid reasonId, UpdateReturnReasonRequest request, CancellationToken cancellationToken)
    {
        ReasonUpdateRequests.Add((reasonId, request));
        FailReasonWriteIfAsked();
        EnsurePermission(Permissions.UsersAdminister);
        var (label, destination) = CleanReason(request.Label, request.StockDestination);
        var index = ReturnReasons.FindIndex(reason => reason.Id == reasonId);
        if (index < 0)
        {
            throw new ServerRefusedException(ApiErrorCodes.NotFound, "Motif de retour introuvable.");
        }

        if (ReturnReasons.Any(reason => reason.Id != reasonId && string.Equals(reason.Label, label, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ServerRefusedException(ApiErrorCodes.Conflict, $"Un motif « {label} » existe déjà.");
        }

        ReturnReasons[index] = ReturnReasons[index] with { Label = label, StockDestination = destination, IsActive = request.IsActive };
        return Task.FromResult(ReturnReasons[index]);
    }
}
