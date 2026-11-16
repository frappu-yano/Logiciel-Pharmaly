using Pharmaly.Contracts;

namespace Pharmaly.Poste.Services.Server;

/// <summary>Module « catalogue » : recherche de produits.</summary>
public interface ICatalogClient
{
    Task<IReadOnlyList<ProductSummary>> SearchProductsAsync(string term, CancellationToken cancellationToken);

    Task<ProductSummary?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken);
}

public sealed class CatalogClient(IServerTransport transport) : ICatalogClient
{
    public async Task<IReadOnlyList<ProductSummary>> SearchProductsAsync(string term, CancellationToken cancellationToken) =>
        await transport.SendAsync<List<ProductSummary>>(
            HttpMethod.Get, $"{ApiRoutes.ProductSearch}?q={Uri.EscapeDataString(term)}", null, cancellationToken) ?? [];

    public Task<ProductSummary?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken) =>
        transport.SendAsync<ProductSummary>(
            HttpMethod.Get, $"{ApiRoutes.ProductByBarcode}/{Uri.EscapeDataString(barcode)}", null, cancellationToken,
            notFoundIsNull: true);
}
