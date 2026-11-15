namespace Pharmaly.Contracts;

public sealed record HealthResponse(
    string Status,
    string Version,
    bool DatabaseReachable,
    int SchemaVersion,
    DateTimeOffset ServerTime);

public sealed record ProductSummary(
    Guid Id,
    long Code,
    string Name,
    decimal SalePrice,
    string? Barcode);

public static class ApiRoutes
{
    public const string Health = "/sante";
    public const string ProductSearch = "/produits/recherche";
    public const string ProductByBarcode = "/produits/code-barres";
}
