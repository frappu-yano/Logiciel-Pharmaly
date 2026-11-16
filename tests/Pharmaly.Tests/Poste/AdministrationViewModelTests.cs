using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Modules;
using Pharmaly.Poste.Modules.Administration;

namespace Pharmaly.Tests.Poste;

public sealed class AdministrationViewModelTests
{
    private readonly FakeServer server = new FakeServer { HasPosteSession = true, EmployeeIdentified = true }.AsAdministrator();

    private async Task<AdministrationViewModel> OpenAsync(Guid? currentEmployeeId = null)
    {
        var viewModel = new AdministrationViewModel(server, currentEmployeeId ?? FakeServer.EmployeeId);
        await viewModel.ActivateAsync();
        return viewModel;
    }

    private static PermissionChoiceViewModel Choice(AdministrationViewModel viewModel, string code) =>
        viewModel.PermissionGroups.SelectMany(group => group.Permissions).Single(choice => choice.Definition.Code == code);

    private static void Select(AdministrationViewModel viewModel, Guid employeeId) =>
        viewModel.SelectedEmployee = viewModel.Employees.Single(row => row.Id == employeeId);

    [Fact]
    public async Task L_ouverture_liste_les_employes_avec_matricule_nom_et_etat()
    {
        server.Employees.Add(new EmployeeSummary(Guid.NewGuid(), 3, "Zohra Ancienne", false, []));

        var viewModel = await OpenAsync();

        Assert.Equal(["7", "12", "3"], viewModel.Employees.Select(row => row.NumberText));
        Assert.Equal([FakeServer.KarimName, FakeServer.EmployeeName, "Zohra Ancienne"], viewModel.Employees.Select(row => row.FullName));
        Assert.Equal([false, true, false], viewModel.Employees.Select(row => row.IsSelf));
        Assert.Equal(["Actif", "Actif", "Inactif"], viewModel.Employees.Select(row => row.StateText));
        Assert.Equal(FakeServer.KarimId, viewModel.SelectedEmployee!.Id);
        Assert.Equal(string.Empty, viewModel.ErrorMessage);
    }

    [Fact]
    public async Task La_fiche_montre_une_case_par_droit_groupees_comme_dans_la_liste_des_droits()
    {
        var viewModel = await OpenAsync();

        Assert.Equal(
            Permissions.All.Select(permission => permission.Group).Distinct(),
            viewModel.PermissionGroups.Select(group => group.Name));
        Assert.Equal(
            Permissions.All.Select(permission => permission.Code),
            viewModel.PermissionGroups.SelectMany(group => group.Permissions).Select(choice => choice.Definition.Code));
        Assert.True(Choice(viewModel, Permissions.SalesCreate).IsGranted);
        Assert.False(Choice(viewModel, Permissions.SalesCancel).IsGranted);
        Assert.Equal(
            ["Tous les sites", "Pharmaly 1 — Didouche", "Pharmaly 2 — Audin"],
            Choice(viewModel, Permissions.SalesCreate).Scopes.Select(scope => scope.Label));
    }

    [Fact]
    public async Task Creer_un_employe_envoie_matricule_nom_et_code_puis_ouvre_sa_fiche()
    {
        var viewModel = await OpenAsync();
        viewModel.NewEmployeeNumber = " 21 ";
        viewModel.NewEmployeeName = " Lina Nouvelle ";
        viewModel.NewEmployeeSecret = "9631";

        await viewModel.CreateEmployeeAsync();

        Assert.Equal(new CreateEmployeeRequest(21, "Lina Nouvelle", "9631"), Assert.Single(server.CreateRequests));
        Assert.Equal("Lina Nouvelle", viewModel.SelectedEmployee!.Employee.FullName);
        Assert.Equal(3, viewModel.Employees.Count);
        Assert.All(viewModel.PermissionGroups.SelectMany(group => group.Permissions), choice => Assert.False(choice.IsGranted));
        Assert.Equal(string.Empty, viewModel.NewEmployeeNumber);
        Assert.Equal(string.Empty, viewModel.NewEmployeeName);
        Assert.Equal(string.Empty, viewModel.NewEmployeeSecret);
        Assert.Contains("Lina Nouvelle est créé", viewModel.InfoMessage);
        Assert.Equal(string.Empty, viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Un_matricule_qui_n_est_pas_un_nombre_n_est_pas_envoye()
    {
        var viewModel = await OpenAsync();
        viewModel.NewEmployeeNumber = "douze";
        viewModel.NewEmployeeName = "Lina Nouvelle";
        viewModel.NewEmployeeSecret = "9631";

        await viewModel.CreateEmployeeAsync();

        Assert.Empty(server.CreateRequests);
        Assert.Equal("Le matricule doit être un nombre.", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Le_refus_du_serveur_a_la_creation_est_affiche_tel_quel_et_la_saisie_est_conservee()
    {
        var viewModel = await OpenAsync();
        viewModel.NewEmployeeNumber = FakeServer.KarimNumber.ToString();
        viewModel.NewEmployeeName = "Lina Nouvelle";
        viewModel.NewEmployeeSecret = "9631";

        await viewModel.CreateEmployeeAsync();

        Assert.Equal("Ce matricule est déjà attribué.", viewModel.ErrorMessage);
        Assert.Equal(string.Empty, viewModel.InfoMessage);
        Assert.Equal("Lina Nouvelle", viewModel.NewEmployeeName);
        Assert.Equal(2, viewModel.Employees.Count);
    }

    [Fact]
    public async Task Un_code_personnel_trop_court_est_refuse_par_le_serveur_et_le_message_est_affiche()
    {
        var viewModel = await OpenAsync();
        viewModel.NewEmployeeNumber = "21";
        viewModel.NewEmployeeName = "Lina Nouvelle";
        viewModel.NewEmployeeSecret = "12";

        await viewModel.CreateEmployeeAsync();

        Assert.Equal("Matricule, nom et code personnel (4 caractères minimum) sont obligatoires.", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Cocher_un_droit_puis_enregistrer_envoie_exactement_l_ensemble_des_droits_coches()
    {
        var viewModel = await OpenAsync();
        Assert.False(viewModel.HasUnsavedChanges);

        Choice(viewModel, Permissions.PaymentsCollect).IsGranted = true;
        Choice(viewModel, Permissions.ProductsView).IsGranted = true;
        Assert.True(viewModel.HasUnsavedChanges);
        await viewModel.SavePermissionsAsync();

        var (employeeId, sent) = Assert.Single(server.PermissionRequests);
        Assert.Equal(FakeServer.KarimId, employeeId);
        Assert.Equal(
            [
                new PermissionGrant(Permissions.ProductsView, null),
                new PermissionGrant(Permissions.SalesCreate, null),
                new PermissionGrant(Permissions.PaymentsCollect, null),
            ],
            sent);
        Assert.Equal($"Droits de {FakeServer.KarimName} enregistrés.", viewModel.InfoMessage);
        Assert.False(viewModel.HasUnsavedChanges);
        // La fiche est relue depuis le serveur : elle montre ce qui est réellement enregistré.
        Assert.Equal(FakeServer.KarimId, viewModel.SelectedEmployee!.Id);
        Assert.True(Choice(viewModel, Permissions.PaymentsCollect).IsGranted);
    }

    [Fact]
    public async Task Decocher_un_droit_le_retire_de_l_envoi()
    {
        var viewModel = await OpenAsync();

        Choice(viewModel, Permissions.SalesCreate).IsGranted = false;
        await viewModel.SavePermissionsAsync();

        Assert.Empty(Assert.Single(server.PermissionRequests).Permissions);
    }

    [Fact]
    public async Task Limiter_un_droit_a_un_site_envoie_l_identifiant_du_site()
    {
        var viewModel = await OpenAsync();
        var sell = Choice(viewModel, Permissions.SalesCreate);
        var discount = Choice(viewModel, Permissions.DiscountsApply);
        Assert.False(discount.CanChooseScope);

        sell.Scope = sell.Scopes.Single(scope => scope.StoreId == FakeServer.Audin.Id);
        discount.IsGranted = true;
        Assert.True(discount.CanChooseScope);
        await viewModel.SavePermissionsAsync();

        Assert.Equal(
            [
                new PermissionGrant(Permissions.SalesCreate, FakeServer.Audin.Id),
                new PermissionGrant(Permissions.DiscountsApply, null),
            ],
            Assert.Single(server.PermissionRequests).Permissions);
        Assert.Equal(FakeServer.Audin.Id, Choice(viewModel, Permissions.SalesCreate).Scope.StoreId);
    }

    [Fact]
    public async Task Un_droit_deja_limite_a_plusieurs_sites_est_renvoye_sans_changement()
    {
        var limited = new[]
        {
            new PermissionGrant(Permissions.SalesCreate, FakeServer.Didouche.Id),
            new PermissionGrant(Permissions.SalesCreate, FakeServer.Audin.Id),
        };
        server.Employees[0] = server.Employees[0] with { Permissions = limited };
        var viewModel = await OpenAsync();

        Assert.True(Choice(viewModel, Permissions.SalesCreate).Scope.KeepsExisting);
        Choice(viewModel, Permissions.ProductsView).IsGranted = true;
        await viewModel.SavePermissionsAsync();

        Assert.Equal(
            [new PermissionGrant(Permissions.ProductsView, null), .. limited],
            Assert.Single(server.PermissionRequests).Permissions);
    }

    [Fact]
    public async Task L_employe_identifie_ne_peut_ni_modifier_ses_droits_ni_se_desactiver()
    {
        var viewModel = await OpenAsync();

        Select(viewModel, FakeServer.EmployeeId);

        Assert.True(viewModel.IsSelfSelected);
        Assert.False(viewModel.CanEditSelection);
        Assert.False(viewModel.SavePermissionsCommand.CanExecute(null));
        Assert.False(viewModel.ToggleStateCommand.CanExecute(null));
        Assert.All(viewModel.PermissionGroups.SelectMany(group => group.Permissions), choice =>
        {
            Assert.False(choice.IsEditable);
            Assert.False(choice.CanChooseScope);
        });
        Assert.Contains("vos propres droits", AdministrationViewModel.SelfExplanation);

        // Même appelées directement, les commandes n'envoient rien et expliquent pourquoi.
        await viewModel.SavePermissionsAsync();
        Assert.Equal(AdministrationViewModel.SelfExplanation, viewModel.ErrorMessage);
        await viewModel.ToggleStateAsync();
        Assert.Null(viewModel.Confirmation);
        Assert.Empty(server.PermissionRequests);
        Assert.Empty(server.StateRequests);
    }

    [Fact]
    public async Task Un_autre_employe_reste_modifiable()
    {
        var viewModel = await OpenAsync();

        Select(viewModel, FakeServer.KarimId);

        Assert.False(viewModel.IsSelfSelected);
        Assert.True(viewModel.SavePermissionsCommand.CanExecute(null));
        Assert.True(viewModel.ToggleStateCommand.CanExecute(null));
        Assert.All(viewModel.PermissionGroups.SelectMany(group => group.Permissions), choice => Assert.True(choice.IsEditable));
    }

    [Fact]
    public async Task Si_le_serveur_refuse_quand_meme_une_modification_de_soi_son_message_est_affiche()
    {
        // Le poste se trompe sur l'identité de l'employé : il laisse partir la demande, le serveur la refuse.
        var viewModel = await OpenAsync(currentEmployeeId: Guid.NewGuid());
        Select(viewModel, FakeServer.EmployeeId);

        Choice(viewModel, Permissions.SalesCreate).IsGranted = true;
        await viewModel.SavePermissionsAsync();

        Assert.Single(server.PermissionRequests);
        Assert.Equal("Vous ne pouvez pas modifier vos propres droits.", viewModel.ErrorMessage);
        Assert.Equal(string.Empty, viewModel.InfoMessage);
        // Rien n'a changé sur le serveur, et les cases cochées restent à l'écran pour correction.
        Assert.Equal([new PermissionGrant(Permissions.UsersAdminister, null)], server.Employees[1].Permissions);
        Assert.True(Choice(viewModel, Permissions.SalesCreate).IsGranted);

        await viewModel.ToggleStateAsync();
        await viewModel.ConfirmAsync();
        Assert.Equal("Vous ne pouvez pas désactiver votre propre compte.", viewModel.ErrorMessage);
        Assert.True(server.Employees[1].IsActive);
    }

    [Fact]
    public async Task Desactiver_un_employe_demande_une_confirmation_et_annuler_n_envoie_rien()
    {
        var viewModel = await OpenAsync();

        await viewModel.ToggleStateAsync();

        Assert.True(viewModel.IsConfirming);
        Assert.Contains(FakeServer.KarimName, viewModel.Confirmation!.Message);
        Assert.Empty(server.StateRequests);

        viewModel.CancelConfirmation();

        Assert.False(viewModel.IsConfirming);
        Assert.Empty(server.StateRequests);
        Assert.True(viewModel.SelectedEmployee!.IsActive);
    }

    [Fact]
    public async Task Confirmer_la_desactivation_l_envoie_et_la_reactivation_ne_demande_pas_de_confirmation()
    {
        var viewModel = await OpenAsync();

        await viewModel.ToggleStateAsync();
        await viewModel.ConfirmAsync();

        Assert.Equal([(FakeServer.KarimId, false)], server.StateRequests);
        Assert.False(viewModel.IsConfirming);
        Assert.Equal("Inactif", viewModel.SelectedStateText);
        Assert.Equal("Réactiver l'employé", viewModel.ToggleStateLabel);
        Assert.Equal($"{FakeServer.KarimName} est désactivé.", viewModel.InfoMessage);

        await viewModel.ToggleStateAsync();

        Assert.False(viewModel.IsConfirming);
        Assert.Equal([(FakeServer.KarimId, false), (FakeServer.KarimId, true)], server.StateRequests);
        Assert.Equal("Actif", viewModel.SelectedStateText);
    }

    [Fact]
    public async Task Remplacer_le_code_personnel_demande_une_confirmation_puis_envoie_le_nouveau_code()
    {
        var viewModel = await OpenAsync();

        viewModel.RequestSecretReset();
        Assert.False(viewModel.IsConfirming);
        Assert.Equal("Saisissez le nouveau code personnel.", viewModel.ErrorMessage);

        viewModel.NewSecret = "7410";
        viewModel.RequestSecretReset();
        Assert.True(viewModel.IsConfirming);
        Assert.Empty(server.SecretRequests);

        await viewModel.ConfirmAsync();

        Assert.Equal([(FakeServer.KarimId, "7410")], server.SecretRequests);
        Assert.Equal(string.Empty, viewModel.NewSecret);
        Assert.Contains("est remplacé", viewModel.InfoMessage);
    }

    [Fact]
    public async Task Un_nouveau_code_refuse_par_le_serveur_affiche_son_message()
    {
        var viewModel = await OpenAsync();
        viewModel.NewSecret = "12";

        viewModel.RequestSecretReset();
        await viewModel.ConfirmAsync();

        Assert.Equal("Le code personnel doit contenir au moins 4 caractères.", viewModel.ErrorMessage);
        Assert.Equal(string.Empty, viewModel.InfoMessage);
    }

    [Fact]
    public async Task Changer_d_employe_efface_le_code_saisi_et_les_messages()
    {
        var viewModel = await OpenAsync();
        viewModel.NewSecret = "7410";
        Choice(viewModel, Permissions.SalesCancel).IsGranted = true;

        Select(viewModel, FakeServer.EmployeeId);

        Assert.Equal(string.Empty, viewModel.NewSecret);
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.True(Choice(viewModel, Permissions.UsersAdminister).IsGranted);
        Assert.False(Choice(viewModel, Permissions.SalesCancel).IsGranted);
    }

    [Fact]
    public async Task Sans_le_droit_d_administrer_le_refus_du_serveur_est_affiche()
    {
        server.EmployeePermissions = [Permissions.ProductsView];

        var viewModel = await OpenAsync();

        Assert.Empty(viewModel.Employees);
        Assert.Equal("Vous n'avez pas le droit d'effectuer cette action.", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Serveur_coupe_pendant_l_enregistrement_le_poste_l_explique_et_garde_les_cases()
    {
        var viewModel = await OpenAsync();
        Choice(viewModel, Permissions.SalesCancel).IsGranted = true;
        server.IsDown = true;

        await viewModel.SavePermissionsAsync();

        Assert.Equal(ModuleViewModel.ServerUnreachableMessage, viewModel.ErrorMessage);
        Assert.True(Choice(viewModel, Permissions.SalesCancel).IsGranted);
        Assert.True(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public async Task Une_session_perdue_redemande_l_identification()
    {
        var viewModel = await OpenAsync();
        var asked = false;
        viewModel.IdentificationRequired += (_, _) => asked = true;
        server.EmployeeIdentified = false;

        await viewModel.SavePermissionsAsync();

        Assert.True(asked);
    }
}
