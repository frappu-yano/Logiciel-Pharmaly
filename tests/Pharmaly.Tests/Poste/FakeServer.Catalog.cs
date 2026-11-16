using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Tests.Poste;

internal sealed partial class FakeServer : ICatalogClient
{
    public static readonly ProductSummary Creme = new(Guid.NewGuid(), 100001, "Crème hydratante 50 ml", 1850.10m, "2000000000015");
    public static readonly ProductSummary Serum = new(Guid.NewGuid(), 100002, "Sérum nuit 30 ml", 4200.20m, "2000000000022");

    private readonly ProductSummary[] catalogue = [Creme, Serum];

    /// <summary>Simule un employé sans le droit de consulter les produits.</summary>
    public bool DenyProducts
    {
        get => !EmployeePermissions.Contains(Permissions.ProductsView);
        set => EmployeePermissions = value
            ? EmployeePermissions.Where(code => code != Permissions.ProductsView).ToList()
            : EmployeePermissions.Append(Permissions.ProductsView).Distinct().ToList();
    }

    public Task<IReadOnlyList<ProductSummary>> SearchProductsAsync(string term, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.ProductsView);
        return Task.FromResult<IReadOnlyList<ProductSummary>>(
            catalogue.Where(product => product.Name.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    public Task<ProductSummary?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.ProductsView);
        return Task.FromResult(catalogue.FirstOrDefault(product => product.Barcode == barcode));
    }
}
