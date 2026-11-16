using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Tests.Poste;

/// <summary>Session de caisse du poste : fermée au départ, comme un poste en début de journée.</summary>
internal sealed partial class FakeServer : ICashSessionClient
{
    /// <summary>Session ouverte du poste ; <c>null</c> quand la caisse n'est pas ouverte.</summary>
    public CashSessionInfo? CashSession { get; set; }

    public List<OpenCashSessionRequest> OpenSessionRequests { get; } = [];

    /// <summary>Pannes de réseau (demande perdue) pour les prochaines ouvertures de caisse.</summary>
    public int FailNextSessionOpenings { get; set; }

    /// <summary>La caisse est déjà ouverte, avec ce fond de caisse.</summary>
    public FakeServer WithOpenCashSession(decimal openingFloat = 5000m)
    {
        CashSession = new CashSessionInfo(
            Guid.NewGuid(), Guid.NewGuid(), "Caisse 1", Didouche.Id, "OUVERTE", openingFloat, EmployeeId, EmployeeName, DateTimeOffset.Now);
        return this;
    }

    public Task<CashSessionInfo?> GetCurrentSessionAsync(CancellationToken cancellationToken)
    {
        EnsureEmployee();
        return Task.FromResult(CashSession);
    }

    public Task<CashSessionInfo> OpenSessionAsync(OpenCashSessionRequest request, CancellationToken cancellationToken)
    {
        OpenSessionRequests.Add(request);
        if (FailNextSessionOpenings > 0)
        {
            FailNextSessionOpenings--;
            throw new HttpRequestException("réseau coupé");
        }

        EnsurePermission(Permissions.RegisterOpen);
        if (CashSession is not null)
        {
            throw new ServerRefusedException(CashErrorCodes.SessionAlreadyOpen, "La caisse de ce poste est déjà ouverte.");
        }

        WithOpenCashSession(request.OpeningFloat);
        return Task.FromResult(CashSession!);
    }

    /// <summary>Mouvements d'espèces enregistrés, une fois chacun, même si la demande est rejouée.</summary>
    public List<CashMovementResult> CashMovements { get; } = [];

    /// <summary>Toutes les demandes de mouvement reçues, y compris celles refusées ou rejouées.</summary>
    public List<CashMovementRequest> MovementRequests { get; } = [];

    /// <summary>Pannes de réseau à appliquer aux prochains mouvements d'espèces, dans l'ordre.</summary>
    public Queue<SaleSendFailure> MovementFailures { get; } = new();

    /// <summary>Ventes encaissées dans la session, ajoutées au résumé (espèces, carte).</summary>
    public (decimal Cash, decimal Card) SessionSales { get; set; }

    public int SummaryReads { get; private set; }

    public Task<CashSessionSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.RegisterReportsView);
        SummaryReads++;
        if (CashSession is not { } session)
        {
            throw new ServerRefusedException(CashErrorCodes.NoOpenSession, "La caisse de ce poste n'est pas ouverte.");
        }

        var confirmed = Settlements.Where(settlement => settlement.CashSessionId == session.Id && settlement.Status == PaymentStatuses.Confirmed)
            .ToList();
        var pending = Settlements.Where(settlement => settlement.CashSessionId == session.Id && settlement.Status == PaymentStatuses.Pending)
            .ToList();
        var settlementCash = confirmed.Where(settlement => settlement.Method == PaymentMethods.Cash).Sum(settlement => settlement.Amount);
        var settlementCard = confirmed.Where(settlement => settlement.Method == PaymentMethods.Card).Sum(settlement => settlement.Amount);
        var cashIn = CashMovements.Where(movement => movement.Kind == CashMovementKinds.In).Sum(movement => movement.Amount);
        var cashOut = CashMovements.Where(movement => movement.Kind == CashMovementKinds.Out).Sum(movement => movement.Amount);
        return Task.FromResult(new CashSessionSummary(
            session, SessionSales.Cash, SessionSales.Card, settlementCash, settlementCard, cashIn, cashOut, 0m, 0m,
            session.OpeningFloat + SessionSales.Cash + settlementCash + cashIn - cashOut, pending.Count, pending.Sum(settlement => settlement.Amount)));
    }

    public Task<CashMovementResult> RecordMovementAsync(CashMovementRequest request, CancellationToken cancellationToken)
    {
        MovementRequests.Add(request);
        var failure = MovementFailures.Count > 0 ? MovementFailures.Dequeue() : SaleSendFailure.None;
        if (failure == SaleSendFailure.RequestLost)
        {
            throw new HttpRequestException("réseau coupé");
        }

        EnsurePermission(Permissions.PaymentsCollect);
        var recorded = CashMovements.SingleOrDefault(movement => movement.Id == request.OperationId);
        if (recorded is null)
        {
            if (string.IsNullOrWhiteSpace(request.Reason))
            {
                throw new ServerRefusedException(
                    ApiErrorCodes.InvalidRequest, "Le motif du mouvement d'espèces est obligatoire (500 caractères au plus).");
            }

            if (CashSession is not { } session)
            {
                throw new ServerRefusedException(
                    CashErrorCodes.NoOpenSession, "La caisse de ce poste n'est pas ouverte. Ouvrez la caisse avant de vendre ou d'encaisser.");
            }

            // L'identifiant d'opération sert d'identifiant au mouvement : un rejeu le retrouve.
            recorded = new CashMovementResult(
                request.OperationId, session.Id, request.Kind, request.Amount, request.Reason.Trim(), EmployeeId, DateTimeOffset.Now);
            CashMovements.Add(recorded);
        }

        if (failure == SaleSendFailure.AnswerLost)
        {
            throw new TaskCanceledException("délai dépassé");
        }

        return Task.FromResult(recorded);
    }
}
