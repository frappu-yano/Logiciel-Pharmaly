using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Modules.Cash;
using Pharmaly.Poste.Modules.Sales;

namespace Pharmaly.Tests.Poste;

/// <summary>Fermeture de la caisse du poste (comptage des espèces) et clôture quotidienne du magasin.</summary>
public sealed class ClosureViewModelTests
{
    private static readonly DateOnly Today = new(2026, 11, 16);

    private readonly FakeServer server =
        new FakeServer { HasPosteSession = true, EmployeeIdentified = true, StoreToday = Today }.AsAdministrator().WithOpenCashSession();

    private readonly RecordingPrinter printer = new();

    private sealed class RecordingPrinter : IClosureReportPrinter
    {
        public List<DailyClosureDetail> Printed { get; } = [];

        public void Print(DailyClosureDetail closure) => Printed.Add(closure);
    }

    private CashViewModel Create() =>
        new(server, server, server, server, CashRights.From(code => server.EmployeePermissions.Contains(code)), () => Today, printer);

    private void Without(params string[] permissions) =>
        server.EmployeePermissions = server.EmployeePermissions.Except(permissions).ToList();

    private async Task<CashViewModel> OpenTabAsync(CashTab tab)
    {
        var viewModel = Create();
        await viewModel.ActivateAsync();
        await viewModel.ShowTabAsync(tab);
        return viewModel;
    }

    /// <summary>F10, montant compté, Entrée (contrôle), Entrée (confirmation).</summary>
    private static async Task CloseRegisterAsync(CashViewModel viewModel, string counted)
    {
        viewModel.BeginCloseRegister();
        viewModel.CountedCashText = counted;
        viewModel.ValidateCountedCash();
        await viewModel.ConfirmCloseRegisterAsync();
    }

    private static ReportLine Line(IEnumerable<ReportLine> lines, string label) => lines.Single(line => line.Label == label);

    /// <summary>Fond 5 000 + ventes en espèces 1 500 = 6 500 attendus.</summary>
    private void WithCashSales() => server.AddDaySale(SalePaymentModes.Cash, 1500m);

    // ---------- Fermeture de la caisse du poste ----------

    [Fact]
    public async Task Fermer_la_caisse_sans_ecart_affiche_le_rapport_de_session_avec_un_ecart_nul()
    {
        WithCashSales();
        var viewModel = await OpenTabAsync(CashTab.Session);
        Assert.True(viewModel.CanShowCloseRegister);

        viewModel.BeginCloseRegister();
        Assert.Equal(SessionStep.CloseCount, viewModel.SessionStep);
        viewModel.CountedCashText = "6500";
        viewModel.ValidateCountedCash();
        Assert.Equal(SessionStep.CloseConfirm, viewModel.SessionStep);
        Assert.Equal("6 500,00 DA", viewModel.CountedCashDisplayText);
        Assert.Empty(server.CloseSessionRequests);

        await viewModel.ConfirmCloseRegisterAsync();

        Assert.Equal(SessionStep.CloseDone, viewModel.SessionStep);
        var report = viewModel.ClosedSessionReport!;
        Assert.Equal("6 500,00 DA", report.ExpectedText);
        Assert.Equal("6 500,00 DA", report.CountedText);
        Assert.Equal("0,00 DA", report.DifferenceText);
        Assert.False(report.IsDifferenceAlert);
        Assert.Equal(6500m, Assert.Single(server.CloseSessionRequests).CountedCash);
        Assert.Null(server.CashSession);
        Assert.False(viewModel.IsCashOpen);

        await viewModel.FinishCloseRegisterAsync();
        Assert.Equal(SessionStep.None, viewModel.SessionStep);
        Assert.Equal("Caisse FERMÉE", viewModel.CashStateText);
        Assert.Equal("Caisse 1 fermée. Écart : 0,00 DA.", viewModel.InfoMessage);
        Assert.True(viewModel.CanShowOpening);
    }

    [Fact]
    public async Task Fermer_la_caisse_avec_un_manque_met_l_ecart_en_evidence()
    {
        WithCashSales();
        var viewModel = await OpenTabAsync(CashTab.Session);

        await CloseRegisterAsync(viewModel, "6450,50");

        var report = viewModel.ClosedSessionReport!;
        Assert.Equal("6 450,50 DA", report.CountedText);
        Assert.Equal("− 49,50 DA (manque)", report.DifferenceText);
        Assert.True(report.IsDifferenceAlert);
        Assert.Contains("manque", report.DifferenceExplanation);
    }

    [Fact]
    public async Task Fermer_la_caisse_avec_un_excedent_met_l_ecart_en_evidence()
    {
        var viewModel = await OpenTabAsync(CashTab.Session);

        await CloseRegisterAsync(viewModel, "5020");

        Assert.Equal("+ 20,00 DA (excédent)", viewModel.ClosedSessionReport!.DifferenceText);
        Assert.True(viewModel.ClosedSessionReport.IsDifferenceAlert);
    }

    [Theory]
    [InlineData("", "Saisissez les espèces comptées dans le tiroir (0 s'il est vide).")]
    [InlineData("12,345", "Montant invalide : un montant positif ou nul, avec au plus deux décimales (virgule ou point).")]
    [InlineData("-5", "Montant invalide : un montant positif ou nul, avec au plus deux décimales (virgule ou point).")]
    public async Task Un_comptage_invalide_est_signale_et_rien_n_est_envoye(string counted, string message)
    {
        var viewModel = await OpenTabAsync(CashTab.Session);
        viewModel.BeginCloseRegister();
        viewModel.CountedCashText = counted;

        viewModel.ValidateCountedCash();

        Assert.Equal(SessionStep.CloseCount, viewModel.SessionStep);
        Assert.Equal(message, viewModel.SessionStepError);
        Assert.Empty(server.CloseSessionRequests);
    }

    [Fact]
    public async Task Un_paiement_TPE_en_attente_bloque_la_fermeture_et_la_liste_de_ce_qui_bloque_est_affichee()
    {
        server.PendingSaleCards.Add(("D1-000042", 1200m));
        var viewModel = await OpenTabAsync(CashTab.Session);

        await CloseRegisterAsync(viewModel, "5000");

        Assert.Equal(SessionStep.CloseBlocked, viewModel.SessionStep);
        Assert.StartsWith("La caisse ne peut pas être fermée : 1 paiement(s) par carte en attente de réponse.", viewModel.CloseBlockedMessage);
        var blocker = Assert.Single(viewModel.CloseBlockers);
        Assert.Equal("Paiement TPE en attente — Caisse 1", blocker.Title);
        Assert.Contains("Ticket D1-000042", blocker.Message);
        Assert.Contains("historique des ventes", blocker.Hint);
        Assert.Contains("Ctrl+T", CashViewModel.CloseBlockedHelp);
        Assert.Contains("historique des ventes", CashViewModel.CloseBlockedHelp);
        Assert.NotNull(server.CashSession);

        await viewModel.FinishCloseRegisterAsync();
        Assert.Equal(SessionStep.None, viewModel.SessionStep);
        Assert.True(viewModel.IsCashOpen);
        Assert.Empty(viewModel.CloseBlockers);
    }

    [Fact]
    public async Task Un_reglement_TPE_en_attente_bloque_aussi_la_fermeture()
    {
        var old = server.AddReceivable(FakeServer.Benali, 3500m, DateTimeOffset.Now.AddDays(-3));
        await server.SettleAsync(new SettleReceivablesRequest(Guid.NewGuid(), [old.Id], PaymentMethods.Card, 3500m), CancellationToken.None);
        var viewModel = await OpenTabAsync(CashTab.Session);

        await CloseRegisterAsync(viewModel, "5000");

        Assert.Equal(SessionStep.CloseBlocked, viewModel.SessionStep);
        var blocker = Assert.Single(viewModel.CloseBlockers);
        Assert.Equal(ClosureBlockerKinds.SettlementCardPending, blocker.Blocker.Kind);
        Assert.Contains("Ctrl+T", blocker.Hint);
    }

    [Fact]
    public async Task Sans_le_droit_de_consulter_les_rapports_le_refus_reste_affiche_tel_quel()
    {
        Without(Permissions.RegisterReportsView);
        server.PendingSaleCards.Add(("D1-000042", 1200m));
        var viewModel = await OpenTabAsync(CashTab.Session);

        await CloseRegisterAsync(viewModel, "5000");

        Assert.Equal(SessionStep.CloseBlocked, viewModel.SessionStep);
        Assert.Contains("Ticket D1-000042", viewModel.CloseBlockedMessage);
        Assert.False(viewModel.HasCloseBlockers);
    }

    [Fact]
    public async Task Panne_reseau_pendant_la_fermeture_puis_Reessayer_renvoie_le_meme_identifiant_d_operation()
    {
        server.CloseSessionFailures.Enqueue(SaleSendFailure.AnswerLost);
        server.CloseSessionFailures.Enqueue(SaleSendFailure.RequestLost);
        var viewModel = await OpenTabAsync(CashTab.Session);

        await CloseRegisterAsync(viewModel, "5000");
        Assert.Equal(SessionStep.CloseUnconfirmed, viewModel.SessionStep);

        // Échap n'abandonne pas une fermeture dont on ignore le sort.
        viewModel.CancelSessionStep();
        Assert.Equal(SessionStep.CloseUnconfirmed, viewModel.SessionStep);

        await viewModel.RetryCloseRegisterAsync();
        Assert.Equal(SessionStep.CloseUnconfirmed, viewModel.SessionStep);
        await viewModel.RetryCloseRegisterAsync();

        Assert.Equal(SessionStep.CloseDone, viewModel.SessionStep);
        Assert.Equal(3, server.CloseSessionRequests.Count);
        Assert.Single(server.CloseSessionRequests.Select(request => request.OperationId).Distinct());
        Assert.Equal(1, server.SessionsClosed);
        Assert.Equal("0,00 DA", viewModel.ClosedSessionReport!.DifferenceText);
    }

    [Fact]
    public async Task Une_nouvelle_fermeture_apres_un_refus_porte_un_nouvel_identifiant()
    {
        server.PendingSaleCards.Add(("D1-000042", 1200m));
        var viewModel = await OpenTabAsync(CashTab.Session);
        await CloseRegisterAsync(viewModel, "5000");
        await viewModel.FinishCloseRegisterAsync();
        server.PendingSaleCards.Clear();

        await CloseRegisterAsync(viewModel, "5000");

        Assert.Equal(SessionStep.CloseDone, viewModel.SessionStep);
        Assert.Equal(2, server.CloseSessionRequests.Select(request => request.OperationId).Distinct().Count());
    }

    [Fact]
    public async Task Sans_le_droit_de_cloturer_la_fermeture_de_caisse_n_est_pas_proposee()
    {
        Without(Permissions.RegisterClose);
        var viewModel = await OpenTabAsync(CashTab.Session);

        Assert.False(viewModel.CanShowCloseRegister);
        viewModel.BeginCloseRegister();
        Assert.Equal(SessionStep.None, viewModel.SessionStep);
    }

    // ---------- État de la journée ----------

    [Fact]
    public async Task L_etat_de_la_journee_montre_les_caisses_et_tout_ce_qui_bloque_la_cloture()
    {
        server.AddOtherRegister("Caisse 2", 8000m, 7950m);
        server.AddOtherRegister("Caisse 3", 3000m, null);
        server.PendingSaleCards.Add(("D1-000042", 1200m));
        server.OfflineOperations = 2;

        var viewModel = await OpenTabAsync(CashTab.Closing);

        Assert.Equal("Journée du 16/11/2026 — Pharmaly 1 — Didouche", viewModel.DayTitle);
        Assert.Equal("Aucune journée clôturée pour ce magasin.", viewModel.LastClosedText);
        Assert.Equal(
            [ClosureBlockerKinds.SessionOpen, ClosureBlockerKinds.SessionOpen, ClosureBlockerKinds.SaleCardPending, ClosureBlockerKinds.OfflineOperations],
            viewModel.DayBlockers.Select(blocker => blocker.Blocker.Kind));
        Assert.Equal("Caisse ouverte — Caisse 1", viewModel.DayBlockers[0].Title);
        Assert.Equal("Caisse ouverte — Caisse 3", viewModel.DayBlockers[1].Title);
        Assert.Equal("Opérations hors ligne non synchronisées", viewModel.DayBlockers[3].Title);
        Assert.True(viewModel.DayBlockers.All(blocker => blocker.HasHint));
        Assert.Equal("Clôture impossible : 4 points bloquent.", viewModel.DayStateText);
        Assert.True(viewModel.HasPendingOffline);
        Assert.StartsWith("2 opérations hors ligne ne sont pas synchronisées", viewModel.PendingOfflineText);

        var registers = viewModel.DayPreview!.Registers;
        Assert.Equal(["Caisse 1", "Caisse 2", "Caisse 3"], registers.Select(row => row.RegisterName));
        Assert.True(registers[0].IsOpen);
        Assert.Equal("—", registers[0].CountedText);
        Assert.Equal("1 paiement TPE en attente", registers[0].PendingCardText);
        Assert.False(registers[1].IsOpen);
        Assert.Equal("Fermée par Karim Autre à " + ClosureFormat.Time(registers[1].Session.ClosedAt!.Value), registers[1].ClosedText);
        Assert.Equal("7 950,00 DA", registers[1].CountedText);
        Assert.Equal("− 50,00 DA (manque)", registers[1].DifferenceText);
        Assert.True(registers[1].IsDifferenceAlert);

        Assert.False(viewModel.IsDayReady);
        Assert.False(viewModel.CanCloseDay);
        Assert.False(viewModel.CanShowForcedClose);
    }

    [Fact]
    public async Task Les_totaux_sont_presentes_en_trois_blocs_sans_confondre_chiffre_d_affaires_et_tresorerie()
    {
        // Une créance d'il y a trois jours, réglée aujourd'hui en espèces : trésorerie, jamais chiffre d'affaires.
        var old = server.AddReceivable(FakeServer.Haddad, 3500m, DateTimeOffset.Now.AddDays(-3));
        await server.SettleAsync(
            new SettleReceivablesRequest(Guid.NewGuid(), [old.Id], PaymentMethods.Cash, 3500m, 3500m), CancellationToken.None);
        server.AddDaySale(SalePaymentModes.Cash, 2000m, discount: 100m);
        server.AddDaySale(SalePaymentModes.Card, 800m);

        // Une vente impayée : chiffre d'affaires, jamais trésorerie.
        server.AddDaySale(SalePaymentModes.Unpaid, 1234m, customer: FakeServer.Benali);
        await server.RecordMovementAsync(
            new CashMovementRequest(Guid.NewGuid(), CashMovementKinds.Out, 300m, "Timbres"), CancellationToken.None);

        var viewModel = await OpenTabAsync(CashTab.Closing);
        var preview = viewModel.DayPreview!;

        Assert.Equal("4 134,00 DA", Line(preview.RevenueLines, "Ventes avant remise").Amount);
        Assert.Equal("3 ventes", Line(preview.RevenueLines, "Ventes avant remise").Note);
        Assert.Equal("− 100,00 DA", Line(preview.RevenueLines, "Remises").Amount);
        Assert.Equal("2 000,00 DA", Line(preview.RevenueLines, "dont comptant en espèces").Amount);
        Assert.Equal("800,00 DA", Line(preview.RevenueLines, "dont comptant par TPE").Amount);
        Assert.Equal("1 234,00 DA", Line(preview.RevenueLines, "dont impayées").Amount);
        Assert.Equal("4 034,00 DA", Line(preview.RevenueLines, "CHIFFRE D'AFFAIRES NET").Amount);
        Assert.Equal("4 034,00 DA", preview.NetRevenueText);
        Assert.DoesNotContain(preview.RevenueLines, line => line.Amount.Contains("3 500,00"));

        Assert.Equal("2 000,00 DA", Line(preview.TreasuryCashLines, "Ventes du jour encaissées").Amount);
        Assert.Equal("3 500,00 DA", Line(preview.TreasuryCashLines, "Règlements d'anciennes créances").Amount);
        Assert.Equal("0,00 DA", Line(preview.TreasuryCashLines, "Impayés du jour réglés le jour même").Amount);
        Assert.Equal("− 300,00 DA", Line(preview.TreasuryCashLines, "Sorties d'espèces").Amount);
        Assert.Equal("5 200,00 DA", Line(preview.TreasuryCashLines, "Variation des espèces").Amount);
        Assert.Equal("10 200,00 DA", Line(preview.TreasuryCashLines, "Espèces attendues").Amount);
        Assert.Equal("800,00 DA", Line(preview.TreasuryCardLines, "Total TPE").Amount);
        Assert.DoesNotContain(preview.TreasuryCashLines.Concat(preview.TreasuryCardLines), line => line.Amount.Contains("1 234,00"));

        Assert.Equal("1 234,00 DA", Line(preview.ReceivableLines, "Impayés créés ce jour").Amount);
        Assert.Equal("1 ticket", Line(preview.ReceivableLines, "Impayés créés ce jour").Note);
        Assert.Equal("3 500,00 DA", Line(preview.ReceivableLines, "Créances soldées ce jour").Amount);
        Assert.Equal("1 234,00 DA", Line(preview.ReceivableLines, "Encours").Amount);
    }

    [Fact]
    public async Task Un_impaye_du_jour_regle_le_jour_meme_apparait_a_part_des_anciennes_creances()
    {
        server.AddDaySale(SalePaymentModes.Unpaid, 900m, customer: FakeServer.Benali);
        var sameDay = server.ReceivableLines.Single();
        await server.SettleAsync(
            new SettleReceivablesRequest(Guid.NewGuid(), [sameDay.Id], PaymentMethods.Cash, 900m, 900m), CancellationToken.None);

        var viewModel = await OpenTabAsync(CashTab.Closing);

        var cash = viewModel.DayPreview!.TreasuryCashLines;
        Assert.Equal("900,00 DA", Line(cash, "Impayés du jour réglés le jour même").Amount);
        Assert.Equal("0,00 DA", Line(cash, "Règlements d'anciennes créances").Amount);
        Assert.Equal("900,00 DA", Line(viewModel.DayPreview.RevenueLines, "CHIFFRE D'AFFAIRES NET").Amount);
    }

    // ---------- Clôture de la journée ----------

    [Fact]
    public async Task La_cloture_est_impossible_tant_qu_une_caisse_est_ouverte()
    {
        var viewModel = await OpenTabAsync(CashTab.Closing);
        Assert.True(viewModel.CanShowCloseDay);
        Assert.False(viewModel.CanCloseDay);

        viewModel.BeginCloseDay();

        Assert.Equal(ClosingStep.None, viewModel.ClosingStep);
        Assert.Empty(server.CloseDayRequests);
    }

    [Fact]
    public async Task Cloture_reussie_apres_fermeture_de_la_caisse_affiche_le_rapport_fige()
    {
        WithCashSales();
        var viewModel = await OpenTabAsync(CashTab.Session);
        await CloseRegisterAsync(viewModel, "6480");
        await viewModel.FinishCloseRegisterAsync();
        await viewModel.ShowTabAsync(CashTab.Closing);
        Assert.True(viewModel.CanCloseDay);
        Assert.Equal("Rien ne bloque : la journée peut être clôturée.", viewModel.DayStateText);

        viewModel.BeginCloseDay();
        Assert.Equal(ClosingStep.Confirm, viewModel.ClosingStep);
        Assert.Equal("16/11/2026", viewModel.ClosingDateText);
        Assert.Empty(server.CloseDayRequests);

        // Une autre touche que F10 ne clôture pas : la vue ne transmet que F10.
        await viewModel.ConfirmCloseDayAsync();

        Assert.Equal(ClosingStep.None, viewModel.ClosingStep);
        var request = Assert.Single(server.CloseDayRequests);
        Assert.Equal(Today, request.BusinessDate);
        Assert.False(request.ForceWithUnknownOperations);
        Assert.Null(request.ForceReason);
        Assert.True(viewModel.IsReportMode);
        Assert.Equal("Clôture du 16/11/2026 — Pharmaly 1 — Didouche — rapport figé", viewModel.OpenedClosureTitle);
        Assert.StartsWith($"Clôturée par {FakeServer.EmployeeName} le ", viewModel.OpenedClosureByText);
        Assert.False(viewModel.IsOpenedClosureForced);
        Assert.Equal("Journée du 16/11/2026 clôturée : le rapport ci-dessous est figé.", viewModel.InfoMessage);
        var report = viewModel.OpenedReport!;
        Assert.Equal("1 500,00 DA", report.NetRevenueText);
        Assert.Equal("− 20,00 DA (manque)", Line(report.TreasuryCashLines, "Écart").Amount);
        Assert.True(Line(report.TreasuryCashLines, "Écart").IsAlert);
        Assert.Equal("− 20,00 DA (manque)", Assert.Single(report.Registers).DifferenceText);
        Assert.Same(server.DailyClosures.Single(), Assert.Single(printer.Printed));

        // Échap : retour à la journée, désormais clôturée.
        await viewModel.BackInClosingAsync();
        Assert.True(viewModel.IsDayMode);
        Assert.False(viewModel.CanCloseDay);
        Assert.Equal(
            "La journée du 16/11/2026 est déjà clôturée. Les opérations saisies depuis appartiennent à la journée suivante.",
            viewModel.DayStateText);
        Assert.Equal("Dernière journée clôturée : 16/11/2026", viewModel.LastClosedText);
    }

    [Fact]
    public async Task Une_double_cloture_est_refusee_par_le_serveur_et_le_refus_est_affiche_tel_quel()
    {
        server.CashSession = null;
        var viewModel = await OpenTabAsync(CashTab.Closing);
        Assert.True(viewModel.CanCloseDay);

        // Un autre poste clôture la journée pendant que l'écran reste affiché.
        server.AddPastClosure(Today, 1000m);
        viewModel.BeginCloseDay();
        await viewModel.ConfirmCloseDayAsync();

        Assert.Equal(
            "La journée du 16/11/2026 est déjà clôturée pour ce magasin. Les opérations saisies depuis appartiennent à la journée suivante.",
            viewModel.ErrorMessage);
        Assert.Equal(ClosingStep.None, viewModel.ClosingStep);
        Assert.True(viewModel.IsDayMode);
        Assert.False(viewModel.CanCloseDay);
        Assert.Single(server.DailyClosures);
        Assert.Empty(printer.Printed);
    }

    [Fact]
    public async Task Un_blocage_apparu_entre_temps_est_refuse_et_la_journee_est_relue()
    {
        server.CashSession = null;
        var viewModel = await OpenTabAsync(CashTab.Closing);
        server.AddOtherRegister("Caisse 2", 1000m, null);

        viewModel.BeginCloseDay();
        await viewModel.ConfirmCloseDayAsync();

        Assert.StartsWith("La journée ne peut pas être clôturée. La caisse « Caisse 2 » est encore ouverte", viewModel.ErrorMessage);
        Assert.Equal(ClosureBlockerKinds.SessionOpen, Assert.Single(viewModel.DayBlockers).Blocker.Kind);
        Assert.False(viewModel.CanCloseDay);
    }

    [Fact]
    public async Task Panne_reseau_pendant_la_cloture_puis_Reessayer_renvoie_le_meme_identifiant_d_operation()
    {
        server.CashSession = null;
        server.CloseDayFailures.Enqueue(SaleSendFailure.AnswerLost);
        var viewModel = await OpenTabAsync(CashTab.Closing);
        viewModel.BeginCloseDay();

        await viewModel.ConfirmCloseDayAsync();
        Assert.Equal(ClosingStep.Unconfirmed, viewModel.ClosingStep);
        viewModel.CancelClosingStep();
        Assert.Equal(ClosingStep.Unconfirmed, viewModel.ClosingStep);
        await viewModel.ShowTabAsync(CashTab.Session);
        Assert.True(viewModel.IsClosingTab);

        await viewModel.RetryCloseDayAsync();

        Assert.True(viewModel.IsReportMode);
        Assert.Equal(2, server.CloseDayRequests.Count);
        Assert.Single(server.CloseDayRequests.Select(request => request.OperationId).Distinct());
        Assert.Single(server.DailyClosures);
    }

    [Fact]
    public async Task Annuler_la_confirmation_ne_cloture_rien()
    {
        server.CashSession = null;
        var viewModel = await OpenTabAsync(CashTab.Closing);
        viewModel.BeginCloseDay();

        viewModel.CancelClosingStep();

        Assert.Equal(ClosingStep.None, viewModel.ClosingStep);
        Assert.Empty(server.CloseDayRequests);
    }

    // ---------- Clôture forcée ----------

    [Fact]
    public async Task La_cloture_forcee_n_est_proposee_qu_avec_les_deux_droits_et_si_les_operations_hors_ligne_sont_le_seul_blocage()
    {
        server.CashSession = null;
        server.OfflineOperations = 3;
        var viewModel = await OpenTabAsync(CashTab.Closing);
        Assert.False(viewModel.CanCloseDay);
        Assert.True(viewModel.CanShowForcedClose);

        Without(Permissions.UsersAdminister);
        var withoutAdministration = await OpenTabAsync(CashTab.Closing);
        Assert.False(withoutAdministration.CanShowForcedClose);
        withoutAdministration.BeginForcedClose();
        Assert.Equal(ClosingStep.None, withoutAdministration.ClosingStep);

        server.AsAdministrator();
        server.WithOpenCashSession();
        var withOpenRegister = await OpenTabAsync(CashTab.Closing);
        Assert.False(withOpenRegister.CanShowForcedClose);

        server.CashSession = null;
        server.OfflineOperations = 0;
        var withoutOffline = await OpenTabAsync(CashTab.Closing);
        Assert.False(withoutOffline.CanShowForcedClose);
        Assert.True(withoutOffline.CanCloseDay);
    }

    [Fact]
    public async Task La_cloture_forcee_exige_un_motif_avertit_et_le_rapport_porte_la_mention()
    {
        server.CashSession = null;
        server.OfflineOperations = 2;
        var viewModel = await OpenTabAsync(CashTab.Closing);

        viewModel.BeginForcedClose();
        Assert.Equal(ClosingStep.ForceReason, viewModel.ClosingStep);
        Assert.Contains("« clôture forcée avec opérations inconnues »", CashViewModel.ForcedWarningText);
        viewModel.ForceReason = "   ";
        viewModel.ValidateForceReason();
        Assert.Equal(ClosingStep.ForceReason, viewModel.ClosingStep);
        Assert.Equal(CashViewModel.ForceReasonRequiredMessage, viewModel.ClosingStepError);

        viewModel.ForceReason = "Poste de secours détruit";
        viewModel.ValidateForceReason();
        Assert.Equal(ClosingStep.Confirm, viewModel.ClosingStep);
        Assert.True(viewModel.IsForcedClosing);
        Assert.Equal("Clôture FORCÉE de la journée", viewModel.ClosingConfirmTitle);
        Assert.Empty(server.CloseDayRequests);

        await viewModel.ConfirmCloseDayAsync();

        var request = Assert.Single(server.CloseDayRequests);
        Assert.True(request.ForceWithUnknownOperations);
        Assert.Equal("Poste de secours détruit", request.ForceReason);
        Assert.True(viewModel.IsReportMode);
        Assert.True(viewModel.IsOpenedClosureForced);
        Assert.Equal("CLÔTURE FORCÉE AVEC OPÉRATIONS INCONNUES — 2 opérations inconnues", viewModel.OpenedClosureWarning);
        Assert.Equal("Motif : Poste de secours détruit", viewModel.OpenedClosureReasonText);
    }

    [Fact]
    public async Task Echap_depuis_la_confirmation_forcee_revient_au_motif()
    {
        server.CashSession = null;
        server.OfflineOperations = 1;
        var viewModel = await OpenTabAsync(CashTab.Closing);
        viewModel.BeginForcedClose();
        viewModel.ForceReason = "Panne";
        viewModel.ValidateForceReason();

        viewModel.CancelClosingStep();
        Assert.Equal(ClosingStep.ForceReason, viewModel.ClosingStep);
        viewModel.CancelClosingStep();
        Assert.Equal(ClosingStep.None, viewModel.ClosingStep);
        Assert.Empty(server.CloseDayRequests);
    }

    // ---------- Historique ----------

    [Fact]
    public async Task L_historique_liste_les_clotures_par_date_et_ouvre_un_rapport_fige()
    {
        server.AddPastClosure(new DateOnly(2026, 11, 13), 12000m);
        var forced = server.AddPastClosure(new DateOnly(2026, 11, 15), 18500m, cashDifference: -150m, forced: true);
        server.AddPastClosure(new DateOnly(2026, 11, 14), 9000m);
        var viewModel = await OpenTabAsync(CashTab.Closing);

        await viewModel.ToggleHistoryAsync();

        Assert.True(viewModel.IsHistoryMode);
        Assert.Equal(["15/11/2026", "14/11/2026", "13/11/2026"], viewModel.HistoryRows.Select(row => row.DateText));
        var first = viewModel.HistoryRows[0];
        Assert.Equal("18 500,00 DA", first.NetRevenueText);
        Assert.Equal("− 150,00 DA (manque)", first.DifferenceText);
        Assert.True(first.IsDifferenceAlert);
        Assert.Equal("FORCÉE", first.ForcedText);
        Assert.Equal(string.Empty, viewModel.HistoryRows[1].ForcedText);
        Assert.Same(first, viewModel.SelectedHistoryRow);

        viewModel.MoveHistorySelection(1);
        viewModel.MoveHistorySelection(-1);
        await viewModel.OpenSelectedClosureAsync();

        Assert.True(viewModel.IsReportMode);
        Assert.Equal(forced.Id, viewModel.OpenedClosure!.Id);
        Assert.Equal("Clôture du 15/11/2026 — Pharmaly 1 — Didouche — rapport figé", viewModel.OpenedClosureTitle);
        Assert.True(viewModel.IsOpenedClosureForced);
        Assert.Equal("Motif : Poste de secours en panne", viewModel.OpenedClosureReasonText);
        Assert.Equal("18 500,00 DA", viewModel.OpenedReport!.NetRevenueText);

        await viewModel.BackInClosingAsync();
        Assert.True(viewModel.IsHistoryMode);
        Assert.Same(viewModel.HistoryRows[0], viewModel.SelectedHistoryRow);
        await viewModel.BackInClosingAsync();
        Assert.True(viewModel.IsDayMode);
    }

    [Fact]
    public async Task Un_historique_vide_le_dit()
    {
        var viewModel = await OpenTabAsync(CashTab.Closing);

        await viewModel.ToggleHistoryAsync();

        Assert.False(viewModel.HasHistory);
        Assert.Equal("Aucune journée n'a encore été clôturée pour ce magasin.", viewModel.InfoMessage);
    }

    // ---------- Droits ----------

    [Fact]
    public async Task Sans_le_droit_de_consulter_les_rapports_l_onglet_Cloture_ne_lit_rien()
    {
        Without(Permissions.RegisterReportsView);
        var viewModel = await OpenTabAsync(CashTab.Closing);

        Assert.True(viewModel.CannotViewReports);
        Assert.False(viewModel.HasDayStatus);
        Assert.Equal(0, server.CurrentDayReads);
        Assert.False(viewModel.CanShowCloseDay);
        await viewModel.ToggleHistoryAsync();
        Assert.True(viewModel.IsDayMode);
    }

    [Fact]
    public async Task Sans_le_droit_de_cloturer_la_journee_se_consulte_mais_ne_se_cloture_pas()
    {
        server.CashSession = null;
        Without(Permissions.RegisterClose);
        var viewModel = await OpenTabAsync(CashTab.Closing);

        Assert.True(viewModel.IsDayReady);
        Assert.False(viewModel.CanShowCloseDay);
        Assert.False(viewModel.CanCloseDay);
        viewModel.BeginCloseDay();
        Assert.Equal(ClosingStep.None, viewModel.ClosingStep);
        Assert.Empty(server.CloseDayRequests);
    }

    [Fact]
    public async Task Pendant_une_cloture_on_ne_change_pas_d_onglet()
    {
        server.CashSession = null;
        var viewModel = await OpenTabAsync(CashTab.Closing);
        viewModel.BeginCloseDay();

        await viewModel.MoveTabAsync(1);

        Assert.True(viewModel.IsClosingTab);
        Assert.Equal(ClosingStep.Confirm, viewModel.ClosingStep);
    }
}
