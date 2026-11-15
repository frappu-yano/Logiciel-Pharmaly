using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Catalog;
using Pharmaly.Server.Database;

namespace Pharmaly.Tests.Server;

public sealed class ServerTests(ServerFixture server) : IClassFixture<ServerFixture>, IAsyncLifetime
{
    private const string CremeBarcode = "2000000000015";
    private const string SerumBarcode = "2000000000022";

    public async ValueTask InitializeAsync()
    {
        await using var connection = await server.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("TRUNCATE products CASCADE");
        await InsertProductAsync(connection, "Crème Hydratante Visage 50 ml", 1850.00m, CremeBarcode);
        await InsertProductAsync(connection, "Sérum Nuit Anti-âge 30 ml", 4200.50m, SerumBarcode);
        await InsertProductAsync(connection, "Gel 100% naturel", 990m, "2000000000039");
        await InsertProductAsync(connection, "Ancien shampooing retiré", 500m, "2000000000046", isActive: false);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task InsertProductAsync(NpgsqlConnection connection, string name, decimal price, string barcode, bool isActive = true)
    {
        var id = Guid.NewGuid();
        await connection.ExecuteAsync(
            "INSERT INTO products (id, name, search_name, sale_price, is_active) VALUES (@id, @name, @searchName, @price, @isActive)",
            new { id, name, searchName = TextNormalizer.ToSearchKey(name), price, isActive });
        await connection.ExecuteAsync(
            "INSERT INTO product_barcodes (barcode, product_id, is_primary) VALUES (@barcode, @id, true)",
            new { barcode, id });
    }

    private async Task<List<ProductSummary>> SearchAsync(string term) =>
        (await server.AdminClient.GetFromJsonAsync<List<ProductSummary>>(
            $"{ApiRoutes.ProductSearch}?q={Uri.EscapeDataString(term)}"))!;

    [Fact]
    public async Task Le_serveur_repond_et_la_base_est_migree()
    {
        var health = await server.Client.GetFromJsonAsync<HealthResponse>(ApiRoutes.Health);

        Assert.NotNull(health);
        Assert.Equal("OK", health.Status);
        Assert.True(health.DatabaseReachable);
        Assert.True(health.SchemaVersion >= 2);
    }

    [Fact]
    public async Task Rejouer_les_migrations_ne_change_rien()
    {
        var runner = server.Services.GetRequiredService<MigrationRunner>();

        Assert.Equal(0, await runner.ApplyAsync());
        Assert.Equal(0, await runner.ApplyAsync());
    }

    [Theory]
    [InlineData("creme")]
    [InlineData("CRÈME hydra")]
    [InlineData("visage creme")]
    public async Task La_recherche_ignore_accents_casse_et_ordre_des_mots(string term)
    {
        var found = await SearchAsync(term);

        var product = Assert.Single(found);
        Assert.Equal("Crème Hydratante Visage 50 ml", product.Name);
        Assert.Equal(1850.00m, product.SalePrice);
        Assert.Equal(CremeBarcode, product.Barcode);
    }

    [Fact]
    public async Task La_recherche_par_debut_de_code_barres_trouve_le_produit()
    {
        var found = await SearchAsync(SerumBarcode[..11]);

        Assert.Contains(found, product => product.Barcode == SerumBarcode);
    }

    [Fact]
    public async Task Un_produit_inactif_n_est_jamais_propose()
    {
        Assert.Empty(await SearchAsync("shampooing"));

        var response = await server.AdminClient.GetAsync($"{ApiRoutes.ProductByBarcode}/2000000000046");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Les_caracteres_joker_et_les_saisies_vides_ne_renvoient_pas_tout_le_catalogue(string term)
    {
        var found = await SearchAsync(term);

        Assert.True(found.Count <= 1, $"« {term} » a renvoyé {found.Count} produits");
    }

    [Fact]
    public async Task Le_signe_pourcent_est_cherche_tel_quel()
    {
        var product = Assert.Single(await SearchAsync("100%"));
        Assert.Equal("Gel 100% naturel", product.Name);
    }

    [Fact]
    public async Task Un_code_barres_exact_renvoie_le_produit_et_son_prix_exact()
    {
        var product = await server.AdminClient.GetFromJsonAsync<ProductSummary>($"{ApiRoutes.ProductByBarcode}/{SerumBarcode}");

        Assert.NotNull(product);
        Assert.Equal(4200.50m, product.SalePrice);
    }

    [Fact]
    public async Task Un_code_barres_inconnu_renvoie_introuvable()
    {
        var response = await server.AdminClient.GetAsync($"{ApiRoutes.ProductByBarcode}/0000000000000");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task La_base_refuse_un_code_barres_en_double_et_un_prix_negatif()
    {
        await using var connection = await server.DataSource.OpenConnectionAsync();

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertProductAsync(connection, "Doublon", 100m, CremeBarcode));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

        var negative = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertProductAsync(connection, "Prix négatif", -1m, "2000000000091"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, negative.SqlState);
    }

    [Fact]
    public async Task Les_donnees_fictives_ne_se_chargent_que_dans_une_base_vide()
    {
        var seeder = server.Services.GetRequiredService<DemoDataSeeder>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.SeedAsync(10));

        await using var connection = await server.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("TRUNCATE products CASCADE");
        await seeder.SeedAsync(500);

        Assert.Equal(500, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM products"));
        Assert.Equal(500, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM product_barcodes"));
        var barcodes = await connection.QueryAsync<string>("SELECT barcode FROM product_barcodes");
        Assert.All(barcodes, barcode => Assert.True(Ean13.IsValid(barcode)));
    }
}
