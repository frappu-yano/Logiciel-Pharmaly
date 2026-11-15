using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;

namespace Pharmaly.Server.Catalog;

public sealed class ProductQueries(NpgsqlDataSource dataSource)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;
    private const int MaxTermLength = 80;
    private const int MinDigitsForCodeSearch = 4;

    private const string SummaryColumns =
        """
        p.id AS Id, p.internal_code AS Code, p.name AS Name, p.sale_price AS SalePrice,
        (SELECT b.barcode FROM product_barcodes b WHERE b.product_id = p.id
          ORDER BY b.is_primary DESC, b.barcode LIMIT 1) AS Barcode
        """;

    public async Task<IReadOnlyList<ProductSummary>> SearchAsync(string? term, int limit, CancellationToken cancellationToken)
    {
        var cleaned = (term ?? string.Empty).Trim();
        if (cleaned.Length == 0)
        {
            return [];
        }

        if (cleaned.Length > MaxTermLength)
        {
            cleaned = cleaned[..MaxTermLength];
        }

        var boundedLimit = Math.Clamp(limit, 1, MaxLimit);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        if (cleaned.Length >= MinDigitsForCodeSearch && cleaned.All(char.IsAsciiDigit))
        {
            var byCode = await connection.QueryAsync<ProductSummary>(new CommandDefinition(
                $"""
                 WITH matches AS (
                     SELECT p.id FROM products p WHERE p.internal_code = @code
                     UNION
                     (SELECT b.product_id FROM product_barcodes b
                      WHERE b.barcode LIKE @prefix ORDER BY b.barcode LIMIT @limit)
                 )
                 SELECT {SummaryColumns}
                 FROM products p
                 JOIN matches ON matches.id = p.id
                 WHERE p.is_active
                 ORDER BY p.name
                 LIMIT @limit
                 """,
                new { code = long.TryParse(cleaned, out var code) ? code : -1, prefix = EscapeLike(cleaned) + "%", limit = boundedLimit },
                cancellationToken: cancellationToken));
            return byCode.AsList();
        }

        // Chaque mot saisi doit apparaître dans le libellé, dans n'importe quel ordre.
        var words = TextNormalizer.ToSearchKey(cleaned).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var patterns = words.Select(word => "%" + EscapeLike(word) + "%").ToArray();
        var byName = await connection.QueryAsync<ProductSummary>(new CommandDefinition(
            $"""
             SELECT {SummaryColumns}
             FROM products p
             WHERE p.is_active AND p.search_name LIKE ALL (@patterns)
             ORDER BY (p.search_name LIKE @startsWith) DESC, p.name
             LIMIT @limit
             """,
            new { patterns, startsWith = EscapeLike(words[0]) + "%", limit = boundedLimit },
            cancellationToken: cancellationToken));
        return byName.AsList();
    }

    public async Task<ProductSummary?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<ProductSummary>(new CommandDefinition(
            $"""
             SELECT {SummaryColumns}
             FROM products p
             JOIN product_barcodes scanned ON scanned.product_id = p.id
             WHERE scanned.barcode = @barcode AND p.is_active
             """,
            new { barcode = barcode.Trim() },
            cancellationToken: cancellationToken));
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
