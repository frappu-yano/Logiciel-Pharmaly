using System.Net;
using System.Net.Http.Json;
using Pharmaly.Contracts;
using Pharmaly.Domain;

namespace Pharmaly.Tests.Server;

/// <summary>Compléments du catalogue demandés par les écrans du poste.</summary>
public sealed class CatalogComplementsTests(ServerFixture server) : IClassFixture<ServerFixture>, IAsyncLifetime
{
    private readonly StockTestKit kit = new(server);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await StockInvariant.AssertHoldsAsync(server.DataSource);

    private async Task<ProductSummary> CreateAsync(string name, string barcode, string? brand = null, string? category = null, bool soldByUnit = true)
    {
        var response = await server.AdminClient.PostAsJsonAsync(
            ApiRoutes.Products, new CreateProductRequest(Guid.NewGuid(), name, [barcode], brand, category, 990m, soldByUnit));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProductSummary>())!;
    }

    private async Task DeactivateAsync(ProductSummary product) =>
        (await server.AdminClient.PutAsJsonAsync(
            ApiRoutes.Product(product.Id),
            new UpdateProductRequest(Guid.NewGuid(), product.Name, null, null, false, null, null))).EnsureSuccessStatusCode();

    [Fact]
    public async Task La_recherche_inclut_les_produits_inactifs_seulement_sur_demande()
    {
        var token = Guid.NewGuid().ToString("N")[..10];
        var activeBarcode = StockTestKit.NewBarcode();
        var inactiveBarcode = StockTestKit.NewBarcode();
        var active = await CreateAsync($"Sirop {token} actif", activeBarcode);
        var inactive = await CreateAsync($"Sirop {token} retiré", inactiveBarcode, soldByUnit: false);
        await kit.SetStockAsync(inactive.Id, await kit.MainLocationAsync(), 2.5m);
        await DeactivateAsync(inactive);

        // Par défaut : inchangé, les produits inactifs sont invisibles.
        var byDefault = await server.AdminClient.GetFromJsonAsync<List<ProductSummary>>($"{ApiRoutes.ProductSearch}?q={token}");
        var only = Assert.Single(byDefault!);
        Assert.Equal((active.Id, true, true), (only.Id, only.IsActive, only.SoldByUnit));
        Assert.Equal(
            HttpStatusCode.NotFound, (await server.AdminClient.GetAsync($"{ApiRoutes.ProductByBarcode}/{inactiveBarcode}")).StatusCode);
        var explicitlyOff = await server.AdminClient.GetFromJsonAsync<List<ProductSummary>>(
            $"{ApiRoutes.ProductSearch}?q={token}&{ApiRoutes.IncludeInactiveParameter}=false");
        Assert.Single(explicitlyOff!);

        // Sur demande : par nom, par code-barres saisi, par lecture du code-barres.
        var all = await server.AdminClient.GetFromJsonAsync<List<ProductSummary>>(
            $"{ApiRoutes.ProductSearch}?q={token}&{ApiRoutes.IncludeInactiveParameter}=true");
        Assert.Equal(2, all!.Count);
        var found = all.Single(product => product.Id == inactive.Id);
        Assert.Equal((false, false, 2.5m), (found.IsActive, found.SoldByUnit, found.SellableStock));
        var typed = await server.AdminClient.GetFromJsonAsync<List<ProductSummary>>(
            $"{ApiRoutes.ProductSearch}?q={inactiveBarcode}&{ApiRoutes.IncludeInactiveParameter}=true");
        Assert.Equal(inactive.Id, Assert.Single(typed!).Id);
        Assert.Empty((await server.AdminClient.GetFromJsonAsync<List<ProductSummary>>($"{ApiRoutes.ProductSearch}?q={inactiveBarcode}"))!);
        var scanned = await server.AdminClient.GetFromJsonAsync<ProductSummary>(
            $"{ApiRoutes.ProductByBarcode}/{inactiveBarcode}?{ApiRoutes.IncludeInactiveParameter}=true");
        Assert.Equal((inactive.Id, false), (scanned!.Id, scanned.IsActive));
        var scannedActive = await server.AdminClient.GetFromJsonAsync<ProductSummary>($"{ApiRoutes.ProductByBarcode}/{activeBarcode}");
        Assert.Equal((active.Id, true, true), (scannedActive!.Id, scannedActive.IsActive, scannedActive.SoldByUnit));
    }

    [Fact]
    public async Task L_identite_expose_le_site_du_poste_et_son_emplacement_vendable()
    {
        var mainLocation = await kit.MainLocationAsync();

        var me = await server.AdminClient.GetFromJsonAsync<CurrentIdentityResponse>(ApiRoutes.CurrentIdentity);
        Assert.Equal((server.StoreId, (Guid?)mainLocation), (me!.StoreId, me.SellableLocationId));

        // La réponse d'identification les donne aussi, sans demande supplémentaire.
        var poste = await server.CreatePosteClientAsync();
        var signedIn = await ServerFixture.IdentifyAsync(poste, ServerFixture.AdminNumber, server.AdminSecret);
        Assert.Equal((server.StoreId, (Guid?)mainLocation), (signedIn.StoreId, signedIn.SellableLocationId));
    }

    [Fact]
    public async Task Les_marques_et_les_categories_existantes_sont_listees_pour_qui_a_un_droit_sur_les_produits()
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        await CreateAsync($"Crème {token}", StockTestKit.NewBarcode(), $"Marque {token} B", $"Catégorie {token}");
        await CreateAsync($"Lait {token}", StockTestKit.NewBarcode(), $"Marque {token} A", $"Catégorie {token}");

        var brands = await server.AdminClient.GetFromJsonAsync<List<ReferenceName>>(ApiRoutes.ProductBrands);
        var categories = await server.AdminClient.GetFromJsonAsync<List<ReferenceName>>(ApiRoutes.ProductCategories);

        // Chaque nom une fois, par ordre alphabétique.
        Assert.Equal(
            new[] { $"Marque {token} A", $"Marque {token} B" },
            brands!.Select(brand => brand.Name).Where(name => name.Contains(token)));
        Assert.Single(categories!, category => category.Name == $"Catégorie {token}");
        Assert.All(brands!.Concat(categories!), reference => Assert.NotEqual(Guid.Empty, reference.Id));

        // N'importe quel droit sur les produits suffit ; sans aucun, refus.
        foreach (var permission in new[] { Permissions.ProductsView, Permissions.ProductsCreate, Permissions.ProductsEdit, Permissions.SalePriceEdit })
        {
            var (allowed, _) = await kit.CreateEmployeeClientAsync(permission);
            (await allowed.GetAsync(ApiRoutes.ProductBrands)).EnsureSuccessStatusCode();
            (await allowed.GetAsync(ApiRoutes.ProductCategories)).EnsureSuccessStatusCode();
        }

        var (other, _) = await kit.CreateEmployeeClientAsync(Permissions.SalesCreate);
        await StockTestKit.AssertErrorAsync(await other.GetAsync(ApiRoutes.ProductBrands), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        await StockTestKit.AssertErrorAsync(await other.GetAsync(ApiRoutes.ProductCategories), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
    }
}
