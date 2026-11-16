using System.Data;
using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;

namespace Pharmaly.Server.Catalog;

public sealed class ProductQueries(NpgsqlDataSource dataSource)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;
    public const int RecentMovementsLimit = 50;
    public const int PriceHistoryLimit = 50;
    private const int MaxTermLength = 80;
    private const int MinDigitsForCodeSearch = 4;

    // SellableStock : somme des emplacements vendables du site du poste (@storeId).
    private const string SummaryColumns =
        $"""
        p.id AS Id, p.internal_code AS Code, p.name AS Name, p.sale_price AS SalePrice,
        (SELECT b.barcode FROM product_barcodes b WHERE b.product_id = p.id
          ORDER BY b.is_primary DESC, b.barcode LIMIT 1) AS Barcode,
        COALESCE((SELECT sum(sl.quantity) FROM stock_levels sl
                  JOIN stock_locations l ON l.id = sl.location_id
                  WHERE sl.product_id = p.id AND l.store_id = @storeId
                    AND l.kind = '{StockLocationKinds.Sellable}'), 0) AS SellableStock
        """;

    public async Task<IReadOnlyList<ProductSummary>> SearchAsync(string? term, int limit, Guid storeId, CancellationToken cancellationToken)
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
                new
                {
                    code = long.TryParse(cleaned, out var code) ? code : -1,
                    prefix = EscapeLike(cleaned) + "%",
                    limit = boundedLimit,
                    storeId,
                },
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
            new { patterns, startsWith = EscapeLike(words[0]) + "%", limit = boundedLimit, storeId },
            cancellationToken: cancellationToken));
        return byName.AsList();
    }

    public async Task<ProductSummary?> FindByBarcodeAsync(string barcode, Guid storeId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<ProductSummary>(new CommandDefinition(
            $"""
             SELECT {SummaryColumns}
             FROM products p
             JOIN product_barcodes scanned ON scanned.product_id = p.id
             WHERE scanned.barcode = @barcode AND p.is_active
             """,
            new { barcode = barcode.Trim(), storeId },
            cancellationToken: cancellationToken));
    }

    /// <summary>Résumé d'un produit, actif ou non, lu dans la transaction de la commande en cours.</summary>
    public static Task<ProductSummary> ReadSummaryAsync(
        IDbConnection connection, IDbTransaction transaction, Guid productId, Guid storeId) =>
        connection.QuerySingleAsync<ProductSummary>(
            $"SELECT {SummaryColumns} FROM products p WHERE p.id = @productId", new { productId, storeId }, transaction);

    /// <summary>Fiche complète : détail, codes-barres, stock par emplacement, derniers mouvements, prix.</summary>
    public async Task<ProductDetail?> ReadDetailAsync(Guid productId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var parameters = new { productId };

        var product = await connection.QuerySingleOrDefaultAsync<ProductRow>(
            """
            SELECT p.id AS Id, p.internal_code AS Code, p.name AS Name, b.name AS Brand, c.name AS Category,
                   p.sale_price AS SalePrice, p.sold_by_unit AS SoldByUnit, p.is_active AS IsActive
            FROM products p
            LEFT JOIN brands b ON b.id = p.brand_id
            LEFT JOIN categories c ON c.id = p.category_id
            WHERE p.id = @productId
            """,
            parameters);
        if (product is null)
        {
            return null;
        }

        var barcodes = (await connection.QueryAsync<string>(
            "SELECT barcode FROM product_barcodes WHERE product_id = @productId ORDER BY is_primary DESC, barcode",
            parameters)).AsList();

        var stock = (await connection.QueryAsync<ProductStockLine>(
            """
            SELECT l.id AS LocationId, l.code AS LocationCode, l.name AS LocationName, l.kind AS LocationKind,
                   s.id AS StoreId, s.name AS StoreName, COALESCE(sl.quantity, 0) AS Quantity
            FROM stock_locations l
            JOIN stores s ON s.id = l.store_id
            LEFT JOIN stock_levels sl ON sl.location_id = l.id AND sl.product_id = @productId
            WHERE l.is_active OR sl.quantity > 0
            ORDER BY s.name, l.kind DESC, l.code
            """,
            parameters)).AsList();

        var movements = (await connection.QueryAsync<MovementRow>(
            """
            SELECT m.id AS Id, m.occurred_at AS OccurredAt, m.location_id AS LocationId, l.name AS LocationName,
                   m.movement_type AS Type, m.quantity AS Quantity, m.balance_after AS BalanceAfter,
                   e.full_name AS EmployeeName, m.source_type AS SourceType, m.source_id AS SourceId, m.note AS Note
            FROM stock_movements m
            JOIN stock_locations l ON l.id = m.location_id
            JOIN employees e ON e.id = m.employee_id
            WHERE m.product_id = @productId
            ORDER BY m.id DESC
            LIMIT @limit
            """,
            new { productId, limit = RecentMovementsLimit }))
            .Select(row => new StockMovementLine(
                row.Id, ToOffset(row.OccurredAt), row.LocationId, row.LocationName, row.Type, row.Quantity,
                row.BalanceAfter, row.EmployeeName, row.SourceType, row.SourceId, row.Note))
            .ToList();

        var prices = (await connection.QueryAsync<(DateTime ChangedAt, decimal? OldPrice, decimal NewPrice, string EmployeeName)>(
            """
            SELECT h.changed_at, h.old_price, h.new_price, e.full_name
            FROM product_sale_prices h
            JOIN employees e ON e.id = h.employee_id
            WHERE h.product_id = @productId
            ORDER BY h.id DESC
            LIMIT @limit
            """,
            new { productId, limit = PriceHistoryLimit }))
            .Select(row => new SalePriceHistoryLine(ToOffset(row.ChangedAt), row.OldPrice, row.NewPrice, row.EmployeeName))
            .ToList();

        return new ProductDetail(
            product.Id, product.Code, product.Name, product.Brand, product.Category, product.SalePrice,
            product.SoldByUnit, product.IsActive, barcodes, stock, movements, prices);
    }

    private static DateTimeOffset ToOffset(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed class MovementRow
    {
        public long Id { get; init; }
        public DateTime OccurredAt { get; init; }
        public Guid LocationId { get; init; }
        public string LocationName { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public decimal Quantity { get; init; }
        public decimal BalanceAfter { get; init; }
        public string EmployeeName { get; init; } = string.Empty;
        public string? SourceType { get; init; }
        public string? SourceId { get; init; }
        public string? Note { get; init; }
    }

    private sealed class ProductRow
    {
        public Guid Id { get; init; }
        public long Code { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? Brand { get; init; }
        public string? Category { get; init; }
        public decimal SalePrice { get; init; }
        public bool SoldByUnit { get; init; }
        public bool IsActive { get; init; }
    }
}
