using Avalonia.Controls;
using Pharmaly.Domain;
using Pharmaly.Poste.Modules;
using Pharmaly.Poste.Modules.Administration;
using Pharmaly.Poste.Modules.Diagnostic;
using Pharmaly.Poste.Modules.Products;
using Pharmaly.Poste.Modules.Sales;
using Pharmaly.Poste.Services;
using Pharmaly.Poste.ViewModels;

namespace Pharmaly.Tests.Poste;

/// <summary>Minuterie d'inactivité pilotée par le test : aucune attente réelle.</summary>
internal sealed class ManualInactivityTimer : IInactivityTimer
{
    public event EventHandler? Elapsed;

    public bool IsRunning { get; private set; }

    public int RestartCount { get; private set; }

    public void Restart()
    {
        IsRunning = true;
        RestartCount++;
    }

    public void Stop() => IsRunning = false;

    /// <summary>Le délai d'inactivité vient de s'écouler.</summary>
    public void Elapse()
    {
        Assert.True(IsRunning, "La minuterie d'inactivité ne tourne pas.");
        IsRunning = false;
        Elapsed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class ShellViewModelTests
{
    private readonly FakeServer server = new() { HasPosteSession = true };
    private readonly ManualInactivityTimer timer = new();

    private ShellViewModel CreateShell(IReadOnlyList<ModuleDefinition>? catalog = null) =>
        new(server.Clients, timer, TimeSpan.Zero, catalog);

    private static async Task SignInAsync(ShellViewModel shell)
    {
        shell.Session.EmployeeNumber = FakeServer.EmployeeNumber.ToString();
        shell.Session.EmployeeSecret = FakeServer.EmployeeSecret;
        await shell.Session.SignInEmployeeAsync();
        Assert.Equal(SessionStage.Work, shell.Session.Stage);
    }

    private static string[] VisibleTitles(ShellViewModel shell) => shell.Modules.Select(entry => entry.Title).ToArray();

    [Fact]
    public void Le_catalogue_contient_les_six_modules_dans_l_ordre_de_la_barre() =>
        Assert.Equal(
            ["Vente", "Bons fournisseurs", "Caisse", "Produits", "Administration", "Diagnostic"],
            ModuleCatalog.All.Select(module => module.Title));

    [Fact]
    public void Chaque_droit_du_cahier_des_charges_ouvre_au_moins_un_module() =>
        Assert.All(Permissions.All, permission =>
            Assert.Contains(ModuleCatalog.All, module => module.Permissions.Contains(permission.Code)));

    [Fact]
    public void Avant_l_identification_aucun_module_n_est_visible_ni_ouvert()
    {
        var shell = CreateShell();

        Assert.Empty(shell.Modules);
        Assert.Null(shell.CurrentModule);
        Assert.False(timer.IsRunning);
    }

    [Fact]
    public async Task Avec_tous_les_droits_les_six_modules_sont_visibles_avec_leur_touche()
    {
        server.AsAdministrator();
        var shell = CreateShell();

        await SignInAsync(shell);

        Assert.Equal(["Vente", "Bons fournisseurs", "Caisse", "Produits", "Administration", "Diagnostic"], VisibleTitles(shell));
        Assert.Equal(["F1", "F2", "F3", "F4", "F5", "F6"], shell.Modules.Select(entry => entry.ShortcutLabel));
        Assert.False(shell.HasNoModule);
    }

    [Theory]
    [InlineData(Permissions.SalesCreate, "Vente")]
    [InlineData(Permissions.DiscountsApply, "Vente")]
    [InlineData(Permissions.NotesView, "Bons fournisseurs")]
    [InlineData(Permissions.NotesCancel, "Bons fournisseurs")]
    [InlineData(Permissions.RegisterClose, "Caisse")]
    [InlineData(Permissions.ReceivablesSettle, "Caisse")]
    [InlineData(Permissions.SalePriceEdit, "Produits")]
    [InlineData(Permissions.UsersAdminister, "Administration")]
    public async Task Un_seul_droit_ne_montre_que_le_module_qu_il_concerne(string permission, string expectedModule)
    {
        server.EmployeePermissions = [permission];
        var shell = CreateShell();

        await SignInAsync(shell);

        Assert.Equal([expectedModule], VisibleTitles(shell));
    }

    [Fact]
    public async Task Le_droit_de_consulter_les_produits_montre_Produits_et_Diagnostic_avec_des_touches_stables()
    {
        var shell = CreateShell();

        await SignInAsync(shell);

        Assert.Equal(["Produits", "Diagnostic"], VisibleTitles(shell));
        // La touche d'un module ne dépend pas des droits : Produits reste sur F4, Diagnostic sur F6.
        Assert.Equal(["F4", "F6"], shell.Modules.Select(entry => entry.ShortcutLabel));
    }

    [Fact]
    public async Task Sans_aucun_droit_aucun_module_n_apparait_et_le_poste_le_dit()
    {
        server.EmployeePermissions = [];
        var shell = CreateShell();

        await SignInAsync(shell);

        Assert.Empty(shell.Modules);
        Assert.True(shell.HasNoModule);
        Assert.Null(shell.CurrentModule);
    }

    [Fact]
    public async Task L_identification_ouvre_le_premier_module_visible_et_le_marque_dans_la_barre()
    {
        server.AsAdministrator();
        var shell = CreateShell();

        await SignInAsync(shell);

        Assert.IsType<SalesViewModel>(shell.CurrentModule);
        Assert.Equal(ModuleKeys.Sales, shell.CurrentDefinition!.Key);
        Assert.Equal("Vente", Assert.Single(shell.Modules, entry => entry.IsCurrent).Title);
    }

    [Fact]
    public async Task Un_module_n_est_construit_qu_a_sa_premiere_ouverture_puis_reutilise()
    {
        var built = new List<string>();
        ModuleDefinition Counted(ModuleDefinition module) => module with
        {
            CreateViewModel = context =>
            {
                built.Add(module.Key);
                return module.CreateViewModel(context);
            },
        };
        server.AsAdministrator();
        var shell = CreateShell(ModuleCatalog.All.Select(Counted).ToList());

        await SignInAsync(shell);
        Assert.Equal([ModuleKeys.Sales], built);

        await shell.OpenByShortcutAsync(4);
        var administration = Assert.IsType<AdministrationViewModel>(shell.CurrentModule);
        Assert.Equal(2, administration.Employees.Count);
        await shell.OpenByShortcutAsync(0);
        await shell.OpenByShortcutAsync(4);

        Assert.Same(administration, shell.CurrentModule);
        Assert.Equal([ModuleKeys.Sales, ModuleKeys.Administration], built);
        Assert.Equal("Administration", Assert.Single(shell.Modules, entry => entry.IsCurrent).Title);
    }

    [Fact]
    public async Task Le_bouton_de_la_barre_ouvre_son_module()
    {
        var shell = CreateShell();
        await SignInAsync(shell);

        await shell.Modules.Single(entry => entry.Title == "Diagnostic").OpenCommand.ExecuteAsync(null);

        Assert.IsType<DiagnosticViewModel>(shell.CurrentModule);
    }

    [Fact]
    public async Task La_touche_d_un_module_non_visible_ne_l_ouvre_pas()
    {
        var shell = CreateShell();
        await SignInAsync(shell);

        await shell.OpenByShortcutAsync(4);
        await shell.OpenByShortcutAsync(99);
        var opened = await shell.OpenAsync(ModuleKeys.Administration);

        Assert.Null(opened);
        Assert.IsType<ProductsViewModel>(shell.CurrentModule);
    }

    [Fact]
    public async Task Verrouiller_revient_a_l_identification_et_detruit_tous_les_modules()
    {
        server.AsAdministrator();
        var shell = CreateShell();
        await SignInAsync(shell);
        var diagnostic = (DiagnosticViewModel)(await shell.OpenAsync(ModuleKeys.Diagnostic))!;
        diagnostic.Query = FakeServer.Creme.Barcode!;
        await diagnostic.SubmitAsync();
        Assert.Single(diagnostic.Lines);
        var administration = (AdministrationViewModel)(await shell.OpenAsync(ModuleKeys.Administration))!;
        administration.NewEmployeeName = "Saisie en cours";

        await shell.LockAsync();

        Assert.Equal(SessionStage.EmployeeSignIn, shell.Session.Stage);
        Assert.Null(shell.CurrentModule);
        Assert.Null(shell.CurrentDefinition);
        Assert.Empty(shell.Modules);
        Assert.Equal(string.Empty, shell.Session.EmployeeName);
        Assert.Empty(shell.Session.Permissions);
        Assert.Equal(Guid.Empty, shell.Session.EmployeeId);
        Assert.False(server.EmployeeIdentified);

        // L'employé suivant reçoit des modules neufs : rien de la saisie précédente.
        await SignInAsync(shell);
        var nextDiagnostic = (DiagnosticViewModel)(await shell.OpenAsync(ModuleKeys.Diagnostic))!;
        var nextAdministration = (AdministrationViewModel)(await shell.OpenAsync(ModuleKeys.Administration))!;
        Assert.NotSame(diagnostic, nextDiagnostic);
        Assert.Empty(nextDiagnostic.Lines);
        Assert.NotSame(administration, nextAdministration);
        Assert.Equal(string.Empty, nextAdministration.NewEmployeeName);
    }

    [Fact]
    public async Task Au_verrouillage_chaque_module_ouvert_est_averti_pour_arreter_ce_qui_tourne()
    {
        var module = new ClosableModule();
        var catalog = new[]
        {
            new ModuleDefinition("essai", "Essai", [Permissions.ProductsView], _ => module, () => new UserControl()),
        };
        var shell = CreateShell(catalog);
        await SignInAsync(shell);
        Assert.Same(module, shell.CurrentModule);
        Assert.Equal(1, module.Activations);

        await shell.LockAsync();

        Assert.True(module.IsClosed);
    }

    [Fact]
    public void Le_delai_d_inactivite_est_de_cinq_minutes() =>
        Assert.Equal(TimeSpan.FromMinutes(5), ShellViewModel.InactivityLockDelay);

    [Fact]
    public async Task Sans_frappe_ni_souris_pendant_le_delai_le_poste_se_verrouille()
    {
        server.AsAdministrator();
        var shell = CreateShell();
        await SignInAsync(shell);
        await shell.OpenAsync(ModuleKeys.Administration);
        Assert.True(timer.IsRunning);

        timer.Elapse();

        Assert.Equal(SessionStage.EmployeeSignIn, shell.Session.Stage);
        Assert.Null(shell.CurrentModule);
        Assert.Empty(shell.Modules);
        Assert.False(server.EmployeeIdentified);
        Assert.False(timer.IsRunning);
    }

    [Fact]
    public async Task Chaque_frappe_ou_geste_de_souris_fait_repartir_le_delai_d_inactivite()
    {
        var shell = CreateShell();
        await SignInAsync(shell);
        var restarts = timer.RestartCount;

        shell.NotifyActivity();
        shell.NotifyActivity();

        Assert.Equal(restarts + 2, timer.RestartCount);
        Assert.Equal(SessionStage.Work, shell.Session.Stage);
    }

    [Fact]
    public async Task Poste_verrouille_la_minuterie_d_inactivite_est_arretee_et_l_activite_ne_la_relance_pas()
    {
        var shell = CreateShell();
        shell.NotifyActivity();
        Assert.False(timer.IsRunning);

        await SignInAsync(shell);
        await shell.LockAsync();
        shell.NotifyActivity();

        Assert.False(timer.IsRunning);
    }

    [Fact]
    public async Task Le_verrouillage_par_inactivite_fonctionne_meme_si_le_serveur_est_coupe()
    {
        var shell = CreateShell();
        await SignInAsync(shell);
        server.IsDown = true;

        timer.Elapse();

        Assert.Equal(SessionStage.EmployeeSignIn, shell.Session.Stage);
        Assert.Null(shell.CurrentModule);
    }

    [Fact]
    public async Task Un_module_qui_perd_sa_session_renvoie_a_l_identification()
    {
        var shell = CreateShell();
        await SignInAsync(shell);
        var diagnostic = (DiagnosticViewModel)(await shell.OpenAsync(ModuleKeys.Diagnostic))!;
        server.EmployeeIdentified = false;

        diagnostic.Query = FakeServer.Creme.Barcode!;
        await diagnostic.SubmitAsync();

        Assert.Equal(SessionStage.EmployeeSignIn, shell.Session.Stage);
        Assert.Null(shell.CurrentModule);
    }

    private sealed class ClosableModule : ModuleViewModel
    {
        public int Activations { get; private set; }

        public bool IsClosed { get; private set; }

        public override Task ActivateAsync()
        {
            Activations++;
            return Task.CompletedTask;
        }

        public override void Close() => IsClosed = true;
    }
}
