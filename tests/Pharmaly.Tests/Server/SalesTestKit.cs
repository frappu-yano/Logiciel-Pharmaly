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
        // ---------- Retours ----------
        ("un retour porte sur une vente validée, qu'on ne peut plus annuler",
            "SELECT count(*) FROM returns r JOIN sales s ON s.id = r.sale_id WHERE s.status <> 'VALIDEE' OR s.store_id <> r.store_id"),
        ("la quantité retournée d'une ligne ne dépasse jamais la quantité vendue",
            """
            SELECT count(*) FROM sale_lines l
            WHERE (SELECT COALESCE(sum(rl.quantity), 0) FROM return_lines rl WHERE rl.sale_line_id = l.id) > l.quantity
            """),
        ("une ligne de retour reprend le produit et le prix de sa ligne de ticket, sur le ticket du retour",
            """
            SELECT count(*) FROM return_lines rl
            JOIN returns r ON r.id = rl.return_id
            JOIN sale_lines l ON l.id = rl.sale_line_id
            WHERE l.sale_id <> r.sale_id OR l.product_id <> rl.product_id OR l.unit_price <> rl.unit_price
            """),
        ("le remboursement d'un retour = somme de ses lignes ; un retour a au moins une ligne",
            """
            SELECT count(*) FROM returns r
            WHERE r.refund_amount <> (SELECT COALESCE(sum(rl.refund_amount), -1) FROM return_lines rl WHERE rl.return_id = r.id)
               OR (r.refund_amount > 0) <> (r.refund_method IS NOT NULL)
            """),
        ("la somme des remboursements d'un ticket ne dépasse jamais son total, ni pour une ligne son montant",
            """
            SELECT (SELECT count(*) FROM sales s
                    WHERE (SELECT COALESCE(sum(r.refund_amount), 0) FROM returns r WHERE r.sale_id = s.id) > s.total)
                 + (SELECT count(*) FROM sale_lines l
                    WHERE (SELECT COALESCE(sum(rl.refund_amount), 0) FROM return_lines rl WHERE rl.sale_line_id = l.id) > l.line_total)
            """),
        ("un ticket entièrement retourné est remboursé exactement de son total",
            """
            SELECT count(*) FROM sales s
            WHERE EXISTS (SELECT 1 FROM returns r WHERE r.sale_id = s.id)
              AND NOT EXISTS (SELECT 1 FROM sale_lines l
                              WHERE (SELECT COALESCE(sum(rl.quantity), 0) FROM return_lines rl WHERE rl.sale_line_id = l.id) < l.quantity)
              AND (SELECT sum(r.refund_amount) FROM returns r WHERE r.sale_id = s.id) <> s.total
            """),
        ("un retour rembourse par le moyen qui a encaissé ; jamais sur une créance non soldée",
            """
            SELECT count(*) FROM returns r JOIN sales s ON s.id = r.sale_id
            WHERE (s.payment_mode IN ('ESPECES', 'TPE') AND r.refund_method IS NOT NULL AND r.refund_method <> s.payment_mode)
               OR (s.payment_mode = 'IMPAYE'
                   AND NOT EXISTS (SELECT 1 FROM receivables c
                                   JOIN receivable_settlement_items i ON i.receivable_id = c.id
                                   JOIN receivable_settlements st ON st.id = i.settlement_id
                                   WHERE c.sale_id = s.id AND c.status = 'SOLDEE' AND st.status = 'CONFIRME'
                                     AND (r.refund_method IS NULL OR r.refund_method = st.method)))
            """),
        ("une ligne de retour réintégrée a exactement un mouvement RETOUR de sa quantité, à l'emplacement du retour ; sinon aucun",
            """
            SELECT count(*) FROM return_lines rl JOIN returns r ON r.id = rl.return_id
            WHERE (SELECT count(*) FROM stock_movements m
                   WHERE m.source_type = 'RETOUR' AND m.source_id = r.id::text AND m.product_id = rl.product_id
                     AND m.movement_type = 'RETOUR' AND m.quantity = rl.quantity
                     AND m.location_id = r.destination_location_id)
                  <> CASE WHEN rl.restocked_quantity > 0 THEN 1 ELSE 0 END
               OR (SELECT count(*) FROM stock_movements m
                   WHERE m.source_type = 'RETOUR' AND m.source_id = r.id::text AND m.product_id = rl.product_id)
                  <> CASE WHEN rl.restocked_quantity > 0 THEN 1 ELSE 0 END
               OR (rl.restocked_quantity > 0) <> (r.destination_location_id IS NOT NULL)
            """),
        ("aucun mouvement RETOUR sans ligne de retour",
            """
            SELECT count(*) FROM stock_movements m
            WHERE (m.movement_type = 'RETOUR' OR m.source_type = 'RETOUR')
              AND NOT EXISTS (SELECT 1 FROM return_lines rl
                              WHERE rl.return_id::text = m.source_id AND rl.product_id = m.product_id
                                AND m.source_type = 'RETOUR' AND m.movement_type = 'RETOUR')
            """),
        ("un retour est rattaché à une session du même compte de caisse et du même site",
            """
            SELECT count(*) FROM returns r JOIN cash_sessions cs ON cs.id = r.cash_session_id
            WHERE cs.register_account_id <> r.register_account_id OR cs.store_id <> r.store_id
            """),
        ("les numéros de retour d'un site se suivent sans trou ni doublon",
            """
            SELECT count(*) FROM (SELECT store_id, count(*) AS total, max(return_seq) AS last, min(return_seq) AS first
                                  FROM returns GROUP BY store_id) t
            WHERE t.first <> 1 OR t.last <> t.total
            """),

        // ---------- Fermeture des caisses et clôture ----------
        ("une caisse fermée garde les espèces attendues exactes et l'écart = compté − attendu ; aucun TPE en attente",
            """
            SELECT count(*) FROM cash_sessions cs
            WHERE cs.status = 'FERMEE'
              AND (cs.cash_difference <> cs.counted_cash - cs.expected_cash
                   OR cs.expected_cash <> cs.opening_float
                      + (SELECT COALESCE(sum(amount), 0) FROM payments
                          WHERE cash_session_id = cs.id AND method = 'ESPECES' AND status IN ('CONFIRME', 'REMBOURSE'))
                      + (SELECT COALESCE(sum(amount), 0) FROM receivable_settlements
                          WHERE cash_session_id = cs.id AND method = 'ESPECES' AND status = 'CONFIRME')
                      + (SELECT COALESCE(sum(amount), 0) FROM cash_movements WHERE cash_session_id = cs.id AND kind = 'ENTREE')
                      - (SELECT COALESCE(sum(amount), 0) FROM cash_movements
                          WHERE cash_session_id = cs.id AND kind IN ('SORTIE', 'REMBOURSEMENT_VENTE'))
                      - (SELECT COALESCE(sum(refund_amount), 0) FROM returns
                          WHERE cash_session_id = cs.id AND refund_method = 'ESPECES')
                   OR EXISTS (SELECT 1 FROM payments p WHERE p.cash_session_id = cs.id AND p.status = 'EN_ATTENTE')
                   OR EXISTS (SELECT 1 FROM receivable_settlements st WHERE st.cash_session_id = cs.id AND st.status = 'EN_ATTENTE'))
            """),
        ("toute journée comptable inscrite sur une opération correspond à une clôture de son magasin",
            """
            SELECT count(*) FROM (
                SELECT store_id, business_date FROM sales
                UNION ALL SELECT store_id, business_date FROM payments
                UNION ALL SELECT store_id, business_date FROM receivable_settlements
                UNION ALL SELECT store_id, business_date FROM cash_movements
                UNION ALL SELECT store_id, business_date FROM sale_cancellations
                UNION ALL SELECT store_id, business_date FROM returns
                UNION ALL SELECT store_id, business_date FROM cash_sessions) o
            WHERE o.business_date IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM daily_closures dc WHERE dc.store_id = o.store_id AND dc.business_date = o.business_date)
            """),
        ("une opération appartient à la même journée que sa session de caisse ; une annulation, à celle de sa vente",
            """
            SELECT (SELECT count(*) FROM sales o JOIN cash_sessions cs ON cs.id = o.cash_session_id
                    WHERE o.business_date IS DISTINCT FROM cs.business_date)
                 + (SELECT count(*) FROM payments o JOIN cash_sessions cs ON cs.id = o.cash_session_id
                    WHERE o.business_date IS DISTINCT FROM cs.business_date)
                 + (SELECT count(*) FROM receivable_settlements o JOIN cash_sessions cs ON cs.id = o.cash_session_id
                    WHERE o.business_date IS DISTINCT FROM cs.business_date)
                 + (SELECT count(*) FROM cash_movements o JOIN cash_sessions cs ON cs.id = o.cash_session_id
                    WHERE o.business_date IS DISTINCT FROM cs.business_date)
                 + (SELECT count(*) FROM returns o JOIN cash_sessions cs ON cs.id = o.cash_session_id
                    WHERE o.business_date IS DISTINCT FROM cs.business_date)
                 + (SELECT count(*) FROM sale_cancellations x JOIN sales s ON s.id = x.sale_id
                    WHERE x.business_date IS DISTINCT FROM s.business_date)
            """),
        ("une journée clôturée ne contient ni caisse ouverte ni paiement par carte en attente",
            """
            SELECT (SELECT count(*) FROM cash_sessions WHERE business_date IS NOT NULL AND status <> 'FERMEE')
                 + (SELECT count(*) FROM payments WHERE business_date IS NOT NULL AND status = 'EN_ATTENTE')
                 + (SELECT count(*) FROM receivable_settlements WHERE business_date IS NOT NULL AND status = 'EN_ATTENTE')
                 + (SELECT count(*) FROM sales WHERE business_date IS NOT NULL AND status = 'EN_ATTENTE_TPE')
            """),
        ("le rapport figé d'une clôture correspond aux opérations de sa journée",
            """
            SELECT count(*) FROM daily_closures dc
            WHERE (dc.report #>> '{revenue,salesTotal}')::numeric
                  <> (SELECT COALESCE(sum(s.total), 0) FROM sales s
                      WHERE s.store_id = dc.store_id AND s.business_date = dc.business_date AND s.status = 'VALIDEE')
               OR (dc.report #>> '{treasury,saleCash}')::numeric
                  <> (SELECT COALESCE(sum(p.amount), 0) FROM payments p
                      WHERE p.store_id = dc.store_id AND p.business_date = dc.business_date AND p.method = 'ESPECES'
                        AND p.status IN ('CONFIRME', 'REMBOURSE'))
               OR (dc.report #>> '{treasury,oldReceivablesCash}')::numeric + (dc.report #>> '{treasury,sameDayReceivablesCash}')::numeric
                  + (dc.report #>> '{treasury,oldReceivablesCard}')::numeric + (dc.report #>> '{treasury,sameDayReceivablesCard}')::numeric
                  <> (SELECT COALESCE(sum(st.amount), 0) FROM receivable_settlements st
                      WHERE st.store_id = dc.store_id AND st.business_date = dc.business_date AND st.status = 'CONFIRME')
               OR (dc.report #>> '{revenue,returnsTotal}')::numeric
                  <> (SELECT COALESCE(sum(r.refund_amount), 0) FROM returns r
                      WHERE r.store_id = dc.store_id AND r.business_date = dc.business_date)
               OR (dc.report #>> '{treasury,expectedCash}')::numeric
                  <> (dc.report #>> '{treasury,openingFloats}')::numeric + (dc.report #>> '{treasury,netCash}')::numeric
               OR (dc.report #>> '{treasury,expectedCash}')::numeric
                  <> (SELECT COALESCE(sum(cs.expected_cash), 0) FROM cash_sessions cs
                      WHERE cs.store_id = dc.store_id AND cs.business_date = dc.business_date)
               OR (dc.report #>> '{treasury,cashDifference}')::numeric
                  <> (SELECT COALESCE(sum(cs.cash_difference), 0) FROM cash_sessions cs
                      WHERE cs.store_id = dc.store_id AND cs.business_date = dc.business_date)
               OR (dc.report #>> '{revenue,netRevenue}')::numeric
                  <> (dc.report #>> '{revenue,salesTotal}')::numeric - (dc.report #>> '{revenue,returnsTotal}')::numeric
               OR (dc.report ->> 'forcedWithUnknownOperations')::boolean <> dc.forced
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
