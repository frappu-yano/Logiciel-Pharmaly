using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste;
using Pharmaly.Poste.Modules.Sales;
using Pharmaly.Poste.ViewModels;
using Pharmaly.Poste.Views;

namespace Pharmaly.Tests.Poste;

/// <summary>Parcours du module Vente au clavier et au lecteur, dans la vraie fenêtre, sans écran.</summary>
public sealed class SalesWindowTests
{
    private readonly FakeServer server = new FakeServer().AsAdministrator();
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

    /// <summary>Un lecteur de codes-barres : les chiffres, puis Entrée.</summary>
    private static async Task ScanAsync(Window window, string barcode)
    {
        window.KeyTextInput(barcode);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
    }

    private static async Task SignInWithKeyboardAsync(MainWindow window)
    {
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
    }

    private static SalesView SalesViewOf(MainWindow window) =>
        Assert.IsType<SalesView>(window.FindControl<ContentControl>("ModuleHost")!.Content);

    private static bool Shown(Control view, string name) => view.FindControl<Control>(name)!.IsEffectivelyVisible;

    private static string? Text(Control view, string name) => view.FindControl<TextBlock>(name)!.Text;

    [AvaloniaFact]
    public async Task Identification_F1_ouverture_de_caisse_deux_scans_especes_monnaie_puis_vente_suivante()
    {
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        Assert.Equal(SessionStage.Work, shell.Session.Stage);

        // F1 : module Vente. La caisse du poste n'est pas ouverte : le curseur attend le fond de caisse.
        Press(window, PhysicalKey.F1);
        await SettleAsync();
        var view = SalesViewOf(window);
        var viewModel = Assert.IsType<SalesViewModel>(shell.CurrentModule);
        Assert.True(Shown(view, "CashClosedPanel"));
        Assert.Equal("La caisse n'est pas ouverte", Text(view, "CashClosedLabel"));
        Assert.False(Shown(view, "CartPanel"));
        var openingBox = view.FindControl<TextBox>("OpeningFloatBox")!;
        Assert.True(openingBox.IsFocused);

        // Fond de caisse compté, Entrée : la confirmation est demandée, rien n'est envoyé.
        window.KeyTextInput("5000");
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.True(Shown(view, "OpeningConfirmationPanel"));
        Assert.Equal("5 000,00 DA", Text(view, "OpeningConfirmationLabel"));
        Assert.Equal("Ouvrir la caisse avec un fond de caisse de 5 000,00 DA ?", viewModel.OpeningConfirmationText);
        Assert.Empty(server.OpenSessionRequests);

        // Entrée confirme : la caisse est ouverte, le curseur est dans la recherche.
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.Equal(5000m, Assert.Single(server.OpenSessionRequests).OpeningFloat);
        Assert.True(Shown(view, "CartPanel"));
        Assert.False(Shown(view, "CashClosedPanel"));
        var searchBox = view.FindControl<TextBox>("SearchBox")!;
        Assert.True(searchBox.IsFocused);
        Assert.True(Shown(view, "LockWarningLabel"));
        Assert.Equal("F12 (verrouillage) : le panier en cours est perdu.", Text(view, "LockWarningLabel"));

        // Les touches affichées : * pour Espèces, la constante provisoire pour le TPE.
        Assert.Equal("*", Text(view, "CashKeyLabel"));
        Assert.Equal(SalesShortcuts.ProvisionalCardKey.ToString(), Text(view, "CardKeyLabel"));
        Assert.True(Shown(view, "CashButton"));
        Assert.True(Shown(view, "CardButton"));
        Assert.True(Shown(view, "UnpaidButton"));

        // Deux scans.
        await ScanAsync(window, FakeServer.Creme.Barcode!);
        await ScanAsync(window, FakeServer.Serum.Barcode!);
        Assert.Equal(2, viewModel.Lines.Count);
        Assert.Equal("6 050,30 DA", Text(view, "TotalLabel"));
        Assert.Equal("2 lignes", Text(view, "LineCountLabel"));
        Assert.True(searchBox.IsFocused);
        Assert.Equal(string.Empty, searchBox.Text ?? string.Empty);

        // * : espèces. Le caractère n'est pas écrit dans la recherche ; le curseur passe au montant reçu.
        Press(window, PhysicalKey.NumPadMultiply);
        await SettleAsync();
        Assert.True(Shown(view, "CashAmountPanel"));
        Assert.Equal(string.Empty, searchBox.Text ?? string.Empty);
        var receivedBox = view.FindControl<TextBox>("ReceivedBox")!;
        Assert.True(receivedBox.IsFocused);
        Assert.Empty(server.SaleRequests);

        // Montant reçu : la monnaie s'affiche pendant la frappe.
        window.KeyTextInput("7000");
        Assert.Equal("949,70 DA", Text(view, "ChangePreviewLabel"));
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        // Confirmation : monnaie à rendre en grand, rien n'est encore envoyé.
        Assert.True(Shown(view, "CashConfirmPanel"));
        Assert.False(Shown(view, "CashAmountPanel"));
        Assert.Equal("7 000,00 DA", Text(view, "AmountReceivedLabel"));
        Assert.Equal("949,70 DA", Text(view, "ChangeDueLabel"));
        Assert.Empty(server.SaleRequests);

        // Entrée : la vente part, une seule fois.
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        var request = Assert.Single(server.SaleRequests);
        Assert.Equal(SalePaymentModes.Cash, request.PaymentMode);
        Assert.Equal(7000m, request.AmountReceived);
        Assert.Equal(6050.30m, request.ExpectedTotal);
        Assert.Equal(2, request.Lines.Count);

        // Écran de fin : numéro de ticket, total, monnaie à rendre.
        Assert.True(Shown(view, "DonePanel"));
        Assert.Equal("Ticket D1-000001", Text(view, "DoneTicketLabel"));
        Assert.Equal("6 050,30 DA", Text(view, "DoneTotalLabel"));
        Assert.Equal("949,70 DA", Text(view, "DoneChangeLabel"));
        Assert.True(Shown(view, "DoneChangeLabel"));
        Assert.False(Shown(view, "DoneUnpaidLabel"));

        // Un seul appui : vente suivante, panier vide, curseur dans la recherche.
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.False(Shown(view, "StepOverlay"));
        Assert.Empty(viewModel.Lines);
        Assert.Equal("0,00 DA", Text(view, "TotalLabel"));
        Assert.True(searchBox.IsFocused);
        Assert.Single(server.SaleRequests);

        // La vente suivante commence par un simple scan.
        await ScanAsync(window, FakeServer.Creme.Barcode!);
        Assert.Equal("1 850,10 DA", Text(view, "TotalLabel"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task TPE_au_clavier_Entree_ne_repond_jamais_a_la_place_du_terminal_et_un_refus_garde_le_panier()
    {
        server.WithOpenCashSession();
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        var view = SalesViewOf(window);
        var viewModel = Assert.IsType<SalesViewModel>(shell.CurrentModule);
        var searchBox = view.FindControl<TextBox>("SearchBox")!;
        Assert.True(searchBox.IsFocused);

        // Scan, quantité portée à 3 avec +, puis la touche TPE (provisoire).
        await ScanAsync(window, FakeServer.Creme.Barcode!);
        Press(window, PhysicalKey.NumPadAdd);
        Press(window, PhysicalKey.NumPadAdd);
        await SettleAsync();
        Assert.Equal(3, viewModel.Lines[0].Quantity);
        Assert.Equal(string.Empty, searchBox.Text ?? string.Empty);
        Assert.Equal(Key.F8, SalesShortcuts.ProvisionalCardKey);
        Press(window, PhysicalKey.F8);
        await SettleAsync();
        Assert.True(Shown(view, "CardConfirmPanel"));
        Assert.Empty(server.SaleRequests);

        // Entrée confirme le lancement : la vente est enregistrée « en attente TPE ».
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.True(Shown(view, "CardPendingPanel"));
        Assert.Equal(SaleStatuses.CardPending, Assert.Single(server.Tickets).Status);
        Assert.Equal("Ticket D1-000001 — 5 550,30 DA", Text(view, "PendingCardLabel"));

        // Entrée, Espace, Échap et un scan parasite : aucune réponse n'est donnée.
        Press(window, PhysicalKey.Enter);
        Press(window, PhysicalKey.Space);
        Press(window, PhysicalKey.Escape);
        await ScanAsync(window, FakeServer.Serum.Barcode!);
        Assert.True(Shown(view, "CardPendingPanel"));
        Assert.Empty(server.CardRequests);
        Assert.Single(viewModel.Lines);
        Assert.Single(server.SaleRequests);

        // R : paiement refusé. Le serveur annule la vente ; le panier reste, le curseur revient à la recherche.
        Press(window, PhysicalKey.R);
        await SettleAsync();
        Assert.False(Assert.Single(server.CardRequests).Request.Accepted);
        Assert.Equal(SaleStatuses.Cancelled, server.Tickets[0].Status);
        Assert.False(Shown(view, "StepOverlay"));
        Assert.Equal(3, Assert.Single(viewModel.Lines).Quantity);
        Assert.StartsWith("Paiement refusé : la vente D1-000001 est annulée et le stock est restitué.", Text(view, "InfoLabel"));
        Assert.True(searchBox.IsFocused);
        Assert.Equal(string.Empty, searchBox.Text ?? string.Empty);

        // Nouvelle tentative par carte, acceptée cette fois avec A : nouveau ticket, nouvel identifiant.
        Press(window, PhysicalKey.F8);
        await SettleAsync();
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Press(window, PhysicalKey.A);
        await SettleAsync();
        Assert.Equal(2, server.SaleRequests.Count);
        Assert.NotEqual(server.SaleRequests[0].OperationId, server.SaleRequests[1].OperationId);
        Assert.Equal(SaleStatuses.Completed, server.Tickets[1].Status);
        Assert.True(Shown(view, "DonePanel"));
        Assert.True(Shown(view, "DoneCardLabel"));
        Assert.Equal("Ticket D1-000002", Text(view, "DoneTicketLabel"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Impaye_au_clavier_le_client_est_cherche_puis_confirme_et_le_ticket_est_marque_impaye()
    {
        // Vendre sans encaisser : seul l'impayé est proposé (D21).
        server.EmployeePermissions = [Permissions.SalesCreate, Permissions.ProductsView];
        server.WithOpenCashSession();
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        var view = SalesViewOf(window);
        var viewModel = Assert.IsType<SalesViewModel>(shell.CurrentModule);
        Assert.False(Shown(view, "CashButton"));
        Assert.False(Shown(view, "CardButton"));
        Assert.False(Shown(view, "DiscountButton"));
        Assert.True(Shown(view, "UnpaidButton"));

        await ScanAsync(window, FakeServer.Serum.Barcode!);

        // * et la touche TPE n'ouvrent rien.
        Press(window, PhysicalKey.NumPadMultiply);
        Press(window, PhysicalKey.F8);
        await SettleAsync();
        Assert.False(Shown(view, "StepOverlay"));

        // F9 : impayé. Le curseur est dans la recherche de client.
        Press(window, PhysicalKey.F9);
        await SettleAsync();
        Assert.True(Shown(view, "UnpaidCustomerPanel"));
        Assert.True(view.FindControl<TextBox>("CustomerSearchBox")!.IsFocused);

        // Entrée sans client : refus, rien ne part.
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.Equal(SalesViewModel.CustomerRequiredMessage, Text(view, "CustomerErrorLabel"));
        Assert.Empty(server.SaleRequests);

        window.KeyTextInput("benali");
        await SettleAsync();
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        // Confirmation explicite : client et montant.
        Assert.True(Shown(view, "UnpaidConfirmPanel"));
        Assert.Equal("Nadia Benali — 0550 12 34 56", Text(view, "UnpaidCustomerLabel"));
        Assert.Equal("4 200,20 DA", Text(view, "UnpaidTotalLabel"));
        Assert.Empty(server.SaleRequests);

        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.Equal(FakeServer.Benali.Id, Assert.Single(server.SaleRequests).CustomerId);
        Assert.True(Shown(view, "DonePanel"));
        Assert.True(Shown(view, "DoneUnpaidLabel"));
        Assert.Equal("IMPAYÉ", Text(view, "DoneUnpaidLabel"));
        Assert.Equal("Client : Nadia Benali", Text(view, "DoneCustomerLabel"));
        Assert.False(Shown(view, "DoneChangeLabel"));
        Assert.Empty(viewModel.Lines);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Historique_au_clavier_scan_du_ticket_puis_annulation_avec_motif_que_seule_la_touche_dediee_confirme()
    {
        server.WithOpenCashSession();
        server.EmployeeIdentified = true;
        var sold = await server.CreateSaleAsync(
            new CreateSaleRequest(
                Guid.NewGuid(), [new SaleLineRequest(FakeServer.Creme.Id, 2)], SalePaymentModes.Cash, AmountReceived: 4000m,
                ExpectedTotal: 3700.20m),
            CancellationToken.None);
        server.EmployeeIdentified = false;
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        var view = SalesViewOf(window);
        var viewModel = Assert.IsType<SalesViewModel>(shell.CurrentModule);

        // Ctrl+H : historique, curseur dans la recherche de ticket.
        Press(window, PhysicalKey.H, RawInputModifiers.Control);
        await SettleAsync();
        var history = view.FindControl<SalesHistoryView>("HistoryView")!;
        Assert.True(history.IsEffectivelyVisible);
        Assert.True(history.FindControl<TextBox>("TicketBox")!.IsFocused);
        Assert.Equal("Ticket D1-000001", Text(history, "DetailTitleLabel"));

        // Le lecteur lit le code-barres du ticket.
        await ScanAsync(window, sold.TicketBarcode);
        Assert.Equal(sold.Id, viewModel.History.SelectedSale!.Id);
        Assert.Equal("3 700,20 DA", Text(history, "DetailTotalLabel"));
        Assert.True(Shown(history, "CancelSaleButton"));

        // Ctrl+Suppr : motif obligatoire.
        Press(window, PhysicalKey.Delete, RawInputModifiers.Control);
        await SettleAsync();
        Assert.True(Shown(history, "CancelReasonPanel"));
        Assert.True(history.FindControl<TextBox>("ReasonBox")!.IsFocused);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.Equal(SalesHistoryViewModel.ReasonRequiredMessage, Text(history, "ReasonErrorLabel"));
        window.KeyTextInput("Erreur de produit");
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        // Confirmation forte : Entrée ne confirme pas.
        Assert.True(Shown(history, "CancelConfirmPanel"));
        Press(window, PhysicalKey.Enter);
        Press(window, PhysicalKey.Space);
        await SettleAsync();
        Assert.Empty(server.CancelRequests);
        Assert.True(Shown(history, "CancelConfirmPanel"));

        Press(window, PhysicalKey.F9);
        await SettleAsync();
        Assert.Equal("Erreur de produit", Assert.Single(server.CancelRequests).Request.Reason);
        Assert.False(Shown(history, "HistoryStepOverlay"));
        Assert.Equal("Annulée", Text(history, "DetailStatusLabel"));
        Assert.Equal("Le ticket D1-000001 est annulé. Rendez 3 700,20 DA en espèces au client.", Text(history, "HistoryInfoLabel"));

        // Échap : retour à la vente, curseur dans la recherche.
        Press(window, PhysicalKey.Escape);
        await SettleAsync();
        Assert.True(Shown(view, "CartPanel"));
        Assert.True(view.FindControl<TextBox>("SearchBox")!.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Plus_frappe_juste_apres_un_scan_encore_en_route_porte_sur_le_produit_scanne()
    {
        server.WithOpenCashSession();
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        var view = SalesViewOf(window);
        var viewModel = Assert.IsType<SalesViewModel>(shell.CurrentModule);
        await ScanAsync(window, FakeServer.Creme.Barcode!);

        // Le serveur tarde à répondre au second scan ; le caissier frappe + aussitôt.
        server.BarcodeGate = new TaskCompletionSource();
        window.KeyTextInput(FakeServer.Serum.Barcode!);
        Press(window, PhysicalKey.Enter);
        Press(window, PhysicalKey.NumPadAdd);
        await SettleAsync();
        Assert.Equal([1m], viewModel.Lines.Select(line => line.Quantity));

        server.BarcodeGate.SetResult();
        await SettleAsync();

        Assert.Equal([FakeServer.Creme.Id, FakeServer.Serum.Id], viewModel.Lines.Select(line => line.ProductId));
        Assert.Equal([1m, 2m], viewModel.Lines.Select(line => line.Quantity));
        Assert.Equal(string.Empty, view.FindControl<TextBox>("SearchBox")!.Text ?? string.Empty);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Le_verrouillage_detruit_le_module_et_l_employe_suivant_retrouve_un_panier_vide()
    {
        server.WithOpenCashSession();
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        var firstView = SalesViewOf(window);
        await ScanAsync(window, FakeServer.Creme.Barcode!);
        Assert.Single(Assert.IsType<SalesViewModel>(shell.CurrentModule).Lines);

        Press(window, PhysicalKey.F12);
        await SettleAsync();
        Assert.Null(window.FindControl<ContentControl>("ModuleHost")!.Content);
        Assert.Null(shell.CurrentModule);

        window.KeyTextInput(FakeServer.EmployeeNumber.ToString());
        Press(window, PhysicalKey.Enter);
        window.KeyTextInput(FakeServer.EmployeeSecret);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        var nextView = SalesViewOf(window);
        Assert.NotSame(firstView, nextView);
        Assert.Empty(Assert.IsType<SalesViewModel>(shell.CurrentModule).Lines);
        Assert.Equal("0,00 DA", Text(nextView, "TotalLabel"));
        Assert.True(nextView.FindControl<TextBox>("SearchBox")!.IsFocused);
        window.Close();
    }
}
