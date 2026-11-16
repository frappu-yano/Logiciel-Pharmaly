using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Modules.Sales;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.Cash;

/// <summary>Étape de l'onglet « Session de caisse ».</summary>
public enum SessionStep
{
    None,

    /// <summary>Entrée ou sortie d'espèces : saisie du montant et du motif.</summary>
    MovementInput,

    /// <summary>Confirmation du mouvement d'espèces.</summary>
    MovementConfirm,

    /// <summary>L'envoi du mouvement n'a pas reçu de réponse : on ne sait pas s'il est enregistré.</summary>
    MovementUnconfirmed,
}

// Session de caisse du poste : état, ouverture, résumé, entrées et sorties d'espèces.
public sealed partial class CashViewModel
{
    public const string MovementUnconfirmedTitle = "Mouvement non confirmé — ne touchez pas au tiroir-caisse";
    public const string ReasonRequiredMessage = "Le motif est obligatoire.";

    private readonly OperationIdKeeper openingOperationIds = new();
    private readonly OperationIdKeeper movementOperationIds = new();
    private CashMovementRequest? unconfirmedMovement;
    private decimal openingFloat;

    /// <summary>Faux tant que l'état de la caisse n'a pas été lu.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCashOpen), nameof(IsCashClosed), nameof(CanShowOpening), nameof(CanShowMovements),
        nameof(CanShowSummary), nameof(CashStateText), nameof(CashOpenedText), nameof(CashFloatText), nameof(IsCashClosedWarning))]
    private bool isCashStateKnown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCashOpen), nameof(IsCashClosed), nameof(CanShowOpening), nameof(CanShowMovements),
        nameof(CanShowSummary), nameof(CashStateText), nameof(CashOpenedText), nameof(CashFloatText), nameof(IsCashClosedWarning))]
    private CashSessionInfo? cashSession;

    public bool IsCashOpen => IsCashStateKnown && CashSession is not null;

    public bool IsCashClosed => IsCashStateKnown && CashSession is null;

    /// <summary>Bandeau du tableau des impayés : aucun règlement n'est possible caisse fermée.</summary>
    public bool IsCashClosedWarning => IsCashClosed && Rights.CanSettle;

    public string CashStateText => !IsCashStateKnown
        ? "État de la caisse inconnu"
        : CashSession is { } session ? $"{session.RegisterName} — OUVERTE" : "Caisse FERMÉE";

    public string CashOpenedText => CashSession is { } session
        ? $"Ouverte par {session.OpenedByName} le {SaleFormat.DateTime(session.OpenedAt)}"
        : string.Empty;

    public string CashFloatText => CashSession is { } session ? Money.ToDisplay(session.OpeningFloat) : string.Empty;

    public bool CanShowOpening => IsCashClosed && Rights.CanOpenRegister;

    public bool CannotOpenRegister => !Rights.CanOpenRegister;

    public bool CanShowMovements => IsCashOpen && Rights.CanMoveCash;

    public bool CanShowSummary => IsCashOpen && Rights.CanViewReports;

    public bool CannotViewReports => !Rights.CanViewReports;

    public string CashInKeyLabel => CashShortcuts.CashInKey.ToString();

    public string CashOutKeyLabel => CashShortcuts.CashOutKey.ToString();

    // ---------- Ouverture ----------

    [ObservableProperty]
    private string openingFloatText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyStepOpen))]
    private bool isConfirmingOpening;

    [ObservableProperty]
    private string openingFloatDisplayText = string.Empty;

    // ---------- Résumé ----------

    public ObservableCollection<SummaryLine> SummaryLines { get; } = [];

    [ObservableProperty]
    private string expectedCashText = string.Empty;

    [ObservableProperty]
    private string pendingCardText = string.Empty;

    [ObservableProperty]
    private bool hasPendingCard;

    // ---------- Mouvements d'espèces ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSessionStepOpen), nameof(IsAnyStepOpen))]
    private SessionStep sessionStep;

    public bool IsSessionStepOpen => SessionStep != SessionStep.None;

    /// <summary><see cref="CashMovementKinds.In"/> ou <see cref="CashMovementKinds.Out"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MovementTitle), nameof(IsMovementOut))]
    private string movementKind = CashMovementKinds.In;

    public string MovementTitle => MovementKind == CashMovementKinds.In ? "Entrée d'espèces" : "Sortie d'espèces";

    public bool IsMovementOut => MovementKind == CashMovementKinds.Out;

    [ObservableProperty]
    private string movementAmountText = string.Empty;

    [ObservableProperty]
    private string movementReason = string.Empty;

    [ObservableProperty]
    private string movementConfirmText = string.Empty;

    [ObservableProperty]
    private string movementAmountDisplayText = string.Empty;

    [ObservableProperty]
    private string sessionStepError = string.Empty;

    private decimal movementAmount;

    // ---------- Lecture ----------

    /// <summary>Lit l'état de la caisse du poste. Renvoie faux si la lecture a échoué.</summary>
    private async Task<bool> RefreshCashSessionStateAsync()
    {
        CashSessionInfo? session = null;
        if (!await RunServerAsync(async () => session = await cash.GetCurrentSessionAsync(CancellationToken.None)))
        {
            return false;
        }

        CashSession = session;
        IsCashStateKnown = true;
        return true;
    }

    private async Task RefreshSessionTabAsync()
    {
        if (!await RefreshCashSessionStateAsync())
        {
            return;
        }

        SummaryLines.Clear();
        ExpectedCashText = string.Empty;
        HasPendingCard = false;
        if (!CanShowSummary)
        {
            return;
        }

        CashSessionSummary? summary = null;
        if (await RunServerAsync(async () => summary = await cash.GetSummaryAsync(CancellationToken.None)) && summary is not null)
        {
            ShowSummary(summary);
        }
        else if (ErrorMessage.Length > 0 && !IsUnreachableMessage())
        {
            // La caisse a pu être fermée entre les deux lectures.
            await RefreshCashSessionStateAsync();
        }
    }

    private bool IsUnreachableMessage() => ErrorMessage == ServerUnreachableMessage;

    private void ShowSummary(CashSessionSummary summary)
    {
        SummaryLines.Clear();
        SummaryLines.Add(new("Fond de caisse", Money.ToDisplay(summary.Session.OpeningFloat)));
        SummaryLines.Add(new("Ventes en espèces", Money.ToDisplay(summary.SaleCashCollected)));
        SummaryLines.Add(new("Ventes par TPE", Money.ToDisplay(summary.SaleCardCollected), Note: "hors tiroir"));
        SummaryLines.Add(new("Règlements d'impayés en espèces", Money.ToDisplay(summary.SettlementCashCollected)));
        SummaryLines.Add(new("Règlements d'impayés par TPE", Money.ToDisplay(summary.SettlementCardCollected), Note: "hors tiroir"));
        SummaryLines.Add(new("Entrées d'espèces", Money.ToDisplay(summary.CashIn)));
        SummaryLines.Add(new("Sorties d'espèces", "− " + Money.ToDisplay(summary.CashOut)));
        SummaryLines.Add(new("Remboursements en espèces", "− " + Money.ToDisplay(summary.CashRefunds)));
        SummaryLines.Add(new("Remboursements par TPE", Money.ToDisplay(summary.CardRefunds), Note: "hors tiroir"));
        ExpectedCashText = Money.ToDisplay(summary.ExpectedCash);
        HasPendingCard = summary.PendingCardCount > 0;
        PendingCardText = HasPendingCard
            ? $"{summary.PendingCardCount} paiement(s) par carte en attente de réponse : {Money.ToDisplay(summary.PendingCardAmount)}, compté(s) nulle part."
            : string.Empty;
    }

    // ---------- Ouverture de la caisse ----------

    /// <summary>Entrée sur le fond de caisse : la saisie est contrôlée, puis la confirmation est demandée.</summary>
    [RelayCommand]
    public void RequestCashOpening()
    {
        if (!CanShowOpening || IsBusy || IsSending)
        {
            return;
        }

        if (!SaleInput.TryParseAmount(OpeningFloatText, out var amount))
        {
            ErrorMessage = OpeningFloatText.Trim().Length == 0
                ? "Saisissez le fond de caisse compté (0 si la caisse est vide)."
                : "Fond de caisse invalide : un montant positif, avec au plus deux décimales.";
            return;
        }

        ErrorMessage = string.Empty;
        openingFloat = amount;
        OpeningFloatDisplayText = Money.ToDisplay(amount);
        IsConfirmingOpening = true;
    }

    [RelayCommand]
    public void CancelCashOpening()
    {
        if (!IsSending)
        {
            IsConfirmingOpening = false;
        }
    }

    [RelayCommand]
    public async Task ConfirmCashOpeningAsync()
    {
        if (!IsConfirmingOpening || IsSending)
        {
            return;
        }

        var amount = openingFloat;
        var request = new OpenCashSessionRequest(openingOperationIds.For(amount.ToString(CultureInfo.InvariantCulture)), amount);
        IsSending = true;
        ErrorMessage = string.Empty;
        try
        {
            var opened = await cash.OpenSessionAsync(request, CancellationToken.None);
            openingOperationIds.Forget();
            IsConfirmingOpening = false;
            OpeningFloatText = string.Empty;
            CashSession = opened;
            IsCashStateKnown = true;
            InfoMessage = $"Caisse ouverte avec un fond de caisse de {Money.ToDisplay(amount)}.";
        }
        catch (ServerRefusedException refused) when (!IsUnknownOutcome(refused))
        {
            openingOperationIds.Forget();
            IsConfirmingOpening = false;
            ErrorMessage = refused.Message;
            RequireIdentificationIfLost(refused);
        }
        catch (Exception exception) when (IsUnknownOutcome(exception))
        {
            // La confirmation reste affichée : Entrée retente avec le même identifiant d'opération.
            ErrorMessage = "Ouverture non confirmée : le serveur n'a pas répondu. Appuyez sur Entrée pour réessayer.";
            return;
        }
        finally
        {
            IsSending = false;
        }

        await RefreshSessionTabAsync();
    }

    // ---------- Entrées et sorties d'espèces ----------

    [RelayCommand]
    public void BeginCashIn() => BeginMovement(CashMovementKinds.In);

    [RelayCommand]
    public void BeginCashOut() => BeginMovement(CashMovementKinds.Out);

    private void BeginMovement(string kind)
    {
        if (!CanShowMovements || IsSessionStepOpen || IsConfirmingOpening || IsSending)
        {
            return;
        }

        ClearMessages();
        MovementKind = kind;
        MovementAmountText = string.Empty;
        MovementReason = string.Empty;
        SessionStepError = string.Empty;
        SessionStep = SessionStep.MovementInput;
    }

    /// <summary>Entrée sur le motif : contrôle la saisie et demande la confirmation. Rien n'est envoyé.</summary>
    [RelayCommand]
    public void ValidateMovement()
    {
        if (SessionStep != SessionStep.MovementInput)
        {
            return;
        }

        if (!SaleInput.TryParseAmount(MovementAmountText, out var amount) || amount <= 0)
        {
            SessionStepError = "Montant invalide : un montant positif, avec au plus deux décimales (virgule ou point).";
            return;
        }

        var reason = MovementReason.Trim();
        if (reason.Length == 0)
        {
            SessionStepError = ReasonRequiredMessage;
            return;
        }

        SessionStepError = string.Empty;
        movementAmount = amount;
        MovementAmountDisplayText = Money.ToDisplay(amount);
        MovementConfirmText = $"{MovementTitle} de {Money.ToDisplay(amount)} — motif : {reason}";
        SessionStep = SessionStep.MovementConfirm;
    }

    [RelayCommand]
    public Task ConfirmMovementAsync()
    {
        if (SessionStep != SessionStep.MovementConfirm || IsSending)
        {
            return Task.CompletedTask;
        }

        var reason = MovementReason.Trim();
        var signature = string.Create(CultureInfo.InvariantCulture, $"{MovementKind}|{movementAmount}|{reason}");
        return SendMovementAsync(new CashMovementRequest(movementOperationIds.For(signature), MovementKind, movementAmount, reason));
    }

    /// <summary>« Réessayer » après un mouvement non confirmé : la même demande, le même identifiant d'opération.</summary>
    [RelayCommand]
    public Task RetryMovementAsync() =>
        SessionStep == SessionStep.MovementUnconfirmed && unconfirmedMovement is { } request && !IsSending
            ? SendMovementAsync(request)
            : Task.CompletedTask;

    private async Task SendMovementAsync(CashMovementRequest request)
    {
        IsSending = true;
        SessionStepError = string.Empty;
        try
        {
            var recorded = await cash.RecordMovementAsync(request, CancellationToken.None);
            movementOperationIds.Forget();
            unconfirmedMovement = null;
            SessionStep = SessionStep.None;
            InfoMessage = recorded.Kind == CashMovementKinds.In
                ? $"Entrée d'espèces enregistrée : {Money.ToDisplay(recorded.Amount)} — {recorded.Reason}."
                : $"Sortie d'espèces enregistrée : {Money.ToDisplay(recorded.Amount)} — {recorded.Reason}.";
        }
        catch (ServerRefusedException refused) when (!IsUnknownOutcome(refused))
        {
            movementOperationIds.Forget();
            unconfirmedMovement = null;
            SessionStep = SessionStep.None;
            ErrorMessage = refused.Message;
            if (RequireIdentificationIfLost(refused))
            {
                return;
            }
        }
        catch (Exception exception) when (IsUnknownOutcome(exception))
        {
            unconfirmedMovement = request;
            SessionStep = SessionStep.MovementUnconfirmed;
            return;
        }
        finally
        {
            IsSending = false;
        }

        var message = ErrorMessage;
        await RefreshSessionTabAsync();
        if (message.Length > 0 && ErrorMessage.Length == 0)
        {
            ErrorMessage = message;
        }
    }

    /// <summary>Échap dans l'onglet Session : revient d'un cran. Un mouvement non confirmé ne s'abandonne pas.</summary>
    [RelayCommand]
    public void CancelSessionStep()
    {
        if (IsSending)
        {
            return;
        }

        SessionStepError = string.Empty;
        switch (SessionStep)
        {
            case SessionStep.MovementInput:
                SessionStep = SessionStep.None;
                break;
            case SessionStep.MovementConfirm:
                SessionStep = SessionStep.MovementInput;
                break;
        }
    }
}
