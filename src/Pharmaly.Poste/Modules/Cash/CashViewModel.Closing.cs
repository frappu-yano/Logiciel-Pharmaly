using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pharmaly.Contracts;
using Pharmaly.Poste.Modules.Sales;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.Cash;

/// <summary>Ce que montre l'onglet « Clôture ».</summary>
public enum ClosingMode
{
    /// <summary>Journée en cours : caisses, blocages, aperçu non figé des totaux.</summary>
    Day,

    /// <summary>Historique des clôtures.</summary>
    History,

    /// <summary>Rapport figé d'une clôture.</summary>
    Report,
}

/// <summary>Étape de la clôture de la journée.</summary>
public enum ClosingStep
{
    None,

    /// <summary>Clôture forcée : saisie du motif, avertissement explicite.</summary>
    ForceReason,

    /// <summary>Confirmation forte : la journée va être figée.</summary>
    Confirm,

    /// <summary>La clôture n'a pas reçu de réponse : on ne sait pas si la journée est clôturée.</summary>
    Unconfirmed,
}

// Clôture quotidienne du magasin : état de la journée, clôture (éventuellement forcée), historique, rapports figés.
public sealed partial class CashViewModel
{
    public const string NoReportsViewRight =
        "Vos droits ne permettent pas de consulter l'état de la journée ni les rapports de caisse : la clôture se fait avec un compte "
        + "qui peut aussi les consulter.";

    public const string ForceReasonRequiredMessage = "Le motif de la clôture forcée est obligatoire.";

    public const string ForcedWarningText =
        "Clôture forcée : des opérations de secours ne sont pas synchronisées et resteront inconnues. Le rapport portera la mention "
        + "« clôture forcée avec opérations inconnues », avec votre nom et votre motif.";

    public const string ClosingUnconfirmedTitle = "Clôture non confirmée — la journée est peut-être déjà figée";

    /// <summary>Clôtures lues dans l'historique.</summary>
    public const int HistoryLimit = 60;

    private const int MaxForceReasonLength = 500;

    private readonly OperationIdKeeper closingOperationIds = new();
    private CloseDayRequest? unconfirmedClosing;

    /// <summary>Mode d'où l'on vient quand un rapport figé est affiché : Échap y revient.</summary>
    private ClosingMode reportReturnMode = ClosingMode.Day;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDayMode), nameof(IsHistoryMode), nameof(IsReportMode), nameof(HistoryKeyText))]
    private ClosingMode closingMode;

    public bool IsDayMode => ClosingMode == ClosingMode.Day;

    public bool IsHistoryMode => ClosingMode == ClosingMode.History;

    public bool IsReportMode => ClosingMode == ClosingMode.Report;

    public string HistoryKeyText => IsDayMode ? "Historique des clôtures" : "Journée en cours";

    public string HistoryKeyLabel => CashShortcuts.HistoryKey.ToString();

    public string ForcedCloseKeyLabel => CashShortcuts.ForcedCloseKey.ToString();

    // ---------- Journée en cours ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDayStatus), nameof(DayTitle), nameof(LastClosedText), nameof(DayStateText),
        nameof(IsDayReady), nameof(IsDayBlocked), nameof(CanCloseDay), nameof(CanShowForcedClose), nameof(PendingOfflineText),
        nameof(HasPendingOffline))]
    private CurrentDayStatus? dayStatus;

    public bool HasDayStatus => DayStatus is not null;

    public string DayTitle => DayStatus is { } day ? $"Journée du {ClosureFormat.Date(day.Today)} — {day.StoreName}" : string.Empty;

    public string LastClosedText => DayStatus?.LastClosedDate is { } last
        ? $"Dernière journée clôturée : {ClosureFormat.Date(last)}"
        : "Aucune journée clôturée pour ce magasin.";

    /// <summary>Rien ne bloque et la journée n'est pas déjà clôturée (selon le serveur).</summary>
    public bool IsDayReady => DayStatus is { CanClose: true };

    public bool IsDayBlocked => DayStatus is { Blockers.Count: > 0 };

    public string DayStateText => DayStatus switch
    {
        null => string.Empty,
        { CanClose: true } => "Rien ne bloque : la journée peut être clôturée.",
        { Blockers.Count: > 0 } day => $"Clôture impossible : {ClosureFormat.Count(day.Blockers.Count, "point bloque", "points bloquent")}.",
        { LastClosedDate: { } last } day when last >= day.Today =>
            $"La journée du {ClosureFormat.Date(day.Today)} est déjà clôturée. Les opérations saisies depuis appartiennent à la journée suivante.",
        _ => "La journée ne peut pas être clôturée pour le moment.",
    };

    public bool HasPendingOffline => DayStatus is { PendingOfflineOperations: > 0 };

    public string PendingOfflineText => DayStatus is { PendingOfflineOperations: > 0 and var count }
        ? $"{ClosureFormat.Count(count, "opération hors ligne n'est pas synchronisée", "opérations hors ligne ne sont pas synchronisées")} : "
          + "la journée ne peut pas être présentée comme consolidée."
        : string.Empty;

    /// <summary>Le bouton « Clôturer la journée » est visible avec le droit ; actif seulement si rien ne bloque.</summary>
    public bool CanShowCloseDay => Rights is { CanCloseRegister: true, CanViewReports: true };

    public bool CanCloseDay => CanShowCloseDay && IsDayReady;

    /// <summary>
    /// Clôture forcée (D37) : les deux droits, des opérations de secours inconnues, et rien d'autre
    /// ne bloque (caisses fermées, aucun TPE en attente).
    /// </summary>
    public bool CanShowForcedClose =>
        CanShowCloseDay && Rights.CanForceClose
        && DayStatus is { PendingOfflineOperations: > 0 } day
        && day.Blockers.All(blocker => blocker.Kind == ClosureBlockerKinds.OfflineOperations)
        && (day.LastClosedDate is not { } last || last < day.Today);

    public ObservableCollection<BlockerRowViewModel> DayBlockers { get; } = [];

    /// <summary>Aperçu non figé des totaux de la journée.</summary>
    [ObservableProperty]
    private ClosureReportViewModel? dayPreview;

    // ---------- Clôture ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClosingStepOpen), nameof(IsAnyStepOpen))]
    private ClosingStep closingStep;

    public bool IsClosingStepOpen => ClosingStep != ClosingStep.None;

    /// <summary>Vrai quand la clôture en cours de confirmation est une clôture forcée.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClosingConfirmTitle))]
    private bool isForcedClosing;

    [ObservableProperty]
    private string forceReason = string.Empty;

    [ObservableProperty]
    private string closingStepError = string.Empty;

    /// <summary>Journée que la confirmation va figer.</summary>
    [ObservableProperty]
    private string closingDateText = string.Empty;

    public string ClosingConfirmTitle => IsForcedClosing ? "Clôture FORCÉE de la journée" : "Clôturer la journée";

    // ---------- Historique et rapport figé ----------

    public ObservableCollection<ClosureHistoryRowViewModel> HistoryRows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHistory))]
    private int historyCount;

    public bool HasHistory => HistoryCount > 0;

    [ObservableProperty]
    private ClosureHistoryRowViewModel? selectedHistoryRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenedClosure), nameof(OpenedClosureTitle), nameof(OpenedClosureByText), nameof(IsOpenedClosureForced),
        nameof(OpenedClosureWarning), nameof(OpenedClosureReasonText))]
    private DailyClosureDetail? openedClosure;

    [ObservableProperty]
    private ClosureReportViewModel? openedReport;

    public bool HasOpenedClosure => OpenedClosure is not null;

    public string OpenedClosureTitle => OpenedClosure is { } closure
        ? $"Clôture du {ClosureFormat.Date(closure.BusinessDate)} — {closure.StoreName} — rapport figé"
        : string.Empty;

    public string OpenedClosureByText => OpenedClosure is { } closure
        ? $"Clôturée par {closure.ClosedByName} le {SaleFormat.DateTime(closure.ClosedAt)}"
        : string.Empty;

    public bool IsOpenedClosureForced => OpenedClosure is { Forced: true } || OpenedClosure?.Report.ForcedWithUnknownOperations == true;

    public string OpenedClosureWarning => OpenedClosure is { } closure && IsOpenedClosureForced
        ? (closure.Report.Warning ?? "Clôture forcée avec opérations inconnues").ToUpperInvariant()
          + $" — {ClosureFormat.Count(closure.Report.UnknownOfflineOperations, "opération inconnue", "opérations inconnues")}"
        : string.Empty;

    public string OpenedClosureReasonText => OpenedClosure?.ForceReason is { Length: > 0 } reason ? $"Motif : {reason}" : string.Empty;

    // ---------- Lecture ----------

    private async Task LoadClosingTabAsync()
    {
        switch (ClosingMode)
        {
            case ClosingMode.History:
                await LoadHistoryAsync();
                break;
            case ClosingMode.Report:
                break;
            default:
                await LoadDayStatusAsync();
                break;
        }
    }

    private async Task LoadDayStatusAsync()
    {
        if (!Rights.CanViewReports)
        {
            return;
        }

        CurrentDayStatus? status = null;
        if (await RunServerAsync(async () => status = await closures.GetCurrentDayAsync(CancellationToken.None)) && status is not null)
        {
            ShowDayStatus(status);
        }
    }

    private void ShowDayStatus(CurrentDayStatus status)
    {
        DayStatus = status;
        DayBlockers.Clear();
        foreach (var blocker in status.Blockers)
        {
            DayBlockers.Add(new BlockerRowViewModel(blocker));
        }

        DayPreview = new ClosureReportViewModel(status.Preview);
        OnPropertyChanged(nameof(CanShowForcedClose));
    }

    // ---------- Clôture de la journée ----------

    /// <summary>F10 dans l'onglet Clôture : demande la confirmation forte. Rien n'est envoyé.</summary>
    [RelayCommand]
    public void BeginCloseDay()
    {
        if (!CanCloseDay || !IsDayMode || IsClosingStepOpen || IsSending || IsBusy || DayStatus is not { } day)
        {
            return;
        }

        ClearMessages();
        IsForcedClosing = false;
        ForceReason = string.Empty;
        ClosingStepError = string.Empty;
        ClosingDateText = ClosureFormat.Date(day.Today);
        ClosingStep = ClosingStep.Confirm;
    }

    /// <summary>F9 dans l'onglet Clôture : clôture forcée, motif d'abord. Rien n'est envoyé.</summary>
    [RelayCommand]
    public void BeginForcedClose()
    {
        if (!CanShowForcedClose || !IsDayMode || IsClosingStepOpen || IsSending || IsBusy || DayStatus is not { } day)
        {
            return;
        }

        ClearMessages();
        IsForcedClosing = true;
        ForceReason = string.Empty;
        ClosingStepError = string.Empty;
        ClosingDateText = ClosureFormat.Date(day.Today);
        ClosingStep = ClosingStep.ForceReason;
    }

    /// <summary>Entrée sur le motif : il est obligatoire ; la confirmation forte vient ensuite.</summary>
    [RelayCommand]
    public void ValidateForceReason()
    {
        if (ClosingStep != ClosingStep.ForceReason)
        {
            return;
        }

        var reason = ForceReason.Trim();
        if (reason.Length == 0)
        {
            ClosingStepError = ForceReasonRequiredMessage;
            return;
        }

        if (reason.Length > MaxForceReasonLength)
        {
            ClosingStepError = $"Le motif fait au plus {MaxForceReasonLength} caractères.";
            return;
        }

        ClosingStepError = string.Empty;
        ClosingStep = ClosingStep.Confirm;
    }

    /// <summary>Confirmation forte (F10, jamais Entrée) : la journée est figée, c'est irréversible.</summary>
    [RelayCommand]
    public Task ConfirmCloseDayAsync()
    {
        if (ClosingStep != ClosingStep.Confirm || IsSending || DayStatus is not { } day)
        {
            return Task.CompletedTask;
        }

        var reason = IsForcedClosing ? ForceReason.Trim() : null;
        var signature = string.Create(
            CultureInfo.InvariantCulture, $"{day.StoreId}|{day.Today:yyyy-MM-dd}|{IsForcedClosing}|{reason}");
        return SendClosingAsync(new CloseDayRequest(
            closingOperationIds.For(signature), day.Today, IsForcedClosing, reason));
    }

    /// <summary>« Réessayer » après une clôture non confirmée : la même demande, le même identifiant d'opération.</summary>
    [RelayCommand]
    public Task RetryCloseDayAsync() =>
        ClosingStep == ClosingStep.Unconfirmed && unconfirmedClosing is { } request && !IsSending
            ? SendClosingAsync(request)
            : Task.CompletedTask;

    private async Task SendClosingAsync(CloseDayRequest request)
    {
        IsSending = true;
        ClosingStepError = string.Empty;
        string refusal;
        try
        {
            var closure = await closures.CloseDayAsync(request, CancellationToken.None);
            closingOperationIds.Forget();
            unconfirmedClosing = null;
            ClosingStep = ClosingStep.None;
            ShowClosure(closure, ClosingMode.Day);
            InfoMessage = $"Journée du {ClosureFormat.Date(closure.BusinessDate)} clôturée : le rapport ci-dessous est figé.";
            printer.Print(closure);
            return;
        }
        catch (ServerRefusedException refused) when (!IsUnknownOutcome(refused))
        {
            closingOperationIds.Forget();
            unconfirmedClosing = null;
            ClosingStep = ClosingStep.None;
            if (RequireIdentificationIfLost(refused))
            {
                ErrorMessage = refused.Message;
                return;
            }

            refusal = refused.Message;
        }
        catch (Exception exception) when (IsUnknownOutcome(exception))
        {
            unconfirmedClosing = request;
            ClosingStep = ClosingStep.Unconfirmed;
            return;
        }
        finally
        {
            IsSending = false;
        }

        // Refus (déjà clôturée, blocage apparu entre-temps…) : affiché tel quel, la journée relue.
        await LoadDayStatusAsync();
        ErrorMessage = refusal;
    }

    /// <summary>Échap pendant la clôture : revient d'un cran. Une clôture non confirmée ne s'abandonne pas.</summary>
    [RelayCommand]
    public void CancelClosingStep()
    {
        if (IsSending)
        {
            return;
        }

        ClosingStepError = string.Empty;
        switch (ClosingStep)
        {
            case ClosingStep.ForceReason:
                ClosingStep = ClosingStep.None;
                break;
            case ClosingStep.Confirm:
                ClosingStep = IsForcedClosing ? ClosingStep.ForceReason : ClosingStep.None;
                break;
        }
    }

    // ---------- Historique ----------

    /// <summary>F7 : historique des clôtures ; depuis l'historique ou un rapport, retour à la journée en cours.</summary>
    [RelayCommand]
    public Task ToggleHistoryAsync()
    {
        if (!Rights.CanViewReports || IsClosingStepOpen || IsSending)
        {
            return Task.CompletedTask;
        }

        ClearMessages();
        ClosingMode = IsDayMode ? ClosingMode.History : ClosingMode.Day;
        return LoadClosingTabAsync();
    }

    private async Task LoadHistoryAsync()
    {
        IReadOnlyList<DailyClosureSummary> closures = [];
        if (!await RunServerAsync(async () => closures = await this.closures.GetClosuresAsync(null, null, HistoryLimit, CancellationToken.None)))
        {
            return;
        }

        var selectedId = SelectedHistoryRow?.Id;
        HistoryRows.Clear();
        foreach (var closure in closures)
        {
            HistoryRows.Add(new ClosureHistoryRowViewModel(closure));
        }

        HistoryCount = HistoryRows.Count;
        SelectedHistoryRow = HistoryRows.FirstOrDefault(row => row.Id == selectedId) ?? HistoryRows.FirstOrDefault();
        if (HistoryRows.Count == 0)
        {
            InfoMessage = "Aucune journée n'a encore été clôturée pour ce magasin.";
        }
    }

    /// <summary>↑ / ↓ dans l'historique.</summary>
    public void MoveHistorySelection(int delta)
    {
        if (HistoryRows.Count == 0)
        {
            return;
        }

        var index = SelectedHistoryRow is { } row ? HistoryRows.IndexOf(row) : -1;
        SelectedHistoryRow = HistoryRows[Math.Clamp(index + delta, 0, HistoryRows.Count - 1)];
    }

    /// <summary>Entrée dans l'historique : ouvre le rapport figé de la clôture choisie.</summary>
    [RelayCommand]
    public async Task OpenSelectedClosureAsync()
    {
        if (!IsHistoryMode || SelectedHistoryRow is not { } row || IsSending)
        {
            return;
        }

        DailyClosureDetail? closure = null;
        if (!await RunServerAsync(async () => closure = await closures.GetClosureAsync(row.Id, CancellationToken.None)))
        {
            return;
        }

        if (closure is null)
        {
            ErrorMessage = "Clôture introuvable.";
            return;
        }

        ClearMessages();
        ShowClosure(closure, ClosingMode.History);
    }

    private void ShowClosure(DailyClosureDetail closure, ClosingMode returnMode)
    {
        reportReturnMode = returnMode;
        OpenedReport = new ClosureReportViewModel(closure.Report);
        OpenedClosure = closure;
        ClosingMode = ClosingMode.Report;
    }

    /// <summary>Échap dans l'onglet Clôture : du rapport figé on revient d'où l'on vient, de l'historique à la journée.</summary>
    [RelayCommand]
    public Task BackInClosingAsync()
    {
        if (IsClosingStepOpen || IsSending || IsDayMode)
        {
            return Task.CompletedTask;
        }

        ClearMessages();
        ClosingMode = IsReportMode ? reportReturnMode : ClosingMode.Day;
        if (!IsReportMode)
        {
            OpenedClosure = null;
            OpenedReport = null;
        }

        return LoadClosingTabAsync();
    }
}
