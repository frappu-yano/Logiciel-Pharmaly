using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Tests.Poste;

/// <summary>
/// Journée du magasin et clôture quotidienne : mêmes règles, mêmes codes et mêmes messages que
/// le vrai serveur (blocages, double clôture, clôture forcée réservée aux deux droits, motif
/// obligatoire, idempotence). Les totaux sont calculés à partir de ce que les tests enregistrent :
/// ventes de la journée, règlements d'impayés, mouvements d'espèces, caisses.
/// </summary>
internal sealed partial class FakeServer : IClosuresClient
{
    public const string ForcedWarning = "Clôture forcée avec opérations inconnues";

    private readonly Dictionary<Guid, DailyClosureDetail> closuresByOperation = [];
    private readonly HashSet<Guid> frozenSettlements = [];
    private readonly HashSet<Guid> frozenMovements = [];

    /// <summary>Date du jour au magasin.</summary>
    public DateOnly StoreToday { get; set; } = DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Ventes validées de la journée en cours : mode (ESPECES, TPE, IMPAYE), total remise déduite, remise.</summary>
    public List<(string Mode, decimal Total, decimal Discount)> DaySales { get; } = [];

    /// <summary>Paiements de vente par carte en attente de réponse sur la caisse du poste.</summary>
    public List<(string Ticket, decimal Amount)> PendingSaleCards { get; } = [];

    /// <summary>Caisses de la journée déjà fermées (celle du poste, une fois fermée, et d'autres).</summary>
    public List<CashSessionReport> ClosedDaySessions { get; } = [];

    /// <summary>Caisses d'autres postes encore ouvertes.</summary>
    public List<CashSessionReport> OtherOpenSessions { get; } = [];

    /// <summary>Opérations de secours non synchronisées.</summary>
    public int OfflineOperations { get; set; }

    public List<DailyClosureDetail> DailyClosures { get; } = [];

    /// <summary>Toutes les demandes de clôture reçues, y compris celles refusées ou rejouées.</summary>
    public List<CloseDayRequest> CloseDayRequests { get; } = [];

    /// <summary>Pannes de réseau à appliquer aux prochaines clôtures, dans l'ordre.</summary>
    public Queue<SaleSendFailure> CloseDayFailures { get; } = new();

    public int CurrentDayReads { get; private set; }

    /// <summary>Une vente validée aujourd'hui sur la caisse du poste ; impayée, elle crée la créance du client.</summary>
    public void AddDaySale(string mode, decimal total, decimal discount = 0m, CustomerSummary? customer = null)
    {
        DaySales.Add((mode, total, discount));
        switch (mode)
        {
            case SalePaymentModes.Cash:
                SessionSales = (SessionSales.Cash + total, SessionSales.Card);
                break;
            case SalePaymentModes.Card:
                SessionSales = (SessionSales.Cash, SessionSales.Card + total);
                break;
            case SalePaymentModes.Unpaid:
                AddReceivable(customer ?? Benali, total);
                break;
        }
    }

    /// <summary>La caisse d'un autre poste, ouverte (sans comptage) ou fermée avec ce comptage.</summary>
    public void AddOtherRegister(string name, decimal expected, decimal? counted)
    {
        var open = counted is null;
        var report = new CashSessionReport(
            Guid.NewGuid(), Guid.NewGuid(), name, open ? "OUVERTE" : "FERMEE", "Karim Autre", DateTimeOffset.Now.AddHours(-8),
            open ? null : "Karim Autre", open ? null : DateTimeOffset.Now.AddHours(-1),
            expected, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, expected, counted, counted - expected, 0);
        (open ? OtherOpenSessions : ClosedDaySessions).Add(report);
    }

    public Task<CurrentDayStatus> GetCurrentDayAsync(CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.RegisterReportsView);
        CurrentDayReads++;
        var blockers = DayBlockers();
        if (OfflineOperations > 0)
        {
            blockers.Add(OfflineBlocker());
        }

        var last = LastClosedDate;
        return Task.FromResult(new CurrentDayStatus(
            Didouche.Id, Didouche.Name, StoreToday, last, blockers.Count == 0 && (last is null || last < StoreToday),
            blockers, OfflineOperations, DayReport(false)));
    }

    public DateOnly? LastClosedDate => DailyClosures.Count == 0 ? null : DailyClosures.Max(closure => closure.BusinessDate);

    public Task<DailyClosureDetail> CloseDayAsync(CloseDayRequest request, CancellationToken cancellationToken)
    {
        CloseDayRequests.Add(request);
        var failure = CloseDayFailures.Count > 0 ? CloseDayFailures.Dequeue() : SaleSendFailure.None;
        if (failure == SaleSendFailure.RequestLost)
        {
            throw new HttpRequestException("réseau coupé");
        }

        EnsurePermission(Permissions.RegisterClose);
        if (request.ForceWithUnknownOperations)
        {
            EnsurePermission(Permissions.UsersAdminister);
        }

        if (!closuresByOperation.TryGetValue(request.OperationId, out var closure))
        {
            closure = RecordClosure(request);
            closuresByOperation[request.OperationId] = closure;
        }

        if (failure == SaleSendFailure.AnswerLost)
        {
            throw new TaskCanceledException("délai dépassé");
        }

        return Task.FromResult(closure);
    }

    private DailyClosureDetail RecordClosure(CloseDayRequest request)
    {
        var date = request.BusinessDate ?? StoreToday;
        if (date > StoreToday)
        {
            throw new ServerRefusedException(
                ClosureErrorCodes.InvalidDate, $"Le {Display(date)} n'est pas encore arrivé : cette journée ne peut pas être clôturée.");
        }

        string? reason = null;
        if (request.ForceWithUnknownOperations)
        {
            reason = (request.ForceReason ?? string.Empty).Trim();
            if (reason.Length is 0 or > 500)
            {
                throw new ServerRefusedException(
                    ApiErrorCodes.InvalidRequest, "Une clôture forcée exige un motif (500 caractères au plus).");
            }
        }

        if (LastClosedDate is { } last && last >= date)
        {
            throw new ServerRefusedException(
                ClosureErrorCodes.DayAlreadyClosed,
                last == date
                    ? $"La journée du {Display(date)} est déjà clôturée pour ce magasin. Les opérations saisies depuis appartiennent à la journée suivante."
                    : $"La journée du {Display(date)} ne peut plus être clôturée : ce magasin est déjà clôturé jusqu'au {Display(last)}.");
        }

        var blockers = DayBlockers();
        if (blockers.Count > 0)
        {
            throw new ServerRefusedException(
                ClosureErrorCodes.DayBlocked,
                "La journée ne peut pas être clôturée. " + string.Join(" ", blockers.Select(blocker => blocker.Message)));
        }

        if (OfflineOperations > 0 && !request.ForceWithUnknownOperations)
        {
            throw new ServerRefusedException(ClosureErrorCodes.OfflineOperationsPending, OfflineBlocker().Message);
        }

        if (OfflineOperations == 0 && request.ForceWithUnknownOperations)
        {
            throw new ServerRefusedException(
                ApiErrorCodes.InvalidRequest, "Aucune opération de secours n'est en attente : la clôture normale suffit, sans forçage.");
        }

        var forced = OfflineOperations > 0;
        var closure = new DailyClosureDetail(
            Guid.NewGuid(), Didouche.Id, Didouche.Name, date, DateTimeOffset.Now, EmployeeId, EmployeeName, forced,
            forced ? reason : null, DayReport(forced));
        DailyClosures.Add(closure);

        // La journée est figée : ses opérations n'appartiennent plus à la journée en cours.
        DaySales.Clear();
        ClosedDaySessions.Clear();
        frozenSettlements.UnionWith(Settlements.Select(settlement => settlement.Id));
        frozenMovements.UnionWith(CashMovements.Select(movement => movement.Id));
        return closure;
    }

    public Task<IReadOnlyList<DailyClosureSummary>> GetClosuresAsync(
        DateOnly? from, DateOnly? to, int? limit, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.RegisterReportsView);
        IReadOnlyList<DailyClosureSummary> list = DailyClosures
            .Where(closure => (from is null || closure.BusinessDate >= from) && (to is null || closure.BusinessDate <= to))
            .OrderByDescending(closure => closure.BusinessDate)
            .Take(limit ?? 60)
            .Select(closure => new DailyClosureSummary(
                closure.Id, closure.StoreId, closure.BusinessDate, closure.ClosedAt, closure.ClosedById, closure.ClosedByName, closure.Forced,
                closure.Report.Revenue.NetRevenue, closure.Report.Treasury.NetCash, closure.Report.Treasury.NetCard,
                closure.Report.Treasury.CashDifference))
            .ToList();
        return Task.FromResult(list);
    }

    public Task<DailyClosureDetail?> GetClosureAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.RegisterReportsView);
        return Task.FromResult(DailyClosures.SingleOrDefault(closure => closure.Id == id));
    }

    /// <summary>Une clôture déjà faite pour ce jour-là (historique).</summary>
    public DailyClosureDetail AddPastClosure(DateOnly date, decimal netRevenue, decimal cashDifference = 0m, bool forced = false)
    {
        var report = new ClosureReport(
            new ClosureRevenue(3, netRevenue, 0m, netRevenue, 0m, 0m, netRevenue, 0, 0m, 0, 0m, 0, 0m, netRevenue),
            new ClosureTreasury(
                netRevenue, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, netRevenue, 0m, 5000m, 5000m + netRevenue,
                5000m + netRevenue + cashDifference, cashDifference),
            new ClosureReceivables(0, 0m, 0, 0m, 0, 0m),
            [],
            forced,
            forced ? 2 : 0,
            forced ? ForcedWarning : null);
        var closure = new DailyClosureDetail(
            Guid.NewGuid(), Didouche.Id, Didouche.Name, date, new DateTimeOffset(date.ToDateTime(new TimeOnly(20, 0))), EmployeeId, EmployeeName,
            forced, forced ? "Poste de secours en panne" : null, report);
        DailyClosures.Add(closure);
        return closure;
    }

    private static string Display(DateOnly date) => date.ToString("dd'/'MM'/'yyyy", System.Globalization.CultureInfo.InvariantCulture);

    private static ClosureBlocker OfflineBlocker(int count) =>
        new(
            ClosureBlockerKinds.OfflineOperations,
            $"{count} opération(s) de secours en attente de synchronisation : la journée ne peut pas être présentée comme consolidée.");

    private ClosureBlocker OfflineBlocker() => OfflineBlocker(OfflineOperations);

    /// <summary>Caisses ouvertes, puis paiements et règlements par carte en attente (comme le serveur).</summary>
    private List<ClosureBlocker> DayBlockers()
    {
        var blockers = new List<ClosureBlocker>();
        var open = OtherOpenSessions.Select(session => (session.Id, session.RegisterName)).ToList();
        if (CashSession is { } own)
        {
            open.Add((own.Id, own.RegisterName));
        }

        blockers.AddRange(open.OrderBy(session => session.RegisterName, StringComparer.Ordinal).Select(session => new ClosureBlocker(
            ClosureBlockerKinds.SessionOpen,
            $"La caisse « {session.RegisterName} » est encore ouverte : comptez ses espèces et fermez-la.",
            session.Id, session.RegisterName)));
        blockers.AddRange(CardBlockers());
        return blockers;
    }

    private List<ClosureBlocker> CardBlockers()
    {
        var blockers = PendingSaleCards.Select(card => new ClosureBlocker(
            ClosureBlockerKinds.SaleCardPending,
            $"Ticket {card.Ticket} : paiement par carte de {Money.ToDisplay(card.Amount)} en attente sur « Caisse 1 ». "
            + "Indiquez « accepté » ou « refusé ».",
            Guid.NewGuid(), "Caisse 1", card.Amount)).ToList();
        blockers.AddRange(Settlements.Where(settlement => settlement.Status == PaymentStatuses.Pending).Select(settlement => new ClosureBlocker(
            ClosureBlockerKinds.SettlementCardPending,
            $"Règlement d'impayés de {settlement.CustomerName} : paiement par carte de {Money.ToDisplay(settlement.Amount)} en attente "
            + "sur « Caisse 1 ». Indiquez « accepté » ou « refusé ».",
            settlement.Id, "Caisse 1", settlement.Amount)));
        return blockers;
    }

    /// <summary>Totaux de la caisse du poste, comme <c>ClosureReports.ReadSessionAsync</c>.</summary>
    private CashSessionReport OwnSessionReport(CashSessionInfo session, decimal? counted, DateTimeOffset? closedAt)
    {
        var confirmed = Settlements.Where(settlement => settlement.CashSessionId == session.Id && settlement.Status == PaymentStatuses.Confirmed)
            .ToList();
        var settlementCash = confirmed.Where(settlement => settlement.Method == PaymentMethods.Cash).Sum(settlement => settlement.Amount);
        var settlementCard = confirmed.Where(settlement => settlement.Method == PaymentMethods.Card).Sum(settlement => settlement.Amount);
        var movements = CashMovements.Where(movement => movement.CashSessionId == session.Id).ToList();
        var cashIn = movements.Where(movement => movement.Kind == CashMovementKinds.In).Sum(movement => movement.Amount);
        var cashOut = movements.Where(movement => movement.Kind == CashMovementKinds.Out).Sum(movement => movement.Amount);
        var expected = session.OpeningFloat + SessionSales.Cash + settlementCash + cashIn - cashOut;
        var pending = PendingSaleCards.Count
            + Settlements.Count(settlement => settlement.CashSessionId == session.Id && settlement.Status == PaymentStatuses.Pending);
        return new CashSessionReport(
            session.Id, session.RegisterAccountId, session.RegisterName, counted is null ? "OUVERTE" : "FERMEE",
            session.OpenedByName, session.OpenedAt, counted is null ? null : EmployeeName, closedAt,
            session.OpeningFloat, SessionSales.Cash, SessionSales.Card, settlementCash, settlementCard, cashIn, cashOut,
            0m, 0m, 0m, 0m, expected, counted, counted - expected, pending);
    }

    private ClosureReport DayReport(bool forced)
    {
        var sessions = ClosedDaySessions.Concat(OtherOpenSessions).ToList();
        if (CashSession is { } own)
        {
            sessions.Add(OwnSessionReport(own, null, null));
        }

        sessions = sessions.OrderBy(session => session.RegisterName, StringComparer.Ordinal).ToList();
        var cash = DaySales.Where(sale => sale.Mode == SalePaymentModes.Cash).Sum(sale => sale.Total);
        var card = DaySales.Where(sale => sale.Mode == SalePaymentModes.Card).Sum(sale => sale.Total);
        var unpaid = DaySales.Where(sale => sale.Mode == SalePaymentModes.Unpaid).ToList();
        var salesTotal = DaySales.Sum(sale => sale.Total);

        // Chaque créance réglée ce jour : ancienne si la vente d'origine est d'un autre jour.
        var settled = Settlements
            .Where(settlement => settlement.Status == PaymentStatuses.Confirmed && !frozenSettlements.Contains(settlement.Id))
            .SelectMany(settlement => settlement.Receivables.Select(line => (settlement.Method, line.Amount,
                SameDay: DateOnly.FromDateTime(line.CreatedAt.LocalDateTime) == StoreToday)))
            .ToList();
        decimal Settled(string method, bool sameDay) =>
            settled.Where(item => item.Method == method && item.SameDay == sameDay).Sum(item => item.Amount);

        var movements = CashMovements.Where(movement => !frozenMovements.Contains(movement.Id)).ToList();
        var cashIn = movements.Where(movement => movement.Kind == CashMovementKinds.In).Sum(movement => movement.Amount);
        var cashOut = movements.Where(movement => movement.Kind == CashMovementKinds.Out).Sum(movement => movement.Amount);
        var netCash = cash + Settled(PaymentMethods.Cash, false) + Settled(PaymentMethods.Cash, true) + cashIn - cashOut;
        var netCard = card + Settled(PaymentMethods.Card, false) + Settled(PaymentMethods.Card, true);
        var open = ReceivableLines.Where(line => line.Status == ReceivableStatuses.Open).ToList();
        return new ClosureReport(
            new ClosureRevenue(
                DaySales.Count, salesTotal + DaySales.Sum(sale => sale.Discount), DaySales.Sum(sale => sale.Discount), cash, card,
                unpaid.Sum(sale => sale.Total), salesTotal, 0, 0m, 0, 0m, 0, 0m, salesTotal),
            new ClosureTreasury(
                cash, card, Settled(PaymentMethods.Cash, false), Settled(PaymentMethods.Card, false),
                Settled(PaymentMethods.Cash, true), Settled(PaymentMethods.Card, true), 0m, 0m, 0m, 0m, cashIn, cashOut, netCash, netCard,
                sessions.Sum(session => session.OpeningFloat), sessions.Sum(session => session.ExpectedCash),
                sessions.Sum(session => session.CountedCash ?? 0m), sessions.Sum(session => session.CashDifference ?? 0m)),
            new ClosureReceivables(
                unpaid.Count, unpaid.Sum(sale => sale.Total), settled.Count, settled.Sum(item => item.Amount),
                open.Count, open.Sum(line => line.Amount)),
            sessions,
            forced,
            forced ? OfflineOperations : 0,
            forced ? ForcedWarning : null);
    }
}
