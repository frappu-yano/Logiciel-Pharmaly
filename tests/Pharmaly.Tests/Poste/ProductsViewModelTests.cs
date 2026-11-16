using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Modules;
using Pharmaly.Poste.Modules.Products;

namespace Pharmaly.Tests.Poste;

public sealed class ProductsViewModelTests
{
    private const string UnknownBarcode = "2999999999993";

    private readonly FakeServer server = new FakeServer { HasPosteSession = true, EmployeeIdentified = true }.AsAdministrator();

    private ProductsViewModel CreateModule() =>
        new(server, server, code => server.EmployeePermissions.Contains(code), TimeSpan.Zero);

    /// <summary>Ouvre la fiche comme le fait un lecteur : le code puis Entrée.</summary>
    private async Task<ProductsViewModel> OpenProductAsync(ProductSummary product)
    {
        var module = CreateModule();
        module.Lookup.Query = product.Barcode!;
        await module.OpenFromSearchAsync();
        Assert.Equal(product.Id, module.Detail!.Id);
        return module;
    }

    private static TForm Form<TForm>(ProductsViewModel module)
        where TForm : ProductFormViewModel => Assert.IsType<TForm>(module.ActiveForm);

    // ---------- Recherche et fiche ----------

    [Fact]
    public void La_recherche_liste_designation_code_barres_prix_et_stock_vendable()
    {
        var module = CreateModule();

        module.Lookup.Query = "crème";

        var row = Assert.Single(module.Lookup.Results);
        Assert.Equal("Crème hydratante 50 ml", row.Name);
        Assert.Equal(FakeServer.Creme.Barcode, row.BarcodeText);
        Assert.Equal("1 850,10 DA", row.PriceText);
        Assert.Equal("12", row.StockText);
        Assert.Same(row, module.Lookup.SelectedResult);
        Assert.Null(module.Detail);
    }

    [Fact]
    public async Task Scanner_un_code_barres_puis_Entree_ouvre_la_fiche_complete()
    {
        var module = await OpenProductAsync(FakeServer.Creme);

        var detail = module.Detail!;
        Assert.Equal("Crème hydratante 50 ml", detail.Name);
        Assert.Equal("Code interne 100001", detail.CodeText);
        Assert.Equal("Dermalia", detail.BrandText);
        Assert.Equal("Soin visage", detail.CategoryText);
        Assert.Equal("Oui", detail.UnitText);
        Assert.Equal("Actif", detail.StateText);
        Assert.Equal("1 850,10 DA", detail.PriceText);
        Assert.Equal(FakeServer.Creme.Barcode, detail.BarcodesText);

        // Stock par emplacement.
        Assert.Equal(["Stock principal", "Produits mis à l'écart"], detail.Stock.Select(line => line.LocationName));
        Assert.Equal(["Vendable", "Mis à l'écart"], detail.Stock.Select(line => line.KindText));
        Assert.Equal(["12", "0"], detail.Stock.Select(line => line.QuantityText));

        // Derniers mouvements : type en français, quantité signée, solde, employé.
        var movement = Assert.Single(detail.Movements);
        Assert.Equal("Entrée par bon", movement.TypeText);
        Assert.Equal("+12", movement.QuantityText);
        Assert.Equal("12", movement.BalanceText);
        Assert.Equal(FakeServer.KarimName, movement.EmployeeName);
        Assert.Matches(@"^\d{2}/\d{2}/\d{4} \d{2}:\d{2}$", movement.DateText);

        // Historique des prix de vente.
        var price = Assert.Single(detail.Prices);
        Assert.Equal("Prix initial", price.OldPriceText);
        Assert.Equal("1 850,10 DA", price.NewPriceText);
    }

    [Fact]
    public async Task Entree_ouvre_le_resultat_selectionne_quand_la_recherche_en_propose_plusieurs()
    {
        var module = CreateModule();
        module.Lookup.Query = "ml";
        Assert.Equal(2, module.Lookup.Results.Count);

        module.Lookup.MoveSelection(+1);
        await module.OpenFromSearchAsync();

        Assert.Equal(FakeServer.Serum.Id, module.Detail!.Id);
    }

    [Fact]
    public async Task Un_code_inconnu_est_signale_et_pre_remplit_la_creation()
    {
        var module = CreateModule();
        module.Lookup.Query = UnknownBarcode;

        await module.OpenFromSearchAsync();

        Assert.Null(module.Detail);
        Assert.Equal($"Aucun produit trouvé pour « {UnknownBarcode} ».", module.Lookup.Notice);

        module.StartCreate();
        Assert.Equal([UnknownBarcode], Form<ProductCreateViewModel>(module).Barcodes.Items);
    }

    [Fact]
    public async Task Le_refus_de_consulter_est_affiche_tel_que_le_serveur_le_formule()
    {
        server.DenyProducts = true;
        var module = CreateModule();
        module.Lookup.Query = FakeServer.Creme.Barcode!;

        await module.OpenFromSearchAsync();

        Assert.False(module.CanView);
        Assert.Equal("Vous n'avez pas le droit d'effectuer cette action.", module.Lookup.Notice);
        Assert.Null(module.Detail);
    }

    [Fact]
    public async Task Une_session_perdue_pendant_la_recherche_redemande_l_identification()
    {
        var module = CreateModule();
        var required = 0;
        module.IdentificationRequired += (_, _) => required++;
        server.EmployeeIdentified = false;
        module.Lookup.Query = FakeServer.Creme.Barcode!;

        await module.OpenFromSearchAsync();

        Assert.True(required > 0);
    }

    // ---------- Droits ----------

    [Fact]
    public async Task Sans_les_droits_d_ecriture_aucune_action_n_est_proposee_ni_executee()
    {
        server.EmployeePermissions = [Permissions.ProductsView];
        var module = await OpenProductAsync(FakeServer.Creme);

        Assert.True(module.CanView);
        Assert.False(module.CanCreate || module.CanEdit || module.CanChangePrice || module.CanAdjust || module.CanTransfer);
        Assert.False(module.HasDetailActions);
        Assert.False(module.StartCreateCommand.CanExecute(null));
        Assert.False(module.StartEditCommand.CanExecute(null));
        Assert.False(module.StartPriceChangeCommand.CanExecute(null));
        Assert.False(module.StartAdjustmentCommand.CanExecute(null));
        Assert.False(module.StartTransferCommand.CanExecute(null));

        // Les raccourcis clavier appellent ces méthodes : elles ne font rien non plus.
        module.StartCreate();
        module.StartEdit();
        module.StartPriceChange();
        module.StartAdjustment();
        await module.StartTransferAsync();
        Assert.Null(module.ActiveForm);
    }

    [Theory]
    [InlineData(Permissions.ProductsCreate, true, false, false, false, false)]
    [InlineData(Permissions.ProductsEdit, false, true, false, false, false)]
    [InlineData(Permissions.SalePriceEdit, false, false, true, false, false)]
    [InlineData(Permissions.StockAdjust, false, false, false, true, false)]
    [InlineData(Permissions.StockTransfer, false, false, false, false, true)]
    public void Chaque_droit_ne_montre_que_son_action(string permission, bool create, bool edit, bool price, bool adjust, bool transfer)
    {
        server.EmployeePermissions = [Permissions.ProductsView, permission];

        var module = CreateModule();

        Assert.Equal(
            (create, edit, price, adjust, transfer),
            (module.CanCreate, module.CanEdit, module.CanChangePrice, module.CanAdjust, module.CanTransfer));
    }

    [Fact]
    public void Les_actions_sur_la_fiche_attendent_qu_un_produit_soit_affiche()
    {
        var module = CreateModule();

        Assert.False(module.StartEditCommand.CanExecute(null));
        Assert.False(module.StartPriceChangeCommand.CanExecute(null));
        Assert.False(module.StartAdjustmentCommand.CanExecute(null));
        Assert.True(module.StartCreateCommand.CanExecute(null));
    }

    // ---------- Création ----------

    private ProductCreateViewModel StartCreation(ProductsViewModel module)
    {
        module.StartCreate();
        var form = Form<ProductCreateViewModel>(module);
        form.Name = " Baume lèvres 15 ml ";
        form.Barcodes.Entry = "2000000000039";
        form.Barcodes.AddEntry();
        form.Brand = "Dermalia";
        form.Category = " Soin lèvres ";
        form.SalePrice = "650,50";
        return form;
    }

    [Fact]
    public async Task Creer_un_produit_envoie_la_saisie_puis_ouvre_sa_fiche()
    {
        var module = CreateModule();
        var form = StartCreation(module);
        form.Barcodes.Entry = "BAUME-15";

        await form.SubmitAsync();

        var request = Assert.Single(server.ProductCreations);
        Assert.NotEqual(Guid.Empty, request.OperationId);
        Assert.Equal("Baume lèvres 15 ml", request.Name);
        Assert.Equal(["2000000000039", "BAUME-15"], request.Barcodes);
        Assert.Equal("Dermalia", request.Brand);
        Assert.Equal("Soin lèvres", request.Category);
        Assert.Equal(650.50m, request.SalePrice);
        Assert.True(request.SoldByUnit);

        Assert.Null(module.ActiveForm);
        Assert.Equal("Baume lèvres 15 ml", module.Detail!.Name);
        Assert.Equal("650,50 DA", module.Detail.PriceText);
        Assert.Equal("« Baume lèvres 15 ml » est créé.", module.InfoMessage);
        Assert.Equal(string.Empty, module.ErrorMessage);
    }

    [Fact]
    public async Task Marque_et_categorie_vides_ne_sont_pas_envoyees()
    {
        var module = CreateModule();
        var form = StartCreation(module);
        form.Brand = "  ";
        form.Category = string.Empty;
        form.SoldByUnit = false;

        await form.SubmitAsync();

        var request = Assert.Single(server.ProductCreations);
        Assert.Null(request.Brand);
        Assert.Null(request.Category);
        Assert.False(request.SoldByUnit);
    }

    [Fact]
    public async Task Un_code_barres_deja_utilise_est_refuse_par_le_serveur_et_la_saisie_est_conservee()
    {
        var module = CreateModule();
        var form = StartCreation(module);
        form.Barcodes.Entry = FakeServer.Creme.Barcode!;

        await form.SubmitAsync();

        Assert.Equal(
            $"Le code-barres {FakeServer.Creme.Barcode} est déjà attribué à « Crème hydratante 50 ml ».", form.ErrorMessage);
        Assert.Same(form, module.ActiveForm);
        Assert.Equal(" Baume lèvres 15 ml ", form.Name);
        Assert.Equal(["2000000000039", FakeServer.Creme.Barcode!], form.Barcodes.Items);
        Assert.Null(module.Detail);

        // L'employé retire le code fautif et valide de nouveau.
        form.Barcodes.Remove(FakeServer.Creme.Barcode!);
        await form.SubmitAsync();

        Assert.Null(module.ActiveForm);
        Assert.Equal("Baume lèvres 15 ml", module.Detail!.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12,345")]
    [InlineData("1.250,50")]
    [InlineData("-5")]
    [InlineData("12,")]
    [InlineData("12 DA")]
    public async Task Un_prix_invalide_n_est_pas_envoye(string price)
    {
        var module = CreateModule();
        var form = StartCreation(module);
        form.SalePrice = price;

        await form.SubmitAsync();

        Assert.Empty(server.ProductCreations);
        Assert.Equal(DecimalInput.InvalidAmountMessage, form.ErrorMessage);
        Assert.Same(form, module.ActiveForm);
    }

    [Theory]
    [InlineData("650,5", 650.5)]
    [InlineData("650.5", 650.5)]
    [InlineData(" 1 250,50 ", 1250.5)]
    [InlineData("0", 0)]
    public async Task Le_prix_accepte_la_virgule_ou_le_point(string price, double expected)
    {
        var module = CreateModule();
        var form = StartCreation(module);
        form.SalePrice = price;

        await form.SubmitAsync();

        Assert.Equal((decimal)expected, Assert.Single(server.ProductCreations).SalePrice);
    }

    [Fact]
    public async Task Echap_abandonne_la_creation_sans_rien_envoyer()
    {
        var module = CreateModule();
        var form = StartCreation(module);

        form.Cancel();

        Assert.Null(module.ActiveForm);
        Assert.Empty(server.ProductCreations);
    }

    [Fact]
    public async Task Le_formulaire_de_creation_s_utilise_seul_avec_un_code_pre_rempli_et_un_rappel()
    {
        // Comme le fera l'écran des bons fournisseurs quand un code inconnu est scanné.
        ProductSummary? created = null;
        var cancelled = false;
        var form = new ProductCreateViewModel(
            server,
            product =>
            {
                created = product;
                return Task.CompletedTask;
            },
            () => cancelled = true,
            UnknownBarcode);
        Assert.Equal([UnknownBarcode], form.Barcodes.Items);
        form.Name = "Lait corps 200 ml";
        form.SalePrice = "990";

        await form.SubmitAsync();

        Assert.Equal("Lait corps 200 ml", created!.Name);
        Assert.Equal(UnknownBarcode, created.Barcode);
        Assert.False(cancelled);
        Assert.Equal(UnknownBarcode, (await server.FindByBarcodeAsync(UnknownBarcode, CancellationToken.None))!.Barcode);
    }

    // ---------- Identifiant d'opération ----------

    [Fact]
    public async Task Apres_une_reponse_perdue_la_nouvelle_tentative_reutilise_le_meme_identifiant_d_operation()
    {
        var module = CreateModule();
        var form = StartCreation(module);
        server.LoseNextResponse = true;

        await form.SubmitAsync();

        // Le serveur a créé le produit, mais le poste n'a pas reçu la réponse.
        Assert.Equal(ModuleViewModel.ServerUnreachableMessage, form.ErrorMessage);
        Assert.Same(form, module.ActiveForm);
        Assert.Equal("650,50", form.SalePrice);

        await form.SubmitAsync();

        Assert.Equal(2, server.ProductCreations.Count);
        Assert.Equal(server.ProductCreations[0].OperationId, server.ProductCreations[1].OperationId);
        Assert.Null(module.ActiveForm);
        Assert.Equal("Baume lèvres 15 ml", module.Detail!.Name);

        // Un seul produit existe : le serveur a reconnu l'opération et n'a rien refait.
        module.Lookup.Query = "baume";
        Assert.Single(module.Lookup.Results);
    }

    [Fact]
    public async Task Apres_une_panne_de_reseau_le_meme_identifiant_repart_et_une_nouvelle_intention_en_recoit_un_autre()
    {
        var module = CreateModule();
        var form = StartCreation(module);
        server.IsDown = true;
        await form.SubmitAsync();
        await form.SubmitAsync();
        Assert.Equal(ModuleViewModel.ServerUnreachableMessage, form.ErrorMessage);

        server.IsDown = false;
        await form.SubmitAsync();

        Assert.Equal(3, server.ProductCreations.Count);
        Assert.Single(server.ProductCreations.Select(request => request.OperationId).Distinct());
        Assert.Null(module.ActiveForm);

        // Nouvelle création : nouvelle intention, nouvel identifiant.
        module.StartCreate();
        var next = Form<ProductCreateViewModel>(module);
        next.Name = "Lait corps 200 ml";
        next.Barcodes.Entry = "2000000000046";
        next.SalePrice = "990";
        await next.SubmitAsync();

        Assert.NotEqual(server.ProductCreations[0].OperationId, server.ProductCreations[3].OperationId);
    }

    [Fact]
    public async Task Un_refus_du_serveur_ne_change_pas_l_identifiant_et_un_succes_le_renouvelle()
    {
        var module = await OpenProductAsync(FakeServer.Creme);
        module.StartAdjustment();
        var form = Form<StockAdjustmentViewModel>(module);
        form.IsCount = false;
        form.Quantity = "-50";
        form.Reason = "Casse";
        await form.SubmitAsync();
        await form.SubmitAsync();
        Assert.Contains("Stock insuffisant", form.ErrorMessage);

        form.Quantity = "-2";
        await form.SubmitAsync();
        await form.SubmitAsync();

        Assert.Equal(2, server.Adjustments.Count);
        Assert.Equal(10, server.StockOf(FakeServer.Creme.Id, FakeServer.MainLocation.Id));
    }

    [Fact]
    public async Task Modifier_la_saisie_apres_une_reponse_perdue_ne_cree_pas_de_doublon_et_le_dit()
    {
        var module = CreateModule();
        var form = StartCreation(module);
        server.LoseNextResponse = true;
        await form.SubmitAsync();

        // Le produit existe déjà côté serveur ; l'employé change le prix et renvoie.
        form.SalePrice = "700";
        await form.SubmitAsync();

        Assert.StartsWith("Cet identifiant d'opération a déjà servi pour une autre demande.", form.ErrorMessage);
        Assert.EndsWith(ProductFormViewModel.PreviousAttemptRecordedMessage, form.ErrorMessage);
        module.Lookup.Query = "baume";
        Assert.Equal("650,50 DA", Assert.Single(module.Lookup.Results).PriceText);
    }

    [Fact]
    public async Task Un_double_declenchement_pendant_l_envoi_n_envoie_qu_une_demande()
    {
        var module = CreateModule();
        var form = StartCreation(module);
        server.HoldWrites = new TaskCompletionSource();

        var first = form.SubmitAsync();
        Assert.True(form.IsBusy);
        await form.SubmitAsync();
        form.Cancel();
        Assert.Same(form, module.ActiveForm);

        server.HoldWrites.SetResult();
        await first;

        Assert.Single(server.ProductCreations);
        Assert.False(form.IsBusy);
        Assert.Null(module.ActiveForm);
    }

    [Fact]
    public async Task Une_session_perdue_pendant_une_ecriture_redemande_l_identification()
    {
        var module = CreateModule();
        var required = 0;
        module.IdentificationRequired += (_, _) => required++;
        var form = StartCreation(module);
        server.EmployeeIdentified = false;

        await form.SubmitAsync();

        Assert.Equal(1, required);
        Assert.Equal("Identifiez-vous avec votre matricule et votre code.", form.ErrorMessage);
    }

    // ---------- Modification ----------

    [Fact]
    public async Task Modifier_un_produit_envoie_la_fiche_et_la_difference_des_codes_barres()
    {
        var module = await OpenProductAsync(FakeServer.Creme);
        module.StartEdit();
        var form = Form<ProductEditViewModel>(module);
        Assert.Equal("Crème hydratante 50 ml", form.Name);
        Assert.Equal("Dermalia", form.Brand);
        Assert.True(form.IsActive);
        Assert.Equal([FakeServer.Creme.Barcode!], form.Barcodes.Items);

        form.Name = "Crème hydratante riche 50 ml";
        form.Brand = string.Empty;
        form.Category = "Hydratation";
        form.Barcodes.Entry = "2000000000053";
        form.Barcodes.AddEntry();
        form.Barcodes.Remove(FakeServer.Creme.Barcode!);
        await form.SubmitAsync();

        var (productId, request) = Assert.Single(server.ProductUpdates);
        Assert.Equal(FakeServer.Creme.Id, productId);
        Assert.Equal("Crème hydratante riche 50 ml", request.Name);
        Assert.Null(request.Brand);
        Assert.Equal("Hydratation", request.Category);
        Assert.True(request.IsActive);
        Assert.Equal(["2000000000053"], request.BarcodesToAdd);
        Assert.Equal([FakeServer.Creme.Barcode!], request.BarcodesToRemove);

        Assert.Null(module.ActiveForm);
        Assert.Equal("Crème hydratante riche 50 ml", module.Detail!.Name);
        Assert.Equal("2000000000053", module.Detail.BarcodesText);
        Assert.Equal("« Crème hydratante riche 50 ml » est enregistré.", module.InfoMessage);
        // La liste des résultats derrière la fiche est relue : le produit n'y est plus sous son ancien code.
        Assert.Empty(module.Lookup.Results);
    }

    [Fact]
    public async Task Desactiver_un_produit_laisse_sa_fiche_affichee_avec_l_etat_inactif()
    {
        var module = await OpenProductAsync(FakeServer.Serum);
        module.StartEdit();
        var form = Form<ProductEditViewModel>(module);
        form.IsActive = false;

        await form.SubmitAsync();

        Assert.False(Assert.Single(server.ProductUpdates).Request.IsActive);
        Assert.Equal("Inactif", module.Detail!.StateText);
        Assert.Empty(Assert.Single(server.ProductUpdates).Request.BarcodesToAdd!);
        Assert.Empty(Assert.Single(server.ProductUpdates).Request.BarcodesToRemove!);
    }

    [Fact]
    public async Task Retirer_le_dernier_code_barres_est_refuse_par_le_serveur()
    {
        var module = await OpenProductAsync(FakeServer.Creme);
        module.StartEdit();
        var form = Form<ProductEditViewModel>(module);
        form.Barcodes.RemoveLast();

        await form.SubmitAsync();

        Assert.Equal("Un produit garde au moins un code-barres.", form.ErrorMessage);
        Assert.Same(form, module.ActiveForm);
    }

    // ---------- Prix de vente ----------

    [Fact]
    public async Task Le_changement_de_prix_demande_confirmation_en_montrant_ancien_et_nouveau_prix()
    {
        var module = await OpenProductAsync(FakeServer.Creme);
        module.StartPriceChange();
        var form = Form<SalePriceViewModel>(module);
        Assert.Equal("1 850,10 DA", form.CurrentPriceText);

        form.NewPrice = "1990.5";
        await form.SubmitAsync();

        // Rien n'est parti : la confirmation montre les deux prix.
        Assert.True(form.IsConfirming);
        Assert.Equal("1 990,50 DA", form.NewPriceText);
        Assert.Empty(server.PriceChanges);

        // Échap revient à la saisie, toujours sans rien envoyer.
        form.Cancel();
        Assert.False(form.IsConfirming);
        Assert.Same(form, module.ActiveForm);
        Assert.Empty(server.PriceChanges);

        await form.SubmitAsync();
        await form.SubmitAsync();

        var (productId, request) = Assert.Single(server.PriceChanges);
        Assert.Equal(FakeServer.Creme.Id, productId);
        Assert.Equal(1990.5m, request.NewPrice);
        Assert.Null(module.ActiveForm);
        Assert.Equal("1 990,50 DA", module.Detail!.PriceText);
        Assert.Equal("Prix de vente de « Crème hydratante 50 ml » : 1 850,10 DA → 1 990,50 DA.", module.InfoMessage);
        Assert.Equal(["1 850,10 DA", "Prix initial"], module.Detail.Prices.Select(line => line.OldPriceText));
        Assert.Equal(FakeServer.EmployeeName, module.Detail.Prices[0].EmployeeName);
        Assert.Equal("1 990,50 DA", Assert.Single(module.Lookup.Results).PriceText);
    }

    [Theory]
    [InlineData("abc", DecimalInput.InvalidAmountMessage)]
    [InlineData("19,999", DecimalInput.InvalidAmountMessage)]
    [InlineData("1850,10", "Le nouveau prix est identique au prix actuel.")]
    public async Task Un_nouveau_prix_invalide_ou_identique_n_ouvre_pas_la_confirmation(string price, string message)
    {
        var module = await OpenProductAsync(FakeServer.Creme);
        module.StartPriceChange();
        var form = Form<SalePriceViewModel>(module);
        form.NewPrice = price;

        await form.SubmitAsync();

        Assert.False(form.IsConfirming);
        Assert.Equal(message, form.ErrorMessage);
        Assert.Empty(server.PriceChanges);
    }

    [Fact]
    public async Task Une_panne_pendant_la_confirmation_du_prix_garde_la_confirmation_et_le_meme_identifiant()
    {
        var module = await OpenProductAsync(FakeServer.Creme);
        module.StartPriceChange();
        var form = Form<SalePriceViewModel>(module);
        form.NewPrice = "2000";
        await form.SubmitAsync();
        server.LoseNextResponse = true;

        await form.SubmitAsync();

        Assert.True(form.IsConfirming);
        Assert.Equal(ModuleViewModel.ServerUnreachableMessage, form.ErrorMessage);

        await form.SubmitAsync();

        Assert.Equal(2, server.PriceChanges.Count);
        Assert.Equal(server.PriceChanges[0].Request.OperationId, server.PriceChanges[1].Request.OperationId);
        // Le prix n'a changé qu'une fois : une seule ligne de plus dans l'historique.
        Assert.Equal(2, module.Detail!.Prices.Count);
        Assert.Equal("2 000,00 DA", module.Detail.PriceText);
    }

    // ---------- Ajustement d'inventaire ----------

    [Fact]
    public async Task L_ajustement_par_quantite_comptee_confirme_avant_et_apres_puis_met_la_fiche_a_jour()
    {
        var module = await OpenProductAsync(FakeServer.Creme);
        module.StartAdjustment();
        var form = Form<StockAdjustmentViewModel>(module);
        Assert.Equal(
            ["Pharmaly 1 — Didouche — Stock principal", "Pharmaly 1 — Didouche — Produits mis à l'écart"],
            form.Locations.Select(location => location.Label));
        Assert.Equal(FakeServer.MainLocation.Id, form.Location!.Id);
        Assert.Equal("12", form.CurrentStockText);
        Assert.True(form.IsCount);

        form.Quantity = "10";
        form.Reason = " Inventaire du rayon ";
        await form.SubmitAsync();

        Assert.True(form.IsConfirming);
        Assert.Equal(("12", "10", "−2"), (form.BeforeText, form.AfterText, form.ChangeText));
        Assert.Empty(server.Adjustments);

        await form.SubmitAsync();

        var request = Assert.Single(server.Adjustments);
        Assert.Equal(FakeServer.Creme.Id, request.ProductId);
        Assert.Equal(FakeServer.MainLocation.Id, request.LocationId);
        Assert.Equal(10, request.NewQuantity);
        Assert.Null(request.QuantityChange);
        Assert.Equal("Inventaire du rayon", request.Reason);

        Assert.Null(module.ActiveForm);
        Assert.Equal(
            "Stock de « Crème hydratante 50 ml » à « Pharmaly 1 — Didouche — Stock principal » : 12 → 10 (−2).", module.InfoMessage);
        Assert.Equal("10", module.Detail!.Stock[0].QuantityText);
        var movement = module.Detail.Movements[0];
        Assert.Equal("Ajustement d'inventaire", movement.TypeText);
        Assert.Equal("−2", movement.QuantityText);
        Assert.Equal("10", movement.BalanceText);
        Assert.Equal(FakeServer.EmployeeName, movement.EmployeeName);
        Assert.Contains("Inventaire du rayon", movement.ContextText);
        Assert.Equal("10", Assert.Single(module.Lookup.Results).StockText);
    }

    [Fact]
    public async Task L_ajustement_par_correction_envoie_l_ecart_a_l_emplacement_choisi()
    {
        var module = await OpenProductAsync(FakeServer.Creme);
        module.StartAdjustment();
        var form = Form<StockAdjustmentViewModel>(module);
        form.Location = form.Locations[1];
        Assert.Equal("0", form.CurrentStockText);
        form.IsCorrection = true;
        Assert.Equal("Correction (+ ou -)", form.QuantityLabel);
        form.Quantity = "+3";
        form.Reason = "Emballages abîmés";

        await form.SubmitAsync();
        Assert.Equal(("0", "3", "+3"), (form.BeforeText, form.AfterText, form.ChangeText));
        await form.SubmitAsync();

        var request = Assert.Single(server.Adjustments);
        Assert.Equal(FakeServer.SetAsideLocation.Id, request.LocationId);
        Assert.Null(request.NewQuantity);
        Assert.Equal(3, request.QuantityChange);
        Assert.Equal(3, server.StockOf(FakeServer.Creme.Id, FakeServer.SetAsideLocation.Id));
    }

    [Theory]
    [InlineData(true, "", "Inventaire", "Quantité invalide")]
    [InlineData(true, "dix", "Inventaire", "Quantité invalide")]
    [InlineData(true, "-3", "Inventaire", "Quantité invalide")]
    [InlineData(true, "1,5", "Inventaire", "se vend à l'unité")]
    [InlineData(false, "0,5", "Inventaire", "se vend à l'unité")]
    [InlineData(false, "0", "Inventaire", "correction de zéro")]
    [InlineData(true, "10", "   ", "Le motif de l'ajustement est obligatoire.")]
    public async Task Une_saisie_d_ajustement_invalide_est_refusee_sans_rien_envoyer(
        bool isCount, string quantity, string reason, string expectedMessage)
    {
        var module = await OpenProductAsync(FakeServer.Creme);
        module.StartAdjustment();
        var form = Form<StockAdjustmentViewModel>(module);
        form.IsCount = isCount;
        form.Quantity = quantity;
        form.Reason = reason;

        await form.SubmitAsync();

        Assert.False(form.IsConfirming);
        Assert.Contains(expectedMessage, form.ErrorMessage);
        Assert.Empty(server.Adjustments);
    }

    [Theory]
    [InlineData("1,5")]
    [InlineData("1.5")]
    public async Task Un_produit_qui_ne_se_vend_pas_a_l_unite_accepte_une_quantite_fractionnaire(string quantity)
    {
        var powder = server.AddProduct("Argile verte au poids", "2000000000060", 320, soldByUnit: false, mainStock: 4);
        var module = await OpenProductAsync(powder);
        module.StartAdjustment();
        var form = Form<StockAdjustmentViewModel>(module);
        form.Quantity = quantity;
        form.Reason = "Pesée";

        await form.SubmitAsync();
        Assert.Equal(("4", "1,5", "−2,5"), (form.BeforeText, form.AfterText, form.ChangeText));
        await form.SubmitAsync();

        Assert.Equal(1.5m, Assert.Single(server.Adjustments).NewQuantity);
        Assert.Equal("1,5", module.Detail!.Stock[0].QuantityText);
    }

    [Fact]
    public async Task Une_reponse_perdue_pendant_l_ajustement_ne_l_applique_qu_une_fois()
    {
        var module = await OpenProductAsync(FakeServer.Creme);
        module.StartAdjustment();
        var form = Form<StockAdjustmentViewModel>(module);
        form.IsCorrection = true;
        form.Quantity = "-2";
        form.Reason = "Casse";
        await form.SubmitAsync();
        server.LoseNextResponse = true;

        await form.SubmitAsync();
        Assert.True(form.IsConfirming);
        Assert.Equal(ModuleViewModel.ServerUnreachableMessage, form.ErrorMessage);
        await form.SubmitAsync();

        Assert.Equal(2, server.Adjustments.Count);
        Assert.Equal(server.Adjustments[0].OperationId, server.Adjustments[1].OperationId);
        Assert.Equal(10, server.StockOf(FakeServer.Creme.Id, FakeServer.MainLocation.Id));
        Assert.Contains("12 → 10 (−2)", module.InfoMessage);
    }

    // ---------- Transfert ----------

    private async Task<(ProductsViewModel Module, StockTransferViewModel Form)> StartTransferAsync()
    {
        var module = CreateModule();
        await module.StartTransferAsync();
        return (module, Form<StockTransferViewModel>(module));
    }

    private static async Task ScanAsync(StockTransferViewModel form, ProductSummary product)
    {
        form.Lookup.Query = product.Barcode!;
        Assert.True(await form.AddFromQueryAsync());
    }

    [Fact]
    public async Task Un_transfert_se_saisit_au_lecteur_se_confirme_puis_affiche_son_resultat()
    {
        var (module, form) = await StartTransferAsync();
        Assert.Equal(FakeServer.MainLocation.Id, form.From!.Id);
        Assert.Equal(FakeServer.SetAsideLocation.Id, form.To!.Id);

        await ScanAsync(form, FakeServer.Creme);
        await ScanAsync(form, FakeServer.Serum);
        await ScanAsync(form, FakeServer.Creme);

        Assert.Equal(["Crème hydratante 50 ml", "Sérum nuit 30 ml"], form.Lines.Select(line => line.Name));
        Assert.Equal(["2", "1"], form.Lines.Select(line => line.QuantityText));
        Assert.Equal(["12", "3"], form.Lines.Select(line => line.AvailableText));
        Assert.Equal(string.Empty, form.Lookup.Query);
        Assert.Same(form.Lines[0], form.SelectedLine);

        // Tab puis une quantité : elle s'applique à la ligne sélectionnée.
        form.LineQuantity = "5";
        Assert.True(form.ApplyLineQuantity());
        form.Note = " Emballages abîmés ";

        await form.SubmitAsync();
        Assert.True(form.IsConfirming);
        Assert.Equal(
            "Transférer 2 produits de « Pharmaly 1 — Didouche — Stock principal » vers "
            + "« Pharmaly 1 — Didouche — Produits mis à l'écart » ?",
            form.ConfirmationText);
        Assert.Empty(server.Transfers);

        await form.SubmitAsync();

        var request = Assert.Single(server.Transfers);
        Assert.NotEqual(Guid.Empty, request.OperationId);
        Assert.Equal(FakeServer.MainLocation.Id, request.FromLocationId);
        Assert.Equal(FakeServer.SetAsideLocation.Id, request.ToLocationId);
        Assert.Equal(
            [new StockTransferLine(FakeServer.Creme.Id, 5), new StockTransferLine(FakeServer.Serum.Id, 1)], request.Lines);
        Assert.Equal("Emballages abîmés", request.Note);

        // Le formulaire reste ouvert sur le résultat, prêt pour le transfert suivant.
        Assert.Same(form, module.ActiveForm);
        Assert.False(form.IsConfirming);
        Assert.Empty(form.Lines);
        Assert.Equal(string.Empty, form.Note);
        Assert.StartsWith("Transfert enregistré de « Pharmaly 1 — Didouche — Stock principal »", form.Result!.Title);
        Assert.Equal(
            [
                new TransferResultLineViewModel("Crème hydratante 50 ml", "5", "7", "5"),
                new TransferResultLineViewModel("Sérum nuit 30 ml", "1", "2", "1"),
            ],
            form.Result.Lines);

        form.Cancel();
        Assert.Null(module.ActiveForm);
    }

    [Fact]
    public async Task Un_stock_insuffisant_affiche_le_message_du_serveur_et_conserve_toute_la_saisie()
    {
        var (module, form) = await StartTransferAsync();
        await ScanAsync(form, FakeServer.Creme);
        await ScanAsync(form, FakeServer.Serum);
        form.LineQuantity = "8";
        Assert.True(form.ApplyLineQuantity());
        form.Note = "Réassort";

        await form.SubmitAsync();
        await form.SubmitAsync();

        Assert.Equal("Stock insuffisant pour « Sérum nuit 30 ml » : 8 demandé, 3 disponible.", form.ErrorMessage);
        Assert.False(form.IsConfirming);
        Assert.Same(form, module.ActiveForm);
        Assert.Equal(["1", "8"], form.Lines.Select(line => line.QuantityText));
        Assert.Equal("Réassort", form.Note);
        Assert.Null(form.Result);
        Assert.Equal(FakeServer.CremeStock, server.StockOf(FakeServer.Creme.Id, FakeServer.MainLocation.Id));

        // L'employé corrige la quantité et valide de nouveau : rien n'est à ressaisir.
        form.LineQuantity = "3";
        Assert.True(form.ApplyLineQuantity());
        await form.SubmitAsync();
        await form.SubmitAsync();

        Assert.Equal(2, form.Result!.Lines.Count);
        Assert.Equal(0, server.StockOf(FakeServer.Serum.Id, FakeServer.MainLocation.Id));
        Assert.Equal(3, server.StockOf(FakeServer.Serum.Id, FakeServer.SetAsideLocation.Id));
    }

    [Fact]
    public async Task Une_reponse_perdue_pendant_le_transfert_ne_deplace_le_stock_qu_une_fois()
    {
        var (_, form) = await StartTransferAsync();
        await ScanAsync(form, FakeServer.Creme);
        form.LineQuantity = "4";
        form.ApplyLineQuantity();
        await form.SubmitAsync();
        server.LoseNextResponse = true;

        await form.SubmitAsync();

        Assert.True(form.IsConfirming);
        Assert.Equal(ModuleViewModel.ServerUnreachableMessage, form.ErrorMessage);
        Assert.Single(form.Lines);

        await form.SubmitAsync();

        Assert.Equal(2, server.Transfers.Count);
        Assert.Equal(server.Transfers[0].OperationId, server.Transfers[1].OperationId);
        Assert.Equal(8, server.StockOf(FakeServer.Creme.Id, FakeServer.MainLocation.Id));
        Assert.Equal(4, server.StockOf(FakeServer.Creme.Id, FakeServer.SetAsideLocation.Id));
        Assert.Equal(new TransferResultLineViewModel("Crème hydratante 50 ml", "4", "8", "4"), Assert.Single(form.Result!.Lines));

        // Le transfert suivant est une nouvelle intention.
        await ScanAsync(form, FakeServer.Creme);
        await form.SubmitAsync();
        await form.SubmitAsync();
        Assert.NotEqual(server.Transfers[0].OperationId, server.Transfers[2].OperationId);
    }

    [Theory]
    [InlineData("abc", "Quantité invalide")]
    [InlineData("1,5", "se vend à l'unité")]
    [InlineData("0", "supérieure à zéro")]
    [InlineData("-2", "Quantité invalide")]
    public async Task Une_quantite_de_transfert_invalide_est_refusee_et_la_ligne_garde_sa_quantite(string quantity, string message)
    {
        var (_, form) = await StartTransferAsync();
        await ScanAsync(form, FakeServer.Creme);
        form.LineQuantity = quantity;

        Assert.False(form.ApplyLineQuantity());

        Assert.Contains(message, form.ErrorMessage);
        Assert.Equal(1, form.Lines[0].Quantity);
    }

    [Fact]
    public async Task Un_transfert_sans_ligne_ou_vers_le_meme_emplacement_n_est_pas_envoye()
    {
        var (_, form) = await StartTransferAsync();

        await form.SubmitAsync();
        Assert.Equal("Ajoutez au moins un produit à transférer.", form.ErrorMessage);

        await ScanAsync(form, FakeServer.Creme);
        form.To = form.From;
        await form.SubmitAsync();

        Assert.Equal("L'emplacement de départ et celui d'arrivée doivent être différents.", form.ErrorMessage);
        Assert.False(form.IsConfirming);
        Assert.Empty(server.Transfers);
    }

    [Fact]
    public async Task Les_lignes_d_un_transfert_se_parcourent_et_se_retirent()
    {
        var (_, form) = await StartTransferAsync();
        await ScanAsync(form, FakeServer.Creme);
        await ScanAsync(form, FakeServer.Serum);
        Assert.Same(form.Lines[1], form.SelectedLine);

        Assert.Equal(0, form.MoveLineSelection(-1));
        Assert.Equal("1", form.LineQuantity);
        form.RemoveSelectedLine();

        Assert.Equal("Sérum nuit 30 ml", Assert.Single(form.Lines).Name);
        Assert.Same(form.Lines[0], form.SelectedLine);

        // Changer l'emplacement de départ met à jour le stock affiché sur les lignes.
        form.From = form.Locations[1];
        Assert.Equal("0", form.Lines[0].AvailableText);
    }

    [Fact]
    public async Task Un_code_inconnu_dans_un_transfert_est_signale_sans_ajouter_de_ligne()
    {
        var (_, form) = await StartTransferAsync();
        form.Lookup.Query = UnknownBarcode;

        Assert.False(await form.AddFromQueryAsync());

        Assert.Empty(form.Lines);
        Assert.Equal($"Aucun produit trouvé pour « {UnknownBarcode} ».", form.Lookup.Notice);
    }

    // ---------- Lecture des nombres ----------

    [Theory]
    [InlineData("12", 12)]
    [InlineData("12,5", 12.5)]
    [InlineData("12.50", 12.5)]
    [InlineData("1 250,50", 1250.5)]
    [InlineData("0,05", 0.05)]
    [InlineData("12,500", 12.5)]
    public void Un_montant_valide_est_lu_avec_la_virgule_ou_le_point(string text, double expected)
    {
        Assert.True(DecimalInput.TryParseAmount(text, out var amount));
        Assert.Equal((decimal)expected, amount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData(",5")]
    [InlineData("1,2,3")]
    [InlineData("1e3")]
    [InlineData("12,345")]
    [InlineData("+12")]
    [InlineData("١٢")]
    [InlineData("12345678901")]
    public void Un_montant_douteux_est_refuse(string? text) => Assert.False(DecimalInput.TryParseAmount(text, out _));

    [Fact]
    public void Les_quantites_s_affichent_sans_zeros_inutiles_avec_leur_signe()
    {
        Assert.Equal("12", Quantities.Format(12.000m));
        Assert.Equal("1,5", Quantities.Format(1.500m));
        Assert.Equal("−2", Quantities.FormatSigned(-2));
        Assert.Equal("+3,25", Quantities.FormatSigned(3.25m));
        Assert.Equal("0", Quantities.FormatSigned(0));
    }
}
