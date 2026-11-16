using Pharmaly.Contracts;

namespace Pharmaly.Poste.Services.Server;

/// <summary>Module « catalogue » : recherche, fiche, création, modification et prix de vente des produits.</summary>
public interface ICatalogClient
{
    Task<IReadOnlyList<ProductSummary>> SearchProductsAsync(string term, CancellationToken cancellationToken);

    Task<ProductSummary?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken);

    /// <summary>Fiche complète : codes-barres, stock par emplacement, derniers mouvements, historique des prix.</summary>
    Task<ProductDetail> GetProductAsync(Guid productId, CancellationToken cancellationToken);

    Task<ProductSummary> CreateProductAsync(CreateProductRequest request, CancellationToken cancellationToken);

    Task<ProductSummary> UpdateProductAsync(Guid productId, UpdateProductRequest request, CancellationToken cancellationToken);

    Task<SalePriceChangeResult> ChangeSalePriceAsync(Guid productId, ChangeSalePriceRequest request, CancellationToken cancellationToken);
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

    public async Task<ProductDetail> GetProductAsync(Guid productId, CancellationToken cancellationToken) =>
        (await transport.SendAsync<ProductDetail>(HttpMethod.Get, ApiRoutes.Product(productId), null, cancellationToken))!;

    public async Task<ProductSummary> CreateProductAsync(CreateProductRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<ProductSummary>(HttpMethod.Post, ApiRoutes.Products, request, cancellationToken))!;

    public async Task<ProductSummary> UpdateProductAsync(
        Guid productId, UpdateProductRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<ProductSummary>(HttpMethod.Put, ApiRoutes.Product(productId), request, cancellationToken))!;

    public async Task<SalePriceChangeResult> ChangeSalePriceAsync(
        Guid productId, ChangeSalePriceRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<SalePriceChangeResult>(
            HttpMethod.Put, ApiRoutes.ProductSalePrice(productId), request, cancellationToken))!;
}
