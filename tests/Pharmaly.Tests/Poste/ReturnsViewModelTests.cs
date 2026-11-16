using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Modules;
using Pharmaly.Poste.Modules.Sales;

namespace Pharmaly.Tests.Poste;

/// <summary>Écran des retours clients du module Vente, avec le faux serveur.</summary>
public sealed class ReturnsViewModelTests
{
    private readonly FakeServer server =
        new FakeServer { HasPosteSession = true, EmployeeIdentified = true }.AsAdministrator().WithStock(10, 10).WithOpenCashSession();

    private readonly RecordingTicketPrinter printer = new();

    private SalesViewModel CreateModule() => new(
        server, server, server, server, server, SalesRights.From(code => server.EmployeePermissions.Contains(code)), TimeSpan.Zero, printer);

    /// <summary>Le module Vente, ouvert sur l'écran des retours (touche des retours).</summary>
    private async Task<(SalesViewModel Module, ReturnsViewModel Returns)> OpenReturnsAsync()
    {
        var module = CreateModule();
        await module.ActivateAsync();
        await module.OpenReturnsAsync();
        Assert.Equal(SalesScreen.Returns, module.Screen);
        return (module, module.Returns);
    }

    private void Without(params string[] permissions) =>
        server.EmployeePermissions = server.EmployeePermissions.Except(permissions).ToList();

    /// <summary>Une vente enregistrée par le serveur.</summary>
    private Task<SaleDetail> SellAsync(
        string mode, IReadOnlyList<(ProductSummary Product, decimal Quantity)>? lines = null, decimal? discountPercent = null)
    {
        lines ??= [(FakeServer.Creme, 2m)];
        var subtotal = lines.Sum(line => SaleMath.LineTotal(line.Quantity, line.Product.SalePrice));
        var total = subtotal - (discountPercent is { } percent ? SaleMath.PercentDiscount(subtotal, percent) : 0m);
        return server.CreateSaleAsync(
            new CreateSaleRequest(
                Guid.NewGuid(), lines.Select(line => new SaleLineRequest(line.Product.Id, line.Quantity)).ToList(), mode,
                discountPercent is null ? null : DiscountKinds.Percent, discountPercent,
                mode == SalePaymentModes.Cash ? total : null,
                mode == SalePaymentModes.Unpaid ? FakeServer.Benali.Id : null,
                total),
            CancellationToken.None);
    }

    private static async Task ScanTicketAsync(ReturnsViewModel returns, string code)
    {
        returns.TicketQuery = code;
        await returns.FindTicketAsync();
    }

    private static ReasonChoiceViewModel Reason(ReturnsViewModel returns, ReturnReason reason) =>
        returns.Reasons.Single(choice => choice.Reason.Id == reason.Id);

    /// <summary>Quantité sur la première ligne, motif, puis aperçu : la confirmation est affichée.</summary>
    private static async Task PrepareAsync(ReturnsViewModel returns, string quantity, ReturnReason reason, int line = 0)
    {
        returns.Lines[line].QuantityText = quantity;
        returns.SelectedReason = Reason(returns, reason);
        await returns.RequestPreviewAsync();
    }

    // ---------- Retrouver le ticket ----------

    [Fact]
    public async Task Un_ticket_scanne_par_son_code_barres_affiche_chaque_ligne_vendue_deja_retournee_retournable_et_payee()
    {
        var sale = await SellAsync(SalePaymentModes.Cash, [(FakeServer.Creme, 2m), (FakeServer.Serum, 1m)]);
        var (_, returns) = await OpenReturnsAsync();

        await ScanTicketAsync(returns, sale.TicketBarcode);

        Assert.Equal(ReturnStep.Choose, returns.Step);
        Assert.Equal(ReturnField.Lines, returns.Field);
        Assert.Equal($"Ticket {sale.TicketNumber}", returns.TicketTitle);
        Assert.Equal(Money.ToDisplay(sale.Total), returns.TicketTotalText);
        Assert.Equal("Espèces", returns.TicketPaymentText);
        Assert.Contains(SaleFormat.DateTime(sale.CreatedAt), returns.TicketInfo);
        Assert.Equal(string.Empty, returns.TicketQuery);
        Assert.False(returns.IsBlocked);

        Assert.Equal(2, returns.Lines.Count);
        var creme = returns.Lines[0];
        Assert.Equal(FakeServer.Creme.Name, creme.Name);
        Assert.Equal("2", creme.SoldText);
        Assert.Equal("0", creme.ReturnedText);
        Assert.Equal("2", creme.ReturnableText);
        Assert.Equal("3 700,20 DA", creme.PaidText);
        Assert.Equal("0", creme.QuantityText);
        Assert.Same(creme, returns.SelectedLine);

        // Les motifs actifs du serveur, dans son ordre, aucun choisi d'office.
        Assert.Equal(
            [FakeServer.Resellable.Label, FakeServer.Damaged.Label, FakeServer.Defective.Label, FakeServer.SaleMistake.Label],
            returns.Reasons.Select(reason => reason.Label));
        Assert.Null(returns.SelectedReason);
    }

    [Fact]
    public async Task Un_ticket_saisi_par_son_numero_est_retrouve_et_un_numero_inconnu_est_signale()
    {
        var sale = await SellAsync(SalePaymentModes.Card);
        await server.ResolveCardAsync(sale.Id, new CardResultRequest(Guid.NewGuid(), true), CancellationToken.None);
        var (_, returns) = await OpenReturnsAsync();

        await ScanTicketAsync(returns, "D1-999999");
        Assert.Equal(ReturnStep.Search, returns.Step);
        Assert.False(returns.HasTicket);
        Assert.Contains("Ticket introuvable : « D1-999999 »", returns.ErrorMessage);

        await ScanTicketAsync(returns, sale.TicketNumber.ToLowerInvariant());
        Assert.Equal(ReturnStep.Choose, returns.Step);
        Assert.Equal(sale.Id, returns.Sale!.SaleId);
        Assert.Equal("TPE", returns.TicketPaymentText);
        Assert.Equal(string.Empty, returns.ErrorMessage);
    }

    [Fact]
    public async Task Un_ticket_impaye_non_regle_n_est_pas_retournable_la_raison_du_serveur_et_la_marche_a_suivre_sont_affichees()
    {
        var sale = await SellAsync(SalePaymentModes.Unpaid);
        var (module, returns) = await OpenReturnsAsync();

        await ScanTicketAsync(returns, sale.TicketNumber);

        Assert.Equal(ReturnStep.Search, returns.Step);
        Assert.True(returns.IsBlocked);
        Assert.StartsWith($"Le ticket {sale.TicketNumber} est impayé : aucun retour tant que sa créance n'est pas réglée en entier.", returns.BlockedMessage);
        Assert.Contains("annulez la vente", returns.BlockedMessage);
        Assert.Contains("historique des ventes (Ctrl+O), puis Ctrl+Suppr", returns.BlockedHint);
        Assert.Equal("IMPAYÉ — créance ouverte", returns.TicketPaymentText);
        Assert.True(returns.CanOpenSaleInHistory);

        // Ce que l'employé peut faire : ouvrir le ticket dans l'historique, d'où il peut annuler la vente.
        await returns.OpenSaleInHistoryAsync();
        Assert.Equal(SalesScreen.History, module.Screen);
        Assert.Equal(sale.Id, module.History.Detail!.Sale.Id);
        Assert.True(module.History.CanCancelSelected);
        Assert.False(module.History.CanReturnSelected);
        Assert.Empty(server.PreviewRequests);
        Assert.Empty(server.ReturnRequests);

        // Réglée : le ticket devient retournable, remboursé selon le règlement.
        server.SettleReceivable(sale.Id);
        await module.History.LoadDetailAsync(sale.Id);
        Assert.True(module.History.CanReturnSelected);
        await module.History.BeginReturnAsync();
        Assert.Equal(SalesScreen.Returns, module.Screen);
        Assert.Equal(ReturnStep.Choose, returns.Step);
        Assert.False(returns.IsBlocked);
    }

    [Fact]
    public async Task Un_ticket_annule_ou_en_attente_TPE_n_est_pas_retournable_avec_la_raison_du_serveur()
    {
        var cancelled = await SellAsync(SalePaymentModes.Cash);
        await server.CancelSaleAsync(cancelled.Id, new CancelSaleRequest(Guid.NewGuid(), "Erreur"), CancellationToken.None);
        var pending = await SellAsync(SalePaymentModes.Card);
        var (_, returns) = await OpenReturnsAsync();

        await ScanTicketAsync(returns, cancelled.TicketBarcode);
        Assert.True(returns.IsBlocked);
        Assert.Equal($"Le ticket {cancelled.TicketNumber} est annulé : il n'y a rien à retourner.", returns.BlockedMessage);
        Assert.False(returns.HasBlockedHint);

        // Un autre scan remplace le ticket affiché.
        await ScanTicketAsync(returns, pending.TicketBarcode);
        Assert.True(returns.IsBlocked);
        Assert.Contains("attend sa réponse", returns.BlockedMessage);
        Assert.Contains("Ctrl+T", returns.BlockedHint);

        // Rien n'est modifiable ni envoyé sur un ticket bloqué.
        await returns.RequestPreviewAsync();
        Assert.Empty(server.PreviewRequests);
    }

    [Fact]
    public async Task Un_ticket_entierement_retourne_n_a_plus_rien_a_retourner()
    {
        var sale = await SellAsync(SalePaymentModes.Cash, [(FakeServer.Creme, 1m)]);
        var (_, returns) = await OpenReturnsAsync();
        await ScanTicketAsync(returns, sale.TicketNumber);
        await PrepareAsync(returns, "1", FakeServer.Resellable);
        await returns.ConfirmAsync();
        returns.StartNextReturn();

        await ScanTicketAsync(returns, sale.TicketNumber);

        Assert.True(returns.IsBlocked);
        Assert.Equal(ReturnsViewModel.FullyReturnedMessage, returns.BlockedMessage);
        Assert.Equal("Déjà remboursé sur ce ticket : 1 850,10 DA", returns.RefundedText);
        Assert.Equal("1", returns.Lines[0].ReturnedText);
        Assert.Equal("0", returns.Lines[0].ReturnableText);
        Assert.False(returns.Lines[0].IsReturnable);
    }

    // ---------- Quantités ----------

    [Fact]
    public async Task Une_quantite_au_dela_du_retournable_est_bloquee_avant_tout_envoi()
    {
        var sale = await SellAsync(SalePaymentModes.Cash);
        var (_, returns) = await OpenReturnsAsync();
        await ScanTicketAsync(returns, sale.TicketNumber);

        returns.Lines[0].QuantityText = "3";
        await returns.NextFromLineAsync();

        Assert.Equal("« Crème hydratante 50 ml » : 3 demandé, 2 encore retournable (2 vendu, 0 déjà retourné).", returns.StepError);
        Assert.True(returns.Lines[0].HasError);
        Assert.Equal(ReturnField.Lines, returns.Field);

        returns.SelectedReason = Reason(returns, FakeServer.Resellable);
        await returns.RequestPreviewAsync();
        Assert.Equal(ReturnStep.Choose, returns.Step);
        Assert.Empty(server.PreviewRequests);

        // Corrigée, la saisie passe : la marque d'erreur disparaît à la frappe.
        returns.Lines[0].QuantityText = "2";
        Assert.False(returns.Lines[0].HasError);
        await returns.NextFromLineAsync();
        Assert.Equal(string.Empty, returns.StepError);
        Assert.Equal(ReturnField.Reason, returns.Field);
    }

    [Fact]
    public async Task Une_fraction_est_refusee_pour_un_produit_vendu_a_l_unite_et_acceptee_pour_un_produit_au_detail()
    {
        server.FractionalProducts.Add(FakeServer.Serum.Id);
        var sale = await SellAsync(SalePaymentModes.Cash, [(FakeServer.Creme, 2m), (FakeServer.Serum, 1m)]);
        var (_, returns) = await OpenReturnsAsync();
        await ScanTicketAsync(returns, sale.TicketNumber);

        returns.Lines[0].QuantityText = "0,5";
        await returns.NextFromLineAsync();
        Assert.Equal("« Crème hydratante 50 ml » se vend à l'unité : la quantité retournée doit être un nombre entier.", returns.StepError);
        Assert.Same(returns.Lines[0], returns.SelectedLine);

        returns.SelectedReason = Reason(returns, FakeServer.Resellable);
        await returns.RequestPreviewAsync();
        Assert.Empty(server.PreviewRequests);

        // Le sérum se vend au détail : 0,5 est accepté, et l'aperçu part.
        returns.Lines[0].QuantityText = "0";
        returns.Lines[1].QuantityText = "0,5";
        await returns.RequestPreviewAsync();
        Assert.Equal(ReturnStep.Confirm, returns.Step);
        var line = Assert.Single(Assert.Single(server.PreviewRequests).Lines);
        Assert.Equal((FakeServer.Serum.Id, 0.5m), (line.ProductId, line.Quantity));

        // Une saisie illisible est refusée.
        returns.Back();
        returns.Lines[1].QuantityText = "1,2,3";
        await returns.RequestPreviewAsync();
        Assert.StartsWith("Quantité invalide pour « Sérum nuit 30 ml »", returns.StepError);
    }

    [Fact]
    public async Task Sans_aucune_quantite_rien_n_est_demande_au_serveur()
    {
        var sale = await SellAsync(SalePaymentModes.Cash);
        var (_, returns) = await OpenReturnsAsync();
        await ScanTicketAsync(returns, sale.TicketNumber);
        returns.SelectedReason = Reason(returns, FakeServer.Resellable);

        await returns.RequestPreviewAsync();

        Assert.Equal(ReturnsViewModel.NothingToReturnMessage, returns.StepError);
        Assert.Equal(ReturnField.Lines, returns.Field);
        Assert.Empty(server.PreviewRequests);
    }

    // ---------- Motif ----------

    [Fact]
    public async Task Le_motif_est_obligatoire()
    {
        var sale = await SellAsync(SalePaymentModes.Cash);
        var (_, returns) = await OpenReturnsAsync();
        await ScanTicketAsync(returns, sale.TicketNumber);
        returns.Lines[0].QuantityText = "1";

        await returns.NextFromLineAsync();
        Assert.Equal(ReturnField.Reason, returns.Field);
        returns.ValidateReason();
        Assert.Equal(ReturnsViewModel.ReasonRequiredMessage, returns.StepError);
        Assert.Equal(ReturnField.Reason, returns.Field);

        await returns.RequestPreviewAsync();
        Assert.Equal(ReturnsViewModel.ReasonRequiredMessage, returns.StepError);
        Assert.Empty(server.PreviewRequests);

        // ↓ choisit le premier motif ; Entrée passe à la note.
        returns.MoveReason(1);
        Assert.Equal(FakeServer.Resellable.Id, returns.SelectedReason!.Reason.Id);
        returns.ValidateReason();
        Assert.Equal(ReturnField.Note, returns.Field);
        Assert.Equal(string.Empty, returns.StepError);
    }

    [Fact]
    public async Task La_destination_de_la_marchandise_est_affichee_selon_le_motif()
    {
        var sale = await SellAsync(SalePaymentModes.Cash);
        var (_, returns) = await OpenReturnsAsync();
        await ScanTicketAsync(returns, sale.TicketNumber);
        Assert.Equal("Choisissez d'abord le motif", returns.DestinationText);

        returns.ChooseReasonNumber(1);
        Assert.Equal("Remise en stock vendable", returns.DestinationText);
        Assert.False(returns.IsNotRestocked);

        returns.ChooseReasonNumber(2);
        Assert.Equal(FakeServer.Damaged.Id, returns.SelectedReason!.Reason.Id);
        Assert.Equal("Pas de remise en stock", returns.DestinationText);
        Assert.Equal("La marchandise rendue sort du circuit : aucun stock n'est réintégré.", returns.DestinationHint);
        Assert.True(returns.IsNotRestocked);

        returns.ChooseReasonNumber(3);
        Assert.Equal("Mise à l'écart", returns.DestinationText);
        Assert.False(returns.IsNotRestocked);

        // Les destinations sont aussi écrites dans la liste des motifs.
        Assert.Equal(
            ["Remise en stock vendable", "Pas de remise en stock", "Mise à l'écart", "Remise en stock vendable"],
            returns.Reasons.Select(reason => reason.DestinationText));
    }

    [Fact]
    public async Task Les_motifs_sont_relus_a_chaque_arrivee_sur_les_retours_mais_pas_pendant_un_retour_en_cours()
    {
        var sale = await SellAsync(SalePaymentModes.Cash);
        var (module, returns) = await OpenReturnsAsync();
        Assert.Equal(4, returns.Reasons.Count);

        // Un administrateur désactive un motif : il disparaît à la prochaine arrivée sur l'écran.
        server.ReturnReasons[1] = FakeServer.Damaged with { IsActive = false };
        await module.CloseReturnsAsync();
        await module.OpenReturnsAsync();
        Assert.DoesNotContain(returns.Reasons, reason => reason.Reason.Id == FakeServer.Damaged.Id);

        // Pendant un retour, le choix en cours n'est pas dérangé.
        await ScanTicketAsync(returns, sale.TicketNumber);
        returns.SelectedReason = Reason(returns, FakeServer.Defective);
        server.ReturnReasons[1] = FakeServer.Damaged;
        await module.ActivateAsync();
        Assert.Equal(3, returns.Reasons.Count);
        Assert.Equal(FakeServer.Defective.Id, returns.SelectedReason!.Reason.Id);
    }

    // ---------- Aperçu ----------

    [Fact]
    public async Task L_apercu_affiche_le_remboursement_tel_que_le_serveur_le_calcule_avec_le_moyen()
    {
        // Vente remisée de 10 % : 2 crèmes, 3 700,20 DA - 370,02 DA = 3 330,18 DA payés.
        var sale = await SellAsync(SalePaymentModes.Cash, [(FakeServer.Creme, 2m)], discountPercent: 10m);
        Assert.Equal(3330.18m, sale.Total);
        var (_, returns) = await OpenReturnsAsync();
        await ScanTicketAsync(returns, sale.TicketNumber);
        Assert.Equal("3 330,18 DA", returns.Lines[0].PaidText);

        await PrepareAsync(returns, "1", FakeServer.Defective);

        Assert.Equal(ReturnStep.Confirm, returns.Step);
        Assert.Equal("1 665,09 DA", returns.PreviewRefundText);
        Assert.Equal("En espèces — sortie de la caisse de ce poste", returns.PreviewMethodText);
        Assert.True(returns.IsPreviewCash);
        Assert.Equal("Mise à l'écart", returns.PreviewDestinationText);
        Assert.Equal(["Crème hydratante 50 ml — 1 rendu — 1 665,09 DA"], returns.PreviewLines);
        var request = Assert.Single(server.PreviewRequests);
        Assert.Equal((sale.Id, FakeServer.Defective.Id), (request.SaleId, request.ReasonId));

        // Le poste n'invente aucun montant : ce que le serveur renvoie est affiché tel quel.
        returns.Back();
        server.RefundDrift = 0.01m;
        await returns.RequestPreviewAsync();
        Assert.Equal("1 665,10 DA", returns.PreviewRefundText);
    }

    [Fact]
    public async Task L_apercu_d_un_ticket_TPE_annonce_un_remboursement_par_carte_sans_especes()
    {
        var sale = await SellAsync(SalePaymentModes.Card);
        await server.ResolveCardAsync(sale.Id, new CardResultRequest(Guid.NewGuid(), true), CancellationToken.None);
        var (_, returns) = await OpenReturnsAsync();
        await ScanTicketAsync(returns, sale.TicketNumber);

        await PrepareAsync(returns, "2", FakeServer.Resellable);

        Assert.True(returns.IsPreviewCard);
        Assert.False(returns.IsPreviewCash);
        Assert.Equal("Par carte, sur le terminal — aucune sortie d'espèces", returns.PreviewMethodText);
        Assert.Equal("3 700,20 DA", returns.PreviewRefundText);
    }

    [Fact]
    public async Task Un_refus_de_l_apercu_est_affiche_tel_quel_et_le_choix_reste_ouvert()
    {
        var sale = await SellAsync(SalePaymentModes.Cash);
        var (_, returns) = await OpenReturnsAsync();
        await ScanTicketAsync(returns, sale.TicketNumber);

        // Le motif a été désactivé entre-temps par un administrateur.
        server.ReturnReasons[0] = FakeServer.Resellable with { IsActive = false };
        await PrepareAsync(returns, "1", FakeServer.Resellable);

        Assert.Equal(ReturnStep.Choose, returns.Step);
        Assert.Equal("Choisissez un motif de retour dans la liste des motifs actifs.", returns.StepError);
        Assert.Null(returns.Preview);
    }

    // ---------- Confirmation et envoi ----------

    [Fact]
    public async Task Choisir_et_voir_l_apercu_sans_confirmer_n_envoie_rien()
    {
        var sale = await SellAsync(SalePaymentModes.Cash);
        var stockAfterSale = server.StockLevels[FakeServer.Creme.Id];
        var (_, returns) = await OpenReturnsAsync();
        await ScanTicketAsync(returns, sale.TicketNumber);

        await PrepareAsync(returns, "1", FakeServer.Resellable);
        Assert.Equal(ReturnStep.Confirm, returns.Step);

        // Échap : retour au choix, rien n'est envoyé ; Échap encore : abandon du ticket.
        returns.Back();
        Assert.Equal(ReturnStep.Choose, returns.Step);
        Assert.Equal(ReturnField.Note, returns.Field);
        returns.Back();
        returns.Back();
        returns.Back();
        Assert.Equal(ReturnStep.Search, returns.Step);
        Assert.False(returns.HasTicket);

        Assert.Empty(server.ReturnRequests);
        Assert.Empty(server.RecordedReturns);
        Assert.Equal(stockAfterSale, server.StockLevels[FakeServer.Creme.Id]);
    }

    [Fact]
    public async Task Un_retour_confirme_part_avec_le_remboursement_affiche_et_l_ecran_de_fin_montre_l_argent_a_rendre()
    {
        var sale = await SellAsync(SalePaymentModes.Cash);
        var stockAfterSale = server.StockLevels[FakeServer.Creme.Id];
        var (_, returns) = await OpenReturnsAsync();
        await ScanTicketAsync(returns, sale.TicketBarcode);
        returns.Lines[0].QuantityText = "1";
        returns.SelectedReason = Reason(returns, FakeServer.Resellable);
        returns.Note = "  Boîte non ouverte ";
        await returns.RequestPreviewAsync();

        await returns.ConfirmAsync();

        var request = Assert.Single(server.ReturnRequests);
        Assert.Equal(sale.Id, request.SaleId);
        Assert.Equal(FakeServer.Resellable.Id, request.ReasonId);
        Assert.Equal(1850.10m, request.ExpectedRefund);
        Assert.Equal("Boîte non ouverte", request.Note);
        Assert.Equal((FakeServer.Creme.Id, 1m), (request.Lines[0].ProductId, request.Lines[0].Quantity));

        Assert.Equal(ReturnStep.Done, returns.Step);
        Assert.Equal($"Retour D1-R000001 — ticket {sale.TicketNumber}", returns.DoneNumberText);
        Assert.Equal("1 850,10 DA", returns.DoneRefundText);
        Assert.True(returns.IsDoneCash);
        Assert.False(returns.IsDoneCard);
        Assert.Equal("Remise en stock vendable", returns.DoneDestinationText);
        Assert.Same(server.RecordedReturns[0], Assert.Single(printer.PrintedReturns));
        Assert.Equal(stockAfterSale + 1, server.StockLevels[FakeServer.Creme.Id]);

        // Un appui : retour à la recherche de ticket, rien de l'ancien ticket ne reste.
        returns.StartNextReturn();
        Assert.Equal(ReturnStep.Search, returns.Step);
        Assert.False(returns.HasTicket);
        Assert.Empty(returns.Lines);
        Assert.Null(returns.SelectedReason);
        Assert.Equal(string.Empty, returns.Note);
    }

    [Fact]
    public async Task Chaque_motif_envoie_la_marchandise_a_sa_destination_et_un_ticket_TPE_est_rembourse_par_carte()
    {
        var sale = await SellAsync(SalePaymentModes.Card, [(FakeServer.Creme, 3m)]);
        await server.ResolveCardAsync(sale.Id, new CardResultRequest(Guid.NewGuid(), true), CancellationToken.None);
        var stockAfterSale = server.StockLevels[FakeServer.Creme.Id];
        var (_, returns) = await OpenReturnsAsync();

        foreach (var reason in new[] { FakeServer.Damaged, FakeServer.Defective, FakeServer.Resellable })
        {
            await ScanTicketAsync(returns, sale.TicketNumber);
            await PrepareAsync(returns, "1", reason);
            await returns.ConfirmAsync();
            Assert.Equal(ReturnStep.Done, returns.Step);
            Assert.True(returns.IsDoneCard);
            Assert.Equal("1 850,10 DA", returns.DoneRefundText);
            returns.StartNextReturn();
        }

        // Endommagé : rien ; défectueux : mis à l'écart ; revendable : stock vendable.
        Assert.Equal(stockAfterSale + 1, server.StockLevels[FakeServer.Creme.Id]);
        Assert.Equal(1m, server.SetAsideStock[FakeServer.Creme.Id]);
        Assert.Equal([0m, 1m, 1m], server.RecordedReturns.Select(done => done.Lines[0].RestockedQuantity));
        Assert.Equal(sale.Total, server.RecordedReturns.Sum(done => done.RefundAmount));
    }

    [Fact]
    public async Task Apres_une_panne_reseau_Reessayer_renvoie_le_meme_identifiant_d_operation_et_le_retour_n_est_enregistre_qu_une_fois()
    {
        var sale = await SellAsync(SalePaymentModes.Cash);
        var (_, returns) = await OpenReturnsAsync();
        await ScanTicketAsync(returns, sale.TicketNumber);
        await PrepareAsync(returns, "2", FakeServer.Resellable);

        // Le serveur enregistre, mais la réponse se perd ; puis la demande suivante se perd en route.
        server.ReturnFailures.Enqueue(SaleSendFailure.AnswerLost);
        server.ReturnFailures.Enqueue(SaleSendFailure.RequestLost);
        await returns.ConfirmAsync();

        Assert.Equal(ReturnStep.Unconfirmed, returns.Step);
        Assert.Equal("Retour non confirmé — ne pas rendre l'argent", ReturnsViewModel.UnconfirmedTitle);
        Assert.Empty(printer.PrintedReturns);

        // Échap ne fait pas sortir de l'état « non confirmé » : seul « Réessayer » le peut.
        returns.Back();
        Assert.Equal(ReturnStep.Unconfirmed, returns.Step);

        await returns.RetryAsync();
        Assert.Equal(ReturnStep.Unconfirmed, returns.Step);
        await returns.RetryAsync();

        Assert.Equal(ReturnStep.Done, returns.Step);
        Assert.Equal(3, server.ReturnRequests.Count);
        Assert.Single(server.ReturnRequests.Select(request => request.OperationId).Distinct());
        Assert.Single(server.RecordedReturns);
        Assert.Single(printer.PrintedReturns);
        Assert.Equal("3 700,20 DA", returns.DoneRefundText);
    }

    [Fact]
    public async Task Un_double_declenchement_ne_produit_qu_un_seul_envoi()
    {
        var sale = await SellAsync(SalePaymentModes.Cash);
        var (_, returns) = await OpenReturnsAsync();
        await ScanTicketAsync(returns, sale.TicketNumber);
        await PrepareAsync(returns, "1", FakeServer.Resellable);
        server.ReturnGate = new TaskCompletionSource();

        var first = returns.ConfirmAsync();
        var second = returns.ConfirmAsync();
        await returns.ConfirmCommand.ExecuteAsync(null);
        Assert.True(returns.IsSending);
        server.ReturnGate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Single(server.ReturnRequests);
        Assert.Single(server.RecordedReturns);
        Assert.Equal(ReturnStep.Done, returns.Step);
    }

    [Fact]
    public async Task Un_remboursement_different_cote_serveur_refait_l_apercu_et_redemande_la_confirmation()
    {
        var sale = await SellAsync(SalePaymentModes.Cash);
        var (_, returns) = await OpenReturnsAsync();
        await ScanTicketAsync(returns, sale.TicketNumber);
        await PrepareAsync(returns, "1", FakeServer.Resellable);
        Assert.Equal("1 850,10 DA", returns.PreviewRefundText);

        server.RefundDrift = 0.05m;
        await returns.ConfirmAsync();

        // Rien n'est enregistré : un nouvel aperçu est affiché avec le montant du serveur, à reconfirmer.
        Assert.Empty(server.RecordedReturns);
        Assert.Equal(ReturnStep.Confirm, returns.Step);
        Assert.Equal(2, server.PreviewRequests.Count);
        Assert.Equal("1 850,15 DA", returns.PreviewRefundText);
        Assert.Equal(
            "Le remboursement a changé : 1 850,15 DA au lieu de 1 850,10 DA. Vérifiez avant de rembourser.", returns.RefundChangedMessage);

        await returns.ConfirmAsync();

        Assert.Equal(ReturnStep.Done, returns.Step);
        Assert.Equal(2, server.ReturnRequests.Count);
        Assert.Equal(1850.15m, server.ReturnRequests[1].ExpectedRefund);
        Assert.NotEqual(server.ReturnRequests[0].OperationId, server.ReturnRequests[1].OperationId);
        Assert.Equal(1850.15m, Assert.Single(server.RecordedReturns).RefundAmount);
    }

    [Fact]
    public async Task Les_refus_du_serveur_sont_affiches_tels_quels_et_le_ticket_est_relu()
    {
        var sale = await SellAsync(SalePaymentModes.Cash);
        var (_, returns) = await OpenReturnsAsync();
        await ScanTicketAsync(returns, sale.TicketNumber);
        await PrepareAsync(returns, "2", FakeServer.Resellable);

        // Un autre poste retourne une crème entre l'aperçu et la confirmation.
        await server.CreateReturnAsync(
            new CreateReturnRequest(Guid.NewGuid(), sale.Id, FakeServer.Resellable.Id, [new ReturnLineRequest(FakeServer.Creme.Id, 1m)]),
            CancellationToken.None);
        await returns.ConfirmAsync();

        Assert.Equal(
            "« Crème hydratante 50 ml » : 2 demandé en retour, 1 encore retournable (2 vendu, 1 déjà retourné).", returns.ErrorMessage);
        Assert.Equal(ReturnErrorCodes.QuantityExceeded, "QUANTITE_RETOUR_DEPASSEE");
        Assert.Equal(ReturnStep.Choose, returns.Step);
        Assert.Equal("1", returns.Lines[0].ReturnedText);
        Assert.Equal("1", returns.Lines[0].ReturnableText);
        Assert.Equal("2", returns.Lines[0].QuantityText);
        Assert.Single(server.RecordedReturns);

        // Caisse fermée entre-temps : le refus du serveur est affiché, rien n'est enregistré.
        returns.Lines[0].QuantityText = "1";
        await returns.RequestPreviewAsync();
        server.CashSession = null;
        await returns.ConfirmAsync();
        Assert.Equal(
            "La caisse de ce poste n'est pas ouverte. Ouvrez la caisse avant de vendre ou d'encaisser.", returns.ErrorMessage);
        Assert.Single(server.RecordedReturns);

        // Motif désactivé entre l'aperçu et la confirmation : refus affiché, la liste des motifs est relue.
        server.WithOpenCashSession();
        returns.SelectedReason = Reason(returns, FakeServer.Damaged);
        await returns.RequestPreviewAsync();
        server.ReturnReasons[1] = FakeServer.Damaged with { IsActive = false };
        await returns.ConfirmAsync();
        Assert.Equal("Choisissez un motif de retour dans la liste des motifs actifs.", returns.ErrorMessage);
        Assert.DoesNotContain(returns.Reasons, reason => reason.Reason.Id == FakeServer.Damaged.Id);
        Assert.Null(returns.SelectedReason);
        Assert.Single(server.RecordedReturns);
    }

    // ---------- Historique ----------

    [Fact]
    public async Task L_historique_des_retours_se_consulte_par_dates_et_par_ticket_avec_le_detail()
    {
        var first = await SellAsync(SalePaymentModes.Cash);
        var second = await SellAsync(SalePaymentModes.Cash, [(FakeServer.Serum, 1m)]);
        var (_, returns) = await OpenReturnsAsync();
        foreach (var (ticket, reason) in new[] { (first, FakeServer.Damaged), (second, FakeServer.Resellable) })
        {
            await ScanTicketAsync(returns, ticket.TicketNumber);
            returns.Note = ticket == first ? "Flacon fendu" : string.Empty;
            await PrepareAsync(returns, "1", reason);
            await returns.ConfirmAsync();
            returns.StartNextReturn();
        }

        await returns.ShowHistoryAsync();
        var history = returns.History;
        Assert.True(returns.IsHistoryShown);
        var today = DateOnly.FromDateTime(DateTime.Now);
        Assert.Equal((today, today), (server.ReturnHistoryQueries[0].From, server.ReturnHistoryQueries[0].To));
        Assert.Equal(["D1-R000002", "D1-R000001"], history.Returns.Select(row => row.ReturnNumber));
        Assert.Equal("4 200,20 DA", history.Returns[0].RefundText);
        Assert.Equal("Espèces", history.Returns[0].MethodText);
        await Task.Yield();
        Assert.Equal("Retour D1-R000002", history.Detail!.Title);

        // Par ticket.
        history.TicketQuery = first.TicketNumber;
        await history.SearchAsync();
        var row = Assert.Single(history.Returns);
        Assert.Equal(first.TicketNumber, row.TicketNumber);
        Assert.Equal(FakeServer.Damaged.Label, row.ReasonLabel);
        await history.LoadDetailAsync(row.Id);
        Assert.Equal("Pas de remise en stock", history.Detail!.DestinationText);
        Assert.Equal("Note : Flacon fendu", history.Detail.NoteText);
        Assert.Equal(["Crème hydratante 50 ml — 1 rendu (1 non remis en stock) — 1 850,10 DA"], history.Detail.Lines);

        // Dates invalides : signalées, rien n'est demandé.
        var queries = server.ReturnHistoryQueries.Count;
        history.FromText = "32/13/2026";
        await history.SearchAsync();
        Assert.StartsWith("Date invalide", history.ErrorMessage);
        Assert.Equal(queries, server.ReturnHistoryQueries.Count);

        returns.HideHistory();
        Assert.False(returns.IsHistoryShown);
    }

    // ---------- Navigation et droits ----------

    [Fact]
    public async Task Le_retour_s_ouvre_depuis_le_detail_d_un_ticket_de_l_historique_et_Echap_revient_a_la_vente()
    {
        var sale = await SellAsync(SalePaymentModes.Cash);
        var module = CreateModule();
        await module.ActivateAsync();
        await module.OpenHistoryAsync();
        Assert.True(module.History.CanReturnSelected);

        await module.History.BeginReturnAsync();

        Assert.Equal(SalesScreen.Returns, module.Screen);
        Assert.Equal(ReturnStep.Choose, module.Returns.Step);
        Assert.Equal(sale.Id, module.Returns.Sale!.SaleId);

        // Retour en cours de confirmation : on ne quitte pas l'écran.
        await PrepareAsync(module.Returns, "1", FakeServer.Resellable);
        await module.CloseReturnsAsync();
        Assert.Equal(SalesScreen.Returns, module.Screen);
        module.Returns.Back();
        await module.CloseReturnsAsync();
        Assert.Equal(SalesScreen.Cart, module.Screen);
    }

    [Fact]
    public async Task Sans_le_droit_de_retour_la_touche_et_les_actions_de_retour_sont_masquees()
    {
        var sale = await SellAsync(SalePaymentModes.Cash);
        Without(Permissions.ReturnsCreate);
        var module = CreateModule();
        await module.ActivateAsync();

        Assert.False(module.CanReturn);
        await module.OpenReturnsAsync();
        Assert.Equal(SalesScreen.Cart, module.Screen);

        await module.OpenHistoryAsync();
        Assert.Equal(sale.Id, module.History.Detail!.Sale.Id);
        Assert.False(module.History.CanReturnSelected);
        await module.History.BeginReturnAsync();
        Assert.Equal(SalesScreen.History, module.Screen);
        Assert.Empty(server.PreviewRequests);
    }

    [Fact]
    public async Task Avec_le_seul_droit_de_retour_le_module_ouvre_les_retours()
    {
        server.EmployeePermissions = [Permissions.ReturnsCreate];
        var module = CreateModule();

        await module.ActivateAsync();

        Assert.Equal(SalesScreen.Returns, module.Screen);
        Assert.True(module.CanReturn);
        Assert.False(module.CanViewHistory);
        Assert.False(module.Returns.CanOpenSaleInHistory);
        Assert.Equal(4, module.Returns.Reasons.Count);

        // Pas d'écran de vente vers lequel revenir.
        await module.CloseReturnsAsync();
        Assert.Equal(SalesScreen.Returns, module.Screen);
    }

    [Fact]
    public void Le_module_Vente_reste_visible_avec_le_seul_droit_de_retour_et_les_motifs_s_administrent_avec_admin_utilisateurs()
    {
        var sales = ModuleCatalog.All.Single(module => module.Key == ModuleKeys.Sales);
        Assert.Contains(Permissions.ReturnsCreate, sales.Permissions);
        var administration = ModuleCatalog.All.Single(module => module.Key == ModuleKeys.Administration);
        Assert.Equal([Permissions.UsersAdminister], administration.Permissions);
    }
}
