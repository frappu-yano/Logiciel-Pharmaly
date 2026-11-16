using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.Sales;

// Encaissement : espèces, TPE, impayé. Choisir un moyen de paiement n'est jamais confirmer :
// chaque issue passe par une étape de confirmation avant l'envoi au serveur.
public sealed partial class SalesViewModel
{
    public const string UnconfirmedTitle = "Vente non confirmée — ne pas remettre la marchandise";
    public const string EmptyCartMessage = "Le panier est vide : scannez ou cherchez un produit.";
    public const string CustomerRequiredMessage = "Une vente impayée exige un client : choisissez-le ou créez-le.";

    private readonly OperationIdKeeper saleOperationIds = new();
    private readonly OperationIdKeeper cardOperationIds = new();
    private readonly OperationIdKeeper customerOperationIds = new();
    private CancellationTokenSource? customerSearchCancellation;

    /// <summary>Demande envoyée sans réponse : « Réessayer » la renvoie telle quelle, avec le même identifiant d'opération.</summary>
    private CreateSaleRequest? unconfirmedSale;

    /// <summary>Mode de paiement en cours de confirmation (<see cref="SalePaymentModes"/>).</summary>
    private string? checkoutMode;

    // ---------- Espèces ----------

    /// <summary>Montant reçu, tel que saisi. Vide : le client donne le montant exact.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangePreviewText))]
    private string receivedText = string.Empty;

    /// <summary>Montant reçu validé.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AmountReceivedText))]
    private decimal amountReceived;

    /// <summary>Monnaie à rendre, calculée sur le montant reçu validé.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangeDueText))]
    private decimal changeDue;

    public string AmountReceivedText => Money.ToDisplay(AmountReceived);

    public string ChangeDueText => Money.ToDisplay(ChangeDue);

    /// <summary>Monnaie à rendre pendant la frappe du montant reçu ; « — » tant que la saisie ne suffit pas.</summary>
    public string ChangePreviewText
    {
        get
        {
            if (ReceivedText.Trim().Length == 0)
            {
                return Money.ToDisplay(0);
            }

            return SaleInput.TryParseAmount(ReceivedText, out var received) && received >= Total
                ? Money.ToDisplay(received - Total)
                : "—";
        }
    }

    // ---------- Impayé ----------

    [ObservableProperty]
    private string customerQuery = string.Empty;

    [ObservableProperty]
    private CustomerSummary? selectedCustomerResult;

    [ObservableProperty]
    private bool hasCustomerResults;

    /// <summary>Client de la vente impayée.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomerText))]
    private CustomerSummary? customer;

    [ObservableProperty]
    private string newCustomerName = string.Empty;

    [ObservableProperty]
    private string newCustomerPhone = string.Empty;

    public ObservableCollection<CustomerSummary> CustomerResults { get; } = [];

    public string CustomerText => Customer is null
        ? string.Empty
        : string.IsNullOrWhiteSpace(Customer.Phone) ? Customer.Name : $"{Customer.Name} — {Customer.Phone}";

    // ---------- TPE en attente ----------

    /// <summary>Vente enregistrée « en attente TPE » : le caissier doit répondre accepté ou refusé.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingCardText))]
    private SaleDetail? pendingCardSale;

    public string PendingCardText => PendingCardSale is { } sale
        ? $"Ticket {sale.TicketNumber} — {Money.ToDisplay(sale.Total)}"
        : string.Empty;

    // ---------- Total différent ----------

    [ObservableProperty]
    private string totalChangeMessage = string.Empty;

    [ObservableProperty]
    private string totalChangeSummary = string.Empty;

    /// <summary>Une ligne par prix modifié : « Produit : ancien prix → nouveau prix ».</summary>
    public ObservableCollection<string> PriceChanges { get; } = [];

    private bool pricesReloaded;

    // ---------- Écran de fin ----------

    /// <summary>Dernière vente enregistrée, affichée sur l'écran de fin.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DoneTicketText), nameof(DoneTotalText), nameof(DoneChangeText), nameof(DoneReceivedText),
        nameof(DoneCustomerText), nameof(IsDoneCash), nameof(IsDoneCard), nameof(IsDoneUnpaid))]
    private SaleDetail? completedSale;

    public string DoneTicketText => CompletedSale is { } sale ? $"Ticket {sale.TicketNumber}" : string.Empty;

    public string DoneTotalText => CompletedSale is { } sale ? Money.ToDisplay(sale.Total) : string.Empty;

    public string DoneChangeText => Money.ToDisplay(CompletedSale?.Payment?.ChangeGiven ?? 0m);

    public string DoneReceivedText => $"Reçu {Money.ToDisplay(CompletedSale?.Payment?.AmountReceived ?? 0m)}";

    public string DoneCustomerText => CompletedSale?.CustomerName is { } name ? $"Client : {name}" : string.Empty;

    public bool IsDoneCash => CompletedSale?.PaymentMode == SalePaymentModes.Cash;

    public bool IsDoneCard => CompletedSale?.PaymentMode == SalePaymentModes.Card;

    public bool IsDoneUnpaid => CompletedSale?.PaymentMode == SalePaymentModes.Unpaid;

    // ---------- Choix du moyen de paiement ----------

    /// <summary>Vérifie que le panier peut être encaissé, après avoir attendu les lectures en route.</summary>
    private async Task<bool> EnterCheckoutAsync(bool allowed)
    {
        if (!allowed || !await EnterStepAsync())
        {
            return false;
        }

        ClearMessages();
        if (Lines.Count == 0)
        {
            ErrorMessage = EmptyCartMessage;
            return false;
        }

        if (IsDiscountTooLarge)
        {
            ErrorMessage =
                $"La remise ({Money.ToDisplay(DiscountAmount)}) dépasse le total de la vente ({Money.ToDisplay(Subtotal)}) : corrigez-la avant d'encaisser.";
            return false;
        }

        return true;
    }

    /// <summary>Touche <c>*</c> : espèces. Ouvre la saisie du montant reçu ; rien n'est envoyé.</summary>
    [RelayCommand]
    public async Task BeginCashAsync()
    {
        if (await EnterCheckoutAsync(CanPayCash))
        {
            checkoutMode = SalePaymentModes.Cash;
            ReceivedText = string.Empty;
            OnPropertyChanged(nameof(ChangePreviewText));
            Step = CheckoutStep.CashAmount;
        }
    }

    /// <summary>Entrée sur le montant reçu : contrôle la saisie et affiche la monnaie à rendre. Rien n'est envoyé.</summary>
    [RelayCommand]
    public void ValidateCashAmount()
    {
        if (Step != CheckoutStep.CashAmount)
        {
            return;
        }

        var received = Total;
        if (ReceivedText.Trim().Length > 0 && !SaleInput.TryParseAmount(ReceivedText, out received))
        {
            StepError = "Montant reçu invalide : saisissez un montant (virgule ou point acceptés), ou rien pour le montant exact.";
            return;
        }

        if (received < Total)
        {
            StepError = $"Montant reçu insuffisant : {Money.ToDisplay(received)} pour un total de {Money.ToDisplay(Total)}.";
            return;
        }

        StepError = string.Empty;
        AmountReceived = received;
        ChangeDue = received - Total;
        Step = CheckoutStep.CashConfirm;
    }

    /// <summary>Touche TPE (provisoire) : ouvre la confirmation ; rien n'est envoyé.</summary>
    [RelayCommand]
    public async Task BeginCardAsync()
    {
        if (await EnterCheckoutAsync(CanPayCard))
        {
            checkoutMode = SalePaymentModes.Card;
            Step = CheckoutStep.CardConfirm;
        }
    }

    /// <summary>Impayé : le client est obligatoire (D5). Ouvre le choix du client ; rien n'est envoyé.</summary>
    [RelayCommand]
    public async Task BeginUnpaidAsync()
    {
        if (await EnterCheckoutAsync(CanSellUnpaid))
        {
            checkoutMode = SalePaymentModes.Unpaid;
            if (Customer is not null)
            {
                Step = CheckoutStep.UnpaidConfirm;
                return;
            }

            CustomerQuery = string.Empty;
            CustomerResults.Clear();
            Step = CheckoutStep.UnpaidCustomer;
        }
    }

    /// <summary>Échap : revient d'un cran. Sans effet pendant un envoi ou quand une réponse est attendue.</summary>
    [RelayCommand]
    public void CancelStep()
    {
        if (IsSending)
        {
            return;
        }

        StepError = string.Empty;
        switch (Step)
        {
            case CheckoutStep.Quantity or CheckoutStep.Discount or CheckoutStep.Abandon or CheckoutStep.CashAmount
                or CheckoutStep.CardConfirm or CheckoutStep.TotalChanged:
                editedLine = null;
                Step = CheckoutStep.None;
                break;
            case CheckoutStep.CashConfirm:
                Step = CheckoutStep.CashAmount;
                break;
            case CheckoutStep.UnpaidCustomer:
                Customer = null;
                Step = CheckoutStep.None;
                break;
            case CheckoutStep.UnpaidNewCustomer or CheckoutStep.UnpaidConfirm:
                Customer = null;
                Step = CheckoutStep.UnpaidCustomer;
                break;
            case CheckoutStep.Done:
                StartNextSale();
                break;
        }
    }

    // ---------- Client d'une vente impayée ----------

    partial void OnCustomerQueryChanged(string value) => _ = SearchCustomersAfterDelayAsync(value);

    private async Task SearchCustomersAfterDelayAsync(string term)
    {
        customerSearchCancellation?.Cancel();
        if (string.IsNullOrWhiteSpace(term))
        {
            CustomerResults.Clear();
            return;
        }

        var cancellation = customerSearchCancellation = new CancellationTokenSource();
        try
        {
            await Task.Delay(searchDelay, cancellation.Token);
            await SearchCustomersNowAsync(term.Trim(), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Une frappe plus récente a remplacé cette recherche.
        }
    }

    private async Task SearchCustomersNowAsync(string term, CancellationToken cancellationToken)
    {
        try
        {
            var found = await customers.SearchCustomersAsync(term, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            CustomerResults.Clear();
            foreach (var match in found)
            {
                CustomerResults.Add(match);
            }

            SelectedCustomerResult = CustomerResults.FirstOrDefault();
            StepError = CustomerResults.Count == 0 ? $"Aucun client trouvé pour « {term} »." : string.Empty;
        }
        catch (ServerRefusedException refused)
        {
            StepError = refused.Message;
            RequireIdentificationIfLost(refused);
        }
        catch (Exception exception) when (IsUnreachable(exception) && !cancellationToken.IsCancellationRequested)
        {
            StepError = ServerUnreachableMessage;
        }
    }

    public void MoveCustomerSelection(int delta)
    {
        if (CustomerResults.Count == 0)
        {
            return;
        }

        var current = SelectedCustomerResult is null ? -1 : CustomerResults.IndexOf(SelectedCustomerResult);
        SelectedCustomerResult = CustomerResults[Math.Clamp(current + delta, 0, CustomerResults.Count - 1)];
    }

    /// <summary>Entrée dans la recherche de client : retient le client choisi et passe à la confirmation.</summary>
    [RelayCommand]
    public async Task ChooseCustomerAsync()
    {
        if (Step != CheckoutStep.UnpaidCustomer)
        {
            return;
        }

        if (SelectedCustomerResult is null && CustomerQuery.Trim().Length > 0)
        {
            customerSearchCancellation?.Cancel();
            await SearchCustomersNowAsync(CustomerQuery.Trim(), CancellationToken.None);
            if (CustomerResults.Count != 1)
            {
                return;
            }
        }

        if (SelectedCustomerResult is not { } chosen)
        {
            StepError = CustomerRequiredMessage;
            return;
        }

        Customer = chosen;
        StepError = string.Empty;
        Step = CheckoutStep.UnpaidConfirm;
    }

    /// <summary>Création rapide : le nom reprend ce qui vient d'être cherché.</summary>
    [RelayCommand]
    public void BeginNewCustomer()
    {
        if (Step != CheckoutStep.UnpaidCustomer)
        {
            return;
        }

        customerSearchCancellation?.Cancel();
        NewCustomerName = CustomerQuery.Trim();
        NewCustomerPhone = string.Empty;
        StepError = string.Empty;
        Step = CheckoutStep.UnpaidNewCustomer;
    }

    [RelayCommand]
    public async Task CreateCustomerAsync()
    {
        if (Step != CheckoutStep.UnpaidNewCustomer || IsSending)
        {
            return;
        }

        var name = NewCustomerName.Trim();
        var phone = NewCustomerPhone.Trim();
        if (name.Length == 0)
        {
            StepError = "Le nom du client est obligatoire.";
            return;
        }

        var request = new CreateCustomerRequest(
            customerOperationIds.For($"{name}|{phone}"), name, phone.Length == 0 ? null : phone);
        IsSending = true;
        StepError = string.Empty;
        try
        {
            var created = await customers.CreateCustomerAsync(request, CancellationToken.None);
            customerOperationIds.Forget();
            Customer = created;
            Step = CheckoutStep.UnpaidConfirm;
        }
        catch (ServerRefusedException refused) when (!IsUnknownOutcome(refused))
        {
            customerOperationIds.Forget();
            StepError = refused.Message;
            RequireIdentificationIfLost(refused);
        }
        catch (Exception exception) when (IsUnknownOutcome(exception))
        {
            StepError = "Client non confirmé : le serveur n'a pas répondu. Appuyez sur Entrée pour réessayer.";
        }
        finally
        {
            IsSending = false;
        }
    }

    // ---------- Confirmation et envoi ----------

    /// <summary>
    /// Entrée sur une confirmation (espèces, TPE, impayé) : envoie la vente. Verrouillé pendant
    /// l'envoi : un double appui ou un double clic ne produit qu'un seul envoi.
    /// </summary>
    [RelayCommand]
    public Task ConfirmSaleAsync()
    {
        if (IsSending)
        {
            return Task.CompletedTask;
        }

        var mode = Step switch
        {
            CheckoutStep.CashConfirm => SalePaymentModes.Cash,
            CheckoutStep.CardConfirm => SalePaymentModes.Card,
            CheckoutStep.UnpaidConfirm => SalePaymentModes.Unpaid,
            _ => null,
        };
        if (mode is null || mode != checkoutMode)
        {
            return Task.CompletedTask;
        }

        if (mode == SalePaymentModes.Unpaid && Customer is null)
        {
            StepError = CustomerRequiredMessage;
            Step = CheckoutStep.UnpaidCustomer;
            return Task.CompletedTask;
        }

        return SendSaleAsync(BuildRequest(mode));
    }

    /// <summary>
    /// Le poste n'envoie aucun prix ; il envoie le total affiché (<c>ExpectedTotal</c>) pour que
    /// le serveur refuse la vente si un prix a changé entre-temps.
    /// </summary>
    private CreateSaleRequest BuildRequest(string mode)
    {
        var lines = Lines.Select(line => new SaleLineRequest(line.ProductId, line.Quantity)).ToList();
        decimal? received = mode == SalePaymentModes.Cash ? AmountReceived : null;
        var customerId = mode == SalePaymentModes.Unpaid ? Customer?.Id : null;
        decimal? discountValue = DiscountKind is null ? null : DiscountValue;
        var total = Total;
        var signature = string.Create(
            CultureInfo.InvariantCulture,
            $"{mode}|{DiscountKind}|{discountValue}|{received}|{customerId}|{total}|{string.Join(';', lines.Select(line => FormattableString.Invariant($"{line.ProductId}:{line.Quantity}")))}");
        return new CreateSaleRequest(
            saleOperationIds.For(signature), lines, mode, DiscountKind, discountValue, received, customerId, total);
    }

    /// <summary>« Réessayer » après une vente non confirmée : la même demande, le même identifiant d'opération.</summary>
    [RelayCommand]
    public Task RetrySaleAsync() =>
        Step == CheckoutStep.Unconfirmed && unconfirmedSale is { } request && !IsSending
            ? SendSaleAsync(request)
            : Task.CompletedTask;

    private async Task SendSaleAsync(CreateSaleRequest request)
    {
        IsSending = true;
        StepError = string.Empty;
        try
        {
            var sale = await sales.CreateSaleAsync(request, CancellationToken.None);
            saleOperationIds.Forget();
            unconfirmedSale = null;
            OnSaleRecorded(sale);
        }
        catch (ServerRefusedException refused) when (!IsUnknownOutcome(refused))
        {
            // Le serveur a répondu non : rien n'est enregistré, la tentative est close.
            saleOperationIds.Forget();
            unconfirmedSale = null;
            await OnSaleRefusedAsync(refused, request);
        }
        catch (Exception exception) when (IsUnknownOutcome(exception))
        {
            // Aucune réponse : la vente est peut-être enregistrée. Le panier reste figé jusqu'à la réponse.
            unconfirmedSale = request;
            Step = CheckoutStep.Unconfirmed;
        }
        finally
        {
            IsSending = false;
        }
    }

    private void OnSaleRecorded(SaleDetail sale)
    {
        switch (sale.Status)
        {
            case SaleStatuses.CardPending:
                PendingCardSale = sale;
                Step = CheckoutStep.CardPending;
                break;
            case SaleStatuses.Completed:
                Finish(sale);
                break;
            default:
                // Ticket déjà annulé entre-temps (réponse rejouée) : le panier reste là pour refaire la vente.
                CloseStep();
                ErrorMessage = $"Le ticket {sale.TicketNumber} est annulé : la vente n'est pas enregistrée.";
                break;
        }
    }

    private async Task OnSaleRefusedAsync(ServerRefusedException refused, CreateSaleRequest request)
    {
        if (RequireIdentificationIfLost(refused))
        {
            CloseStep();
            ErrorMessage = refused.Message;
            return;
        }

        switch (refused.Code)
        {
            case SaleErrorCodes.TotalMismatch:
                await ShowTotalChangeAsync(refused.Message, request.ExpectedTotal ?? Total);
                break;

            case CashErrorCodes.NoOpenSession:
                // La caisse a été fermée entre-temps : retour à l'écran d'ouverture, panier conservé.
                CloseStep();
                CashSession = null;
                Screen = SalesScreen.CashClosed;
                ErrorMessage = refused.Message;
                break;

            case ApiErrorCodes.InsufficientStock:
                // Le message du serveur nomme le produit et les quantités : affiché tel quel, panier conservé.
                CloseStep();
                await ReloadProductsAsync();
                ErrorMessage = refused.Message;
                break;

            default:
                CloseStep();
                ErrorMessage = refused.Message;
                break;
        }
    }

    // ---------- Total différent ----------

    private async Task ShowTotalChangeAsync(string serverMessage, decimal previousTotal)
    {
        var previousPrices = Lines.ToDictionary(line => line.ProductId, line => line.UnitPrice);
        pricesReloaded = await ReloadProductsAsync();

        PriceChanges.Clear();
        foreach (var line in Lines.Where(line => previousPrices[line.ProductId] != line.UnitPrice))
        {
            PriceChanges.Add($"{line.Name} : {Money.ToDisplay(previousPrices[line.ProductId])} → {line.UnitPriceText}");
        }

        TotalChangeMessage = serverMessage;
        TotalChangeSummary = pricesReloaded
            ? $"Ancien total : {Money.ToDisplay(previousTotal)} — nouveau total : {TotalText}"
            : "Les prix n'ont pas pu être relus sur le serveur. Revenez au panier et scannez de nouveau les produits.";
        StepError = string.Empty;
        Step = CheckoutStep.TotalChanged;
    }

    /// <summary>
    /// Entrée sur « le total a changé » : la différence est vue, l'encaissement reprend à sa
    /// première étape avec le nouveau total. Rien n'est envoyé sans une nouvelle confirmation.
    /// </summary>
    [RelayCommand]
    public async Task AcknowledgeTotalChangeAsync()
    {
        if (Step != CheckoutStep.TotalChanged)
        {
            return;
        }

        CloseStep();
        if (!pricesReloaded)
        {
            return;
        }

        switch (checkoutMode)
        {
            case SalePaymentModes.Cash:
                await BeginCashAsync();
                break;
            case SalePaymentModes.Card:
                await BeginCardAsync();
                break;
            case SalePaymentModes.Unpaid:
                await BeginUnpaidAsync();
                break;
        }
    }

    // ---------- Réponse du TPE ----------

    /// <summary>
    /// « Paiement accepté » ou « Paiement refusé », saisi par le caissier (D6). Refusé : le
    /// serveur annule la vente et restitue le stock (D19) ; le panier reste à l'écran.
    /// </summary>
    public async Task AnswerCardAsync(bool accepted)
    {
        if (Step != CheckoutStep.CardPending || PendingCardSale is not { } pending || IsSending)
        {
            return;
        }

        var request = new CardResultRequest(cardOperationIds.For($"{pending.Id}|{accepted}"), accepted);
        IsSending = true;
        StepError = string.Empty;
        try
        {
            var sale = await sales.ResolveCardAsync(pending.Id, request, CancellationToken.None);
            cardOperationIds.Forget();
            PendingCardSale = null;
            if (sale.Status == SaleStatuses.Completed)
            {
                Finish(sale);
            }
            else
            {
                CloseStep();
                await ReloadProductsAsync();
                InfoMessage =
                    $"Paiement refusé : la vente {sale.TicketNumber} est annulée et le stock est restitué. "
                    + "Le panier est conservé : encaissez autrement (ce sera un nouveau ticket).";
            }
        }
        catch (ServerRefusedException refused) when (!IsUnknownOutcome(refused))
        {
            cardOperationIds.Forget();
            PendingCardSale = null;
            CloseStep();
            ErrorMessage = RequireIdentificationIfLost(refused)
                ? refused.Message
                : $"{refused.Message} Vérifiez le ticket {pending.TicketNumber} dans l'historique avant de continuer.";
        }
        catch (Exception exception) when (IsUnknownOutcome(exception))
        {
            StepError = "Réponse non confirmée : le serveur n'a pas répondu. Donnez de nouveau la même réponse.";
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    public Task AcceptCardAsync() => AnswerCardAsync(true);

    [RelayCommand]
    public Task RefuseCardAsync() => AnswerCardAsync(false);

    // ---------- Fin de vente ----------

    private void Finish(SaleDetail sale)
    {
        ClearCart();
        ClearMessages();
        CompletedSale = sale;
        StepError = string.Empty;
        Step = CheckoutStep.Done;

        // Point d'accroche de l'impression : rien n'est imprimé ni simulé pour l'instant.
        printer.Print(sale);
    }

    /// <summary>Un seul appui après l'écran de fin : panier vide, curseur dans la recherche.</summary>
    [RelayCommand]
    public void StartNextSale()
    {
        if (Step != CheckoutStep.Done)
        {
            return;
        }

        CompletedSale = null;
        Query = string.Empty;
        CloseStep();
    }
}
