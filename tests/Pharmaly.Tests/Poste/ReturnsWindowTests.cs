using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste;
using Pharmaly.Poste.Modules.Administration;
using Pharmaly.Poste.Modules.Sales;
using Pharmaly.Poste.ViewModels;
using Pharmaly.Poste.Views;

namespace Pharmaly.Tests.Poste;

/// <summary>Retours clients et motifs de retour au clavier et au lecteur, dans la vraie fenêtre, sans écran.</summary>
public sealed class ReturnsWindowTests
{
    private readonly FakeServer server = new FakeServer().AsAdministrator().WithStock(10, 10);
    private readonly ManualInactivityTimer timer = new();

    private (MainWindow Window, ShellViewModel Shell) OpenWindow()
    {
        var shell = new ShellViewModel(server.Clients, timer, TimeSpan.Zero);
        var window = new MainWindow(shell, new LaunchOptions(null, false, null));
        window.Show();
        return (window, shell);
    }

    private static async Task SettleAsync()
    {
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None) =>
        window.KeyPressQwerty(key, modifiers);

    private static async Task SignInWithKeyboardAsync(MainWindow window, bool posteToo = true)
    {
        await SettleAsync();
        if (posteToo)
        {
            window.KeyTextInput(FakeServer.RegisterLogin);
            Press(window, PhysicalKey.Enter);
            window.KeyTextInput(FakeServer.RegisterPassword);
            Press(window, PhysicalKey.Enter);
            await SettleAsync();
        }

        window.KeyTextInput(FakeServer.EmployeeNumber.ToString());
        Press(window, PhysicalKey.Enter);
        window.KeyTextInput(FakeServer.EmployeeSecret);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
    }

    private static T ModuleView<T>(MainWindow window) where T : Control =>
        Assert.IsType<T>(window.FindControl<ContentControl>("ModuleHost")!.Content);

    private static bool Shown(Control view, string name) => view.FindControl<Control>(name)!.IsEffectivelyVisible;

    private static string? Text(Control view, string name) => view.FindControl<TextBlock>(name)!.Text;

    private static TextBox? FocusedTextBox(Control view) =>
        view.GetVisualDescendants().OfType<TextBox>().SingleOrDefault(box => box.IsFocused);

    /// <summary>Une vente faite plus tôt dans la journée, avant l'identification sur ce poste.</summary>
    private async Task<SaleDetail> SellCashAsync()
    {
        var total = SaleMath.LineTotal(2, FakeServer.Creme.SalePrice) + FakeServer.Serum.SalePrice;
        server.EmployeeIdentified = true;
        var sale = await server.CreateSaleAsync(
            new CreateSaleRequest(
                Guid.NewGuid(), [new SaleLineRequest(FakeServer.Creme.Id, 2), new SaleLineRequest(FakeServer.Serum.Id, 1)],
                SalePaymentModes.Cash, AmountReceived: total, ExpectedTotal: total),
            CancellationToken.None);
        server.EmployeeIdentified = false;
        return sale;
    }

    [AvaloniaFact]
    public async Task Identification_F1_touche_des_retours_scan_du_ticket_quantite_motif_apercu_confirmation_puis_ecran_de_fin()
    {
        server.WithOpenCashSession();
        var sale = await SellCashAsync();
        var stockAfterSale = server.StockLevels[FakeServer.Creme.Id];
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);

        // F1 : la vente. La touche des retours est affichée.
        Press(window, PhysicalKey.F1);
        await SettleAsync();
        var salesView = ModuleView<SalesView>(window);
        var module = Assert.IsType<SalesViewModel>(shell.CurrentModule);
        Assert.True(Shown(salesView, "ReturnsButton"));
        Assert.Equal("Ctrl+R", Text(salesView, "ReturnsKeyLabel"));

        // Ctrl+R : l'écran des retours, curseur dans la recherche de ticket.
        Press(window, PhysicalKey.R, RawInputModifiers.Control);
        await SettleAsync();
        Assert.Equal(SalesScreen.Returns, module.Screen);
        var view = salesView.FindControl<ReturnsView>("ReturnsView")!;
        Assert.True(view.IsEffectivelyVisible);
        Assert.False(Shown(salesView, "CartPanel"));
        var ticketBox = view.FindControl<TextBox>("TicketBox")!;
        Assert.True(ticketBox.IsFocused);

        // Scan du code-barres du ticket : les lignes s'affichent, le curseur est sur la quantité de la première.
        window.KeyTextInput(sale.TicketBarcode);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.Equal($"Ticket {sale.TicketNumber}", Text(view, "TicketTitleLabel"));
        Assert.Equal("7 900,40 DA", Text(view, "TicketTotalLabel"));
        Assert.False(Shown(view, "BlockedPanel"));
        var cremeBox = FocusedTextBox(view);
        Assert.NotNull(cremeBox);
        Assert.Same(module.Returns.Lines[0], cremeBox.DataContext);

        // Quantité 1 (la valeur 0 proposée est remplacée), Entrée : ligne suivante ; Entrée : le motif.
        window.KeyTextInput("1");
        Assert.Equal("1", module.Returns.Lines[0].QuantityText);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.Same(module.Returns.Lines[1], FocusedTextBox(view)!.DataContext);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.Equal(ReturnField.Reason, module.Returns.Field);
        Assert.True(view.FindControl<ListBox>("ReasonList")!.IsKeyboardFocusWithin);

        // Entrée sans motif : refusé, le motif est obligatoire.
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.Equal(ReturnsViewModel.ReasonRequiredMessage, Text(view, "ReturnStepErrorLabel"));

        // 2 : « Produit endommagé » ; la destination s'affiche.
        Press(window, PhysicalKey.Digit2);
        await SettleAsync();
        Assert.Equal(FakeServer.Damaged.Id, module.Returns.SelectedReason!.Reason.Id);
        Assert.Equal("Pas de remise en stock", Text(view, "DestinationLabel"));

        // Entrée : la note ; Entrée : l'aperçu du serveur. Rien n'est encore envoyé.
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.True(view.FindControl<TextBox>("NoteBox")!.IsFocused);
        window.KeyTextInput("Flacon fendu");
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.True(Shown(view, "ReturnConfirmPanel"));
        Assert.Equal("1 850,10 DA", Text(view, "PreviewRefundLabel"));
        Assert.Equal("En espèces — sortie de la caisse de ce poste", Text(view, "PreviewMethodLabel"));
        Assert.Single(server.PreviewRequests);
        Assert.Empty(server.ReturnRequests);

        // Entrée confirme : un seul envoi, avec le remboursement affiché.
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        var request = Assert.Single(server.ReturnRequests);
        Assert.Equal(1850.10m, request.ExpectedRefund);
        Assert.Equal(FakeServer.Damaged.Id, request.ReasonId);
        Assert.Equal("Flacon fendu", request.Note);

        // Écran de fin : numéro du retour, montant à rendre en grand. Endommagé : rien ne revient en stock.
        Assert.True(Shown(view, "ReturnDonePanel"));
        Assert.Equal($"Retour D1-R000001 — ticket {sale.TicketNumber}", Text(view, "ReturnDoneNumberLabel"));
        Assert.Equal("1 850,10 DA", Text(view, "ReturnDoneCashLabel"));
        Assert.True(Shown(view, "ReturnDoneCashLabel"));
        Assert.Equal(stockAfterSale, server.StockLevels[FakeServer.Creme.Id]);

        // Entrée : retour à la recherche de ticket.
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.False(Shown(view, "ReturnStepOverlay"));
        Assert.True(ticketBox.IsFocused);
        Assert.Single(server.ReturnRequests);

        // Échap : retour à la vente, curseur dans la recherche de produits.
        Press(window, PhysicalKey.Escape);
        await SettleAsync();
        Assert.Equal(SalesScreen.Cart, module.Screen);
        Assert.True(salesView.FindControl<TextBox>("SearchBox")!.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Apres_un_verrouillage_rien_du_retour_de_l_employe_precedent_ne_reste_affiche()
    {
        server.WithOpenCashSession();
        var sale = await SellCashAsync();
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        Press(window, PhysicalKey.R, RawInputModifiers.Control);
        await SettleAsync();
        window.KeyTextInput(sale.TicketNumber);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        window.KeyTextInput("2");
        Assert.Equal("2", Assert.IsType<SalesViewModel>(shell.CurrentModule).Returns.Lines[0].QuantityText);

        Press(window, PhysicalKey.F12);
        await SettleAsync();
        Assert.Null(shell.CurrentModule);
        await SignInWithKeyboardAsync(window, posteToo: false);

        var module = Assert.IsType<SalesViewModel>(shell.CurrentModule);
        Assert.Equal(SalesScreen.Cart, module.Screen);
        Assert.False(module.Returns.HasTicket);
        Assert.Empty(module.Returns.Lines);
        Assert.Empty(server.PreviewRequests);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Le_detail_d_un_ticket_de_l_historique_ouvre_le_retour_avec_Ctrl_R()
    {
        server.WithOpenCashSession();
        var sale = await SellCashAsync();
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        var salesView = ModuleView<SalesView>(window);

        Press(window, PhysicalKey.H, RawInputModifiers.Control);
        await SettleAsync();
        Assert.True(Shown(salesView.FindControl<SalesHistoryView>("HistoryView")!, "ReturnSaleButton"));
        Press(window, PhysicalKey.R, RawInputModifiers.Control);
        await SettleAsync();

        var module = Assert.IsType<SalesViewModel>(shell.CurrentModule);
        Assert.Equal(SalesScreen.Returns, module.Screen);
        Assert.Equal(sale.Id, module.Returns.Sale!.SaleId);
        var view = salesView.FindControl<ReturnsView>("ReturnsView")!;
        Assert.Same(module.Returns.Lines[0], FocusedTextBox(view)!.DataContext);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Sans_le_droit_de_retour_la_touche_des_retours_n_apparait_pas_et_ne_fait_rien()
    {
        server.EmployeePermissions = server.EmployeePermissions.Except([Permissions.ReturnsCreate]).ToList();
        server.WithOpenCashSession();
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        var salesView = ModuleView<SalesView>(window);

        Assert.False(Shown(salesView, "ReturnsButton"));
        Press(window, PhysicalKey.R, RawInputModifiers.Control);
        await SettleAsync();
        Assert.Equal(SalesScreen.Cart, Assert.IsType<SalesViewModel>(shell.CurrentModule).Screen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Administration_au_clavier_creer_un_motif_puis_le_desactiver_apres_confirmation()
    {
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        Press(window, PhysicalKey.F5);
        await SettleAsync();
        var adminView = ModuleView<AdministrationView>(window);
        var module = Assert.IsType<AdministrationViewModel>(shell.CurrentModule);

        // Ctrl+M : onglet des motifs, curseur sur la liste.
        Press(window, PhysicalKey.M, RawInputModifiers.Control);
        await SettleAsync();
        Assert.True(module.IsReturnReasonsTab);
        Assert.False(Shown(adminView, "EmployeesPanel"));
        var view = adminView.FindControl<ReturnReasonsView>("ReturnReasonsView")!;
        Assert.True(view.IsEffectivelyVisible);
        Assert.True(view.FindControl<ListBox>("ReasonsList")!.IsKeyboardFocusWithin);
        Assert.Equal(4, module.ReturnReasons.Reasons.Count);

        // Ctrl+N : libellé, Entrée, destination (↓ ↓ : pas de remise en stock), Entrée crée le motif.
        Press(window, PhysicalKey.N, RawInputModifiers.Control);
        await SettleAsync();
        window.KeyTextInput("Produit périmé");
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.True(view.FindControl<ComboBox>("NewDestinationBox")!.IsKeyboardFocusWithin);
        Press(window, PhysicalKey.ArrowDown);
        Press(window, PhysicalKey.ArrowDown);
        await SettleAsync();
        Assert.Equal(ReturnStockDestinations.None, module.ReturnReasons.NewDestination.Code);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        var created = Assert.Single(server.ReasonCreateRequests);
        Assert.Equal(("Produit périmé", ReturnStockDestinations.None), (created.Label, created.StockDestination));
        Assert.Equal("Produit périmé", module.ReturnReasons.SelectedReason!.Label);

        // Ctrl+D : désactivation, avec confirmation ; Échap n'envoie rien, Entrée confirme.
        Press(window, PhysicalKey.D, RawInputModifiers.Control);
        await SettleAsync();
        Assert.True(Shown(view, "ReasonConfirmationPanel"));
        Press(window, PhysicalKey.Escape);
        await SettleAsync();
        Assert.Empty(server.ReasonUpdateRequests);
        Press(window, PhysicalKey.D, RawInputModifiers.Control);
        await SettleAsync();
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        var update = Assert.Single(server.ReasonUpdateRequests);
        Assert.False(update.Request.IsActive);
        Assert.Equal("Motif « Produit périmé » désactivé.", Text(view, "ReasonsInfoLabel"));

        // Ctrl+E : retour aux employés, intact.
        Press(window, PhysicalKey.E, RawInputModifiers.Control);
        await SettleAsync();
        Assert.True(Shown(adminView, "EmployeesPanel"));
        Assert.True(adminView.FindControl<ListBox>("EmployeeList")!.IsKeyboardFocusWithin);
        window.Close();
    }
}
