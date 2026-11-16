using Avalonia;
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
using Pharmaly.Poste.Modules.Diagnostic;
using Pharmaly.Poste.Modules.Products;
using Pharmaly.Poste.Modules.Sales;
using Pharmaly.Poste.Services;
using Pharmaly.Poste.ViewModels;
using Pharmaly.Poste.Views;
using Pharmaly.Tests.Poste;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Pharmaly.Tests.Poste;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class MainWindowTests
{
    private readonly FakeServer server = new();
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

    /// <summary>Identification complète au clavier, comme le fait un employé à l'ouverture du poste.</summary>
    private static async Task SignInWithKeyboardAsync(MainWindow window)
    {
        await SettleAsync();
        Assert.True(window.FindControl<TextBox>("RegisterLoginBox")!.IsFocused);
        Assert.False(window.FindControl<Control>("WorkArea")!.IsVisible);
        window.KeyTextInput(FakeServer.RegisterLogin);
        Press(window, PhysicalKey.Enter);
        window.KeyTextInput(FakeServer.RegisterPassword);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        Assert.True(window.FindControl<TextBox>("EmployeeNumberBox")!.IsFocused);
        window.KeyTextInput(FakeServer.EmployeeNumber.ToString());
        Press(window, PhysicalKey.Enter);
        window.KeyTextInput(FakeServer.EmployeeSecret);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
    }

    private static TView ModuleView<TView>(MainWindow window)
        where TView : Control =>
        Assert.IsType<TView>(window.FindControl<ContentControl>("ModuleHost")!.Content);

    private static string[] ModuleBarTitles(MainWindow window) =>
        window.FindControl<ItemsControl>("ModuleBar")!.GetVisualDescendants().OfType<Button>()
            .Select(button => ((ModuleEntryViewModel)button.DataContext!).Title).ToArray();

    [AvaloniaFact]
    public async Task Identification_au_clavier_puis_scan_puis_verrouillage()
    {
        var (window, shell) = OpenWindow();
        var session = shell.Session;
        await SettleAsync();

        // Niveau 1 : compte de caisse.
        Assert.True(window.FindControl<TextBox>("RegisterLoginBox")!.IsFocused);
        Assert.False(window.FindControl<Control>("WorkArea")!.IsVisible);
        window.KeyTextInput(FakeServer.RegisterLogin);
        Press(window, PhysicalKey.Enter);
        window.KeyTextInput(FakeServer.RegisterPassword);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        // Niveau 2 : employé.
        Assert.Equal(SessionStage.EmployeeSignIn, session.Stage);
        Assert.True(window.FindControl<TextBox>("EmployeeNumberBox")!.IsFocused);
        window.KeyTextInput(FakeServer.EmployeeNumber.ToString());
        Press(window, PhysicalKey.Enter);
        window.KeyTextInput(FakeServer.EmployeeSecret);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        Assert.Equal(SessionStage.Work, session.Stage);
        Assert.True(window.FindControl<Control>("WorkArea")!.IsVisible);
        Assert.Equal("Samia Test", window.FindControl<TextBlock>("EmployeeLabel")!.Text);

        // Avec le seul droit de consulter les produits : Produits (F4) puis Diagnostic (F6).
        Assert.Equal(["Produits", "Diagnostic"], ModuleBarTitles(window));
        ModuleView<ProductsView>(window);

        // F6 ouvre le module Diagnostic et place le curseur dans la recherche.
        Press(window, PhysicalKey.F6);
        await SettleAsync();
        var diagnosticView = ModuleView<DiagnosticView>(window);
        var viewModel = Assert.IsType<DiagnosticViewModel>(shell.CurrentModule);
        var searchBox = diagnosticView.FindControl<TextBox>("SearchBox")!;
        Assert.True(searchBox.IsFocused);

        // Un lecteur de codes-barres envoie les chiffres puis Entrée, comme un clavier.
        window.KeyTextInput(FakeServer.Creme.Barcode!);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        Assert.Single(viewModel.Lines);
        Assert.Equal("1 850,10 DA", diagnosticView.FindControl<TextBlock>("TotalLabel")!.Text);
        Assert.True(searchBox.IsFocused);

        // F12 verrouille : l'écran de travail disparaît, le module et ses lignes n'existent plus.
        Press(window, PhysicalKey.F12);
        await SettleAsync();

        Assert.Equal(SessionStage.EmployeeSignIn, session.Stage);
        Assert.False(window.FindControl<Control>("WorkArea")!.IsVisible);
        Assert.Null(window.FindControl<ContentControl>("ModuleHost")!.Content);
        Assert.Null(shell.CurrentModule);
        Assert.Empty(ModuleBarTitles(window));
        Assert.Equal(string.Empty, window.FindControl<TextBlock>("EmployeeLabel")!.Text ?? string.Empty);
        Assert.True(window.FindControl<TextBox>("EmployeeNumberBox")!.IsFocused);

        // L'employé suivant retrouve un module Diagnostic vide.
        window.KeyTextInput(FakeServer.EmployeeNumber.ToString());
        Press(window, PhysicalKey.Enter);
        window.KeyTextInput(FakeServer.EmployeeSecret);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Press(window, PhysicalKey.F6);
        await SettleAsync();
        var nextView = ModuleView<DiagnosticView>(window);
        Assert.NotSame(diagnosticView, nextView);
        Assert.Empty(Assert.IsType<DiagnosticViewModel>(shell.CurrentModule).Lines);
        Assert.Equal("0,00 DA", nextView.FindControl<TextBlock>("TotalLabel")!.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Administration_au_clavier_cocher_un_droit_puis_enregistrer()
    {
        server.AsAdministrator();
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);

        // Tous les droits : les six modules, et l'employé arrive sur le premier (état vide).
        Assert.Equal(["Vente", "Bons fournisseurs", "Caisse", "Produits", "Administration", "Diagnostic"], ModuleBarTitles(window));
        var salesView = ModuleView<SalesView>(window);
        Assert.Equal("Module en cours de développement", salesView.FindControl<TextBlock>("EmptyStateLabel")!.Text);

        // F5 : Administration. Le curseur est sur la liste, premier employé sélectionné.
        Press(window, PhysicalKey.F5);
        await SettleAsync();
        var view = ModuleView<AdministrationView>(window);
        var viewModel = Assert.IsType<AdministrationViewModel>(shell.CurrentModule);
        var list = view.FindControl<ListBox>("EmployeeList")!;
        Assert.True(list.IsKeyboardFocusWithin);
        Assert.Equal(FakeServer.KarimId, viewModel.SelectedEmployee!.Id);
        Assert.Equal($"{FakeServer.KarimName} — matricule 7", view.FindControl<TextBlock>("SelectedTitleLabel")!.Text);

        // Entrée : le curseur passe sur la première case à cocher (« Consulter les produits »).
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        var firstBox = view.GetVisualDescendants().OfType<CheckBox>().First();
        Assert.True(firstBox.IsFocused);
        Assert.Equal("Consulter les produits", firstBox.Content);
        Assert.False(firstBox.IsChecked);

        // Espace coche la case.
        Press(window, PhysicalKey.Space);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        await SettleAsync();
        Assert.True(firstBox.IsChecked);
        Assert.True(viewModel.HasUnsavedChanges);
        Assert.Empty(server.PermissionRequests);

        // Ctrl+S enregistre : le serveur reçoit exactement les droits cochés.
        Press(window, PhysicalKey.S, RawInputModifiers.Control);
        await SettleAsync();

        var (employeeId, sent) = Assert.Single(server.PermissionRequests);
        Assert.Equal(FakeServer.KarimId, employeeId);
        Assert.Equal(
            [new PermissionGrant(Permissions.ProductsView, null), new PermissionGrant(Permissions.SalesCreate, null)],
            sent);
        Assert.Equal($"Droits de {FakeServer.KarimName} enregistrés.", view.FindControl<TextBlock>("InfoLabel")!.Text);
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.True(list.IsKeyboardFocusWithin);

        // ↓ : l'employé identifié. Ses cases sont grisées et l'explication est affichée.
        Press(window, PhysicalKey.ArrowDown);
        await SettleAsync();
        Assert.True(viewModel.IsSelfSelected);
        Assert.True(view.FindControl<TextBlock>("SelfExplanationLabel")!.IsEffectivelyVisible);
        Assert.All(view.GetVisualDescendants().OfType<CheckBox>(), box => Assert.False(box.IsEffectivelyEnabled));
        Assert.False(view.FindControl<Button>("SaveButton")!.IsEffectivelyEnabled);
        Assert.False(view.FindControl<Button>("ToggleStateButton")!.IsEffectivelyEnabled);
        Press(window, PhysicalKey.S, RawInputModifiers.Control);
        await SettleAsync();
        Assert.Single(server.PermissionRequests);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Creation_d_un_employe_et_desactivation_confirmee_au_clavier()
    {
        server.AsAdministrator();
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        Press(window, PhysicalKey.F5);
        await SettleAsync();
        var view = ModuleView<AdministrationView>(window);
        var viewModel = Assert.IsType<AdministrationViewModel>(shell.CurrentModule);

        // Ctrl+N : matricule, nom, code, chacun validé par Entrée.
        Press(window, PhysicalKey.N, RawInputModifiers.Control);
        Assert.True(view.FindControl<TextBox>("NewNumberBox")!.IsFocused);
        window.KeyTextInput("21");
        Press(window, PhysicalKey.Enter);
        Assert.True(view.FindControl<TextBox>("NewNameBox")!.IsFocused);
        window.KeyTextInput("Lina Nouvelle");
        Press(window, PhysicalKey.Enter);
        Assert.True(view.FindControl<TextBox>("NewSecretBox")!.IsFocused);
        window.KeyTextInput("9631");
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        Assert.Equal(new CreateEmployeeRequest(21, "Lina Nouvelle", "9631"), Assert.Single(server.CreateRequests));
        Assert.Equal("Lina Nouvelle — matricule 21", view.FindControl<TextBlock>("SelectedTitleLabel")!.Text);
        Assert.True(view.FindControl<ListBox>("EmployeeList")!.IsKeyboardFocusWithin);

        // Désactivation : rien ne part avant la confirmation ; Échap annule, Entrée confirme.
        var toggle = view.FindControl<Button>("ToggleStateButton")!;
        toggle.Command!.Execute(null);
        await SettleAsync();
        Assert.True(view.FindControl<Control>("ConfirmationPanel")!.IsEffectivelyVisible);
        Assert.Contains("Lina Nouvelle", view.FindControl<TextBlock>("ConfirmationLabel")!.Text);
        Press(window, PhysicalKey.Escape);
        await SettleAsync();
        Assert.False(view.FindControl<Control>("ConfirmationPanel")!.IsEffectivelyVisible);
        Assert.Empty(server.StateRequests);

        toggle.Command!.Execute(null);
        await SettleAsync();
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.Equal(viewModel.SelectedEmployee!.Id, Assert.Single(server.StateRequests).EmployeeId);
        Assert.False(server.StateRequests[0].IsActive);
        Assert.Equal("Inactif", view.FindControl<TextBlock>("SelectedStateLabel")!.Text);
        Assert.Equal("Réactiver l'employé", toggle.Content);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Les_modules_s_ouvrent_aussi_avec_Ctrl_chiffre_et_a_la_souris()
    {
        server.AsAdministrator();
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);

        Press(window, PhysicalKey.Digit6, RawInputModifiers.Control);
        await SettleAsync();
        Assert.True(ModuleView<DiagnosticView>(window).FindControl<TextBox>("SearchBox")!.IsFocused);

        // Depuis un module sans champ de saisie, les touches de navigation restent actives.
        Press(window, PhysicalKey.F1);
        await SettleAsync();
        ModuleView<SalesView>(window);
        Press(window, PhysicalKey.F5);
        await SettleAsync();
        ModuleView<AdministrationView>(window);

        // Clic sur le bouton « Diagnostic » de la barre.
        var button = window.FindControl<ItemsControl>("ModuleBar")!.GetVisualDescendants().OfType<Button>()
            .Single(candidate => ((ModuleEntryViewModel)candidate.DataContext!).Title == "Diagnostic");
        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        await SettleAsync();

        Assert.IsType<DiagnosticViewModel>(shell.CurrentModule);
        Assert.True(ModuleView<DiagnosticView>(window).FindControl<TextBox>("SearchBox")!.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public async Task L_inactivite_verrouille_le_poste_et_la_frappe_repousse_le_delai()
    {
        server.AsAdministrator();
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        Press(window, PhysicalKey.F5);
        await SettleAsync();
        ModuleView<AdministrationView>(window);

        // Une frappe et un mouvement de souris font repartir la minuterie.
        var restarts = timer.RestartCount;
        Press(window, PhysicalKey.ArrowDown);
        Assert.Equal(restarts + 1, timer.RestartCount);
        window.MouseMove(new Point(200, 300));
        Assert.Equal(restarts + 2, timer.RestartCount);

        // Le délai s'écoule : même effet que F12.
        timer.Elapse();
        await SettleAsync();

        Assert.Equal(SessionStage.EmployeeSignIn, shell.Session.Stage);
        Assert.False(window.FindControl<Control>("WorkArea")!.IsVisible);
        Assert.Null(window.FindControl<ContentControl>("ModuleHost")!.Content);
        Assert.Empty(window.GetVisualDescendants().OfType<AdministrationView>());
        Assert.True(window.FindControl<TextBox>("EmployeeNumberBox")!.IsFocused);
        Assert.False(timer.IsRunning);
        window.Close();
    }

    [AvaloniaFact]
    public async Task La_minuterie_du_poste_se_declenche_une_fois_apres_le_delai_et_s_arrete_sur_demande()
    {
        // La vraie minuterie, avec un délai de quelques millisecondes à la place des cinq minutes.
        var realTimer = new InactivityTimer(TimeSpan.FromMilliseconds(30));
        var elapsed = 0;
        realTimer.Elapsed += (_, _) => elapsed++;

        realTimer.Restart();
        Assert.Equal(0, elapsed);
        await SettleUntilAsync(() => elapsed > 0);
        Assert.Equal(1, elapsed);
        await Task.Delay(120);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, elapsed);

        realTimer.Restart();
        realTimer.Stop();
        await Task.Delay(120);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, elapsed);
    }

    private static async Task SettleUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Sans_aucun_droit_la_fenetre_l_explique_et_F12_verrouille_toujours()
    {
        server.EmployeePermissions = [];
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);

        Assert.True(window.FindControl<Control>("NoModulePanel")!.IsEffectivelyVisible);
        Assert.Empty(ModuleBarTitles(window));

        Press(window, PhysicalKey.F12);
        await SettleAsync();

        Assert.Equal(SessionStage.EmployeeSignIn, shell.Session.Stage);
        window.Close();
    }
}
