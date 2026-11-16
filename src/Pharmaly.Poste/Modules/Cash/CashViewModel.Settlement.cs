using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Modules.Sales;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.Cash;

/// <summary>Étape affichée par-dessus le tableau des impayés. Tant qu'une étape est ouverte, la sélection est figée.</summary>
public enum SettlementStep
{
    None,

    /// <summary>Plusieurs clients correspondent à la recherche : choisir le bon.</summary>
    ChooseCustomer,

    /// <summary>Espèces : saisie du montant reçu (vide : montant exact).</summary>
    CashAmount,

    /// <summary>Espèces : monnaie à rendre affichée, confirmation attendue.</summary>
    CashConfirm,

    /// <summary>TPE : confirmation avant d'enregistrer le règlement « en attente ».</summary>
    CardConfirm,

    /// <summary>TPE : le règlement attend la réponse du terminal, saisie par le caissier.</summary>
    CardPending,

    /// <summary>L'envoi n'a pas reçu de réponse : on ne sait pas si le règlement est enregistré.</summary>
    Unconfirmed,

    /// <summary>Règlement confirmé : récapitulatif.</summary>
    Done,
}

// Règlement : espèces ou TPE, toujours exactement la somme due, jamais sans confirmation.
public sealed partial class CashViewModel
{
    public const string UnconfirmedTitle = "Règlement non confirmé — ne pas rendre la monnaie";
    public const string CashClosedMessage =
        "La caisse de ce poste n'est pas ouverte : aucun règlement n'est possible. Ouvrez-la dans l'onglet « Session de caisse ».";

    private readonly OperationIdKeeper settleOperationIds = new();
    private readonly OperationIdKeeper cardOperationIds = new();

    /// <summary>Demande envoyée sans réponse : « Réessayer » la renvoie telle quelle, avec le même identifiant d'opération.</summary>
    private SettleReceivablesRequest? unconfirmedSettlement;

    /// <summary>Moyen en cours de confirmation (<see cref="PaymentMethods"/>).</summary>
    private string? checkoutMethod;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStepOpen), nameof(IsAnyStepOpen))]
    private SettlementStep step;

    public bool IsStepOpen => Step != SettlementStep.None;

    /// <summary>Refus ou saisie invalide de l'étape ouverte, affiché dans son panneau.</summary>
    [ObservableProperty]
    private string stepError = string.Empty;

    /// <summary>Tickets, client et total figés à l'ouverture de l'étape : c'est exactement ce qui sera envoyé.</summary>
    private IReadOnlyList<ReceivableLine> checkoutLines = [];

    [ObservableProperty]
    private string checkoutCustomerText = string.Empty;

    [ObservableProperty]
    private string checkoutTicketsText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckoutTotalText), nameof(ChangePreviewText))]
    private decimal checkoutTotal;

    public string CheckoutTotalText => Money.ToDisplay(CheckoutTotal);

    // ---------- Espèces ----------

    /// <summary>Montant reçu, tel que saisi. Vide : le client donne le montant exact.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangePreviewText))]
    private string receivedText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AmountReceivedText))]
    private decimal amountReceived;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangeDueText))]
    private decimal changeDue;

    public string AmountReceivedText => Money.ToDisplay(AmountReceived);

    public string ChangeDueText => Money.ToDisplay(ChangeDue);

    /// <summary>Monnaie à rendre pendant la frappe ; « — » tant que la saisie ne suffit pas.</summary>
    public string ChangePreviewText
    {
        get
        {
            if (ReceivedText.Trim().Length == 0)
            {
                return Money.ToDisplay(0);
            }

            return SaleInput.TryParseAmount(ReceivedText, out var received) && received >= CheckoutTotal
                ? Money.ToDisplay(received - CheckoutTotal)
                : "—";
        }
    }

    // ---------- TPE ----------

    /// <summary>Règlement par carte en attente de la réponse du terminal.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingSettlementText))]
    private SettlementResult? pendingSettlement;

    public string PendingSettlementText => PendingSettlement is { } settlement
        ? $"{settlement.CustomerName} — {Money.ToDisplay(settlement.Amount)}"
        : string.Empty;

    public string CardAcceptedKeyLabel => SalesShortcuts.CardAcceptedLabel;

    public string CardRefusedKeyLabel => SalesShortcuts.CardRefusedLabel;

    // ---------- Choix du client ----------

    public ObservableCollection<CustomerSummary> CustomerResults { get; } = [];

    public bool HasCustomerResults => CustomerResults.Count > 0;

    [ObservableProperty]
    private CustomerSummary? selectedCustomerResult;

    // ---------- Récapitulatif ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DoneTitle), nameof(DoneTicketsText), nameof(DoneAmountText), nameof(DoneReceivedText),
        nameof(DoneChangeText), nameof(IsDoneCash), nameof(IsDoneCard))]
    private SettlementResult? completedSettlement;

    public string DoneTitle => CompletedSettlement is { } settlement
        ? $"{settlement.CustomerName} — {ReceivableFormat.Tickets(settlement.Receivables.Count)} soldé{(settlement.Receivables.Count > 1 ? "s" : string.Empty)}"
        : string.Empty;

    public string DoneTicketsText => CompletedSettlement is { } settlement
        ? string.Join(", ", settlement.Receivables.Select(line => $"{line.TicketNumber} ({Money.ToDisplay(line.Amount)})"))
        : string.Empty;

    public string DoneAmountText => Money.ToDisplay(CompletedSettlement?.Amount ?? 0m);

    public string DoneReceivedText => $"Reçu {Money.ToDisplay(CompletedSettlement?.AmountReceived ?? 0m)}";

    public string DoneChangeText => Money.ToDisplay(CompletedSettlement?.ChangeGiven ?? 0m);

    public bool IsDoneCash => CompletedSettlement?.Method == PaymentMethods.Cash;

    public bool IsDoneCard => CompletedSettlement?.Method == PaymentMethods.Card;

    // ---------- Choix du moyen : rien n'est envoyé ----------

    private bool EnterCheckout()
    {
        if (!CanSettle || IsStepOpen || IsSending || IsBusy || !IsReceivablesTab)
        {
            return false;
        }

        ClearMessages();
        if (selection.Count == 0)
        {
            ErrorMessage = "Choisissez d'abord le ou les tickets à régler (Espace sur la ligne, ou scan du ticket).";
            return false;
        }

        if (IsCashClosed)
        {
            ErrorMessage = CashClosedMessage;
            return false;
        }

        checkoutLines = selection.ToList();
        CheckoutCustomerText = checkoutLines[0].CustomerName;
        CheckoutTicketsText = string.Join(
            Environment.NewLine,
            checkoutLines.Select(line => $"{line.TicketNumber} — {SaleFormat.DateTime(line.CreatedAt)} — {Money.ToDisplay(line.Amount)}"));
        CheckoutTotal = checkoutLines.Sum(line => line.Amount);
        StepError = string.Empty;
        return true;
    }

    /// <summary>Touche <c>*</c> : espèces. Ouvre la saisie du montant reçu ; rien n'est envoyé.</summary>
    [RelayCommand]
    public void BeginCash()
    {
        if (EnterCheckout())
        {
            checkoutMethod = PaymentMethods.Cash;
            ReceivedText = string.Empty;
            OnPropertyChanged(nameof(ChangePreviewText));
            Step = SettlementStep.CashAmount;
        }
    }

    /// <summary>Entrée sur le montant reçu : contrôle la saisie et affiche la monnaie à rendre. Rien n'est envoyé.</summary>
    [RelayCommand]
    public void ValidateCashAmount()
    {
        if (Step != SettlementStep.CashAmount)
        {
            return;
        }

        var received = CheckoutTotal;
        if (ReceivedText.Trim().Length > 0 && !SaleInput.TryParseAmount(ReceivedText, out received))
        {
            StepError = "Montant reçu invalide : saisissez un montant (virgule ou point acceptés), ou rien pour le montant exact.";
            return;
        }

        if (received < CheckoutTotal)
        {
            StepError =
                $"Montant reçu insuffisant : {Money.ToDisplay(received)} pour {Money.ToDisplay(CheckoutTotal)} dus. Un ticket impayé se règle en entier.";
            return;
        }

        StepError = string.Empty;
        AmountReceived = received;
        ChangeDue = received - CheckoutTotal;
        Step = SettlementStep.CashConfirm;
    }

    /// <summary>Touche TPE (provisoire, D7) : ouvre la confirmation ; rien n'est envoyé.</summary>
    [RelayCommand]
    public void BeginCard()
    {
        if (EnterCheckout())
        {
            checkoutMethod = PaymentMethods.Card;
            Step = SettlementStep.CardConfirm;
        }
    }

    /// <summary>Échap : revient d'un cran. Sans effet pendant un envoi ou quand une réponse est attendue.</summary>
    [RelayCommand]
    public async Task CancelStepAsync()
    {
        if (IsSending)
        {
            return;
        }

        StepError = string.Empty;
        switch (Step)
        {
            case SettlementStep.ChooseCustomer or SettlementStep.CashAmount or SettlementStep.CardConfirm:
                Step = SettlementStep.None;
                break;
            case SettlementStep.CashConfirm:
                Step = SettlementStep.CashAmount;
                break;
            case SettlementStep.Done:
                await FinishAsync();
                break;
        }
    }

    // ---------- Envoi ----------

    /// <summary>
    /// Entrée sur une confirmation : envoie le règlement. Verrouillé pendant l'envoi : un double
    /// appui ou un double clic ne produit qu'un seul envoi.
    /// </summary>
    [RelayCommand]
    public Task ConfirmSettlementAsync()
    {
        if (IsSending)
        {
            return Task.CompletedTask;
        }

        var method = Step switch
        {
            SettlementStep.CashConfirm => PaymentMethods.Cash,
            SettlementStep.CardConfirm => PaymentMethods.Card,
            _ => null,
        };
        if (method is null || method != checkoutMethod || checkoutLines.Count == 0)
        {
            return Task.CompletedTask;
        }

        var ids = checkoutLines.Select(line => line.Id).ToList();
        decimal? received = method == PaymentMethods.Cash ? AmountReceived : null;
        var amount = CheckoutTotal;
        var signature = string.Create(
            CultureInfo.InvariantCulture, $"{method}|{amount}|{received}|{string.Join(';', ids.Order())}");
        return SendSettlementAsync(new SettleReceivablesRequest(settleOperationIds.For(signature), ids, method, amount, received));
    }

    /// <summary>« Réessayer » après un règlement non confirmé : la même demande, le même identifiant d'opération.</summary>
    [RelayCommand]
    public Task RetrySettlementAsync() =>
        Step == SettlementStep.Unconfirmed && unconfirmedSettlement is { } request && !IsSending
            ? SendSettlementAsync(request)
            : Task.CompletedTask;

    private async Task SendSettlementAsync(SettleReceivablesRequest request)
    {
        IsSending = true;
        StepError = string.Empty;
        try
        {
            var result = await receivables.SettleAsync(request, CancellationToken.None);
            settleOperationIds.Forget();
            unconfirmedSettlement = null;
            await OnSettlementRecordedAsync(result);
        }
        catch (ServerRefusedException refused) when (!IsUnknownOutcome(refused))
        {
            // Le serveur a répondu non : rien n'est encaissé, la tentative est close.
            settleOperationIds.Forget();
            unconfirmedSettlement = null;
            await OnSettlementRefusedAsync(refused);
        }
        catch (Exception exception) when (IsUnknownOutcome(exception))
        {
            // Aucune réponse : le règlement est peut-être enregistré. On ne rend pas la monnaie avant de savoir.
            unconfirmedSettlement = request;
            Step = SettlementStep.Unconfirmed;
        }
        finally
        {
            IsSending = false;
        }
    }

    private async Task OnSettlementRecordedAsync(SettlementResult result)
    {
        switch (result.Status)
        {
            case PaymentStatuses.Confirmed:
                Finish(result);
                break;
            case PaymentStatuses.Pending:
                // TPE : rien n'est soldé tant que le caissier n'a pas répondu « accepté ».
                PendingSettlement = result;
                Step = SettlementStep.CardPending;
                break;
            default:
                // Réponse rejouée d'un règlement par carte déjà refusé : les tickets sont restés ouverts.
                Step = SettlementStep.None;
                await ReloadAsync();
                ErrorMessage = "Ce règlement par carte a été refusé : les tickets restent impayés.";
                break;
        }
    }

    /// <summary>Le refus du serveur est affiché tel quel ; le tableau est relu, car un autre poste a pu encaisser.</summary>
    private async Task OnSettlementRefusedAsync(ServerRefusedException refused)
    {
        Step = SettlementStep.None;
        if (RequireIdentificationIfLost(refused))
        {
            ErrorMessage = refused.Message;
            return;
        }

        if (refused.Code == CashErrorCodes.NoOpenSession)
        {
            CashSession = null;
            IsCashStateKnown = true;
        }

        await PruneSelectionAsync();
        await ReloadAsync();
        ErrorMessage = refused.Message;
    }

    /// <summary>
    /// Relit tous les tickets du client de la sélection (même ceux que le filtre cache) et
    /// retire ceux qui ne sont plus réglables : soldés ailleurs, bloqués par un TPE en attente…
    /// </summary>
    private async Task PruneSelectionAsync()
    {
        if (selection.Count == 0)
        {
            return;
        }

        try
        {
            var table = await receivables.GetReceivablesAsync(
                new ReceivablesQuery(null, selection[0].CustomerId, Limit: ReceivablesQueryLimit), CancellationToken.None);
            var current = table.Lines.ToDictionary(line => line.Id);
            selection.RemoveAll(line => !current.TryGetValue(line.Id, out var fresh) || !IsSettleable(fresh));
            RefreshSelection();
        }
        catch (Exception exception) when (exception is ServerRefusedException || IsUnreachable(exception))
        {
            // La lecture du tableau qui suit affichera l'incident ; le serveur refusera de toute façon un ticket soldé.
        }
    }

    /// <summary>Limite haute du serveur pour une lecture du tableau.</summary>
    private const int ReceivablesQueryLimit = 1000;

    // ---------- Réponse du TPE ----------

    /// <summary>
    /// Ctrl+T sur une ligne « TPE en attente » (D23) : relit le règlement et demande la
    /// réponse du terminal. Rien n'est envoyé avant « accepté » ou « refusé ».
    /// </summary>
    [RelayCommand]
    public async Task BeginPendingAnswerAsync()
    {
        if (!CanAnswerCurrentPending || IsStepOpen || IsSending || CurrentRow?.Line.PendingSettlementId is not { } settlementId)
        {
            return;
        }

        ClearMessages();
        SettlementResult? settlement = null;
        if (!await RunServerAsync(async () => settlement = await receivables.GetSettlementAsync(settlementId, CancellationToken.None)))
        {
            return;
        }

        if (settlement is null || settlement.Status != PaymentStatuses.Pending)
        {
            await ReloadAsync();
            ErrorMessage = "Ce règlement n'attend plus de réponse du TPE : le tableau est relu.";
            return;
        }

        CheckoutCustomerText = settlement.CustomerName;
        CheckoutTicketsText = string.Join(
            Environment.NewLine, settlement.Receivables.Select(line => $"{line.TicketNumber} — {Money.ToDisplay(line.Amount)}"));
        CheckoutTotal = settlement.Amount;
        PendingSettlement = settlement;
        StepError = string.Empty;
        Step = SettlementStep.CardPending;
    }

    /// <summary>
    /// « Paiement accepté » ou « Paiement refusé », saisi par le caissier (D6). Accepté : les
    /// tickets sont soldés. Refusé : ils restent ouverts et la sélection est conservée.
    /// </summary>
    public async Task AnswerCardAsync(bool accepted)
    {
        if (Step != SettlementStep.CardPending || PendingSettlement is not { } pending || IsSending)
        {
            return;
        }

        var request = new CardResultRequest(cardOperationIds.For($"{pending.Id}|{accepted}"), accepted);
        IsSending = true;
        StepError = string.Empty;
        SettlementResult? result = null;
        ServerRefusedException? refusal = null;
        try
        {
            result = await receivables.ResolveCardAsync(pending.Id, request, CancellationToken.None);
            cardOperationIds.Forget();
        }
        catch (ServerRefusedException refused) when (!IsUnknownOutcome(refused))
        {
            cardOperationIds.Forget();
            refusal = refused;
        }
        catch (Exception exception) when (IsUnknownOutcome(exception))
        {
            StepError = "Réponse non confirmée : le serveur n'a pas répondu. Donnez de nouveau la même réponse.";
            return;
        }
        finally
        {
            IsSending = false;
        }

        PendingSettlement = null;
        if (refusal is not null)
        {
            Step = SettlementStep.None;
            if (RequireIdentificationIfLost(refusal))
            {
                ErrorMessage = refusal.Message;
                return;
            }

            await ReloadAsync();
            ErrorMessage = refusal.Message;
            return;
        }

        if (result!.Status == PaymentStatuses.Confirmed)
        {
            Finish(result);
            return;
        }

        Step = SettlementStep.None;
        await ReloadAsync();
        InfoMessage =
            $"Paiement refusé : {ReceivableFormat.Tickets(result.Receivables.Count)} de {result.CustomerName} "
            + $"({Money.ToDisplay(result.Amount)}) reste{(result.Receivables.Count > 1 ? "nt" : string.Empty)} impayé{(result.Receivables.Count > 1 ? "s" : string.Empty)}. "
            + "Encaissez autrement ou laissez la dette ouverte.";
    }

    [RelayCommand]
    public Task AcceptCardAsync() => AnswerCardAsync(true);

    [RelayCommand]
    public Task RefuseCardAsync() => AnswerCardAsync(false);

    // ---------- Choix du client ----------

    private void OpenCustomerChoice(IReadOnlyList<CustomerSummary> found)
    {
        CustomerResults.Clear();
        foreach (var customer in found)
        {
            CustomerResults.Add(customer);
        }

        SelectedCustomerResult = CustomerResults.FirstOrDefault();
        StepError = string.Empty;
        Step = SettlementStep.ChooseCustomer;
    }

    public void MoveCustomerSelection(int delta)
    {
        if (CustomerResults.Count == 0)
        {
            return;
        }

        var index = SelectedCustomerResult is null ? -1 : CustomerResults.IndexOf(SelectedCustomerResult);
        SelectedCustomerResult = CustomerResults[Math.Clamp(index + delta, 0, CustomerResults.Count - 1)];
    }

    /// <summary>Entrée sur la liste des clients : le tableau ne montre plus que ses tickets.</summary>
    [RelayCommand]
    public async Task ChooseCustomerAsync()
    {
        if (Step != SettlementStep.ChooseCustomer || SelectedCustomerResult is not { } chosen)
        {
            return;
        }

        Step = SettlementStep.None;
        CustomerResults.Clear();
        await FilterOnCustomerAsync(chosen);
    }

    // ---------- Récapitulatif ----------

    private void Finish(SettlementResult result)
    {
        selection.RemoveAll(line => result.Receivables.Any(settled => settled.Id == line.Id));
        RefreshSelection();
        ClearMessages();
        StepError = string.Empty;
        CompletedSettlement = result;
        Step = SettlementStep.Done;
    }

    /// <summary>Entrée sur le récapitulatif : retour au tableau, relu.</summary>
    [RelayCommand]
    public async Task FinishAsync()
    {
        if (Step != SettlementStep.Done)
        {
            return;
        }

        CompletedSettlement = null;
        Step = SettlementStep.None;
        await ReloadAsync();
    }
}
