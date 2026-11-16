namespace Pharmaly.Contracts;

// ---------- Caisse : session du poste et mouvements d'espèces ----------

public static class CashRoutes
{
    /// <summary>GET : session ouverte du compte de caisse du poste (ou aucune).</summary>
    public const string CurrentSession = "/caisse/session";

    /// <summary>POST : ouvrir la session avec le fond de caisse compté.</summary>
    public const string OpenSession = "/caisse/session/ouverture";

    /// <summary>GET : totaux de la session ouverte (espèces attendues, TPE, règlements…).</summary>
    public const string SessionSummary = "/caisse/session/resume";

    /// <summary>POST : entrée ou sortie d'espèces hors vente.</summary>
    public const string Movements = "/caisse/mouvements";
}

public static class CashErrorCodes
{
    /// <summary>Aucune session de caisse ouverte sur ce poste.</summary>
    public const string NoOpenSession = "CAISSE_NON_OUVERTE";

    public const string SessionAlreadyOpen = "CAISSE_DEJA_OUVERTE";
}

public static class PaymentMethods
{
    public const string Cash = "ESPECES";
    public const string Card = "TPE";

    public static bool IsKnown(string? method) => method is Cash or Card;
}

/// <summary>État d'un encaissement (paiement de vente ou règlement de créance).</summary>
public static class PaymentStatuses
{
    /// <summary>TPE : en attente de la réponse du terminal. Jamais compté comme encaissé.</summary>
    public const string Pending = "EN_ATTENTE";
    public const string Confirmed = "CONFIRME";

    /// <summary>TPE : paiement refusé.</summary>
    public const string Failed = "ECHOUE";

    /// <summary>Encaissé, puis remboursé par l'annulation de la vente.</summary>
    public const string Refunded = "REMBOURSE";
}

public static class CashMovementKinds
{
    public const string In = "ENTREE";
    public const string Out = "SORTIE";

    /// <summary>Espèces rendues pour une vente annulée : créé par l'annulation, jamais à la main.</summary>
    public const string SaleRefund = "REMBOURSEMENT_VENTE";
}

public sealed record OpenCashSessionRequest(Guid OperationId, decimal OpeningFloat);

public sealed record CashSessionInfo(
    Guid Id,
    Guid RegisterAccountId,
    string RegisterName,
    Guid StoreId,
    string Status,
    decimal OpeningFloat,
    Guid OpenedById,
    string OpenedByName,
    DateTimeOffset OpenedAt);

/// <summary>Session est absent quand la caisse du poste n'est pas ouverte.</summary>
public sealed record CurrentCashSessionResponse(CashSessionInfo? Session);

/// <summary>
/// Totaux de la session. Un paiement TPE en attente n'entre dans aucun total encaissé :
/// il est compté à part (PendingCard*).
/// </summary>
/// <param name="Session">Session concernée.</param>
/// <param name="SaleCashCollected">Espèces encaissées pour des ventes de la session, remboursements non déduits.</param>
/// <param name="SaleCardCollected">Paiements par carte acceptés pour des ventes, remboursements non déduits.</param>
/// <param name="SettlementCashCollected">Règlements d'anciennes créances en espèces : trésorerie, pas du chiffre d'affaires.</param>
/// <param name="SettlementCardCollected">Règlements d'anciennes créances par carte.</param>
/// <param name="CashIn">Entrées d'espèces hors vente.</param>
/// <param name="CashOut">Sorties d'espèces hors vente.</param>
/// <param name="CashRefunds">Espèces rendues par cette session pour des ventes annulées.</param>
/// <param name="CardRefunds">Remboursements par carte faits par cette session pour des ventes annulées.</param>
/// <param name="ExpectedCash">Fond + espèces encaissées + entrées − sorties − remboursements en espèces (annulations et retours).</param>
/// <param name="PendingCardCount">Paiements par carte en attente de réponse (ventes et règlements).</param>
/// <param name="PendingCardAmount">Leur montant : compté nulle part ailleurs.</param>
/// <param name="ReturnCashRefunds">Espèces rendues par cette session pour des retours clients.</param>
/// <param name="ReturnCardRefunds">Remboursements par carte faits par cette session pour des retours clients.</param>
public sealed record CashSessionSummary(
    CashSessionInfo Session,
    decimal SaleCashCollected,
    decimal SaleCardCollected,
    decimal SettlementCashCollected,
    decimal SettlementCardCollected,
    decimal CashIn,
    decimal CashOut,
    decimal CashRefunds,
    decimal CardRefunds,
    decimal ExpectedCash,
    int PendingCardCount,
    decimal PendingCardAmount,
    decimal ReturnCashRefunds = 0,
    decimal ReturnCardRefunds = 0);

/// <summary>Kind : ENTREE ou SORTIE. Reason est obligatoire.</summary>
public sealed record CashMovementRequest(Guid OperationId, string Kind, decimal Amount, string Reason);

public sealed record CashMovementResult(
    Guid Id,
    Guid CashSessionId,
    string Kind,
    decimal Amount,
    string Reason,
    Guid EmployeeId,
    DateTimeOffset OccurredAt);
