using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pharmaly.Domain;
using Pharmaly.Poste;
using Pharmaly.Poste.Modules.Products;
using Pharmaly.Poste.ViewModels;
using Pharmaly.Poste.Views;

namespace Pharmaly.Tests.Poste;

/// <summary>Parcours du module Produits au clavier, dans la vraie fenêtre, sans écran.</summary>
public sealed class ProductsWindowTests
{
    private readonly FakeServer server = new();
    private readonly ManualInactivityTimer timer = new();

    private static async Task SettleAsync()
    {
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None) =>
        window.KeyPressQwerty(key, modifiers);

    /// <summary>Identification au clavier puis F4 : le module Produits, curseur dans la recherche.</summary>
    private async Task<(MainWindow Window, ProductsView View, ProductsViewModel Module)> OpenProductsAsync()
    {
        var shell = new ShellViewModel(server.Clients, timer, TimeSpan.Zero);
        var window = new MainWindow(shell, new LaunchOptions(null, false, null));
        window.Show();
        await SettleAsync();
        window.KeyTextInput(FakeServer.RegisterLogin);
        Press(window, PhysicalKey.Enter);
        window.KeyTextInput(FakeServer.RegisterPassword);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        window.KeyTextInput(FakeServer.EmployeeNumber.ToString());
        Press(window, PhysicalKey.Enter);
        window.KeyTextInput(FakeServer.EmployeeSecret);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        Press(window, PhysicalKey.F4);
        await SettleAsync();
        var view = Assert.IsType<ProductsView>(window.FindControl<ContentControl>("ModuleHost")!.Content);
        var module = Assert.IsType<ProductsViewModel>(shell.CurrentModule);
        Assert.True(view.FindControl<TextBox>("SearchBox")!.IsFocused);
        return (window, view, module);
    }

    private static TView FormView<TView>(ProductsView view)
        where TView : Control => Assert.IsType<TView>(view.FindControl<ContentControl>("FormHost")!.Content);

    [AvaloniaFact]
    public async Task Identification_F4_recherche_fiche_puis_ajustement_d_inventaire_au_clavier()
    {
        server.AsAdministrator();
        var (window, view, module) = await OpenProductsAsync();
        var searchBox = view.FindControl<TextBox>("SearchBox")!;
        Assert.True(view.FindControl<Control>("EmptyDetailLabel")!.IsEffectivelyVisible);

        // Recherche par nom : la liste arrive pendant la frappe, Entrée ouvre la fiche.
        window.KeyTextInput("crème");
        await SettleAsync();
        Assert.Single(module.Lookup.Results);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        Assert.Equal("Crème hydratante 50 ml", view.FindControl<TextBlock>("DetailNameLabel")!.Text);
        Assert.Equal("1 850,10 DA", view.FindControl<TextBlock>("DetailPriceLabel")!.Text);
        Assert.Equal("Actif", view.FindControl<TextBlock>("DetailStateLabel")!.Text);
        Assert.True(searchBox.IsFocused);
        Assert.True(view.FindControl<Button>("AdjustButton")!.IsEffectivelyVisible);

        // F9 : ajustement. Le curseur est sur l'emplacement ; Entrée passe à la quantité.
        Press(window, PhysicalKey.F9);
        await SettleAsync();
        var form = FormView<StockAdjustmentView>(view);
        Assert.True(view.FindControl<Control>("FormPanel")!.IsEffectivelyVisible);
        Assert.True(form.FindControl<ComboBox>("LocationBox")!.IsFocused);
        Assert.Equal("12", form.FindControl<TextBlock>("CurrentStockLabel")!.Text);
        Press(window, PhysicalKey.Enter);
        Assert.True(form.FindControl<TextBox>("QuantityBox")!.IsFocused);
        window.KeyTextInput("10");
        Press(window, PhysicalKey.Enter);
        Assert.True(form.FindControl<TextBox>("ReasonBox")!.IsFocused);
        window.KeyTextInput("Inventaire");
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        // Confirmation avant → après : rien n'est encore envoyé, Échap revient à la saisie.
        Assert.True(form.FindControl<Control>("ConfirmationPanel")!.IsEffectivelyVisible);
        Assert.Equal("Stock : 12   →   10   (−2)", form.FindControl<TextBlock>("ConfirmationLabel")!.Inlines!.Text);
        Assert.Empty(server.Adjustments);
        Press(window, PhysicalKey.Escape);
        await SettleAsync();
        Assert.False(form.FindControl<Control>("ConfirmationPanel")!.IsEffectivelyVisible);
        Assert.True(form.FindControl<TextBox>("QuantityBox")!.IsFocused);
        Assert.Empty(server.Adjustments);

        // Ctrl+Entrée revalide, Entrée confirme.
        Press(window, PhysicalKey.Enter, RawInputModifiers.Control);
        await SettleAsync();
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        var request = Assert.Single(server.Adjustments);
        Assert.Equal(10, request.NewQuantity);
        Assert.Equal("Inventaire", request.Reason);
        Assert.False(view.FindControl<Control>("FormPanel")!.IsEffectivelyVisible);
        Assert.Contains("12 → 10 (−2)", view.FindControl<TextBlock>("InfoLabel")!.Text);
        Assert.Equal("10", module.Detail!.Stock[0].QuantityText);

        // Le curseur est revenu dans la recherche, texte sélectionné : le scan suivant le remplace.
        Assert.True(searchBox.IsFocused);
        window.KeyTextInput(FakeServer.Serum.Barcode!);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.Equal(FakeServer.Serum.Barcode, searchBox.Text);
        Assert.Equal("Sérum nuit 30 ml", view.FindControl<TextBlock>("DetailNameLabel")!.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Creation_au_clavier_avec_un_code_scanne_inconnu_puis_changement_de_prix_confirme()
    {
        server.AsAdministrator();
        var (window, view, module) = await OpenProductsAsync();

        // Un code inconnu est scanné, puis Ctrl+N : il est déjà dans le formulaire.
        window.KeyTextInput("2999999999993");
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.True(view.FindControl<TextBlock>("NoticeLabel")!.IsEffectivelyVisible);
        Press(window, PhysicalKey.N, RawInputModifiers.Control);
        await SettleAsync();
        var form = FormView<ProductCreateView>(view);
        Assert.True(form.FindControl<TextBox>("NameBox")!.IsFocused);

        window.KeyTextInput("Lait corps 200 ml");
        Press(window, PhysicalKey.Enter);
        Assert.True(form.FindControl<TextBox>("BarcodeBox")!.IsFocused);
        // Le lecteur ajoute un second code ; Entrée dans le champ vide passe à la marque.
        window.KeyTextInput("2000000000077");
        Press(window, PhysicalKey.Enter);
        Assert.True(form.FindControl<TextBox>("BarcodeBox")!.IsFocused);
        Press(window, PhysicalKey.Enter);
        Assert.True(form.FindControl<TextBox>("BrandBox")!.IsFocused);
        window.KeyTextInput("Botaniq");
        Press(window, PhysicalKey.Enter);
        Assert.True(form.FindControl<TextBox>("CategoryBox")!.IsFocused);
        Press(window, PhysicalKey.Enter);
        Assert.True(form.FindControl<TextBox>("PriceBox")!.IsFocused);

        // Prix invalide : refusé sur place, rien n'est envoyé.
        window.KeyTextInput("99O");
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.Equal(DecimalInput.InvalidAmountMessage, form.FindControl<TextBlock>("ErrorLabel")!.Text);
        Assert.Empty(server.ProductCreations);
        Press(window, PhysicalKey.A, RawInputModifiers.Control);
        window.KeyTextInput("990,50");
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        var created = Assert.Single(server.ProductCreations);
        Assert.Equal("Lait corps 200 ml", created.Name);
        Assert.Equal(["2999999999993", "2000000000077"], created.Barcodes);
        Assert.Equal("Botaniq", created.Brand);
        Assert.Null(created.Category);
        Assert.Equal(990.50m, created.SalePrice);
        Assert.Equal("Lait corps 200 ml", view.FindControl<TextBlock>("DetailNameLabel")!.Text);
        Assert.Equal("« Lait corps 200 ml » est créé.", view.FindControl<TextBlock>("InfoLabel")!.Text);
        Assert.True(view.FindControl<TextBox>("SearchBox")!.IsFocused);

        // F8 : changement de prix, confirmé par Entrée après avoir vu les deux prix.
        Press(window, PhysicalKey.F8);
        await SettleAsync();
        var priceForm = FormView<SalePriceView>(view);
        Assert.True(priceForm.FindControl<TextBox>("NewPriceBox")!.IsFocused);
        window.KeyTextInput("1050");
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.True(priceForm.FindControl<Control>("ConfirmationPanel")!.IsEffectivelyVisible);
        Assert.Equal("990,50 DA   →   1 050,00 DA", priceForm.FindControl<TextBlock>("ConfirmationLabel")!.Inlines!.Text);
        Assert.Empty(server.PriceChanges);
        // Pendant la confirmation, une frappe ne modifie plus le prix.
        window.KeyTextInput("9");
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        Assert.Equal(1050m, Assert.Single(server.PriceChanges).Request.NewPrice);
        Assert.Equal("1 050,00 DA", view.FindControl<TextBlock>("DetailPriceLabel")!.Text);
        Assert.Null(module.ActiveForm);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Transfert_au_clavier_scan_quantite_confirmation_et_resultat()
    {
        server.AsAdministrator();
        var (window, view, module) = await OpenProductsAsync();

        Press(window, PhysicalKey.F10);
        await SettleAsync();
        var form = FormView<StockTransferView>(view);
        var transfer = Assert.IsType<StockTransferViewModel>(module.ActiveForm);
        Assert.True(form.FindControl<ComboBox>("FromBox")!.IsFocused);
        Press(window, PhysicalKey.Enter);
        Assert.True(form.FindControl<ComboBox>("ToBox")!.IsFocused);
        Press(window, PhysicalKey.Enter);
        var searchBox = form.FindControl<TextBox>("SearchBox")!;
        Assert.True(searchBox.IsFocused);

        // Scan, Tab, quantité, Entrée : retour à la recherche pour le produit suivant.
        window.KeyTextInput(FakeServer.Serum.Barcode!);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.Single(transfer.Lines);
        Assert.True(searchBox.IsFocused);
        Press(window, PhysicalKey.Tab);
        Assert.True(form.FindControl<TextBox>("QuantityBox")!.IsFocused);
        window.KeyTextInput("8");
        Press(window, PhysicalKey.Enter);
        Assert.True(searchBox.IsFocused);
        Assert.Equal(8, transfer.Lines[0].Quantity);

        // Stock insuffisant : le message du serveur est affiché, la ligne est toujours là.
        Press(window, PhysicalKey.Enter, RawInputModifiers.Control);
        await SettleAsync();
        Assert.True(form.FindControl<Control>("ConfirmationPanel")!.IsEffectivelyVisible);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.Equal(
            "Stock insuffisant pour « Sérum nuit 30 ml » : 8 demandé, 3 disponible.", form.FindControl<TextBlock>("ErrorLabel")!.Text);
        Assert.False(form.FindControl<Control>("ConfirmationPanel")!.IsEffectivelyVisible);
        Assert.Single(transfer.Lines);
        Assert.True(searchBox.IsFocused);

        // Correction de la quantité, puis transfert confirmé.
        Press(window, PhysicalKey.Tab);
        window.KeyTextInput("2");
        Press(window, PhysicalKey.Enter);
        Press(window, PhysicalKey.Enter, RawInputModifiers.Control);
        await SettleAsync();
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        Assert.Equal(2, server.Transfers.Count);
        Assert.Equal(1, server.StockOf(FakeServer.Serum.Id, FakeServer.MainLocation.Id));
        Assert.True(form.FindControl<Control>("ResultPanel")!.IsEffectivelyVisible);
        Assert.StartsWith("Transfert enregistré", form.FindControl<TextBlock>("ResultTitleLabel")!.Text);
        Assert.True(searchBox.IsFocused);

        // Échap ferme le transfert et rend le curseur à la recherche du module.
        Press(window, PhysicalKey.Escape);
        await SettleAsync();
        Assert.Null(module.ActiveForm);
        Assert.True(view.FindControl<TextBox>("SearchBox")!.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Sans_les_droits_les_boutons_sont_absents_et_les_raccourcis_sans_effet()
    {
        // Droit de consulter seulement (réglage par défaut du faux serveur).
        var (window, view, module) = await OpenProductsAsync();
        window.KeyTextInput(FakeServer.Creme.Barcode!);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.Equal("Crème hydratante 50 ml", view.FindControl<TextBlock>("DetailNameLabel")!.Text);

        foreach (var name in new[] { "CreateButton", "TransferButton", "EditButton", "PriceButton", "AdjustButton" })
        {
            Assert.False(view.FindControl<Button>(name)!.IsEffectivelyVisible, name);
        }

        Press(window, PhysicalKey.N, RawInputModifiers.Control);
        Press(window, PhysicalKey.F7);
        Press(window, PhysicalKey.F8);
        Press(window, PhysicalKey.F9);
        Press(window, PhysicalKey.F10);
        await SettleAsync();

        Assert.Null(module.ActiveForm);
        Assert.False(view.FindControl<Control>("FormPanel")!.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Avec_le_seul_droit_de_modifier_seul_le_bouton_Modifier_apparait_et_Echap_ferme_le_formulaire()
    {
        server.EmployeePermissions = [Permissions.ProductsView, Permissions.ProductsEdit];
        var (window, view, module) = await OpenProductsAsync();
        window.KeyTextInput(FakeServer.Creme.Barcode!);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        Assert.True(view.FindControl<Button>("EditButton")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<Button>("PriceButton")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<Button>("AdjustButton")!.IsEffectivelyVisible);

        Press(window, PhysicalKey.F7);
        await SettleAsync();
        var form = FormView<ProductEditView>(view);
        Assert.True(form.FindControl<TextBox>("NameBox")!.IsFocused);
        Assert.DoesNotContain(form.GetVisualDescendants().OfType<TextBox>(), box => box.Name == "PriceBox");

        Press(window, PhysicalKey.Escape);
        await SettleAsync();
        Assert.Null(module.ActiveForm);
        Assert.Empty(server.ProductUpdates);
        Assert.True(view.FindControl<TextBox>("SearchBox")!.IsFocused);
        window.Close();
    }
}
