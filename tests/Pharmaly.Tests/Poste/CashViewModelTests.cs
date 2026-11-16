using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Modules;
using Pharmaly.Poste.Modules.Cash;
using Pharmaly.Poste.Modules.Sales;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Tests.Poste;

public sealed class CashViewModelTests
{
    private static readonly DateOnly Today = new(2026, 11, 16);

    private readonly FakeServer server =
        new FakeServer { HasPosteSession = true, EmployeeIdentified = true }.AsAdministrator().WithOpenCashSession();

    private readonly ReceivableLine benali1;
    private readonly ReceivableLine benali2;
    private readonly ReceivableLine benali3;
    private readonly ReceivableLine haddad;

    public CashViewModelTests()
    {
        // L'exemple du cahier des charges : 3 500 + 6 200 + 1 800 = 11 500 DA pour un même client.
        benali1 = server.AddReceivable(FakeServer.Benali, 3500m);
        benali2 = server.AddReceivable(FakeServer.Benali, 6200m);
        benali3 = server.AddReceivable(FakeServer.Benali, 1800m);
        haddad = server.AddReceivable(FakeServer.Haddad, 2000m);
    }

    /// <summary>Le module tel que le poste le construit : les droits affichés sont ceux de l'employé identifié.</summary>
    private CashViewModel Create() =>
        new(server, server, server, CashRights.From(code => server.EmployeePermissions.Contains(code)), () => Today);

    private async Task<CashViewModel> OpenAsync()
    {
        var viewModel = Create();
        await viewModel.ActivateAsync();
        return viewModel;
    }

    private void Without(params string[] permissions) =>
        server.EmployeePermissions = server.EmployeePermissions.Except(permissions).ToList();

    private static ReceivableRowViewModel Row(CashViewModel viewModel, ReceivableLine line) =>
        viewModel.Rows.Single(row => row.Id == line.Id);

    /// <summary>Le lecteur lit le code-barres du ticket, puis Entrée.</summary>
    private static async Task ScanAsync(CashViewModel viewModel, ReceivableLine line)
    {
        viewModel.SearchText = line.TicketBarcode;
        await viewModel.SubmitSearchAsync();
    }

    /// <summary>Les trois tickets de Nadia Benali cochés.</summary>
    private async Task<CashViewModel> OpenWithBenaliSelectedAsync()
    {
        var viewModel = await OpenAsync();
        viewModel.Toggle(Row(viewModel, benali1));
        viewModel.Toggle(Row(viewModel, benali2));
        viewModel.Toggle(Row(viewModel, benali3));
        return viewModel;
    }

    /// <summary>Espèces jusqu'à la confirmation incluse : touche *, montant reçu, Entrée, Entrée.</summary>
    private static async Task PayCashAsync(CashViewModel viewModel, string received = "")
    {
        viewModel.BeginCash();
        viewModel.ReceivedText = received;
        viewModel.ValidateCashAmount();
        await viewModel.ConfirmSettlementAsync();
    }

    // ---------- Onglets ----------

    [Fact]
    public async Task Le_module_s_ouvre_sur_les_impayes_et_Ctrl_Tab_passe_a_la_session_puis_a_la_cloture_vide()
    {
        var viewModel = await OpenAsync();
        Assert.True(viewModel.IsReceivablesTab);

        await viewModel.MoveTabAsync(1);
        Assert.True(viewModel.IsSessionTab);
        await viewModel.MoveTabAsync(1);
        Assert.True(viewModel.IsClosingTab);
        Assert.Equal("La clôture quotidienne n'est pas encore disponible sur ce poste.", CashViewModel.ClosingNotAvailable);
        await viewModel.MoveTabAsync(1);
        Assert.True(viewModel.IsReceivablesTab);
        await viewModel.MoveTabAsync(-1);
        Assert.True(viewModel.IsClosingTab);
    }

    [Fact]
    public async Task Pendant_un_reglement_on_ne_change_pas_d_onglet()
    {
        var viewModel = await OpenWithBenaliSelectedAsync();
        viewModel.BeginCash();

        await viewModel.MoveTabAsync(1);

        Assert.True(viewModel.IsReceivablesTab);
        Assert.Equal(SettlementStep.CashAmount, viewModel.Step);
    }

    // ---------- Tableau et indicateurs ----------

    [Fact]
    public async Task Le_tableau_montre_les_impayes_ouverts_et_les_indicateurs_portent_sur_toutes_les_creances()
    {
        server.SettleElsewhere(haddad.Id);

        var viewModel = await OpenAsync();

        Assert.Equal([benali1.Id, benali2.Id, benali3.Id], viewModel.Rows.Select(row => row.Id));
        var row = Row(viewModel, benali1);
        Assert.Equal(benali1.TicketNumber, row.TicketNumber);
        Assert.Equal("Nadia Benali", row.CustomerName);
        Assert.Equal("3 500,00 DA", row.AmountText);
        Assert.Equal("Ouvert", row.StatusText);
        Assert.Equal(FakeServer.EmployeeName, row.EmployeeName);
        Assert.Equal(string.Empty, row.SettledAtText);
        Assert.Equal(string.Empty, row.MethodText);

        Assert.Equal("11 500,00 DA", viewModel.OpenAmountText);
        Assert.Equal("3", viewModel.OpenCountText);
        Assert.Equal("13 500,00 DA", viewModel.TotalAmountText);
        Assert.Equal("2 000,00 DA", viewModel.SettledAmountText);
        Assert.Equal("1 ticket", viewModel.SettledCountText);
        Assert.Equal(ReceivableStatuses.Open, Assert.Single(server.ReceivableQueries).Status);
    }

    [Fact]
    public async Task L_historique_des_creances_soldees_montre_la_date_et_le_moyen_du_reglement()
    {
        server.SettleElsewhere(haddad.Id, PaymentMethods.Card);
        var viewModel = await OpenAsync();

        await viewModel.ShowSettledHistoryAsync();

        var row = Assert.Single(viewModel.Rows);
        Assert.Equal(haddad.Id, row.Id);
        Assert.Equal("Soldé", row.StatusText);
        Assert.Equal("TPE", row.MethodText);
        Assert.NotEqual(string.Empty, row.SettledAtText);
        Assert.Equal("Soldés", viewModel.SelectedStatus.Label);
    }

    // ---------- Filtres ----------

    [Fact]
    public async Task F7_passe_des_ouverts_aux_soldes_puis_a_tous()
    {
        server.SettleElsewhere(haddad.Id);
        var viewModel = await OpenAsync();
        Assert.Equal(3, viewModel.Rows.Count);

        viewModel.CycleStatus();
        await Task.Yield();
        Assert.Equal("Soldés", viewModel.SelectedStatus.Label);
        Assert.Equal([haddad.Id], viewModel.Rows.Select(row => row.Id));

        viewModel.CycleStatus();
        await Task.Yield();
        Assert.Equal("Tous", viewModel.SelectedStatus.Label);
        Assert.Equal(4, viewModel.Rows.Count);
        Assert.Null(server.ReceivableQueries[^1].Status);

        viewModel.CycleStatus();
        await Task.Yield();
        Assert.Equal("Ouverts", viewModel.SelectedStatus.Label);
    }

    [Fact]
    public async Task Les_dates_bornent_le_tableau_et_une_date_invalide_est_signalee_sans_appel()
    {
        server.AddReceivable(FakeServer.Haddad, 900m, new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.FromHours(1)));
        var viewModel = await OpenAsync();
        Assert.Equal(5, viewModel.Rows.Count);

        viewModel.FromText = "01/10/2026";
        viewModel.ToText = "31/10";
        await viewModel.ReloadAsync();

        Assert.Equal(new DateOnly(2026, 10, 1), server.ReceivableQueries[^1].From);
        Assert.Equal(new DateOnly(2026, 10, 31), server.ReceivableQueries[^1].To);
        Assert.Equal("900,00 DA", Assert.Single(viewModel.Rows).AmountText);

        var reads = server.ReceivableQueries.Count;
        viewModel.FromText = "32/13/2026";
        await viewModel.ReloadAsync();
        Assert.Equal("Date invalide : saisissez jj/mm/aaaa, ou rien pour ne pas borner.", viewModel.ErrorMessage);
        Assert.Equal(reads, server.ReceivableQueries.Count);
    }

    // ---------- Recherche ----------

    [Fact]
    public async Task Le_scan_du_ticket_montre_les_tickets_de_son_client_et_coche_ce_ticket()
    {
        var viewModel = await OpenAsync();

        await ScanAsync(viewModel, benali2);

        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.Equal("Client : Nadia Benali", viewModel.CustomerFilterText);
        Assert.Equal([benali1.Id, benali2.Id, benali3.Id], viewModel.Rows.Select(row => row.Id));
        Assert.Equal(benali2.Id, viewModel.CurrentRow!.Id);
        Assert.True(viewModel.CurrentRow.IsChecked);
        Assert.Equal([benali2.Id], viewModel.Selection.Select(line => line.Id));
        Assert.Equal("6 200,00 DA", viewModel.SelectionTotalText);
        Assert.Equal("Nadia Benali", viewModel.SelectionCustomerText);
        Assert.Equal(FakeServer.Benali.Id, server.ReceivableQueries[^1].CustomerId);
    }

    [Fact]
    public async Task Le_numero_exact_du_ticket_le_retrouve_comme_un_scan()
    {
        var viewModel = await OpenAsync();

        viewModel.SearchText = haddad.TicketNumber.ToLowerInvariant();
        await viewModel.SubmitSearchAsync();

        Assert.Equal("Client : Omar Haddad", viewModel.CustomerFilterText);
        Assert.Equal([haddad.Id], viewModel.Selection.Select(line => line.Id));
    }

    [Fact]
    public async Task Le_scan_d_un_ticket_deja_solde_le_montre_sans_le_cocher()
    {
        server.SettleElsewhere(benali1.Id);
        var viewModel = await OpenAsync();

        await ScanAsync(viewModel, benali1);

        Assert.Equal("Tous", viewModel.SelectedStatus.Label);
        Assert.Equal(benali1.Id, viewModel.CurrentRow!.Id);
        Assert.Empty(viewModel.Selection);
        Assert.Equal($"Ticket {benali1.TicketNumber} — Nadia Benali — Soldé.", viewModel.InfoMessage);
    }

    [Fact]
    public async Task Une_partie_du_numero_cherche_dans_le_tableau()
    {
        var viewModel = await OpenAsync();

        viewModel.SearchText = haddad.TicketNumber[^3..];
        await viewModel.SubmitSearchAsync();

        Assert.Equal(haddad.TicketNumber[^3..], server.ReceivableQueries[^1].Ticket);
        Assert.Equal([haddad.Id], viewModel.Rows.Select(row => row.Id));
        Assert.Empty(viewModel.Selection);
        Assert.False(viewModel.HasCustomerFilter);
    }

    [Fact]
    public async Task Le_nom_d_un_client_montre_ses_tickets()
    {
        var viewModel = await OpenAsync();

        viewModel.SearchText = "haddad";
        await viewModel.SubmitSearchAsync();

        Assert.Equal("Client : Omar Haddad", viewModel.CustomerFilterText);
        Assert.Equal([haddad.Id], viewModel.Rows.Select(row => row.Id));
        Assert.Equal(string.Empty, viewModel.SearchText);

        await viewModel.ClearCustomerFilterAsync();
        Assert.Equal(4, viewModel.Rows.Count);
    }

    [Fact]
    public async Task Plusieurs_clients_correspondants_sont_proposes_au_choix()
    {
        var viewModel = await OpenAsync();

        viewModel.SearchText = "a";
        await viewModel.SubmitSearchAsync();

        Assert.Equal(SettlementStep.ChooseCustomer, viewModel.Step);
        Assert.Equal(["Nadia Benali", "Omar Haddad"], viewModel.CustomerResults.Select(customer => customer.Name));
        viewModel.MoveCustomerSelection(1);
        await viewModel.ChooseCustomerAsync();

        Assert.Equal(SettlementStep.None, viewModel.Step);
        Assert.Equal([haddad.Id], viewModel.Rows.Select(row => row.Id));
    }

    [Fact]
    public async Task Une_recherche_sans_ticket_ni_client_le_dit()
    {
        var viewModel = await OpenAsync();

        viewModel.SearchText = "Zorglub";
        await viewModel.SubmitSearchAsync();

        Assert.Equal("Aucun ticket ni client ne correspond à « Zorglub ».", viewModel.ErrorMessage);
        Assert.Equal(SettlementStep.None, viewModel.Step);
    }

    // ---------- Sélection ----------

    [Fact]
    public async Task Plusieurs_tickets_du_meme_client_se_cochent_et_le_total_est_leur_somme()
    {
        var viewModel = await OpenWithBenaliSelectedAsync();

        Assert.Equal(3, viewModel.SelectionCount);
        Assert.Equal(11500m, viewModel.SelectionTotal);
        Assert.Equal("11 500,00 DA", viewModel.SelectionTotalText);
        Assert.All(viewModel.Rows.Where(row => row.Line.CustomerId == FakeServer.Benali.Id), row => Assert.True(row.IsChecked));

        // Espace sur une ligne cochée la décoche.
        viewModel.CurrentRow = Row(viewModel, benali2);
        viewModel.ToggleCurrent();
        Assert.Equal(5300m, viewModel.SelectionTotal);
        Assert.False(Row(viewModel, benali2).IsChecked);
    }

    [Fact]
    public async Task Un_ticket_d_un_autre_client_ne_peut_pas_rejoindre_la_selection_et_l_ecran_explique_pourquoi()
    {
        var viewModel = await OpenWithBenaliSelectedAsync();

        viewModel.Toggle(Row(viewModel, haddad));

        Assert.False(Row(viewModel, haddad).IsChecked);
        Assert.Equal(3, viewModel.SelectionCount);
        Assert.Equal(
            $"Le ticket {haddad.TicketNumber} appartient à Omar Haddad, pas à Nadia Benali : un règlement groupé concerne un seul client. "
            + "Réglez d'abord la sélection en cours, ou décochez-la.",
            viewModel.ErrorMessage);
        Assert.Empty(server.SettleRequests);
    }

    [Fact]
    public async Task Le_scan_d_un_ticket_d_un_autre_client_ne_le_coche_pas()
    {
        var viewModel = await OpenAsync();
        await ScanAsync(viewModel, benali1);

        await ScanAsync(viewModel, haddad);

        Assert.Equal([benali1.Id], viewModel.Selection.Select(line => line.Id));
        Assert.Contains("un règlement groupé concerne un seul client", viewModel.ErrorMessage);
    }

    // ---------- Espèces ----------

    [Fact]
    public async Task Choisir_le_moyen_n_est_pas_confirmer_rien_n_est_envoye_avant_la_confirmation()
    {
        var viewModel = await OpenWithBenaliSelectedAsync();

        viewModel.BeginCash();
        Assert.Equal(SettlementStep.CashAmount, viewModel.Step);
        Assert.Equal("Nadia Benali", viewModel.CheckoutCustomerText);
        Assert.Equal("11 500,00 DA", viewModel.CheckoutTotalText);
        viewModel.ValidateCashAmount();
        Assert.Equal(SettlementStep.CashConfirm, viewModel.Step);
        await viewModel.CancelStepAsync();
        await viewModel.CancelStepAsync();
        Assert.Equal(SettlementStep.None, viewModel.Step);

        viewModel.BeginCard();
        Assert.Equal(SettlementStep.CardConfirm, viewModel.Step);
        await viewModel.CancelStepAsync();

        Assert.Empty(server.SettleRequests);
        Assert.Equal(3, viewModel.SelectionCount);
    }

    [Fact]
    public async Task Sans_ticket_choisi_aucun_moyen_ne_s_ouvre()
    {
        var viewModel = await OpenAsync();

        viewModel.BeginCash();

        Assert.Equal(SettlementStep.None, viewModel.Step);
        Assert.Equal("Choisissez d'abord le ou les tickets à régler (Espace sur la ligne, ou scan du ticket).", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Reglement_groupe_en_especes_montant_exactement_du_et_monnaie_a_rendre()
    {
        var viewModel = await OpenWithBenaliSelectedAsync();

        viewModel.BeginCash();
        viewModel.ReceivedText = "12000,5";
        Assert.Equal("500,50 DA", viewModel.ChangePreviewText);
        viewModel.ValidateCashAmount();
        Assert.Equal("12 000,50 DA", viewModel.AmountReceivedText);
        Assert.Equal("500,50 DA", viewModel.ChangeDueText);
        Assert.Empty(server.SettleRequests);
        await viewModel.ConfirmSettlementAsync();

        var request = Assert.Single(server.SettleRequests);
        Assert.Equal(PaymentMethods.Cash, request.Method);
        Assert.Equal(11500m, request.Amount);
        Assert.Equal(12000.50m, request.AmountReceived);
        Assert.Equal(new[] { benali1.Id, benali2.Id, benali3.Id }.Order(), request.ReceivableIds.Order());

        // Récapitulatif : tickets soldés, montant, monnaie.
        Assert.Equal(SettlementStep.Done, viewModel.Step);
        Assert.Equal("Nadia Benali — 3 tickets soldés", viewModel.DoneTitle);
        Assert.Equal("11 500,00 DA", viewModel.DoneAmountText);
        Assert.Equal("500,50 DA", viewModel.DoneChangeText);
        Assert.Equal("Reçu 12 000,50 DA", viewModel.DoneReceivedText);
        Assert.Contains(benali1.TicketNumber, viewModel.DoneTicketsText);
        Assert.True(viewModel.IsDoneCash);
        Assert.All([benali1, benali2, benali3], line => Assert.Equal(ReceivableStatuses.Settled, server.Receivable(line.Id).Status));

        // Retour au tableau, relu : seul le ticket de Omar Haddad reste ouvert.
        await viewModel.FinishAsync();
        Assert.Equal(SettlementStep.None, viewModel.Step);
        Assert.Equal([haddad.Id], viewModel.Rows.Select(row => row.Id));
        Assert.Equal(0, viewModel.SelectionCount);
        Assert.Equal("2 000,00 DA", viewModel.OpenAmountText);
        Assert.Single(server.SettleRequests);
    }

    [Fact]
    public async Task Montant_recu_vide_vaut_le_montant_exact_et_un_montant_insuffisant_est_refuse_a_l_ecran()
    {
        var viewModel = await OpenWithBenaliSelectedAsync();
        viewModel.BeginCash();

        viewModel.ReceivedText = "11000";
        Assert.Equal("—", viewModel.ChangePreviewText);
        viewModel.ValidateCashAmount();
        Assert.Equal(SettlementStep.CashAmount, viewModel.Step);
        Assert.Equal(
            "Montant reçu insuffisant : 11 000,00 DA pour 11 500,00 DA dus. Un ticket impayé se règle en entier.", viewModel.StepError);

        viewModel.ReceivedText = string.Empty;
        viewModel.ValidateCashAmount();
        await viewModel.ConfirmSettlementAsync();

        var request = Assert.Single(server.SettleRequests);
        Assert.Equal(11500m, request.AmountReceived);
        Assert.Equal("0,00 DA", viewModel.DoneChangeText);
    }

    [Fact]
    public async Task Le_montant_envoye_est_toujours_la_somme_due_au_moment_de_la_confirmation()
    {
        var viewModel = await OpenAsync();
        viewModel.Toggle(Row(viewModel, benali1));
        viewModel.Toggle(Row(viewModel, benali3));

        await PayCashAsync(viewModel, "6000");

        var request = Assert.Single(server.SettleRequests);
        Assert.Equal(3500m + 1800m, request.Amount);
        Assert.Equal(2, request.ReceivableIds.Count);
        Assert.Equal(ReceivableStatuses.Open, server.Receivable(benali2.Id).Status);
    }

    [Fact]
    public async Task Un_double_declenchement_de_la_confirmation_ne_produit_qu_un_seul_envoi()
    {
        var viewModel = await OpenWithBenaliSelectedAsync();
        viewModel.BeginCash();
        viewModel.ValidateCashAmount();
        server.SettlementGate = new TaskCompletionSource();

        var first = viewModel.ConfirmSettlementAsync();
        var second = viewModel.ConfirmSettlementAsync();
        await viewModel.RetrySettlementAsync();
        server.SettlementGate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Single(server.SettleRequests);
        Assert.Single(server.Settlements);
        Assert.Equal(SettlementStep.Done, viewModel.Step);
    }

    // ---------- Panne de réseau ----------

    [Theory]
    [InlineData(SaleSendFailure.RequestLost)]
    [InlineData(SaleSendFailure.AnswerLost)]
    public async Task Apres_une_panne_reseau_Reessayer_renvoie_le_meme_operationId_et_ne_regle_qu_une_fois(SaleSendFailure failure)
    {
        var viewModel = await OpenWithBenaliSelectedAsync();
        server.SettlementFailures.Enqueue(failure);

        await PayCashAsync(viewModel, "12000");

        Assert.Equal(SettlementStep.Unconfirmed, viewModel.Step);
        Assert.Equal("Règlement non confirmé — ne pas rendre la monnaie", CashViewModel.UnconfirmedTitle);
        await viewModel.CancelStepAsync();
        await viewModel.MoveTabAsync(1);
        Assert.Equal(SettlementStep.Unconfirmed, viewModel.Step);

        await viewModel.RetrySettlementAsync();

        Assert.Equal(2, server.SettleRequests.Count);
        Assert.Equal(server.SettleRequests[0].OperationId, server.SettleRequests[1].OperationId);
        Assert.Equal(server.SettleRequests[0], server.SettleRequests[1]);
        Assert.Single(server.Settlements);
        Assert.Equal(SettlementStep.Done, viewModel.Step);
        Assert.Equal("500,00 DA", viewModel.DoneChangeText);
    }

    [Fact]
    public async Task Une_nouvelle_intention_apres_un_refus_recoit_un_nouvel_operationId()
    {
        var viewModel = await OpenAsync();
        viewModel.Toggle(Row(viewModel, benali1));
        server.CashSession = null;
        await PayCashAsync(viewModel);
        server.WithOpenCashSession();
        await viewModel.RefreshAsync();

        // Le refus n'a rien encaissé : la sélection est restée, on encaisse de nouveau.
        Assert.Equal([benali1.Id], viewModel.Selection.Select(line => line.Id));
        await PayCashAsync(viewModel);

        Assert.Equal(2, server.SettleRequests.Count);
        Assert.NotEqual(server.SettleRequests[0].OperationId, server.SettleRequests[1].OperationId);
    }

    // ---------- Refus du serveur ----------

    [Fact]
    public async Task Un_ticket_regle_entre_temps_par_un_autre_poste_affiche_le_refus_et_recharge_le_tableau()
    {
        var viewModel = await OpenWithBenaliSelectedAsync();
        server.SettleElsewhere(benali2.Id);

        await PayCashAsync(viewModel, "12000");

        Assert.Equal(SettlementStep.None, viewModel.Step);
        Assert.Equal($"Le ticket {benali2.TicketNumber} est déjà soldé. Rien n'a été encaissé.", viewModel.ErrorMessage);
        Assert.DoesNotContain(viewModel.Rows, row => row.Id == benali2.Id);
        Assert.Equal([benali1.Id, benali3.Id], viewModel.Selection.Select(line => line.Id));
        Assert.Equal(ReceivableStatuses.Open, server.Receivable(benali1.Id).Status);
        Assert.Empty(server.Settlements);
    }

    [Fact]
    public async Task Un_reglement_par_carte_en_cours_ailleurs_est_refuse_avec_le_message_du_serveur()
    {
        var other = Create();
        await other.ActivateAsync();
        other.Toggle(Row(other, benali1));
        other.BeginCard();
        await other.ConfirmSettlementAsync();
        var viewModel = await OpenAsync();
        Assert.Equal("TPE en attente", Row(viewModel, benali1).StatusText);

        // L'écran refuse de cocher un ticket bloqué par un TPE en attente…
        viewModel.Toggle(Row(viewModel, benali1));
        Assert.Empty(viewModel.Selection);
        Assert.Contains("Un règlement par carte est en attente", viewModel.ErrorMessage);

        // … et si le TPE est lancé après l'affichage, le refus du serveur est montré tel quel.
        viewModel.Toggle(Row(viewModel, benali2));
        var third = Create();
        await third.ActivateAsync();
        third.Toggle(Row(third, benali2));
        third.BeginCard();
        await third.ConfirmSettlementAsync();
        await PayCashAsync(viewModel);

        Assert.Equal(
            $"Un règlement par carte est déjà en attente sur le ticket {benali2.TicketNumber}. Répondez-y (accepté ou refusé) avant de recommencer.",
            viewModel.ErrorMessage);
        Assert.Equal(ReceivableErrorCodes.SettlementPending, "REGLEMENT_EN_COURS");
        Assert.Empty(viewModel.Selection);
    }

    [Fact]
    public async Task Caisse_fermee_entre_temps_le_refus_est_affiche_et_l_ecran_le_signale()
    {
        var viewModel = await OpenWithBenaliSelectedAsync();
        server.CashSession = null;

        await PayCashAsync(viewModel);

        Assert.Equal("La caisse de ce poste n'est pas ouverte. Ouvrez la caisse avant de vendre ou d'encaisser.", viewModel.ErrorMessage);
        Assert.True(viewModel.IsCashClosedWarning);

        // Désormais, l'écran prévient sans rien envoyer.
        viewModel.BeginCash();
        Assert.Equal(SettlementStep.None, viewModel.Step);
        Assert.Equal(CashViewModel.CashClosedMessage, viewModel.ErrorMessage);
        Assert.Single(server.SettleRequests);
    }

    [Fact]
    public async Task Le_refus_clients_differents_du_serveur_est_affiche_tel_quel()
    {
        // Le poste ne peut pas réunir deux clients ; un serveur qui verrait autrement les tickets refuse quand même.
        var viewModel = await OpenAsync();
        viewModel.Toggle(Row(viewModel, benali1));
        var index = server.ReceivableLines.FindIndex(line => line.Id == benali1.Id);
        server.ReceivableLines[index] = server.ReceivableLines[index] with { CustomerId = FakeServer.Haddad.Id };
        viewModel.Toggle(Row(viewModel, benali2));

        await PayCashAsync(viewModel);

        Assert.Equal(
            "Ces tickets appartiennent à des clients différents : un règlement groupé concerne un seul client. Rien n'a été encaissé.",
            viewModel.ErrorMessage);
        Assert.Empty(server.Settlements);
    }

    // ---------- TPE ----------

    [Fact]
    public async Task TPE_accepte_la_dette_n_est_soldee_qu_apres_accepte()
    {
        var viewModel = await OpenWithBenaliSelectedAsync();

        viewModel.BeginCard();
        Assert.Equal("Nadia Benali", viewModel.CheckoutCustomerText);
        Assert.Empty(server.SettleRequests);
        await viewModel.ConfirmSettlementAsync();

        var request = Assert.Single(server.SettleRequests);
        Assert.Equal(PaymentMethods.Card, request.Method);
        Assert.Equal(11500m, request.Amount);
        Assert.Null(request.AmountReceived);
        Assert.Equal(SettlementStep.CardPending, viewModel.Step);
        Assert.Equal("Nadia Benali — 11 500,00 DA", viewModel.PendingSettlementText);
        Assert.All([benali1, benali2, benali3], line => Assert.Equal(ReceivableStatuses.Open, server.Receivable(line.Id).Status));

        // Une confirmation ou un nouvel envoi ne répond pas à la place du terminal.
        await viewModel.ConfirmSettlementAsync();
        await viewModel.CancelStepAsync();
        Assert.Equal(SettlementStep.CardPending, viewModel.Step);
        Assert.Empty(server.SettlementCardRequests);

        await viewModel.AcceptCardAsync();

        Assert.True(Assert.Single(server.SettlementCardRequests).Request.Accepted);
        Assert.Equal(SettlementStep.Done, viewModel.Step);
        Assert.True(viewModel.IsDoneCard);
        Assert.All([benali1, benali2, benali3], line => Assert.Equal(ReceivableStatuses.Settled, server.Receivable(line.Id).Status));
        Assert.Single(server.SettleRequests);
    }

    [Fact]
    public async Task TPE_refuse_la_dette_reste_ouverte_et_la_selection_est_conservee()
    {
        var viewModel = await OpenWithBenaliSelectedAsync();
        viewModel.BeginCard();
        await viewModel.ConfirmSettlementAsync();

        await viewModel.RefuseCardAsync();

        Assert.False(Assert.Single(server.SettlementCardRequests).Request.Accepted);
        Assert.Equal(SettlementStep.None, viewModel.Step);
        Assert.All([benali1, benali2, benali3], line => Assert.Equal(ReceivableStatuses.Open, server.Receivable(line.Id).Status));
        Assert.Equal(
            "Paiement refusé : 3 tickets de Nadia Benali (11 500,00 DA) restent impayés. Encaissez autrement ou laissez la dette ouverte.",
            viewModel.InfoMessage);
        Assert.Equal(3, viewModel.SelectionCount);
        Assert.Equal("Ouvert", Row(viewModel, benali1).StatusText);

        // Encaissement en espèces juste après, sans rien recocher.
        await PayCashAsync(viewModel);
        Assert.Equal(SettlementStep.Done, viewModel.Step);
        Assert.Equal(2, server.SettleRequests.Count);
    }

    [Fact]
    public async Task Reponse_du_TPE_non_confirmee_se_redonne_avec_le_meme_operationId()
    {
        var viewModel = await OpenWithBenaliSelectedAsync();
        viewModel.BeginCard();
        await viewModel.ConfirmSettlementAsync();
        server.FailNextSettlementCardAnswers = 1;

        await viewModel.AcceptCardAsync();
        Assert.Equal(SettlementStep.CardPending, viewModel.Step);
        Assert.Equal("Réponse non confirmée : le serveur n'a pas répondu. Donnez de nouveau la même réponse.", viewModel.StepError);

        await viewModel.AcceptCardAsync();

        Assert.Equal(2, server.SettlementCardRequests.Count);
        Assert.Equal(server.SettlementCardRequests[0].Request.OperationId, server.SettlementCardRequests[1].Request.OperationId);
        Assert.Equal(SettlementStep.Done, viewModel.Step);
    }

    [Fact]
    public async Task Un_reglement_TPE_reste_en_attente_recoit_sa_reponse_depuis_le_tableau()
    {
        // Un TPE lancé puis laissé sans réponse (poste verrouillé, par exemple).
        var abandoned = await OpenWithBenaliSelectedAsync();
        abandoned.BeginCard();
        await abandoned.ConfirmSettlementAsync();
        var pendingId = server.Settlements.Single().Id;

        var viewModel = await OpenAsync();
        viewModel.CurrentRow = Row(viewModel, benali3);
        Assert.True(viewModel.CanAnswerCurrentPending);
        Assert.Equal(pendingId, viewModel.CurrentRow.Line.PendingSettlementId);
        viewModel.CurrentRow = Row(viewModel, haddad);
        Assert.False(viewModel.CanAnswerCurrentPending);
        await viewModel.BeginPendingAnswerAsync();
        Assert.Equal(SettlementStep.None, viewModel.Step);

        viewModel.CurrentRow = Row(viewModel, benali3);
        await viewModel.BeginPendingAnswerAsync();

        Assert.Equal(SettlementStep.CardPending, viewModel.Step);
        Assert.Equal("Nadia Benali — 11 500,00 DA", viewModel.PendingSettlementText);
        Assert.Empty(server.SettlementCardRequests);

        await viewModel.AcceptCardAsync();

        Assert.Equal(pendingId, Assert.Single(server.SettlementCardRequests).SettlementId);
        Assert.Equal(SettlementStep.Done, viewModel.Step);
        Assert.Equal(ReceivableStatuses.Settled, server.Receivable(benali3.Id).Status);
    }

    [Fact]
    public async Task Un_TPE_en_attente_refuse_depuis_le_tableau_debloque_les_tickets()
    {
        var abandoned = await OpenWithBenaliSelectedAsync();
        abandoned.BeginCard();
        await abandoned.ConfirmSettlementAsync();

        var viewModel = await OpenAsync();
        viewModel.CurrentRow = Row(viewModel, benali1);
        await viewModel.BeginPendingAnswerAsync();
        await viewModel.RefuseCardAsync();

        Assert.Equal(SettlementStep.None, viewModel.Step);
        Assert.Equal("Ouvert", Row(viewModel, benali1).StatusText);
        Assert.False(viewModel.CanAnswerCurrentPending);
        viewModel.Toggle(Row(viewModel, benali1));
        Assert.Equal(1, viewModel.SelectionCount);
    }

    [Fact]
    public async Task Une_reponse_TPE_deja_donnee_ailleurs_est_signalee_et_le_tableau_relu()
    {
        var abandoned = await OpenWithBenaliSelectedAsync();
        abandoned.BeginCard();
        await abandoned.ConfirmSettlementAsync();
        var viewModel = await OpenAsync();
        viewModel.CurrentRow = Row(viewModel, benali1);
        await viewModel.BeginPendingAnswerAsync();

        // Le poste qui a lancé le paiement répond le premier.
        await abandoned.AcceptCardAsync();
        await viewModel.RefuseCardAsync();

        Assert.Equal(SettlementStep.None, viewModel.Step);
        Assert.Equal("Ce règlement n'attend pas de réponse du TPE.", viewModel.ErrorMessage);
        Assert.DoesNotContain(viewModel.Rows, row => row.Id == benali1.Id);
        Assert.Equal(ReceivableStatuses.Settled, server.Receivable(benali1.Id).Status);
    }

    [Fact]
    public void Les_touches_affichees_sont_etoile_pour_les_especes_et_la_constante_provisoire_du_TPE()
    {
        var viewModel = Create();

        Assert.Equal("*", viewModel.CashKeyLabel);
        Assert.Equal(SalesShortcuts.ProvisionalCardKey.ToString(), viewModel.CardKeyLabel);
        Assert.Equal("A", viewModel.CardAcceptedKeyLabel);
        Assert.Equal("R", viewModel.CardRefusedKeyLabel);
    }

    // ---------- Session de caisse ----------

    [Fact]
    public async Task L_onglet_session_montre_l_etat_de_la_caisse_et_un_resume_lisible()
    {
        server.SessionSales = (8000m, 4500m);
        var viewModel = await OpenWithBenaliSelectedAsync();
        await PayCashAsync(viewModel, "12000");
        await viewModel.FinishAsync();

        await viewModel.ShowTabAsync(CashTab.Session);

        Assert.True(viewModel.IsCashOpen);
        Assert.Equal("Caisse 1 — OUVERTE", viewModel.CashStateText);
        Assert.StartsWith($"Ouverte par {FakeServer.EmployeeName} le ", viewModel.CashOpenedText);
        Assert.Equal("5 000,00 DA", viewModel.CashFloatText);
        Assert.True(viewModel.CanShowSummary);
        var lines = viewModel.SummaryLines.ToDictionary(line => line.Label, line => line.Amount);
        Assert.Equal("8 000,00 DA", lines["Ventes en espèces"]);
        Assert.Equal("4 500,00 DA", lines["Ventes par TPE"]);
        Assert.Equal("11 500,00 DA", lines["Règlements d'impayés en espèces"]);
        Assert.Equal("0,00 DA", lines["Règlements d'impayés par TPE"]);
        Assert.Equal("0,00 DA", lines["Entrées d'espèces"]);
        Assert.Equal("− 0,00 DA", lines["Sorties d'espèces"]);
        Assert.True(lines.ContainsKey("Remboursements en espèces"));
        Assert.Equal("24 500,00 DA", viewModel.ExpectedCashText);
        Assert.False(viewModel.HasPendingCard);
    }

    [Fact]
    public async Task Un_TPE_en_attente_apparait_a_part_dans_le_resume()
    {
        var viewModel = await OpenWithBenaliSelectedAsync();
        viewModel.BeginCard();
        await viewModel.ConfirmSettlementAsync();
        var session = await OpenAsync();

        await session.ShowTabAsync(CashTab.Session);

        Assert.True(session.HasPendingCard);
        Assert.Equal(
            "1 paiement(s) par carte en attente de réponse : 11 500,00 DA, compté(s) nulle part.", session.PendingCardText);
        Assert.Equal("5 000,00 DA", session.ExpectedCashText);
    }

    [Fact]
    public async Task Caisse_fermee_l_onglet_session_propose_l_ouverture_avec_confirmation()
    {
        server.CashSession = null;
        var viewModel = await OpenAsync();
        Assert.True(viewModel.IsCashClosedWarning);
        await viewModel.ShowTabAsync(CashTab.Session);
        Assert.True(viewModel.IsCashClosed);
        Assert.Equal("Caisse FERMÉE", viewModel.CashStateText);
        Assert.True(viewModel.CanShowOpening);
        Assert.False(viewModel.CanShowMovements);

        viewModel.OpeningFloatText = "3000,5";
        viewModel.RequestCashOpening();
        Assert.True(viewModel.IsConfirmingOpening);
        Assert.Equal("3 000,50 DA", viewModel.OpeningFloatDisplayText);
        Assert.Empty(server.OpenSessionRequests);

        await viewModel.ConfirmCashOpeningAsync();

        Assert.Equal(3000.50m, Assert.Single(server.OpenSessionRequests).OpeningFloat);
        Assert.True(viewModel.IsCashOpen);
        Assert.Equal("Caisse ouverte avec un fond de caisse de 3 000,50 DA.", viewModel.InfoMessage);
        Assert.True(viewModel.CanShowSummary);
        Assert.Equal("3 000,50 DA", viewModel.ExpectedCashText);
    }

    [Fact]
    public async Task Ouverture_de_caisse_non_confirmee_se_retente_avec_le_meme_operationId()
    {
        server.CashSession = null;
        server.FailNextSessionOpenings = 1;
        var viewModel = await OpenAsync();
        await viewModel.ShowTabAsync(CashTab.Session);
        viewModel.OpeningFloatText = "0";
        viewModel.RequestCashOpening();

        await viewModel.ConfirmCashOpeningAsync();
        Assert.True(viewModel.IsConfirmingOpening);
        await viewModel.ConfirmCashOpeningAsync();

        Assert.Equal(2, server.OpenSessionRequests.Count);
        Assert.Equal(server.OpenSessionRequests[0].OperationId, server.OpenSessionRequests[1].OperationId);
        Assert.True(viewModel.IsCashOpen);
    }

    [Fact]
    public async Task Sortie_d_especes_motif_obligatoire_confirmation_puis_un_seul_enregistrement()
    {
        var viewModel = await OpenAsync();
        await viewModel.ShowTabAsync(CashTab.Session);
        Assert.True(viewModel.CanShowMovements);

        viewModel.BeginCashOut();
        Assert.Equal(SessionStep.MovementInput, viewModel.SessionStep);
        Assert.Equal("Sortie d'espèces", viewModel.MovementTitle);
        viewModel.MovementAmountText = "1500.50";
        viewModel.ValidateMovement();
        Assert.Equal(CashViewModel.ReasonRequiredMessage, viewModel.SessionStepError);
        Assert.Equal(SessionStep.MovementInput, viewModel.SessionStep);

        viewModel.MovementReason = "  Achat de sacs  ";
        viewModel.ValidateMovement();
        Assert.Equal(SessionStep.MovementConfirm, viewModel.SessionStep);
        Assert.Equal("1 500,50 DA", viewModel.MovementAmountDisplayText);
        Assert.Equal("Sortie d'espèces de 1 500,50 DA — motif : Achat de sacs", viewModel.MovementConfirmText);
        Assert.Empty(server.MovementRequests);

        await viewModel.ConfirmMovementAsync();
        await viewModel.ConfirmMovementAsync();

        var request = Assert.Single(server.MovementRequests);
        Assert.Equal(CashMovementKinds.Out, request.Kind);
        Assert.Equal(1500.50m, request.Amount);
        Assert.Equal("Achat de sacs", request.Reason);
        Assert.Equal(SessionStep.None, viewModel.SessionStep);
        Assert.Equal("Sortie d'espèces enregistrée : 1 500,50 DA — Achat de sacs.", viewModel.InfoMessage);
        Assert.Equal("3 499,50 DA", viewModel.ExpectedCashText);
        Assert.Equal("− 1 500,50 DA", viewModel.SummaryLines.Single(line => line.Label == "Sorties d'espèces").Amount);
    }

    [Fact]
    public async Task Entree_d_especes_apres_une_panne_Reessayer_garde_le_meme_operationId()
    {
        var viewModel = await OpenAsync();
        await viewModel.ShowTabAsync(CashTab.Session);
        server.MovementFailures.Enqueue(SaleSendFailure.AnswerLost);
        viewModel.BeginCashIn();
        viewModel.MovementAmountText = "2000";
        viewModel.MovementReason = "Appoint de monnaie";
        viewModel.ValidateMovement();

        await viewModel.ConfirmMovementAsync();
        Assert.Equal(SessionStep.MovementUnconfirmed, viewModel.SessionStep);
        viewModel.CancelSessionStep();
        Assert.Equal(SessionStep.MovementUnconfirmed, viewModel.SessionStep);

        await viewModel.RetryMovementAsync();

        Assert.Equal(2, server.MovementRequests.Count);
        Assert.Equal(server.MovementRequests[0].OperationId, server.MovementRequests[1].OperationId);
        Assert.Single(server.CashMovements);
        Assert.Equal("7 000,00 DA", viewModel.ExpectedCashText);
    }

    [Fact]
    public async Task Un_montant_de_mouvement_invalide_est_signale_sans_envoi()
    {
        var viewModel = await OpenAsync();
        await viewModel.ShowTabAsync(CashTab.Session);
        viewModel.BeginCashIn();
        viewModel.MovementReason = "Appoint";

        foreach (var text in new[] { "", "0", "-5", "12,345", "abc" })
        {
            viewModel.MovementAmountText = text;
            viewModel.ValidateMovement();
            Assert.Equal(SessionStep.MovementInput, viewModel.SessionStep);
        }

        Assert.Empty(server.MovementRequests);
    }

    // ---------- Droits ----------

    [Fact]
    public async Task Sans_le_droit_de_regler_le_tableau_se_consulte_mais_aucune_action_de_reglement_n_apparait()
    {
        Without(Permissions.ReceivablesSettle);
        var viewModel = await OpenAsync();

        Assert.Equal(4, viewModel.Rows.Count);
        Assert.False(viewModel.CanSettle);
        viewModel.Toggle(Row(viewModel, benali1));
        Assert.Empty(viewModel.Selection);
        await ScanAsync(viewModel, benali1);
        Assert.Empty(viewModel.Selection);
        viewModel.BeginCash();
        viewModel.BeginCard();
        Assert.Equal(SettlementStep.None, viewModel.Step);
        Assert.Empty(server.SettleRequests);
    }

    [Fact]
    public async Task Sans_le_droit_de_consulter_les_impayes_le_module_s_ouvre_sur_la_session()
    {
        Without(Permissions.ReceivablesView);
        var viewModel = await OpenAsync();

        Assert.True(viewModel.IsSessionTab);
        Assert.False(viewModel.CanSettle);
        Assert.Empty(server.ReceivableQueries);
        await viewModel.ShowTabAsync(CashTab.Receivables);
        Assert.Empty(server.ReceivableQueries);
        Assert.Equal("Vos droits ne permettent pas de consulter les impayés.", CashViewModel.NoReceivablesViewRight);
    }

    [Fact]
    public async Task Sans_les_droits_de_caisse_ni_ouverture_ni_resume_ni_mouvements()
    {
        Without(Permissions.RegisterOpen, Permissions.RegisterReportsView, Permissions.PaymentsCollect);
        var viewModel = await OpenAsync();
        await viewModel.ShowTabAsync(CashTab.Session);

        Assert.True(viewModel.IsCashOpen);
        Assert.False(viewModel.CanShowSummary);
        Assert.True(viewModel.CannotViewReports);
        Assert.False(viewModel.CanShowMovements);
        Assert.Equal(0, server.SummaryReads);
        viewModel.BeginCashOut();
        Assert.Equal(SessionStep.None, viewModel.SessionStep);

        server.CashSession = null;
        await viewModel.RefreshAsync();
        Assert.True(viewModel.IsCashClosed);
        Assert.False(viewModel.CanShowOpening);
        viewModel.OpeningFloatText = "100";
        viewModel.RequestCashOpening();
        Assert.False(viewModel.IsConfirmingOpening);
    }

    [Fact]
    public async Task Panne_de_reseau_a_la_lecture_du_tableau_affiche_le_message_habituel()
    {
        var viewModel = Create();
        server.IsDown = true;

        await viewModel.ActivateAsync();

        Assert.Equal(ModuleViewModel.ServerUnreachableMessage, viewModel.ErrorMessage);
        Assert.Empty(viewModel.Rows);
    }

    [Fact]
    public async Task La_route_et_les_parametres_du_tableau_suivent_le_serveur()
    {
        Assert.Equal(string.Empty, ReceivablesClient.ToQueryString(new ReceivablesQuery()));
        var customer = Guid.Parse("6f0a3c0e-6f0e-4f43-9f6e-0d3b8c1d2a11");
        Assert.Equal(
            "?statut=OUVERTE&client=6f0a3c0e-6f0e-4f43-9f6e-0d3b8c1d2a11&du=2026-11-01&au=2026-11-16&q=D1-0&limite=50",
            ReceivablesClient.ToQueryString(new ReceivablesQuery(
                ReceivableStatuses.Open, customer, new DateOnly(2026, 11, 1), Today, "D1-0", 50)));
        await Task.CompletedTask;
    }
}
