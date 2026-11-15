using Dapper;
using Npgsql;
using NpgsqlTypes;
using Pharmaly.Domain;

namespace Pharmaly.Server.Catalog;

/// <summary>
/// Remplit une base VIDE avec un catalogue fictif, pour les essais de rapidité.
/// Les codes-barres utilisent le préfixe 200, réservé aux usages internes : ils ne
/// correspondent à aucun produit réel.
/// </summary>
public sealed class DemoDataSeeder(NpgsqlDataSource dataSource)
{
    private const string DemoBarcodePrefix = "200";

    private static readonly string[] Kinds =
    [
        "Crème hydratante", "Gel nettoyant", "Sérum", "Shampooing", "Lait corporel", "Écran solaire",
        "Baume à lèvres", "Eau micellaire", "Masque", "Huile sèche", "Déodorant", "Dentifrice",
        "Vitamine C", "Magnésium", "Savon surgras", "Lotion tonique", "Contour des yeux", "Après-shampooing",
    ];

    private static readonly string[] Ranges =
    [
        "Sensitive", "Hydra", "Pure", "Repair", "Solaire", "Bébé", "Homme", "Intense", "Nuit", "Jour",
        "Anti-âge", "Éclat", "Apaisant", "Nutri", "Fresh",
    ];

    private static readonly string[] Brands =
    [
        "Dermalia", "Alvéo", "Sanovita", "Clairderm", "Novaléa", "Botaniq", "Pharmasoin", "Lumiskin",
    ];

    private static readonly string[] Sizes = ["30 ml", "50 ml", "75 ml", "100 ml", "150 ml", "200 ml", "250 ml", "400 ml"];

    public async Task SeedAsync(int productCount, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        if (await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM products)"))
        {
            throw new InvalidOperationException(
                "La base contient déjà des produits. Les données fictives ne se chargent que dans une base vide.");
        }

        // Générateur à graine fixe : le même catalogue fictif à chaque chargement.
        var random = new Random(20261115);
        var products = Enumerable.Range(0, productCount).Select(index =>
        {
            var name = $"{Brands[random.Next(Brands.Length)]} {Kinds[random.Next(Kinds.Length)]} "
                       + $"{Ranges[random.Next(Ranges.Length)]} {Sizes[random.Next(Sizes.Length)]} n°{index + 1}";
            var price = random.Next(20, 900) * 10m;
            var barcode = Ean13.WithCheckDigit(DemoBarcodePrefix + index.ToString("D9"));
            return (Id: Guid.NewGuid(), Name: name, Price: price, Barcode: barcode);
        }).ToList();

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var writer = await connection.BeginBinaryImportAsync(
                         "COPY products (id, name, search_name, sale_price) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var product in products)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(product.Id, NpgsqlDbType.Uuid, cancellationToken);
                await writer.WriteAsync(product.Name, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(TextNormalizer.ToSearchKey(product.Name), NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(product.Price, NpgsqlDbType.Numeric, cancellationToken);
            }

            await writer.CompleteAsync(cancellationToken);
        }

        await using (var writer = await connection.BeginBinaryImportAsync(
                         "COPY product_barcodes (barcode, product_id, is_primary) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var product in products)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(product.Barcode, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(product.Id, NpgsqlDbType.Uuid, cancellationToken);
                await writer.WriteAsync(true, NpgsqlDbType.Boolean, cancellationToken);
            }

            await writer.CompleteAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        await connection.ExecuteAsync("ANALYZE products; ANALYZE product_barcodes;");
    }
}
