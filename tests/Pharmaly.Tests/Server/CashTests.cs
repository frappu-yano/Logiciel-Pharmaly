using System.Net;
using System.Net.Http.Json;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Cash;

namespace Pharmaly.Tests.Server;

[Collection(SalesTestKit.Collection)]
public sealed class CashTests(ServerFixture server) : IClassFixture<ServerFixture>, IAsyncLifetime
{
    private readonly SalesTestKit kit = new(server);

    public async ValueTask InitializeAsync() => await kit.EnsureSessionOpenAsync();

    /// <summary>Après chaque scénario : stock et argent cohérents, sur toute la base.</summary>
    public async ValueTask DisposeAsync()
    {
        await StockInvariant.AssertHoldsAsync(server.DataSource);
        await MoneyInvariant.AssertHoldsAsync(server.DataSource);
    }

    private static Task<HttpResponseMessage> MoveAsync(HttpClient client, string kind, decimal amount, string reason = "Monnaie") =>
        client.PostAsJsonAsync(CashRoutes.Movements, new CashMovementRequest(Guid.NewGuid(), kind, amount, reason));

    // ---------- Ouverture ----------

    [Fact]
    public async Task L_ouverture_enregistre_le_fond_de_caisse_et_l_employe()
    {
        var poste = await kit.CreateAdminOnNewRegisterAsync();
        Assert.Null((await poste.GetFromJsonAsync<CurrentCashSessionResponse>(CashRoutes.CurrentSession))!.Session);

        var opened = await SalesTestKit.ReadAsync<CashSessionInfo>(await SalesTestKit.OpenSessionAsync(poste, 12_345.50m));

        Assert.Equal("OUVERTE", opened.Status);
        Assert.Equal(12_345.50m, opened.OpeningFloat);
        Assert.Equal(server.AdminId, opened.OpenedById);
        Assert.Equal(server.StoreId, opened.StoreId);
        var current = (await poste.GetFromJsonAsync<CurrentCashSessionResponse>(CashRoutes.CurrentSession))!.Session;
        Assert.Equal(opened, current);
        Assert.Equal(12_345.50m, (await kit.SummaryAsync(poste)).ExpectedCash);
        Assert.Equal(1, await kit.Stock.CountAuditAsync(CashAuditActions.SessionOpened, opened.Id.ToString()));
    }

    [Fact]
    public async Task Une_seule_session_ouverte_par_caisse()
    {
        var (login, password) = await kit.CreateRegisterAsync();
        var poste = await kit.AdminOnRegisterAsync(login, password);
        var otherPoste = await kit.AdminOnRegisterAsync(login, password);
        var operationId = Guid.NewGuid();
        var opened = await SalesTestKit.ReadAsync<CashSessionInfo>(await SalesTestKit.OpenSessionAsync(poste, 1000m, operationId));

        // Une seconde ouverture est refusée, depuis ce poste comme depuis un autre poste de la même caisse.
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.OpenSessionAsync(poste), HttpStatusCode.Conflict, CashErrorCodes.SessionAlreadyOpen);
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.OpenSessionAsync(otherPoste), HttpStatusCode.Conflict, CashErrorCodes.SessionAlreadyOpen);

        // Un double clic sur l'ouverture renvoie la session déjà ouverte.
        var replay = await SalesTestKit.ReadAsync<CashSessionInfo>(await SalesTestKit.OpenSessionAsync(poste, 1000m, operationId));
        Assert.Equal(opened.Id, replay.Id);
        Assert.Equal(1, await kit.ScalarAsync<int>(
            "SELECT count(*) FROM cash_sessions WHERE register_account_id = @Id", new { Id = opened.RegisterAccountId }));
    }

    [Fact]
    public async Task Deux_ouvertures_simultanees_de_la_meme_caisse_une_seule_reussit()
    {
        for (var round = 0; round < 5; round++)
        {
            var (login, password) = await kit.CreateRegisterAsync();
            var first = await kit.AdminOnRegisterAsync(login, password);
            var second = await kit.AdminOnRegisterAsync(login, password);

            var responses = await SalesTestKit.InParallelAsync(
                6, index => SalesTestKit.OpenSessionAsync(index % 2 == 0 ? first : second, 100m * (index + 1)));

            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            foreach (var refused in responses.Where(response => response.StatusCode != HttpStatusCode.OK))
            {
                await StockTestKit.AssertErrorAsync(refused, HttpStatusCode.Conflict, CashErrorCodes.SessionAlreadyOpen);
            }

            Assert.Equal(1, await kit.ScalarAsync<int>(
                """
                SELECT count(*) FROM cash_sessions cs JOIN register_accounts ra ON ra.id = cs.register_account_id
                WHERE ra.login = @login
                """,
                new { login }));
        }
    }

    [Fact]
    public async Task La_base_refuse_une_seconde_session_ouverte_pour_le_meme_compte_de_caisse()
    {
        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => kit.ScalarAsync<object>(
            """
            WITH op AS (
                INSERT INTO operations (operation_id, kind, request_hash) VALUES (gen_random_uuid(), 'ESSAI', 'essai')
                RETURNING operation_id)
            INSERT INTO cash_sessions (register_account_id, store_id, opening_float, opened_by, operation_id)
            SELECT cs.register_account_id, cs.store_id, 0, cs.opened_by, op.operation_id
            FROM cash_sessions cs, op WHERE cs.status = 'OUVERTE' LIMIT 1
            """));

        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("cash_sessions_one_open", exception.ConstraintName);
    }

    [Fact]
    public async Task Un_fond_de_caisse_invalide_est_refuse()
    {
        var poste = await kit.CreateAdminOnNewRegisterAsync();

        foreach (var amount in new[] { -1m, 10.001m })
        {
            await StockTestKit.AssertErrorAsync(
                await SalesTestKit.OpenSessionAsync(poste, amount), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        }

        Assert.Null((await poste.GetFromJsonAsync<CurrentCashSessionResponse>(CashRoutes.CurrentSession))!.Session);
        // Un fond de caisse nul est permis.
        (await SalesTestKit.OpenSessionAsync(poste, 0m)).EnsureSuccessStatusCode();
    }

    // ---------- Aucun encaissement sans session ----------

    [Fact]
    public async Task Aucun_encaissement_sans_session_de_caisse_ouverte()
    {
        var product = await kit.CreateProductAsync(stock: 5, price: 700m);
        var customer = await kit.CreateCustomerAsync();
        // Créance, vente payée et paiements en attente préparés sur la caisse ouverte.
        var debt = await kit.SellUnpaidAsync(customer.Id, 700m);
        var paid = await kit.SellOkAsync(SalesTestKit.Cash(product.Id));
        var closed = await kit.CreateAdminOnNewRegisterAsync();

        var refusals = new[]
        {
            await SalesTestKit.SellAsync(closed, SalesTestKit.Cash(product.Id)),
            await SalesTestKit.SellAsync(closed, SalesTestKit.Card(product.Id)),
            // Choix prudent : même une vente impayée exige une caisse ouverte (tout ticket appartient à une session).
            await SalesTestKit.SellAsync(closed, SalesTestKit.Unpaid(product.Id, customer.Id)),
            await SalesTestKit.SettleAsync(closed, PaymentMethods.Cash, 700m, null, debt.Receivable!.Id),
            await SalesTestKit.SettleAsync(closed, PaymentMethods.Card, 700m, null, debt.Receivable!.Id),
            await MoveAsync(closed, CashMovementKinds.In, 100m),
            await MoveAsync(closed, CashMovementKinds.Out, 100m),
            // Rembourser une vente payée, c'est sortir de l'argent de la caisse du poste.
            await SalesTestKit.CancelAsync(closed, paid.Id),
            await closed.GetAsync(CashRoutes.SessionSummary),
        };
        foreach (var refusal in refusals)
        {
            var error = await StockTestKit.AssertErrorAsync(refusal, HttpStatusCode.Conflict, CashErrorCodes.NoOpenSession);
            Assert.Contains("caisse", error.Message);
        }

        Assert.Equal(4m, await kit.StockAsync(product.Id));
        Assert.Equal(ReceivableStatuses.Open, (await kit.ReceivableAsync(debt)).Status);
        Assert.Equal(SaleStatuses.Completed, (await kit.ReadSaleAsync(paid.Id)).Status);
        Assert.Equal(0, await kit.ScalarAsync<int>(
            """
            SELECT (SELECT count(*) FROM payments p JOIN cash_sessions cs ON cs.id = p.cash_session_id WHERE cs.status <> 'OUVERTE')::int
                 + (SELECT count(*) FROM sales WHERE cash_session_id IS NULL)::int
            """));

        // Une fois la caisse ouverte, les mêmes gestes passent ; la vente impayée s'annule sans remboursement.
        (await SalesTestKit.OpenSessionAsync(closed)).EnsureSuccessStatusCode();
        await kit.SellOkAsync(SalesTestKit.Cash(product.Id), closed);
        (await SalesTestKit.SettleAsync(closed, PaymentMethods.Cash, 700m, null, debt.Receivable!.Id)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Une_vente_impayee_s_annule_depuis_un_poste_dont_la_caisse_est_fermee()
    {
        var customer = await kit.CreateCustomerAsync();
        var debt = await kit.SellUnpaidAsync(customer.Id, 450m);
        var closed = await kit.CreateAdminOnNewRegisterAsync();

        // Aucun argent ne sort : l'annulation d'un ticket jamais encaissé ne dépend pas de la caisse.
        var cancelled = await SalesTestKit.ReadAsync<SaleDetail>(await SalesTestKit.CancelAsync(closed, debt.Id));

        Assert.Equal(ReceivableStatuses.Cancelled, cancelled.Receivable!.Status);
    }

    // ---------- Rattachement à la session et à l'employé ----------

    [Fact]
    public async Task Chaque_encaissement_porte_la_session_le_site_l_employe_le_moyen_et_le_montant()
    {
        var poste = await kit.CreateAdminOnNewRegisterAsync();
        var session = await SalesTestKit.ReadAsync<CashSessionInfo>(await SalesTestKit.OpenSessionAsync(poste, 1000m));
        var (number, employeeId) = await kit.CreateEmployeeAsync(
            Permissions.SalesCreate, Permissions.PaymentsCollect, Permissions.ReceivablesSettle);
        await ServerFixture.IdentifyAsync(poste, number, SalesTestKit.EmployeeSecret);
        var product = await kit.CreateProductAsync(stock: 5, price: 800m);
        var customer = await kit.CreateCustomerAsync();
        // La créance naît sur une autre caisse : le règlement appartient à la caisse qui encaisse.
        var debt = await kit.SellUnpaidAsync(customer.Id, 300m);

        var sale = await kit.SellOkAsync(SalesTestKit.Cash(product.Id), poste);
        var settlement = await SalesTestKit.ReadAsync<SettlementResult>(
            await SalesTestKit.SettleAsync(poste, PaymentMethods.Cash, 300m, null, debt.Receivable!.Id));
        var movement = await SalesTestKit.ReadAsync<CashMovementResult>(await MoveAsync(poste, CashMovementKinds.Out, 50m, "Achat de sacs"));

        Assert.Equal(session.Id, sale.CashSessionId);
        Assert.Equal(session.Id, sale.Payment!.CashSessionId);
        Assert.Equal(session.Id, settlement.CashSessionId);
        Assert.NotEqual(session.Id, debt.CashSessionId);
        Assert.Equal(employeeId, settlement.EmployeeId);
        Assert.Equal(session.Id, movement.CashSessionId);
        Assert.Equal(employeeId, movement.EmployeeId);
        foreach (var table in new[] { "payments", "receivable_settlements", "cash_movements" })
        {
            // Session, site, compte de caisse, employé, date ; la journée comptable n'est pas encore figée.
            Assert.Equal(1, await kit.ScalarAsync<int>(
                $"""
                 SELECT count(*) FROM {table}
                 WHERE cash_session_id = @Id AND store_id = @StoreId AND register_account_id = @RegisterAccountId
                   AND employee_id = @employeeId AND business_date IS NULL AND amount > 0
                   AND {(table == "cash_movements" ? "occurred_at" : "created_at")} > now() - interval '5 minutes'
                 """,
                new { session.Id, session.StoreId, session.RegisterAccountId, employeeId }));
        }
    }

    [Fact]
    public async Task Le_resume_distingue_les_ventes_encaissees_des_reglements_d_anciennes_creances()
    {
        var poste = await kit.CreateAdminOnNewRegisterAsync();
        (await SalesTestKit.OpenSessionAsync(poste, 1000m)).EnsureSuccessStatusCode();
        var product = await kit.CreateProductAsync(stock: 10, price: 500m);
        var customer = await kit.CreateCustomerAsync();
        var cashDebt = await kit.SellUnpaidAsync(customer.Id, 3500m, poste);
        var cardDebt = await kit.SellUnpaidAsync(customer.Id, 6200m, poste);

        await kit.SellOkAsync(SalesTestKit.Cash(product.Id, 2, received: 2000m), poste);
        var card = await kit.SellOkAsync(SalesTestKit.Card(product.Id), poste);
        (await SalesTestKit.CardResultAsync(poste, card.Id, accepted: true)).EnsureSuccessStatusCode();
        await kit.SellOkAsync(SalesTestKit.Card(product.Id), poste);
        (await SalesTestKit.SettleAsync(poste, PaymentMethods.Cash, 3500m, null, cashDebt.Receivable!.Id)).EnsureSuccessStatusCode();
        var cardSettlement = await SalesTestKit.ReadAsync<SettlementResult>(
            await SalesTestKit.SettleAsync(poste, PaymentMethods.Card, 6200m, null, cardDebt.Receivable!.Id));
        (await SalesTestKit.SettlementCardResultAsync(poste, cardSettlement.Id, accepted: true)).EnsureSuccessStatusCode();
        (await MoveAsync(poste, CashMovementKinds.In, 200m)).EnsureSuccessStatusCode();
        (await MoveAsync(poste, CashMovementKinds.Out, 75.25m)).EnsureSuccessStatusCode();

        var summary = await kit.SummaryAsync(poste);

        Assert.Equal(1000m, summary.SaleCashCollected);
        Assert.Equal(500m, summary.SaleCardCollected);
        Assert.Equal(3500m, summary.SettlementCashCollected);
        Assert.Equal(6200m, summary.SettlementCardCollected);
        Assert.Equal(200m, summary.CashIn);
        Assert.Equal(75.25m, summary.CashOut);
        Assert.Equal(0m, summary.CashRefunds);
        // 1000 + 1000 + 3500 + 200 − 75,25 : les paiements par carte et les impayés n'y entrent pas.
        Assert.Equal(5624.75m, summary.ExpectedCash);
        Assert.Equal(1, summary.PendingCardCount);
        Assert.Equal(500m, summary.PendingCardAmount);
        // Chiffre d'affaires de la session (ventes validées, impayées comprises) ≠ trésorerie.
        Assert.Equal(11_200m, await kit.ScalarAsync<decimal>(
            "SELECT sum(total) FROM sales WHERE cash_session_id = @Id AND status = 'VALIDEE'", new { summary.Session.Id }));
    }

    // ---------- Entrées et sorties d'espèces ----------

    [Fact]
    public async Task Entrees_et_sorties_d_especes_exigent_un_motif_et_sont_auditees()
    {
        var before = await kit.SummaryAsync();

        var entry = await SalesTestKit.ReadAsync<CashMovementResult>(
            await MoveAsync(server.AdminClient, CashMovementKinds.In, 500m, "  Appoint de monnaie  "));
        var exit = await SalesTestKit.ReadAsync<CashMovementResult>(
            await MoveAsync(server.AdminClient, CashMovementKinds.Out, 120.50m, "Dépôt en banque"));

        Assert.Equal("Appoint de monnaie", entry.Reason);
        Assert.Equal(server.AdminId, exit.EmployeeId);
        var after = await kit.SummaryAsync();
        Assert.Equal(before.CashIn + 500m, after.CashIn);
        Assert.Equal(before.CashOut + 120.50m, after.CashOut);
        Assert.Equal(before.ExpectedCash + 379.50m, after.ExpectedCash);
        Assert.Equal(1, await kit.Stock.CountAuditAsync(CashAuditActions.CashIn, entry.Id.ToString()));
        Assert.Equal(1, await kit.Stock.CountAuditAsync(CashAuditActions.CashOut, exit.Id.ToString()));

        foreach (var refused in new[]
                 {
                     await MoveAsync(server.AdminClient, CashMovementKinds.Out, 10m, " "),
                     await MoveAsync(server.AdminClient, CashMovementKinds.In, 0m),
                     await MoveAsync(server.AdminClient, CashMovementKinds.In, -5m),
                     await MoveAsync(server.AdminClient, CashMovementKinds.In, 1.005m),
                     await MoveAsync(server.AdminClient, "DON", 10m),
                     // Un remboursement de vente ne se saisit pas à la main : il vient de l'annulation.
                     await MoveAsync(server.AdminClient, CashMovementKinds.SaleRefund, 10m),
                 })
        {
            await StockTestKit.AssertErrorAsync(refused, HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        }

        Assert.Equal(after, await kit.SummaryAsync());
    }

    [Fact]
    public async Task Un_double_clic_sur_un_mouvement_d_especes_n_en_cree_qu_un()
    {
        var before = await kit.SummaryAsync();
        var request = new CashMovementRequest(Guid.NewGuid(), CashMovementKinds.Out, 40m, "Timbres");

        var responses = await SalesTestKit.InParallelAsync(
            5, _ => server.AdminClient.PostAsJsonAsync(CashRoutes.Movements, request));

        var ids = new HashSet<Guid>();
        foreach (var response in responses)
        {
            ids.Add((await SalesTestKit.ReadAsync<CashMovementResult>(response)).Id);
        }

        Assert.Single(ids);
        Assert.Equal(before.CashOut + 40m, (await kit.SummaryAsync()).CashOut);
    }

    // ---------- Permissions ----------

    [Fact]
    public async Task Chaque_route_de_la_caisse_est_refusee_sans_son_droit()
    {
        string[] all =
        [
            Permissions.SalesCreate, Permissions.PaymentsCollect, Permissions.RegisterOpen, Permissions.RegisterReportsView,
            Permissions.RegisterClose,
        ];
        async Task<HttpClient> WithoutAsync(string permission) =>
            (await kit.CreateEmployeeClientAsync(all.Where(code => code != permission).ToArray())).Client;

        var refusals = new[]
        {
            await SalesTestKit.OpenSessionAsync(await WithoutAsync(Permissions.RegisterOpen)),
            await (await WithoutAsync(Permissions.RegisterReportsView)).GetAsync(CashRoutes.SessionSummary),
            await MoveAsync(await WithoutAsync(Permissions.PaymentsCollect), CashMovementKinds.In, 10m),
        };
        foreach (var refusal in refusals)
        {
            await StockTestKit.AssertErrorAsync(refusal, HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        }

        // L'état de la caisse est visible de tout employé identifié, pas d'un poste sans employé.
        var (anyone, _) = await kit.CreateEmployeeClientAsync();
        Assert.NotNull((await anyone.GetFromJsonAsync<CurrentCashSessionResponse>(CashRoutes.CurrentSession))!.Session);
        await StockTestKit.AssertErrorAsync(
            await (await server.CreatePosteClientAsync()).GetAsync(CashRoutes.CurrentSession),
            HttpStatusCode.Unauthorized, ApiErrorCodes.EmployeeNotIdentified);
    }
}
