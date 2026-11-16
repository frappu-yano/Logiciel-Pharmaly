using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Pharmaly.Contracts;
using Pharmaly.Poste;
using Pharmaly.Poste.Modules.Cash;
using Pharmaly.Poste.ViewModels;
using Pharmaly.Poste.Views;

namespace Pharmaly.Tests.Poste;

/// <summary>Fermeture de la caisse et clôture de la journée au clavier, dans la vraie fenêtre, sans écran.</summary>
public sealed class ClosureWindowTests
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

    private static async Task KeyAsync(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Press(window, key, modifiers);
        await SettleAsync();
    }

    [AvaloniaFact]
    public async Task Identification_F3_session_fermeture_avec_comptage_puis_cloture_de_la_journee_et_rapport_fige()
    {
        server.AddDaySale(SalePaymentModes.Cash, 1500m);
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        Assert.Equal(SessionStage.Work, shell.Session.Stage);

        // F3 : Caisse ; Ctrl+Tab : session de caisse, ouverte.
        await KeyAsync(window, PhysicalKey.F3);
        var view = CashViewOf(window);
        var viewModel = Assert.IsType<CashViewModel>(shell.CurrentModule);
        await KeyAsync(window, PhysicalKey.Tab, RawInputModifiers.Control);
        Assert.True(Shown(view, "SessionPanel"));
        Assert.True(Shown(view, "CloseRegisterButton"));
        Assert.Equal("6 500,00 DA", Text(view, "ExpectedCashLabel"));

        // F10 : comptage ; le curseur attend le montant.
        await KeyAsync(window, PhysicalKey.F10);
        Assert.True(Shown(view, "CloseCountPanel"));
        Assert.True(view.FindControl<TextBox>("CountedCashBox")!.IsFocused);
        window.KeyTextInput("6450");
        await KeyAsync(window, PhysicalKey.Enter);
        Assert.True(Shown(view, "CloseConfirmPanel"));
        Assert.Equal("6 450,00 DA", Text(view, "CountedCashConfirmLabel"));
        Assert.Empty(server.CloseSessionRequests);

        // Entrée : la caisse est fermée ; le rapport montre l'écart, mis en évidence.
        await KeyAsync(window, PhysicalKey.Enter);
        Assert.True(Shown(view, "CloseDonePanel"));
        Assert.Equal(6450m, Assert.Single(server.CloseSessionRequests).CountedCash);
        Assert.Equal("6 500,00 DA", Text(view, "ClosedExpectedLabel"));
        Assert.Equal("6 450,00 DA", Text(view, "ClosedCountedLabel"));
        Assert.Equal("− 50,00 DA (manque)", Text(view, "ClosedDifferenceLabel"));
        Assert.Contains("warning", view.FindControl<Border>("ClosedDifferencePanel")!.Classes);

        await KeyAsync(window, PhysicalKey.Enter);
        Assert.False(Shown(view, "SessionStepOverlay"));
        Assert.Equal("Caisse FERMÉE", Text(view, "CashStateLabel"));

        // Ctrl+Tab : clôture ; rien ne bloque.
        await KeyAsync(window, PhysicalKey.Tab, RawInputModifiers.Control);
        Assert.True(Shown(view, "ClosingPanel"));
        Assert.True(Shown(view, "DayScroll"));
        Assert.False(Shown(view, "DayBlockersPanel"));
        Assert.True(view.FindControl<Button>("CloseDayButton")!.IsEnabled);
        Assert.False(Shown(view, "ForcedCloseButton"));
        Assert.Equal("Rien ne bloque : la journée peut être clôturée.", Text(view, "DayStateLabel"));

        // F10 : confirmation forte. Entrée ne clôture pas ; F10 clôture.
        await KeyAsync(window, PhysicalKey.F10);
        Assert.True(Shown(view, "ClosingConfirmPanel"));
        await KeyAsync(window, PhysicalKey.Enter);
        Assert.True(Shown(view, "ClosingConfirmPanel"));
        Assert.Empty(server.CloseDayRequests);
        await KeyAsync(window, PhysicalKey.F10);

        Assert.Single(server.CloseDayRequests);
        Assert.False(Shown(view, "ClosingStepOverlay"));
        Assert.True(Shown(view, "ReportScroll"));
        Assert.False(Shown(view, "DayScroll"));
        Assert.StartsWith("Clôture du ", Text(view, "OpenedClosureTitleLabel"));
        Assert.EndsWith("rapport figé", Text(view, "OpenedClosureTitleLabel"));
        Assert.True(Shown(view, "ClosingInfoLabel"));
        Assert.False(Shown(view, "ForcedReportPanel"));
        Assert.Equal("1 500,00 DA", viewModel.OpenedReport!.NetRevenueText);

        // Échap : la journée, désormais clôturée ; F7 : l'historique ; Entrée : le rapport figé.
        await KeyAsync(window, PhysicalKey.Escape);
        Assert.True(Shown(view, "DayScroll"));
        Assert.False(view.FindControl<Button>("CloseDayButton")!.IsEnabled);
        await KeyAsync(window, PhysicalKey.F7);
        Assert.True(Shown(view, "HistoryList"));
        Assert.Single(viewModel.HistoryRows);
        await KeyAsync(window, PhysicalKey.Enter);
        Assert.True(Shown(view, "ReportScroll"));
        await KeyAsync(window, PhysicalKey.Escape);
        Assert.True(Shown(view, "HistoryList"));
        await KeyAsync(window, PhysicalKey.Escape);
        Assert.True(Shown(view, "DayScroll"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Un_TPE_en_attente_bloque_la_fermeture_et_la_liste_est_affichee()
    {
        server.PendingSaleCards.Add(("D1-000042", 1200m));
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        await KeyAsync(window, PhysicalKey.F3);
        var view = CashViewOf(window);
        await KeyAsync(window, PhysicalKey.Tab, RawInputModifiers.Control);

        await KeyAsync(window, PhysicalKey.F10);
        window.KeyTextInput("5000");
        await KeyAsync(window, PhysicalKey.Enter);
        await KeyAsync(window, PhysicalKey.Enter);

        Assert.True(Shown(view, "CloseBlockedPanel"));
        Assert.Contains("Ticket D1-000042", Text(view, "CloseBlockedLabel"));
        Assert.True(Shown(view, "CloseBlockersList"));
        Assert.Equal(CashViewModel.CloseBlockedHelp, Text(view, "CloseBlockedHelpLabel"));

        await KeyAsync(window, PhysicalKey.Escape);
        Assert.False(Shown(view, "SessionStepOverlay"));
        Assert.Equal("Caisse 1 — OUVERTE", Text(view, "CashStateLabel"));
        Assert.NotNull(shell.CurrentModule);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Apres_un_verrouillage_pendant_une_cloture_rien_ne_reste_affiche()
    {
        server.CashSession = null;
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        await KeyAsync(window, PhysicalKey.F3);
        await KeyAsync(window, PhysicalKey.Tab, RawInputModifiers.Control);
        await KeyAsync(window, PhysicalKey.Tab, RawInputModifiers.Control);
        var firstModel = Assert.IsType<CashViewModel>(shell.CurrentModule);
        await KeyAsync(window, PhysicalKey.F10);
        Assert.Equal(ClosingStep.Confirm, firstModel.ClosingStep);

        await KeyAsync(window, PhysicalKey.F12);
        Assert.Null(shell.CurrentModule);
        await IdentifyEmployeeAsync(window);
        await KeyAsync(window, PhysicalKey.F3);

        var nextView = CashViewOf(window);
        var nextModel = Assert.IsType<CashViewModel>(shell.CurrentModule);
        Assert.NotSame(firstModel, nextModel);
        Assert.Equal(ClosingStep.None, nextModel.ClosingStep);
        Assert.False(nextModel.HasDayStatus);
        Assert.False(Shown(nextView, "ClosingStepOverlay"));
        Assert.Empty(server.CloseDayRequests);
        window.Close();
    }
}
