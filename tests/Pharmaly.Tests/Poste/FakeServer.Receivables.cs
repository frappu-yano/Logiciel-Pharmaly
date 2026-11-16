using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Tests.Poste;

/// <summary>
/// Impayés : mêmes règles, mêmes codes et mêmes messages que le vrai serveur, y compris
/// l'idempotence (rejouer un identifiant d'opération renvoie le règlement déjà enregistré).
/// </summary>
internal sealed partial class FakeServer : IReceivablesClient
{
    private readonly Dictionary<Guid, Guid> settlementsByOperation = [];
    private readonly Dictionary<Guid, SettlementResult> settlementsByCardOperation = [];
    private int receivableSequence;

    /// <summary>Créances connues, dans l'ordre de création.</summary>
    public List<ReceivableLine> ReceivableLines { get; } = [];

    /// <summary>Règlements enregistrés.</summary>
    public List<SettlementResult> Settlements { get; } = [];

    /// <summary>Toutes les demandes de règlement reçues, y compris celles refusées ou rejouées.</summary>
    public List<SettleReceivablesRequest> SettleRequests { get; } = [];

    public List<(Guid SettlementId, CardResultRequest Request)> SettlementCardRequests { get; } = [];

    public List<ReceivablesQuery> ReceivableQueries { get; } = [];

    /// <summary>Pannes de réseau à appliquer aux prochains envois de règlement, dans l'ordre.</summary>
    public Queue<SaleSendFailure> SettlementFailures { get; } = new();

    /// <summary>Pannes de réseau (demande perdue) pour les prochaines réponses du TPE à un règlement.</summary>
    public int FailNextSettlementCardAnswers { get; set; }

    /// <summary>Quand il est défini, chaque envoi de règlement attend qu'il soit terminé.</summary>
    public TaskCompletionSource? SettlementGate { get; set; }

    /// <summary>Ajoute une vente impayée (créance ouverte) du client donné.</summary>
    public ReceivableLine AddReceivable(CustomerSummary customer, decimal amount, DateTimeOffset? createdAt = null)
    {
        var number = ++receivableSequence;
        var line = new ReceivableLine(
            Guid.NewGuid(), Guid.NewGuid(), $"D1-{900000 + number:000000}", $"TK{9000000000 + number:0000000000}",
            createdAt ?? DateTimeOffset.Now.AddMinutes(-number), customer.Id, customer.Name, amount, ReceivableStatuses.Open,
            EmployeeId, EmployeeName, null, null, null, null, null);
        ReceivableLines.Add(line);
        return line;
    }

    /// <summary>Un autre poste règle ce ticket (ou lance un TPE dessus) pendant que l'écran reste affiché.</summary>
    public void SettleElsewhere(Guid receivableId, string method = PaymentMethods.Cash) =>
        Update(receivableId, line => line with
        {
            Status = ReceivableStatuses.Settled, SettledAt = DateTimeOffset.Now, SettlementMethod = method,
            SettledByName = "Autre caissier", SettlementId = Guid.NewGuid(),
        });

    public ReceivableLine Receivable(Guid id) => ReceivableLines.Single(line => line.Id == id);

    private void Update(Guid id, Func<ReceivableLine, ReceivableLine> change)
    {
        var index = ReceivableLines.FindIndex(line => line.Id == id);
        ReceivableLines[index] = change(ReceivableLines[index]);
    }

    private ReceivableIndicators Indicators()
    {
        var open = ReceivableLines.Where(line => line.Status == ReceivableStatuses.Open).ToList();
        var settled = ReceivableLines.Where(line => line.Status == ReceivableStatuses.Settled).ToList();
        return new ReceivableIndicators(
            open.Sum(line => line.Amount), open.Count, open.Sum(line => line.Amount) + settled.Sum(line => line.Amount),
            open.Count + settled.Count, settled.Sum(line => line.Amount), settled.Count);
    }

    public Task<ReceivablesTable> GetReceivablesAsync(ReceivablesQuery query, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.ReceivablesView);
        ReceivableQueries.Add(query);
        var lines = ReceivableLines
            .Where(line => query.Status is null || line.Status == query.Status)
            .Where(line => query.CustomerId is null || line.CustomerId == query.CustomerId)
            .Where(line => query.From is null || DateOnly.FromDateTime(line.CreatedAt.LocalDateTime) >= query.From)
            .Where(line => query.To is null || DateOnly.FromDateTime(line.CreatedAt.LocalDateTime) <= query.To)
            .Where(line => query.Ticket is null
                || line.TicketNumber.Contains(query.Ticket, StringComparison.OrdinalIgnoreCase)
                || line.TicketBarcode.Contains(query.Ticket, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(line => line.CreatedAt)
            .ToList();
        return Task.FromResult(new ReceivablesTable(Indicators(), lines));
    }

    public Task<ReceivableLine?> FindByTicketAsync(string numberOrBarcode, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.ReceivablesView);
        var code = numberOrBarcode.Trim().ToUpperInvariant();
        return Task.FromResult(ReceivableLines.SingleOrDefault(line => line.TicketNumber == code || line.TicketBarcode == code));
    }

    public Task<SettlementResult?> GetSettlementAsync(Guid settlementId, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.ReceivablesView);
        return Task.FromResult(Settlements.SingleOrDefault(settlement => settlement.Id == settlementId) is { } found ? Fresh(found) : null);
    }

    private SettlementResult Fresh(SettlementResult settlement) => settlement with
    {
        Receivables = settlement.Receivables.Select(line => Receivable(line.Id)).ToList(),
    };

    public async Task<SettlementResult> SettleAsync(SettleReceivablesRequest request, CancellationToken cancellationToken)
    {
        SettleRequests.Add(request);
        if (SettlementGate is { } gate)
        {
            await gate.Task;
        }

        var failure = SettlementFailures.Count > 0 ? SettlementFailures.Dequeue() : SaleSendFailure.None;
        if (failure == SaleSendFailure.RequestLost)
        {
            throw new HttpRequestException("réseau coupé");
        }

        EnsurePermission(Permissions.ReceivablesSettle);
        if (!settlementsByOperation.TryGetValue(request.OperationId, out var settlementId))
        {
            settlementId = RecordSettlement(request).Id;
            settlementsByOperation[request.OperationId] = settlementId;
        }

        if (failure == SaleSendFailure.AnswerLost)
        {
            throw new TaskCanceledException("délai dépassé");
        }

        return Fresh(Settlements.Single(settlement => settlement.Id == settlementId));
    }

    private SettlementResult RecordSettlement(SettleReceivablesRequest request)
    {
        var ids = request.ReceivableIds.ToList();
        if (ids.Count is 0 or > 100)
        {
            throw new ServerRefusedException(ApiErrorCodes.InvalidRequest, "Un règlement porte sur 1 à 100 tickets impayés.");
        }

        if (!PaymentMethods.IsKnown(request.Method))
        {
            throw new ServerRefusedException(ApiErrorCodes.InvalidRequest, "Moyen de règlement inconnu : ESPECES ou TPE.");
        }

        if (CashSession is not { } session)
        {
            throw new ServerRefusedException(
                CashErrorCodes.NoOpenSession, "La caisse de ce poste n'est pas ouverte. Ouvrez la caisse avant de vendre ou d'encaisser.");
        }

        var lines = ids.Select(id => ReceivableLines.SingleOrDefault(line => line.Id == id)
            ?? throw new ServerRefusedException(ApiErrorCodes.NotFound, "Ticket impayé introuvable.")).ToList();
        if (lines.Select(line => line.CustomerId).Distinct().Count() > 1)
        {
            throw new ServerRefusedException(
                ReceivableErrorCodes.DifferentCustomers,
                "Ces tickets appartiennent à des clients différents : un règlement groupé concerne un seul client. Rien n'a été encaissé.");
        }

        if (lines.Find(line => line.Status != ReceivableStatuses.Open) is { } closed)
        {
            throw new ServerRefusedException(
                ReceivableErrorCodes.NotOpen,
                $"Le ticket {closed.TicketNumber} est déjà {(closed.Status == ReceivableStatuses.Settled ? "soldé" : "annulé")}. Rien n'a été encaissé.");
        }

        if (lines.Find(line => line.PendingSettlementId is not null) is { } pending)
        {
            throw new ServerRefusedException(
                ReceivableErrorCodes.SettlementPending,
                $"Un règlement par carte est déjà en attente sur le ticket {pending.TicketNumber}. Répondez-y (accepté ou refusé) avant de recommencer.");
        }

        var due = lines.Sum(line => line.Amount);
        if (request.Amount != due)
        {
            throw new ServerRefusedException(
                ReceivableErrorCodes.AmountMismatch,
                $"Le montant à encaisser est exactement {Money.ToDisplay(due)} ; {Money.ToDisplay(request.Amount)} a été saisi. Un ticket impayé se règle en entier.");
        }

        var isCash = request.Method == PaymentMethods.Cash;
        if (isCash && request.AmountReceived is null)
        {
            throw new ServerRefusedException(ApiErrorCodes.InvalidRequest, "Indiquez le montant reçu en espèces.");
        }

        if (isCash && request.AmountReceived < due)
        {
            throw new ServerRefusedException(
                SaleErrorCodes.InsufficientAmountReceived,
                $"Montant reçu insuffisant : {Money.ToDisplay(request.AmountReceived ?? 0)} pour {Money.ToDisplay(due)} dus.");
        }

        var now = DateTimeOffset.Now;
        var settlement = new SettlementResult(
            Guid.NewGuid(), lines[0].CustomerId, lines[0].CustomerName, request.Method,
            isCash ? PaymentStatuses.Confirmed : PaymentStatuses.Pending, due, isCash ? request.AmountReceived : null,
            isCash ? request.AmountReceived - due : null, session.Id, EmployeeId, EmployeeName, now, isCash ? now : null, lines);
        foreach (var line in lines)
        {
            Update(line.Id, current => isCash
                ? current with
                {
                    Status = ReceivableStatuses.Settled, SettledAt = now, SettlementMethod = request.Method, SettledByName = EmployeeName,
                    SettlementId = settlement.Id,
                }
                : current with { PendingSettlementId = settlement.Id });
        }

        Settlements.Add(settlement);
        return settlement;
    }

    // Même signature que la réponse du TPE d'une vente : implémentation explicite.
    Task<SettlementResult> IReceivablesClient.ResolveCardAsync(Guid settlementId, CardResultRequest request, CancellationToken cancellationToken)
    {
        SettlementCardRequests.Add((settlementId, request));
        if (FailNextSettlementCardAnswers > 0)
        {
            FailNextSettlementCardAnswers--;
            throw new HttpRequestException("réseau coupé");
        }

        EnsurePermission(Permissions.ReceivablesSettle);
        if (settlementsByCardOperation.TryGetValue(request.OperationId, out var replayed))
        {
            return Task.FromResult(Fresh(replayed));
        }

        var index = Settlements.FindIndex(settlement => settlement.Id == settlementId);
        if (index < 0)
        {
            throw new ServerRefusedException(ApiErrorCodes.NotFound, "Règlement introuvable.");
        }

        var settlement = Settlements[index];
        if (settlement.Status != PaymentStatuses.Pending)
        {
            throw new ServerRefusedException(ReceivableErrorCodes.SettlementNotPending, "Ce règlement n'attend pas de réponse du TPE.");
        }

        var now = DateTimeOffset.Now;
        foreach (var line in settlement.Receivables)
        {
            Update(line.Id, current => request.Accepted
                ? current with
                {
                    Status = ReceivableStatuses.Settled, SettledAt = now, SettlementMethod = settlement.Method, SettledByName = EmployeeName,
                    SettlementId = settlement.Id, PendingSettlementId = null,
                }
                : current with { PendingSettlementId = null });
        }

        settlement = settlement with
        {
            Status = request.Accepted ? PaymentStatuses.Confirmed : PaymentStatuses.Failed,
            ConfirmedAt = request.Accepted ? now : null,
        };
        Settlements[index] = settlement;
        settlementsByCardOperation[request.OperationId] = settlement;
        return Task.FromResult(Fresh(settlement));
    }
}
