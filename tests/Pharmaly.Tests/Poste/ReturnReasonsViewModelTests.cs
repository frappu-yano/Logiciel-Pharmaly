using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Modules.Administration;

namespace Pharmaly.Tests.Poste;

/// <summary>Onglet « motifs de retour » du module Administration, avec le faux serveur.</summary>
public sealed class ReturnReasonsViewModelTests
{
    private readonly FakeServer server = new FakeServer { HasPosteSession = true, EmployeeIdentified = true }.AsAdministrator();

    private async Task<(AdministrationViewModel Module, ReturnReasonsViewModel Reasons)> OpenAsync()
    {
        var module = new AdministrationViewModel(server, server, FakeServer.EmployeeId);
        await module.ActivateAsync();
        await module.ShowTabAsync(AdministrationTab.ReturnReasons);
        return (module, module.ReturnReasons);
    }

    private static void Select(ReturnReasonsViewModel reasons, ReturnReason reason) =>
        reasons.SelectedReason = reasons.Reasons.Single(row => row.Id == reason.Id);

    [Fact]
    public async Task L_onglet_liste_les_motifs_actifs_et_inactifs_avec_leur_destination()
    {
        server.ReturnReasons[2] = FakeServer.Defective with { IsActive = false };

        var (module, reasons) = await OpenAsync();

        Assert.True(module.IsReturnReasonsTab);
        Assert.False(module.IsEmployeesTab);
        Assert.Equal(
            ["Retour revendable", "Produit endommagé", "Produit défectueux", "Erreur de vente"], reasons.Reasons.Select(row => row.Label));
        Assert.Equal(["Actif", "Actif", "Inactif", "Actif"], reasons.Reasons.Select(row => row.StateText));
        Assert.Equal("Pas de remise en stock", reasons.Reasons[1].DestinationText);
        Assert.Equal(FakeServer.Resellable.Id, reasons.SelectedReason!.Id);
        Assert.Equal("Retour revendable", reasons.EditLabel);
        Assert.Equal(ReturnStockDestinations.Sellable, reasons.EditDestination.Code);
        Assert.Equal(
            [ReturnStockDestinations.Sellable, ReturnStockDestinations.SetAside, ReturnStockDestinations.None],
            reasons.Destinations.Select(choice => choice.Code));

        // Retour à l'onglet des employés : rien de cassé.
        await module.ShowTabAsync(AdministrationTab.Employees);
        Assert.True(module.IsEmployeesTab);
        Assert.NotEmpty(module.Employees);
    }

    [Fact]
    public async Task Creer_un_motif_envoie_le_libelle_et_la_destination_puis_relit_la_liste()
    {
        var (_, reasons) = await OpenAsync();

        reasons.NewLabel = "   ";
        await reasons.CreateAsync();
        Assert.Equal("Le libellé du motif est obligatoire.", reasons.ErrorMessage);
        Assert.Empty(server.ReasonCreateRequests);

        reasons.NewLabel = "  Emballage abîmé ";
        reasons.NewDestination = DestinationChoice.For(ReturnStockDestinations.SetAside);
        await reasons.CreateAsync();

        var request = Assert.Single(server.ReasonCreateRequests);
        Assert.Equal(("Emballage abîmé", ReturnStockDestinations.SetAside), (request.Label, request.StockDestination));
        Assert.Equal(5, reasons.Reasons.Count);
        Assert.Equal("Emballage abîmé", reasons.SelectedReason!.Label);
        Assert.Equal("Motif « Emballage abîmé » créé : mise à l'écart.", reasons.InfoMessage);
        Assert.Equal(string.Empty, reasons.NewLabel);

        // Le serveur fait foi : un doublon est refusé avec son message.
        reasons.NewLabel = "erreur de vente";
        await reasons.CreateAsync();
        Assert.Equal("Un motif « erreur de vente » existe déjà.", reasons.ErrorMessage);
        Assert.Equal(5, reasons.Reasons.Count);
    }

    [Fact]
    public async Task Apres_une_panne_reseau_recreer_le_meme_motif_reprend_le_meme_identifiant_d_operation()
    {
        var (_, reasons) = await OpenAsync();
        reasons.NewLabel = "Péremption proche";
        server.FailNextReasonWrites = 1;

        await reasons.CreateAsync();
        Assert.Equal(ReturnReasonsViewModel.UnconfirmedMessage, reasons.ErrorMessage);
        await reasons.CreateAsync();

        Assert.Equal(2, server.ReasonCreateRequests.Count);
        Assert.Equal(server.ReasonCreateRequests[0].OperationId, server.ReasonCreateRequests[1].OperationId);
        Assert.Single(server.ReturnReasons, reason => reason.Label == "Péremption proche");
    }

    [Fact]
    public async Task Modifier_un_motif_change_son_libelle_et_sa_destination_sans_toucher_a_son_etat()
    {
        var (_, reasons) = await OpenAsync();
        Select(reasons, FakeServer.SaleMistake);

        reasons.EditLabel = "Erreur de vente (caisse)";
        reasons.EditDestination = DestinationChoice.For(ReturnStockDestinations.None);
        await reasons.SaveAsync();

        var (id, request) = Assert.Single(server.ReasonUpdateRequests);
        Assert.Equal(FakeServer.SaleMistake.Id, id);
        Assert.Equal(("Erreur de vente (caisse)", ReturnStockDestinations.None, true), (request.Label, request.StockDestination, request.IsActive));
        Assert.Equal("Erreur de vente (caisse)", reasons.SelectedReason!.Label);
        Assert.Equal("Pas de remise en stock", reasons.SelectedReason.DestinationText);
        Assert.StartsWith("Motif « Erreur de vente (caisse) » enregistré.", reasons.InfoMessage);
    }

    [Fact]
    public async Task Desactiver_un_motif_demande_une_confirmation_et_rien_n_est_envoye_avant()
    {
        var (_, reasons) = await OpenAsync();
        Select(reasons, FakeServer.Damaged);

        await reasons.ToggleStateAsync();
        Assert.True(reasons.IsConfirming);
        Assert.Contains("Désactiver le motif « Produit endommagé » ?", reasons.Confirmation!.Message);
        Assert.Empty(server.ReasonUpdateRequests);

        // Échap : rien n'est envoyé.
        reasons.CancelConfirmation();
        Assert.False(reasons.IsConfirming);
        Assert.Empty(server.ReasonUpdateRequests);
        Assert.True(server.ReturnReasons[1].IsActive);

        await reasons.ToggleStateAsync();
        await reasons.ConfirmAsync();

        var (id, request) = Assert.Single(server.ReasonUpdateRequests);
        Assert.Equal((FakeServer.Damaged.Id, false), (id, request.IsActive));
        Assert.False(reasons.SelectedReason!.IsActive);
        Assert.Equal("Inactif", reasons.SelectedReason.StateText);
        Assert.Equal("Réactiver", reasons.ToggleStateLabel);
        Assert.Equal("Motif « Produit endommagé » désactivé.", reasons.InfoMessage);

        // Réactiver ne demande pas de confirmation.
        await reasons.ToggleStateAsync();
        Assert.False(reasons.IsConfirming);
        Assert.True(server.ReturnReasons[1].IsActive);
        Assert.Equal(2, server.ReasonUpdateRequests.Count);
    }

    [Fact]
    public async Task Sans_le_droit_d_administrer_les_utilisateurs_le_serveur_refuse_et_le_refus_est_affiche()
    {
        var (_, reasons) = await OpenAsync();
        server.EmployeePermissions = [Permissions.ReturnsCreate];

        reasons.NewLabel = "Autre";
        await reasons.CreateAsync();

        Assert.Equal("Vous n'avez pas le droit d'effectuer cette action.", reasons.ErrorMessage);
        Assert.DoesNotContain(server.ReturnReasons, reason => reason.Label == "Autre");
    }
}
