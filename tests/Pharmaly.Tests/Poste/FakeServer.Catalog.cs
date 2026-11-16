using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Tests.Poste;

internal sealed partial class FakeServer : ICatalogClient
{
    public static readonly ProductSummary Creme = new(Guid.NewGuid(), 100001, "Crème hydratante 50 ml", 1850.10m, "2000000000015");
    public static readonly ProductSummary Serum = new(Guid.NewGuid(), 100002, "Sérum nuit 30 ml", 4200.20m, "2000000000022");

    private readonly ProductSummary[] catalogue = [Creme, Serum];

    /// <summary>Stock vendable de chaque produit sur le site du poste ; les ventes le font baisser.</summary>
    public Dictionary<Guid, decimal> StockLevels { get; } = new() { [Creme.Id] = 10, [Serum.Id] = 10 };

    /// <summary>Prix de vente modifiés depuis le début du test (un autre poste a changé un prix).</summary>
    public Dictionary<Guid, decimal> PriceChanges { get; } = [];

    /// <summary>Le produit tel que le serveur le renvoie maintenant : prix et stock du moment.</summary>
    private ProductSummary Current(ProductSummary product) => product with
    {
        SalePrice = PriceChanges.GetValueOrDefault(product.Id, product.SalePrice),
        SellableStock = StockLevels.GetValueOrDefault(product.Id),
    };

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
            catalogue
                .Where(product => product.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || product.Code.ToString() == term)
                .Select(Current).ToList());
    }

    public Task<ProductSummary?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.ProductsView);
        return Task.FromResult(catalogue.Where(product => product.Barcode == barcode).Select(Current).FirstOrDefault());
    }
}
