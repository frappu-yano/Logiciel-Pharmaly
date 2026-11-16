using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Server.Access;

namespace Pharmaly.Tests.Server;

/// <summary>
/// Contrôle d'intégrité financière, sur TOUTE la base : à appeler à la fin de chaque scénario
/// de vente, de caisse ou d'impayé, avec <see cref="StockInvariant"/>.
/// </summary>
public static class MoneyInvariant
{
    private static readonly (string Rule, string Sql)[] Rules =
    [
        ("le total d'un ticket = somme de ses lignes − remise",
            """
            SELECT count(*) FROM sales s
            WHERE s.total <> s.subtotal - s.discount_amount OR s.discount_amount > s.subtotal
               OR s.subtotal <> (SELECT COALESCE(sum(l.line_total), 0) FROM sale_lines l WHERE l.sale_id = s.id)
               OR NOT EXISTS (SELECT 1 FROM sale_lines l WHERE l.sale_id = s.id)
            """),
        ("une vente comptant validée a exactement un paiement confirmé, égal à son total",
            """
            SELECT count(*) FROM sales s
            WHERE s.status = 'VALIDEE' AND s.payment_mode IN ('ESPECES', 'TPE')
              AND (SELECT COALESCE(sum(p.amount), -1) FROM payments p
                   WHERE p.sale_id = s.id AND p.status = 'CONFIRME' AND p.method = s.payment_mode) <> s.total
            """),
        ("un paiement confirmé appartient à une vente validée, un paiement en attente à une vente en attente de TPE",
            """
            SELECT count(*) FROM payments p JOIN sales s ON s.id = p.sale_id
            WHERE (p.status = 'CONFIRME') <> (s.status = 'VALIDEE')
               OR (p.status = 'EN_ATTENTE') <> (s.status = 'EN_ATTENTE_TPE')
               OR (p.status IN ('ECHOUE', 'REMBOURSE') AND s.status <> 'ANNULEE')
               OR p.amount <> s.total
            """),
        ("une vente en attente de TPE a un paiement en attente",
            """
            SELECT count(*) FROM sales s
            WHERE s.status = 'EN_ATTENTE_TPE'
              AND NOT EXISTS (SELECT 1 FROM payments p WHERE p.sale_id = s.id AND p.status = 'EN_ATTENTE')
            """),
        ("une vente impayée n'a aucun paiement et a exactement une créance, de son montant, pour son client",
            """
            SELECT count(*) FROM sales s
            WHERE s.payment_mode = 'IMPAYE'
              AND (EXISTS (SELECT 1 FROM payments p WHERE p.sale_id = s.id)
                   OR (SELECT count(*) FROM receivables r
                       WHERE r.sale_id = s.id AND r.amount = s.total AND r.customer_id = s.customer_id) <> 1)
            """),
        ("seule une vente impayée a une créance",
            "SELECT count(*) FROM receivables r JOIN sales s ON s.id = r.sale_id WHERE s.payment_mode <> 'IMPAYE'"),
        ("une créance est annulée si et seulement si sa vente l'est",
            """
            SELECT count(*) FROM receivables r JOIN sales s ON s.id = r.sale_id
            WHERE (r.status = 'ANNULEE') <> (s.status = 'ANNULEE')
            """),
        ("une vente est annulée si et seulement si elle a une opération d'annulation",
            """
            SELECT count(*) FROM sales s
            WHERE (s.status = 'ANNULEE') <> EXISTS (SELECT 1 FROM sale_cancellations x WHERE x.sale_id = s.id)
            """),
        ("une créance soldée a exactement un règlement confirmé ; une créance non soldée n'en a aucun",
            """
            SELECT count(*) FROM receivables r
            WHERE (SELECT count(*) FROM receivable_settlement_items i
                   JOIN receivable_settlements st ON st.id = i.settlement_id
                   WHERE i.receivable_id = r.id AND st.status = 'CONFIRME')
                  <> CASE WHEN r.status = 'SOLDEE' THEN 1 ELSE 0 END
            """),
        ("une ligne de règlement est active si et seulement si son règlement n'a pas échoué",
            """
            SELECT count(*) FROM receivable_settlement_items i JOIN receivable_settlements st ON st.id = i.settlement_id
            WHERE i.is_active <> (st.status IN ('EN_ATTENTE', 'CONFIRME'))
            """),
        ("une créance figure dans au plus un règlement actif",
            """
            SELECT count(*) FROM (SELECT receivable_id FROM receivable_settlement_items WHERE is_active
                                  GROUP BY receivable_id HAVING count(*) > 1) doubles
            """),
        ("un règlement vaut exactement la somme de ses créances, réglées en entier, toutes du même client",
            """
            SELECT count(*) FROM receivable_settlements st
            WHERE st.amount <> (SELECT COALESCE(sum(i.amount), 0) FROM receivable_settlement_items i WHERE i.settlement_id = st.id)
               OR EXISTS (SELECT 1 FROM receivable_settlement_items i JOIN receivables r ON r.id = i.receivable_id
                          WHERE i.settlement_id = st.id AND (i.amount <> r.amount OR r.customer_id <> st.customer_id))
            """),
        ("une vente en espèces annulée et encaissée est remboursée une fois, du montant encaissé",
            """
            SELECT count(*) FROM payments p
            WHERE p.status = 'REMBOURSE' AND p.amount > 0
              AND ((p.method = 'ESPECES' AND (SELECT COALESCE(sum(m.amount), 0) FROM cash_movements m
                                               WHERE m.sale_id = p.sale_id AND m.kind = 'REMBOURSEMENT_VENTE') <> p.amount)
                   OR NOT EXISTS (SELECT 1 FROM sale_cancellations x
                                  WHERE x.sale_id = p.sale_id AND x.refund_method = p.method AND x.refund_amount = p.amount))
            """),
        ("aucun remboursement sans paiement remboursé",
            """
            SELECT count(*) FROM cash_movements m
            WHERE m.kind = 'REMBOURSEMENT_VENTE'
              AND NOT EXISTS (SELECT 1 FROM payments p WHERE p.sale_id = m.sale_id AND p.status = 'REMBOURSE' AND p.amount = m.amount)
            """),
        ("tout encaissement est rattaché à une session du même compte de caisse et du même site",
            """
            SELECT (SELECT count(*) FROM payments p JOIN cash_sessions cs ON cs.id = p.cash_session_id
                    WHERE cs.register_account_id <> p.register_account_id OR cs.store_id <> p.store_id)
                 + (SELECT count(*) FROM receivable_settlements st JOIN cash_sessions cs ON cs.id = st.cash_session_id
                    WHERE cs.register_account_id <> st.register_account_id OR cs.store_id <> st.store_id)
                 + (SELECT count(*) FROM cash_movements m JOIN cash_sessions cs ON cs.id = m.cash_session_id
                    WHERE cs.register_account_id <> m.register_account_id OR cs.store_id <> m.store_id)
                 + (SELECT count(*) FROM sales s JOIN cash_sessions cs ON cs.id = s.cash_session_id
                    WHERE cs.register_account_id <> s.register_account_id OR cs.store_id <> s.store_id)
            """),
        ("une seule session ouverte par compte de caisse",
            """
            SELECT count(*) FROM (SELECT register_account_id FROM cash_sessions WHERE status = 'OUVERTE'
                                  GROUP BY register_account_id HAVING count(*) > 1) doubles
            """),
        ("chaque ligne vendue a exactement un mouvement VENTE de sa quantité, compensé une fois si la vente est annulée",
            """
            SELECT count(*) FROM sale_lines l JOIN sales s ON s.id = l.sale_id
            WHERE (SELECT count(*) FROM stock_movements m
                   WHERE m.source_type = 'VENTE' AND m.source_id = s.id::text AND m.product_id = l.product_id
                     AND m.location_id = l.location_id AND m.movement_type = 'VENTE' AND m.quantity = -l.quantity) <> 1
               OR (SELECT count(*) FROM stock_movements m
                   WHERE m.source_type = 'VENTE' AND m.source_id = s.id::text AND m.product_id = l.product_id
                     AND m.location_id = l.location_id AND m.movement_type = 'ANNULATION_VENTE' AND m.quantity = l.quantity)
                  <> CASE WHEN s.status = 'ANNULEE' THEN 1 ELSE 0 END
               OR (SELECT count(*) FROM stock_movements m
                   WHERE m.source_type = 'VENTE' AND m.source_id = s.id::text AND m.product_id = l.product_id)
                  <> CASE WHEN s.status = 'ANNULEE' THEN 2 ELSE 1 END
            """),
        ("aucun mouvement de vente sans ligne de ticket",
            """
            SELECT count(*) FROM stock_movements m
            WHERE m.movement_type IN ('VENTE', 'ANNULATION_VENTE')
              AND NOT EXISTS (SELECT 1 FROM sale_lines l
                              WHERE l.sale_id::text = m.source_id AND l.product_id = m.product_id AND m.source_type = 'VENTE')
            """),
        ("les numéros de ticket d'un site se suivent sans trou ni doublon",
            """
            SELECT count(*) FROM (SELECT store_id, count(*) AS tickets, max(ticket_seq) AS last, min(ticket_seq) AS first
                                  FROM sales GROUP BY store_id) t
            WHERE t.first <> 1 OR t.last <> t.tickets
            """),
    ];

    public static async Task AssertHoldsAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        // Une seule photographie : d'autres séries de tests n'écrivent pas dans cette base.
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        foreach (var (rule, sql) in Rules)
        {
            var violations = await connection.ExecuteScalarAsync<long>(sql, transaction: transaction);
            Assert.True(violations == 0, $"Intégrité financière rompue ({violations}) : {rule}.");
        }
    }
}

/// <summary>Gestes communs aux tests des ventes, de la caisse et des impayés.</summary>
public sealed class SalesTestKit(ServerFixture server)
{
    /// <summary>
    /// Les séries des ventes, de la caisse et des impayés passent l'une après l'autre : chacune
    /// lance déjà des dizaines de demandes simultanées sur le même PostgreSQL.
    /// </summary>
    public const string Collection = "Ventes, caisse et impayés";

    public const string EmployeeSecret = "731946";
    private static int nextEmployeeNumber = 7000;
    private static int nextRegister;

    public StockTestKit Stock { get; } = new(server);

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Réponse {(int)response.StatusCode} : {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    // ---------- Caisse ----------

    public static Task<HttpResponseMessage> OpenSessionAsync(HttpClient client, decimal openingFloat = 5000m, Guid? operationId = null) =>
        client.PostAsJsonAsync(CashRoutes.OpenSession, new OpenCashSessionRequest(operationId ?? Guid.NewGuid(), openingFloat));

    /// <summary>La caisse partagée des tests (« caisse1 ») est ouverte une fois pour la série.</summary>
    public async Task EnsureSessionOpenAsync()
    {
        var current = await server.AdminClient.GetFromJsonAsync<CurrentCashSessionResponse>(CashRoutes.CurrentSession);
        if (current!.Session is null)
        {
            (await OpenSessionAsync(server.AdminClient)).EnsureSuccessStatusCode();
        }
    }

    public async Task<CashSessionSummary> SummaryAsync(HttpClient? client = null) =>
        await ReadAsync<CashSessionSummary>(await (client ?? server.AdminClient).GetAsync(CashRoutes.SessionSummary));

    /// <summary>Nouveau compte de caisse du site principal, sans session ouverte.</summary>
    public async Task<(string Login, string Password)> CreateRegisterAsync()
    {
        var login = $"caisse-essai-{Interlocked.Increment(ref nextRegister)}";
        var password = await server.Services.GetRequiredService<SiteSetup>().CreateRegisterAccountAsync(login, $"Caisse {login}", "D1");
        return (login, password);
    }

    /// <summary>Poste d'un nouveau compte de caisse (sans session ouverte), administrateur identifié.</summary>
    public async Task<HttpClient> CreateAdminOnNewRegisterAsync()
    {
        var (login, password) = await CreateRegisterAsync();
        return await AdminOnRegisterAsync(login, password);
    }

    public async Task<HttpClient> AdminOnRegisterAsync(string login, string password)
    {
        var client = await server.CreatePosteClientAsync(login, password);
        await ServerFixture.IdentifyAsync(client, ServerFixture.AdminNumber, server.AdminSecret);
        return client;
    }

    // ---------- Employés ----------

    /// <summary>Nouvel employé avec ces droits ; son matricule permet de l'identifier sur n'importe quel poste.</summary>
    public async Task<(int Number, Guid Id)> CreateEmployeeAsync(params string[] permissions)
    {
        var number = Interlocked.Increment(ref nextEmployeeNumber);
        var created = await server.AdminClient.PostAsJsonAsync(
            ApiRoutes.Employees, new CreateEmployeeRequest(number, $"Employé {number}", EmployeeSecret));
        var employee = await ReadAsync<EmployeeSummary>(created);
        if (permissions.Length > 0)
        {
            (await server.AdminClient.PutAsJsonAsync(
                $"{ApiRoutes.Employees}/{employee.Id}/permissions",
                new SetPermissionsRequest(permissions.Select(code => new PermissionGrant(code, null)).ToList()))).EnsureSuccessStatusCode();
        }

        return (number, employee.Id);
    }

    /// <summary>Nouveau poste de la caisse partagée, avec un nouvel employé possédant exactement ces droits.</summary>
    public async Task<(HttpClient Client, Guid EmployeeId)> CreateEmployeeClientAsync(params string[] permissions)
    {
        var (number, id) = await CreateEmployeeAsync(permissions);
        var client = await server.CreatePosteClientAsync();
        await ServerFixture.IdentifyAsync(client, number, EmployeeSecret);
        return (client, id);
    }

    // ---------- Produits et clients ----------

    /// <summary>Produit neuf avec ce stock vendable sur le site principal.</summary>
    public async Task<ProductSummary> CreateProductAsync(decimal stock, decimal price = 1200m, bool soldByUnit = true)
    {
        var product = await Stock.CreateProductAsync(price: price, soldByUnit: soldByUnit);
        if (stock > 0)
        {
            await Stock.SetStockAsync(product.Id, await Stock.MainLocationAsync(), stock);
        }

        return product;
    }

    public async Task<decimal> StockAsync(Guid productId) =>
        await Stock.ReadLevelAsync(productId, await Stock.MainLocationAsync());

    public async Task<CustomerSummary> CreateCustomerAsync(string? name = null, string? phone = null) =>
        await ReadAsync<CustomerSummary>(await server.AdminClient.PostAsJsonAsync(
            CustomerRoutes.Customers, new CreateCustomerRequest(Guid.NewGuid(), name ?? $"Client {Guid.NewGuid():N}", phone)));

    // ---------- Ventes ----------

    public static Task<HttpResponseMessage> SellAsync(HttpClient client, CreateSaleRequest request) =>
        client.PostAsJsonAsync(SaleRoutes.Sales, request);

    public static CreateSaleRequest Cash(Guid productId, decimal quantity = 1, decimal received = 1_000_000m, Guid? operationId = null) =>
        new(operationId ?? Guid.NewGuid(), [new SaleLineRequest(productId, quantity)], SalePaymentModes.Cash, AmountReceived: received);

    public static CreateSaleRequest Card(Guid productId, decimal quantity = 1) =>
        new(Guid.NewGuid(), [new SaleLineRequest(productId, quantity)], SalePaymentModes.Card);

    public static CreateSaleRequest Unpaid(Guid productId, Guid? customerId, decimal quantity = 1) =>
        new(Guid.NewGuid(), [new SaleLineRequest(productId, quantity)], SalePaymentModes.Unpaid, CustomerId: customerId);

    public async Task<SaleDetail> SellOkAsync(CreateSaleRequest request, HttpClient? client = null) =>
        await ReadAsync<SaleDetail>(await SellAsync(client ?? server.AdminClient, request));

    /// <summary>Vente impayée d'un produit neuf à ce prix : crée une créance de ce montant.</summary>
    public async Task<SaleDetail> SellUnpaidAsync(Guid customerId, decimal amount, HttpClient? client = null)
    {
        var product = await CreateProductAsync(stock: 1, price: amount);
        return await SellOkAsync(Unpaid(product.Id, customerId), client);
    }

    public static Task<HttpResponseMessage> CardResultAsync(HttpClient client, Guid saleId, bool accepted, Guid? operationId = null) =>
        client.PostAsJsonAsync(SaleRoutes.CardResult(saleId), new CardResultRequest(operationId ?? Guid.NewGuid(), accepted));

    public static Task<HttpResponseMessage> CancelAsync(
        HttpClient client, Guid saleId, string reason = "Erreur de caisse", Guid? operationId = null) =>
        client.PostAsJsonAsync(SaleRoutes.Cancellation(saleId), new CancelSaleRequest(operationId ?? Guid.NewGuid(), reason));

    public async Task<SaleDetail> ReadSaleAsync(Guid saleId) =>
        await ReadAsync<SaleDetail>(await server.AdminClient.GetAsync(SaleRoutes.Sale(saleId)));

    // ---------- Impayés ----------

    public static Task<HttpResponseMessage> SettleAsync(
        HttpClient client, string method, decimal amount, Guid? operationId = null, params Guid[] receivableIds) =>
        client.PostAsJsonAsync(
            ReceivableRoutes.Settlements,
            new SettleReceivablesRequest(
                operationId ?? Guid.NewGuid(), receivableIds, method, amount,
                method == PaymentMethods.Cash ? amount : null));

    public static Task<HttpResponseMessage> SettlementCardResultAsync(HttpClient client, Guid settlementId, bool accepted) =>
        client.PostAsJsonAsync(
            ReceivableRoutes.SettlementCardResult(settlementId), new CardResultRequest(Guid.NewGuid(), accepted));

    public async Task<ReceivablesTable> ReceivablesAsync(string query = "") =>
        await ReadAsync<ReceivablesTable>(await server.AdminClient.GetAsync(ReceivableRoutes.Receivables + query));

    public async Task<ReceivableLine> ReceivableAsync(SaleDetail sale) =>
        await ReadAsync<ReceivableLine>(await server.AdminClient.GetAsync(ReceivableRoutes.ByTicket(sale.TicketNumber)));

    // ---------- Lecture directe de la base ----------

    public async Task<T> ScalarAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = await server.DataSource.OpenConnectionAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
    }

    public Task<int> CountSaleMovementsAsync(Guid saleId, string? type = null) =>
        ScalarAsync<int>(
            """
            SELECT count(*) FROM stock_movements
            WHERE source_type = 'VENTE' AND source_id = @id AND (@type::text IS NULL OR movement_type = @type)
            """,
            new { id = saleId.ToString(), type });

    public Task<long> CountMovementsAsync() => ScalarAsync<long>("SELECT count(*) FROM stock_movements");

    public static Task<T[]> InParallelAsync<T>(int count, Func<int, Task<T>> action) =>
        Task.WhenAll(Enumerable.Range(0, count).Select(index => Task.Run(() => action(index)))).WaitAsync(TimeSpan.FromSeconds(90));
}
