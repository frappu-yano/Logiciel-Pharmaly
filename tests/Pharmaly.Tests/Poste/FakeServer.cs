using Pharmaly.Contracts;
using Pharmaly.Poste.Services;

namespace Pharmaly.Tests.Poste;

internal sealed class FakeServer : IServerClient
{
    public static readonly ProductSummary Creme = new(Guid.NewGuid(), 100001, "Crème hydratante 50 ml", 1850.10m, "2000000000015");
    public static readonly ProductSummary Serum = new(Guid.NewGuid(), 100002, "Sérum nuit 30 ml", 4200.20m, "2000000000022");

    private readonly ProductSummary[] catalogue = [Creme, Serum];

    public bool IsDown { get; set; }

    public Task<HealthResponse?> GetHealthAsync(CancellationToken cancellationToken) =>
        IsDown
            ? throw new HttpRequestException("serveur coupé")
            : Task.FromResult<HealthResponse?>(new HealthResponse("OK", "0.1.0", true, 1, DateTimeOffset.Now));

    public Task<IReadOnlyList<ProductSummary>> SearchProductsAsync(string term, CancellationToken cancellationToken) =>
        IsDown
            ? throw new HttpRequestException("serveur coupé")
            : Task.FromResult<IReadOnlyList<ProductSummary>>(
                catalogue.Where(product => product.Name.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList());

    public Task<ProductSummary?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken) =>
        IsDown
            ? throw new HttpRequestException("serveur coupé")
            : Task.FromResult(catalogue.FirstOrDefault(product => product.Barcode == barcode));
}
