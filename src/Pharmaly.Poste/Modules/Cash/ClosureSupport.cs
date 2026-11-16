using System.Globalization;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Modules.Sales;

namespace Pharmaly.Poste.Modules.Cash;

/// <summary>
/// Point d'accroche de l'impression du rapport de clôture figé. L'impression n'existe pas encore.
/// Une implémentation ne doit jamais lever d'exception : la journée est déjà clôturée.
/// </summary>
public interface IClosureReportPrinter
{
    void Print(DailyClosureDetail closure);
}

/// <summary>Aucune imprimante : rien n'est imprimé, rien n'est affiché.</summary>
public sealed class NoClosureReportPrinter : IClosureReportPrinter
{
    public void Print(DailyClosureDetail closure)
    {
    }
}

/// <summary>Une ligne d'un bloc de rapport : libellé, montant, précision éventuelle.</summary>
/// <param name="IsTotal">Ligne de résultat, en gras.</param>
/// <param name="IsAlert">Montant à remarquer (écart de caisse non nul).</param>
public sealed record ReportLine(string Label, string Amount, string Note = "", bool IsTotal = false, bool IsAlert = false);

/// <summary>Mots et montants de la clôture, tels que l'employé les lit.</summary>
public static class ClosureFormat
{
    public static string Date(DateOnly date) => date.ToString("dd'/'MM'/'yyyy", CultureInfo.InvariantCulture);

    public static string Time(DateTimeOffset moment) => moment.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Montant retiré : « − 500,00 DA », ou « 0,00 DA » quand il n'y a rien.</summary>
    public static string Deducted(decimal amount) => amount == 0 ? Money.ToDisplay(0) : "− " + Money.ToDisplay(amount);

    /// <summary>Écart compté − attendu : « 0,00 DA », « − 50,00 DA (manque) » ou « + 20,00 DA (excédent) ».</summary>
    public static string Difference(decimal difference) => difference switch
    {
        0 => Money.ToDisplay(0),
        < 0 => $"− {Money.ToDisplay(-difference)} (manque)",
        _ => $"+ {Money.ToDisplay(difference)} (excédent)",
    };

    public static string Count(int count, string singular, string plural) => count == 1 ? $"1 {singular}" : $"{count} {plural}";

    public static string BlockerTitle(string kind) => kind switch
    {
        ClosureBlockerKinds.SessionOpen => "Caisse ouverte",
        ClosureBlockerKinds.SaleCardPending => "Paiement TPE en attente",
        ClosureBlockerKinds.SettlementCardPending => "Règlement TPE en attente",
        ClosureBlockerKinds.OfflineOperations => "Opérations hors ligne non synchronisées",
        _ => "Blocage",
    };

    /// <summary>Ce que l'employé doit faire pour lever le blocage.</summary>
    public static string BlockerHint(string kind) => kind switch
    {
        ClosureBlockerKinds.SessionOpen =>
            "Sur le poste de cette caisse : Caisse, onglet « Session de caisse », F10 pour compter les espèces et fermer.",
        ClosureBlockerKinds.SaleCardPending =>
            "Vente : historique des ventes, ouvrez le ticket et indiquez la réponse du terminal (accepté ou refusé).",
        ClosureBlockerKinds.SettlementCardPending =>
            "Caisse, onglet « Impayés » : Ctrl+T, puis indiquez la réponse du terminal (accepté ou refusé).",
        ClosureBlockerKinds.OfflineOperations =>
            "Attendez que le poste de secours se reconnecte et synchronise ses opérations.",
        _ => string.Empty,
    };
}

/// <summary>Un blocage de fermeture ou de clôture, avec ce qu'il faut faire pour le lever.</summary>
public sealed class BlockerRowViewModel(ClosureBlocker blocker)
{
    public ClosureBlocker Blocker { get; } = blocker;

    public string Title => ClosureFormat.BlockerTitle(Blocker.Kind)
        + (Blocker.RegisterName is { Length: > 0 } register ? $" — {register}" : string.Empty);

    public string Message => Blocker.Message;

    public string Hint => ClosureFormat.BlockerHint(Blocker.Kind);

    public bool HasHint => Hint.Length > 0;
}

/// <summary>Une caisse de la journée : qui l'a ouverte et fermée, espèces attendues, comptées, écart.</summary>
public sealed class RegisterRowViewModel(CashSessionReport session)
{
    /// <summary>État d'une caisse encore ouverte, tel que le serveur l'écrit.</summary>
    public const string OpenStatus = "OUVERTE";

    public CashSessionReport Session { get; } = session;

    public string RegisterName => Session.RegisterName;

    public bool IsOpen => Session.Status == OpenStatus;

    public string StatusText => IsOpen ? "OUVERTE" : "Fermée";

    public string OpenedText => $"Ouverte par {Session.OpenedByName} à {ClosureFormat.Time(Session.OpenedAt)}";

    public string ClosedText => Session is { ClosedByName: { } name, ClosedAt: { } at }
        ? $"Fermée par {name} à {ClosureFormat.Time(at)}"
        : "Pas encore fermée";

    public string ExpectedText => Money.ToDisplay(Session.ExpectedCash);

    public string CountedText => Session.CountedCash is { } counted ? Money.ToDisplay(counted) : "—";

    public string DifferenceText => Session.CashDifference is { } difference ? ClosureFormat.Difference(difference) : "—";

    public bool IsDifferenceAlert => Session.CashDifference is { } difference && difference != 0;

    public bool HasPendingCard => Session.PendingCardCount > 0;

    public string PendingCardText => HasPendingCard
        ? ClosureFormat.Count(Session.PendingCardCount, "paiement TPE en attente", "paiements TPE en attente")
        : string.Empty;
}

/// <summary>
/// Rapport d'une journée en trois blocs qui ne se mélangent jamais : chiffre d'affaires
/// (ce qui a été vendu, payé ou non), trésorerie (l'argent réellement entré et sorti) et
/// créances. Sert à l'aperçu non figé comme au rapport figé d'une clôture.
/// </summary>
public sealed class ClosureReportViewModel
{
    public ClosureReportViewModel(ClosureReport report)
    {
        Report = report;
        RevenueLines = BuildRevenue(report.Revenue);
        TreasuryCashLines = BuildTreasuryCash(report.Treasury);
        TreasuryCardLines = BuildTreasuryCard(report.Treasury);
        ReceivableLines = BuildReceivables(report.Receivables);
        Registers = report.Sessions.Select(session => new RegisterRowViewModel(session)).ToList();
    }

    public ClosureReport Report { get; }

    public IReadOnlyList<ReportLine> RevenueLines { get; }

    /// <summary>Trésorerie, espèces : encaissements, règlements, mouvements, attendu, compté, écart.</summary>
    public IReadOnlyList<ReportLine> TreasuryCashLines { get; }

    /// <summary>Trésorerie, carte : hors tiroir.</summary>
    public IReadOnlyList<ReportLine> TreasuryCardLines { get; }

    public IReadOnlyList<ReportLine> ReceivableLines { get; }

    public IReadOnlyList<RegisterRowViewModel> Registers { get; }

    public bool HasRegisters => Registers.Count > 0;

    public string NetRevenueText => Money.ToDisplay(Report.Revenue.NetRevenue);

    public string CashDifferenceText => ClosureFormat.Difference(Report.Treasury.CashDifference);

    public bool IsCashDifferenceAlert => Report.Treasury.CashDifference != 0;

    private static List<ReportLine> BuildRevenue(ClosureRevenue revenue) =>
    [
        new("Ventes avant remise", Money.ToDisplay(revenue.Subtotal), ClosureFormat.Count(revenue.SalesCount, "vente", "ventes")),
        new("Remises", ClosureFormat.Deducted(revenue.Discounts)),
        new("Total des ventes", Money.ToDisplay(revenue.SalesTotal), IsTotal: true),
        new("dont comptant en espèces", Money.ToDisplay(revenue.CashSales)),
        new("dont comptant par TPE", Money.ToDisplay(revenue.CardSales)),
        new("dont impayées", Money.ToDisplay(revenue.UnpaidSales), "sans encaissement"),
        new("Retours du jour", ClosureFormat.Deducted(revenue.ReturnsTotal), ClosureFormat.Count(revenue.ReturnsCount, "retour", "retours")),
        new("CHIFFRE D'AFFAIRES NET", Money.ToDisplay(revenue.NetRevenue), IsTotal: true),
        new("Ventes annulées", Money.ToDisplay(revenue.CancelledTotal),
            ClosureFormat.Count(revenue.CancelledCount, "vente", "ventes") + ", hors CA"),
        new("Paiements TPE refusés", Money.ToDisplay(revenue.CardRefusedTotal),
            ClosureFormat.Count(revenue.CardRefusedCount, "vente", "ventes") + ", hors CA"),
    ];

    private static List<ReportLine> BuildTreasuryCash(ClosureTreasury treasury) =>
    [
        new("Ventes du jour encaissées", Money.ToDisplay(treasury.SaleCash)),
        new("Impayés du jour réglés le jour même", Money.ToDisplay(treasury.SameDayReceivablesCash)),
        new("Règlements d'anciennes créances", Money.ToDisplay(treasury.OldReceivablesCash), "hors CA du jour"),
        new("Entrées d'espèces", Money.ToDisplay(treasury.CashIn)),
        new("Sorties d'espèces", ClosureFormat.Deducted(treasury.CashOut)),
        new("Remboursements d'annulations", ClosureFormat.Deducted(treasury.CancellationCashRefunds)),
        new("Remboursements de retours", ClosureFormat.Deducted(treasury.ReturnCashRefunds)),
        new("Variation des espèces", Money.ToDisplay(treasury.NetCash), IsTotal: true),
        new("Fonds de caisse", Money.ToDisplay(treasury.OpeningFloats)),
        new("Espèces attendues", Money.ToDisplay(treasury.ExpectedCash), "toutes caisses", IsTotal: true),
        new("Espèces comptées", Money.ToDisplay(treasury.CountedCash), "caisses fermées"),
        new("Écart", ClosureFormat.Difference(treasury.CashDifference), "compté − attendu", IsTotal: true,
            IsAlert: treasury.CashDifference != 0),
    ];

    private static List<ReportLine> BuildTreasuryCard(ClosureTreasury treasury) =>
    [
        new("Ventes du jour encaissées", Money.ToDisplay(treasury.SaleCard)),
        new("Impayés du jour réglés le jour même", Money.ToDisplay(treasury.SameDayReceivablesCard)),
        new("Règlements d'anciennes créances", Money.ToDisplay(treasury.OldReceivablesCard), "hors CA du jour"),
        new("Remboursements d'annulations", ClosureFormat.Deducted(treasury.CancellationCardRefunds)),
        new("Remboursements de retours", ClosureFormat.Deducted(treasury.ReturnCardRefunds)),
        new("Total TPE", Money.ToDisplay(treasury.NetCard), "hors tiroir", IsTotal: true),
    ];

    private static List<ReportLine> BuildReceivables(ClosureReceivables receivables) =>
    [
        new("Impayés créés ce jour", Money.ToDisplay(receivables.CreatedAmount),
            ClosureFormat.Count(receivables.CreatedCount, "ticket", "tickets")),
        new("Créances soldées ce jour", Money.ToDisplay(receivables.SettledAmount),
            ClosureFormat.Count(receivables.SettledCount, "ticket", "tickets")),
        new("Encours", Money.ToDisplay(receivables.OutstandingAmount),
            ClosureFormat.Count(receivables.OutstandingCount, "ticket ouvert", "tickets ouverts"), IsTotal: true),
    ];
}

/// <summary>Une clôture passée, dans l'historique.</summary>
public sealed class ClosureHistoryRowViewModel(DailyClosureSummary closure)
{
    public DailyClosureSummary Closure { get; } = closure;

    public Guid Id => Closure.Id;

    public string DateText => ClosureFormat.Date(Closure.BusinessDate);

    public string ClosedText => $"{SaleFormat.DateTime(Closure.ClosedAt)} — {Closure.ClosedByName}";

    public string NetRevenueText => Money.ToDisplay(Closure.NetRevenue);

    public string NetCashText => Money.ToDisplay(Closure.NetCash);

    public string NetCardText => Money.ToDisplay(Closure.NetCard);

    public string DifferenceText => ClosureFormat.Difference(Closure.CashDifference);

    public bool IsDifferenceAlert => Closure.CashDifference != 0;

    public string ForcedText => Closure.Forced ? "FORCÉE" : string.Empty;
}

/// <summary>Le rapport de la fermeture d'une caisse : ce qui devait être dans le tiroir, ce qui y était.</summary>
public sealed class SessionReportViewModel(CashSessionReport report)
{
    public CashSessionReport Report { get; } = report;

    public string Title => $"{Report.RegisterName} — fermée";

    public string ClosedText => Report is { ClosedByName: { } name, ClosedAt: { } at }
        ? $"Fermée par {name} le {SaleFormat.DateTime(at)}"
        : string.Empty;

    public IReadOnlyList<ReportLine> Lines { get; } =
    [
        new("Fond de caisse", Money.ToDisplay(report.OpeningFloat)),
        new("Ventes en espèces", Money.ToDisplay(report.SaleCash)),
        new("Règlements d'impayés en espèces", Money.ToDisplay(report.SettlementCash)),
        new("Entrées d'espèces", Money.ToDisplay(report.CashIn)),
        new("Sorties d'espèces", ClosureFormat.Deducted(report.CashOut)),
        new("Remboursements en espèces", ClosureFormat.Deducted(report.CancellationCashRefunds + report.ReturnCashRefunds)),
        new("TPE (ventes et règlements)", Money.ToDisplay(report.SaleCard + report.SettlementCard), "hors tiroir"),
    ];

    public string ExpectedText => Money.ToDisplay(Report.ExpectedCash);

    public string CountedText => Report.CountedCash is { } counted ? Money.ToDisplay(counted) : "—";

    public string DifferenceText => ClosureFormat.Difference(Report.CashDifference ?? 0);

    public bool IsDifferenceAlert => Report.CashDifference is { } difference && difference != 0;

    public string DifferenceExplanation => Report.CashDifference switch
    {
        null or 0 => "Aucun écart : le tiroir contient exactement les espèces attendues.",
        < 0 => "Il manque des espèces dans le tiroir par rapport à ce qui était attendu.",
        _ => "Le tiroir contient plus d'espèces que ce qui était attendu.",
    };
}
