namespace Pharmaly.Contracts;

// ---------- Retours clients ----------

public static class ReturnRoutes
{
    /// <summary>POST : enregistrer un retour. GET : historique (« ?du=&amp;au=&amp;ticket=&amp;vente=&amp;limite= »).</summary>
    public const string Returns = "/retours";

    /// <summary>POST : calculer le remboursement d'un retour sans rien enregistrer.</summary>
    public const string Preview = "/retours/apercu";

    /// <summary>GET « /retours/ticket/{numéro ou code-barres} » : ce qui reste retournable sur un ticket.</summary>
    public const string ReturnableByTicket = "/retours/ticket";

    /// <summary>GET : motifs (« ?tous=true » pour voir aussi les motifs désactivés). POST : créer un motif.</summary>
    public const string Reasons = "/retours/motifs";

    /// <summary>GET : un retour.</summary>
    public static string Return(Guid id) => $"{Returns}/{id}";

    public static string ByTicket(string numberOrBarcode) => $"{ReturnableByTicket}/{Uri.EscapeDataString(numberOrBarcode)}";

    /// <summary>PUT : modifier ou désactiver un motif.</summary>
    public static string Reason(Guid id) => $"{Reasons}/{id}";
}

public static class ReturnErrorCodes
{
    /// <summary>Vente annulée ou en attente de réponse du TPE : rien à retourner.</summary>
    public const string SaleNotReturnable = "VENTE_NON_RETOURNABLE";

    /// <summary>
    /// Vente impayée dont la créance est encore ouverte : pas de retour tant qu'elle n'est pas
    /// réglée (ou annuler la vente si sa journée n'est pas clôturée).
    /// </summary>
    public const string ReceivableOpen = "RETOUR_CREANCE_OUVERTE";

    /// <summary>Quantité demandée supérieure à ce qui reste retournable sur la ligne.</summary>
    public const string QuantityExceeded = "QUANTITE_RETOUR_DEPASSEE";

    /// <summary>Motif inconnu ou désactivé.</summary>
    public const string InvalidReason = "MOTIF_RETOUR_INVALIDE";

    /// <summary>Le remboursement calculé par le serveur n'est pas celui affiché par le poste.</summary>
    public const string RefundMismatch = "REMBOURSEMENT_DIFFERENT";

    /// <summary>La vente a déjà fait l'objet d'un retour : elle ne peut plus être annulée.</summary>
    public const string SaleHasReturns = "VENTE_AVEC_RETOUR";

    /// <summary>Le site n'a pas d'emplacement actif pour la destination du motif.</summary>
    public const string NoDestinationLocation = "EMPLACEMENT_RETOUR_ABSENT";
}

/// <summary>Destination de la marchandise retournée, portée par le motif.</summary>
public static class ReturnStockDestinations
{
    /// <summary>Emplacement vendable du site.</summary>
    public const string Sellable = "VENDABLE";

    /// <summary>Emplacement « mis à l'écart » du site.</summary>
    public const string SetAside = "ECARTE";

    /// <summary>Aucune réintégration : la marchandise sort du circuit, aucun mouvement de stock.</summary>
    public const string None = "AUCUNE";

    public static bool IsKnown(string? destination) => destination is Sellable or SetAside or None;
}

public sealed record ReturnReason(Guid Id, string Label, string StockDestination, bool IsActive, int SortOrder);

public sealed record CreateReturnReasonRequest(Guid OperationId, string Label, string StockDestination);

/// <summary>Modifie le libellé, la destination et l'état. IsActive = false désactive le motif.</summary>
public sealed record UpdateReturnReasonRequest(Guid OperationId, string Label, string StockDestination, bool IsActive);

/// <summary>Ligne retournée : le produit du ticket et la quantité rendue.</summary>
public sealed record ReturnLineRequest(Guid ProductId, decimal Quantity);

/// <summary>
/// Le poste n'envoie jamais de montant : le serveur calcule le remboursement.
/// <para>ExpectedRefund (facultatif) : remboursement affiché à l'employé ; s'il diffère du
/// calcul du serveur, le retour est refusé (REMBOURSEMENT_DIFFERENT).</para>
/// </summary>
public sealed record CreateReturnRequest(
    Guid OperationId,
    Guid SaleId,
    Guid ReasonId,
    IReadOnlyList<ReturnLineRequest> Lines,
    string? Note = null,
    decimal? ExpectedRefund = null);

/// <summary>Même contenu qu'un retour, sans identifiant d'opération : rien n'est enregistré.</summary>
public sealed record PreviewReturnRequest(Guid SaleId, Guid ReasonId, IReadOnlyList<ReturnLineRequest> Lines);

public sealed record ReturnPreviewLine(Guid ProductId, string ProductName, decimal Quantity, decimal RefundAmount, decimal RestockedQuantity);

/// <summary>RefundMethod : ESPECES, TPE, ou absent si rien n'est à rembourser.</summary>
public sealed record ReturnPreview(
    Guid SaleId,
    string TicketNumber,
    string ReasonLabel,
    string StockDestination,
    string? RefundMethod,
    decimal RefundAmount,
    IReadOnlyList<ReturnPreviewLine> Lines);

/// <param name="LineNumber">Numéro de la ligne sur le ticket.</param>
/// <param name="ProductId">Produit.</param>
/// <param name="ProductName">Désignation au moment de la vente.</param>
/// <param name="SoldQuantity">Quantité vendue.</param>
/// <param name="ReturnedQuantity">Quantité déjà retournée.</param>
/// <param name="ReturnableQuantity">Quantité encore retournable.</param>
/// <param name="UnitPrice">Prix unitaire du ticket, avant remise.</param>
/// <param name="PaidAmount">Montant réellement payé pour la ligne, remise de la vente déduite.</param>
/// <param name="RefundedAmount">Déjà remboursé pour cette ligne.</param>
/// <param name="RefundableAmount">Remboursement si tout le reste de la ligne est retourné.</param>
public sealed record ReturnableLine(
    int LineNumber,
    Guid ProductId,
    string ProductName,
    decimal SoldQuantity,
    decimal ReturnedQuantity,
    decimal ReturnableQuantity,
    decimal UnitPrice,
    decimal PaidAmount,
    decimal RefundedAmount,
    decimal RefundableAmount);

/// <summary>
/// Ce qui peut être retourné sur un ticket. Quand CanReturn est faux, BlockedCode et
/// BlockedMessage disent pourquoi (vente annulée, créance encore ouverte…).
/// </summary>
public sealed record ReturnableSale(
    Guid SaleId,
    string TicketNumber,
    string TicketBarcode,
    DateTimeOffset SoldAt,
    string Status,
    string PaymentMode,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal Total,
    string? CustomerName,
    bool CanReturn,
    string? BlockedCode,
    string? BlockedMessage,
    string? RefundMethod,
    decimal RefundedAmount,
    IReadOnlyList<ReturnableLine> Lines);

/// <param name="LineNumber">Numéro de la ligne sur le retour.</param>
/// <param name="ProductId">Produit.</param>
/// <param name="ProductName">Désignation au moment de la vente.</param>
/// <param name="Quantity">Quantité retournée.</param>
/// <param name="UnitPrice">Prix unitaire du ticket, avant remise.</param>
/// <param name="RefundAmount">Remboursement dû à cette ligne.</param>
/// <param name="RestockedQuantity">Quantité remise en stock.</param>
/// <param name="NotRestockedQuantity">Quantité sortie du circuit (aucun mouvement de stock).</param>
public sealed record ReturnLineDetail(
    int LineNumber,
    Guid ProductId,
    string ProductName,
    decimal Quantity,
    decimal UnitPrice,
    decimal RefundAmount,
    decimal RestockedQuantity,
    decimal NotRestockedQuantity);

public sealed record ReturnDetail(
    Guid Id,
    string ReturnNumber,
    Guid SaleId,
    string TicketNumber,
    Guid StoreId,
    string RegisterName,
    Guid CashSessionId,
    Guid EmployeeId,
    string EmployeeName,
    DateTimeOffset CreatedAt,
    Guid ReasonId,
    string ReasonLabel,
    string StockDestination,
    Guid? DestinationLocationId,
    string? DestinationLocationName,
    string? Note,
    string? RefundMethod,
    decimal RefundAmount,
    IReadOnlyList<ReturnLineDetail> Lines);

public sealed record ReturnSummary(
    Guid Id,
    string ReturnNumber,
    Guid SaleId,
    string TicketNumber,
    DateTimeOffset CreatedAt,
    string ReasonLabel,
    string StockDestination,
    string? RefundMethod,
    decimal RefundAmount,
    Guid EmployeeId,
    string EmployeeName);
