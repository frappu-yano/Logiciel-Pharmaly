using System.Net;
using System.Net.Http.Json;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Receivables;

namespace Pharmaly.Tests.Server;

[Collection(SalesTestKit.Collection)]
public sealed class ReceivablesTests(ServerFixture server) : IClassFixture<ServerFixture>, IAsyncLifetime
{
    private readonly SalesTestKit kit = new(server);

    public async ValueTask InitializeAsync() => await kit.EnsureSessionOpenAsync();

    /// <summary>Après chaque scénario : stock et argent cohérents, sur toute la base.</summary>
    public async ValueTask DisposeAsync()
    {
        await StockInvariant.AssertHoldsAsync(server.DataSource);
        await MoneyInvariant.AssertHoldsAsync(server.DataSource);
    }

    private Task<HttpResponseMessage> SettleCashAsync(decimal amount, params Guid[] receivableIds) =>
        SalesTestKit.SettleAsync(server.AdminClient, PaymentMethods.Cash, amount, null, receivableIds);

    private Task<HttpResponseMessage> SettleCardAsync(decimal amount, params Guid[] receivableIds) =>
        SalesTestKit.SettleAsync(server.AdminClient, PaymentMethods.Card, amount, null, receivableIds);

    private async Task<string> StatusAsync(SaleDetail sale) => (await kit.ReceivableAsync(sale)).Status;

    // ---------- Règlement intégral ----------

    [Fact]
    public async Task Reglement_integral_d_un_impaye_en_especes()
    {
        var customer = await kit.CreateCustomerAsync("M. Haddad");
        var sale = await kit.SellUnpaidAsync(customer.Id, 3500m);
        var movements = await kit.CountMovementsAsync();
        var before = await kit.SummaryAsync();

        var settlement = await SalesTestKit.ReadAsync<SettlementResult>(await server.AdminClient.PostAsJsonAsync(
            ReceivableRoutes.Settlements,
            new SettleReceivablesRequest(Guid.NewGuid(), [sale.Receivable!.Id], PaymentMethods.Cash, 3500m, AmountReceived: 4000m)));

        Assert.Equal(PaymentStatuses.Confirmed, settlement.Status);
        Assert.Equal(3500m, settlement.Amount);
        Assert.Equal(4000m, settlement.AmountReceived);
        Assert.Equal(500m, settlement.ChangeGiven);
        Assert.Equal(customer.Id, settlement.CustomerId);
        Assert.Equal(server.AdminId, settlement.EmployeeId);
        Assert.NotNull(settlement.ConfirmedAt);
        var line = Assert.Single(settlement.Receivables);
        Assert.Equal(ReceivableStatuses.Settled, line.Status);
        Assert.Equal(sale.TicketNumber, line.TicketNumber);
        Assert.Equal(PaymentMethods.Cash, line.SettlementMethod);
        Assert.Equal(settlement.Id, line.SettlementId);
        Assert.NotNull(line.SettledAt);
        var ticket = await kit.ReadSaleAsync(sale.Id);
        Assert.False(ticket.IsUnpaid);
        Assert.Equal(ReceivableStatuses.Settled, ticket.Receivable!.Status);
        // Le règlement ne provoque aucun mouvement de stock.
        Assert.Equal(movements, await kit.CountMovementsAsync());
        // Trésorerie du jour du règlement, pas du chiffre d'affaires : rien dans les encaissements de ventes.
        var after = await kit.SummaryAsync();
        Assert.Equal(before.SettlementCashCollected + 3500m, after.SettlementCashCollected);
        Assert.Equal(before.SaleCashCollected, after.SaleCashCollected);
        Assert.Equal(before.ExpectedCash + 3500m, after.ExpectedCash);
        Assert.Equal(0, await kit.ScalarAsync<int>("SELECT count(*) FROM payments WHERE sale_id = @Id", new { sale.Id }));
        Assert.Equal(1, await kit.Stock.CountAuditAsync(ReceivableAuditActions.Settled, settlement.Id.ToString()));
    }

    [Fact]
    public async Task Un_montant_different_du_montant_du_est_refuse()
    {
        var customer = await kit.CreateCustomerAsync();
        var sale = await kit.SellUnpaidAsync(customer.Id, 3500m);

        // Règlement partiel, puis trop-perçu.
        foreach (var amount in new[] { 3499.99m, 1000m, 3500.01m })
        {
            var error = await StockTestKit.AssertErrorAsync(
                await SettleCashAsync(amount, sale.Receivable!.Id), HttpStatusCode.BadRequest, ReceivableErrorCodes.AmountMismatch);
            Assert.Contains("3 500,00 DA", error.Message);
            await StockTestKit.AssertErrorAsync(
                await SettleCardAsync(amount, sale.Receivable!.Id), HttpStatusCode.BadRequest, ReceivableErrorCodes.AmountMismatch);
        }

        foreach (var amount in new[] { 0m, -3500m, 3500.001m })
        {
            await StockTestKit.AssertErrorAsync(
                await SettleCashAsync(amount, sale.Receivable!.Id), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        }

        // Montant juste, mais espèces reçues insuffisantes.
        await StockTestKit.AssertErrorAsync(
            await server.AdminClient.PostAsJsonAsync(
                ReceivableRoutes.Settlements,
                new SettleReceivablesRequest(Guid.NewGuid(), [sale.Receivable!.Id], PaymentMethods.Cash, 3500m, AmountReceived: 3000m)),
            HttpStatusCode.BadRequest, SaleErrorCodes.InsufficientAmountReceived);

        Assert.Equal(ReceivableStatuses.Open, await StatusAsync(sale));
        Assert.Equal(0, await kit.ScalarAsync<int>(
            "SELECT count(*) FROM receivable_settlement_items WHERE receivable_id = @Id", new { sale.Receivable!.Id }));
    }

    // ---------- Règlement groupé ----------

    [Fact]
    public async Task Reglement_groupe_de_trois_tickets_par_un_seul_encaissement()
    {
        var customer = await kit.CreateCustomerAsync();
        var first = await kit.SellUnpaidAsync(customer.Id, 3500m);
        var second = await kit.SellUnpaidAsync(customer.Id, 6200m);
        var third = await kit.SellUnpaidAsync(customer.Id, 1800m);
        var movements = await kit.CountMovementsAsync();
        var before = await kit.SummaryAsync();

        var settlement = await SalesTestKit.ReadAsync<SettlementResult>(
            await SettleCashAsync(11_500m, first.Receivable!.Id, second.Receivable!.Id, third.Receivable!.Id));

        Assert.Equal(11_500m, settlement.Amount);
        Assert.Equal(3, settlement.Receivables.Count);
        Assert.All(settlement.Receivables, line =>
        {
            Assert.Equal(ReceivableStatuses.Settled, line.Status);
            Assert.Equal(settlement.Id, line.SettlementId);
        });
        Assert.Equal([3500m, 6200m, 1800m], settlement.Receivables.Select(line => line.Amount));
        // Un seul encaissement pour les trois tickets.
        Assert.Equal(1, await kit.ScalarAsync<int>(
            "SELECT count(*) FROM receivable_settlements WHERE customer_id = @Id", new { customer.Id }));
        Assert.Equal(before.SettlementCashCollected + 11_500m, (await kit.SummaryAsync()).SettlementCashCollected);
        Assert.Equal(movements, await kit.CountMovementsAsync());
    }

    [Fact]
    public async Task Reglement_groupe_dont_le_montant_n_est_pas_la_somme_exacte_refuse_et_rien_n_est_solde()
    {
        var customer = await kit.CreateCustomerAsync();
        var first = await kit.SellUnpaidAsync(customer.Id, 3500m);
        var second = await kit.SellUnpaidAsync(customer.Id, 6200m);

        // Le montant d'un seul des deux tickets : pas de règlement partiel du lot.
        foreach (var amount in new[] { 3500m, 6200m, 9699.99m })
        {
            await StockTestKit.AssertErrorAsync(
                await SettleCashAsync(amount, first.Receivable!.Id, second.Receivable!.Id),
                HttpStatusCode.BadRequest, ReceivableErrorCodes.AmountMismatch);
        }

        Assert.Equal(ReceivableStatuses.Open, await StatusAsync(first));
        Assert.Equal(ReceivableStatuses.Open, await StatusAsync(second));
    }

    [Fact]
    public async Task Reglement_groupe_avec_un_ticket_d_un_autre_client_refuse_et_rien_n_est_solde()
    {
        var customer = await kit.CreateCustomerAsync();
        var other = await kit.CreateCustomerAsync();
        var first = await kit.SellUnpaidAsync(customer.Id, 3500m);
        var second = await kit.SellUnpaidAsync(customer.Id, 6200m);
        var foreign = await kit.SellUnpaidAsync(other.Id, 1800m);
        var before = await kit.SummaryAsync();

        var error = await StockTestKit.AssertErrorAsync(
            await SettleCashAsync(11_500m, first.Receivable!.Id, second.Receivable!.Id, foreign.Receivable!.Id),
            HttpStatusCode.Conflict, ReceivableErrorCodes.DifferentCustomers);

        Assert.Contains("Rien n'a été encaissé", error.Message);
        foreach (var sale in new[] { first, second, foreign })
        {
            Assert.Equal(ReceivableStatuses.Open, await StatusAsync(sale));
        }

        Assert.Equal(before, await kit.SummaryAsync());
        Assert.Equal(0, await kit.ScalarAsync<int>(
            "SELECT count(*) FROM receivable_settlements WHERE customer_id = ANY(@ids)", new { ids = new[] { customer.Id, other.Id } }));
    }

    [Fact]
    public async Task Reglement_groupe_dont_un_ticket_est_deja_solde_refuse_et_rien_n_est_solde()
    {
        var customer = await kit.CreateCustomerAsync();
        var first = await kit.SellUnpaidAsync(customer.Id, 3500m);
        var second = await kit.SellUnpaidAsync(customer.Id, 6200m);
        var alreadyPaid = await kit.SellUnpaidAsync(customer.Id, 1800m);
        (await SettleCashAsync(1800m, alreadyPaid.Receivable!.Id)).EnsureSuccessStatusCode();
        var before = await kit.SummaryAsync();

        var error = await StockTestKit.AssertErrorAsync(
            await SettleCashAsync(11_500m, first.Receivable!.Id, second.Receivable!.Id, alreadyPaid.Receivable!.Id),
            HttpStatusCode.Conflict, ReceivableErrorCodes.NotOpen);

        Assert.Contains(alreadyPaid.TicketNumber, error.Message);
        Assert.Equal(ReceivableStatuses.Open, await StatusAsync(first));
        Assert.Equal(ReceivableStatuses.Open, await StatusAsync(second));
        Assert.Equal(before, await kit.SummaryAsync());
        Assert.Equal(1, await kit.ScalarAsync<int>(
            "SELECT count(*) FROM receivable_settlements WHERE customer_id = @Id", new { customer.Id }));

        // Les deux tickets restants se règlent ensuite normalement.
        (await SettleCashAsync(9700m, first.Receivable!.Id, second.Receivable!.Id)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Un_reglement_mal_forme_est_refuse()
    {
        var customer = await kit.CreateCustomerAsync();
        var sale = await kit.SellUnpaidAsync(customer.Id, 1000m);
        var id = sale.Receivable!.Id;

        await StockTestKit.AssertErrorAsync(await SettleCashAsync(1000m), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        await StockTestKit.AssertErrorAsync(await SettleCashAsync(2000m, id, id), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SettleAsync(server.AdminClient, "CHEQUE", 1000m, null, id),
            HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        await StockTestKit.AssertErrorAsync(
            await SettleCashAsync(1000m, Guid.NewGuid()), HttpStatusCode.NotFound, ApiErrorCodes.NotFound);
        await StockTestKit.AssertErrorAsync(
            await SettleCashAsync(1000m, id, Guid.NewGuid()), HttpStatusCode.NotFound, ApiErrorCodes.NotFound);

        Assert.Equal(ReceivableStatuses.Open, await StatusAsync(sale));
    }

    // ---------- Double règlement ----------

    [Fact]
    public async Task Un_second_reglement_de_la_meme_creance_est_refuse()
    {
        var customer = await kit.CreateCustomerAsync();
        var sale = await kit.SellUnpaidAsync(customer.Id, 2500m);
        (await SettleCashAsync(2500m, sale.Receivable!.Id)).EnsureSuccessStatusCode();
        var before = await kit.SummaryAsync();

        var error = await StockTestKit.AssertErrorAsync(
            await SettleCashAsync(2500m, sale.Receivable!.Id), HttpStatusCode.Conflict, ReceivableErrorCodes.NotOpen);
        await StockTestKit.AssertErrorAsync(
            await SettleCardAsync(2500m, sale.Receivable!.Id), HttpStatusCode.Conflict, ReceivableErrorCodes.NotOpen);

        Assert.Contains("déjà soldé", error.Message);
        Assert.Equal(before, await kit.SummaryAsync());
        Assert.Equal(1, await kit.ScalarAsync<int>(
            "SELECT count(*) FROM receivable_settlement_items WHERE receivable_id = @Id", new { sale.Receivable!.Id }));
    }

    [Fact]
    public async Task Deux_postes_reglent_la_meme_creance_en_meme_temps_un_seul_reussit()
    {
        var customer = await kit.CreateCustomerAsync();
        var (first, _) = await kit.CreateEmployeeClientAsync(Permissions.ReceivablesSettle);
        var (second, _) = await kit.CreateEmployeeClientAsync(Permissions.ReceivablesSettle);

        for (var round = 0; round < 10; round++)
        {
            var sale = await kit.SellUnpaidAsync(customer.Id, 1500m);
            var before = await kit.SummaryAsync();

            // Espèces contre espèces, puis espèces contre carte.
            var responses = await SalesTestKit.InParallelAsync(2, index => SalesTestKit.SettleAsync(
                index == 0 ? first : second,
                index == 1 && round % 2 == 1 ? PaymentMethods.Card : PaymentMethods.Cash,
                1500m, null, sale.Receivable!.Id));

            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            var refused = responses.Single(response => response.StatusCode != HttpStatusCode.OK);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains(
                (await refused.Content.ReadFromJsonAsync<ApiError>())!.Code,
                new[] { ReceivableErrorCodes.NotOpen, ReceivableErrorCodes.SettlementPending });
            Assert.Equal(1, await kit.ScalarAsync<int>(
                "SELECT count(*) FROM receivable_settlement_items WHERE receivable_id = @Id AND is_active", new { sale.Receivable!.Id }));

            // Un seul encaissement, en espèces ou en attente de TPE, jamais deux.
            var after = await kit.SummaryAsync();
            Assert.Equal(
                1500m,
                after.SettlementCashCollected - before.SettlementCashCollected + after.PendingCardAmount - before.PendingCardAmount);
        }
    }

    [Fact]
    public async Task Vingt_reglements_groupes_simultanes_qui_se_chevauchent_chaque_creance_est_soldee_une_seule_fois()
    {
        var customer = await kit.CreateCustomerAsync();
        var sales = new List<SaleDetail>();
        for (var index = 0; index < 4; index++)
        {
            sales.Add(await kit.SellUnpaidAsync(customer.Id, 1000m));
        }

        var ids = sales.Select(sale => sale.Receivable!.Id).ToArray();
        var before = await kit.SummaryAsync();

        // Paires prises dans des ordres opposés : aucun interblocage, aucune créance soldée deux fois.
        var responses = await SalesTestKit.InParallelAsync(20, index =>
        {
            var pair = (index % 4) switch
            {
                0 => new[] { ids[0], ids[1] },
                1 => new[] { ids[1], ids[2] },
                2 => new[] { ids[3], ids[2] },
                _ => new[] { ids[3], ids[0] },
            };
            return SettleCashAsync(2000m, pair);
        });

        Assert.All(responses, response => Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        var succeeded = responses.Count(response => response.StatusCode == HttpStatusCode.OK);
        Assert.InRange(succeeded, 1, 2);
        Assert.Equal(succeeded * 2, await kit.ScalarAsync<int>(
            "SELECT count(*) FROM receivables WHERE id = ANY(@ids) AND status = 'SOLDEE'", new { ids }));
        Assert.Equal(before.SettlementCashCollected + succeeded * 2000m, (await kit.SummaryAsync()).SettlementCashCollected);
    }

    [Fact]
    public async Task Double_clic_sur_le_reglement_un_seul_encaissement_et_la_meme_reponse()
    {
        var customer = await kit.CreateCustomerAsync();
        var sale = await kit.SellUnpaidAsync(customer.Id, 2000m);
        var operationId = Guid.NewGuid();
        var before = await kit.SummaryAsync();

        var responses = await SalesTestKit.InParallelAsync(
            6, _ => SalesTestKit.SettleAsync(server.AdminClient, PaymentMethods.Cash, 2000m, operationId, sale.Receivable!.Id));

        var ids = new HashSet<Guid>();
        foreach (var response in responses)
        {
            ids.Add((await SalesTestKit.ReadAsync<SettlementResult>(response)).Id);
        }

        Assert.Single(ids);
        Assert.Equal(before.SettlementCashCollected + 2000m, (await kit.SummaryAsync()).SettlementCashCollected);
    }

    [Fact]
    public async Task La_base_refuse_qu_une_creance_figure_dans_deux_reglements_actifs()
    {
        var customer = await kit.CreateCustomerAsync();
        var sale = await kit.SellUnpaidAsync(customer.Id, 2000m);
        var other = await kit.SellUnpaidAsync(customer.Id, 2000m);
        (await SettleCashAsync(2000m, sale.Receivable!.Id)).EnsureSuccessStatusCode();
        var otherSettlement = await SalesTestKit.ReadAsync<SettlementResult>(await SettleCashAsync(2000m, other.Receivable!.Id));

        // Même en contournant le serveur, la base n'accepte pas un second règlement de la créance.
        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => kit.ScalarAsync<object>(
            "INSERT INTO receivable_settlement_items (settlement_id, receivable_id, amount) VALUES (@settlement, @receivable, 2000)",
            new { settlement = otherSettlement.Id, receivable = sale.Receivable!.Id }));

        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("receivable_settlement_items_one_active", exception.ConstraintName);
    }

    // ---------- TPE ----------

    [Fact]
    public async Task Reglement_par_TPE_la_creance_n_est_soldee_qu_a_la_confirmation()
    {
        var customer = await kit.CreateCustomerAsync();
        var first = await kit.SellUnpaidAsync(customer.Id, 3500m);
        var second = await kit.SellUnpaidAsync(customer.Id, 6200m);
        var movements = await kit.CountMovementsAsync();
        var before = await kit.SummaryAsync();

        var pending = await SalesTestKit.ReadAsync<SettlementResult>(
            await SettleCardAsync(9700m, first.Receivable!.Id, second.Receivable!.Id));

        Assert.Equal(PaymentStatuses.Pending, pending.Status);
        Assert.Null(pending.ConfirmedAt);
        Assert.All(pending.Receivables, line =>
        {
            Assert.Equal(ReceivableStatuses.Open, line.Status);
            Assert.Equal(pending.Id, line.PendingSettlementId);
            Assert.Null(line.SettledAt);
            Assert.Null(line.SettlementMethod);
        });
        Assert.True((await kit.ReadSaleAsync(first.Id)).IsUnpaid);
        // Rien n'est compté comme encaissé avant la confirmation.
        var during = await kit.SummaryAsync();
        Assert.Equal(before.SettlementCardCollected, during.SettlementCardCollected);
        Assert.Equal(before.PendingCardAmount + 9700m, during.PendingCardAmount);
        Assert.Equal(before.ExpectedCash, during.ExpectedCash);

        var accepted = await SalesTestKit.ReadAsync<SettlementResult>(
            await SalesTestKit.SettlementCardResultAsync(server.AdminClient, pending.Id, accepted: true));

        Assert.Equal(PaymentStatuses.Confirmed, accepted.Status);
        Assert.NotNull(accepted.ConfirmedAt);
        Assert.All(accepted.Receivables, line =>
        {
            Assert.Equal(ReceivableStatuses.Settled, line.Status);
            Assert.Equal(PaymentMethods.Card, line.SettlementMethod);
            Assert.Null(line.PendingSettlementId);
        });
        var after = await kit.SummaryAsync();
        Assert.Equal(before.SettlementCardCollected + 9700m, after.SettlementCardCollected);
        Assert.Equal(before.PendingCardAmount, after.PendingCardAmount);
        Assert.Equal(before.ExpectedCash, after.ExpectedCash);
        Assert.Equal(movements, await kit.CountMovementsAsync());

        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SettlementCardResultAsync(server.AdminClient, pending.Id, accepted: false),
            HttpStatusCode.Conflict, ReceivableErrorCodes.SettlementNotPending);
        Assert.Equal(ReceivableStatuses.Settled, await StatusAsync(first));
    }

    [Fact]
    public async Task Echec_du_paiement_TPE_la_creance_reste_ouverte_et_intacte()
    {
        var customer = await kit.CreateCustomerAsync();
        var sale = await kit.SellUnpaidAsync(customer.Id, 4200m);
        var before = await kit.SummaryAsync();
        var pending = await SalesTestKit.ReadAsync<SettlementResult>(await SettleCardAsync(4200m, sale.Receivable!.Id));

        var refused = await SalesTestKit.ReadAsync<SettlementResult>(
            await SalesTestKit.SettlementCardResultAsync(server.AdminClient, pending.Id, accepted: false));

        Assert.Equal(PaymentStatuses.Failed, refused.Status);
        Assert.Null(refused.ConfirmedAt);
        var line = await kit.ReceivableAsync(sale);
        Assert.Equal(ReceivableStatuses.Open, line.Status);
        Assert.Equal(4200m, line.Amount);
        Assert.Null(line.SettledAt);
        Assert.Null(line.SettlementId);
        Assert.Null(line.PendingSettlementId);
        Assert.True((await kit.ReadSaleAsync(sale.Id)).IsUnpaid);
        Assert.Equal(before, await kit.SummaryAsync());
        Assert.Equal(1, await kit.Stock.CountAuditAsync(ReceivableAuditActions.CardRefused, pending.Id.ToString()));

        // Une réponse « accepté » tardive ne ressuscite pas un règlement refusé.
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SettlementCardResultAsync(server.AdminClient, pending.Id, accepted: true),
            HttpStatusCode.Conflict, ReceivableErrorCodes.SettlementNotPending);

        // La créance, libérée, se règle ensuite normalement, une seule fois.
        (await SettleCashAsync(4200m, sale.Receivable!.Id)).EnsureSuccessStatusCode();
        Assert.Equal(ReceivableStatuses.Settled, await StatusAsync(sale));
    }

    [Fact]
    public async Task Un_second_reglement_est_refuse_pendant_qu_un_TPE_est_en_attente_puis_debloque_par_un_refus()
    {
        var customer = await kit.CreateCustomerAsync();
        var sale = await kit.SellUnpaidAsync(customer.Id, 3000m);
        var companion = await kit.SellUnpaidAsync(customer.Id, 1000m);
        var pending = await SalesTestKit.ReadAsync<SettlementResult>(await SettleCardAsync(3000m, sale.Receivable!.Id));
        var (otherPoste, otherEmployee) = await kit.CreateEmployeeClientAsync(Permissions.ReceivablesSettle);

        // Ni en espèces, ni par carte, ni dans un règlement groupé, ni depuis un autre poste.
        foreach (var attempt in new[]
                 {
                     await SettleCashAsync(3000m, sale.Receivable!.Id),
                     await SettleCardAsync(3000m, sale.Receivable!.Id),
                     await SettleCashAsync(4000m, sale.Receivable!.Id, companion.Receivable!.Id),
                     await SalesTestKit.SettleAsync(otherPoste, PaymentMethods.Cash, 3000m, null, sale.Receivable!.Id),
                 })
        {
            var error = await StockTestKit.AssertErrorAsync(attempt, HttpStatusCode.Conflict, ReceivableErrorCodes.SettlementPending);
            Assert.Contains(sale.TicketNumber, error.Message);
        }

        Assert.Equal(ReceivableStatuses.Open, await StatusAsync(sale));
        Assert.Equal(ReceivableStatuses.Open, await StatusAsync(companion));
        // La vente ne s'annule pas non plus tant que le TPE n'a pas répondu.
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.CancelAsync(server.AdminClient, sale.Id), HttpStatusCode.Conflict, ReceivableErrorCodes.SettlementPending);

        // Déblocage : un employé autorisé, même sur un autre poste, répond « refusé ».
        (await SalesTestKit.SettlementCardResultAsync(otherPoste, pending.Id, accepted: false)).EnsureSuccessStatusCode();
        Assert.Equal(otherEmployee, await kit.ScalarAsync<Guid>(
            "SELECT resolved_by FROM receivable_settlements WHERE id = @Id", new { pending.Id }));

        (await SettleCashAsync(4000m, sale.Receivable!.Id, companion.Receivable!.Id)).EnsureSuccessStatusCode();
        Assert.Equal(ReceivableStatuses.Settled, await StatusAsync(sale));
        Assert.Equal(ReceivableStatuses.Settled, await StatusAsync(companion));
    }

    [Fact]
    public async Task Reponses_simultanees_du_TPE_sur_un_reglement_une_seule_est_retenue()
    {
        var customer = await kit.CreateCustomerAsync();
        for (var round = 0; round < 5; round++)
        {
            var sale = await kit.SellUnpaidAsync(customer.Id, 900m);
            var pending = await SalesTestKit.ReadAsync<SettlementResult>(await SettleCardAsync(900m, sale.Receivable!.Id));

            var responses = await SalesTestKit.InParallelAsync(
                2, index => SalesTestKit.SettlementCardResultAsync(server.AdminClient, pending.Id, accepted: index == 0));

            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            await StockTestKit.AssertErrorAsync(
                responses.Single(response => response.StatusCode != HttpStatusCode.OK),
                HttpStatusCode.Conflict, ReceivableErrorCodes.SettlementNotPending);
            var settlement = await SalesTestKit.ReadAsync<SettlementResult>(
                await server.AdminClient.GetAsync(ReceivableRoutes.Settlement(pending.Id)));
            Assert.Equal(
                settlement.Status == PaymentStatuses.Confirmed ? ReceivableStatuses.Settled : ReceivableStatuses.Open,
                await StatusAsync(sale));
        }
    }

    // ---------- Tableau, indicateurs, clients ----------

    [Fact]
    public async Task Le_tableau_des_impayes_montre_ses_colonnes_ses_filtres_et_ses_indicateurs()
    {
        var before = (await kit.ReceivablesAsync()).Indicators;
        var customer = await kit.CreateCustomerAsync("Pharmacie Ould Ali");
        var other = await kit.CreateCustomerAsync();
        var (seller, sellerId) = await kit.CreateEmployeeClientAsync(Permissions.SalesCreate);
        var open = await kit.SellUnpaidAsync(customer.Id, 3500m, seller);
        var settled = await kit.SellUnpaidAsync(customer.Id, 6200m);
        var foreign = await kit.SellUnpaidAsync(other.Id, 1800m);
        var cancelled = await kit.SellUnpaidAsync(other.Id, 700m);
        (await SettleCardAsync(6200m, settled.Receivable!.Id)).EnsureSuccessStatusCode();
        var cardSettlement = (await kit.ReceivableAsync(settled)).PendingSettlementId!.Value;
        (await SalesTestKit.SettlementCardResultAsync(server.AdminClient, cardSettlement, accepted: true)).EnsureSuccessStatusCode();
        (await SalesTestKit.CancelAsync(server.AdminClient, cancelled.Id)).EnsureSuccessStatusCode();

        var table = await kit.ReceivablesAsync();

        // Indicateurs : impayés ouverts, nombre de tickets, total des créances, créances soldées.
        Assert.Equal(before.OpenAmount + 5300m, table.Indicators.OpenAmount);
        Assert.Equal(before.OpenCount + 2, table.Indicators.OpenCount);
        Assert.Equal(before.TotalAmount + 11_500m, table.Indicators.TotalAmount);
        Assert.Equal(before.TotalCount + 3, table.Indicators.TotalCount);
        Assert.Equal(before.SettledAmount + 6200m, table.Indicators.SettledAmount);
        Assert.Equal(before.SettledCount + 1, table.Indicators.SettledCount);

        var openLine = table.Lines.Single(line => line.Id == open.Receivable!.Id);
        Assert.Equal(open.TicketNumber, openLine.TicketNumber);
        Assert.Equal(open.TicketBarcode, openLine.TicketBarcode);
        Assert.Equal("Pharmacie Ould Ali", openLine.CustomerName);
        Assert.Equal(3500m, openLine.Amount);
        Assert.Equal(ReceivableStatuses.Open, openLine.Status);
        Assert.Equal(sellerId, openLine.EmployeeId);
        Assert.Null(openLine.SettledAt);
        Assert.Null(openLine.SettlementMethod);
        var settledLine = table.Lines.Single(line => line.Id == settled.Receivable!.Id);
        Assert.Equal(ReceivableStatuses.Settled, settledLine.Status);
        Assert.NotNull(settledLine.SettledAt);
        Assert.Equal(PaymentMethods.Card, settledLine.SettlementMethod);
        Assert.Equal("Administratrice Test", settledLine.SettledByName);

        // Filtres.
        var openOnly = (await kit.ReceivablesAsync("?statut=OUVERTE")).Lines;
        Assert.Contains(open.Receivable!.Id, openOnly.Select(line => line.Id));
        Assert.All(openOnly, line => Assert.Equal(ReceivableStatuses.Open, line.Status));
        var settledHistory = (await kit.ReceivablesAsync("?statut=SOLDEE")).Lines;
        Assert.Contains(settled.Receivable!.Id, settledHistory.Select(line => line.Id));
        Assert.All(settledHistory, line => Assert.NotNull(line.SettledAt));
        Assert.Equal(
            new[] { open.Receivable!.Id, settled.Receivable!.Id }.Order(),
            (await kit.ReceivablesAsync($"?client={customer.Id}")).Lines.Select(line => line.Id).Order());
        Assert.Equal(
            [foreign.Receivable!.Id],
            (await kit.ReceivablesAsync($"?client={other.Id}&statut=OUVERTE")).Lines.Select(line => line.Id));
        Assert.Equal([open.Receivable!.Id], (await kit.ReceivablesAsync($"?q={open.TicketNumber}")).Lines.Select(line => line.Id));
        Assert.Equal([open.Receivable!.Id], (await kit.ReceivablesAsync($"?q={open.TicketBarcode}")).Lines.Select(line => line.Id));
        var today = DateOnly.FromDateTime(await kit.ScalarAsync<DateTime>("SELECT current_date"));
        Assert.Contains(
            open.Receivable!.Id,
            (await kit.ReceivablesAsync($"?du={today:yyyy-MM-dd}&au={today:yyyy-MM-dd}")).Lines.Select(line => line.Id));
        Assert.Empty((await kit.ReceivablesAsync($"?du={today.AddDays(1):yyyy-MM-dd}")).Lines);
        await StockTestKit.AssertErrorAsync(
            await server.AdminClient.GetAsync(ReceivableRoutes.Receivables + "?statut=PERDUE"),
            HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);

        // Recherche par scan du code-barres du ticket, ou par son numéro.
        var scanned = await SalesTestKit.ReadAsync<ReceivableLine>(
            await server.AdminClient.GetAsync(ReceivableRoutes.ByTicket(open.TicketBarcode)));
        Assert.Equal(open.Receivable!.Id, scanned.Id);
        await StockTestKit.AssertErrorAsync(
            await server.AdminClient.GetAsync(ReceivableRoutes.ByTicket("TK9999999999")), HttpStatusCode.NotFound, ApiErrorCodes.NotFound);
    }

    [Fact]
    public async Task Un_client_se_cree_vite_se_recherche_et_sa_fiche_montre_ses_creances()
    {
        var name = $"Zerrouki Amel {Guid.NewGuid():N}";
        var customer = await kit.CreateCustomerAsync($"  {name}  ", "0550 12 34 56");
        var noPhone = await kit.CreateCustomerAsync($"Sans téléphone {Guid.NewGuid():N}");
        var open = await kit.SellUnpaidAsync(customer.Id, 1500m);
        var settled = await kit.SellUnpaidAsync(customer.Id, 800m);
        (await SettleCashAsync(800m, settled.Receivable!.Id)).EnsureSuccessStatusCode();

        Assert.Equal(name, customer.Name);
        Assert.Null(noPhone.Phone);

        async Task<List<CustomerSummary>> SearchAsync(string term) =>
            (await server.AdminClient.GetFromJsonAsync<List<CustomerSummary>>(
                $"{CustomerRoutes.CustomerSearch}?q={Uri.EscapeDataString(term)}"))!;

        // Mots dans le désordre, sans accents ni majuscules ; ou par téléphone.
        var found = Assert.Single(await SearchAsync($"amel ZERROUKI {name[^32..]}"));
        Assert.Equal(customer.Id, found.Id);
        Assert.Equal(1500m, found.OpenAmount);
        Assert.Equal(1, found.OpenCount);
        Assert.Contains(customer.Id, (await SearchAsync("0550123456")).Select(result => result.Id));
        Assert.Contains(noPhone.Id, (await SearchAsync("sans telephone")).Select(result => result.Id));
        Assert.Empty(await SearchAsync("aucun client de ce nom"));

        var detail = await SalesTestKit.ReadAsync<CustomerDetail>(await server.AdminClient.GetAsync(CustomerRoutes.Customer(customer.Id)));
        Assert.Equal(name, detail.Name);
        Assert.Equal("0550 12 34 56", detail.Phone);
        Assert.Equal(1500m, detail.OpenAmount);
        Assert.Equal(1, detail.OpenCount);
        Assert.Equal(
            new[] { open.Receivable!.Id, settled.Receivable!.Id }.Order(), detail.Receivables.Select(line => line.Id).Order());

        await StockTestKit.AssertErrorAsync(
            await server.AdminClient.GetAsync(CustomerRoutes.Customer(Guid.NewGuid())), HttpStatusCode.NotFound, ApiErrorCodes.NotFound);
        await StockTestKit.AssertErrorAsync(
            await server.AdminClient.PostAsJsonAsync(CustomerRoutes.Customers, new CreateCustomerRequest(Guid.NewGuid(), "   ")),
            HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);

        // Un double clic sur la création ne crée qu'un client.
        var request = new CreateCustomerRequest(Guid.NewGuid(), $"Doublon {Guid.NewGuid():N}");
        var first = await SalesTestKit.ReadAsync<CustomerSummary>(await server.AdminClient.PostAsJsonAsync(CustomerRoutes.Customers, request));
        var replay = await SalesTestKit.ReadAsync<CustomerSummary>(await server.AdminClient.PostAsJsonAsync(CustomerRoutes.Customers, request));
        Assert.Equal(first.Id, replay.Id);
    }

    // ---------- Identité et permissions ----------

    [Fact]
    public async Task Le_reglement_est_attribue_a_l_employe_identifie_au_moment_de_l_encaissement()
    {
        var customer = await kit.CreateCustomerAsync();
        var first = await kit.SellUnpaidAsync(customer.Id, 1000m);
        var second = await kit.SellUnpaidAsync(customer.Id, 2000m);
        var (firstNumber, firstId) = await kit.CreateEmployeeAsync(Permissions.ReceivablesSettle);
        var (secondNumber, secondId) = await kit.CreateEmployeeAsync(Permissions.ReceivablesSettle);
        var poste = await server.CreatePosteClientAsync();

        await ServerFixture.IdentifyAsync(poste, firstNumber, SalesTestKit.EmployeeSecret);
        var byFirst = await SalesTestKit.ReadAsync<SettlementResult>(
            await SalesTestKit.SettleAsync(poste, PaymentMethods.Cash, 1000m, null, first.Receivable!.Id));
        await ServerFixture.IdentifyAsync(poste, secondNumber, SalesTestKit.EmployeeSecret);
        var bySecond = await SalesTestKit.ReadAsync<SettlementResult>(
            await SalesTestKit.SettleAsync(poste, PaymentMethods.Cash, 2000m, null, second.Receivable!.Id));

        Assert.Equal(firstId, byFirst.EmployeeId);
        Assert.Equal(secondId, bySecond.EmployeeId);
        Assert.Equal(firstId, await kit.ScalarAsync<Guid>(
            "SELECT employee_id FROM receivable_settlements WHERE id = @Id", new { byFirst.Id }));
        // Le vendeur du ticket reste le vendeur : le règlement ne réécrit pas la vente.
        Assert.Equal(server.AdminId, (await kit.ReceivableAsync(first)).EmployeeId);
    }

    [Fact]
    public async Task Chaque_route_des_impayes_et_des_clients_est_refusee_sans_son_droit()
    {
        var customer = await kit.CreateCustomerAsync();
        var sale = await kit.SellUnpaidAsync(customer.Id, 1000m);
        var pendingSale = await kit.SellUnpaidAsync(customer.Id, 500m);
        var pending = await SalesTestKit.ReadAsync<SettlementResult>(await SettleCardAsync(500m, pendingSale.Receivable!.Id));
        var (viewer, _) = await kit.CreateEmployeeClientAsync(Permissions.ReceivablesView, Permissions.PaymentsCollect);
        var (settler, _) = await kit.CreateEmployeeClientAsync(Permissions.ReceivablesSettle, Permissions.PaymentsCollect);
        var (stranger, _) = await kit.CreateEmployeeClientAsync(Permissions.ProductsView, Permissions.SalesHistoryView);

        var refusals = new[]
        {
            // Consulter ne donne pas le droit de régler.
            await SalesTestKit.SettleAsync(viewer, PaymentMethods.Cash, 1000m, null, sale.Receivable!.Id),
            await SalesTestKit.SettlementCardResultAsync(viewer, pending.Id, accepted: true),
            // Régler ne donne pas le droit de consulter le tableau ni les fiches.
            await settler.GetAsync(ReceivableRoutes.Receivables),
            await settler.GetAsync(ReceivableRoutes.ByTicket(sale.TicketNumber)),
            await settler.GetAsync(ReceivableRoutes.Settlement(pending.Id)),
            await settler.GetAsync(CustomerRoutes.Customer(customer.Id)),
            // Créer un client demande le droit de vendre.
            await settler.PostAsJsonAsync(CustomerRoutes.Customers, new CreateCustomerRequest(Guid.NewGuid(), "Interdit")),
            await stranger.GetAsync(CustomerRoutes.CustomerSearch + "?q=a"),
            await stranger.GetAsync(ReceivableRoutes.Receivables),
        };
        foreach (var refusal in refusals)
        {
            await StockTestKit.AssertErrorAsync(refusal, HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        }

        Assert.Equal(ReceivableStatuses.Open, await StatusAsync(sale));
        Assert.Equal(ReceivableStatuses.Open, await StatusAsync(pendingSale));
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SettleAsync(
                await server.CreatePosteClientAsync(), PaymentMethods.Cash, 1000m, null, sale.Receivable!.Id),
            HttpStatusCode.Unauthorized, ApiErrorCodes.EmployeeNotIdentified);

        // Avec le droit, chacun fait sa part.
        (await viewer.GetAsync(ReceivableRoutes.Receivables)).EnsureSuccessStatusCode();
        (await viewer.GetAsync(CustomerRoutes.CustomerSearch + "?q=a")).EnsureSuccessStatusCode();
        (await settler.GetAsync(CustomerRoutes.CustomerSearch + "?q=a")).EnsureSuccessStatusCode();
        (await SalesTestKit.SettleAsync(settler, PaymentMethods.Cash, 1000m, null, sale.Receivable!.Id)).EnsureSuccessStatusCode();
        (await SalesTestKit.SettlementCardResultAsync(settler, pending.Id, accepted: false)).EnsureSuccessStatusCode();
    }
}
