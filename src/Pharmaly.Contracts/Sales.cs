namespace Pharmaly.Contracts;

// ---------- Ventes ----------

public static class SaleRoutes
{
    /// <summary>POST : créer une vente. GET : historique filtrable.</summary>
    public const string Sales = "/ventes";

    /// <summary>GET « /ventes/ticket/{numéro ou code-barres} ».</summary>
    public const string SaleByTicket = "/ventes/ticket";

    public const string CancellationSuffix = "annulation";
    public const string CardResultSuffix = "tpe";

    /// <summary>GET : détail d'un ticket.</summary>
    public static string Sale(Guid id) => $"{Sales}/{id}";

    /// <summary>POST : annuler une vente.</summary>
    public static string Cancellation(Guid id) => $"{Sales}/{id}/{CancellationSuffix}";

    /// <summary>POST : réponse du TPE (accepté ou refusé).</summary>
    public static string CardResult(Guid id) => $"{Sales}/{id}/{CardResultSuffix}";

    public static string ByTicket(string numberOrBarcode) => $"{SaleByTicket}/{Uri.EscapeDataString(numberOrBarcode)}";
}

public static class SaleErrorCodes
{
    public const string InactiveProduct = "PRODUIT_INACTIF";
    public const string InvalidDiscount = "REMISE_INVALIDE";
    public const string InsufficientAmountReceived = "MONTANT_RECU_INSUFFISANT";
    public const string CustomerRequired = "CLIENT_OBLIGATOIRE";

    /// <summary>Le total calculé par le serveur n'est pas celui affiché par le poste (prix modifié).</summary>
    public const string TotalMismatch = "TOTAL_DIFFERENT";
    public const string AlreadyCancelled = "VENTE_DEJA_ANNULEE";

    /// <summary>La créance de cette vente est déjà réglée : passer par un retour.</summary>
    public const string ReceivableAlreadySettled = "CREANCE_DEJA_REGLEE";

    /// <summary>La journée de la vente est clôturée : passer par un retour.</summary>
    public const string DayClosed = "JOURNEE_CLOTUREE";

    /// <summary>Le paiement par carte n'attend plus de réponse.</summary>
    public const string CardNotPending = "PAIEMENT_TPE_NON_EN_ATTENTE";

    /// <summary>Le paiement par carte attend encore sa réponse : répondre d'abord accepté ou refusé.</summary>
    public const string CardPending = "PAIEMENT_TPE_EN_ATTENTE";
}

public static class SalePaymentModes
{
    public const string Cash = PaymentMethods.Cash;
    public const string Card = PaymentMethods.Card;
    public const string Unpaid = "IMPAYE";
}

public static class SaleStatuses
{
    public const string Completed = "VALIDEE";

    /// <summary>Paiement par carte lancé, réponse du terminal attendue.</summary>
    public const string CardPending = "EN_ATTENTE_TPE";
    public const string Cancelled = "ANNULEE";

    public static bool IsKnown(string? status) => status is Completed or CardPending or Cancelled;
}

public static class DiscountKinds
{
    public const string Amount = "MONTANT";
    public const string Percent = "POURCENTAGE";
}

public static class SaleCancellationKinds
{
    public const string Cancelled = "ANNULATION";
    public const string CardRefused = "TPE_REFUSE";
}

public sealed record SaleLineRequest(Guid ProductId, decimal Quantity);

/// <summary>
/// Le poste n'envoie jamais de prix : le serveur lit le prix de vente au moment de la vente.
/// <para>PaymentMode : ESPECES (AmountReceived obligatoire), TPE ou IMPAYE (CustomerId obligatoire).</para>
/// <para>Remise sur la vente entière : DiscountKind = MONTANT ou POURCENTAGE, avec DiscountValue.</para>
/// <para>ExpectedTotal (facultatif) : total affiché au client ; s'il diffère du total calculé
/// par le serveur, la vente est refusée (TOTAL_DIFFERENT) au lieu d'être encaissée à un autre montant.</para>
/// </summary>
public sealed record CreateSaleRequest(
    Guid OperationId,
    IReadOnlyList<SaleLineRequest> Lines,
    string PaymentMode,
    string? DiscountKind = null,
    decimal? DiscountValue = null,
    decimal? AmountReceived = null,
    Guid? CustomerId = null,
    decimal? ExpectedTotal = null);

public sealed record SaleLineDetail(
    int LineNumber,
    Guid ProductId,
    string ProductName,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal);

public sealed record SalePaymentDetail(
    Guid Id,
    string Method,
    string Status,
    decimal Amount,
    decimal? AmountReceived,
    decimal? ChangeGiven,
    Guid CashSessionId,
    Guid EmployeeId,
    string EmployeeName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConfirmedAt);

public sealed record SaleReceivableDetail(Guid Id, string Status, decimal Amount, DateTimeOffset? SettledAt);

public sealed record SaleCancellationDetail(
    string Kind,
    string Reason,
    Guid EmployeeId,
    string EmployeeName,
    DateTimeOffset OccurredAt,
    string? RefundMethod,
    decimal RefundAmount);

/// <summary>Ticket complet. IsUnpaid : vente impayée dont la créance est encore ouverte.</summary>
public sealed record SaleDetail(
    Guid Id,
    string TicketNumber,
    string TicketBarcode,
    Guid StoreId,
    string StoreName,
    string RegisterName,
    Guid CashSessionId,
    Guid EmployeeId,
    string EmployeeName,
    DateTimeOffset CreatedAt,
    string Status,
    string PaymentMode,
    bool IsUnpaid,
    decimal Subtotal,
    string? DiscountKind,
    decimal? DiscountValue,
    decimal DiscountAmount,
    decimal Total,
    Guid? CustomerId,
    string? CustomerName,
    IReadOnlyList<SaleLineDetail> Lines,
    SalePaymentDetail? Payment,
    SaleReceivableDetail? Receivable,
    SaleCancellationDetail? Cancellation);

public sealed record SaleSummary(
    Guid Id,
    string TicketNumber,
    string TicketBarcode,
    DateTimeOffset CreatedAt,
    string Status,
    string PaymentMode,
    bool IsUnpaid,
    decimal Total,
    Guid EmployeeId,
    string EmployeeName,
    string? CustomerName);

/// <summary>Reason est obligatoire.</summary>
public sealed record CancelSaleRequest(Guid OperationId, string Reason);

/// <summary>Réponse du terminal de paiement, saisie par le caissier (D6).</summary>
public sealed record CardResultRequest(Guid OperationId, bool Accepted);
