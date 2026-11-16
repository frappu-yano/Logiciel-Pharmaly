using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Pharmaly.Contracts;
using Pharmaly.Server.Access;

namespace Pharmaly.Tests.Server;

/// <summary>
/// Un magasin neuf, à part : ses caisses, son stock et sa journée n'appartiennent qu'au test
/// qui le crée. Les montants d'un rapport de clôture y sont donc exacts.
/// </summary>
public sealed record TestStore(Guid Id, string Code, Guid MainLocationId, Guid SetAsideLocationId);

/// <summary>Gestes communs aux tests des retours et de la clôture.</summary>
public sealed class ClosureTestKit(ServerFixture server)
{
    private static int nextStore;
    private static int nextRegister;

    public SalesTestKit Sales { get; } = new(server);

    // ---------- Magasins et caisses ----------

    public async Task<TestStore> CreateStoreAsync()
    {
        var code = $"T{Interlocked.Increment(ref nextStore)}";
        var id = await server.Services.GetRequiredService<SiteSetup>().InitializeSiteAsync(code, $"Magasin d'essai {code}", "MAGASIN");
        return new TestStore(
            id, code,
            await Sales.Stock.LocationIdAsync(id, SiteSetup.MainLocationCode),
            await Sales.Stock.LocationIdAsync(id, SiteSetup.SetAsideLocationCode));
    }

    /// <summary>Nouveau compte de caisse de ce magasin (caisse fermée).</summary>
    public async Task<(string Login, string Password)> CreateRegisterAsync(TestStore store)
    {
        var login = $"caisse-cloture-{Interlocked.Increment(ref nextRegister)}";
        var password = await server.Services.GetRequiredService<SiteSetup>().CreateRegisterAccountAsync(login, $"Caisse {login}", store.Code);
        return (login, password);
    }

    /// <summary>Nouvelle caisse de ce magasin, administrateur identifié ; ouverte si un fond de caisse est donné.</summary>
    public async Task<HttpClient> CreateRegisterClientAsync(TestStore store, decimal? openingFloat = 1000m)
    {
        var (login, password) = await CreateRegisterAsync(store);
        var client = await Sales.AdminOnRegisterAsync(login, password);
        if (openingFloat is { } amount)
        {
            await OpenAsync(client, amount);
        }

        return client;
    }

    /// <summary>Poste d'une caisse existante, avec un nouvel employé possédant exactement ces droits.</summary>
    public async Task<HttpClient> EmployeeOnRegisterAsync((string Login, string Password) register, params string[] permissions)
    {
        var (number, _) = await Sales.CreateEmployeeAsync(permissions);
        var client = await server.CreatePosteClientAsync(register.Login, register.Password);
        await ServerFixture.IdentifyAsync(client, number, SalesTestKit.EmployeeSecret);
        return client;
    }

    public static async Task<CashSessionInfo> OpenAsync(HttpClient client, decimal openingFloat) =>
        await SalesTestKit.ReadAsync<CashSessionInfo>(await SalesTestKit.OpenSessionAsync(client, openingFloat));

    // ---------- Produits ----------

    /// <summary>Produit neuf avec ce stock vendable dans ce magasin.</summary>
    public async Task<ProductSummary> ProductAsync(TestStore store, decimal price, decimal stock = 100, bool soldByUnit = true)
    {
        var product = await Sales.Stock.CreateProductAsync(price: price, soldByUnit: soldByUnit);
        await Sales.Stock.SetStockAsync(product.Id, store.MainLocationId, stock);
        return product;
    }

    /// <summary>Vente d'un produit neuf : « quantity » unités à « price ».</summary>
    public async Task<SaleDetail> SellAsync(
        HttpClient client, TestStore store, decimal price, string mode = SalePaymentModes.Cash, decimal quantity = 1,
        Guid? customerId = null, string? discountKind = null, decimal? discountValue = null)
    {
        var product = await ProductAsync(store, price);
        return await SalesTestKit.ReadAsync<SaleDetail>(await SalesTestKit.SellAsync(client, new CreateSaleRequest(
            Guid.NewGuid(), [new SaleLineRequest(product.Id, quantity)], mode, discountKind, discountValue,
            mode == SalePaymentModes.Cash ? 10_000_000m : null, customerId)));
    }

    // ---------- Retours ----------

    public async Task<IReadOnlyList<ReturnReason>> ReasonsAsync(bool all = false) =>
        (await server.AdminClient.GetFromJsonAsync<List<ReturnReason>>(ReturnRoutes.Reasons + (all ? "?tous=true" : string.Empty)))!;

    public async Task<ReturnReason> ReasonAsync(string label) =>
        (await ReasonsAsync(all: true)).Single(reason => reason.Label == label);

    public static Task<HttpResponseMessage> ReturnAsync(
        HttpClient client, Guid saleId, Guid reasonId, Guid? operationId = null, decimal? expectedRefund = null,
        params (Guid ProductId, decimal Quantity)[] lines) =>
        client.PostAsJsonAsync(
            ReturnRoutes.Returns,
            new CreateReturnRequest(
                operationId ?? Guid.NewGuid(), saleId, reasonId,
                lines.Select(line => new ReturnLineRequest(line.ProductId, line.Quantity)).ToList(), null, expectedRefund));

    /// <summary>Retour d'une quantité de la première ligne du ticket.</summary>
    public static Task<HttpResponseMessage> ReturnFirstLineAsync(HttpClient client, SaleDetail sale, Guid reasonId, decimal quantity = 1) =>
        ReturnAsync(client, sale.Id, reasonId, null, null, (sale.Lines[0].ProductId, quantity));

    public async Task<ReturnDetail> ReturnOkAsync(HttpClient client, SaleDetail sale, string reasonLabel, decimal quantity = 1) =>
        await SalesTestKit.ReadAsync<ReturnDetail>(
            await ReturnFirstLineAsync(client, sale, (await ReasonAsync(reasonLabel)).Id, quantity));

    // ---------- Fermeture et clôture ----------

    public static Task<HttpResponseMessage> CloseSessionAsync(HttpClient client, decimal countedCash, Guid? operationId = null) =>
        client.PostAsJsonAsync(ClosureRoutes.CloseSession, new CloseCashSessionRequest(operationId ?? Guid.NewGuid(), countedCash));

    /// <summary>Ferme la caisse en comptant exactement les espèces attendues, plus un écart éventuel.</summary>
    public static async Task<CashSessionReport> CloseSessionOkAsync(HttpClient client, decimal difference = 0m)
    {
        var expected = (await SalesTestKit.ReadAsync<CashSessionSummary>(await client.GetAsync(CashRoutes.SessionSummary))).ExpectedCash;
        return await SalesTestKit.ReadAsync<CashSessionReport>(await CloseSessionAsync(client, expected + difference));
    }

    public static Task<HttpResponseMessage> CloseDayAsync(
        HttpClient client, DateOnly? date = null, bool force = false, string? forceReason = null, Guid? operationId = null) =>
        client.PostAsJsonAsync(ClosureRoutes.Closures, new CloseDayRequest(operationId ?? Guid.NewGuid(), date, force, forceReason));

    public static async Task<DailyClosureDetail> CloseDayOkAsync(HttpClient client) =>
        await SalesTestKit.ReadAsync<DailyClosureDetail>(await CloseDayAsync(client));

    public static async Task<CurrentDayStatus> DayAsync(HttpClient client) =>
        await SalesTestKit.ReadAsync<CurrentDayStatus>(await client.GetAsync(ClosureRoutes.CurrentDay));

    /// <summary>Date du jour au magasin, selon l'horloge du serveur (heure d'Algérie).</summary>
    public DateOnly Today() => DateOnly.FromDateTime(server.Clock.GetUtcNow().ToOffset(TimeSpan.FromHours(1)).DateTime);

    /// <summary>Le serveur passe au lendemain.</summary>
    public void NextDay() => server.Clock.Offset += TimeSpan.FromDays(1);

    /// <summary>Nombre de lignes de ce magasin dans une table, selon leur journée comptable.</summary>
    public Task<long> CountDayRowsAsync(string table, Guid storeId, DateOnly? date) =>
        Sales.ScalarAsync<long>(
            $"SELECT count(*) FROM {table} WHERE store_id = @storeId AND business_date IS NOT DISTINCT FROM @date::date",
            new { storeId, date = date?.ToDateTime(TimeOnly.MinValue) });

    public static readonly string[] DayTables =
    [
        "sales", "payments", "receivable_settlements", "cash_movements", "sale_cancellations", "returns", "cash_sessions",
    ];
}
