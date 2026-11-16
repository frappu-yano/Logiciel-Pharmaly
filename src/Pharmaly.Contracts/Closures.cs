namespace Pharmaly.Contracts;

// ---------- Fermeture d'une caisse et clôture quotidienne du magasin ----------

public static class ClosureRoutes
{
    /// <summary>POST : fermer la caisse du poste, avec le comptage des espèces.</summary>
    public const string CloseSession = "/caisse/session/fermeture";

    /// <summary>GET : journée en cours du magasin (caisses, ce qui bloque la clôture, aperçu des totaux).</summary>
    public const string CurrentDay = "/cloture/journee";

    /// <summary>POST : clôturer la journée du magasin. GET : clôtures passées (« ?du=&amp;au=&amp;limite= »).</summary>
    public const string Closures = "/cloture";

    /// <summary>GET : une clôture et son rapport figé.</summary>
    public static string Closure(Guid id) => $"{Closures}/{id}";
}

public static class ClosureErrorCodes
{
    /// <summary>Un paiement ou un règlement par carte attend sa réponse sur cette caisse (D23).</summary>
    public const string CardPending = "CAISSE_TPE_EN_ATTENTE";

    /// <summary>La journée ne peut pas être clôturée : caisse encore ouverte ou opération en attente.</summary>
    public const string DayBlocked = "CLOTURE_BLOQUEE";

    /// <summary>Des opérations de secours ne sont pas synchronisées : journée non consolidable.</summary>
    public const string OfflineOperationsPending = "OPERATIONS_HORS_LIGNE_EN_ATTENTE";

    /// <summary>Ce magasin a déjà une clôture pour cette date (ou une date postérieure).</summary>
    public const string DayAlreadyClosed = "JOURNEE_DEJA_CLOTUREE";

    /// <summary>La date demandée est dans le futur.</summary>
    public const string InvalidDate = "DATE_DE_CLOTURE_INVALIDE";
}

public static class ClosureBlockerKinds
{
    public const string SessionOpen = "CAISSE_OUVERTE";
    public const string SaleCardPending = "PAIEMENT_TPE_EN_ATTENTE";
    public const string SettlementCardPending = "REGLEMENT_TPE_EN_ATTENTE";
    public const string OfflineOperations = "OPERATIONS_HORS_LIGNE";
}

/// <summary>
/// Ce qui empêche de fermer une caisse ou de clôturer la journée.
/// <para>Kind : voir <see cref="ClosureBlockerKinds"/>. Reference : identifiant de la session, du
/// ticket ou du règlement concerné. Amount : montant du paiement en attente, s'il y en a un.</para>
/// </summary>
public sealed record ClosureBlocker(string Kind, string Message, Guid? Reference = null, string? RegisterName = null, decimal? Amount = null);

/// <summary>
/// Refus d'une fermeture ou d'une clôture : mêmes champs qu'<see cref="ApiError"/>, plus la
/// liste de ce qui bloque.
/// </summary>
public sealed record ClosureBlockedError(string Code, string Message, IReadOnlyList<ClosureBlocker> Blockers);

public sealed record CloseCashSessionRequest(Guid OperationId, decimal CountedCash);

/// <summary>
/// Une caisse (session) et ses totaux. CountedCash et CashDifference sont absents tant que la
/// caisse est ouverte.
/// </summary>
/// <param name="Id">Session de caisse.</param>
/// <param name="RegisterAccountId">Compte de caisse.</param>
/// <param name="RegisterName">Nom affiché de la caisse.</param>
/// <param name="Status">OUVERTE ou FERMEE.</param>
/// <param name="OpenedByName">Employé qui a ouvert.</param>
/// <param name="OpenedAt">Date d'ouverture.</param>
/// <param name="ClosedByName">Employé qui a compté et fermé.</param>
/// <param name="ClosedAt">Date de fermeture.</param>
/// <param name="OpeningFloat">Fond de caisse.</param>
/// <param name="SaleCash">Ventes encaissées en espèces.</param>
/// <param name="SaleCard">Ventes encaissées par carte.</param>
/// <param name="SettlementCash">Règlements d'impayés en espèces.</param>
/// <param name="SettlementCard">Règlements d'impayés par carte.</param>
/// <param name="CashIn">Entrées d'espèces hors vente.</param>
/// <param name="CashOut">Sorties d'espèces hors vente.</param>
/// <param name="CancellationCashRefunds">Espèces rendues pour des ventes annulées.</param>
/// <param name="CancellationCardRefunds">Remboursements par carte de ventes annulées.</param>
/// <param name="ReturnCashRefunds">Espèces rendues pour des retours.</param>
/// <param name="ReturnCardRefunds">Remboursements par carte de retours.</param>
/// <param name="ExpectedCash">Fond + ventes espèces + règlements espèces + entrées − sorties − remboursements espèces.</param>
/// <param name="CountedCash">Espèces comptées à la fermeture.</param>
/// <param name="CashDifference">Compté − attendu : négatif s'il manque des espèces.</param>
/// <param name="PendingCardCount">Paiements et règlements par carte en attente de réponse.</param>
public sealed record CashSessionReport(
    Guid Id,
    Guid RegisterAccountId,
    string RegisterName,
    string Status,
    string OpenedByName,
    DateTimeOffset OpenedAt,
    string? ClosedByName,
    DateTimeOffset? ClosedAt,
    decimal OpeningFloat,
    decimal SaleCash,
    decimal SaleCard,
    decimal SettlementCash,
    decimal SettlementCard,
    decimal CashIn,
    decimal CashOut,
    decimal CancellationCashRefunds,
    decimal CancellationCardRefunds,
    decimal ReturnCashRefunds,
    decimal ReturnCardRefunds,
    decimal ExpectedCash,
    decimal? CountedCash,
    decimal? CashDifference,
    int PendingCardCount);

/// <summary>
/// Chiffre d'affaires de la journée : ce qui a été vendu, payé ou non. Ce n'est PAS la trésorerie.
/// </summary>
/// <param name="SalesCount">Ventes validées de la journée (comptant et impayées).</param>
/// <param name="Subtotal">Leur montant avant remise.</param>
/// <param name="Discounts">Remises accordées sur ces ventes.</param>
/// <param name="CashSales">Ventes payées en espèces, remises déduites.</param>
/// <param name="CardSales">Ventes payées par carte, remises déduites.</param>
/// <param name="UnpaidSales">Ventes impayées : chiffre d'affaires sans encaissement.</param>
/// <param name="SalesTotal">Espèces + carte + impayées = sous-total − remises.</param>
/// <param name="CancelledCount">Ventes de la journée annulées par un employé (hors chiffre d'affaires).</param>
/// <param name="CancelledTotal">Leur montant.</param>
/// <param name="CardRefusedCount">Ventes annulées parce que le TPE a refusé le paiement (hors chiffre d'affaires).</param>
/// <param name="CardRefusedTotal">Leur montant.</param>
/// <param name="ReturnsCount">Retours faits dans la journée, quelle que soit la date de la vente d'origine.</param>
/// <param name="ReturnsTotal">Valeur des articles retournés (montant remboursé).</param>
/// <param name="NetRevenue">SalesTotal − ReturnsTotal.</param>
public sealed record ClosureRevenue(
    int SalesCount,
    decimal Subtotal,
    decimal Discounts,
    decimal CashSales,
    decimal CardSales,
    decimal UnpaidSales,
    decimal SalesTotal,
    int CancelledCount,
    decimal CancelledTotal,
    int CardRefusedCount,
    decimal CardRefusedTotal,
    int ReturnsCount,
    decimal ReturnsTotal,
    decimal NetRevenue);

/// <summary>
/// Trésorerie de la journée : l'argent réellement entré et sorti. Ce n'est PAS le chiffre d'affaires.
/// </summary>
/// <param name="SaleCash">Encaissements en espèces des ventes de la journée (remboursements non déduits).</param>
/// <param name="SaleCard">Encaissements par carte des ventes de la journée (remboursements non déduits).</param>
/// <param name="OldReceivablesCash">Règlements en espèces de créances d'un autre jour : trésorerie sans chiffre d'affaires.</param>
/// <param name="OldReceivablesCard">Règlements par carte de créances d'un autre jour.</param>
/// <param name="SameDayReceivablesCash">Règlements en espèces d'impayés créés ce jour même.</param>
/// <param name="SameDayReceivablesCard">Règlements par carte d'impayés créés ce jour même.</param>
/// <param name="CancellationCashRefunds">Espèces rendues pour des ventes annulées.</param>
/// <param name="CancellationCardRefunds">Remboursements par carte de ventes annulées.</param>
/// <param name="ReturnCashRefunds">Espèces rendues pour des retours.</param>
/// <param name="ReturnCardRefunds">Remboursements par carte de retours.</param>
/// <param name="CashIn">Entrées d'espèces hors vente.</param>
/// <param name="CashOut">Sorties d'espèces hors vente.</param>
/// <param name="NetCash">Variation des espèces : encaissements + entrées − sorties − remboursements.</param>
/// <param name="NetCard">Encaissements par carte − remboursements par carte.</param>
/// <param name="OpeningFloats">Somme des fonds de caisse.</param>
/// <param name="ExpectedCash">Espèces attendues, toutes caisses : fonds + NetCash.</param>
/// <param name="CountedCash">Espèces comptées, toutes caisses fermées.</param>
/// <param name="CashDifference">Écart total : compté − attendu (caisses fermées).</param>
public sealed record ClosureTreasury(
    decimal SaleCash,
    decimal SaleCard,
    decimal OldReceivablesCash,
    decimal OldReceivablesCard,
    decimal SameDayReceivablesCash,
    decimal SameDayReceivablesCard,
    decimal CancellationCashRefunds,
    decimal CancellationCardRefunds,
    decimal ReturnCashRefunds,
    decimal ReturnCardRefunds,
    decimal CashIn,
    decimal CashOut,
    decimal NetCash,
    decimal NetCard,
    decimal OpeningFloats,
    decimal ExpectedCash,
    decimal CountedCash,
    decimal CashDifference);

/// <summary>Créances du magasin.</summary>
/// <param name="CreatedCount">Impayés créés ce jour (ventes impayées validées de la journée).</param>
/// <param name="CreatedAmount">Leur montant.</param>
/// <param name="SettledCount">Créances soldées ce jour, à une caisse de ce magasin.</param>
/// <param name="SettledAmount">Leur montant.</param>
/// <param name="OutstandingCount">Créances de ce magasin encore ouvertes à la clôture.</param>
/// <param name="OutstandingAmount">Encours à la clôture.</param>
public sealed record ClosureReceivables(
    int CreatedCount,
    decimal CreatedAmount,
    int SettledCount,
    decimal SettledAmount,
    int OutstandingCount,
    decimal OutstandingAmount);

/// <summary>
/// Rapport d'une journée. Stocké tel quel à la clôture ; calculé à la demande pour l'aperçu.
/// </summary>
/// <param name="Revenue">Chiffre d'affaires.</param>
/// <param name="Treasury">Trésorerie.</param>
/// <param name="Receivables">Créances.</param>
/// <param name="Sessions">Caisses de la journée, avec le comptage de chacune.</param>
/// <param name="ForcedWithUnknownOperations">Vrai : « clôture forcée avec opérations inconnues ».</param>
/// <param name="UnknownOfflineOperations">Opérations de secours non synchronisées au moment de la clôture.</param>
/// <param name="Warning">Mention à afficher en tête du rapport quand la clôture a été forcée.</param>
public sealed record ClosureReport(
    ClosureRevenue Revenue,
    ClosureTreasury Treasury,
    ClosureReceivables Receivables,
    IReadOnlyList<CashSessionReport> Sessions,
    bool ForcedWithUnknownOperations = false,
    int UnknownOfflineOperations = 0,
    string? Warning = null);

/// <summary>
/// Journée en cours du magasin du poste : tout ce qui n'appartient pas encore à une journée clôturée.
/// </summary>
/// <param name="StoreId">Magasin.</param>
/// <param name="StoreName">Nom du magasin.</param>
/// <param name="Today">Date du jour au magasin (heure d'Algérie).</param>
/// <param name="LastClosedDate">Dernière journée clôturée, s'il y en a une.</param>
/// <param name="CanClose">Vrai si rien ne bloque et si la date du jour n'est pas déjà clôturée.</param>
/// <param name="Blockers">Ce qui empêche de clôturer.</param>
/// <param name="PendingOfflineOperations">Opérations de secours en attente de synchronisation.</param>
/// <param name="Preview">Aperçu des totaux, non figé.</param>
public sealed record CurrentDayStatus(
    Guid StoreId,
    string StoreName,
    DateOnly Today,
    DateOnly? LastClosedDate,
    bool CanClose,
    IReadOnlyList<ClosureBlocker> Blockers,
    int PendingOfflineOperations,
    ClosureReport Preview);

/// <summary>
/// Clôture de la journée du magasin du poste.
/// <para>BusinessDate (facultatif) : journée à clôturer, par défaut la date du jour. Sert à
/// clôturer une journée oubliée ; jamais dans le futur, toujours après la dernière clôture.</para>
/// <para>ForceWithUnknownOperations + ForceReason : procédure exceptionnelle, quand des opérations
/// de secours restent non synchronisées. Le motif est obligatoire.</para>
/// </summary>
public sealed record CloseDayRequest(
    Guid OperationId,
    DateOnly? BusinessDate = null,
    bool ForceWithUnknownOperations = false,
    string? ForceReason = null);

public sealed record DailyClosureSummary(
    Guid Id,
    Guid StoreId,
    DateOnly BusinessDate,
    DateTimeOffset ClosedAt,
    Guid ClosedById,
    string ClosedByName,
    bool Forced,
    decimal NetRevenue,
    decimal NetCash,
    decimal NetCard,
    decimal CashDifference);

public sealed record DailyClosureDetail(
    Guid Id,
    Guid StoreId,
    string StoreName,
    DateOnly BusinessDate,
    DateTimeOffset ClosedAt,
    Guid ClosedById,
    string ClosedByName,
    bool Forced,
    string? ForceReason,
    ClosureReport Report);
