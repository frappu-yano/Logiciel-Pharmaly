using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Modules.Sales;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.Cash;

// Fermeture de la caisse du poste : comptage des espèces, confirmation, rapport de session avec l'écart.
public sealed partial class CashViewModel
{
    public const string CloseUnconfirmedTitle = "Fermeture non confirmée — ne touchez pas au tiroir-caisse";

    public const string CloseBlockedHelp =
        "Résolvez d'abord chaque paiement par carte en attente : règlement d'impayés dans l'onglet « Impayés » (Ctrl+T), "
        + "paiement d'une vente dans l'historique des ventes (module Vente). Puis recommencez la fermeture (F10).";

    private readonly OperationIdKeeper closeOperationIds = new();
    private CloseCashSessionRequest? unconfirmedClose;
    private decimal countedCash;

    /// <summary>Le bouton « Fermer la caisse » : caisse ouverte et droit « clôturer une caisse ».</summary>
    public bool CanShowCloseRegister => IsCashOpen && Rights.CanCloseRegister;

    public string CloseKeyLabel => CashShortcuts.CloseKey.ToString();

    [ObservableProperty]
    private string countedCashText = string.Empty;

    [ObservableProperty]
    private string countedCashDisplayText = string.Empty;

    [ObservableProperty]
    private string closeRegisterName = string.Empty;

    /// <summary>Refus de la fermeture, tel que le serveur l'a formulé.</summary>
    [ObservableProperty]
    private string closeBlockedMessage = string.Empty;

    /// <summary>Ce qui bloque la fermeture de cette caisse, quand on a pu le relire.</summary>
    public ObservableCollection<BlockerRowViewModel> CloseBlockers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCloseBlockers))]
    private int closeBlockerCount;

    public bool HasCloseBlockers => CloseBlockerCount > 0;

    /// <summary>Rapport de la session que l'on vient de fermer.</summary>
    [ObservableProperty]
    private SessionReportViewModel? closedSessionReport;

    /// <summary>F10 dans l'onglet Session : saisie des espèces comptées. Rien n'est envoyé.</summary>
    [RelayCommand]
    public void BeginCloseRegister()
    {
        if (!CanShowCloseRegister || IsSessionStepOpen || IsConfirmingOpening || IsSending || CashSession is not { } session)
        {
            return;
        }

        ClearMessages();
        CloseRegisterName = session.RegisterName;
        CountedCashText = string.Empty;
        SessionStepError = string.Empty;
        SessionStep = SessionStep.CloseCount;
    }

    /// <summary>Entrée sur le montant compté : contrôle la saisie et demande la confirmation.</summary>
    [RelayCommand]
    public void ValidateCountedCash()
    {
        if (SessionStep != SessionStep.CloseCount)
        {
            return;
        }

        if (!SaleInput.TryParseAmount(CountedCashText, out var amount))
        {
            SessionStepError = CountedCashText.Trim().Length == 0
                ? "Saisissez les espèces comptées dans le tiroir (0 s'il est vide)."
                : "Montant invalide : un montant positif ou nul, avec au plus deux décimales (virgule ou point).";
            return;
        }

        SessionStepError = string.Empty;
        countedCash = amount;
        CountedCashDisplayText = Money.ToDisplay(amount);
        SessionStep = SessionStep.CloseConfirm;
    }

    [RelayCommand]
    public Task ConfirmCloseRegisterAsync()
    {
        if (SessionStep != SessionStep.CloseConfirm || IsSending)
        {
            return Task.CompletedTask;
        }

        var signature = countedCash.ToString(CultureInfo.InvariantCulture);
        return SendCloseAsync(new CloseCashSessionRequest(closeOperationIds.For(signature), countedCash));
    }

    /// <summary>« Réessayer » après une fermeture non confirmée : la même demande, le même identifiant d'opération.</summary>
    [RelayCommand]
    public Task RetryCloseRegisterAsync() =>
        SessionStep == SessionStep.CloseUnconfirmed && unconfirmedClose is { } request && !IsSending
            ? SendCloseAsync(request)
            : Task.CompletedTask;

    private async Task SendCloseAsync(CloseCashSessionRequest request)
    {
        IsSending = true;
        SessionStepError = string.Empty;
        try
        {
            var report = await cash.CloseSessionAsync(request, CancellationToken.None);
            closeOperationIds.Forget();
            unconfirmedClose = null;
            CountedCashText = string.Empty;
            ClosedSessionReport = new SessionReportViewModel(report);
            CashSession = null;
            IsCashStateKnown = true;
            SummaryLines.Clear();
            ExpectedCashText = string.Empty;
            HasPendingCard = false;
            SessionStep = SessionStep.CloseDone;
        }
        catch (ServerRefusedException refused) when (!IsUnknownOutcome(refused))
        {
            closeOperationIds.Forget();
            unconfirmedClose = null;
            if (RequireIdentificationIfLost(refused))
            {
                SessionStep = SessionStep.None;
                ErrorMessage = refused.Message;
                return;
            }

            if (refused.Code == ClosureErrorCodes.CardPending)
            {
                CloseBlockedMessage = refused.Message;
                SetCloseBlockers([]);
                SessionStep = SessionStep.CloseBlocked;
            }
            else
            {
                SessionStep = SessionStep.None;
                ErrorMessage = refused.Message;
            }
        }
        catch (Exception exception) when (IsUnknownOutcome(exception))
        {
            unconfirmedClose = request;
            SessionStep = SessionStep.CloseUnconfirmed;
            return;
        }
        finally
        {
            IsSending = false;
        }

        if (SessionStep == SessionStep.CloseBlocked)
        {
            await LoadCloseBlockersAsync();
        }
        else if (SessionStep == SessionStep.None)
        {
            var message = ErrorMessage;
            await RefreshSessionTabAsync();
            if (message.Length > 0 && ErrorMessage.Length == 0)
            {
                ErrorMessage = message;
            }
        }
    }

    /// <summary>
    /// Le refus nomme déjà chaque paiement en attente ; quand l'employé peut consulter la journée,
    /// la liste détaillée de cette caisse est relue pour l'afficher ligne par ligne.
    /// </summary>
    private async Task LoadCloseBlockersAsync()
    {
        if (!Rights.CanViewReports)
        {
            return;
        }

        try
        {
            var day = await closures.GetCurrentDayAsync(CancellationToken.None);
            SetCloseBlockers(day.Blockers.Where(blocker =>
                blocker.Kind is ClosureBlockerKinds.SaleCardPending or ClosureBlockerKinds.SettlementCardPending
                && (blocker.RegisterName is null || blocker.RegisterName == CloseRegisterName)));
        }
        catch (Exception exception) when (exception is ServerRefusedException or HttpRequestException or TaskCanceledException)
        {
            // Le message du refus suffit : la liste détaillée n'est qu'un confort.
        }
    }

    private void SetCloseBlockers(IEnumerable<ClosureBlocker> blockers)
    {
        CloseBlockers.Clear();
        foreach (var blocker in blockers)
        {
            CloseBlockers.Add(new BlockerRowViewModel(blocker));
        }

        CloseBlockerCount = CloseBlockers.Count;
    }

    /// <summary>Entrée ou Échap sur le rapport de fermeture ou sur le refus : retour à l'onglet, relu.</summary>
    [RelayCommand]
    public async Task FinishCloseRegisterAsync()
    {
        if (SessionStep is not (SessionStep.CloseDone or SessionStep.CloseBlocked) || IsSending)
        {
            return;
        }

        var done = SessionStep == SessionStep.CloseDone ? ClosedSessionReport : null;
        SessionStep = SessionStep.None;
        CloseBlockers.Clear();
        CloseBlockerCount = 0;
        await RefreshSessionTabAsync();
        if (done is not null && ErrorMessage.Length == 0)
        {
            InfoMessage = $"{done.Report.RegisterName} fermée. Écart : {done.DifferenceText}.";
        }
    }
}
