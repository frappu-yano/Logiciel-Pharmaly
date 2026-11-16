using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Modules;
using Pharmaly.Poste.Modules.Sales;

namespace Pharmaly.Tests.Poste;

/// <summary>Imprimante de test : retient les tickets qu'on lui confie.</summary>
internal sealed class RecordingTicketPrinter : ITicketPrinter
{
    public List<SaleDetail> Printed { get; } = [];

    public void Print(SaleDetail ticket) => Printed.Add(ticket);
}

public sealed class SalesViewModelTests
{
    private readonly FakeServer server =
        new FakeServer { HasPosteSession = true, EmployeeIdentified = true }.AsAdministrator().WithOpenCashSession();

    private readonly RecordingTicketPrinter printer = new();

    /// <summary>Le module tel que le poste le construit : les droits affichés sont ceux de l'employé identifié.</summary>
    private SalesViewModel Create() => new(
        server, server, server, server, SalesRights.From(code => server.EmployeePermissions.Contains(code)), TimeSpan.Zero, printer);

    private async Task<SalesViewModel> OpenAsync()
    {
        var viewModel = Create();
        await viewModel.ActivateAsync();
        return viewModel;
    }

    private void Without(params string[] permissions) =>
        server.EmployeePermissions = server.EmployeePermissions.Except(permissions).ToList();

    /// <summary>Un lecteur de codes-barres : les chiffres, puis Entrée.</summary>
    private static async Task ScanAsync(SalesViewModel viewModel, ProductSummary product)
    {
        viewModel.Query = product.Barcode!;
        await viewModel.SubmitQueryAsync();
    }

    private async Task<SalesViewModel> OpenWithCremeAndSerumAsync()
    {
        var viewModel = await OpenAsync();
        await ScanAsync(viewModel, FakeServer.Creme);
        await ScanAsync(viewModel, FakeServer.Serum);
        return viewModel;
    }

    /// <summary>Espèces jusqu'à la confirmation incluse : touche *, montant reçu, Entrée, Entrée.</summary>
    private static async Task PayCashAsync(SalesViewModel viewModel, string received = "")
    {
        await viewModel.BeginCashAsync();
        viewModel.ReceivedText = received;
        viewModel.ValidateCashAmount();
        await viewModel.ConfirmSaleAsync();
    }

    private const decimal CremeAndSerum = 1850.10m + 4200.20m;

    // ---------- Panier ----------

    [Fact]
    public async Task Un_scan_ajoute_une_ligne_et_un_second_scan_du_meme_produit_porte_sa_quantite_a_2()
    {
        var viewModel = await OpenAsync();

        await ScanAsync(viewModel, FakeServer.Creme);

        var line = Assert.Single(viewModel.Lines);
        Assert.Equal(FakeServer.Creme.Id, line.ProductId);
        Assert.Equal(1, line.Quantity);
        Assert.Equal("1 850,10 DA", viewModel.TotalText);
        Assert.Equal(string.Empty, viewModel.Query);

        await ScanAsync(viewModel, FakeServer.Creme);

        Assert.Same(line, Assert.Single(viewModel.Lines));
        Assert.Equal(2, line.Quantity);
        Assert.Equal("2", line.QuantityText);
        Assert.Equal("3 700,20 DA", line.LineTotalText);
        Assert.Equal("3 700,20 DA", viewModel.TotalText);
        Assert.Same(line, viewModel.SelectedLine);
        Assert.Equal(string.Empty, viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Un_code_barres_inconnu_n_ajoute_rien_et_le_dit()
    {
        var viewModel = await OpenAsync();

        viewModel.Query = "2999999999999";
        await viewModel.SubmitQueryAsync();

        Assert.Empty(viewModel.Lines);
        Assert.Equal("Code-barres inconnu : 2999999999999. Rien n'a été ajouté.", viewModel.ErrorMessage);
        Assert.Equal(string.Empty, viewModel.Query);
    }

    [Fact]
    public async Task La_recherche_par_nom_propose_les_produits_et_Entree_ajoute_celui_qui_est_choisi()
    {
        var viewModel = await OpenAsync();

        viewModel.Query = "m";

        Assert.Equal([FakeServer.Creme.Id, FakeServer.Serum.Id], viewModel.Results.Select(product => product.Id));
        Assert.True(viewModel.HasResults);
        Assert.Equal(FakeServer.Creme.Id, viewModel.SelectedResult!.Id);

        viewModel.MoveResultSelection(+1);
        await viewModel.SubmitQueryAsync();

        Assert.Equal(FakeServer.Serum.Id, Assert.Single(viewModel.Lines).ProductId);
        Assert.Equal(string.Empty, viewModel.Query);
        Assert.False(viewModel.HasResults);
    }

    [Fact]
    public async Task Une_recherche_sans_resultat_le_dit_et_n_ajoute_rien()
    {
        var viewModel = await OpenAsync();

        viewModel.Query = "aspirine";
        await viewModel.SubmitQueryAsync();

        Assert.Empty(viewModel.Lines);
        Assert.Equal("Aucun produit trouvé pour « aspirine ».", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Plus_et_moins_changent_la_quantite_sans_jamais_descendre_a_zero_et_Suppr_retire_la_ligne()
    {
        var viewModel = await OpenWithCremeAndSerumAsync();
        viewModel.MoveLineSelection(-1);
        Assert.Equal(FakeServer.Creme.Id, viewModel.SelectedLine!.ProductId);

        viewModel.IncreaseSelectedLine();
        viewModel.IncreaseSelectedLine();
        Assert.Equal(3, viewModel.SelectedLine.Quantity);
        Assert.Equal(Money.ToDisplay((3 * 1850.10m) + 4200.20m), viewModel.TotalText);

        viewModel.DecreaseSelectedLine();
        viewModel.DecreaseSelectedLine();
        viewModel.DecreaseSelectedLine();
        Assert.Equal(1, viewModel.SelectedLine.Quantity);

        viewModel.RemoveSelectedLine();
        Assert.Equal(FakeServer.Serum.Id, Assert.Single(viewModel.Lines).ProductId);
        Assert.Equal(FakeServer.Serum.Id, viewModel.SelectedLine!.ProductId);
        Assert.Equal("4 200,20 DA", viewModel.TotalText);
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData(" 12 ", 12)]
    public async Task La_quantite_se_saisit_directement(string typed, int expected)
    {
        var viewModel = await OpenAsync();
        await ScanAsync(viewModel, FakeServer.Creme);

        await viewModel.BeginQuantityEditAsync();
        Assert.Equal(CheckoutStep.Quantity, viewModel.Step);
        Assert.Equal("1", viewModel.QuantityText);
        viewModel.QuantityText = typed;
        await viewModel.ConfirmQuantityAsync();

        Assert.Equal(CheckoutStep.None, viewModel.Step);
        Assert.Equal(expected, viewModel.Lines[0].Quantity);
        Assert.Equal(Money.ToDisplay(expected * 1850.10m), viewModel.TotalText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("1,2345")]
    public async Task Une_quantite_invalide_est_refusee_et_la_ligne_ne_change_pas(string typed)
    {
        var viewModel = await OpenAsync();
        await ScanAsync(viewModel, FakeServer.Creme);

        await viewModel.BeginQuantityEditAsync();
        viewModel.QuantityText = typed;
        await viewModel.ConfirmQuantityAsync();

        Assert.Equal(CheckoutStep.Quantity, viewModel.Step);
        Assert.Contains("Quantité invalide", viewModel.StepError);
        Assert.Equal(1, viewModel.Lines[0].Quantity);
    }

    [Fact]
    public async Task Pas_de_fraction_pour_un_produit_vendu_a_l_unite()
    {
        var viewModel = await OpenAsync();
        await ScanAsync(viewModel, FakeServer.Creme);

        await viewModel.BeginQuantityEditAsync();
        viewModel.QuantityText = "1,5";
        await viewModel.ConfirmQuantityAsync();

        Assert.Equal(CheckoutStep.Quantity, viewModel.Step);
        Assert.Equal($"« {FakeServer.Creme.Name} » se vend à l'unité : la quantité doit être un nombre entier.", viewModel.StepError);
        Assert.Equal(1, viewModel.Lines[0].Quantity);

        // Une quantité entière passe, sans relire la fiche du produit.
        viewModel.QuantityText = "4";
        await viewModel.ConfirmQuantityAsync();
        Assert.Equal(4, viewModel.Lines[0].Quantity);
        Assert.Equal(1, server.SoldByUnitReads);
    }

    [Theory]
    [InlineData("0,5")]
    [InlineData("0.5")]
    public async Task Une_fraction_est_acceptee_pour_un_produit_vendu_au_detail_avec_virgule_ou_point(string typed)
    {
        server.FractionalProducts.Add(FakeServer.Serum.Id);
        var viewModel = await OpenAsync();
        await ScanAsync(viewModel, FakeServer.Serum);

        await viewModel.BeginQuantityEditAsync();
        viewModel.QuantityText = typed;
        await viewModel.ConfirmQuantityAsync();

        Assert.Equal(CheckoutStep.None, viewModel.Step);
        Assert.Equal(0.5m, viewModel.Lines[0].Quantity);
        Assert.Equal("0,5", viewModel.Lines[0].QuantityText);
        Assert.Equal(Money.ToDisplay(SaleMath.LineTotal(0.5m, 4200.20m)), viewModel.TotalText);
    }

    [Fact]
    public async Task Une_quantite_superieure_au_stock_connu_est_signalee_tout_de_suite()
    {
        server.StockLevels[FakeServer.Creme.Id] = 1;
        var viewModel = await OpenAsync();

        await ScanAsync(viewModel, FakeServer.Creme);
        Assert.Equal("1", viewModel.Lines[0].StockText);
        Assert.False(viewModel.Lines[0].IsOverStock);
        Assert.Equal(string.Empty, viewModel.StockWarning);

        await ScanAsync(viewModel, FakeServer.Creme);

        Assert.True(viewModel.Lines[0].IsOverStock);
        Assert.Equal(
            $"Stock insuffisant d'après le dernier stock connu : « {FakeServer.Creme.Name} » (demandé 2, en stock 1).",
            viewModel.StockWarning);
    }

    // ---------- Totaux et remise ----------

    [Fact]
    public async Task Les_totaux_sont_ceux_de_SaleMath_avec_une_remise_en_montant()
    {
        var viewModel = await OpenWithCremeAndSerumAsync();
        viewModel.IncreaseSelectedLine();

        await viewModel.BeginDiscountEditAsync();
        viewModel.DiscountInput = "250,30";
        viewModel.ConfirmDiscount();

        var subtotal = SaleMath.LineTotal(1, 1850.10m) + SaleMath.LineTotal(2, 4200.20m);
        Assert.Equal(CheckoutStep.None, viewModel.Step);
        Assert.Equal(subtotal, viewModel.Subtotal);
        Assert.Equal(250.30m, viewModel.DiscountAmount);
        Assert.Equal(subtotal - 250.30m, viewModel.Total);
        Assert.Equal("10 250,50 DA", viewModel.SubtotalText);
        Assert.Equal("− 250,30 DA", viewModel.DiscountAmountText);
        Assert.Equal("10 000,20 DA", viewModel.TotalText);
        Assert.Equal("Remise", viewModel.DiscountTitle);

        // Le serveur calcule le même total : la vente passe avec ExpectedTotal.
        await PayCashAsync(viewModel);
        var ticket = Assert.Single(server.Tickets);
        Assert.Equal(viewModel.CompletedSale!.Total, ticket.Total);
        Assert.Equal(10000.20m, ticket.Total);
        Assert.Equal(DiscountKinds.Amount, server.SaleRequests[0].DiscountKind);
        Assert.Equal(250.30m, server.SaleRequests[0].DiscountValue);
    }

    [Fact]
    public async Task Une_remise_en_pourcentage_est_arrondie_au_centime_la_demie_vers_le_haut_comme_le_serveur()
    {
        var viewModel = await OpenAsync();
        await ScanAsync(viewModel, FakeServer.Creme);

        await viewModel.BeginDiscountEditAsync();
        viewModel.DiscountInput = "5 %";
        viewModel.ConfirmDiscount();

        // 1 850,10 × 5 % = 92,505 : arrondi à 92,51.
        Assert.Equal(92.51m, SaleMath.PercentDiscount(1850.10m, 5));
        Assert.Equal(92.51m, viewModel.DiscountAmount);
        Assert.Equal(1757.59m, viewModel.Total);
        Assert.Equal("Remise 5 %", viewModel.DiscountTitle);
        Assert.Equal("1 757,59 DA", viewModel.TotalText);

        await PayCashAsync(viewModel);
        var request = Assert.Single(server.SaleRequests);
        Assert.Equal(DiscountKinds.Percent, request.DiscountKind);
        Assert.Equal(5m, request.DiscountValue);
        Assert.Equal(1757.59m, request.ExpectedTotal);
        Assert.Equal(1757.59m, Assert.Single(server.Tickets).Total);
        Assert.Equal(92.51m, server.Tickets[0].DiscountAmount);
    }

    [Theory]
    [InlineData("1850,11", "La remise (1 850,11 DA) dépasse le total de la vente (1 850,10 DA).")]
    [InlineData("101 %", "Remise invalide")]
    [InlineData("-5", "Remise invalide")]
    [InlineData("dix", "Remise invalide")]
    [InlineData("10,555", "Remise invalide")]
    public async Task Une_remise_superieure_au_total_ou_invalide_est_refusee(string typed, string expectedError)
    {
        var viewModel = await OpenAsync();
        await ScanAsync(viewModel, FakeServer.Creme);

        await viewModel.BeginDiscountEditAsync();
        viewModel.DiscountInput = typed;
        viewModel.ConfirmDiscount();

        Assert.Equal(CheckoutStep.Discount, viewModel.Step);
        Assert.StartsWith(expectedError, viewModel.StepError);
        Assert.False(viewModel.HasDiscount);
        Assert.Equal(1850.10m, viewModel.Total);
    }

    [Fact]
    public async Task Une_remise_devenue_superieure_au_total_bloque_l_encaissement_avant_tout_envoi()
    {
        var viewModel = await OpenWithCremeAndSerumAsync();
        await viewModel.BeginDiscountEditAsync();
        viewModel.DiscountInput = "5000";
        viewModel.ConfirmDiscount();

        // Le sérum est retiré : la remise de 5 000 dépasse maintenant le total de 1 850,10.
        viewModel.RemoveSelectedLine();
        Assert.True(viewModel.IsDiscountTooLarge);

        await viewModel.BeginCashAsync();

        Assert.Equal(CheckoutStep.None, viewModel.Step);
        Assert.Contains("dépasse le total de la vente", viewModel.ErrorMessage);
        Assert.Empty(server.SaleRequests);
    }

    [Fact]
    public async Task Sans_le_droit_de_remise_la_remise_n_est_ni_proposee_ni_saisissable()
    {
        Without(Permissions.DiscountsApply);
        var viewModel = await OpenAsync();
        await ScanAsync(viewModel, FakeServer.Creme);

        Assert.False(viewModel.CanDiscount);
        await viewModel.BeginDiscountEditAsync();
        Assert.Equal(CheckoutStep.None, viewModel.Step);

        await PayCashAsync(viewModel);
        Assert.Null(Assert.Single(server.SaleRequests).DiscountKind);
        Assert.Null(server.SaleRequests[0].DiscountValue);
    }

    [Fact]
    public async Task Avec_le_droit_de_remise_la_remise_est_proposee()
    {
        var viewModel = await OpenAsync();

        Assert.True(viewModel.CanDiscount);
    }

    // ---------- Espèces ----------

    [Fact]
    public async Task Especes_sans_montant_saisi_le_client_donne_le_montant_exact()
    {
        var viewModel = await OpenWithCremeAndSerumAsync();

        await viewModel.BeginCashAsync();
        Assert.Equal(CheckoutStep.CashAmount, viewModel.Step);
        Assert.Equal("0,00 DA", viewModel.ChangePreviewText);
        viewModel.ValidateCashAmount();

        // Le montant est validé : rien n'est encore parti, la confirmation est attendue.
        Assert.Equal(CheckoutStep.CashConfirm, viewModel.Step);
        Assert.Equal(CremeAndSerum, viewModel.AmountReceived);
        Assert.Equal("0,00 DA", viewModel.ChangeDueText);
        Assert.Empty(server.SaleRequests);

        await viewModel.ConfirmSaleAsync();

        var request = Assert.Single(server.SaleRequests);
        Assert.Equal(SalePaymentModes.Cash, request.PaymentMode);
        Assert.Equal(CremeAndSerum, request.AmountReceived);
        Assert.Equal(CremeAndSerum, request.ExpectedTotal);
        Assert.Null(request.CustomerId);
        Assert.Equal(
            [new SaleLineRequest(FakeServer.Creme.Id, 1), new SaleLineRequest(FakeServer.Serum.Id, 1)], request.Lines);
        Assert.Equal(CheckoutStep.Done, viewModel.Step);
        Assert.Equal("Ticket D1-000001", viewModel.DoneTicketText);
        Assert.Equal("6 050,30 DA", viewModel.DoneTotalText);
        Assert.Equal("0,00 DA", viewModel.DoneChangeText);
        Assert.True(viewModel.IsDoneCash);
        Assert.False(viewModel.IsDoneUnpaid);
    }

    [Theory]
    [InlineData("10000", "3 949,70 DA")]
    [InlineData("6 100,50", "50,20 DA")]
    [InlineData("6100.5", "50,20 DA")]
    public async Task Especes_la_monnaie_a_rendre_est_exacte(string received, string expectedChange)
    {
        var viewModel = await OpenWithCremeAndSerumAsync();

        await viewModel.BeginCashAsync();
        viewModel.ReceivedText = received;
        Assert.Equal(expectedChange, viewModel.ChangePreviewText);
        viewModel.ValidateCashAmount();
        Assert.Equal(expectedChange, viewModel.ChangeDueText);
        await viewModel.ConfirmSaleAsync();

        Assert.Equal(expectedChange, viewModel.DoneChangeText);
        Assert.Equal(Money.ToDisplay(server.Tickets[0].Payment!.ChangeGiven!.Value), viewModel.DoneChangeText);
        Assert.Equal(server.SaleRequests[0].AmountReceived - CremeAndSerum, server.Tickets[0].Payment!.ChangeGiven);
    }

    [Theory]
    [InlineData("6000", "Montant reçu insuffisant : 6 000,00 DA pour un total de 6 050,30 DA.")]
    [InlineData("abc", "Montant reçu invalide")]
    [InlineData("-7000", "Montant reçu invalide")]
    [InlineData("7000,123", "Montant reçu invalide")]
    [InlineData("7.000,00", "Montant reçu invalide")]
    public async Task Un_montant_recu_insuffisant_ou_invalide_est_bloque_avant_tout_envoi(string received, string expectedError)
    {
        var viewModel = await OpenWithCremeAndSerumAsync();

        await viewModel.BeginCashAsync();
        viewModel.ReceivedText = received;
        viewModel.ValidateCashAmount();
        await viewModel.ConfirmSaleAsync();

        Assert.Equal(CheckoutStep.CashAmount, viewModel.Step);
        Assert.StartsWith(expectedError, viewModel.StepError);
        Assert.Equal("—", viewModel.ChangePreviewText);
        Assert.Empty(server.SaleRequests);
        Assert.Equal(2, viewModel.Lines.Count);
    }

    [Fact]
    public async Task Avec_un_panier_vide_aucun_encaissement_ne_s_ouvre()
    {
        var viewModel = await OpenAsync();

        await viewModel.BeginCashAsync();
        await viewModel.BeginCardAsync();
        await viewModel.BeginUnpaidAsync();

        Assert.Equal(CheckoutStep.None, viewModel.Step);
        Assert.Equal(SalesViewModel.EmptyCartMessage, viewModel.ErrorMessage);
        Assert.Empty(server.SaleRequests);
    }

    [Fact]
    public async Task Apres_la_vente_le_ticket_est_confie_a_l_imprimante_et_un_seul_appui_demarre_la_vente_suivante()
    {
        var viewModel = await OpenWithCremeAndSerumAsync();
        await PayCashAsync(viewModel, "7000");

        // Écran de fin : le panier est déjà vide, le ticket est passé au point d'accroche de l'impression.
        Assert.Equal(CheckoutStep.Done, viewModel.Step);
        Assert.Empty(viewModel.Lines);
        var printed = Assert.Single(printer.Printed);
        Assert.Equal(server.Tickets[0].Id, printed.Id);
        Assert.Equal(2, printed.Lines.Count);

        viewModel.StartNextSale();

        Assert.Equal(CheckoutStep.None, viewModel.Step);
        Assert.True(viewModel.IsCartEditable);
        Assert.Null(viewModel.CompletedSale);
        Assert.Equal("0,00 DA", viewModel.TotalText);
        Assert.Equal(string.Empty, viewModel.Query);
        Assert.Equal(string.Empty, viewModel.ErrorMessage);

        // Le stock connu du serveur a baissé d'une unité par article.
        Assert.Equal(9, server.StockLevels[FakeServer.Creme.Id]);
        Assert.Equal(9, server.StockLevels[FakeServer.Serum.Id]);
    }

    // ---------- TPE ----------

    [Fact]
    public async Task TPE_accepte_la_vente_reste_en_attente_jusqu_a_la_reponse_et_Entree_ne_repond_pas()
    {
        var viewModel = await OpenWithCremeAndSerumAsync();

        await viewModel.BeginCardAsync();
        Assert.Equal(CheckoutStep.CardConfirm, viewModel.Step);
        Assert.Empty(server.SaleRequests);

        await viewModel.ConfirmSaleAsync();

        var request = Assert.Single(server.SaleRequests);
        Assert.Equal(SalePaymentModes.Card, request.PaymentMode);
        Assert.Null(request.AmountReceived);
        Assert.Equal(CremeAndSerum, request.ExpectedTotal);
        Assert.Equal(CheckoutStep.CardPending, viewModel.Step);
        Assert.Equal(SaleStatuses.CardPending, server.Tickets[0].Status);
        Assert.Equal("Ticket D1-000001 — 6 050,30 DA", viewModel.PendingCardText);
        Assert.Empty(printer.Printed);

        // Entrée (confirmation) et Échap ne répondent pas à la place du terminal.
        await viewModel.ConfirmSaleAsync();
        viewModel.CancelStep();
        Assert.Equal(CheckoutStep.CardPending, viewModel.Step);
        Assert.Empty(server.CardRequests);
        Assert.Single(server.SaleRequests);

        await viewModel.AnswerCardAsync(accepted: true);

        Assert.True(Assert.Single(server.CardRequests).Request.Accepted);
        Assert.Equal(SaleStatuses.Completed, server.Tickets[0].Status);
        Assert.Equal(CheckoutStep.Done, viewModel.Step);
        Assert.True(viewModel.IsDoneCard);
        Assert.Empty(viewModel.Lines);
        Assert.Single(printer.Printed);
    }

    [Fact]
    public async Task TPE_refuse_la_vente_est_annulee_le_panier_reste_et_la_tentative_suivante_a_un_nouvel_identifiant()
    {
        var viewModel = await OpenWithCremeAndSerumAsync();
        await viewModel.BeginCardAsync();
        await viewModel.ConfirmSaleAsync();
        Assert.Equal(9, server.StockLevels[FakeServer.Creme.Id]);

        await viewModel.AnswerCardAsync(accepted: false);

        // Le serveur a annulé la vente et restitué le stock ; l'écran le dit et garde le panier.
        Assert.False(Assert.Single(server.CardRequests).Request.Accepted);
        Assert.Equal(SaleStatuses.Cancelled, server.Tickets[0].Status);
        Assert.Equal(10, server.StockLevels[FakeServer.Creme.Id]);
        Assert.Equal(CheckoutStep.None, viewModel.Step);
        Assert.True(viewModel.IsCartEditable);
        Assert.Equal(2, viewModel.Lines.Count);
        Assert.Equal("6 050,30 DA", viewModel.TotalText);
        Assert.Equal(
            "Paiement refusé : la vente D1-000001 est annulée et le stock est restitué. "
            + "Le panier est conservé : encaissez autrement (ce sera un nouveau ticket).",
            viewModel.InfoMessage);
        Assert.Empty(printer.Printed);

        // Encaisser autrement : un nouveau ticket, donc un nouvel identifiant d'opération.
        await PayCashAsync(viewModel, "7000");

        Assert.Equal(2, server.SaleRequests.Count);
        Assert.NotEqual(server.SaleRequests[0].OperationId, server.SaleRequests[1].OperationId);
        Assert.Equal(2, server.Tickets.Count);
        Assert.Equal("Ticket D1-000002", viewModel.DoneTicketText);
        Assert.Equal(SaleStatuses.Completed, server.Tickets[1].Status);
    }

    [Fact]
    public async Task TPE_la_reponse_perdue_par_le_reseau_se_redonne_avec_le_meme_identifiant()
    {
        var viewModel = await OpenWithCremeAndSerumAsync();
        await viewModel.BeginCardAsync();
        await viewModel.ConfirmSaleAsync();
        server.FailNextTicketWrites = 1;

        await viewModel.AnswerCardAsync(accepted: true);

        Assert.Equal(CheckoutStep.CardPending, viewModel.Step);
        Assert.StartsWith("Réponse non confirmée", viewModel.StepError);

        await viewModel.AnswerCardAsync(accepted: true);

        Assert.Equal(2, server.CardRequests.Count);
        Assert.Equal(server.CardRequests[0].Request.OperationId, server.CardRequests[1].Request.OperationId);
        Assert.Equal(CheckoutStep.Done, viewModel.Step);
    }

    // ---------- Impayé ----------

    [Fact]
    public async Task Une_vente_impayee_sans_client_est_impossible()
    {
        var viewModel = await OpenWithCremeAndSerumAsync();

        await viewModel.BeginUnpaidAsync();
        Assert.Equal(CheckoutStep.UnpaidCustomer, viewModel.Step);

        // Entrée sans client choisi, puis tentative de confirmation : rien ne part.
        await viewModel.ChooseCustomerAsync();
        Assert.Equal(CheckoutStep.UnpaidCustomer, viewModel.Step);
        Assert.Equal(SalesViewModel.CustomerRequiredMessage, viewModel.StepError);
        await viewModel.ConfirmSaleAsync();

        Assert.Equal(CheckoutStep.UnpaidCustomer, viewModel.Step);
        Assert.Null(viewModel.Customer);
        Assert.Empty(server.SaleRequests);
    }

    [Fact]
    public async Task Une_vente_impayee_montre_le_client_et_le_montant_avant_confirmation_puis_marque_le_ticket_impaye()
    {
        var viewModel = await OpenWithCremeAndSerumAsync();
        await viewModel.BeginUnpaidAsync();

        viewModel.CustomerQuery = "benali";
        Assert.Equal(FakeServer.Benali.Id, Assert.Single(viewModel.CustomerResults).Id);
        await viewModel.ChooseCustomerAsync();

        Assert.Equal(CheckoutStep.UnpaidConfirm, viewModel.Step);
        Assert.Equal("Nadia Benali — 0550 12 34 56", viewModel.CustomerText);
        Assert.Equal("6 050,30 DA", viewModel.TotalText);
        Assert.Empty(server.SaleRequests);

        await viewModel.ConfirmSaleAsync();

        var request = Assert.Single(server.SaleRequests);
        Assert.Equal(SalePaymentModes.Unpaid, request.PaymentMode);
        Assert.Equal(FakeServer.Benali.Id, request.CustomerId);
        Assert.Null(request.AmountReceived);
        Assert.Equal(CremeAndSerum, request.ExpectedTotal);
        Assert.Equal(CheckoutStep.Done, viewModel.Step);
        Assert.True(viewModel.IsDoneUnpaid);
        Assert.False(viewModel.IsDoneCash);
        Assert.Equal("Client : Nadia Benali", viewModel.DoneCustomerText);
        Assert.True(server.Tickets[0].IsUnpaid);
        Assert.Null(server.Tickets[0].Payment);
        Assert.Null(viewModel.Customer);
    }

    [Fact]
    public async Task Un_client_se_cree_rapidement_avec_son_nom_et_un_telephone_facultatif()
    {
        var viewModel = await OpenWithCremeAndSerumAsync();
        await viewModel.BeginUnpaidAsync();
        viewModel.CustomerQuery = "Yacine Nouveau";
        Assert.Empty(viewModel.CustomerResults);

        viewModel.BeginNewCustomer();
        Assert.Equal(CheckoutStep.UnpaidNewCustomer, viewModel.Step);
        Assert.Equal("Yacine Nouveau", viewModel.NewCustomerName);

        // Le nom est obligatoire.
        viewModel.NewCustomerName = "  ";
        await viewModel.CreateCustomerAsync();
        Assert.Equal("Le nom du client est obligatoire.", viewModel.StepError);
        Assert.Empty(server.CustomerRequests);

        viewModel.NewCustomerName = "Yacine Nouveau";
        await viewModel.CreateCustomerAsync();

        var created = Assert.Single(server.CustomerRequests);
        Assert.Equal("Yacine Nouveau", created.Name);
        Assert.Null(created.Phone);
        Assert.Equal(CheckoutStep.UnpaidConfirm, viewModel.Step);
        Assert.Equal("Yacine Nouveau", viewModel.CustomerText);

        await viewModel.ConfirmSaleAsync();
        Assert.Equal(server.Customers.Single(customer => customer.Name == "Yacine Nouveau").Id, server.SaleRequests[0].CustomerId);
        Assert.Equal("Client : Yacine Nouveau", viewModel.DoneCustomerText);
    }

    [Fact]
    public async Task La_creation_de_client_retentee_apres_une_panne_garde_le_meme_identifiant()
    {
        var viewModel = await OpenWithCremeAndSerumAsync();
        await viewModel.BeginUnpaidAsync();
        viewModel.BeginNewCustomer();
        viewModel.NewCustomerName = "Lyes Coupure";
        viewModel.NewCustomerPhone = "0661 00 00 00";
        server.FailNextCustomerWrites = 1;

        await viewModel.CreateCustomerAsync();
        Assert.Equal(CheckoutStep.UnpaidNewCustomer, viewModel.Step);
        Assert.StartsWith("Client non confirmé", viewModel.StepError);

        await viewModel.CreateCustomerAsync();

        Assert.Equal(2, server.CustomerRequests.Count);
        Assert.Equal(server.CustomerRequests[0], server.CustomerRequests[1]);
        Assert.Equal("0661 00 00 00", server.CustomerRequests[1].Phone);
        Assert.Equal(CheckoutStep.UnpaidConfirm, viewModel.Step);
    }

    // ---------- Fiabilité de l'enregistrement ----------

    [Fact]
    public async Task Un_double_declenchement_de_la_confirmation_ne_produit_qu_un_seul_envoi()
    {
        var viewModel = await OpenWithCremeAndSerumAsync();
        await viewModel.BeginCashAsync();
        viewModel.ValidateCashAmount();
        server.SaleGate = new TaskCompletionSource();

        // Double Entrée, puis double clic sur le bouton, pendant que le premier envoi est en route.
        var first = viewModel.ConfirmSaleAsync();
        var second = viewModel.ConfirmSaleAsync();
        viewModel.ConfirmSaleCommand.Execute(null);
        Assert.True(viewModel.IsSending);
        Assert.False(viewModel.IsCartEditable);
        Assert.Single(server.SaleRequests);

        server.SaleGate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Single(server.SaleRequests);
        Assert.Single(server.Tickets);
        Assert.False(viewModel.IsSending);
        Assert.Equal(CheckoutStep.Done, viewModel.Step);
    }

    [Theory]
    [InlineData(SaleSendFailure.RequestLost)]
    [InlineData(SaleSendFailure.AnswerLost)]
    public async Task Apres_une_panne_reseau_la_vente_est_non_confirmee_et_Reessayer_renvoie_le_meme_identifiant(SaleSendFailure failure)
    {
        var viewModel = await OpenWithCremeAndSerumAsync();
        server.SaleFailures.Enqueue(failure);

        await PayCashAsync(viewModel, "7000");

        // État explicite, panier figé : aucune frappe ne le modifie, Échap n'en sort pas.
        Assert.Equal(CheckoutStep.Unconfirmed, viewModel.Step);
        Assert.Equal("Vente non confirmée — ne pas remettre la marchandise", SalesViewModel.UnconfirmedTitle);
        Assert.False(viewModel.IsCartEditable);
        await ScanAsync(viewModel, FakeServer.Creme);
        viewModel.IncreaseSelectedLine();
        viewModel.RemoveSelectedLine();
        viewModel.CancelStep();
        await viewModel.BeginCardAsync();
        Assert.Equal(CheckoutStep.Unconfirmed, viewModel.Step);
        Assert.Equal([1m, 1m], viewModel.Lines.Select(line => line.Quantity));
        Assert.Empty(printer.Printed);

        await viewModel.RetrySaleAsync();

        // La même demande, le même identifiant : le serveur n'enregistre qu'un ticket.
        Assert.Equal(2, server.SaleRequests.Count);
        Assert.Equal(server.SaleRequests[0].OperationId, server.SaleRequests[1].OperationId);
        Assert.Same(server.SaleRequests[0], server.SaleRequests[1]);
        Assert.Single(server.Tickets);
        Assert.Equal(CheckoutStep.Done, viewModel.Step);
        Assert.Equal("949,70 DA", viewModel.DoneChangeText);
        Assert.Single(printer.Printed);
        Assert.Equal(9, server.StockLevels[FakeServer.Creme.Id]);
    }

    [Fact]
    public async Task Plusieurs_pannes_de_suite_gardent_toujours_le_meme_identifiant()
    {
        var viewModel = await OpenWithCremeAndSerumAsync();
        server.SaleFailures.Enqueue(SaleSendFailure.AnswerLost);
        server.SaleFailures.Enqueue(SaleSendFailure.RequestLost);
        server.SaleFailures.Enqueue(SaleSendFailure.AnswerLost);

        await PayCashAsync(viewModel);
        await viewModel.RetrySaleAsync();
        await viewModel.RetrySaleAsync();
        Assert.Equal(CheckoutStep.Unconfirmed, viewModel.Step);
        await viewModel.RetrySaleAsync();

        Assert.Equal(4, server.SaleRequests.Count);
        Assert.Single(server.SaleRequests.Select(request => request.OperationId).Distinct());
        Assert.Single(server.Tickets);
        Assert.Equal(CheckoutStep.Done, viewModel.Step);
    }

    [Fact]
    public async Task Un_contenu_modifie_ou_une_nouvelle_vente_recoit_un_nouvel_identifiant()
    {
        server.StockLevels[FakeServer.Creme.Id] = 1;
        var viewModel = await OpenAsync();
        await ScanAsync(viewModel, FakeServer.Creme);
        await ScanAsync(viewModel, FakeServer.Creme);

        // Première tentative : refusée par le serveur (stock). Le contenu est corrigé, puis renvoyé.
        await PayCashAsync(viewModel);
        Assert.Empty(server.Tickets);
        viewModel.DecreaseSelectedLine();
        await PayCashAsync(viewModel);
        Assert.Equal(CheckoutStep.Done, viewModel.Step);

        // Vente suivante.
        viewModel.StartNextSale();
        await ScanAsync(viewModel, FakeServer.Serum);
        await PayCashAsync(viewModel);

        Assert.Equal(3, server.SaleRequests.Count);
        Assert.Equal(3, server.SaleRequests.Select(request => request.OperationId).Distinct().Count());
        Assert.DoesNotContain(Guid.Empty, server.SaleRequests.Select(request => request.OperationId));
        Assert.Equal(2, server.Tickets.Count);
    }

    [Fact]
    public async Task TOTAL_DIFFERENT_recharge_les_prix_montre_la_difference_et_redemande_confirmation()
    {
        var viewModel = await OpenWithCremeAndSerumAsync();

        // Un autre poste modifie le prix de la crème pendant la vente.
        server.PriceChanges[FakeServer.Creme.Id] = 1900m;
        await PayCashAsync(viewModel, "7000");

        Assert.Empty(server.Tickets);
        Assert.Equal(CheckoutStep.TotalChanged, viewModel.Step);
        Assert.Equal(
            "Le total a changé : 6 100,20 DA au lieu de 6 050,30 DA. Vérifiez les prix avant d'encaisser.",
            viewModel.TotalChangeMessage);
        Assert.Equal([$"{FakeServer.Creme.Name} : 1 850,10 DA → 1 900,00 DA"], viewModel.PriceChanges);
        Assert.Equal("Ancien total : 6 050,30 DA — nouveau total : 6 100,20 DA", viewModel.TotalChangeSummary);
        Assert.Equal("1 900,00 DA", viewModel.Lines[0].UnitPriceText);
        Assert.Equal("6 100,20 DA", viewModel.TotalText);

        // Entrée : l'encaissement reprend à sa première étape. Rien ne part sans nouvelle confirmation.
        await viewModel.AcknowledgeTotalChangeAsync();
        Assert.Equal(CheckoutStep.CashAmount, viewModel.Step);
        Assert.Equal(string.Empty, viewModel.ReceivedText);
        Assert.Single(server.SaleRequests);

        viewModel.ReceivedText = "7000";
        viewModel.ValidateCashAmount();
        Assert.Equal("899,80 DA", viewModel.ChangeDueText);
        await viewModel.ConfirmSaleAsync();

        Assert.Equal(2, server.SaleRequests.Count);
        Assert.Equal(6100.20m, server.SaleRequests[1].ExpectedTotal);
        Assert.NotEqual(server.SaleRequests[0].OperationId, server.SaleRequests[1].OperationId);
        Assert.Equal(6100.20m, Assert.Single(server.Tickets).Total);
        Assert.Equal(CheckoutStep.Done, viewModel.Step);
    }

    [Fact]
    public async Task STOCK_INSUFFISANT_affiche_le_message_du_serveur_tel_quel_et_garde_le_panier()
    {
        var viewModel = await OpenWithCremeAndSerumAsync();

        // Un autre caissier vient de vendre presque tout le sérum.
        server.StockLevels[FakeServer.Serum.Id] = 0;
        await PayCashAsync(viewModel, "7000");

        Assert.Empty(server.Tickets);
        Assert.Equal(CheckoutStep.None, viewModel.Step);
        Assert.Equal($"Stock insuffisant pour « {FakeServer.Serum.Name} » : 1 demandé, 0 disponible.", viewModel.ErrorMessage);
        Assert.Equal(2, viewModel.Lines.Count);
        Assert.True(viewModel.IsCartEditable);

        // Le stock affiché est relu : la ligne fautive est signalée.
        Assert.True(viewModel.Lines[1].IsOverStock);
        Assert.Equal("0", viewModel.Lines[1].StockText);
    }

    [Fact]
    public async Task CAISSE_NON_OUVERTE_en_cours_de_vente_ramene_a_l_ouverture_sans_perdre_le_panier()
    {
        var viewModel = await OpenWithCremeAndSerumAsync();

        // La caisse est fermée entre-temps.
        server.CashSession = null;
        await PayCashAsync(viewModel, "7000");

        Assert.Empty(server.Tickets);
        Assert.Equal(SalesScreen.CashClosed, viewModel.Screen);
        Assert.Equal(CheckoutStep.None, viewModel.Step);
        Assert.Equal(
            "La caisse de ce poste n'est pas ouverte. Ouvrez la caisse avant de vendre ou d'encaisser.", viewModel.ErrorMessage);
        Assert.Equal(2, viewModel.Lines.Count);

        // L'employé rouvre la caisse ici : le panier est toujours là.
        viewModel.OpeningFloatText = "3000";
        viewModel.RequestCashOpening();
        await viewModel.ConfirmCashOpeningAsync();

        Assert.Equal(SalesScreen.Cart, viewModel.Screen);
        Assert.Equal([FakeServer.Creme.Id, FakeServer.Serum.Id], viewModel.Lines.Select(line => line.ProductId));
        Assert.Equal("6 050,30 DA", viewModel.TotalText);

        await PayCashAsync(viewModel, "7000");
        Assert.Single(server.Tickets);
        Assert.NotEqual(server.SaleRequests[0].OperationId, server.SaleRequests[1].OperationId);
    }

    [Fact]
    public async Task Un_autre_refus_du_serveur_est_affiche_tel_quel_et_le_panier_reste()
    {
        var viewModel = await OpenWithCremeAndSerumAsync();

        // Le droit d'encaisser est retiré pendant la vente : seul le serveur le sait.
        Without(Permissions.PaymentsCollect);
        await PayCashAsync(viewModel);

        Assert.Equal("Vous n'avez pas le droit d'effectuer cette action.", viewModel.ErrorMessage);
        Assert.Equal(CheckoutStep.None, viewModel.Step);
        Assert.Equal(2, viewModel.Lines.Count);
        Assert.Empty(server.Tickets);
    }

    [Fact]
    public async Task Une_session_perdue_pendant_la_vente_redemande_l_identification()
    {
        var viewModel = await OpenWithCremeAndSerumAsync();
        var asked = 0;
        viewModel.IdentificationRequired += (_, _) => asked++;

        server.EmployeeIdentified = false;
        await PayCashAsync(viewModel);

        Assert.Equal(1, asked);
        Assert.Empty(server.Tickets);
    }

    // ---------- Caisse ----------

    [Fact]
    public async Task Caisse_fermee_avec_le_droit_l_employe_l_ouvre_ici_avec_le_fond_compte_apres_confirmation()
    {
        server.CashSession = null;
        var viewModel = await OpenAsync();

        Assert.Equal(SalesScreen.CashClosed, viewModel.Screen);
        Assert.True(viewModel.CanOpenRegister);
        Assert.False(viewModel.IsCartEditable);

        // Saisie vide, puis invalide : refusées proprement.
        viewModel.RequestCashOpening();
        Assert.Equal("Saisissez le fond de caisse compté (0 si la caisse est vide).", viewModel.ErrorMessage);
        viewModel.OpeningFloatText = "douze";
        viewModel.RequestCashOpening();
        Assert.StartsWith("Fond de caisse invalide", viewModel.ErrorMessage);
        Assert.False(viewModel.IsConfirmingOpening);

        viewModel.OpeningFloatText = "12 500,50";
        viewModel.RequestCashOpening();

        // Confirmation demandée : rien n'est encore envoyé.
        Assert.True(viewModel.IsConfirmingOpening);
        Assert.Equal("Ouvrir la caisse avec un fond de caisse de 12 500,50 DA ?", viewModel.OpeningConfirmationText);
        Assert.Empty(server.OpenSessionRequests);

        await viewModel.ConfirmCashOpeningAsync();

        Assert.Equal(12500.50m, Assert.Single(server.OpenSessionRequests).OpeningFloat);
        Assert.NotEqual(Guid.Empty, server.OpenSessionRequests[0].OperationId);
        Assert.Equal(SalesScreen.Cart, viewModel.Screen);
        Assert.True(viewModel.IsCartEditable);
        Assert.Equal("Caisse ouverte avec un fond de caisse de 12 500,50 DA.", viewModel.InfoMessage);
        Assert.Equal($"Caisse 1 — ouverte par {FakeServer.EmployeeName}", viewModel.CashSessionText);
    }

    [Fact]
    public async Task Caisse_fermee_sans_le_droit_l_ecran_dit_qu_un_employe_habilite_doit_l_ouvrir()
    {
        server.CashSession = null;
        Without(Permissions.RegisterOpen);
        var viewModel = await OpenAsync();

        Assert.Equal(SalesScreen.CashClosed, viewModel.Screen);
        Assert.False(viewModel.CanOpenRegister);
        Assert.True(viewModel.CannotOpenRegister);
        Assert.Equal(
            "Un employé habilité à ouvrir la caisse doit l'ouvrir avant toute vente.", SalesViewModel.CashClosedWithoutRight);

        // Aucune ouverture ne part de ce poste, même en forçant la saisie.
        viewModel.OpeningFloatText = "5000";
        viewModel.RequestCashOpening();
        await viewModel.ConfirmCashOpeningAsync();
        Assert.False(viewModel.IsConfirmingOpening);
        Assert.Empty(server.OpenSessionRequests);

        // Un employé habilité l'ouvre : « Vérifier de nouveau » mène à la vente.
        server.WithOpenCashSession();
        await viewModel.RefreshCashSessionAsync();
        Assert.Equal(SalesScreen.Cart, viewModel.Screen);
    }

    [Fact]
    public async Task L_ouverture_de_caisse_retentee_apres_une_panne_garde_le_meme_identifiant()
    {
        server.CashSession = null;
        var viewModel = await OpenAsync();
        viewModel.OpeningFloatText = "5000";
        viewModel.RequestCashOpening();
        server.FailNextSessionOpenings = 1;

        await viewModel.ConfirmCashOpeningAsync();

        Assert.Equal(SalesScreen.CashClosed, viewModel.Screen);
        Assert.True(viewModel.IsConfirmingOpening);
        Assert.Equal(ModuleViewModel.ServerUnreachableMessage, viewModel.ErrorMessage);

        await viewModel.ConfirmCashOpeningAsync();

        Assert.Equal(2, server.OpenSessionRequests.Count);
        Assert.Equal(server.OpenSessionRequests[0], server.OpenSessionRequests[1]);
        Assert.Equal(SalesScreen.Cart, viewModel.Screen);
    }

    [Fact]
    public async Task Si_la_caisse_vient_d_etre_ouverte_par_un_autre_le_refus_du_serveur_est_montre_et_la_vente_commence()
    {
        server.CashSession = null;
        var viewModel = await OpenAsync();
        viewModel.OpeningFloatText = "5000";
        viewModel.RequestCashOpening();
        server.WithOpenCashSession(8000m);

        await viewModel.ConfirmCashOpeningAsync();

        Assert.Equal(SalesScreen.Cart, viewModel.Screen);
        Assert.Equal("La caisse de ce poste est déjà ouverte.", viewModel.InfoMessage);
        Assert.Equal(8000m, viewModel.CashSession!.OpeningFloat);
    }

    [Fact]
    public async Task Serveur_injoignable_a_l_ouverture_du_module_l_etat_de_la_caisse_reste_inconnu()
    {
        server.IsDown = true;
        var viewModel = await OpenAsync();

        Assert.Equal(SalesScreen.Loading, viewModel.Screen);
        Assert.Equal(ModuleViewModel.ServerUnreachableMessage, viewModel.ErrorMessage);

        server.IsDown = false;
        await viewModel.RefreshCashSessionAsync();
        Assert.Equal(SalesScreen.Cart, viewModel.Screen);
        Assert.Equal(string.Empty, viewModel.ErrorMessage);
    }

    // ---------- Droits ----------

    [Fact]
    public async Task Avec_tous_les_droits_les_trois_issues_sont_proposees()
    {
        var viewModel = await OpenAsync();

        Assert.True(viewModel.CanPayCash);
        Assert.True(viewModel.CanPayCard);
        Assert.True(viewModel.CanSellUnpaid);
        Assert.True(viewModel.CanViewHistory);
    }

    [Fact]
    public async Task Vendre_sans_encaisser_ne_propose_que_l_impaye()
    {
        Without(Permissions.PaymentsCollect);
        var viewModel = await OpenWithCremeAndSerumAsync();

        Assert.False(viewModel.CanPayCash);
        Assert.False(viewModel.CanPayCard);
        Assert.True(viewModel.CanSellUnpaid);

        // Les touches Espèces et TPE n'ouvrent rien.
        await viewModel.BeginCashAsync();
        await viewModel.BeginCardAsync();
        Assert.Equal(CheckoutStep.None, viewModel.Step);

        // L'impayé reste possible et le serveur l'accepte.
        await viewModel.BeginUnpaidAsync();
        viewModel.CustomerQuery = "haddad";
        await viewModel.ChooseCustomerAsync();
        await viewModel.ConfirmSaleAsync();
        Assert.Equal(SalePaymentModes.Unpaid, Assert.Single(server.Tickets).PaymentMode);
    }

    [Fact]
    public async Task Sans_le_droit_de_vendre_mais_avec_l_historique_le_module_ouvre_l_historique()
    {
        server.EmployeePermissions = [Permissions.SalesHistoryView];
        var viewModel = await OpenAsync();

        Assert.Equal(SalesScreen.History, viewModel.Screen);
        Assert.False(viewModel.CanPayCash);
        Assert.False(viewModel.CanSellUnpaid);

        // Pas de retour vers un écran de vente qu'il n'a pas le droit d'utiliser.
        await viewModel.CloseHistoryAsync();
        Assert.Equal(SalesScreen.History, viewModel.Screen);
    }

    [Fact]
    public async Task Sans_vendre_ni_historique_le_module_l_explique()
    {
        server.EmployeePermissions = [Permissions.DiscountsApply];
        var viewModel = await OpenAsync();

        Assert.Equal(SalesScreen.NoRight, viewModel.Screen);
        Assert.False(viewModel.CanDiscount);
    }

    [Fact]
    public async Task L_historique_ne_s_ouvre_qu_avec_son_droit_et_le_panier_est_retrouve_au_retour()
    {
        var viewModel = await OpenWithCremeAndSerumAsync();

        await viewModel.OpenHistoryAsync();
        Assert.Equal(SalesScreen.History, viewModel.Screen);
        await viewModel.CloseHistoryAsync();

        Assert.Equal(SalesScreen.Cart, viewModel.Screen);
        Assert.Equal(2, viewModel.Lines.Count);

        Without(Permissions.SalesHistoryView);
        var withoutRight = await OpenAsync();
        Assert.False(withoutRight.CanViewHistory);
        await withoutRight.OpenHistoryAsync();
        Assert.Equal(SalesScreen.Cart, withoutRight.Screen);
    }

    // ---------- Touches ----------

    [Fact]
    public async Task La_touche_Especes_est_l_etoile()
    {
        var viewModel = await OpenAsync();

        Assert.Equal("*", SalesShortcuts.CashSymbol);
        Assert.Equal("*", viewModel.CashKeyLabel);
    }

    [Fact]
    public async Task La_touche_TPE_vient_de_la_constante_provisoire()
    {
        var viewModel = await OpenAsync();

        Assert.Equal(SalesShortcuts.ProvisionalCardKey.ToString(), viewModel.CardKeyLabel);
        Assert.Equal(SalesShortcuts.ProvisionalCardKey.ToString(), SalesShortcuts.CardLabel);

        // Valeur provisoire de la décision D7 : à remplacer ici quand la direction aura choisi.
        Assert.Equal(Avalonia.Input.Key.F8, SalesShortcuts.ProvisionalCardKey);
    }

    [Fact]
    public void L_aide_a_l_ecran_previent_que_le_verrouillage_perd_le_panier()
    {
        Assert.Equal("F12 (verrouillage) : le panier en cours est perdu.", SalesViewModel.LockWarning);
    }

    // ---------- Saisies ----------

    [Theory]
    [InlineData("1250,50", 1250.50)]
    [InlineData("1250.50", 1250.50)]
    [InlineData("1 250,5", 1250.50)]
    [InlineData("0", 0)]
    [InlineData(" 700 DA ", 700)]
    public void Un_montant_se_saisit_avec_virgule_ou_point(string typed, double expected)
    {
        Assert.True(SaleInput.TryParseAmount(typed, out var amount));
        Assert.Equal((decimal)expected, amount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData("1,234")]
    [InlineData("1.250,50")]
    [InlineData("12e3")]
    [InlineData("99999999999")]
    public void Un_montant_invalide_est_refuse(string typed)
    {
        Assert.False(SaleInput.TryParseAmount(typed, out _));
    }

    [Theory]
    [InlineData("", null, 0)]
    [InlineData("0", null, 0)]
    [InlineData("500", DiscountKinds.Amount, 500)]
    [InlineData("500,50", DiscountKinds.Amount, 500.50)]
    [InlineData("10%", DiscountKinds.Percent, 10)]
    [InlineData("7.5 %", DiscountKinds.Percent, 7.5)]
    [InlineData("100 %", DiscountKinds.Percent, 100)]
    public void Une_remise_se_saisit_en_montant_ou_en_pourcentage(string typed, string? expectedKind, double expectedValue)
    {
        Assert.True(SaleInput.TryParseDiscount(typed, out var kind, out var value));
        Assert.Equal(expectedKind, kind);
        Assert.Equal((decimal)expectedValue, value);
    }
}
