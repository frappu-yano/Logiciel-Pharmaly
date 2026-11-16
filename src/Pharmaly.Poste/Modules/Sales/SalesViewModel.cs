using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.Sales;

/// <summary>Écran affiché par le module.</summary>
public enum SalesScreen
{
    /// <summary>L'état de la caisse du poste n'est pas encore connu.</summary>
    Loading,

    /// <summary>L'employé n'a ni le droit de vendre ni celui de consulter l'historique.</summary>
    NoRight,

    /// <summary>La caisse du poste n'est pas ouverte : aucune vente n'est possible.</summary>
    CashClosed,

    /// <summary>Saisie du panier et encaissement.</summary>
    Cart,

    /// <summary>Historique des ventes du site.</summary>
    History,
}

/// <summary>Étape affichée par-dessus le panier. Tant qu'une étape est ouverte, le panier est figé.</summary>
public enum CheckoutStep
{
    None,

    /// <summary>Saisie directe de la quantité de la ligne sélectionnée.</summary>
    Quantity,

    /// <summary>Saisie de la remise sur la vente entière.</summary>
    Discount,

    /// <summary>Confirmation avant de vider le panier.</summary>
    Abandon,

    /// <summary>Espèces : saisie du montant reçu.</summary>
    CashAmount,

    /// <summary>Espèces : monnaie à rendre affichée, confirmation attendue.</summary>
    CashConfirm,

    /// <summary>TPE : confirmation avant d'enregistrer la vente « en attente TPE ».</summary>
    CardConfirm,

    /// <summary>TPE : la vente est enregistrée, le caissier doit répondre « accepté » ou « refusé ».</summary>
    CardPending,

    /// <summary>Impayé : choix du client.</summary>
    UnpaidCustomer,

    /// <summary>Impayé : création rapide d'un client.</summary>
    UnpaidNewCustomer,

    /// <summary>Impayé : confirmation montrant le client et le montant.</summary>
    UnpaidConfirm,

    /// <summary>L'envoi n'a pas reçu de réponse : on ne sait pas si la vente est enregistrée.</summary>
    Unconfirmed,

    /// <summary>Le serveur a calculé un autre total : la différence est montrée avant de reconfirmer.</summary>
    TotalChanged,

    /// <summary>Vente enregistrée : écran de fin.</summary>
    Done,
}

/// <summary>
/// Module Vente : panier, encaissement (espèces, TPE, impayé), historique et annulation.
/// Le poste ne décide de rien : le serveur fixe les prix, contrôle le stock et les droits.
/// Ce modèle de vue garantit seulement qu'une vente n'est jamais envoyée deux fois et que
/// l'employé sait toujours si elle est enregistrée.
/// </summary>
public sealed partial class SalesViewModel : ModuleViewModel
{
    public const string CashClosedTitle = "La caisse n'est pas ouverte";
    public const string CashClosedWithoutRight = "Un employé habilité à ouvrir la caisse doit l'ouvrir avant toute vente.";
    public const string NoRightMessage = "Vos droits ne permettent ni de vendre ni de consulter l'historique des ventes.";

    /// <summary>Affiché en permanence dans l'aide du panier : le verrouillage détruit le module.</summary>
    public const string LockWarning = "F12 (verrouillage) : le panier en cours est perdu.";

    /// <summary>
    /// Code que le transport donne à une réponse du serveur qui n'est pas un refus lisible
    /// (erreur interne, par exemple) : on ne sait alors pas si la commande est enregistrée.
    /// </summary>
    internal const string UnreadableAnswerCode = "ERREUR";

    private readonly ISalesClient sales;
    private readonly ICustomersClient customers;
    private readonly ICashSessionClient cash;
    private readonly ICatalogClient catalog;
    private readonly ITicketPrinter printer;
    private readonly TimeSpan searchDelay;
    private readonly OperationIdKeeper openingOperationIds = new();

    public SalesViewModel(
        ISalesClient sales,
        ICustomersClient customers,
        ICashSessionClient cash,
        ICatalogClient catalog,
        SalesRights rights,
        TimeSpan searchDelay,
        ITicketPrinter? printer = null)
    {
        this.sales = sales;
        this.customers = customers;
        this.cash = cash;
        this.catalog = catalog;
        this.searchDelay = searchDelay;
        this.printer = printer ?? new NoTicketPrinter();
        Rights = rights;
        History = new SalesHistoryViewModel(sales, rights, RequireIdentificationIfLost);
        Results.CollectionChanged += (_, _) => HasResults = Results.Count > 0;
        CustomerResults.CollectionChanged += (_, _) => HasCustomerResults = CustomerResults.Count > 0;
    }

    public SalesRights Rights { get; }

    /// <summary>Sous-écran « historique et annulation ».</summary>
    public SalesHistoryViewModel History { get; }

    /// <summary>Touche Espèces affichée à l'écran : <c>*</c>, confirmée par la direction.</summary>
    public string CashKeyLabel => SalesShortcuts.CashSymbol;

    /// <summary>Touche TPE affichée à l'écran : lue dans la constante provisoire (D7).</summary>
    public string CardKeyLabel => SalesShortcuts.CardLabel;

    public string UnpaidKeyLabel => SalesShortcuts.UnpaidLabel;

    public string DiscountKeyLabel => SalesShortcuts.DiscountLabel;

    public string CardAcceptedKeyLabel => SalesShortcuts.CardAcceptedLabel;

    public string CardRefusedKeyLabel => SalesShortcuts.CardRefusedLabel;

    /// <summary>Espèces et TPE : seulement avec « vendre » et « encaisser ».</summary>
    public bool CanPayCash => Rights.CanSell && Rights.CanCollect;

    public bool CanPayCard => Rights.CanSell && Rights.CanCollect;

    /// <summary>Un employé qui a « vendre » sans « encaisser » ne se voit proposer que l'impayé (D21).</summary>
    public bool CanSellUnpaid => Rights.CanSell;

    public bool CanDiscount => Rights.CanSell && Rights.CanDiscount;

    public bool CanViewHistory => Rights.CanViewHistory;

    public bool CanOpenRegister => Rights.CanOpenRegister;

    public bool CannotOpenRegister => !Rights.CanOpenRegister;

    /// <summary>Vrai si l'historique peut être quitté pour revenir à la vente.</summary>
    public bool CanSell => Rights.CanSell;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsNoRight), nameof(IsCashClosed), nameof(IsCart), nameof(IsHistory), nameof(IsCartEditable))]
    private SalesScreen screen = SalesScreen.Loading;

    public bool IsLoading => Screen == SalesScreen.Loading;

    public bool IsNoRight => Screen == SalesScreen.NoRight;

    public bool IsCashClosed => Screen == SalesScreen.CashClosed;

    public bool IsCart => Screen == SalesScreen.Cart;

    public bool IsHistory => Screen == SalesScreen.History;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStepOpen), nameof(IsCartEditable))]
    private CheckoutStep step;

    public bool IsStepOpen => Step != CheckoutStep.None;

    /// <summary>Le panier ne se modifie que sur l'écran de vente, sans étape ouverte ni envoi en cours.</summary>
    public bool IsCartEditable => IsCart && Step == CheckoutStep.None && !IsSending;

    /// <summary>Refus ou saisie invalide de l'étape ouverte, affiché dans son panneau.</summary>
    [ObservableProperty]
    private string stepError = string.Empty;

    /// <summary>Information utile à l'employé (caisse ouverte, paiement refusé et panier conservé…).</summary>
    [ObservableProperty]
    private string infoMessage = string.Empty;

    /// <summary>Vrai pendant qu'une commande d'écriture attend la réponse du serveur : rien d'autre ne part.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCartEditable))]
    private bool isSending;

    // ---------- Caisse du poste ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CashSessionText))]
    private CashSessionInfo? cashSession;

    public string CashSessionText => CashSession is { } session
        ? $"{session.RegisterName} — ouverte par {session.OpenedByName}"
        : string.Empty;

    /// <summary>Fond de caisse compté, tel que l'employé le saisit.</summary>
    [ObservableProperty]
    private string openingFloatText = string.Empty;

    /// <summary>Vrai quand le fond de caisse est saisi et attend la confirmation.</summary>
    [ObservableProperty]
    private bool isConfirmingOpening;

    [ObservableProperty]
    private string openingConfirmationText = string.Empty;

    /// <summary>Fond de caisse à confirmer, affiché en grand.</summary>
    [ObservableProperty]
    private string openingFloatDisplayText = string.Empty;

    private decimal openingFloat;

    public override async Task ActivateAsync()
    {
        if (!Rights.CanSell)
        {
            if (Rights.CanViewHistory)
            {
                Screen = SalesScreen.History;
                await History.ActivateAsync();
            }
            else
            {
                Screen = SalesScreen.NoRight;
            }

            return;
        }

        // Une étape ouverte (encaissement en cours) ou l'historique ne sont pas dérangés par un retour sur le module.
        if (Screen == SalesScreen.History || Step != CheckoutStep.None || IsSending)
        {
            return;
        }

        await RefreshCashSessionAsync();
    }

    /// <summary>Lit la session de caisse du poste : écran de vente si elle est ouverte, écran d'ouverture sinon.</summary>
    [RelayCommand]
    public async Task RefreshCashSessionAsync()
    {
        CashSessionInfo? session = null;
        if (!await RunServerAsync(async () => session = await cash.GetCurrentSessionAsync(CancellationToken.None)))
        {
            return;
        }

        CashSession = session;
        if (session is null)
        {
            Screen = SalesScreen.CashClosed;
        }
        else
        {
            IsConfirmingOpening = false;
            Screen = SalesScreen.Cart;
        }
    }

    /// <summary>Entrée sur le fond de caisse : la saisie est contrôlée, puis la confirmation est demandée.</summary>
    [RelayCommand]
    public void RequestCashOpening()
    {
        if (!Rights.CanOpenRegister || IsBusy)
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
        OpeningConfirmationText = $"Ouvrir la caisse avec un fond de caisse de {Money.ToDisplay(amount)} ?";
        OpeningFloatDisplayText = Money.ToDisplay(amount);
        IsConfirmingOpening = true;
    }

    [RelayCommand]
    public void CancelCashOpening() => IsConfirmingOpening = false;

    /// <summary>Ouvre la caisse du poste. Le panier éventuel est conservé.</summary>
    [RelayCommand]
    public async Task ConfirmCashOpeningAsync()
    {
        if (!IsConfirmingOpening || IsBusy)
        {
            return;
        }

        var amount = openingFloat;
        var request = new OpenCashSessionRequest(openingOperationIds.For(amount.ToString()), amount);
        CashSessionInfo? opened = null;
        ServerRefusedException? refusal = null;
        var done = await RunServerAsync(async () =>
        {
            try
            {
                opened = await cash.OpenSessionAsync(request, CancellationToken.None);
            }
            catch (ServerRefusedException refused)
            {
                refusal = refused;
                throw;
            }
        });

        if (done)
        {
            openingOperationIds.Forget();
            IsConfirmingOpening = false;
            OpeningFloatText = string.Empty;
            CashSession = opened;
            Screen = SalesScreen.Cart;
            InfoMessage = $"Caisse ouverte avec un fond de caisse de {Money.ToDisplay(amount)}.";
        }
        else if (refusal is not null)
        {
            // Le serveur a répondu : la demande est close, quelle que soit la réponse.
            openingOperationIds.Forget();
            IsConfirmingOpening = false;
            if (refusal.Code == CashErrorCodes.SessionAlreadyOpen)
            {
                // Quelqu'un vient de l'ouvrir : le message du serveur reste affiché, la vente peut commencer.
                var message = refusal.Message;
                await RefreshCashSessionAsync();
                if (IsCart)
                {
                    InfoMessage = message;
                }
            }
        }

        // Panne de réseau : la confirmation reste affichée, Entrée retente avec le même identifiant d'opération.
    }

    // ---------- Historique ----------

    [RelayCommand]
    public async Task OpenHistoryAsync()
    {
        if (!Rights.CanViewHistory || Step != CheckoutStep.None || IsSending || Screen is SalesScreen.History or SalesScreen.NoRight)
        {
            return;
        }

        Screen = SalesScreen.History;
        await History.ActivateAsync();
    }

    /// <summary>Retour à la vente ; le panier est tel qu'il a été laissé.</summary>
    [RelayCommand]
    public async Task CloseHistoryAsync()
    {
        if (Screen != SalesScreen.History || !Rights.CanSell || History.IsStepOpen)
        {
            return;
        }

        Screen = CashSession is null ? SalesScreen.Loading : SalesScreen.Cart;
        await RefreshCashSessionAsync();
    }

    private void ClearMessages()
    {
        ErrorMessage = string.Empty;
        InfoMessage = string.Empty;
    }

    private static bool IsUnreachable(Exception exception) => exception is HttpRequestException or TaskCanceledException;

    /// <summary>Vrai quand on ne peut pas savoir si le serveur a enregistré la commande.</summary>
    private static bool IsUnknownOutcome(Exception exception) =>
        IsUnreachable(exception) || exception is ServerRefusedException { Code: UnreadableAnswerCode };
}
