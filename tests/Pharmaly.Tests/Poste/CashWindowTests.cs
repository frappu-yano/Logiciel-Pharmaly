using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Pharmaly.Contracts;
using Pharmaly.Poste;
using Pharmaly.Poste.Modules.Cash;
using Pharmaly.Poste.Modules.Sales;
using Pharmaly.Poste.ViewModels;
using Pharmaly.Poste.Views;

namespace Pharmaly.Tests.Poste;

/// <summary>Parcours du module Caisse au clavier et au lecteur, dans la vraie fenêtre, sans écran.</summary>
public sealed class CashWindowTests
{
    private readonly FakeServer server = new FakeServer().AsAdministrator().WithOpenCashSession();
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

    /// <summary>Un lecteur de codes-barres : les caractères, puis Entrée.</summary>
    private static async Task ScanAsync(Window window, string barcode)
    {
        window.KeyTextInput(barcode);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
    }

    private static async Task IdentifyEmployeeAsync(MainWindow window)
    {
        window.KeyTextInput(FakeServer.EmployeeNumber.ToString());
        Press(window, PhysicalKey.Enter);
        window.KeyTextInput(FakeServer.EmployeeSecret);
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
        await IdentifyEmployeeAsync(window);
    }

    private static CashView CashViewOf(MainWindow window) =>
        Assert.IsType<CashView>(window.FindControl<ContentControl>("ModuleHost")!.Content);

    private static bool Shown(Control view, string name) => view.FindControl<Control>(name)!.IsEffectivelyVisible;

    private static string? Text(Control view, string name) => view.FindControl<TextBlock>(name)!.Text;

    [AvaloniaFact]
    public async Task Identification_F3_scan_d_un_ticket_second_ticket_du_meme_client_etoile_confirmation_et_recapitulatif()
    {
        var first = server.AddReceivable(FakeServer.Benali, 3500m);
        var second = server.AddReceivable(FakeServer.Benali, 6200m);
        server.AddReceivable(FakeServer.Haddad, 2000m);
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        Assert.Equal(SessionStage.Work, shell.Session.Stage);

        // F3 : module Caisse, onglet Impayés ; le curseur attend le lecteur.
        Press(window, PhysicalKey.F3);
        await SettleAsync();
        var view = CashViewOf(window);
        var viewModel = Assert.IsType<CashViewModel>(shell.CurrentModule);
        Assert.True(Shown(view, "ReceivablesPanel"));
        Assert.False(Shown(view, "SessionPanel"));
        var searchBox = view.FindControl<TextBox>("SearchBox")!;
        Assert.True(searchBox.IsFocused);
        Assert.Equal("11 700,00 DA", Text(view, "OpenAmountLabel"));
        Assert.Equal("3", Text(view, "OpenCountLabel"));
        Assert.Equal("*", Text(view, "CashKeyLabel"));
        Assert.Equal(SalesShortcuts.ProvisionalCardKey.ToString(), Text(view, "CardKeyLabel"));
        Assert.True(Shown(view, "CashButton"));
        Assert.True(Shown(view, "CardButton"));

        // Scan du premier ticket : les tickets de Nadia Benali, celui-ci coché.
        await ScanAsync(window, first.TicketBarcode);
        Assert.Equal(string.Empty, searchBox.Text ?? string.Empty);
        Assert.True(Shown(view, "CustomerFilterChip"));
        Assert.Equal("Client : Nadia Benali", Text(view, "CustomerFilterLabel"));
        Assert.Equal(2, viewModel.Rows.Count);
        Assert.Equal(first.Id, viewModel.CurrentRow!.Id);
        Assert.Equal("3 500,00 DA", Text(view, "SelectionTotalLabel"));

        // ↓ puis Espace : le second ticket rejoint la sélection. Rien n'est écrit dans la recherche.
        Press(window, PhysicalKey.ArrowDown);
        Press(window, PhysicalKey.Space);
        await SettleAsync();
        Assert.Equal(second.Id, viewModel.CurrentRow!.Id);
        Assert.Equal(2, viewModel.SelectionCount);
        Assert.Equal("9 700,00 DA", Text(view, "SelectionTotalLabel"));
        Assert.Equal(string.Empty, searchBox.Text ?? string.Empty);
        Assert.True(searchBox.IsFocused);

        // * : espèces. Le caractère n'est pas écrit ; le curseur passe au montant reçu. Rien n'est envoyé.
        Press(window, PhysicalKey.NumPadMultiply);
        await SettleAsync();
        Assert.True(Shown(view, "CashAmountPanel"));
        Assert.Equal("Nadia Benali", Text(view, "CashCustomerLabel"));
        Assert.Equal("9 700,00 DA", Text(view, "CashTotalLabel"));
        Assert.Equal(string.Empty, searchBox.Text ?? string.Empty);
        Assert.True(view.FindControl<TextBox>("ReceivedBox")!.IsFocused);
        Assert.Empty(server.SettleRequests);

        window.KeyTextInput("10000");
        Assert.Equal("300,00 DA", Text(view, "ChangePreviewLabel"));
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        // Confirmation : client, total, reçu, monnaie. Toujours rien d'envoyé.
        Assert.True(Shown(view, "CashConfirmPanel"));
        Assert.Equal("9 700,00 DA", Text(view, "CashConfirmTotalLabel"));
        Assert.Equal("10 000,00 DA", Text(view, "AmountReceivedLabel"));
        Assert.Equal("300,00 DA", Text(view, "ChangeDueLabel"));
        Assert.Empty(server.SettleRequests);

        // Entrée : un seul envoi, exactement la somme due.
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        var request = Assert.Single(server.SettleRequests);
        Assert.Equal(PaymentMethods.Cash, request.Method);
        Assert.Equal(9700m, request.Amount);
        Assert.Equal(10000m, request.AmountReceived);
        Assert.Equal(2, request.ReceivableIds.Count);

        // Récapitulatif.
        Assert.True(Shown(view, "DonePanel"));
        Assert.Equal("Nadia Benali — 2 tickets soldés", Text(view, "DoneTitleLabel"));
        Assert.Equal("9 700,00 DA", Text(view, "DoneAmountLabel"));
        Assert.Equal("300,00 DA", Text(view, "DoneChangeLabel"));

        // Entrée : retour au tableau relu, curseur dans la recherche.
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.False(Shown(view, "StepOverlay"));
        Assert.Empty(viewModel.Rows);
        Assert.Equal("2 000,00 DA", Text(view, "OpenAmountLabel"));
        Assert.True(searchBox.IsFocused);
        Assert.Single(server.SettleRequests);
        window.Close();
    }

    [AvaloniaFact]
    public async Task L_etoile_du_clavier_principal_reconnue_au_texte_ouvre_les_especes_sans_rien_ecrire()
    {
        var ticket = server.AddReceivable(FakeServer.Benali, 3500m);
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        Press(window, PhysicalKey.F3);
        await SettleAsync();
        var view = CashViewOf(window);
        var viewModel = Assert.IsType<CashViewModel>(shell.CurrentModule);
        await ScanAsync(window, ticket.TicketBarcode);

        // Disposition où la touche n'apporte pas son symbole : seul le texte « * » arrive.
        window.KeyTextInput("*");
        await SettleAsync();

        Assert.Equal(SettlementStep.CashAmount, viewModel.Step);
        Assert.Equal(string.Empty, view.FindControl<TextBox>("SearchBox")!.Text ?? string.Empty);
        window.KeyTextInput("*");
        await SettleAsync();
        Assert.Equal(string.Empty, view.FindControl<TextBox>("ReceivedBox")!.Text ?? string.Empty);
        Assert.Empty(server.SettleRequests);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TPE_au_clavier_Entree_ne_repond_jamais_et_R_laisse_la_dette_ouverte()
    {
        var ticket = server.AddReceivable(FakeServer.Haddad, 2000m);
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        Press(window, PhysicalKey.F3);
        await SettleAsync();
        var view = CashViewOf(window);
        var viewModel = Assert.IsType<CashViewModel>(shell.CurrentModule);

        await ScanAsync(window, ticket.TicketNumber);
        Press(window, PhysicalKey.F8);
        await SettleAsync();
        Assert.True(Shown(view, "CardConfirmPanel"));
        Assert.Empty(server.SettleRequests);

        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.True(Shown(view, "CardPendingPanel"));
        Assert.Single(server.SettleRequests);

        // Entrée et Échap ne répondent pas à la place du terminal.
        Press(window, PhysicalKey.Enter);
        Press(window, PhysicalKey.Escape);
        await SettleAsync();
        Assert.True(Shown(view, "CardPendingPanel"));
        Assert.Empty(server.SettlementCardRequests);

        Press(window, PhysicalKey.R);
        await SettleAsync();
        Assert.False(Assert.Single(server.SettlementCardRequests).Request.Accepted);
        Assert.False(Shown(view, "StepOverlay"));
        Assert.Equal(ReceivableStatuses.Open, server.Receivable(ticket.Id).Status);
        Assert.True(Shown(view, "InfoLabel"));
        Assert.Equal(1, viewModel.SelectionCount);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Ctrl_Tab_mene_a_la_session_de_caisse_et_F9_ouvre_une_sortie_d_especes()
    {
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        Press(window, PhysicalKey.F3);
        await SettleAsync();
        var view = CashViewOf(window);
        var viewModel = Assert.IsType<CashViewModel>(shell.CurrentModule);

        Press(window, PhysicalKey.Tab, RawInputModifiers.Control);
        await SettleAsync();
        Assert.True(viewModel.IsSessionTab);
        Assert.True(Shown(view, "SessionPanel"));
        Assert.Equal("Caisse 1 — OUVERTE", Text(view, "CashStateLabel"));
        Assert.Equal("5 000,00 DA", Text(view, "ExpectedCashLabel"));

        Press(window, PhysicalKey.F9);
        await SettleAsync();
        Assert.True(Shown(view, "MovementInputPanel"));
        Assert.True(view.FindControl<TextBox>("MovementAmountBox")!.IsFocused);
        window.KeyTextInput("250,75");
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.True(view.FindControl<TextBox>("MovementReasonBox")!.IsFocused);
        window.KeyTextInput("Timbres");
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.True(Shown(view, "MovementConfirmPanel"));
        Assert.Empty(server.MovementRequests);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        var request = Assert.Single(server.MovementRequests);
        Assert.Equal(CashMovementKinds.Out, request.Kind);
        Assert.Equal(250.75m, request.Amount);
        Assert.Equal("Timbres", request.Reason);
        Assert.Equal("4 749,25 DA", Text(view, "ExpectedCashLabel"));

        // Ctrl+Tab encore : la clôture, vide, puis retour aux impayés.
        Press(window, PhysicalKey.Tab, RawInputModifiers.Control);
        await SettleAsync();
        Assert.True(Shown(view, "ClosingPanel"));
        Assert.Equal(CashViewModel.ClosingNotAvailable, Text(view, "ClosingLabel"));
        Press(window, PhysicalKey.Tab, RawInputModifiers.Control);
        await SettleAsync();
        Assert.True(Shown(view, "ReceivablesPanel"));
        Assert.True(view.FindControl<TextBox>("SearchBox")!.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Apres_un_verrouillage_rien_de_l_employe_precedent_ne_reste_affiche()
    {
        var ticket = server.AddReceivable(FakeServer.Benali, 3500m);
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        Press(window, PhysicalKey.F3);
        await SettleAsync();
        var firstView = CashViewOf(window);
        var firstModel = Assert.IsType<CashViewModel>(shell.CurrentModule);
        await ScanAsync(window, ticket.TicketBarcode);
        Press(window, PhysicalKey.NumPadMultiply);
        await SettleAsync();
        Assert.Equal(SettlementStep.CashAmount, firstModel.Step);

        Press(window, PhysicalKey.F12);
        await SettleAsync();
        Assert.Null(window.FindControl<ContentControl>("ModuleHost")!.Content);
        Assert.Null(shell.CurrentModule);

        await IdentifyEmployeeAsync(window);
        Press(window, PhysicalKey.F3);
        await SettleAsync();

        var nextView = CashViewOf(window);
        var nextModel = Assert.IsType<CashViewModel>(shell.CurrentModule);
        Assert.NotSame(firstView, nextView);
        Assert.NotSame(firstModel, nextModel);
        Assert.Equal(SettlementStep.None, nextModel.Step);
        Assert.Equal(0, nextModel.SelectionCount);
        Assert.False(nextModel.HasCustomerFilter);
        Assert.False(Shown(nextView, "StepOverlay"));
        Assert.Empty(server.SettleRequests);
        window.Close();
    }
}
