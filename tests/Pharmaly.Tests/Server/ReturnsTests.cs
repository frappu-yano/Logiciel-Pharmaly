using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Returns;

namespace Pharmaly.Tests.Server;

[Collection(SalesTestKit.Collection)]
public sealed class ReturnsTests(ServerFixture server) : IClassFixture<ServerFixture>, IAsyncLifetime
{
    private const string Resellable = "Retour revendable";
    private const string Damaged = "Produit endommagé";
    private const string Defective = "Produit défectueux";
    private const string SaleError = "Erreur de vente";

    private readonly ClosureTestKit kit = new(server);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>Après chaque scénario : stock et argent cohérents, sur toute la base.</summary>
    public async ValueTask DisposeAsync()
    {
        await StockInvariant.AssertHoldsAsync(server.DataSource);
        await MoneyInvariant.AssertHoldsAsync(server.DataSource);
    }

    private Task<decimal> LevelAsync(Guid productId, Guid locationId) => kit.Sales.Stock.ReadLevelAsync(productId, locationId);

    private Task<long> CountReturnsAsync(Guid saleId) =>
        kit.Sales.ScalarAsync<long>("SELECT count(*) FROM returns WHERE sale_id = @saleId", new { saleId });

    private Task<string> ReceivableStateAsync(Guid saleId) =>
        kit.Sales.ScalarAsync<string>("SELECT amount::text || ' ' || status FROM receivables WHERE sale_id = @saleId", new { saleId });

    private static async Task<CashSessionSummary> SummaryAsync(HttpClient client) =>
        await SalesTestKit.ReadAsync<CashSessionSummary>(await client.GetAsync(CashRoutes.SessionSummary));

    private static async Task<SaleDetail> SaleAsync(HttpClient client, Guid saleId) =>
        await SalesTestKit.ReadAsync<SaleDetail>(await client.GetAsync(SaleRoutes.Sale(saleId)));

    // ---------- Destination du stock selon le motif ----------

    [Fact]
    public async Task Un_retour_revendable_remet_le_stock_en_vente_et_rembourse_en_especes()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store, 1000m);
        var sale = await kit.SellAsync(poste, store, price: 1200m, quantity: 2);
        var productId = sale.Lines[0].ProductId;
        Assert.Equal(98m, await LevelAsync(productId, store.MainLocationId));
        var before = JsonSerializer.Serialize(await SaleAsync(poste, sale.Id));

        var created = await kit.ReturnOkAsync(poste, sale, Resellable);

        // Document : numéro stable, motif, employé réel, session, remboursement calculé par le serveur.
        Assert.Equal($"{store.Code}-R000001", created.ReturnNumber);
        Assert.Equal(sale.Id, created.SaleId);
        Assert.Equal(sale.TicketNumber, created.TicketNumber);
        Assert.Equal(Resellable, created.ReasonLabel);
        Assert.Equal(ReturnStockDestinations.Sellable, created.StockDestination);
        Assert.Equal(store.MainLocationId, created.DestinationLocationId);
        Assert.Equal(server.AdminId, created.EmployeeId);
        Assert.Equal(PaymentMethods.Cash, created.RefundMethod);
        Assert.Equal(1200m, created.RefundAmount);
        var line = Assert.Single(created.Lines);
        Assert.Equal((productId, 1m, 1200m, 1m, 0m), (line.ProductId, line.Quantity, line.RefundAmount, line.RestockedQuantity, line.NotRestockedQuantity));

        // Stock : un mouvement RETOUR distinct, vers l'emplacement vendable.
        Assert.Equal(99m, await LevelAsync(productId, store.MainLocationId));
        Assert.Equal(1, await kit.Sales.Stock.CountMovementsAsync(productId, StockMovementTypes.Return));
        Assert.Equal(1, await kit.Sales.ScalarAsync<int>(
            """
            SELECT count(*) FROM stock_movements
            WHERE source_type = 'RETOUR' AND source_id = @id AND movement_type = 'RETOUR' AND quantity = 1 AND location_id = @location
            """,
            new { id = created.Id.ToString(), location = store.MainLocationId }));

        // Trésorerie : sortie d'espèces de la caisse du poste, le jour du retour.
        var summary = await SummaryAsync(poste);
        Assert.Equal(2400m, summary.SaleCashCollected);
        Assert.Equal(1200m, summary.ReturnCashRefunds);
        Assert.Equal(0m, summary.ReturnCardRefunds);
        Assert.Equal(1000m + 2400m - 1200m, summary.ExpectedCash);
        Assert.Equal(summary.Session.Id, created.CashSessionId);

        // La vente d'origine n'est pas réécrite.
        Assert.Equal(before, JsonSerializer.Serialize(await SaleAsync(poste, sale.Id)));
        Assert.Equal(1, await kit.Sales.Stock.CountAuditAsync(ReturnAuditActions.ReturnCreated, created.Id.ToString()));

        // Historique : par identifiant, par ticket, par dates.
        Assert.Equal(
            JsonSerializer.Serialize(created),
            JsonSerializer.Serialize(await poste.GetFromJsonAsync<ReturnDetail>(ReturnRoutes.Return(created.Id))));
        var byTicket = await poste.GetFromJsonAsync<List<ReturnSummary>>($"{ReturnRoutes.Returns}?ticket={sale.TicketNumber}");
        Assert.Equal(created.Id, Assert.Single(byTicket!).Id);
        var bySale = await poste.GetFromJsonAsync<List<ReturnSummary>>($"{ReturnRoutes.Returns}?vente={sale.Id}");
        Assert.Equal(created.ReturnNumber, Assert.Single(bySale!).ReturnNumber);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var byDates = await poste.GetFromJsonAsync<List<ReturnSummary>>(
            $"{ReturnRoutes.Returns}?du={today.AddDays(-1):yyyy-MM-dd}&au={today.AddDays(1):yyyy-MM-dd}");
        Assert.Contains(byDates!, summaryLine => summaryLine.Id == created.Id);
        Assert.Empty((await poste.GetFromJsonAsync<List<ReturnSummary>>($"{ReturnRoutes.Returns}?au={today.AddDays(-2):yyyy-MM-dd}"))!);
    }

    [Fact]
    public async Task Un_retour_de_produit_endommage_ne_cree_aucun_mouvement_de_stock_mais_reste_trace()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store);
        var sale = await kit.SellAsync(poste, store, price: 750m, quantity: 3);
        var productId = sale.Lines[0].ProductId;
        var movementsBefore = await kit.Sales.CountMovementsAsync();

        var created = await kit.ReturnOkAsync(poste, sale, Damaged, quantity: 2);

        // Aucune réintégration : ni mouvement, ni solde modifié, nulle part.
        Assert.Equal(movementsBefore, await kit.Sales.CountMovementsAsync());
        Assert.Equal(0, await kit.Sales.Stock.CountMovementsAsync(productId, StockMovementTypes.Return));
        Assert.Equal(97m, await LevelAsync(productId, store.MainLocationId));
        Assert.Equal(0m, await LevelAsync(productId, store.SetAsideLocationId));

        // Le retour est tout de même enregistré, remboursé et tracé, avec la quantité sortie du circuit.
        Assert.Equal(ReturnStockDestinations.None, created.StockDestination);
        Assert.Null(created.DestinationLocationId);
        Assert.Equal(1500m, created.RefundAmount);
        var line = Assert.Single(created.Lines);
        Assert.Equal((2m, 0m, 2m), (line.Quantity, line.RestockedQuantity, line.NotRestockedQuantity));
        Assert.Equal(1, await CountReturnsAsync(sale.Id));
        Assert.Equal(1, await kit.Sales.Stock.CountAuditAsync(ReturnAuditActions.ReturnCreated, created.Id.ToString()));
        Assert.Equal(1500m, (await SummaryAsync(poste)).ReturnCashRefunds);
        Assert.Equal(2m, await kit.Sales.ScalarAsync<decimal>(
            "SELECT sum(quantity - restocked_quantity) FROM return_lines WHERE return_id = @Id", new { created.Id }));
    }

    [Fact]
    public async Task Un_retour_de_produit_defectueux_va_a_l_emplacement_mis_a_l_ecart()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store);
        var sale = await kit.SellAsync(poste, store, price: 900m, quantity: 2);
        var productId = sale.Lines[0].ProductId;

        var created = await kit.ReturnOkAsync(poste, sale, Defective);

        Assert.Equal(ReturnStockDestinations.SetAside, created.StockDestination);
        Assert.Equal(store.SetAsideLocationId, created.DestinationLocationId);
        // Le stock vendable ne bouge pas : le produit défectueux n'est pas remis en vente.
        Assert.Equal(98m, await LevelAsync(productId, store.MainLocationId));
        Assert.Equal(1m, await LevelAsync(productId, store.SetAsideLocationId));
        Assert.Equal(1m, Assert.Single(created.Lines).RestockedQuantity);

        // « Erreur de vente » : retour au stock vendable (D8).
        var error = await kit.ReturnOkAsync(poste, sale, SaleError);
        Assert.Equal(store.MainLocationId, error.DestinationLocationId);
        Assert.Equal(99m, await LevelAsync(productId, store.MainLocationId));
    }

    // ---------- Quantités ----------

    [Fact]
    public async Task Une_quantite_superieure_au_vendu_est_refusee()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store);
        var sale = await kit.SellAsync(poste, store, price: 500m, quantity: 2);
        var reason = await kit.ReasonAsync(Resellable);
        var other = await kit.ProductAsync(store, 100m);

        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.ReturnFirstLineAsync(poste, sale, reason.Id, 3), HttpStatusCode.Conflict, ReturnErrorCodes.QuantityExceeded);
        // Produit absent du ticket, quantité nulle ou négative, aucune ligne, doublon : refusés.
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.ReturnAsync(poste, sale.Id, reason.Id, null, null, (other.Id, 1)),
            HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        foreach (var quantity in new[] { 0m, -1m, 0.0001m })
        {
            await StockTestKit.AssertErrorAsync(
                await ClosureTestKit.ReturnFirstLineAsync(poste, sale, reason.Id, quantity), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        }

        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.ReturnAsync(poste, sale.Id, reason.Id), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        var productId = sale.Lines[0].ProductId;
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.ReturnAsync(poste, sale.Id, reason.Id, null, null, (productId, 1), (productId, 1)),
            HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        // Produit vendu à l'unité : pas de fraction.
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.ReturnFirstLineAsync(poste, sale, reason.Id, 0.5m), HttpStatusCode.BadRequest, ApiErrorCodes.FractionalQuantity);
        // Ticket inconnu, motif inconnu.
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.ReturnAsync(poste, Guid.NewGuid(), reason.Id, null, null, (productId, 1)),
            HttpStatusCode.NotFound, ApiErrorCodes.NotFound);
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.ReturnAsync(poste, sale.Id, Guid.NewGuid(), null, null, (productId, 1)),
            HttpStatusCode.BadRequest, ReturnErrorCodes.InvalidReason);

        Assert.Equal(0, await CountReturnsAsync(sale.Id));
        Assert.Equal(98m, await LevelAsync(productId, store.MainLocationId));
        Assert.Equal(0m, (await SummaryAsync(poste)).ReturnCashRefunds);
    }

    [Fact]
    public async Task Le_cumul_des_retours_ne_depasse_jamais_la_quantite_vendue()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store);
        var sale = await kit.SellAsync(poste, store, price: 400m, quantity: 3);
        var productId = sale.Lines[0].ProductId;
        var reason = await kit.ReasonAsync(Resellable);

        (await ClosureTestKit.ReturnFirstLineAsync(poste, sale, reason.Id, 2)).EnsureSuccessStatusCode();
        var error = await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.ReturnFirstLineAsync(poste, sale, reason.Id, 2), HttpStatusCode.Conflict, ReturnErrorCodes.QuantityExceeded);
        Assert.Contains("1 encore retournable", error.Message);

        // Ce qui reste retournable est visible avant de saisir.
        var returnable = await SalesTestKit.ReadAsync<ReturnableSale>(await poste.GetAsync(ReturnRoutes.ByTicket(sale.TicketBarcode)));
        Assert.True(returnable.CanReturn);
        var line = Assert.Single(returnable.Lines);
        Assert.Equal((3m, 2m, 1m, 1200m, 800m, 400m), (line.SoldQuantity, line.ReturnedQuantity, line.ReturnableQuantity, line.PaidAmount, line.RefundedAmount, line.RefundableAmount));

        (await ClosureTestKit.ReturnFirstLineAsync(poste, sale, reason.Id, 1)).EnsureSuccessStatusCode();
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.ReturnFirstLineAsync(poste, sale, reason.Id, 1), HttpStatusCode.Conflict, ReturnErrorCodes.QuantityExceeded);

        Assert.Equal(2, await CountReturnsAsync(sale.Id));
        Assert.Equal(100m, await LevelAsync(productId, store.MainLocationId));
        Assert.Equal(1200m, (await SummaryAsync(poste)).ReturnCashRefunds);
        Assert.False((await SalesTestKit.ReadAsync<ReturnableSale>(await poste.GetAsync(ReturnRoutes.ByTicket(sale.TicketNumber)))).CanReturn);
    }

    [Fact]
    public async Task Deux_retours_simultanes_ne_depassent_pas_la_quantite_vendue()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store, 0m);
        var otherPoste = await kit.CreateRegisterClientAsync(store, 0m);
        var reason = await kit.ReasonAsync(Resellable);

        // Dernier exemplaire retournable : deux caisses le retournent en même temps, plusieurs fois.
        for (var round = 0; round < 3; round++)
        {
            var single = await kit.SellAsync(poste, store, price: 1000m, quantity: 1);
            var results = await SalesTestKit.InParallelAsync(
                6, index => ClosureTestKit.ReturnFirstLineAsync(index % 2 == 0 ? poste : otherPoste, single, reason.Id, 1));
            Assert.Equal(1, results.Count(response => response.StatusCode == HttpStatusCode.OK));
            foreach (var refused in results.Where(response => response.StatusCode != HttpStatusCode.OK))
            {
                await StockTestKit.AssertErrorAsync(refused, HttpStatusCode.Conflict, ReturnErrorCodes.QuantityExceeded);
            }

            Assert.Equal(1, await CountReturnsAsync(single.Id));
            Assert.Equal(100m, await LevelAsync(single.Lines[0].ProductId, store.MainLocationId));
        }

        // Cinq vendus, douze retours simultanés de deux : deux passent, pas un de plus.
        var sale = await kit.SellAsync(poste, store, price: 333.33m, quantity: 5, discountKind: DiscountKinds.Percent, discountValue: 7m);
        var parallel = await SalesTestKit.InParallelAsync(
            12, index => ClosureTestKit.ReturnFirstLineAsync(index % 2 == 0 ? poste : otherPoste, sale, reason.Id, 2));
        Assert.Equal(2, parallel.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(4m, await kit.Sales.ScalarAsync<decimal>(
            "SELECT sum(rl.quantity) FROM return_lines rl JOIN returns r ON r.id = rl.return_id WHERE r.sale_id = @Id", new { sale.Id }));
        Assert.Equal(99m, await LevelAsync(sale.Lines[0].ProductId, store.MainLocationId));

        // Espèces sorties = somme exacte des remboursements, répartie sur les deux caisses.
        var refunded = await kit.Sales.ScalarAsync<decimal>(
            "SELECT sum(refund_amount) FROM returns WHERE store_id = @Id", new { store.Id });
        Assert.Equal(3000m + ReturnMath.DueForLine(sale.Total, 5, 4), refunded);
        Assert.Equal(refunded, (await SummaryAsync(poste)).ReturnCashRefunds + (await SummaryAsync(otherPoste)).ReturnCashRefunds);
    }

    // ---------- Remboursement ----------

    [Fact]
    public async Task Le_remboursement_suit_la_remise_en_pourcentage_au_centime_pres()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store);
        var reason = await kit.ReasonAsync(Resellable);
        var a = await kit.ProductAsync(store, 333.33m);
        var b = await kit.ProductAsync(store, 100.10m);
        // Sous-total 999,99 + 100,10 = 1 100,09 ; remise 15 % = 165,01 (165,0135 arrondi) ; total 935,08.
        var sale = await SalesTestKit.ReadAsync<SaleDetail>(await SalesTestKit.SellAsync(poste, new CreateSaleRequest(
            Guid.NewGuid(), [new SaleLineRequest(a.Id, 3), new SaleLineRequest(b.Id, 1)], SalePaymentModes.Cash,
            DiscountKinds.Percent, 15m, 1000m)));
        Assert.Equal((1100.09m, 165.01m, 935.08m), (sale.Subtotal, sale.DiscountAmount, sale.Total));

        // Payé pour A : 935,08 × 999,99 ÷ 1 100,09 = 849,99 ; pour B : le reste, 85,09.
        var returnable = await SalesTestKit.ReadAsync<ReturnableSale>(await poste.GetAsync(ReturnRoutes.ByTicket(sale.TicketNumber)));
        Assert.Equal(new[] { 849.99m, 85.09m }, returnable.Lines.Select(line => line.PaidAmount));

        // L'aperçu annonce le remboursement sans rien enregistrer.
        var preview = await SalesTestKit.ReadAsync<ReturnPreview>(await poste.PostAsJsonAsync(
            ReturnRoutes.Preview, new PreviewReturnRequest(sale.Id, reason.Id, [new ReturnLineRequest(a.Id, 1)])));
        Assert.Equal((283.33m, PaymentMethods.Cash), (preview.RefundAmount, preview.RefundMethod));
        Assert.Equal(0, await CountReturnsAsync(sale.Id));

        // Un montant attendu différent de celui du serveur est refusé, rien n'est remboursé.
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.ReturnAsync(poste, sale.Id, reason.Id, null, 333.33m, (a.Id, 1)),
            HttpStatusCode.Conflict, ReturnErrorCodes.RefundMismatch);
        Assert.Equal(0, await CountReturnsAsync(sale.Id));

        // Une unité de A : 849,99 ÷ 3 = 283,33 — jamais le prix affiché de 333,33.
        var first = await SalesTestKit.ReadAsync<ReturnDetail>(
            await ClosureTestKit.ReturnAsync(poste, sale.Id, reason.Id, null, 283.33m, (a.Id, 1)));
        Assert.Equal(283.33m, first.RefundAmount);
        // Le reste de A et B ensemble : 566,66 + 85,09.
        var rest = await SalesTestKit.ReadAsync<ReturnDetail>(
            await ClosureTestKit.ReturnAsync(poste, sale.Id, reason.Id, null, null, (b.Id, 1), (a.Id, 2)));
        Assert.Equal(new[] { 85.09m, 566.66m }, rest.Lines.Select(line => line.RefundAmount));
        Assert.Equal(651.75m, rest.RefundAmount);

        // Tout est retourné : remboursé exactement le total payé, pas un centime de plus.
        Assert.Equal(sale.Total, first.RefundAmount + rest.RefundAmount);
        Assert.Equal(1000m + 935.08m - 935.08m, (await SummaryAsync(poste)).ExpectedCash);
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.ReturnAsync(poste, sale.Id, reason.Id, null, null, (a.Id, 1)),
            HttpStatusCode.Conflict, ReturnErrorCodes.QuantityExceeded);
    }

    [Fact]
    public async Task La_somme_des_remboursements_ne_depasse_jamais_le_total_du_ticket()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store);
        var reason = await kit.ReasonAsync(Damaged);

        // 3 × 10,00 avec 33,33 % de remise : remise 10,00 (9,999 arrondi), total 20,00.
        // Dû après 1, 2, 3 retours : 6,67 ; 13,33 ; 20,00 → remboursements 6,67 ; 6,66 ; 6,67.
        var percent = await kit.SellAsync(poste, store, price: 10m, quantity: 3, discountKind: DiscountKinds.Percent, discountValue: 33.33m);
        Assert.Equal(20m, percent.Total);
        var refunds = new List<decimal>();
        for (var unit = 0; unit < 3; unit++)
        {
            refunds.Add((await SalesTestKit.ReadAsync<ReturnDetail>(
                await ClosureTestKit.ReturnFirstLineAsync(poste, percent, reason.Id, 1))).RefundAmount);
        }

        Assert.Equal(new[] { 6.67m, 6.66m, 6.67m }, refunds);
        Assert.Equal(percent.Total, refunds.Sum());

        // 3 × 10,00 avec 0,01 de remise en montant : total 29,99 → 10,00 ; 9,99 ; 10,00.
        var amount = await kit.SellAsync(poste, store, price: 10m, quantity: 3, discountKind: DiscountKinds.Amount, discountValue: 0.01m);
        refunds.Clear();
        for (var unit = 0; unit < 3; unit++)
        {
            refunds.Add((await SalesTestKit.ReadAsync<ReturnDetail>(
                await ClosureTestKit.ReturnFirstLineAsync(poste, amount, reason.Id, 1))).RefundAmount);
        }

        Assert.Equal(new[] { 10.00m, 9.99m, 10.00m }, refunds);
        Assert.Equal(29.99m, refunds.Sum());

        // Remise de 100 % : rien n'a été payé, rien n'est remboursé, le retour est tout de même tracé.
        var free = await kit.SellAsync(poste, store, price: 50m, quantity: 1, discountKind: DiscountKinds.Percent, discountValue: 100m);
        var freeReturn = await SalesTestKit.ReadAsync<ReturnDetail>(await ClosureTestKit.ReturnFirstLineAsync(poste, free, reason.Id, 1));
        Assert.Equal((0m, (string?)null), (freeReturn.RefundAmount, freeReturn.RefundMethod));

        // Produit vendu au poids : 1,5 à 199,99 = 299,99 (299,985 arrondi) ; retour de 0,5 puis de 1.
        var byWeight = await kit.ProductAsync(store, 199.99m, soldByUnit: false);
        var weighed = await SalesTestKit.ReadAsync<SaleDetail>(
            await SalesTestKit.SellAsync(poste, SalesTestKit.Cash(byWeight.Id, 1.5m)));
        Assert.Equal(299.99m, weighed.Total);
        var half = await SalesTestKit.ReadAsync<ReturnDetail>(await ClosureTestKit.ReturnFirstLineAsync(poste, weighed, reason.Id, 0.5m));
        var whole = await SalesTestKit.ReadAsync<ReturnDetail>(await ClosureTestKit.ReturnFirstLineAsync(poste, weighed, reason.Id, 1m));
        Assert.Equal((100.00m, 199.99m), (half.RefundAmount, whole.RefundAmount));

        Assert.Equal(
            0,
            await kit.Sales.ScalarAsync<long>(
                """
                SELECT count(*) FROM sales s
                WHERE s.store_id = @Id AND (SELECT COALESCE(sum(r.refund_amount), 0) FROM returns r WHERE r.sale_id = s.id) <> s.total
                """,
                new { store.Id }));
    }

    [Fact]
    public void Le_calcul_du_remboursement_repartit_le_total_paye_sans_perdre_ni_creer_un_centime()
    {
        // Parts des lignes : leur somme vaut toujours le total payé.
        Assert.Equal(new[] { 849.99m, 85.09m }, ReturnMath.PaidPerLine([999.99m, 100.10m], 1100.09m, 935.08m));
        Assert.Equal(new[] { 3.33m, 3.34m, 3.33m }, ReturnMath.PaidPerLine([10m, 10m, 10m], 30m, 10m));
        Assert.Equal(new[] { 0m, 0m }, ReturnMath.PaidPerLine([0m, 0m], 0m, 0m));
        Assert.Equal(new[] { 10m, 20m }, ReturnMath.PaidPerLine([10m, 20m], 30m, 30m));

        // La demie s'arrondit vers le haut (D25) : 0,05 payé pour 2 unités → 0,03 puis 0,02.
        Assert.Equal(0.03m, ReturnMath.RefundForLine(0.05m, 2, 0, 1));
        Assert.Equal(0.02m, ReturnMath.RefundForLine(0.05m, 2, 1, 1));

        // Quel que soit le découpage des retours, le cumul remboursé vaut la part payée, jamais plus.
        foreach (var (paid, sold) in new[] { (849.99m, 3m), (20m, 3m), (0.01m, 7m), (1234.56m, 13m), (299.99m, 1.5m) })
        {
            var step = sold / 6;
            var returned = 0m;
            var refunded = 0m;
            while (returned < sold)
            {
                var quantity = Math.Min(step, sold - returned);
                var refund = ReturnMath.RefundForLine(paid, sold, returned, quantity);
                Assert.True(refund >= 0);
                returned += quantity;
                refunded += refund;
                Assert.True(refunded <= paid);
            }

            Assert.Equal(paid, refunded);
        }
    }

    // ---------- Ventes non retournables ----------

    [Fact]
    public async Task Une_vente_annulee_ou_en_attente_de_TPE_n_est_pas_retournable()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store);
        var reason = await kit.ReasonAsync(Resellable);

        var cancelled = await kit.SellAsync(poste, store, price: 600m);
        (await SalesTestKit.CancelAsync(poste, cancelled.Id)).EnsureSuccessStatusCode();
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.ReturnFirstLineAsync(poste, cancelled, reason.Id), HttpStatusCode.Conflict, ReturnErrorCodes.SaleNotReturnable);
        var blocked = await SalesTestKit.ReadAsync<ReturnableSale>(await poste.GetAsync(ReturnRoutes.ByTicket(cancelled.TicketNumber)));
        Assert.Equal((false, ReturnErrorCodes.SaleNotReturnable), (blocked.CanReturn, blocked.BlockedCode));

        var card = await kit.SellAsync(poste, store, price: 800m, mode: SalePaymentModes.Card, quantity: 2);
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.ReturnFirstLineAsync(poste, card, reason.Id), HttpStatusCode.Conflict, ReturnErrorCodes.SaleNotReturnable);
        Assert.Equal(0, await CountReturnsAsync(cancelled.Id) + await CountReturnsAsync(card.Id));
        Assert.Equal(0, await kit.Sales.ScalarAsync<long>("SELECT count(*) FROM returns WHERE store_id = @Id", new { store.Id }));

        // Carte acceptée : le retour devient possible, remboursé par carte, sans sortie d'espèces.
        (await SalesTestKit.CardResultAsync(poste, card.Id, accepted: true)).EnsureSuccessStatusCode();
        var cashBefore = (await SummaryAsync(poste)).ExpectedCash;
        var created = await SalesTestKit.ReadAsync<ReturnDetail>(await ClosureTestKit.ReturnFirstLineAsync(poste, card, reason.Id));
        Assert.Equal((PaymentMethods.Card, 800m), (created.RefundMethod, created.RefundAmount));
        var summary = await SummaryAsync(poste);
        Assert.Equal(cashBefore, summary.ExpectedCash);
        Assert.Equal((0m, 800m), (summary.ReturnCashRefunds, summary.ReturnCardRefunds));
        Assert.Equal(0, await kit.Sales.ScalarAsync<long>(
            "SELECT count(*) FROM cash_movements WHERE store_id = @Id AND kind <> 'REMBOURSEMENT_VENTE'", new { store.Id }));
    }

    [Fact]
    public async Task Une_vente_qui_a_un_retour_ne_peut_plus_etre_annulee()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store);
        var sale = await kit.SellAsync(poste, store, price: 300m, quantity: 2);
        await kit.ReturnOkAsync(poste, sale, Resellable);

        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.CancelAsync(poste, sale.Id), HttpStatusCode.Conflict, ReturnErrorCodes.SaleHasReturns);

        Assert.Equal(SaleStatuses.Completed, (await SaleAsync(poste, sale.Id)).Status);
        Assert.Equal(99m, await LevelAsync(sale.Lines[0].ProductId, store.MainLocationId));
        // Le reste se traite par un autre retour.
        await kit.ReturnOkAsync(poste, sale, Resellable);
        Assert.Equal(100m, await LevelAsync(sale.Lines[0].ProductId, store.MainLocationId));
    }

    [Fact]
    public async Task Une_vente_impayee_n_accepte_aucun_retour_tant_que_sa_creance_est_ouverte()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store, 1000m);
        var customer = await kit.Sales.CreateCustomerAsync();
        var reason = await kit.ReasonAsync(Resellable);
        var sale = await kit.SellAsync(poste, store, price: 1750m, mode: SalePaymentModes.Unpaid, quantity: 2, customerId: customer.Id);
        var productId = sale.Lines[0].ProductId;

        // Créance ouverte : pas de retour, donc ni remboursement ni créance réduite (pas de règlement partiel déguisé).
        var error = await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.ReturnFirstLineAsync(poste, sale, reason.Id), HttpStatusCode.Conflict, ReturnErrorCodes.ReceivableOpen);
        Assert.Contains("annulez la vente", error.Message);
        var blocked = await SalesTestKit.ReadAsync<ReturnableSale>(await poste.GetAsync(ReturnRoutes.ByTicket(sale.TicketNumber)));
        Assert.Equal((false, ReturnErrorCodes.ReceivableOpen), (blocked.CanReturn, blocked.BlockedCode));
        await StockTestKit.AssertErrorAsync(
            await poste.PostAsJsonAsync(ReturnRoutes.Preview, new PreviewReturnRequest(sale.Id, reason.Id, [new ReturnLineRequest(productId, 1)])),
            HttpStatusCode.Conflict, ReturnErrorCodes.ReceivableOpen);

        // Même refus pendant qu'un règlement par carte attend sa réponse.
        var pending = await SalesTestKit.ReadAsync<SettlementResult>(
            await SalesTestKit.SettleAsync(poste, PaymentMethods.Card, 3500m, null, sale.Receivable!.Id));
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.ReturnFirstLineAsync(poste, sale, reason.Id), HttpStatusCode.Conflict, ReturnErrorCodes.ReceivableOpen);
        (await SalesTestKit.SettlementCardResultAsync(poste, pending.Id, accepted: false)).EnsureSuccessStatusCode();

        Assert.Equal(0, await CountReturnsAsync(sale.Id));
        Assert.Equal(98m, await LevelAsync(productId, store.MainLocationId));
        Assert.Equal("3500.00 OUVERTE", await ReceivableStateAsync(sale.Id));
        Assert.Equal(1000m, (await SummaryAsync(poste)).ExpectedCash);

        // Une fois le ticket réglé en entier, le retour rembourse par le moyen du règlement.
        (await SalesTestKit.SettleAsync(poste, PaymentMethods.Cash, 3500m, null, sale.Receivable.Id)).EnsureSuccessStatusCode();
        var created = await SalesTestKit.ReadAsync<ReturnDetail>(await ClosureTestKit.ReturnFirstLineAsync(poste, sale, reason.Id));
        Assert.Equal((PaymentMethods.Cash, 1750m), (created.RefundMethod, created.RefundAmount));
        Assert.Equal(99m, await LevelAsync(productId, store.MainLocationId));
        Assert.Equal(1000m + 3500m - 1750m, (await SummaryAsync(poste)).ExpectedCash);
        // La créance reste soldée, de son montant d'origine : le retour ne la réécrit pas.
        Assert.Equal("3500.00 SOLDEE", await ReceivableStateAsync(sale.Id));

        // Réglée par carte : remboursement par carte, sans sortie d'espèces.
        var cardDebt = await kit.SellAsync(poste, store, price: 900m, mode: SalePaymentModes.Unpaid, customerId: customer.Id);
        var cardSettlement = await SalesTestKit.ReadAsync<SettlementResult>(
            await SalesTestKit.SettleAsync(poste, PaymentMethods.Card, 900m, null, cardDebt.Receivable!.Id));
        (await SalesTestKit.SettlementCardResultAsync(poste, cardSettlement.Id, accepted: true)).EnsureSuccessStatusCode();
        var cardReturn = await SalesTestKit.ReadAsync<ReturnDetail>(await ClosureTestKit.ReturnFirstLineAsync(poste, cardDebt, reason.Id));
        Assert.Equal((PaymentMethods.Card, 900m), (cardReturn.RefundMethod, cardReturn.RefundAmount));
        Assert.Equal(1000m + 3500m - 1750m, (await SummaryAsync(poste)).ExpectedCash);
    }

    // ---------- Idempotence, session, droits ----------

    [Fact]
    public async Task Le_meme_identifiant_d_operation_n_enregistre_qu_un_seul_retour()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store);
        var sale = await kit.SellAsync(poste, store, price: 450m, quantity: 4);
        var productId = sale.Lines[0].ProductId;
        var reason = await kit.ReasonAsync(Resellable);
        var operationId = Guid.NewGuid();

        var first = await SalesTestKit.ReadAsync<ReturnDetail>(
            await ClosureTestKit.ReturnAsync(poste, sale.Id, reason.Id, operationId, null, (productId, 1)));
        var replayed = await SalesTestKit.ReadAsync<ReturnDetail>(
            await ClosureTestKit.ReturnAsync(poste, sale.Id, reason.Id, operationId, null, (productId, 1)));
        // Double clic : six envois simultanés d'un autre retour.
        var doubleClick = Guid.NewGuid();
        var parallel = await SalesTestKit.InParallelAsync(
            6, _ => ClosureTestKit.ReturnAsync(poste, sale.Id, reason.Id, doubleClick, null, (productId, 1)));

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(replayed));
        var ids = new HashSet<Guid>();
        foreach (var response in parallel)
        {
            ids.Add((await SalesTestKit.ReadAsync<ReturnDetail>(response)).Id);
        }

        Assert.Single(ids);
        Assert.Equal(2, await CountReturnsAsync(sale.Id));
        Assert.Equal(2, await kit.Sales.Stock.CountMovementsAsync(productId, StockMovementTypes.Return));
        Assert.Equal(98m, await LevelAsync(productId, store.MainLocationId));
        Assert.Equal(900m, (await SummaryAsync(poste)).ReturnCashRefunds);

        // Le même identifiant pour une autre demande est refusé, et le message ne prétend pas
        // que rien n'a été enregistré : la première demande, elle, l'est bien.
        var error = await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.ReturnAsync(poste, sale.Id, reason.Id, operationId, null, (productId, 2)),
            HttpStatusCode.Conflict, ApiErrorCodes.OperationMismatch);
        Assert.DoesNotContain("Rien n'a été enregistré", error.Message);
        Assert.Contains("reste enregistrée", error.Message);
        Assert.Equal(2, await CountReturnsAsync(sale.Id));
    }

    [Fact]
    public async Task Un_retour_exige_une_caisse_ouverte_sur_le_poste()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store);
        var closedPoste = await kit.CreateRegisterClientAsync(store, openingFloat: null);
        var sale = await kit.SellAsync(poste, store, price: 650m);
        var reason = await kit.ReasonAsync(Resellable);

        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.ReturnFirstLineAsync(closedPoste, sale, reason.Id), HttpStatusCode.Conflict, CashErrorCodes.NoOpenSession);
        // Sans réintégration non plus : le retour doit appartenir à une caisse pour ne pas échapper à la clôture.
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.ReturnFirstLineAsync(closedPoste, sale, (await kit.ReasonAsync(Damaged)).Id),
            HttpStatusCode.Conflict, CashErrorCodes.NoOpenSession);

        Assert.Equal(0, await CountReturnsAsync(sale.Id));
        Assert.Equal(99m, await LevelAsync(sale.Lines[0].ProductId, store.MainLocationId));

        // La caisse ouverte, le retour passe : les espèces sortent de la caisse qui fait le retour.
        await ClosureTestKit.OpenAsync(closedPoste, 2000m);
        (await ClosureTestKit.ReturnFirstLineAsync(closedPoste, sale, reason.Id)).EnsureSuccessStatusCode();
        Assert.Equal(2000m - 650m, (await SummaryAsync(closedPoste)).ExpectedCash);
        Assert.Equal(1000m + 650m, (await SummaryAsync(poste)).ExpectedCash);
    }

    [Fact]
    public async Task Les_retours_sont_reserves_aux_employes_habilites()
    {
        var store = await kit.CreateStoreAsync();
        var register = await kit.CreateRegisterAsync(store);
        var admin = await kit.Sales.AdminOnRegisterAsync(register.Login, register.Password);
        await ClosureTestKit.OpenAsync(admin, 1000m);
        var sale = await kit.SellAsync(admin, store, price: 500m, quantity: 3);
        var reason = await kit.ReasonAsync(Resellable);
        var productId = sale.Lines[0].ProductId;
        var preview = new PreviewReturnRequest(sale.Id, reason.Id, [new ReturnLineRequest(productId, 1)]);

        // Vendre, encaisser, annuler, consulter : aucun de ces droits ne permet un retour.
        var seller = await kit.EmployeeOnRegisterAsync(
            register, Permissions.SalesCreate, Permissions.PaymentsCollect, Permissions.SalesCancel, Permissions.SalesHistoryView);
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.ReturnFirstLineAsync(seller, sale, reason.Id), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        await StockTestKit.AssertErrorAsync(
            await seller.PostAsJsonAsync(ReturnRoutes.Preview, preview), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        await StockTestKit.AssertErrorAsync(
            await seller.GetAsync(ReturnRoutes.ByTicket(sale.TicketNumber)), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        Assert.Equal(0, await CountReturnsAsync(sale.Id));
        // L'historique des ventes donne accès à l'historique des retours, et les motifs se lisent.
        (await seller.GetAsync(ReturnRoutes.Returns)).EnsureSuccessStatusCode();
        (await seller.GetAsync(ReturnRoutes.Reasons)).EnsureSuccessStatusCode();

        // « Effectuer un retour » suffit, et l'employé réel est conservé.
        var (number, employeeId) = await kit.Sales.CreateEmployeeAsync(Permissions.ReturnsCreate);
        var returner = await server.CreatePosteClientAsync(register.Login, register.Password);
        await ServerFixture.IdentifyAsync(returner, number, SalesTestKit.EmployeeSecret);
        var created = await SalesTestKit.ReadAsync<ReturnDetail>(await ClosureTestKit.ReturnFirstLineAsync(returner, sale, reason.Id));
        Assert.Equal(employeeId, created.EmployeeId);
        (await returner.GetAsync(ReturnRoutes.Return(created.Id))).EnsureSuccessStatusCode();
        // Mais ni annuler une vente, ni administrer les motifs.
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.CancelAsync(returner, sale.Id), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        await StockTestKit.AssertErrorAsync(
            await returner.PostAsJsonAsync(ReturnRoutes.Reasons, new CreateReturnReasonRequest(Guid.NewGuid(), "Motif interdit", "VENDABLE")),
            HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        await StockTestKit.AssertErrorAsync(
            await returner.PutAsJsonAsync(ReturnRoutes.Reason(reason.Id), new UpdateReturnReasonRequest(Guid.NewGuid(), Resellable, "AUCUNE", true)),
            HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);

        // Aucun droit : pas même l'historique.
        var nobody = await kit.EmployeeOnRegisterAsync(register);
        await StockTestKit.AssertErrorAsync(
            await nobody.GetAsync(ReturnRoutes.Returns), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        await StockTestKit.AssertErrorAsync(
            await nobody.GetAsync(ReturnRoutes.Return(created.Id)), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        // Poste sans employé identifié.
        var poste = await server.CreatePosteClientAsync(register.Login, register.Password);
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.ReturnFirstLineAsync(poste, sale, reason.Id), HttpStatusCode.Unauthorized, ApiErrorCodes.EmployeeNotIdentified);

        // Un ticket d'un autre site est introuvable depuis ce poste.
        await kit.Sales.EnsureSessionOpenAsync();
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.ReturnFirstLineAsync(server.AdminClient, sale, reason.Id), HttpStatusCode.NotFound, ApiErrorCodes.NotFound);
        Assert.Equal(1, await CountReturnsAsync(sale.Id));
    }

    // ---------- Motifs configurables ----------

    [Fact]
    public async Task Les_motifs_de_retour_sont_configurables_et_portent_la_destination_du_stock()
    {
        // Valeurs initiales (D8).
        var defaults = await kit.ReasonsAsync();
        Assert.Equal(ReturnStockDestinations.Sellable, defaults.Single(reason => reason.Label == Resellable).StockDestination);
        Assert.Equal(ReturnStockDestinations.None, defaults.Single(reason => reason.Label == Damaged).StockDestination);
        Assert.Equal(ReturnStockDestinations.SetAside, defaults.Single(reason => reason.Label == Defective).StockDestination);
        Assert.Equal(ReturnStockDestinations.Sellable, defaults.Single(reason => reason.Label == SaleError).StockDestination);

        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store);
        var sale = await kit.SellAsync(poste, store, price: 200m, quantity: 5);
        var productId = sale.Lines[0].ProductId;

        // Création : utilisable aussitôt.
        var label = $"Rappel du fournisseur {Guid.NewGuid():N}";
        var operationId = Guid.NewGuid();
        var createRequest = new CreateReturnReasonRequest(operationId, $"  {label} ", "ecarte");
        var created = await SalesTestKit.ReadAsync<ReturnReason>(await poste.PostAsJsonAsync(ReturnRoutes.Reasons, createRequest));
        Assert.Equal((label, ReturnStockDestinations.SetAside, true), (created.Label, created.StockDestination, created.IsActive));
        Assert.Equal(created, await SalesTestKit.ReadAsync<ReturnReason>(await poste.PostAsJsonAsync(ReturnRoutes.Reasons, createRequest)));
        Assert.Equal(1, await kit.Sales.Stock.CountAuditAsync(ReturnAuditActions.ReasonCreated, created.Id.ToString()));
        await StockTestKit.AssertErrorAsync(
            await poste.PostAsJsonAsync(ReturnRoutes.Reasons, new CreateReturnReasonRequest(Guid.NewGuid(), label.ToUpperInvariant(), "AUCUNE")),
            HttpStatusCode.Conflict, ApiErrorCodes.Conflict);
        foreach (var invalid in new[] { new CreateReturnReasonRequest(Guid.NewGuid(), " ", "AUCUNE"), new CreateReturnReasonRequest(Guid.NewGuid(), "Autre", "POUBELLE") })
        {
            await StockTestKit.AssertErrorAsync(
                await poste.PostAsJsonAsync(ReturnRoutes.Reasons, invalid), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        }

        var first = await SalesTestKit.ReadAsync<ReturnDetail>(await ClosureTestKit.ReturnFirstLineAsync(poste, sale, created.Id));
        Assert.Equal(1m, await LevelAsync(productId, store.SetAsideLocationId));

        // Modification : la destination change pour les retours suivants, pas pour les anciens.
        var renamed = label + " (modifié)";
        var updated = await SalesTestKit.ReadAsync<ReturnReason>(await poste.PutAsJsonAsync(
            ReturnRoutes.Reason(created.Id), new UpdateReturnReasonRequest(Guid.NewGuid(), renamed, ReturnStockDestinations.None, true)));
        Assert.Equal((renamed, ReturnStockDestinations.None), (updated.Label, updated.StockDestination));
        Assert.Equal(1, await kit.Sales.Stock.CountAuditAsync(ReturnAuditActions.ReasonUpdated, created.Id.ToString()));
        var second = await SalesTestKit.ReadAsync<ReturnDetail>(await ClosureTestKit.ReturnFirstLineAsync(poste, sale, created.Id));
        Assert.Equal((renamed, ReturnStockDestinations.None, (Guid?)null), (second.ReasonLabel, second.StockDestination, second.DestinationLocationId));
        Assert.Equal(1m, await LevelAsync(productId, store.SetAsideLocationId));
        var firstAgain = await poste.GetFromJsonAsync<ReturnDetail>(ReturnRoutes.Return(first.Id));
        Assert.Equal((label, ReturnStockDestinations.SetAside), (firstAgain!.ReasonLabel, firstAgain.StockDestination));

        // Désactivation : le motif disparaît de la liste et n'est plus accepté.
        (await poste.PutAsJsonAsync(
            ReturnRoutes.Reason(created.Id), new UpdateReturnReasonRequest(Guid.NewGuid(), renamed, ReturnStockDestinations.None, false)))
            .EnsureSuccessStatusCode();
        Assert.DoesNotContain(await kit.ReasonsAsync(), reason => reason.Id == created.Id);
        Assert.Contains(await kit.ReasonsAsync(all: true), reason => reason.Id == created.Id && !reason.IsActive);
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.ReturnFirstLineAsync(poste, sale, created.Id), HttpStatusCode.BadRequest, ReturnErrorCodes.InvalidReason);
        await StockTestKit.AssertErrorAsync(
            await poste.PutAsJsonAsync(ReturnRoutes.Reason(Guid.NewGuid()), new UpdateReturnReasonRequest(Guid.NewGuid(), "Inconnu", "AUCUNE", true)),
            HttpStatusCode.NotFound, ApiErrorCodes.NotFound);
        Assert.Equal(2, await CountReturnsAsync(sale.Id));
    }

    // ---------- Un retour ne se modifie pas ----------

    [Fact]
    public async Task Un_retour_enregistre_ne_peut_etre_ni_modifie_ni_supprime()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store);
        var sale = await kit.SellAsync(poste, store, price: 100m, quantity: 2);
        var created = await kit.ReturnOkAsync(poste, sale, Resellable);

        foreach (var sql in new[]
                 {
                     "UPDATE returns SET refund_amount = 1 WHERE id = @Id",
                     "UPDATE returns SET reason_label = 'Autre' WHERE id = @Id",
                     "DELETE FROM returns WHERE id = @Id",
                     "UPDATE return_lines SET quantity = 2, restocked_quantity = 2 WHERE return_id = @Id",
                     "DELETE FROM return_lines WHERE return_id = @Id",
                 })
        {
            await using var connection = await server.DataSource.OpenConnectionAsync();
            var exception = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(sql, new { created.Id }));
            Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, exception.SqlState);
        }

        Assert.Equal(
            JsonSerializer.Serialize(created),
            JsonSerializer.Serialize(await poste.GetFromJsonAsync<ReturnDetail>(ReturnRoutes.Return(created.Id))));
    }
}
