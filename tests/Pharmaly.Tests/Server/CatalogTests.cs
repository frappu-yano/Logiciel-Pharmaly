using System.Net;
using System.Net.Http.Json;
using Dapper;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Access;

namespace Pharmaly.Tests.Server;

public sealed class CatalogTests(ServerFixture server) : IClassFixture<ServerFixture>, IAsyncLifetime
{
    private readonly StockTestKit kit = new(server);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>Après chaque scénario : somme des mouvements = solde, sur toute la base.</summary>
    public async ValueTask DisposeAsync() => await StockInvariant.AssertHoldsAsync(server.DataSource);

    private static CreateProductRequest NewProduct(
        string name, string[]? barcodes = null, string? brand = null, string? category = null,
        decimal price = 1850m, bool soldByUnit = true, Guid? operationId = null) =>
        new(operationId ?? Guid.NewGuid(), name, barcodes ?? [StockTestKit.NewBarcode()], brand, category, price, soldByUnit);

    private async Task<ProductSummary> CreateAsync(CreateProductRequest request)
    {
        var response = await server.AdminClient.PostAsJsonAsync(ApiRoutes.Products, request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProductSummary>())!;
    }

    private async Task<ProductDetail> DetailAsync(Guid id) =>
        (await server.AdminClient.GetFromJsonAsync<ProductDetail>(ApiRoutes.Product(id)))!;

    private async Task<List<ProductSummary>> SearchAsync(string term) =>
        (await server.AdminClient.GetFromJsonAsync<List<ProductSummary>>(
            $"{ApiRoutes.ProductSearch}?q={Uri.EscapeDataString(term)}"))!;

    private static UpdateProductRequest Update(
        string name, string? brand = null, string? category = null, bool isActive = true,
        string[]? add = null, string[]? remove = null) =>
        new(Guid.NewGuid(), name, brand, category, isActive, add, remove);

    // ---------- Création ----------

    [Fact]
    public async Task Un_produit_cree_a_ses_codes_barres_sa_marque_sa_categorie_et_son_prix_historise()
    {
        var first = StockTestKit.NewBarcode();
        var second = StockTestKit.NewBarcode();

        var created = await CreateAsync(NewProduct(
            "  Crème réparatrice 40 ml ", [first, second], "Dermalia", "Soins du visage", 2350.50m));

        Assert.Equal("Crème réparatrice 40 ml", created.Name);
        Assert.Equal(first, created.Barcode);
        Assert.Equal(0m, created.SellableStock);
        Assert.True(created.Code >= 100_000);

        var detail = await DetailAsync(created.Id);
        Assert.Equal("Dermalia", detail.Brand);
        Assert.Equal("Soins du visage", detail.Category);
        Assert.Equal(2350.50m, detail.SalePrice);
        Assert.True(detail.SoldByUnit);
        Assert.True(detail.IsActive);
        Assert.Equal([first, second], detail.Barcodes);
        var price = Assert.Single(detail.SalePriceHistory);
        Assert.Null(price.OldPrice);
        Assert.Equal(2350.50m, price.NewPrice);
        Assert.Equal("Administratrice Test", price.EmployeeName);
        Assert.Equal(1, await kit.CountAuditAsync(AuditActions.ProductCreated, created.Id.ToString()));
        Assert.Contains(await SearchAsync("creme reparatrice"), product => product.Id == created.Id);
    }

    [Fact]
    public async Task Marque_et_categorie_sont_creees_a_la_volee_une_seule_fois()
    {
        await CreateAsync(NewProduct("Gel douche surgras 200 ml", brand: "Novaléa", category: "Hygiène"));
        await CreateAsync(NewProduct("Gel douche surgras 400 ml", brand: " novaléa ", category: "HYGIÈNE"));

        await using var connection = await server.DataSource.OpenConnectionAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM brands WHERE lower(name) = 'novaléa'"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM categories WHERE lower(name) = 'hygiène'"));
    }

    [Fact]
    public async Task Une_creation_rejouee_ne_cree_qu_un_produit_et_renvoie_la_meme_reponse()
    {
        var request = NewProduct("Baume à lèvres rejoué");

        var first = await CreateAsync(request);
        var replays = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => CreateAsync(request))));

        Assert.All(replays, replay => Assert.Equal(first, replay));
        await using var connection = await server.DataSource.OpenConnectionAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM products WHERE name = 'Baume à lèvres rejoué'"));
    }

    [Fact]
    public async Task Une_creation_invalide_est_refusee_sans_rien_enregistrer()
    {
        var existing = await CreateAsync(NewProduct("Produit au code déjà pris"));
        var invalid = new[]
        {
            NewProduct("   "),
            NewProduct("Sans code-barres", barcodes: []),
            NewProduct("Code-barres trop court", barcodes: ["12"]),
            NewProduct("Prix négatif", price: -1m),
            NewProduct("Prix à trois décimales", price: 10.005m),
            NewProduct("Sans identifiant d'opération", operationId: Guid.Empty),
        };

        foreach (var request in invalid)
        {
            await StockTestKit.AssertErrorAsync(
                await server.AdminClient.PostAsJsonAsync(ApiRoutes.Products, request),
                HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        }

        var duplicate = await server.AdminClient.PostAsJsonAsync(
            ApiRoutes.Products, NewProduct("Doublon de code-barres", barcodes: [existing.Barcode!]));
        var error = await StockTestKit.AssertErrorAsync(duplicate, HttpStatusCode.Conflict, ApiErrorCodes.Conflict);
        Assert.Contains("Produit au code déjà pris", error.Message);

        await using var connection = await server.DataSource.OpenConnectionAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM products WHERE name IN ('Sans code-barres', 'Prix négatif', 'Doublon de code-barres')"));
    }

    // ---------- Modification ----------

    [Fact]
    public async Task Modifier_un_produit_change_sa_fiche_et_ses_codes_barres_mais_jamais_son_prix()
    {
        var original = StockTestKit.NewBarcode();
        var added = StockTestKit.NewBarcode();
        var created = await CreateAsync(NewProduct("Lait corporel 250 ml", [original], "Alvéo", "Corps", 990m));

        var response = await server.AdminClient.PutAsJsonAsync(
            ApiRoutes.Product(created.Id),
            Update("Lait corporel nourrissant 250 ml", "Sanovita", null, add: [added], remove: [original]));

        response.EnsureSuccessStatusCode();
        var summary = (await response.Content.ReadFromJsonAsync<ProductSummary>())!;
        Assert.Equal("Lait corporel nourrissant 250 ml", summary.Name);
        Assert.Equal(added, summary.Barcode);
        Assert.Equal(990m, summary.SalePrice);

        var detail = await DetailAsync(created.Id);
        Assert.Equal("Sanovita", detail.Brand);
        Assert.Null(detail.Category);
        Assert.Equal([added], detail.Barcodes);
        Assert.Single(detail.SalePriceHistory);
        Assert.Equal(1, await kit.CountAuditAsync(AuditActions.ProductUpdated, created.Id.ToString()));
        Assert.Contains(await SearchAsync("nourrissant"), product => product.Id == created.Id);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await server.AdminClient.GetAsync($"{ApiRoutes.ProductByBarcode}/{original}")).StatusCode);
    }

    [Fact]
    public async Task Un_prix_glisse_dans_la_modification_d_un_produit_est_ignore()
    {
        var created = await CreateAsync(NewProduct("Huile sèche 100 ml", price: 3100m));

        var response = await server.AdminClient.PutAsJsonAsync(
            ApiRoutes.Product(created.Id),
            new { operationId = Guid.NewGuid(), name = "Huile sèche 100 ml", isActive = true, salePrice = 1m, newPrice = 1m });

        response.EnsureSuccessStatusCode();
        Assert.Equal(3100m, (await DetailAsync(created.Id)).SalePrice);
    }

    [Fact]
    public async Task Un_produit_desactive_disparait_de_la_recherche_mais_garde_sa_fiche()
    {
        var created = await CreateAsync(NewProduct("Déodorant retiré du catalogue"));

        (await server.AdminClient.PutAsJsonAsync(
            ApiRoutes.Product(created.Id), Update("Déodorant retiré du catalogue", isActive: false))).EnsureSuccessStatusCode();

        Assert.DoesNotContain(await SearchAsync("deodorant retire"), product => product.Id == created.Id);
        Assert.False((await DetailAsync(created.Id)).IsActive);
    }

    [Fact]
    public async Task Une_modification_de_codes_barres_incoherente_est_refusee()
    {
        var other = await CreateAsync(NewProduct("Produit voisin"));
        var created = await CreateAsync(NewProduct("Produit à modifier"));
        var requests = new (UpdateProductRequest Request, HttpStatusCode Status, string Code)[]
        {
            (Update("Produit à modifier", remove: [created.Barcode!]), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest),
            (Update("Produit à modifier", remove: [other.Barcode!]), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest),
            (Update("Produit à modifier", add: [other.Barcode!]), HttpStatusCode.Conflict, ApiErrorCodes.Conflict),
            (Update("Produit à modifier", add: ["x"]), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest),
            (Update(""), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest),
        };

        foreach (var (request, status, code) in requests)
        {
            await StockTestKit.AssertErrorAsync(
                await server.AdminClient.PutAsJsonAsync(ApiRoutes.Product(created.Id), request), status, code);
        }

        await StockTestKit.AssertErrorAsync(
            await server.AdminClient.PutAsJsonAsync(ApiRoutes.Product(Guid.NewGuid()), Update("Inconnu")),
            HttpStatusCode.NotFound, ApiErrorCodes.NotFound);
        Assert.Equal([created.Barcode!], (await DetailAsync(created.Id)).Barcodes);
        Assert.Equal(0, await kit.CountAuditAsync(AuditActions.ProductUpdated, created.Id.ToString()));
    }

    // ---------- Prix de vente ----------

    [Fact]
    public async Task Le_changement_de_prix_de_vente_est_historise_audite_et_idempotent()
    {
        var created = await CreateAsync(NewProduct("Sérum éclat 30 ml", price: 4200m));
        var request = new ChangeSalePriceRequest(Guid.NewGuid(), 4500.50m);

        var first = await server.AdminClient.PutAsJsonAsync(ApiRoutes.ProductSalePrice(created.Id), request);
        var replay = await server.AdminClient.PutAsJsonAsync(ApiRoutes.ProductSalePrice(created.Id), request);

        var expected = new SalePriceChangeResult(created.Id, 4200m, 4500.50m);
        Assert.Equal(expected, await first.Content.ReadFromJsonAsync<SalePriceChangeResult>());
        Assert.Equal(expected, await replay.Content.ReadFromJsonAsync<SalePriceChangeResult>());

        var detail = await DetailAsync(created.Id);
        Assert.Equal(4500.50m, detail.SalePrice);
        Assert.Equal(2, detail.SalePriceHistory.Count);
        Assert.Equal((4200m, 4500.50m), (detail.SalePriceHistory[0].OldPrice, detail.SalePriceHistory[0].NewPrice));
        Assert.Equal(1, await kit.CountAuditAsync(AuditActions.SalePriceChanged, created.Id.ToString()));

        await StockTestKit.AssertErrorAsync(
            await server.AdminClient.PutAsJsonAsync(
                ApiRoutes.ProductSalePrice(created.Id), new ChangeSalePriceRequest(Guid.NewGuid(), -5m)),
            HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        await StockTestKit.AssertErrorAsync(
            await server.AdminClient.PutAsJsonAsync(
                ApiRoutes.ProductSalePrice(Guid.NewGuid()), new ChangeSalePriceRequest(Guid.NewGuid(), 5m)),
            HttpStatusCode.NotFound, ApiErrorCodes.NotFound);
    }

    [Fact]
    public async Task Le_prix_de_vente_n_est_pas_modifiable_sans_le_droit_dedie()
    {
        var created = await CreateAsync(NewProduct("Écran solaire 50 ml", price: 2800m));
        var (editor, _) = await kit.CreateEmployeeClientAsync(
            Permissions.ProductsView, Permissions.ProductsCreate, Permissions.ProductsEdit);
        var (pricer, pricerId) = await kit.CreateEmployeeClientAsync(Permissions.SalePriceEdit);

        var refused = await editor.PutAsJsonAsync(
            ApiRoutes.ProductSalePrice(created.Id), new ChangeSalePriceRequest(Guid.NewGuid(), 1m));

        await StockTestKit.AssertErrorAsync(refused, HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        Assert.Equal(2800m, (await DetailAsync(created.Id)).SalePrice);

        (await pricer.PutAsJsonAsync(ApiRoutes.ProductSalePrice(created.Id), new ChangeSalePriceRequest(Guid.NewGuid(), 2950m)))
            .EnsureSuccessStatusCode();
        Assert.Equal(2950m, (await DetailAsync(created.Id)).SalePrice);
        await using var connection = await server.DataSource.OpenConnectionAsync();
        Assert.Equal(pricerId, await connection.ExecuteScalarAsync<Guid>(
            "SELECT employee_id FROM product_sale_prices WHERE product_id = @Id ORDER BY id DESC LIMIT 1", new { created.Id }));
    }

    // ---------- Permissions ----------

    [Fact]
    public async Task Chaque_route_du_catalogue_est_refusee_sans_le_droit_correspondant()
    {
        var created = await CreateAsync(NewProduct("Produit protégé", price: 700m));
        var (viewer, viewerId) = await kit.CreateEmployeeClientAsync(Permissions.ProductsView);
        var (stranger, _) = await kit.CreateEmployeeClientAsync(Permissions.SalesCreate);
        var (creator, _) = await kit.CreateEmployeeClientAsync(Permissions.ProductsCreate);

        var refusals = new[]
        {
            await viewer.PostAsJsonAsync(ApiRoutes.Products, NewProduct("Création interdite")),
            await viewer.PutAsJsonAsync(ApiRoutes.Product(created.Id), Update("Renommage interdit")),
            await creator.PutAsJsonAsync(ApiRoutes.Product(created.Id), Update("Renommage interdit")),
            await viewer.PutAsJsonAsync(ApiRoutes.ProductSalePrice(created.Id), new ChangeSalePriceRequest(Guid.NewGuid(), 1m)),
            await stranger.GetAsync(ApiRoutes.Product(created.Id)),
            await stranger.GetAsync($"{ApiRoutes.ProductSearch}?q=protege"),
            await creator.GetAsync(ApiRoutes.Product(created.Id)),
        };

        foreach (var response in refusals)
        {
            await StockTestKit.AssertErrorAsync(response, HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        }

        var detail = await viewer.GetFromJsonAsync<ProductDetail>(ApiRoutes.Product(created.Id));
        Assert.Equal("Produit protégé", detail!.Name);
        Assert.Equal(700m, detail.SalePrice);
        await using var connection = await server.DataSource.OpenConnectionAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM products WHERE name = 'Création interdite'"));
        Assert.Equal(3, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM audit_logs WHERE action = @action AND employee_id = @viewerId",
            new { action = AuditActions.PermissionDenied, viewerId }));
        (await creator.PostAsJsonAsync(ApiRoutes.Products, NewProduct("Création autorisée"))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task L_administrateur_cree_a_l_installation_recoit_les_droits_de_stock()
    {
        var identity = await server.AdminClient.GetFromJsonAsync<CurrentIdentityResponse>(ApiRoutes.CurrentIdentity);

        Assert.Contains(Permissions.StockAdjust, identity!.Permissions);
        Assert.Contains(Permissions.StockTransfer, identity.Permissions);
        Assert.Equal(Permissions.All.Count, identity.Permissions.Count);
    }

    // ---------- Stock dans la recherche et la fiche ----------

    [Fact]
    public async Task La_recherche_renvoie_le_stock_vendable_du_site_du_poste_uniquement()
    {
        var created = await CreateAsync(NewProduct("Shampooing fortifiant unique 400 ml"));
        await kit.SetStockAsync(created.Id, await kit.MainLocationAsync(), 7);
        await kit.SetStockAsync(created.Id, await kit.SetAsideLocationAsync(), 2);
        await kit.SetStockAsync(created.Id, await kit.OtherStoreLocationAsync(), 4);

        var found = Assert.Single(await SearchAsync("shampooing fortifiant unique"));
        var scanned = await server.AdminClient.GetFromJsonAsync<ProductSummary>($"{ApiRoutes.ProductByBarcode}/{created.Barcode}");

        Assert.Equal(7m, found.SellableStock);
        Assert.Equal(7m, scanned!.SellableStock);
    }

    [Fact]
    public async Task La_fiche_produit_montre_le_stock_par_emplacement_et_les_derniers_mouvements()
    {
        var main = await kit.MainLocationAsync();
        var aside = await kit.SetAsideLocationAsync();
        var created = await CreateAsync(NewProduct("Masque purifiant 75 ml"));
        await kit.SetStockAsync(created.Id, main, 10);
        (await StockTestKit.TransferAsync(server.AdminClient, main, aside, null, (created.Id, 4))).EnsureSuccessStatusCode();

        var detail = await DetailAsync(created.Id);

        // Tous les emplacements des deux sites figurent, même sans stock.
        Assert.Equal(4, detail.Stock.Count);
        Assert.Equal(6m, detail.Stock.Single(line => line.LocationId == main).Quantity);
        Assert.Equal(4m, detail.Stock.Single(line => line.LocationId == aside).Quantity);
        Assert.Equal(StockLocationKinds.SetAside, detail.Stock.Single(line => line.LocationId == aside).LocationKind);
        Assert.Equal(0m, detail.Stock.Where(line => line.StoreId == server.OtherStoreId).Sum(line => line.Quantity));

        // Du plus récent au plus ancien.
        Assert.Equal(
            [StockMovementTypes.TransferIn, StockMovementTypes.TransferOut, StockMovementTypes.Adjustment],
            detail.RecentMovements.Select(movement => movement.Type));
        var transferOut = detail.RecentMovements[1];
        Assert.Equal((-4m, 6m, main), (transferOut.Quantity, transferOut.BalanceAfter, transferOut.LocationId));
        Assert.Equal("Administratrice Test", transferOut.EmployeeName);
        Assert.Equal("Inventaire d'essai", detail.RecentMovements[2].Note);
        Assert.True(DateTimeOffset.UtcNow - transferOut.OccurredAt < TimeSpan.FromMinutes(5));

        await StockTestKit.AssertErrorAsync(
            await server.AdminClient.GetAsync(ApiRoutes.Product(Guid.NewGuid())), HttpStatusCode.NotFound, ApiErrorCodes.NotFound);
    }
}
