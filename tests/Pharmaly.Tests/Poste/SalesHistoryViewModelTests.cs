using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Modules;
using Pharmaly.Poste.Modules.Sales;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Tests.Poste;

public sealed class SalesHistoryViewModelTests
{
    private readonly FakeServer server =
        new FakeServer { HasPosteSession = true, EmployeeIdentified = true }.AsAdministrator().WithOpenCashSession();

    private int identificationsAsked;

    private SalesHistoryViewModel Create() => new(
        server,
        SalesRights.From(code => server.EmployeePermissions.Contains(code)),
        refused =>
        {
            var lost = refused.Code is ApiErrorCodes.EmployeeNotIdentified or ApiErrorCodes.PosteNotSignedIn;
            identificationsAsked += lost ? 1 : 0;
            return lost;
        });

    private async Task<SalesHistoryViewModel> OpenAsync()
    {
        var viewModel = Create();
        await viewModel.ActivateAsync();
        return viewModel;
    }

    private void Without(params string[] permissions) =>
        server.EmployeePermissions = server.EmployeePermissions.Except(permissions).ToList();

    /// <summary>Une vente enregistrée par le serveur, comme si elle venait d'être faite sur ce poste.</summary>
    private Task<SaleDetail> SellAsync(string mode, ProductSummary? product = null, decimal quantity = 1)
    {
        product ??= FakeServer.Creme;
        var total = SaleMath.LineTotal(quantity, product.SalePrice);
        return server.CreateSaleAsync(
            new CreateSaleRequest(
                Guid.NewGuid(), [new SaleLineRequest(product.Id, quantity)], mode,
                AmountReceived: mode == SalePaymentModes.Cash ? total + 149.90m : null,
                CustomerId: mode == SalePaymentModes.Unpaid ? FakeServer.Benali.Id : null,
                ExpectedTotal: total),
            CancellationToken.None);
    }

    /// <summary>Motif saisi, validé par Entrée, puis confirmation forte.</summary>
    private static async Task CancelAsync(SalesHistoryViewModel viewModel, string reason)
    {
        viewModel.BeginCancel();
        viewModel.CancelReason = reason;
        viewModel.ValidateCancelReason();
        await viewModel.ConfirmCancelAsync();
    }

    [Fact]
    public async Task L_historique_liste_les_ventes_du_jour_les_plus_recentes_d_abord_avec_le_detail_de_la_premiere()
    {
        await SellAsync(SalePaymentModes.Cash);
        await SellAsync(SalePaymentModes.Unpaid, FakeServer.Serum, 2);
        await SellAsync(SalePaymentModes.Card);

        var viewModel = await OpenAsync();

        var today = DateOnly.FromDateTime(DateTime.Now);
        Assert.Equal(today.ToString("dd/MM/yyyy"), viewModel.FromText);
        Assert.Equal(viewModel.FromText, viewModel.ToText);
        Assert.Equal(new SalesHistoryQuery(today, today), Assert.Single(server.HistoryQueries));
        Assert.Equal(["D1-000003", "D1-000002", "D1-000001"], viewModel.Sales.Select(row => row.TicketNumber));
        Assert.Equal(["En attente TPE", "Validée", "Validée"], viewModel.Sales.Select(row => row.StatusText));
        Assert.Equal(["TPE", "IMPAYÉ", "Espèces"], viewModel.Sales.Select(row => row.ModeText));
        Assert.Equal(["1 850,10 DA", "8 400,40 DA", "1 850,10 DA"], viewModel.Sales.Select(row => row.TotalText));

        // Le premier ticket est sélectionné et son détail affiché.
        Assert.Equal("D1-000003", viewModel.SelectedSale!.TicketNumber);
        Assert.Equal("Ticket D1-000003", viewModel.Detail!.Title);
        Assert.Equal("TPE — en attente de la réponse du terminal", viewModel.Detail.PaymentText);
    }

    [Fact]
    public async Task Le_detail_d_un_ticket_montre_ses_lignes_son_total_et_son_paiement()
    {
        await SellAsync(SalePaymentModes.Cash, FakeServer.Serum, 2);
        await SellAsync(SalePaymentModes.Unpaid);
        var viewModel = await OpenAsync();

        viewModel.MoveSelection(+1);
        await Task.Yield();

        var detail = viewModel.Detail!;
        Assert.Equal("Ticket D1-000001", detail.Title);
        var line = Assert.Single(detail.Lines);
        Assert.Equal(FakeServer.Serum.Name, line.Name);
        Assert.Equal("2", line.QuantityText);
        Assert.Equal("4 200,20 DA", line.UnitPriceText);
        Assert.Equal("8 400,40 DA", line.LineTotalText);
        Assert.Equal("8 400,40 DA", detail.TotalText);
        Assert.Equal("Espèces — encaissé — reçu 8 550,30 DA, rendu 149,90 DA", detail.PaymentText);
        Assert.False(detail.HasCancellation);

        viewModel.MoveSelection(-1);
        await Task.Yield();
        Assert.Equal("IMPAYÉ — Nadia Benali — 1 850,10 DA dus", viewModel.Detail!.PaymentText);
    }

    [Fact]
    public async Task Le_filtre_de_statut_et_les_dates_sont_envoyes_au_serveur()
    {
        await SellAsync(SalePaymentModes.Cash);
        await SellAsync(SalePaymentModes.Card);
        var viewModel = await OpenAsync();

        // Changer de statut relance la recherche.
        viewModel.SelectedStatus = viewModel.StatusChoices.Single(choice => choice.Code == SaleStatuses.CardPending);
        await Task.Yield();

        Assert.Equal(SaleStatuses.CardPending, server.HistoryQueries[^1].Status);
        Assert.Equal("D1-000002", Assert.Single(viewModel.Sales).TicketNumber);

        viewModel.SelectedStatus = viewModel.StatusChoices[0];
        viewModel.FromText = "1/1/2020";
        viewModel.ToText = "31/12/2020";
        await viewModel.SearchAsync();

        Assert.Equal(new SalesHistoryQuery(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)), server.HistoryQueries[^1]);
        Assert.Empty(viewModel.Sales);
        Assert.Null(viewModel.Detail);
        Assert.Equal("Aucun ticket ne correspond à ces filtres.", viewModel.InfoMessage);

        // Dates vides : aucune borne.
        viewModel.FromText = string.Empty;
        viewModel.ToText = " ";
        await viewModel.SearchAsync();
        Assert.Equal(new SalesHistoryQuery(), server.HistoryQueries[^1]);
        Assert.Equal(2, viewModel.Sales.Count);
    }

    [Theory]
    [InlineData("32/01/2026")]
    [InlineData("hier")]
    [InlineData("2026-11-16")]
    public async Task Une_date_invalide_est_refusee_sans_interroger_le_serveur(string typed)
    {
        var viewModel = await OpenAsync();
        var queries = server.HistoryQueries.Count;

        viewModel.FromText = typed;
        await viewModel.SearchAsync();

        Assert.Equal("Date invalide : saisissez jj/mm/aaaa, ou rien pour ne pas borner.", viewModel.ErrorMessage);
        Assert.Equal(queries, server.HistoryQueries.Count);
    }

    [Theory]
    [InlineData("D1-000002")]
    [InlineData("d1-000002")]
    [InlineData("TK0000000002")]
    public async Task Un_numero_exact_ou_le_code_barres_du_ticket_lu_au_lecteur_ouvre_le_ticket_quelles_que_soient_les_dates(string typed)
    {
        await SellAsync(SalePaymentModes.Cash);
        await SellAsync(SalePaymentModes.Unpaid, FakeServer.Serum);
        await SellAsync(SalePaymentModes.Cash);
        var viewModel = await OpenAsync();
        viewModel.FromText = viewModel.ToText = "01/01/2020";
        var queries = server.HistoryQueries.Count;

        viewModel.TicketQuery = typed;
        await viewModel.SearchAsync();

        Assert.Equal("D1-000002", Assert.Single(viewModel.Sales).TicketNumber);
        Assert.Equal("Ticket D1-000002", viewModel.Detail!.Title);
        Assert.Equal(FakeServer.Serum.Name, viewModel.Detail.Lines[0].Name);
        Assert.Equal(queries, server.HistoryQueries.Count);
        Assert.Equal(string.Empty, viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Une_partie_de_numero_filtre_la_liste()
    {
        await SellAsync(SalePaymentModes.Cash);
        await SellAsync(SalePaymentModes.Cash);
        var viewModel = await OpenAsync();

        viewModel.TicketQuery = "0002";
        await viewModel.SearchAsync();

        Assert.Equal("0002", server.HistoryQueries[^1].Ticket);
        Assert.Equal("D1-000002", Assert.Single(viewModel.Sales).TicketNumber);
    }

    // ---------- Annulation ----------

    [Fact]
    public async Task L_annulation_exige_un_motif_puis_une_confirmation_forte_avant_tout_envoi()
    {
        await SellAsync(SalePaymentModes.Cash);
        var viewModel = await OpenAsync();
        Assert.True(viewModel.CanCancelSelected);

        viewModel.BeginCancel();
        Assert.Equal(HistoryStep.CancelReason, viewModel.Step);
        Assert.Equal("Annuler le ticket D1-000001 — 1 850,10 DA", viewModel.StepTitle);

        // Sans motif : refusé, on reste sur la saisie, et la confirmation forcée ne passe pas non plus.
        viewModel.CancelReason = "   ";
        viewModel.ValidateCancelReason();
        Assert.Equal(HistoryStep.CancelReason, viewModel.Step);
        Assert.Equal(SalesHistoryViewModel.ReasonRequiredMessage, viewModel.StepError);
        await viewModel.ConfirmCancelAsync();
        Assert.Empty(server.CancelRequests);

        viewModel.CancelReason = " Erreur de produit ";
        viewModel.ValidateCancelReason();

        // Confirmation forte : elle décrit ce qui va se passer. Rien n'est encore envoyé.
        Assert.Equal(HistoryStep.CancelConfirm, viewModel.Step);
        Assert.Equal(
            "Le stock sera restitué et 1 850,10 DA seront à rendre au client en espèces, sortis de la caisse de ce poste.",
            viewModel.CancelConsequence);
        Assert.Empty(server.CancelRequests);

        await viewModel.ConfirmCancelAsync();

        var (saleId, request) = Assert.Single(server.CancelRequests);
        Assert.Equal(server.Tickets[0].Id, saleId);
        Assert.Equal("Erreur de produit", request.Reason);
        Assert.NotEqual(Guid.Empty, request.OperationId);
        Assert.Equal(HistoryStep.None, viewModel.Step);
        Assert.Equal("Le ticket D1-000001 est annulé. Rendez 1 850,10 DA en espèces au client.", viewModel.InfoMessage);
        Assert.Equal("Annulée", viewModel.SelectedSale!.StatusText);
        Assert.Equal("Annulée", viewModel.Detail!.StatusText);
        Assert.Contains("Erreur de produit", viewModel.Detail.CancellationText);
        Assert.Contains("Remboursement en espèces : 1 850,10 DA.", viewModel.Detail.CancellationText);
        Assert.False(viewModel.CanCancelSelected);
        Assert.Equal(10, server.StockLevels[FakeServer.Creme.Id]);
    }

    [Fact]
    public async Task Echap_sur_la_confirmation_revient_au_motif_et_n_annule_rien()
    {
        await SellAsync(SalePaymentModes.Cash);
        var viewModel = await OpenAsync();
        viewModel.BeginCancel();
        viewModel.CancelReason = "Erreur";
        viewModel.ValidateCancelReason();

        viewModel.CloseStep();
        Assert.Equal(HistoryStep.CancelReason, viewModel.Step);
        viewModel.CloseStep();

        Assert.Equal(HistoryStep.None, viewModel.Step);
        Assert.Empty(server.CancelRequests);
        Assert.Equal(SaleStatuses.Completed, server.Tickets[0].Status);
    }

    [Fact]
    public async Task Sans_le_droit_d_annuler_l_annulation_n_est_pas_proposee()
    {
        await SellAsync(SalePaymentModes.Cash);
        Without(Permissions.SalesCancel);
        var viewModel = await OpenAsync();

        Assert.False(viewModel.CanCancelSelected);
        viewModel.BeginCancel();
        Assert.Equal(HistoryStep.None, viewModel.Step);
    }

    [Fact]
    public async Task JOURNEE_CLOTUREE_le_refus_du_serveur_est_affiche_tel_quel()
    {
        var sale = await SellAsync(SalePaymentModes.Cash);
        server.ClosedDaySales.Add(sale.Id);
        var viewModel = await OpenAsync();

        await CancelAsync(viewModel, "Erreur de caisse");

        Assert.Equal(
            "La journée de cette vente est clôturée : elle ne peut plus être annulée. Passez par un retour.", viewModel.ErrorMessage);
        Assert.Equal(HistoryStep.None, viewModel.Step);
        Assert.Equal(string.Empty, viewModel.InfoMessage);
        Assert.Equal("Validée", viewModel.Detail!.StatusText);
        Assert.Equal(9, server.StockLevels[FakeServer.Creme.Id]);
    }

    [Fact]
    public async Task CREANCE_DEJA_REGLEE_le_refus_du_serveur_est_affiche_tel_quel()
    {
        var sale = await SellAsync(SalePaymentModes.Unpaid);
        var viewModel = await OpenAsync();
        Assert.Equal("Le stock sera restitué et la créance du client sera annulée.", Consequence(viewModel));

        // La créance est réglée à la caisse pendant que l'écran est ouvert.
        server.SettleReceivable(sale.Id);
        await CancelAsync(viewModel, "Client mécontent");

        Assert.Equal(
            "Le ticket D1-000001 a déjà été réglé : il ne peut plus être annulé. Passez par un retour.", viewModel.ErrorMessage);
        Assert.Equal(SaleStatuses.Completed, server.Tickets[0].Status);

        // Le ticket est relu : il n'est plus affiché comme impayé.
        Assert.StartsWith("Vente impayée — Nadia Benali — réglée", viewModel.Detail!.PaymentText);
        Assert.Equal("Impayé", viewModel.SelectedSale!.ModeText);
    }

    private static string Consequence(SalesHistoryViewModel viewModel)
    {
        viewModel.BeginCancel();
        viewModel.CancelReason = "x";
        viewModel.ValidateCancelReason();
        var consequence = viewModel.CancelConsequence;
        viewModel.CloseStep();
        viewModel.CloseStep();
        return consequence;
    }

    [Fact]
    public async Task VENTE_DEJA_ANNULEE_le_refus_du_serveur_est_affiche_tel_quel_et_le_ticket_est_relu()
    {
        var sale = await SellAsync(SalePaymentModes.Cash);
        var viewModel = await OpenAsync();

        // Un autre poste annule le même ticket entre-temps.
        await server.CancelSaleAsync(sale.Id, new CancelSaleRequest(Guid.NewGuid(), "Annulé ailleurs"), CancellationToken.None);
        await CancelAsync(viewModel, "Erreur");

        Assert.Equal("Le ticket D1-000001 est déjà annulé.", viewModel.ErrorMessage);
        Assert.Equal("Annulée", viewModel.Detail!.StatusText);
        Assert.Contains("Annulé ailleurs", viewModel.Detail.CancellationText);
        Assert.False(viewModel.CanCancelSelected);
    }

    [Fact]
    public async Task PAIEMENT_TPE_EN_ATTENTE_l_annulation_n_est_pas_proposee_et_le_refus_du_serveur_serait_affiche_tel_quel()
    {
        var sale = await SellAsync(SalePaymentModes.Cash);
        var pending = await SellAsync(SalePaymentModes.Card);
        var viewModel = await OpenAsync();

        // Ticket en attente TPE sélectionné : pas d'annulation, seulement la réponse du terminal.
        Assert.Equal(pending.Id, viewModel.SelectedSale!.Id);
        Assert.False(viewModel.CanCancelSelected);
        Assert.True(viewModel.CanAnswerCardSelected);
        viewModel.BeginCancel();
        Assert.Equal(HistoryStep.None, viewModel.Step);

        // Le refus du serveur, tel qu'il arriverait si l'écran était en retard sur l'état du ticket.
        var refused = await Assert.ThrowsAsync<ServerRefusedException>(() =>
            server.CancelSaleAsync(pending.Id, new CancelSaleRequest(Guid.NewGuid(), "Erreur"), CancellationToken.None));
        Assert.Equal(SaleErrorCodes.CardPending, refused.Code);
        Assert.NotEqual(sale.Id, pending.Id);
    }

    [Fact]
    public async Task Une_annulation_sans_reponse_du_serveur_se_retente_avec_le_meme_identifiant()
    {
        await SellAsync(SalePaymentModes.Cash);
        var viewModel = await OpenAsync();
        server.FailNextTicketWrites = 1;

        await CancelAsync(viewModel, "Erreur de produit");

        Assert.Equal(HistoryStep.CancelConfirm, viewModel.Step);
        Assert.StartsWith("Annulation non confirmée", viewModel.StepError);
        Assert.Equal(SaleStatuses.Completed, server.Tickets[0].Status);

        await viewModel.ConfirmCancelAsync();

        Assert.Equal(2, server.CancelRequests.Count);
        Assert.Equal(server.CancelRequests[0].Request, server.CancelRequests[1].Request);
        Assert.Equal(SaleStatuses.Cancelled, server.Tickets[0].Status);
        Assert.Equal(HistoryStep.None, viewModel.Step);
    }

    [Fact]
    public async Task Une_session_perdue_redemande_l_identification()
    {
        await SellAsync(SalePaymentModes.Cash);
        var viewModel = await OpenAsync();

        server.EmployeeIdentified = false;
        await CancelAsync(viewModel, "Erreur");

        Assert.Equal(1, identificationsAsked);
        Assert.Equal(SaleStatuses.Completed, server.Tickets[0].Status);
    }

    // ---------- Paiements par carte restés en attente ----------

    [Fact]
    public async Task Une_vente_restee_en_attente_TPE_est_visible_et_recoit_sa_reponse_acceptee()
    {
        await SellAsync(SalePaymentModes.Cash);
        var pending = await SellAsync(SalePaymentModes.Card, FakeServer.Serum);
        var viewModel = await OpenAsync();
        viewModel.SelectedStatus = viewModel.StatusChoices.Single(choice => choice.Code == SaleStatuses.CardPending);
        await Task.Yield();

        Assert.Equal(pending.TicketNumber, Assert.Single(viewModel.Sales).TicketNumber);
        Assert.True(viewModel.CanAnswerCardSelected);

        viewModel.BeginCardAnswer();
        Assert.Equal(HistoryStep.CardAnswer, viewModel.Step);
        Assert.Equal("Ticket D1-000002 — 4 200,20 DA", viewModel.StepTitle);
        Assert.Empty(server.CardRequests);

        await viewModel.AnswerCardAsync(accepted: true);

        Assert.True(Assert.Single(server.CardRequests).Request.Accepted);
        Assert.Equal(pending.Id, server.CardRequests[0].SaleId);
        Assert.Equal(HistoryStep.None, viewModel.Step);
        Assert.Equal("Paiement accepté : le ticket D1-000002 est validé.", viewModel.InfoMessage);
        Assert.Equal("Validée", viewModel.Detail!.StatusText);
        Assert.Equal("TPE — encaissé", viewModel.Detail.PaymentText);
        Assert.False(viewModel.CanAnswerCardSelected);
        Assert.True(viewModel.CanCancelSelected);
    }

    [Fact]
    public async Task Une_vente_restee_en_attente_TPE_refusee_est_annulee_et_son_stock_restitue()
    {
        await SellAsync(SalePaymentModes.Card, FakeServer.Serum, 3);
        var viewModel = await OpenAsync();
        Assert.Equal(7, server.StockLevels[FakeServer.Serum.Id]);

        viewModel.BeginCardAnswer();
        await viewModel.AnswerCardAsync(accepted: false);

        Assert.False(Assert.Single(server.CardRequests).Request.Accepted);
        Assert.Equal("Paiement refusé : la vente D1-000001 est annulée et le stock est restitué.", viewModel.InfoMessage);
        Assert.Equal("Annulée", viewModel.Detail!.StatusText);
        Assert.Equal("TPE — paiement refusé", viewModel.Detail.PaymentText);
        Assert.Contains("Paiement par carte refusé", viewModel.Detail.CancellationText);
        Assert.Equal(10, server.StockLevels[FakeServer.Serum.Id]);
    }

    [Fact]
    public async Task Sans_le_droit_d_encaisser_la_reponse_du_TPE_n_est_pas_proposee()
    {
        await SellAsync(SalePaymentModes.Card);
        Without(Permissions.PaymentsCollect);
        var viewModel = await OpenAsync();

        Assert.False(viewModel.CanAnswerCardSelected);
        viewModel.BeginCardAnswer();
        await viewModel.AnswerCardAsync(accepted: true);

        Assert.Equal(HistoryStep.None, viewModel.Step);
        Assert.Empty(server.CardRequests);
    }

    [Fact]
    public async Task Une_reponse_deja_donnee_ailleurs_le_refus_du_serveur_est_affiche_tel_quel()
    {
        var pending = await SellAsync(SalePaymentModes.Card);
        var viewModel = await OpenAsync();
        viewModel.BeginCardAnswer();

        // Le caissier qui a fait la vente répond « accepté » sur son poste au même moment.
        await server.ResolveCardAsync(pending.Id, new CardResultRequest(Guid.NewGuid(), true), CancellationToken.None);
        await viewModel.AnswerCardAsync(accepted: false);

        Assert.Equal("Ce ticket n'attend pas de réponse du TPE.", viewModel.ErrorMessage);
        Assert.Equal(HistoryStep.None, viewModel.Step);
        Assert.Equal("Validée", viewModel.Detail!.StatusText);
        Assert.Equal(SaleStatuses.Completed, server.Tickets[0].Status);
    }

    [Fact]
    public async Task Serveur_injoignable_l_historique_le_dit()
    {
        var viewModel = Create();
        server.IsDown = true;

        await viewModel.ActivateAsync();

        Assert.Equal(ModuleViewModel.ServerUnreachableMessage, viewModel.ErrorMessage);
        Assert.Empty(viewModel.Sales);
    }
}
