namespace Pharmaly.Contracts;

// ---------- Clients ----------

public static class CustomerRoutes
{
    /// <summary>POST : création rapide. GET « /clients/{id} » : fiche avec ses créances.</summary>
    public const string Customers = "/clients";

    /// <summary>GET « /clients/recherche?q=…&amp;limite=… ».</summary>
    public const string CustomerSearch = "/clients/recherche";

    public static string Customer(Guid id) => $"{Customers}/{id}";
}

/// <summary>Name est obligatoire, Phone facultatif.</summary>
public sealed record CreateCustomerRequest(Guid OperationId, string Name, string? Phone = null);

/// <summary>OpenAmount et OpenCount : créances encore ouvertes du client.</summary>
public sealed record CustomerSummary(Guid Id, string Name, string? Phone, decimal OpenAmount = 0, int OpenCount = 0);

public sealed record CustomerDetail(
    Guid Id,
    string Name,
    string? Phone,
    DateTimeOffset CreatedAt,
    decimal OpenAmount,
    int OpenCount,
    IReadOnlyList<ReceivableLine> Receivables);

// ---------- Impayés ----------

public static class ReceivableRoutes
{
    /// <summary>GET : tableau des impayés avec ses indicateurs.</summary>
    public const string Receivables = "/impayes";

    /// <summary>GET « /impayes/ticket/{numéro ou code-barres} ».</summary>
    public const string ReceivableByTicket = "/impayes/ticket";

    /// <summary>POST : régler une ou plusieurs créances du même client.</summary>
    public const string Settlements = "/impayes/reglements";

    public const string CardResultSuffix = "tpe";

    public static string ByTicket(string numberOrBarcode) =>
        $"{ReceivableByTicket}/{Uri.EscapeDataString(numberOrBarcode)}";

    /// <summary>GET : un règlement.</summary>
    public static string Settlement(Guid id) => $"{Settlements}/{id}";

    /// <summary>POST : réponse du TPE (accepté ou refusé) pour un règlement en attente.</summary>
    public static string SettlementCardResult(Guid id) => $"{Settlements}/{id}/{CardResultSuffix}";
}

public static class ReceivableErrorCodes
{
    /// <summary>Le montant encaissé n'est pas exactement la somme due (règlement partiel interdit).</summary>
    public const string AmountMismatch = "MONTANT_DIFFERENT_DU_DU";
    public const string DifferentCustomers = "CLIENTS_DIFFERENTS";

    /// <summary>Créance déjà soldée ou annulée.</summary>
    public const string NotOpen = "CREANCE_NON_OUVERTE";

    /// <summary>Un paiement par carte est en attente sur cette créance.</summary>
    public const string SettlementPending = "REGLEMENT_EN_COURS";

    /// <summary>Le règlement n'attend plus de réponse du TPE.</summary>
    public const string SettlementNotPending = "REGLEMENT_NON_EN_ATTENTE";
}

public static class ReceivableStatuses
{
    public const string Open = "OUVERTE";
    public const string Settled = "SOLDEE";
    public const string Cancelled = "ANNULEE";

    public static bool IsKnown(string? status) => status is Open or Settled or Cancelled;
}

/// <summary>
/// Ligne du tableau des impayés. EmployeeName : vendeur. SettledAt, SettlementMethod et
/// SettledByName : renseignés quand la créance est soldée. PendingSettlementId : règlement
/// par carte en attente de réponse (la créance est alors bloquée).
/// </summary>
public sealed record ReceivableLine(
    Guid Id,
    Guid SaleId,
    string TicketNumber,
    string TicketBarcode,
    DateTimeOffset CreatedAt,
    Guid CustomerId,
    string CustomerName,
    decimal Amount,
    string Status,
    Guid EmployeeId,
    string EmployeeName,
    DateTimeOffset? SettledAt,
    string? SettlementMethod,
    string? SettledByName,
    Guid? SettlementId,
    Guid? PendingSettlementId);

/// <summary>
/// Indicateurs sur toutes les créances (indépendants des filtres du tableau).
/// TotalAmount : créances ouvertes et soldées, hors créances annulées.
/// </summary>
public sealed record ReceivableIndicators(
    decimal OpenAmount,
    int OpenCount,
    decimal TotalAmount,
    int TotalCount,
    decimal SettledAmount,
    int SettledCount);

public sealed record ReceivablesTable(ReceivableIndicators Indicators, IReadOnlyList<ReceivableLine> Lines);

/// <summary>
/// Règlement intégral d'une ou plusieurs créances du même client par un seul encaissement.
/// Amount doit être exactement la somme due. Method : ESPECES (AmountReceived obligatoire) ou TPE.
/// </summary>
public sealed record SettleReceivablesRequest(
    Guid OperationId,
    IReadOnlyList<Guid> ReceivableIds,
    string Method,
    decimal Amount,
    decimal? AmountReceived = null);

/// <summary>Status : CONFIRME (créances soldées), EN_ATTENTE (TPE, rien n'est soldé) ou ECHOUE.</summary>
public sealed record SettlementResult(
    Guid Id,
    Guid CustomerId,
    string CustomerName,
    string Method,
    string Status,
    decimal Amount,
    decimal? AmountReceived,
    decimal? ChangeGiven,
    Guid CashSessionId,
    Guid EmployeeId,
    string EmployeeName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConfirmedAt,
    IReadOnlyList<ReceivableLine> Receivables);
