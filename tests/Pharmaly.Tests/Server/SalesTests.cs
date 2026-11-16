using System.Net;
using System.Net.Http.Json;
using System.Text;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Sales;

namespace Pharmaly.Tests.Server;

[Collection(SalesTestKit.Collection)]
public sealed class SalesTests(ServerFixture server) : IClassFixture<ServerFixture>, IAsyncLifetime
{
    private readonly SalesTestKit kit = new(server);

    public async ValueTask InitializeAsync() => await kit.EnsureSessionOpenAsync();

    /// <summary>Après chaque scénario : stock et argent cohérents, sur toute la base.</summary>
    public async ValueTask DisposeAsync()
    {
        await StockInvariant.AssertHoldsAsync(server.DataSource);
        await MoneyInvariant.AssertHoldsAsync(server.DataSource);
    }

    // ---------- Concurrence ----------

    [Fact]
    public async Task Deux_caissiers_vendent_en_meme_temps_le_dernier_exemplaire_un_seul_reussit()
    {
        var (first, _) = await kit.CreateEmployeeClientAsync(Permissions.SalesCreate, Permissions.PaymentsCollect);
        var (second, _) = await kit.CreateEmployeeClientAsync(Permissions.SalesCreate, Permissions.PaymentsCollect);

        for (var round = 0; round < 10; round++)
        {
            var product = await kit.CreateProductAsync(stock: 1);

            var responses = await SalesTestKit.InParallelAsync(
                2, index => SalesTestKit.SellAsync(index == 0 ? first : second, SalesTestKit.Cash(product.Id)));

            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            var error = await StockTestKit.AssertErrorAsync(
                responses.Single(response => response.StatusCode != HttpStatusCode.OK),
                HttpStatusCode.Conflict, ApiErrorCodes.InsufficientStock);
            Assert.Contains(product.Name, error.Message);
            Assert.Equal(0m, await kit.StockAsync(product.Id));
            Assert.Equal(1, await kit.Stock.CountMovementsAsync(product.Id, StockMovementTypes.Sale));
            Assert.Equal(1, await kit.ScalarAsync<int>(
                "SELECT count(*) FROM sale_lines WHERE product_id = @Id", new { product.Id }));
        }
    }

    [Fact]
    public async Task Quarante_ventes_concurrentes_sur_un_stock_de_vingt_cinq_vingt_cinq_reussissent_exactement()
    {
        var product = await kit.CreateProductAsync(stock: 25);

        var responses = await SalesTestKit.InParallelAsync(
            40, _ => SalesTestKit.SellAsync(server.AdminClient, SalesTestKit.Cash(product.Id)));

        Assert.Equal(25, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        foreach (var refused in responses.Where(response => response.StatusCode != HttpStatusCode.OK))
        {
            await StockTestKit.AssertErrorAsync(refused, HttpStatusCode.Conflict, ApiErrorCodes.InsufficientStock);
        }

        Assert.Equal(0m, await kit.StockAsync(product.Id));
        Assert.Equal(25, await kit.Stock.CountMovementsAsync(product.Id, StockMovementTypes.Sale));
        Assert.Equal(25, await kit.ScalarAsync<int>(
            """
            SELECT count(*) FROM payments p JOIN sale_lines l ON l.sale_id = p.sale_id
            WHERE l.product_id = @Id AND p.status = 'CONFIRME'
            """,
            new { product.Id }));

        // Les tickets vendus ont tous un numéro et un code-barres différents.
        var sales = new List<SaleDetail>();
        foreach (var response in responses.Where(response => response.StatusCode == HttpStatusCode.OK))
        {
            sales.Add(await SalesTestKit.ReadAsync<SaleDetail>(response));
        }

        Assert.Equal(25, sales.Select(sale => sale.TicketNumber).Distinct().Count());
        Assert.Equal(25, sales.Select(sale => sale.TicketBarcode).Distinct().Count());
    }

    [Fact]
    public async Task Des_ventes_croisees_sur_plusieurs_produits_ne_provoquent_aucun_interblocage()
    {
        var a = await kit.CreateProductAsync(stock: 100);
        var b = await kit.CreateProductAsync(stock: 100);

        var responses = await SalesTestKit.InParallelAsync(30, index =>
        {
            var lines = index % 2 == 0
                ? new[] { new SaleLineRequest(a.Id, 1), new SaleLineRequest(b.Id, 2) }
                : [new SaleLineRequest(b.Id, 2), new SaleLineRequest(a.Id, 1)];
            return SalesTestKit.SellAsync(
                server.AdminClient, new CreateSaleRequest(Guid.NewGuid(), lines, SalePaymentModes.Cash, AmountReceived: 100_000m));
        });

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(70m, await kit.StockAsync(a.Id));
        Assert.Equal(40m, await kit.StockAsync(b.Id));
    }

    // ---------- Idempotence ----------

    [Fact]
    public async Task Un_double_clic_cree_une_seule_vente_un_seul_paiement_et_un_seul_mouvement()
    {
        var product = await kit.CreateProductAsync(stock: 10);
        var request = SalesTestKit.Cash(product.Id, quantity: 2, received: 3000m);

        var first = await kit.SellOkAsync(request);
        // Relance après un délai réseau : même demande, même identifiant d'opération.
        var replay = await kit.SellOkAsync(request);

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(first.TicketNumber, replay.TicketNumber);
        Assert.Equal(first.Payment!.Id, replay.Payment!.Id);
        Assert.Equal(first.Payment.ChangeGiven, replay.Payment.ChangeGiven);
        Assert.Equal(8m, await kit.StockAsync(product.Id));
        Assert.Equal(1, await kit.CountSaleMovementsAsync(first.Id));
        Assert.Equal(1, await kit.ScalarAsync<int>("SELECT count(*) FROM payments WHERE sale_id = @Id", new { first.Id }));
        Assert.Equal(1, await kit.ScalarAsync<int>(
            "SELECT count(*) FROM sale_lines WHERE product_id = @Id", new { product.Id }));
        Assert.Equal(1, await kit.Stock.CountAuditAsync(SaleAuditActions.SaleCreated, first.Id.ToString()));
    }

    [Fact]
    public async Task Dix_envois_simultanes_de_la_meme_vente_creent_une_seule_vente_et_donnent_la_meme_reponse()
    {
        var product = await kit.CreateProductAsync(stock: 10);
        var request = SalesTestKit.Cash(product.Id);

        var responses = await SalesTestKit.InParallelAsync(10, _ => SalesTestKit.SellAsync(server.AdminClient, request));

        var sales = new List<SaleDetail>();
        foreach (var response in responses)
        {
            sales.Add(await SalesTestKit.ReadAsync<SaleDetail>(response));
        }

        Assert.Single(sales.Select(sale => sale.Id).Distinct());
        Assert.Single(sales.Select(sale => sale.TicketNumber).Distinct());
        Assert.Equal(9m, await kit.StockAsync(product.Id));
        Assert.Equal(1, await kit.Stock.CountMovementsAsync(product.Id, StockMovementTypes.Sale));
        Assert.Equal(1, await kit.ScalarAsync<int>("SELECT count(*) FROM payments WHERE sale_id = @Id", new { sales[0].Id }));
    }

    [Fact]
    public async Task Un_identifiant_d_operation_ne_sert_pas_a_une_autre_vente()
    {
        var product = await kit.CreateProductAsync(stock: 10);
        var operationId = Guid.NewGuid();
        await kit.SellOkAsync(SalesTestKit.Cash(product.Id, 1, operationId: operationId));

        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SellAsync(server.AdminClient, SalesTestKit.Cash(product.Id, 3, operationId: operationId)),
            HttpStatusCode.Conflict, ApiErrorCodes.OperationMismatch);

        Assert.Equal(9m, await kit.StockAsync(product.Id));
    }

    [Fact]
    public async Task Une_vente_refusee_n_enregistre_rien_et_ne_consomme_aucun_numero_de_ticket()
    {
        var product = await kit.CreateProductAsync(stock: 2);
        var before = await kit.SellOkAsync(SalesTestKit.Cash(product.Id));
        var request = SalesTestKit.Cash(product.Id, quantity: 5);

        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SellAsync(server.AdminClient, request), HttpStatusCode.Conflict, ApiErrorCodes.InsufficientStock);
        Assert.Equal(1m, await kit.StockAsync(product.Id));

        // La même commande peut être retentée une fois le stock corrigé ; le numéro suit sans trou.
        await kit.Stock.SetStockAsync(product.Id, await kit.Stock.MainLocationAsync(), 5);
        var after = await kit.SellOkAsync(request);
        Assert.Equal(0m, await kit.StockAsync(product.Id));
        Assert.Equal(TicketSequence(before) + 1, TicketSequence(after));
    }

    private static long TicketSequence(SaleDetail sale) => long.Parse(sale.TicketNumber[(sale.TicketNumber.LastIndexOf('-') + 1)..]);

    // ---------- Prix, total, remises ----------

    [Fact]
    public async Task Le_prix_est_lu_sur_le_serveur_un_prix_envoye_par_le_poste_est_ignore()
    {
        var product = await kit.CreateProductAsync(stock: 5, price: 1500m);
        var json =
            $$"""
              {
                "operationId": "{{Guid.NewGuid()}}",
                "lines": [ { "productId": "{{product.Id}}", "quantity": 2, "unitPrice": 1, "lineTotal": 2 } ],
                "paymentMode": "ESPECES",
                "amountReceived": 3000,
                "total": 2,
                "subtotal": 2
              }
              """;

        var sale = await SalesTestKit.ReadAsync<SaleDetail>(await server.AdminClient.PostAsync(
            SaleRoutes.Sales, new StringContent(json, Encoding.UTF8, "application/json")));

        Assert.Equal(1500m, sale.Lines[0].UnitPrice);
        Assert.Equal(3000m, sale.Lines[0].LineTotal);
        Assert.Equal(3000m, sale.Total);
        Assert.Equal(3000m, sale.Payment!.Amount);

        // Un montant reçu calculé sur le faux prix est insuffisant : rien n'est vendu à prix cassé.
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SellAsync(server.AdminClient, SalesTestKit.Cash(product.Id, 2, received: 2m)),
            HttpStatusCode.BadRequest, SaleErrorCodes.InsufficientAmountReceived);
        Assert.Equal(3m, await kit.StockAsync(product.Id));
    }

    [Fact]
    public async Task Le_prix_applique_est_celui_du_moment_de_la_vente_et_le_ticket_ne_change_plus_ensuite()
    {
        var product = await kit.CreateProductAsync(stock: 5, price: 1000m);
        var old = await kit.SellOkAsync(SalesTestKit.Cash(product.Id));
        (await server.AdminClient.PutAsJsonAsync(
            ApiRoutes.ProductSalePrice(product.Id), new ChangeSalePriceRequest(Guid.NewGuid(), 1250m))).EnsureSuccessStatusCode();

        // Le poste affiche encore l'ancien prix : la vente est refusée plutôt qu'encaissée à un autre montant.
        var stale = SalesTestKit.Cash(product.Id) with { ExpectedTotal = 1000m };
        var error = await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SellAsync(server.AdminClient, stale), HttpStatusCode.Conflict, SaleErrorCodes.TotalMismatch);
        Assert.Contains("1 250,00 DA", error.Message);
        Assert.Equal(4m, await kit.StockAsync(product.Id));

        var recent = await kit.SellOkAsync(SalesTestKit.Cash(product.Id) with { ExpectedTotal = 1250m });
        Assert.Equal(1250m, recent.Total);
        Assert.Equal(1000m, (await kit.ReadSaleAsync(old.Id)).Total);
    }

    [Fact]
    public async Task Le_total_est_la_somme_exacte_des_lignes()
    {
        var a = await kit.CreateProductAsync(stock: 10, price: 1234.56m);
        var b = await kit.CreateProductAsync(stock: 10, price: 0.10m);
        // Produit au poids : 0,333 × 199,99 = 66,59667 → 66,60.
        var c = await kit.CreateProductAsync(stock: 10, price: 199.99m, soldByUnit: false);

        var sale = await kit.SellOkAsync(new CreateSaleRequest(
            Guid.NewGuid(),
            [new SaleLineRequest(a.Id, 3), new SaleLineRequest(b.Id, 3), new SaleLineRequest(c.Id, 0.333m)],
            SalePaymentModes.Cash,
            AmountReceived: 4000m));

        Assert.Equal([3703.68m, 0.30m, 66.60m], sale.Lines.Select(line => line.LineTotal));
        Assert.Equal([1, 2, 3], sale.Lines.Select(line => line.LineNumber));
        Assert.Equal(3770.58m, sale.Subtotal);
        Assert.Equal(3770.58m, sale.Total);
        Assert.Equal(229.42m, sale.Payment!.ChangeGiven);
        Assert.Equal(9.667m, await kit.StockAsync(c.Id));
        Assert.Equal(3, await kit.CountSaleMovementsAsync(sale.Id, StockMovementTypes.Sale));
    }

    [Fact]
    public async Task Une_remise_en_montant_est_deduite_du_total()
    {
        var product = await kit.CreateProductAsync(stock: 5, price: 1200m);

        var sale = await kit.SellOkAsync(
            SalesTestKit.Cash(product.Id, 2) with { DiscountKind = DiscountKinds.Amount, DiscountValue = 150.50m });

        Assert.Equal(2400m, sale.Subtotal);
        Assert.Equal(DiscountKinds.Amount, sale.DiscountKind);
        Assert.Equal(150.50m, sale.DiscountAmount);
        Assert.Equal(2249.50m, sale.Total);
        Assert.Equal(2249.50m, sale.Payment!.Amount);
    }

    [Theory]
    // prix, quantité, pourcentage → remise arrondie au centime (la demie s'éloigne de zéro), total
    [InlineData("33.33", 1, "10", "3.33", "30.00")]
    [InlineData("33.35", 1, "10", "3.34", "30.01")]
    [InlineData("0.05", 1, "50", "0.03", "0.02")]
    [InlineData("0.01", 1, "50", "0.01", "0.00")]
    [InlineData("1234.56", 3, "12.5", "462.96", "3240.72")]
    [InlineData("999.99", 7, "33.33", "2333.08", "4666.85")]
    [InlineData("1200", 1, "100", "1200.00", "0.00")]
    [InlineData("1200", 2, "0.01", "0.24", "2399.76")]
    public async Task Une_remise_en_pourcentage_est_arrondie_au_centime_exactement(
        string price, int quantity, string percent, string expectedDiscount, string expectedTotal)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var product = await kit.CreateProductAsync(stock: 10, price: decimal.Parse(price, culture));

        var sale = await kit.SellOkAsync(SalesTestKit.Cash(product.Id, quantity) with
        {
            DiscountKind = DiscountKinds.Percent,
            DiscountValue = decimal.Parse(percent, culture),
        });

        Assert.Equal(decimal.Parse(expectedDiscount, culture), sale.DiscountAmount);
        Assert.Equal(decimal.Parse(expectedTotal, culture), sale.Total);
        Assert.Equal(sale.Subtotal - sale.DiscountAmount, sale.Total);
        Assert.Equal(sale.Total, sale.Payment!.Amount);
        Assert.Equal(DiscountKinds.Percent, sale.DiscountKind);
    }

    [Fact]
    public async Task Une_remise_superieure_au_total_est_refusee()
    {
        var product = await kit.CreateProductAsync(stock: 5, price: 1200m);
        var tooMuch = SalesTestKit.Cash(product.Id) with { DiscountKind = DiscountKinds.Amount, DiscountValue = 1200.01m };
        var overHundred = SalesTestKit.Cash(product.Id) with { DiscountKind = DiscountKinds.Percent, DiscountValue = 100.01m };
        var negative = SalesTestKit.Cash(product.Id) with { DiscountKind = DiscountKinds.Amount, DiscountValue = -5m };
        var unknown = SalesTestKit.Cash(product.Id) with { DiscountKind = "CADEAU", DiscountValue = 5m };
        var untyped = SalesTestKit.Cash(product.Id) with { DiscountValue = 5m };

        foreach (var request in new[] { tooMuch, overHundred, negative, unknown, untyped })
        {
            await StockTestKit.AssertErrorAsync(
                await SalesTestKit.SellAsync(server.AdminClient, request), HttpStatusCode.BadRequest, SaleErrorCodes.InvalidDiscount);
        }

        Assert.Equal(5m, await kit.StockAsync(product.Id));

        // Une remise égale au total reste permise : la vente vaut alors zéro.
        var free = await kit.SellOkAsync(
            SalesTestKit.Cash(product.Id, received: 0m) with { DiscountKind = DiscountKinds.Amount, DiscountValue = 1200m });
        Assert.Equal(0m, free.Total);
        Assert.Equal(0m, free.Payment!.ChangeGiven);
    }

    [Fact]
    public async Task Une_remise_sans_le_droit_est_refusee()
    {
        var product = await kit.CreateProductAsync(stock: 5);
        var (cashier, cashierId) = await kit.CreateEmployeeClientAsync(Permissions.SalesCreate, Permissions.PaymentsCollect);
        var byAmount = SalesTestKit.Cash(product.Id) with { DiscountKind = DiscountKinds.Amount, DiscountValue = 1m };
        var byPercent = SalesTestKit.Cash(product.Id) with { DiscountKind = DiscountKinds.Percent, DiscountValue = 1m };

        foreach (var request in new[] { byAmount, byPercent })
        {
            await StockTestKit.AssertErrorAsync(
                await SalesTestKit.SellAsync(cashier, request), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        }

        Assert.Equal(5m, await kit.StockAsync(product.Id));
        Assert.Equal(2, await kit.ScalarAsync<int>(
            "SELECT count(*) FROM audit_logs WHERE action = 'PERMISSION_REFUSEE' AND employee_id = @cashierId", new { cashierId }));

        // Sans remise, ou avec une remise nulle, la vente passe.
        await kit.SellOkAsync(SalesTestKit.Cash(product.Id), cashier);
        var zero = await kit.SellOkAsync(
            SalesTestKit.Cash(product.Id) with { DiscountKind = DiscountKinds.Percent, DiscountValue = 0m }, cashier);
        Assert.Null(zero.DiscountKind);
        Assert.Equal(0m, zero.DiscountAmount);

        var (discounter, _) = await kit.CreateEmployeeClientAsync(
            Permissions.SalesCreate, Permissions.PaymentsCollect, Permissions.DiscountsApply);
        Assert.Equal(1199m, (await kit.SellOkAsync(byAmount, discounter)).Total);
    }

    // ---------- Règles de la vente ----------

    [Fact]
    public async Task Une_quantite_fractionnaire_est_refusee_pour_un_produit_vendu_a_l_unite()
    {
        var unit = await kit.CreateProductAsync(stock: 5);
        var bulk = await kit.CreateProductAsync(stock: 5, soldByUnit: false);

        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SellAsync(server.AdminClient, SalesTestKit.Cash(unit.Id, 0.5m)),
            HttpStatusCode.BadRequest, ApiErrorCodes.FractionalQuantity);
        Assert.Equal(5m, await kit.StockAsync(unit.Id));

        await kit.SellOkAsync(SalesTestKit.Cash(bulk.Id, 0.5m));
        Assert.Equal(4.5m, await kit.StockAsync(bulk.Id));
    }

    [Fact]
    public async Task Un_produit_inactif_est_invendable()
    {
        var product = await kit.CreateProductAsync(stock: 5);
        (await server.AdminClient.PutAsJsonAsync(
            ApiRoutes.Product(product.Id),
            new UpdateProductRequest(Guid.NewGuid(), product.Name, null, null, IsActive: false, null, null))).EnsureSuccessStatusCode();

        var error = await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SellAsync(server.AdminClient, SalesTestKit.Cash(product.Id)),
            HttpStatusCode.Conflict, SaleErrorCodes.InactiveProduct);

        Assert.Contains(product.Name, error.Message);
        Assert.Equal(5m, await kit.StockAsync(product.Id));
    }

    [Fact]
    public async Task Une_vente_mal_formee_est_refusee_sans_rien_enregistrer()
    {
        var product = await kit.CreateProductAsync(stock: 5);
        var line = new SaleLineRequest(product.Id, 1);
        CreateSaleRequest Request(IReadOnlyList<SaleLineRequest> lines, string mode = SalePaymentModes.Cash) =>
            new(Guid.NewGuid(), lines, mode, AmountReceived: 10_000m);

        foreach (var request in new[]
                 {
                     Request([]),
                     Request([new SaleLineRequest(product.Id, 0)]),
                     Request([new SaleLineRequest(product.Id, -1)]),
                     Request([line, line]),
                     Request([line], "CHEQUE"),
                     Request([line]) with { AmountReceived = null },
                     Request([line]) with { OperationId = Guid.Empty },
                 })
        {
            await StockTestKit.AssertErrorAsync(
                await SalesTestKit.SellAsync(server.AdminClient, request), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        }

        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SellAsync(server.AdminClient, Request([new SaleLineRequest(Guid.NewGuid(), 1)])),
            HttpStatusCode.NotFound, ApiErrorCodes.NotFound);
        Assert.Equal(5m, await kit.StockAsync(product.Id));
        Assert.Equal(0, await kit.Stock.CountMovementsAsync(product.Id, StockMovementTypes.Sale));
    }

    [Fact]
    public async Task Une_vente_de_plusieurs_lignes_est_refusee_en_entier_si_un_seul_produit_manque()
    {
        var plenty = await kit.CreateProductAsync(stock: 10);
        var scarce = await kit.CreateProductAsync(stock: 1);

        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SellAsync(server.AdminClient, new CreateSaleRequest(
                Guid.NewGuid(), [new SaleLineRequest(plenty.Id, 2), new SaleLineRequest(scarce.Id, 2)],
                SalePaymentModes.Cash, AmountReceived: 100_000m)),
            HttpStatusCode.Conflict, ApiErrorCodes.InsufficientStock);

        Assert.Equal(10m, await kit.StockAsync(plenty.Id));
        Assert.Equal(1m, await kit.StockAsync(scarce.Id));
    }

    [Fact]
    public async Task Le_stock_mis_a_l_ecart_ou_d_un_autre_site_n_est_pas_vendable()
    {
        var product = await kit.Stock.CreateProductAsync();
        await kit.Stock.SetStockAsync(product.Id, await kit.Stock.SetAsideLocationAsync(), 5);
        await kit.Stock.SetStockAsync(product.Id, await kit.Stock.OtherStoreLocationAsync(), 5);

        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SellAsync(server.AdminClient, SalesTestKit.Cash(product.Id)),
            HttpStatusCode.Conflict, ApiErrorCodes.InsufficientStock);
    }

    // ---------- Espèces ----------

    [Fact]
    public async Task Vente_en_especes_la_monnaie_a_rendre_est_exacte_et_conservee()
    {
        var product = await kit.CreateProductAsync(stock: 5, price: 1234.56m);

        var sale = await kit.SellOkAsync(SalesTestKit.Cash(product.Id, received: 2000m));

        Assert.Equal(SaleStatuses.Completed, sale.Status);
        Assert.False(sale.IsUnpaid);
        Assert.Equal(PaymentMethods.Cash, sale.Payment!.Method);
        Assert.Equal(PaymentStatuses.Confirmed, sale.Payment.Status);
        Assert.Equal(1234.56m, sale.Payment.Amount);
        Assert.Equal(2000m, sale.Payment.AmountReceived);
        Assert.Equal(765.44m, sale.Payment.ChangeGiven);
        Assert.NotNull(sale.Payment.ConfirmedAt);
        Assert.Equal(765.44m, await kit.ScalarAsync<decimal>(
            "SELECT change_given FROM payments WHERE sale_id = @Id", new { sale.Id }));

        // Montant exact : aucune monnaie.
        var exact = await kit.SellOkAsync(SalesTestKit.Cash(product.Id, received: 1234.56m));
        Assert.Equal(0m, exact.Payment!.ChangeGiven);
    }

    [Fact]
    public async Task Vente_en_especes_un_montant_recu_insuffisant_est_refuse()
    {
        var product = await kit.CreateProductAsync(stock: 5, price: 1200m);

        var error = await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SellAsync(server.AdminClient, SalesTestKit.Cash(product.Id, received: 1199.99m)),
            HttpStatusCode.BadRequest, SaleErrorCodes.InsufficientAmountReceived);

        Assert.Contains("1 200,00 DA", error.Message);
        Assert.Equal(5m, await kit.StockAsync(product.Id));
        Assert.Equal(0, await kit.ScalarAsync<int>(
            "SELECT count(*) FROM sale_lines WHERE product_id = @Id", new { product.Id }));
    }

    // ---------- TPE ----------

    [Fact]
    public async Task Vente_par_TPE_en_attente_puis_acceptee()
    {
        var product = await kit.CreateProductAsync(stock: 5, price: 2500m);
        var before = await kit.SummaryAsync();

        var pending = await kit.SellOkAsync(SalesTestKit.Card(product.Id));

        Assert.Equal(SaleStatuses.CardPending, pending.Status);
        Assert.Equal(PaymentStatuses.Pending, pending.Payment!.Status);
        Assert.Null(pending.Payment.ConfirmedAt);
        Assert.Equal(4m, await kit.StockAsync(product.Id));
        // Rien n'est compté comme encaissé avant la confirmation.
        var during = await kit.SummaryAsync();
        Assert.Equal(before.SaleCardCollected, during.SaleCardCollected);
        Assert.Equal(before.SaleCashCollected, during.SaleCashCollected);
        Assert.Equal(before.ExpectedCash, during.ExpectedCash);
        Assert.Equal(before.PendingCardCount + 1, during.PendingCardCount);
        Assert.Equal(before.PendingCardAmount + 2500m, during.PendingCardAmount);
        Assert.Equal(0, await kit.ScalarAsync<int>(
            "SELECT count(*) FROM payments WHERE sale_id = @Id AND (status = 'CONFIRME' OR confirmed_at IS NOT NULL)", new { pending.Id }));

        var accepted = await SalesTestKit.ReadAsync<SaleDetail>(
            await SalesTestKit.CardResultAsync(server.AdminClient, pending.Id, accepted: true));

        Assert.Equal(SaleStatuses.Completed, accepted.Status);
        Assert.Equal(PaymentStatuses.Confirmed, accepted.Payment!.Status);
        Assert.NotNull(accepted.Payment.ConfirmedAt);
        Assert.Null(accepted.Cancellation);
        Assert.Equal(4m, await kit.StockAsync(product.Id));
        Assert.Equal(1, await kit.CountSaleMovementsAsync(pending.Id));
        var after = await kit.SummaryAsync();
        Assert.Equal(before.SaleCardCollected + 2500m, after.SaleCardCollected);
        Assert.Equal(before.PendingCardCount, after.PendingCardCount);
        Assert.Equal(before.ExpectedCash, after.ExpectedCash);

        // Une seconde réponse est refusée : le paiement n'attend plus rien.
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.CardResultAsync(server.AdminClient, pending.Id, accepted: false),
            HttpStatusCode.Conflict, SaleErrorCodes.CardNotPending);
        Assert.Equal(SaleStatuses.Completed, (await kit.ReadSaleAsync(pending.Id)).Status);
    }

    [Fact]
    public async Task Vente_par_TPE_refusee_la_vente_est_annulee_et_le_stock_restitue()
    {
        var product = await kit.CreateProductAsync(stock: 5, price: 2500m);
        var before = await kit.SummaryAsync();
        var pending = await kit.SellOkAsync(SalesTestKit.Card(product.Id, 2));
        Assert.Equal(3m, await kit.StockAsync(product.Id));

        var refused = await SalesTestKit.ReadAsync<SaleDetail>(
            await SalesTestKit.CardResultAsync(server.AdminClient, pending.Id, accepted: false));

        Assert.Equal(SaleStatuses.Cancelled, refused.Status);
        Assert.Equal(PaymentStatuses.Failed, refused.Payment!.Status);
        Assert.Null(refused.Payment.ConfirmedAt);
        Assert.Equal(SaleCancellationKinds.CardRefused, refused.Cancellation!.Kind);
        Assert.Equal(0m, refused.Cancellation.RefundAmount);
        Assert.Null(refused.Cancellation.RefundMethod);
        // La marchandise n'est pas partie : le stock revient, par un mouvement compensatoire.
        Assert.Equal(5m, await kit.StockAsync(product.Id));
        Assert.Equal(1, await kit.CountSaleMovementsAsync(pending.Id, StockMovementTypes.Sale));
        Assert.Equal(1, await kit.CountSaleMovementsAsync(pending.Id, StockMovementTypes.SaleCancellation));
        // Rien n'a été encaissé, rien n'est remboursé.
        var after = await kit.SummaryAsync();
        Assert.Equal(before.SaleCardCollected, after.SaleCardCollected);
        Assert.Equal(before.ExpectedCash, after.ExpectedCash);
        Assert.Equal(before.CashRefunds, after.CashRefunds);
        Assert.Equal(before.CardRefunds, after.CardRefunds);
        Assert.Equal(before.PendingCardCount, after.PendingCardCount);
        Assert.Equal(0, await kit.ScalarAsync<int>("SELECT count(*) FROM cash_movements WHERE sale_id = @Id", new { pending.Id }));

        // Ni seconde réponse, ni annulation d'une vente déjà annulée.
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.CardResultAsync(server.AdminClient, pending.Id, accepted: true),
            HttpStatusCode.Conflict, SaleErrorCodes.CardNotPending);
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.CancelAsync(server.AdminClient, pending.Id), HttpStatusCode.Conflict, SaleErrorCodes.AlreadyCancelled);
        Assert.Equal(5m, await kit.StockAsync(product.Id));

        // Le client paie autrement : c'est une nouvelle vente, avec un nouveau ticket.
        var paid = await kit.SellOkAsync(SalesTestKit.Cash(product.Id, 2));
        Assert.NotEqual(pending.TicketNumber, paid.TicketNumber);
        Assert.Equal(3m, await kit.StockAsync(product.Id));
    }

    [Fact]
    public async Task Reponses_simultanees_du_TPE_accepte_et_refuse_une_seule_est_retenue()
    {
        for (var round = 0; round < 5; round++)
        {
            var product = await kit.CreateProductAsync(stock: 1);
            var pending = await kit.SellOkAsync(SalesTestKit.Card(product.Id));

            var responses = await SalesTestKit.InParallelAsync(
                2, index => SalesTestKit.CardResultAsync(server.AdminClient, pending.Id, accepted: index == 0));

            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            await StockTestKit.AssertErrorAsync(
                responses.Single(response => response.StatusCode != HttpStatusCode.OK),
                HttpStatusCode.Conflict, SaleErrorCodes.CardNotPending);
            var sale = await kit.ReadSaleAsync(pending.Id);
            Assert.Equal(sale.Status == SaleStatuses.Completed ? 0m : 1m, await kit.StockAsync(product.Id));
        }
    }

    [Fact]
    public async Task Double_clic_sur_la_reponse_du_TPE_une_seule_reponse_enregistree()
    {
        var product = await kit.CreateProductAsync(stock: 3);
        var pending = await kit.SellOkAsync(SalesTestKit.Card(product.Id));
        var operationId = Guid.NewGuid();

        var responses = await SalesTestKit.InParallelAsync(
            5, _ => SalesTestKit.CardResultAsync(server.AdminClient, pending.Id, accepted: false, operationId));

        foreach (var response in responses)
        {
            Assert.Equal(SaleStatuses.Cancelled, (await SalesTestKit.ReadAsync<SaleDetail>(response)).Status);
        }

        Assert.Equal(3m, await kit.StockAsync(product.Id));
        Assert.Equal(1, await kit.CountSaleMovementsAsync(pending.Id, StockMovementTypes.SaleCancellation));
    }

    [Fact]
    public async Task Une_vente_en_attente_de_TPE_ne_s_annule_pas_avant_la_reponse_du_terminal()
    {
        var product = await kit.CreateProductAsync(stock: 3);
        var pending = await kit.SellOkAsync(SalesTestKit.Card(product.Id));

        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.CancelAsync(server.AdminClient, pending.Id), HttpStatusCode.Conflict, SaleErrorCodes.CardPending);

        Assert.Equal(2m, await kit.StockAsync(product.Id));
        (await SalesTestKit.CardResultAsync(server.AdminClient, pending.Id, accepted: true)).EnsureSuccessStatusCode();
    }

    // ---------- Vente impayée ----------

    [Fact]
    public async Task Vente_impayee_le_stock_est_deduit_une_creance_est_creee_et_rien_n_est_encaisse()
    {
        var product = await kit.CreateProductAsync(stock: 5, price: 3500m);
        var customer = await kit.CreateCustomerAsync("Mme Benali");
        var before = await kit.SummaryAsync();

        var sale = await kit.SellOkAsync(SalesTestKit.Unpaid(product.Id, customer.Id, 2));

        Assert.Equal(SaleStatuses.Completed, sale.Status);
        Assert.Equal(SalePaymentModes.Unpaid, sale.PaymentMode);
        Assert.True(sale.IsUnpaid);
        Assert.Null(sale.Payment);
        Assert.Equal(customer.Id, sale.CustomerId);
        Assert.Equal("Mme Benali", sale.CustomerName);
        Assert.Equal(ReceivableStatuses.Open, sale.Receivable!.Status);
        Assert.Equal(7000m, sale.Receivable.Amount);
        Assert.Equal(3m, await kit.StockAsync(product.Id));
        Assert.Equal(1, await kit.CountSaleMovementsAsync(sale.Id, StockMovementTypes.Sale));
        Assert.Equal(0, await kit.ScalarAsync<int>("SELECT count(*) FROM payments WHERE sale_id = @Id", new { sale.Id }));
        Assert.Equal(1, await kit.ScalarAsync<int>(
            "SELECT count(*) FROM receivables WHERE sale_id = @Id AND status = 'OUVERTE' AND amount = 7000", new { sale.Id }));
        // Aucun encaissement fictif dans la caisse.
        var after = await kit.SummaryAsync();
        Assert.Equal(before with { }, after);
    }

    [Fact]
    public async Task Vente_impayee_sans_client_refusee()
    {
        var product = await kit.CreateProductAsync(stock: 5);

        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SellAsync(server.AdminClient, SalesTestKit.Unpaid(product.Id, null)),
            HttpStatusCode.BadRequest, SaleErrorCodes.CustomerRequired);
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SellAsync(server.AdminClient, SalesTestKit.Unpaid(product.Id, Guid.NewGuid())),
            HttpStatusCode.NotFound, ApiErrorCodes.NotFound);

        Assert.Equal(5m, await kit.StockAsync(product.Id));
        Assert.Equal(0, await kit.ScalarAsync<int>(
            "SELECT count(*) FROM sale_lines WHERE product_id = @Id", new { product.Id }));
    }

    // ---------- Ticket ----------

    [Fact]
    public async Task Le_ticket_a_un_numero_stable_prefixe_du_site_et_un_code_barres_qui_le_retrouvent()
    {
        var product = await kit.CreateProductAsync(stock: 5);

        var first = await kit.SellOkAsync(SalesTestKit.Cash(product.Id));
        var second = await kit.SellOkAsync(SalesTestKit.Cash(product.Id));

        Assert.Matches("^D1-[0-9]{6,}$", first.TicketNumber);
        Assert.Matches("^TK[0-9]{10}$", first.TicketBarcode);
        Assert.Equal(TicketSequence(first) + 1, TicketSequence(second));
        Assert.NotEqual(first.TicketBarcode, second.TicketBarcode);

        var byNumber = await SalesTestKit.ReadAsync<SaleDetail>(
            await server.AdminClient.GetAsync(SaleRoutes.ByTicket(first.TicketNumber.ToLowerInvariant() + " ")));
        var byBarcode = await SalesTestKit.ReadAsync<SaleDetail>(
            await server.AdminClient.GetAsync(SaleRoutes.ByTicket(first.TicketBarcode)));
        Assert.Equal(first.Id, byNumber.Id);
        Assert.Equal(first.Id, byBarcode.Id);
        Assert.Equal(first.Lines, byBarcode.Lines);

        await StockTestKit.AssertErrorAsync(
            await server.AdminClient.GetAsync(SaleRoutes.ByTicket("D1-999999")), HttpStatusCode.NotFound, ApiErrorCodes.NotFound);
        await StockTestKit.AssertErrorAsync(
            await server.AdminClient.GetAsync(SaleRoutes.Sale(Guid.NewGuid())), HttpStatusCode.NotFound, ApiErrorCodes.NotFound);
    }

    [Fact]
    public async Task Un_ticket_enregistre_ne_peut_etre_ni_modifie_ni_supprime_dans_la_base()
    {
        var product = await kit.CreateProductAsync(stock: 5);
        var sale = await kit.SellOkAsync(SalesTestKit.Cash(product.Id));

        foreach (var sql in new[]
                 {
                     "UPDATE sales SET total = 1, subtotal = 1 WHERE id = @Id",
                     "UPDATE sales SET ticket_number = 'D1-000000' WHERE id = @Id",
                     "UPDATE sales SET employee_id = employee_id, created_at = now() WHERE id = @Id",
                     "DELETE FROM sales WHERE id = @Id",
                     "UPDATE sale_lines SET quantity = 2 WHERE sale_id = @Id",
                     "DELETE FROM sale_lines WHERE sale_id = @Id",
                 })
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => kit.ScalarAsync<object>(sql, new { sale.Id }));
        }

        Assert.Equal(sale, await kit.ReadSaleAsync(sale.Id) with { Lines = sale.Lines });
    }

    // ---------- Annulation ----------

    [Fact]
    public async Task Annulation_d_une_vente_en_especes_le_stock_est_restitue_et_le_paiement_rembourse()
    {
        var product = await kit.CreateProductAsync(stock: 5, price: 1200m);
        var sale = await kit.SellOkAsync(SalesTestKit.Cash(product.Id, 2, received: 3000m));
        var before = await kit.SummaryAsync();

        var cancelled = await SalesTestKit.ReadAsync<SaleDetail>(
            await SalesTestKit.CancelAsync(server.AdminClient, sale.Id, "Client revenu sur sa décision"));

        Assert.Equal(SaleStatuses.Cancelled, cancelled.Status);
        Assert.Equal(PaymentStatuses.Refunded, cancelled.Payment!.Status);
        Assert.Equal(SaleCancellationKinds.Cancelled, cancelled.Cancellation!.Kind);
        Assert.Equal("Client revenu sur sa décision", cancelled.Cancellation.Reason);
        Assert.Equal(server.AdminId, cancelled.Cancellation.EmployeeId);
        Assert.Equal(PaymentMethods.Cash, cancelled.Cancellation.RefundMethod);
        Assert.Equal(2400m, cancelled.Cancellation.RefundAmount);
        // La vente d'origine garde son numéro, ses lignes et ses montants.
        Assert.Equal(sale.TicketNumber, cancelled.TicketNumber);
        Assert.Equal(sale.Lines, cancelled.Lines);
        Assert.Equal(2400m, cancelled.Total);
        // Stock restitué par un mouvement compensatoire, l'ancien mouvement reste.
        Assert.Equal(5m, await kit.StockAsync(product.Id));
        Assert.Equal(1, await kit.CountSaleMovementsAsync(sale.Id, StockMovementTypes.Sale));
        Assert.Equal(1, await kit.CountSaleMovementsAsync(sale.Id, StockMovementTypes.SaleCancellation));
        // Remboursement tracé comme sortie de caisse.
        Assert.Equal(2400m, await kit.ScalarAsync<decimal>(
            "SELECT amount FROM cash_movements WHERE sale_id = @Id AND kind = 'REMBOURSEMENT_VENTE'", new { sale.Id }));
        var after = await kit.SummaryAsync();
        Assert.Equal(before.CashRefunds + 2400m, after.CashRefunds);
        Assert.Equal(before.ExpectedCash - 2400m, after.ExpectedCash);
        Assert.Equal(1, await kit.Stock.CountAuditAsync(SaleAuditActions.SaleCancelled, sale.Id.ToString()));
    }

    [Fact]
    public async Task Annulation_d_une_vente_payee_par_carte_le_remboursement_est_trace_sans_sortie_d_especes()
    {
        var product = await kit.CreateProductAsync(stock: 5, price: 1800m);
        var sale = await kit.SellOkAsync(SalesTestKit.Card(product.Id));
        (await SalesTestKit.CardResultAsync(server.AdminClient, sale.Id, accepted: true)).EnsureSuccessStatusCode();
        var before = await kit.SummaryAsync();

        var cancelled = await SalesTestKit.ReadAsync<SaleDetail>(await SalesTestKit.CancelAsync(server.AdminClient, sale.Id));

        Assert.Equal(PaymentStatuses.Refunded, cancelled.Payment!.Status);
        Assert.Equal(PaymentMethods.Card, cancelled.Cancellation!.RefundMethod);
        Assert.Equal(1800m, cancelled.Cancellation.RefundAmount);
        Assert.Equal(5m, await kit.StockAsync(product.Id));
        var after = await kit.SummaryAsync();
        Assert.Equal(before.CardRefunds + 1800m, after.CardRefunds);
        Assert.Equal(before.ExpectedCash, after.ExpectedCash);
        Assert.Equal(0, await kit.ScalarAsync<int>("SELECT count(*) FROM cash_movements WHERE sale_id = @Id", new { sale.Id }));
    }

    [Fact]
    public async Task Une_double_annulation_est_refusee()
    {
        var product = await kit.CreateProductAsync(stock: 5);
        var sale = await kit.SellOkAsync(SalesTestKit.Cash(product.Id));
        (await SalesTestKit.CancelAsync(server.AdminClient, sale.Id)).EnsureSuccessStatusCode();

        var error = await StockTestKit.AssertErrorAsync(
            await SalesTestKit.CancelAsync(server.AdminClient, sale.Id), HttpStatusCode.Conflict, SaleErrorCodes.AlreadyCancelled);

        Assert.Contains(sale.TicketNumber, error.Message);
        Assert.Equal(5m, await kit.StockAsync(product.Id));
        Assert.Equal(1, await kit.CountSaleMovementsAsync(sale.Id, StockMovementTypes.SaleCancellation));
        Assert.Equal(1, await kit.ScalarAsync<int>("SELECT count(*) FROM cash_movements WHERE sale_id = @Id", new { sale.Id }));
    }

    [Fact]
    public async Task Dix_annulations_simultanees_de_la_meme_vente_une_seule_reussit()
    {
        var product = await kit.CreateProductAsync(stock: 5);
        var sale = await kit.SellOkAsync(SalesTestKit.Cash(product.Id, 3));

        var responses = await SalesTestKit.InParallelAsync(10, _ => SalesTestKit.CancelAsync(server.AdminClient, sale.Id));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        foreach (var refused in responses.Where(response => response.StatusCode != HttpStatusCode.OK))
        {
            await StockTestKit.AssertErrorAsync(refused, HttpStatusCode.Conflict, SaleErrorCodes.AlreadyCancelled);
        }

        Assert.Equal(5m, await kit.StockAsync(product.Id));
        Assert.Equal(1, await kit.CountSaleMovementsAsync(sale.Id, StockMovementTypes.SaleCancellation));
        Assert.Equal(1, await kit.ScalarAsync<int>("SELECT count(*) FROM cash_movements WHERE sale_id = @Id", new { sale.Id }));
    }

    [Fact]
    public async Task Double_clic_sur_l_annulation_meme_reponse_et_un_seul_remboursement()
    {
        var product = await kit.CreateProductAsync(stock: 5);
        var sale = await kit.SellOkAsync(SalesTestKit.Cash(product.Id));
        var operationId = Guid.NewGuid();

        var first = await SalesTestKit.ReadAsync<SaleDetail>(
            await SalesTestKit.CancelAsync(server.AdminClient, sale.Id, operationId: operationId));
        var replay = await SalesTestKit.ReadAsync<SaleDetail>(
            await SalesTestKit.CancelAsync(server.AdminClient, sale.Id, operationId: operationId));

        Assert.Equal(first.Cancellation, replay.Cancellation);
        Assert.Equal(5m, await kit.StockAsync(product.Id));
        Assert.Equal(1, await kit.ScalarAsync<int>("SELECT count(*) FROM cash_movements WHERE sale_id = @Id", new { sale.Id }));
    }

    [Fact]
    public async Task L_annulation_exige_un_motif()
    {
        var product = await kit.CreateProductAsync(stock: 5);
        var sale = await kit.SellOkAsync(SalesTestKit.Cash(product.Id));

        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.CancelAsync(server.AdminClient, sale.Id, "   "), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.CancelAsync(server.AdminClient, Guid.NewGuid()), HttpStatusCode.NotFound, ApiErrorCodes.NotFound);

        Assert.Equal(SaleStatuses.Completed, (await kit.ReadSaleAsync(sale.Id)).Status);
        Assert.Equal(4m, await kit.StockAsync(product.Id));
    }

    [Fact]
    public async Task Une_vente_impayee_annulee_annule_sa_creance()
    {
        var product = await kit.CreateProductAsync(stock: 5, price: 900m);
        var customer = await kit.CreateCustomerAsync();
        var sale = await kit.SellOkAsync(SalesTestKit.Unpaid(product.Id, customer.Id));
        var before = await kit.SummaryAsync();

        var cancelled = await SalesTestKit.ReadAsync<SaleDetail>(await SalesTestKit.CancelAsync(server.AdminClient, sale.Id));

        Assert.Equal(SaleStatuses.Cancelled, cancelled.Status);
        Assert.False(cancelled.IsUnpaid);
        Assert.Equal(ReceivableStatuses.Cancelled, cancelled.Receivable!.Status);
        Assert.Null(cancelled.Cancellation!.RefundMethod);
        Assert.Equal(0m, cancelled.Cancellation.RefundAmount);
        Assert.Equal(5m, await kit.StockAsync(product.Id));
        // Aucun argent n'avait été encaissé : aucun remboursement.
        Assert.Equal(before, await kit.SummaryAsync());

        // Une créance annulée ne se règle plus.
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SettleAsync(server.AdminClient, PaymentMethods.Cash, 900m, null, sale.Receivable!.Id),
            HttpStatusCode.Conflict, ReceivableErrorCodes.NotOpen);
    }

    [Fact]
    public async Task Une_vente_dont_la_creance_est_reglee_ne_s_annule_pas()
    {
        var product = await kit.CreateProductAsync(stock: 5, price: 900m);
        var customer = await kit.CreateCustomerAsync();
        var sale = await kit.SellOkAsync(SalesTestKit.Unpaid(product.Id, customer.Id));
        (await SalesTestKit.SettleAsync(server.AdminClient, PaymentMethods.Cash, 900m, null, sale.Receivable!.Id)).EnsureSuccessStatusCode();

        var error = await StockTestKit.AssertErrorAsync(
            await SalesTestKit.CancelAsync(server.AdminClient, sale.Id), HttpStatusCode.Conflict, SaleErrorCodes.ReceivableAlreadySettled);

        Assert.Contains("retour", error.Message);
        Assert.Contains(sale.TicketNumber, error.Message);
        var after = await kit.ReadSaleAsync(sale.Id);
        Assert.Equal(SaleStatuses.Completed, after.Status);
        Assert.Equal(ReceivableStatuses.Settled, after.Receivable!.Status);
        Assert.Equal(4m, await kit.StockAsync(product.Id));
    }

    [Fact]
    public async Task Une_vente_dont_la_journee_est_cloturee_ne_s_annule_plus()
    {
        // La journée comptable est fixée par une vraie clôture, sur un magasin réservé à ce test
        // (la base interdit désormais de la poser à la main sans clôture correspondante).
        var closures = new ClosureTestKit(server);
        var store = await closures.CreateStoreAsync();
        var poste = await closures.CreateRegisterClientAsync(store, 1000m);
        var product = await closures.ProductAsync(store, price: 1200m, stock: 5);
        var sale = await kit.SellOkAsync(SalesTestKit.Cash(product.Id), poste);
        await ClosureTestKit.CloseSessionOkAsync(poste);
        await ClosureTestKit.CloseDayOkAsync(poste);
        Assert.NotNull(await kit.ScalarAsync<DateTime?>("SELECT business_date FROM sales WHERE id = @Id", new { sale.Id }));
        await ClosureTestKit.OpenAsync(poste, 1000m);

        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.CancelAsync(poste, sale.Id), HttpStatusCode.Conflict, SaleErrorCodes.DayClosed);

        Assert.Equal(4m, await kit.Stock.ReadLevelAsync(product.Id, store.MainLocationId));
        Assert.Equal(
            SaleStatuses.Completed,
            (await SalesTestKit.ReadAsync<SaleDetail>(await poste.GetAsync(SaleRoutes.Sale(sale.Id)))).Status);
        Assert.Equal(1000m, (await kit.SummaryAsync(poste)).ExpectedCash);
    }

    // ---------- Historique ----------

    [Fact]
    public async Task L_historique_se_filtre_par_date_employe_statut_mode_et_numero()
    {
        var product = await kit.CreateProductAsync(stock: 10);
        var customer = await kit.CreateCustomerAsync();
        var (seller, sellerId) = await kit.CreateEmployeeClientAsync(Permissions.SalesCreate, Permissions.PaymentsCollect);
        var cash = await kit.SellOkAsync(SalesTestKit.Cash(product.Id), seller);
        var unpaid = await kit.SellOkAsync(SalesTestKit.Unpaid(product.Id, customer.Id), seller);
        var cancelled = await kit.SellOkAsync(SalesTestKit.Cash(product.Id));
        (await SalesTestKit.CancelAsync(server.AdminClient, cancelled.Id)).EnsureSuccessStatusCode();

        async Task<List<SaleSummary>> ListAsync(string query) =>
            await SalesTestKit.ReadAsync<List<SaleSummary>>(await server.AdminClient.GetAsync(SaleRoutes.Sales + query));

        var bySeller = await ListAsync($"?employe={sellerId}");
        Assert.Equal([unpaid.Id, cash.Id], bySeller.Select(sale => sale.Id));
        Assert.True(bySeller[0].IsUnpaid);
        Assert.Equal(customer.Name, bySeller[0].CustomerName);

        Assert.Contains(cancelled.Id, (await ListAsync("?statut=ANNULEE")).Select(sale => sale.Id));
        Assert.DoesNotContain(cash.Id, (await ListAsync("?statut=ANNULEE")).Select(sale => sale.Id));
        Assert.All(await ListAsync("?mode=IMPAYE"), sale => Assert.Equal(SalePaymentModes.Unpaid, sale.PaymentMode));
        Assert.Equal([cash.Id], (await ListAsync($"?numero={cash.TicketNumber}")).Select(sale => sale.Id));
        Assert.Equal([cash.Id], (await ListAsync($"?numero={cash.TicketBarcode}")).Select(sale => sale.Id));

        var today = DateOnly.FromDateTime(await kit.ScalarAsync<DateTime>("SELECT current_date"));
        Assert.Contains(cash.Id, (await ListAsync($"?du={today:yyyy-MM-dd}&au={today:yyyy-MM-dd}")).Select(sale => sale.Id));
        Assert.Empty(await ListAsync($"?du={today.AddDays(1):yyyy-MM-dd}"));
        Assert.Empty(await ListAsync($"?au={today.AddDays(-1):yyyy-MM-dd}"));
        Assert.Single(await ListAsync("?limite=1"));
    }

    // ---------- Identité de l'employé ----------

    [Fact]
    public async Task L_employe_enregistre_est_celui_identifie_au_moment_de_l_operation()
    {
        var product = await kit.CreateProductAsync(stock: 10);
        var (firstNumber, firstId) = await kit.CreateEmployeeAsync(
            Permissions.SalesCreate, Permissions.PaymentsCollect, Permissions.SalesCancel);
        var (secondNumber, secondId) = await kit.CreateEmployeeAsync(
            Permissions.SalesCreate, Permissions.PaymentsCollect, Permissions.SalesCancel);
        var poste = await server.CreatePosteClientAsync();

        await ServerFixture.IdentifyAsync(poste, firstNumber, SalesTestKit.EmployeeSecret);
        var firstSale = await kit.SellOkAsync(SalesTestKit.Cash(product.Id), poste);
        var cardSale = await kit.SellOkAsync(SalesTestKit.Card(product.Id), poste);

        // Un autre employé s'identifie ensuite sur le même poste.
        await ServerFixture.IdentifyAsync(poste, secondNumber, SalesTestKit.EmployeeSecret);
        var secondSale = await kit.SellOkAsync(SalesTestKit.Cash(product.Id), poste);
        (await SalesTestKit.CardResultAsync(poste, cardSale.Id, accepted: true)).EnsureSuccessStatusCode();
        (await SalesTestKit.CancelAsync(poste, firstSale.Id)).EnsureSuccessStatusCode();

        var first = await kit.ReadSaleAsync(firstSale.Id);
        Assert.Equal(firstId, first.EmployeeId);
        Assert.Equal(firstId, first.Payment!.EmployeeId);
        Assert.Equal(secondId, first.Cancellation!.EmployeeId);
        Assert.Equal(secondId, (await kit.ReadSaleAsync(secondSale.Id)).EmployeeId);
        Assert.Equal(firstId, (await kit.ReadSaleAsync(cardSale.Id)).Payment!.EmployeeId);
        Assert.Equal(secondId, await kit.ScalarAsync<Guid>(
            "SELECT resolved_by FROM payments WHERE sale_id = @Id", new { cardSale.Id }));
        Assert.Equal(firstId, await kit.ScalarAsync<Guid>(
            "SELECT DISTINCT employee_id FROM stock_movements WHERE source_id = @id AND movement_type = 'VENTE'",
            new { id = firstSale.Id.ToString() }));
        Assert.Equal(secondId, await kit.ScalarAsync<Guid>(
            "SELECT employee_id FROM cash_movements WHERE sale_id = @Id", new { firstSale.Id }));
        Assert.Equal(firstId, await kit.ScalarAsync<Guid>(
            "SELECT employee_id FROM audit_logs WHERE action = @action AND entity_id = @id",
            new { action = SaleAuditActions.SaleCreated, id = firstSale.Id.ToString() }));
    }

    [Fact]
    public async Task Le_jeton_d_un_employe_remplace_sur_le_poste_ne_permet_plus_de_vendre()
    {
        var product = await kit.CreateProductAsync(stock: 10);
        var (firstNumber, _) = await kit.CreateEmployeeAsync(Permissions.SalesCreate, Permissions.PaymentsCollect);
        var (secondNumber, _) = await kit.CreateEmployeeAsync();
        var poste = await server.CreatePosteClientAsync();
        var first = await ServerFixture.IdentifyAsync(poste, firstNumber, SalesTestKit.EmployeeSecret);
        await ServerFixture.IdentifyAsync(poste, secondNumber, SalesTestKit.EmployeeSecret);

        // Le second employé n'a pas le droit de vendre ; l'ancien jeton du premier est éteint.
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SellAsync(poste, SalesTestKit.Cash(product.Id)), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        poste.DefaultRequestHeaders.Remove(ApiHeaders.EmployeeToken);
        poste.DefaultRequestHeaders.Add(ApiHeaders.EmployeeToken, first.Token);
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SellAsync(poste, SalesTestKit.Cash(product.Id)),
            HttpStatusCode.Unauthorized, ApiErrorCodes.EmployeeNotIdentified);

        Assert.Equal(10m, await kit.StockAsync(product.Id));
    }

    // ---------- Permissions ----------

    [Fact]
    public async Task Vendre_et_encaisser_sont_deux_droits_distincts()
    {
        var product = await kit.CreateProductAsync(stock: 10);
        var customer = await kit.CreateCustomerAsync();
        var (seller, _) = await kit.CreateEmployeeClientAsync(Permissions.SalesCreate);
        var (collector, _) = await kit.CreateEmployeeClientAsync(Permissions.PaymentsCollect);

        // Vendre sans encaisser : ni espèces ni TPE.
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SellAsync(seller, SalesTestKit.Cash(product.Id)), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SellAsync(seller, SalesTestKit.Card(product.Id)), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        // Encaisser sans vendre : aucune vente.
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SellAsync(collector, SalesTestKit.Cash(product.Id)), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        Assert.Equal(10m, await kit.StockAsync(product.Id));

        // Une vente impayée n'encaisse rien : le droit de vendre suffit.
        await kit.SellOkAsync(SalesTestKit.Unpaid(product.Id, customer.Id), seller);

        // La réponse du TPE est un encaissement.
        var pending = await kit.SellOkAsync(SalesTestKit.Card(product.Id));
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.CardResultAsync(seller, pending.Id, accepted: true), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        (await SalesTestKit.CardResultAsync(collector, pending.Id, accepted: true)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Chaque_route_des_ventes_est_refusee_sans_son_droit()
    {
        var product = await kit.CreateProductAsync(stock: 10);
        var sale = await kit.SellOkAsync(SalesTestKit.Cash(product.Id));
        // Tous les droits des ventes sauf celui de la route essayée.
        string[] all =
        [
            Permissions.SalesCreate, Permissions.PaymentsCollect, Permissions.DiscountsApply, Permissions.SalesCancel,
            Permissions.SalesHistoryView, Permissions.ReceivablesView, Permissions.ReceivablesSettle, Permissions.RegisterOpen,
        ];
        async Task<HttpClient> WithoutAsync(string permission) =>
            (await kit.CreateEmployeeClientAsync(all.Where(code => code != permission).ToArray())).Client;

        var noSale = await WithoutAsync(Permissions.SalesCreate);
        var noCancel = await WithoutAsync(Permissions.SalesCancel);
        var noHistory = await WithoutAsync(Permissions.SalesHistoryView);
        var noCollect = await WithoutAsync(Permissions.PaymentsCollect);

        var refusals = new[]
        {
            await SalesTestKit.SellAsync(noSale, SalesTestKit.Cash(product.Id)),
            await SalesTestKit.CancelAsync(noCancel, sale.Id),
            await SalesTestKit.CardResultAsync(noCollect, sale.Id, accepted: true),
            await noHistory.GetAsync(SaleRoutes.Sales),
            await noHistory.GetAsync(SaleRoutes.Sale(sale.Id)),
            await noHistory.GetAsync(SaleRoutes.ByTicket(sale.TicketNumber)),
        };
        foreach (var refusal in refusals)
        {
            await StockTestKit.AssertErrorAsync(refusal, HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        }

        // Sans employé identifié, puis sans poste connecté.
        var poste = await server.CreatePosteClientAsync();
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SellAsync(poste, SalesTestKit.Cash(product.Id)),
            HttpStatusCode.Unauthorized, ApiErrorCodes.EmployeeNotIdentified);
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.SellAsync(server.Client, SalesTestKit.Cash(product.Id)),
            HttpStatusCode.Unauthorized, ApiErrorCodes.PosteNotSignedIn);

        Assert.Equal(SaleStatuses.Completed, (await kit.ReadSaleAsync(sale.Id)).Status);
        Assert.Equal(9m, await kit.StockAsync(product.Id));
    }
}
