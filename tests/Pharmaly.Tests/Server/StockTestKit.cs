using System.Net;
using System.Net.Http.Json;
using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Access;

namespace Pharmaly.Tests.Server;

/// <summary>
/// Contrôle permanent du stock, sur TOUTE la base : à appeler à la fin de chaque scénario.
/// </summary>
public static class StockInvariant
{
    public static async Task AssertHoldsAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();

        var negative = await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM stock_levels WHERE quantity < 0");
        Assert.True(negative == 0, $"{negative} solde(s) négatif(s).");

        var sumMismatch = await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(*)
            FROM stock_levels sl
            LEFT JOIN (SELECT product_id, location_id, sum(quantity) AS total
                       FROM stock_movements GROUP BY product_id, location_id) m
                   ON m.product_id = sl.product_id AND m.location_id = sl.location_id
            WHERE sl.quantity <> COALESCE(m.total, 0)
            """);
        Assert.True(sumMismatch == 0, $"{sumMismatch} solde(s) différent(s) de la somme de leurs mouvements.");

        var lastMismatch = await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(*)
            FROM stock_levels sl
            JOIN LATERAL (SELECT m.balance_after FROM stock_movements m
                          WHERE m.product_id = sl.product_id AND m.location_id = sl.location_id
                          ORDER BY m.id DESC LIMIT 1) last ON true
            WHERE last.balance_after <> sl.quantity
            """);
        Assert.True(lastMismatch == 0, $"{lastMismatch} solde(s) différent(s) du « solde après » de leur dernier mouvement.");

        // Chaque « solde après » est le cumul des mouvements qui le précèdent.
        var chainMismatch = await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(*) FROM (
                SELECT balance_after,
                       sum(quantity) OVER (PARTITION BY product_id, location_id ORDER BY id) AS running
                FROM stock_movements) m
            WHERE m.balance_after <> m.running OR m.balance_after < 0
            """);
        Assert.True(chainMismatch == 0, $"{chainMismatch} mouvement(s) dont le « solde après » ne suit pas le journal.");
    }
}

/// <summary>Gestes communs aux tests du catalogue et du stock.</summary>
public sealed class StockTestKit(ServerFixture server)
{
    private const string EmployeeSecret = "482615";
    private static int nextBarcode = 500_000;
    private static int nextEmployeeNumber = 5000;

    public static string NewBarcode() =>
        Ean13.WithCheckDigit("200" + Interlocked.Increment(ref nextBarcode).ToString("D9"));

    public async Task<Guid> LocationIdAsync(Guid storeId, string code)
    {
        await using var connection = await server.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<Guid>(
            "SELECT id FROM stock_locations WHERE store_id = @storeId AND code = @code", new { storeId, code });
    }

    public Task<Guid> MainLocationAsync() => LocationIdAsync(server.StoreId, SiteSetup.MainLocationCode);

    public Task<Guid> SetAsideLocationAsync() => LocationIdAsync(server.StoreId, SiteSetup.SetAsideLocationCode);

    public Task<Guid> OtherStoreLocationAsync() => LocationIdAsync(server.OtherStoreId, SiteSetup.MainLocationCode);

    public async Task<ProductSummary> CreateProductAsync(string? name = null, bool soldByUnit = true, decimal price = 1200m)
    {
        var barcode = NewBarcode();
        var response = await server.AdminClient.PostAsJsonAsync(
            ApiRoutes.Products,
            new CreateProductRequest(Guid.NewGuid(), name ?? $"Produit d'essai {barcode}", [barcode], null, null, price, soldByUnit));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProductSummary>())!;
    }

    /// <summary>Nouveau poste avec un nouvel employé possédant exactement ces droits.</summary>
    public async Task<(HttpClient Client, Guid EmployeeId)> CreateScopedEmployeeClientAsync(params PermissionGrant[] grants)
    {
        var number = Interlocked.Increment(ref nextEmployeeNumber);
        var created = await server.AdminClient.PostAsJsonAsync(
            ApiRoutes.Employees, new CreateEmployeeRequest(number, $"Employé {number}", EmployeeSecret));
        created.EnsureSuccessStatusCode();
        var employee = (await created.Content.ReadFromJsonAsync<EmployeeSummary>())!;
        if (grants.Length > 0)
        {
            (await server.AdminClient.PutAsJsonAsync(
                $"{ApiRoutes.Employees}/{employee.Id}/permissions", new SetPermissionsRequest(grants))).EnsureSuccessStatusCode();
        }

        var client = await server.CreatePosteClientAsync();
        await ServerFixture.IdentifyAsync(client, number, EmployeeSecret);
        return (client, employee.Id);
    }

    public Task<(HttpClient Client, Guid EmployeeId)> CreateEmployeeClientAsync(params string[] permissions) =>
        CreateScopedEmployeeClientAsync(permissions.Select(code => new PermissionGrant(code, null)).ToArray());

    public static Task<HttpResponseMessage> AdjustByAsync(
        HttpClient client, Guid productId, Guid locationId, decimal change, Guid? operationId = null) =>
        client.PostAsJsonAsync(
            ApiRoutes.StockAdjustments,
            new StockAdjustmentRequest(operationId ?? Guid.NewGuid(), productId, locationId, null, change, "Essai automatique"));

    public static Task<HttpResponseMessage> CountAsync(HttpClient client, Guid productId, Guid locationId, decimal counted) =>
        client.PostAsJsonAsync(
            ApiRoutes.StockAdjustments,
            new StockAdjustmentRequest(Guid.NewGuid(), productId, locationId, counted, null, "Inventaire d'essai"));

    /// <summary>Met le stock à cette quantité, par un ajustement d'inventaire de l'administrateur.</summary>
    public async Task SetStockAsync(Guid productId, Guid locationId, decimal quantity) =>
        (await CountAsync(server.AdminClient, productId, locationId, quantity)).EnsureSuccessStatusCode();

    public static Task<HttpResponseMessage> TransferAsync(
        HttpClient client, Guid from, Guid to, Guid? operationId = null, params (Guid ProductId, decimal Quantity)[] lines) =>
        client.PostAsJsonAsync(
            ApiRoutes.StockTransfers,
            new StockTransferRequest(
                operationId ?? Guid.NewGuid(), from, to,
                lines.Select(line => new StockTransferLine(line.ProductId, line.Quantity)).ToList(), null));

    public async Task<decimal> ReadLevelAsync(Guid productId, Guid locationId)
    {
        await using var connection = await server.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<decimal?>(
            "SELECT quantity FROM stock_levels WHERE product_id = @productId AND location_id = @locationId",
            new { productId, locationId }) ?? 0m;
    }

    public async Task<int> CountMovementsAsync(Guid productId, string? type = null)
    {
        await using var connection = await server.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM stock_movements WHERE product_id = @productId AND (@type::text IS NULL OR movement_type = @type)",
            new { productId, type });
    }

    public async Task<int> CountAuditAsync(string action, string entityId)
    {
        await using var connection = await server.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM audit_logs WHERE action = @action AND entity_id = @entityId", new { action, entityId });
    }

    public static async Task<ApiError> AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiError>())!;
        Assert.Equal(code, error.Code);
        return error;
    }
}
