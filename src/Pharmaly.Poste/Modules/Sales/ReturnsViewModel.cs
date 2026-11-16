using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.Sales;

/// <summary>Étape d'un retour client.</summary>
public enum ReturnStep
{
    /// <summary>Le curseur attend le scan ou le numéro d'un ticket. Un ticket non retournable reste affiché, avec la raison.</summary>
    Search,

    /// <summary>Ticket retrouvé : quantités, motif et note. Rien n'est envoyé.</summary>
    Choose,

    /// <summary>Aperçu du serveur affiché : le remboursement attend la confirmation.</summary>
    Confirm,

    /// <summary>L'envoi n'a pas reçu de réponse : on ne sait pas si le retour est enregistré.</summary>
    Unconfirmed,

    /// <summary>Retour enregistré : écran de fin.</summary>
    Done,
}

/// <summary>Zone de saisie active pendant le choix.</summary>
public enum ReturnField
{
    /// <summary>Quantité de la ligne sélectionnée.</summary>
    Lines,

    /// <summary>Motif (obligatoire), parmi les motifs actifs.</summary>
    Reason,

    /// <summary>Note facultative.</summary>
    Note,
}

/// <summary>
/// Retours clients, sous-écran du module Vente. Le serveur décide de tout : ce qui reste
/// retournable, le remboursement (calculé par l'aperçu, jamais par le poste), la destination de
/// la marchandise. Ce modèle de vue garantit qu'un retour n'est jamais envoyé sans confirmation,
/// jamais deux fois, et que l'employé sait toujours s'il est enregistré avant de rendre l'argent.
/// </summary>
public sealed partial class ReturnsViewModel : ObservableObject
{
    public const string UnconfirmedTitle = "Retour non confirmé — ne pas rendre l'argent";
    public const string ReasonRequiredMessage = "Choisissez le motif du retour : il est obligatoire.";
    public const string NothingToReturnMessage = "Saisissez la quantité rendue sur au moins une ligne.";
    public const string FullyReturnedMessage = "Tout ce ticket a déjà été retourné : il n'y a plus rien à retourner.";

    private readonly IReturnsClient returns;
    private readonly ISalesClient sales;
    private readonly SalesRights rights;
    private readonly Func<ServerRefusedException, bool> requireIdentificationIfLost;
    private readonly ITicketPrinter printer;
    private readonly OperationIdKeeper returnOperationIds = new();

    /// <summary>Demande envoyée sans réponse : « Réessayer » la renvoie telle quelle, avec le même identifiant d'opération.</summary>
    private CreateReturnRequest? unconfirmedReturn;

    public ReturnsViewModel(
        IReturnsClient returns,
        ISalesClient sales,
        SalesRights rights,
        Func<ServerRefusedException, bool> requireIdentificationIfLost,
        ITicketPrinter printer,
        Func<DateOnly>? today = null)
    {
        this.returns = returns;
        this.sales = sales;
        this.rights = rights;
        this.requireIdentificationIfLost = requireIdentificationIfLost;
        this.printer = printer;
        History = new ReturnsHistoryViewModel(returns, requireIdentificationIfLost, today);
    }

    /// <summary>Historique des retours (Ctrl+H depuis cet écran).</summary>
    public ReturnsHistoryViewModel History { get; }

    /// <summary>Ouvre un ticket dans l'historique des ventes (pour l'annuler, répondre au TPE…). Fourni par le module.</summary>
    public Func<string, Task>? OpenSaleInHistory { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearch), nameof(IsChoosing), nameof(IsStepOpen), nameof(IsLinesZone), nameof(IsReasonZone),
        nameof(IsNoteZone))]
    private ReturnStep step;

    public bool IsSearch => Step == ReturnStep.Search;

    public bool IsChoosing => Step == ReturnStep.Choose;

    /// <summary>Une étape s'affiche par-dessus l'écran (confirmation, non confirmé, fin).</summary>
    public bool IsStepOpen => Step is ReturnStep.Confirm or ReturnStep.Unconfirmed or ReturnStep.Done;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinesZone), nameof(IsReasonZone), nameof(IsNoteZone))]
    private ReturnField field;

    /// <summary>Zone active, mise en évidence pendant le choix seulement.</summary>
    public bool IsLinesZone => IsChoosing && Field == ReturnField.Lines;

    public bool IsReasonZone => IsChoosing && Field == ReturnField.Reason;

    public bool IsNoteZone => IsChoosing && Field == ReturnField.Note;

    /// <summary>Vrai quand l'historique des retours est affiché à la place de la saisie.</summary>
    [ObservableProperty]
    private bool isHistoryShown;

    /// <summary>Numéro du ticket ou code-barres lu au lecteur.</summary>
    [ObservableProperty]
    private string ticketQuery = string.Empty;

    [ObservableProperty]
    private string errorMessage = string.Empty;

    [ObservableProperty]
    private string infoMessage = string.Empty;

    /// <summary>Saisie refusée pendant le choix ou refus affiché sur une étape.</summary>
    [ObservableProperty]
    private string stepError = string.Empty;

    /// <summary>Vrai pendant une lecture (ticket, motifs, aperçu).</summary>
    [ObservableProperty]
    private bool isBusy;

    /// <summary>Vrai pendant l'envoi du retour : rien d'autre ne part.</summary>
    [ObservableProperty]
    private bool isSending;

    // ---------- Ticket ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTicket), nameof(TicketTitle), nameof(TicketInfo), nameof(TicketTotalText),
        nameof(TicketPaymentText), nameof(RefundedText), nameof(HasRefunded), nameof(IsBlocked), nameof(BlockedMessage),
        nameof(BlockedHint), nameof(HasBlockedHint), nameof(CanOpenSaleInHistory))]
    private ReturnableSale? sale;

    public ObservableCollection<ReturnLineViewModel> Lines { get; } = [];

    [ObservableProperty]
    private ReturnLineViewModel? selectedLine;

    public bool HasTicket => Sale is not null;

    public string TicketTitle => Sale is { } ticket ? $"Ticket {ticket.TicketNumber}" : string.Empty;

    public string TicketInfo => Sale is { } ticket
        ? $"{SaleFormat.DateTime(ticket.SoldAt)} — code-barres {ticket.TicketBarcode}"
          + (ticket.CustomerName is { } customer ? $" — client : {customer}" : string.Empty)
        : string.Empty;

    public string TicketTotalText => Sale is { } ticket ? Money.ToDisplay(ticket.Total) : string.Empty;

    /// <summary>Moyen de paiement du ticket ; pour un impayé, l'état de la créance.</summary>
    public string TicketPaymentText => Sale switch
    {
        null => string.Empty,
        { PaymentMode: SalePaymentModes.Unpaid, RefundMethod: { } method } => $"Impayé réglé ({SaleFormat.PaymentMode(method)})",
        { PaymentMode: SalePaymentModes.Unpaid } => "IMPAYÉ — créance ouverte",
        { } ticket => SaleFormat.PaymentMode(ticket.PaymentMode),
    };

    public bool HasRefunded => Sale?.RefundedAmount > 0;

    public string RefundedText => Sale is { RefundedAmount: > 0 } ticket
        ? $"Déjà remboursé sur ce ticket : {Money.ToDisplay(ticket.RefundedAmount)}"
        : string.Empty;

    /// <summary>Le ticket ne peut pas (ou plus) être retourné.</summary>
    public bool IsBlocked => Sale is { CanReturn: false };

    /// <summary>La raison donnée par le serveur, telle quelle.</summary>
    public string BlockedMessage => Sale switch
    {
        { CanReturn: false, BlockedMessage: { Length: > 0 } message } => message,
        { CanReturn: false } => FullyReturnedMessage,
        _ => string.Empty,
    };

    /// <summary>Ce que l'employé peut faire à la place, selon la raison du blocage et ses droits.</summary>
    public string BlockedHint
    {
        get
        {
            if (Sale is not { CanReturn: false } ticket)
            {
                return string.Empty;
            }

            var history = $"ouvrez le ticket dans l'historique des ventes ({SalesShortcuts.OpenSaleInHistoryLabel})";
            return ticket.BlockedCode switch
            {
                ReturnErrorCodes.ReceivableOpen when rights is { CanViewHistory: true, CanCancel: true } =>
                    $"Pour annuler la vente : {history}, puis Ctrl+Suppr. Pour la régler : module Caisse, Impayés.",
                ReturnErrorCodes.ReceivableOpen => "Pour la régler : module Caisse, Impayés. Pour l'annuler : un employé habilité, depuis l'historique des ventes.",
                ReturnErrorCodes.SaleNotReturnable when ticket.Status == SaleStatuses.CardPending && rights is { CanViewHistory: true, CanCollect: true } =>
                    $"Pour répondre au TPE : {history}, puis Ctrl+T.",
                _ => string.Empty,
            };
        }
    }

    public bool HasBlockedHint => BlockedHint.Length > 0;

    public bool CanOpenSaleInHistory => HasTicket && rights.CanViewHistory && OpenSaleInHistory is not null;

    // ---------- Motif et note ----------

    /// <summary>Motifs actifs, dans l'ordre du serveur.</summary>
    public ObservableCollection<ReasonChoiceViewModel> Reasons { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReason), nameof(DestinationText), nameof(DestinationHint), nameof(IsNotRestocked))]
    private ReasonChoiceViewModel? selectedReason;

    public bool HasReason => SelectedReason is not null;

    /// <summary>Où va la marchandise selon le motif choisi.</summary>
    public string DestinationText => SelectedReason is { } reason
        ? SaleFormat.StockDestination(reason.Reason.StockDestination)
        : "Choisissez d'abord le motif";

    public string DestinationHint => SelectedReason is { } reason ? SaleFormat.StockDestinationHint(reason.Reason.StockDestination) : string.Empty;

    /// <summary>Motif sans réintégration : à signaler nettement.</summary>
    public bool IsNotRestocked => SelectedReason?.Reason.StockDestination == ReturnStockDestinations.None;

    [ObservableProperty]
    private string note = string.Empty;

    private bool reasonsLoaded;

    // ---------- Aperçu ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewRefundText), nameof(PreviewMethodText), nameof(PreviewDestinationText),
        nameof(IsPreviewCash), nameof(IsPreviewCard), nameof(PreviewTitle))]
    private ReturnPreview? preview;

    /// <summary>Lignes de l'aperçu : désignation, quantité, remboursement calculé par le serveur.</summary>
    public ObservableCollection<string> PreviewLines { get; } = [];

    public string PreviewTitle => Preview is { } shown ? $"Retour sur le ticket {shown.TicketNumber} — {shown.ReasonLabel}" : string.Empty;

    /// <summary>Montant remboursé, tel que le serveur l'a calculé.</summary>
    public string PreviewRefundText => Money.ToDisplay(Preview?.RefundAmount ?? 0m);

    public string PreviewMethodText => SaleFormat.RefundMethod(Preview?.RefundMethod);

    public string PreviewDestinationText => Preview is { } shown ? SaleFormat.StockDestination(shown.StockDestination) : string.Empty;

    public bool IsPreviewCash => Preview?.RefundMethod == PaymentMethods.Cash;

    public bool IsPreviewCard => Preview?.RefundMethod == PaymentMethods.Card;

    /// <summary>Le serveur a refusé le montant affiché : un nouvel aperçu est montré, à reconfirmer.</summary>
    [ObservableProperty]
    private string refundChangedMessage = string.Empty;

    // ---------- Fin ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DoneNumberText), nameof(DoneRefundText), nameof(DoneDestinationText), nameof(IsDoneCash),
        nameof(IsDoneCard), nameof(IsDoneWithoutRefund))]
    private ReturnDetail? completedReturn;

    public string DoneNumberText => CompletedReturn is { } done ? $"Retour {done.ReturnNumber} — ticket {done.TicketNumber}" : string.Empty;

    public string DoneRefundText => Money.ToDisplay(CompletedReturn?.RefundAmount ?? 0m);

    public string DoneDestinationText => CompletedReturn is { } done ? SaleFormat.StockDestination(done.StockDestination) : string.Empty;

    public bool IsDoneCash => CompletedReturn?.RefundMethod == PaymentMethods.Cash;

    public bool IsDoneCard => CompletedReturn?.RefundMethod == PaymentMethods.Card;

    public bool IsDoneWithoutRefund => CompletedReturn is { RefundMethod: null };

    // ---------- Arrivée sur l'écran ----------

    /// <summary>
    /// À chaque arrivée : les motifs actifs sont relus (un administrateur a pu en changer), sauf
    /// pendant un retour en cours, qui n'est pas dérangé.
    /// </summary>
    public async Task ActivateAsync()
    {
        if (!reasonsLoaded || Step == ReturnStep.Search)
        {
            await LoadReasonsAsync();
        }
    }

    private async Task LoadReasonsAsync()
    {
        IReadOnlyList<ReturnReason> active = [];
        if (!await ReadAsync(async () => active = await returns.ListReasonsAsync(false, CancellationToken.None)))
        {
            return;
        }

        reasonsLoaded = true;
        var keptId = SelectedReason?.Reason.Id;
        Reasons.Clear();
        foreach (var (reason, index) in active.Where(reason => reason.IsActive).Select((reason, index) => (reason, index)))
        {
            Reasons.Add(new ReasonChoiceViewModel(reason, index + 1));
        }

        SelectedReason = Reasons.FirstOrDefault(choice => choice.Reason.Id == keptId);
        if (Reasons.Count == 0)
        {
            ErrorMessage = "Aucun motif de retour actif : un administrateur doit en créer (module Administration).";
        }
    }

    // ---------- Recherche du ticket ----------

    /// <summary>Ouvre directement un ticket (depuis le détail d'un ticket de l'historique des ventes).</summary>
    public async Task OpenTicketAsync(string ticketNumber)
    {
        if (IsSending || Step is ReturnStep.Confirm or ReturnStep.Unconfirmed)
        {
            return;
        }

        IsHistoryShown = false;
        ClearTicket();
        Step = ReturnStep.Search;
        TicketQuery = ticketNumber;
        await FindTicketAsync();
    }

    /// <summary>Entrée (ou fin de scan) dans la recherche : lit ce qui reste retournable sur le ticket.</summary>
    [RelayCommand]
    public async Task FindTicketAsync()
    {
        if (Step != ReturnStep.Search || IsBusy)
        {
            return;
        }

        ClearMessages();
        var code = TicketQuery.Trim();
        if (code.Length == 0)
        {
            ErrorMessage = "Scannez le code-barres du ticket ou saisissez son numéro.";
            return;
        }

        if (!reasonsLoaded)
        {
            await LoadReasonsAsync();
        }

        ReturnableSale? found = null;
        if (!await ReadAsync(async () => found = await returns.FindReturnableAsync(code, CancellationToken.None)))
        {
            return;
        }

        if (found is null)
        {
            ClearTicket();
            ErrorMessage = $"Ticket introuvable : « {code} ». Vérifiez le numéro ou scannez le code-barres imprimé.";
            return;
        }

        TicketQuery = string.Empty;
        ShowTicket(found, keepQuantities: false);
    }

    /// <summary>Affiche le ticket. Retournable : le choix commence sur la première ligne ; sinon la raison reste affichée.</summary>
    private void ShowTicket(ReturnableSale found, bool keepQuantities)
    {
        var typed = keepQuantities ? Lines.ToDictionary(line => line.ProductId, line => line.QuantityText) : [];
        var units = Lines.Where(line => line.SoldByUnit is not null).ToDictionary(line => line.ProductId, line => line.SoldByUnit);
        Lines.Clear();
        foreach (var line in found.Lines)
        {
            var row = new ReturnLineViewModel(line) { SoldByUnit = units.GetValueOrDefault(line.ProductId) };
            if (row.IsReturnable && typed.TryGetValue(line.ProductId, out var text))
            {
                row.QuantityText = text;
            }

            Lines.Add(row);
        }

        Sale = found;
        OnPropertyChanged(nameof(CanOpenSaleInHistory));
        SelectedLine = Lines.FirstOrDefault(line => line.IsReturnable) ?? Lines.FirstOrDefault();
        StepError = string.Empty;
        if (found.CanReturn)
        {
            Field = ReturnField.Lines;
            Step = ReturnStep.Choose;
        }
        else
        {
            Step = ReturnStep.Search;
        }
    }

    private void ClearTicket()
    {
        Sale = null;
        Lines.Clear();
        SelectedLine = null;
        SelectedReason = null;
        Note = string.Empty;
        Preview = null;
        PreviewLines.Clear();
        RefundChangedMessage = string.Empty;
        StepError = string.Empty;
    }

    private void ClearMessages()
    {
        ErrorMessage = string.Empty;
        InfoMessage = string.Empty;
    }

    /// <summary>Ouvre le ticket affiché dans l'historique des ventes (annulation, réponse du TPE…).</summary>
    [RelayCommand]
    public async Task OpenSaleInHistoryAsync()
    {
        if (!CanOpenSaleInHistory || IsSending || Step is ReturnStep.Confirm or ReturnStep.Unconfirmed || Sale is not { } ticket)
        {
            return;
        }

        await OpenSaleInHistory!(ticket.TicketNumber);
    }

    // ---------- Choix : quantités ----------

    public void MoveLine(int delta)
    {
        var editable = Lines.Where(line => line.IsReturnable).ToList();
        if (Step != ReturnStep.Choose || editable.Count == 0)
        {
            return;
        }

        var current = SelectedLine is null ? -1 : editable.IndexOf(SelectedLine);
        SelectedLine = editable[Math.Clamp(current + delta, 0, editable.Count - 1)];
        Field = ReturnField.Lines;
    }

    /// <summary>
    /// Entrée sur une quantité : elle est contrôlée, puis le curseur passe à la ligne retournable
    /// suivante, ou au motif après la dernière.
    /// </summary>
    [RelayCommand]
    public async Task NextFromLineAsync()
    {
        if (Step != ReturnStep.Choose || IsBusy || SelectedLine is not { } line)
        {
            return;
        }

        if (!await CheckLineAsync(line))
        {
            return;
        }

        StepError = string.Empty;
        var editable = Lines.Where(row => row.IsReturnable).ToList();
        var index = editable.IndexOf(line);
        if (index >= 0 && index < editable.Count - 1)
        {
            SelectedLine = editable[index + 1];
        }
        else
        {
            Field = ReturnField.Reason;
        }
    }

    /// <summary>
    /// Contrôle la quantité d'une ligne : nombre valide, jamais plus que le retournable, jamais de
    /// fraction pour un produit vendu à l'unité. Le serveur refait tous ces contrôles.
    /// </summary>
    private async Task<bool> CheckLineAsync(ReturnLineViewModel line)
    {
        if (!line.TryReadQuantity(out var quantity))
        {
            return Reject(line, $"Quantité invalide pour « {line.Name} » : un nombre positif (virgule ou point), ou 0.");
        }

        var returnable = line.Line.ReturnableQuantity;
        if (quantity > returnable)
        {
            return Reject(
                line,
                $"« {line.Name} » : {SaleFormat.Quantity(quantity)} demandé, {SaleFormat.Quantity(returnable)} encore retournable "
                + $"({line.SoldText} vendu, {line.ReturnedText} déjà retourné).");
        }

        if (quantity == decimal.Truncate(quantity))
        {
            return true;
        }

        if (line.SoldByUnit is null)
        {
            IsBusy = true;
            try
            {
                line.SoldByUnit = await sales.IsSoldByUnitAsync(line.ProductId, CancellationToken.None);
            }
            catch (ServerRefusedException refused) when (refused.Code == ApiErrorCodes.PermissionDenied)
            {
                // Sans le droit de consulter les produits, la fiche n'est pas lisible : le serveur tranchera à l'aperçu.
            }
            catch (ServerRefusedException refused)
            {
                requireIdentificationIfLost(refused);
                return Reject(line, refused.Message);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                return Reject(line, "Serveur injoignable : impossible de vérifier si ce produit se vend au détail.");
            }
            finally
            {
                IsBusy = false;
            }
        }

        return line.SoldByUnit == true
            ? Reject(line, $"« {line.Name} » se vend à l'unité : la quantité retournée doit être un nombre entier.")
            : true;
    }

    private bool Reject(ReturnLineViewModel line, string message)
    {
        SelectedLine = line;
        Field = ReturnField.Lines;
        line.HasError = true;
        StepError = message;
        return false;
    }

    // ---------- Choix : motif et note ----------

    /// <summary>Tab : zone suivante (quantités → motif → note → quantités).</summary>
    public void NextField()
    {
        if (Step == ReturnStep.Choose)
        {
            Field = Field switch
            {
                ReturnField.Lines => ReturnField.Reason,
                ReturnField.Reason => ReturnField.Note,
                _ => ReturnField.Lines,
            };
        }
    }

    public void MoveReason(int delta)
    {
        if (Step != ReturnStep.Choose || Reasons.Count == 0)
        {
            return;
        }

        var current = SelectedReason is null ? (delta > 0 ? -1 : Reasons.Count) : Reasons.IndexOf(SelectedReason);
        SelectedReason = Reasons[Math.Clamp(current + delta, 0, Reasons.Count - 1)];
        StepError = string.Empty;
    }

    /// <summary>Chiffre tapé sur le motif : choisit le motif de ce numéro.</summary>
    public void ChooseReasonNumber(int number)
    {
        if (Step == ReturnStep.Choose && number >= 1 && number <= Reasons.Count)
        {
            SelectedReason = Reasons[number - 1];
            StepError = string.Empty;
        }
    }

    /// <summary>Entrée sur le motif : il est obligatoire ; le curseur passe à la note.</summary>
    [RelayCommand]
    public void ValidateReason()
    {
        if (Step != ReturnStep.Choose)
        {
            return;
        }

        if (SelectedReason is null)
        {
            StepError = ReasonRequiredMessage;
            Field = ReturnField.Reason;
            return;
        }

        StepError = string.Empty;
        Field = ReturnField.Note;
    }

    /// <summary>Échap pendant le choix : revient d'une zone ; depuis les quantités, abandonne le ticket (rien n'est envoyé).</summary>
    [RelayCommand]
    public void Back()
    {
        if (IsSending || IsBusy)
        {
            return;
        }

        switch (Step)
        {
            case ReturnStep.Choose when Field == ReturnField.Note:
                Field = ReturnField.Reason;
                break;
            case ReturnStep.Choose when Field == ReturnField.Reason:
                Field = ReturnField.Lines;
                break;
            case ReturnStep.Choose:
            case ReturnStep.Search when HasTicket:
                ClearTicket();
                ClearMessages();
                Step = ReturnStep.Search;
                break;
            case ReturnStep.Search:
                TicketQuery = string.Empty;
                ClearMessages();
                break;
            case ReturnStep.Confirm:
                // Retour au choix : rien n'est envoyé.
                RefundChangedMessage = string.Empty;
                StepError = string.Empty;
                Field = ReturnField.Note;
                Step = ReturnStep.Choose;
                break;
            case ReturnStep.Done:
                StartNextReturn();
                break;
        }
    }

    // ---------- Aperçu ----------

    /// <summary>
    /// Entrée sur la note : tout est contrôlé, puis l'aperçu est demandé au serveur. Le montant
    /// remboursé affiché est celui que le serveur calcule. Rien n'est enregistré.
    /// </summary>
    [RelayCommand]
    public async Task RequestPreviewAsync()
    {
        if (Step != ReturnStep.Choose || IsBusy || Sale is not { } ticket)
        {
            return;
        }

        ClearMessages();
        var lines = await ReadLinesAsync();
        if (lines is null)
        {
            return;
        }

        if (SelectedReason is not { } reason)
        {
            StepError = ReasonRequiredMessage;
            Field = ReturnField.Reason;
            return;
        }

        RefundChangedMessage = string.Empty;
        await ShowPreviewAsync(new PreviewReturnRequest(ticket.SaleId, reason.Reason.Id, lines));
    }

    /// <summary>Les lignes rendues (quantité positive), contrôlées ; <c>null</c> si une saisie est refusée.</summary>
    private async Task<IReadOnlyList<ReturnLineRequest>?> ReadLinesAsync()
    {
        var requested = new List<ReturnLineRequest>();
        foreach (var line in Lines.Where(line => line.IsReturnable))
        {
            if (!await CheckLineAsync(line))
            {
                return null;
            }

            line.TryReadQuantity(out var quantity);
            if (quantity > 0)
            {
                requested.Add(new ReturnLineRequest(line.ProductId, quantity));
            }
        }

        if (requested.Count == 0)
        {
            StepError = NothingToReturnMessage;
            Field = ReturnField.Lines;
            SelectedLine = Lines.FirstOrDefault(line => line.IsReturnable);
            return null;
        }

        return requested;
    }

    /// <summary>Demande l'aperçu ; succès : confirmation affichée. Refus : affiché tel quel, le choix reste ouvert.</summary>
    private async Task<bool> ShowPreviewAsync(PreviewReturnRequest request)
    {
        IsBusy = true;
        StepError = string.Empty;
        try
        {
            var shown = await returns.PreviewAsync(request, CancellationToken.None);
            Preview = shown;
            PreviewLines.Clear();
            foreach (var line in shown.Lines)
            {
                PreviewLines.Add($"{line.ProductName} — {SaleFormat.Quantity(line.Quantity)} rendu — {Money.ToDisplay(line.RefundAmount)}");
            }

            Step = ReturnStep.Confirm;
            return true;
        }
        catch (ServerRefusedException refused)
        {
            BackToChoice(refused.Message);
            requireIdentificationIfLost(refused);
            return false;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            BackToChoice(ModuleViewModel.ServerUnreachableMessage);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BackToChoice(string message)
    {
        Preview = null;
        PreviewLines.Clear();
        RefundChangedMessage = string.Empty;
        StepError = message;
        Field = ReturnField.Lines;
        Step = ReturnStep.Choose;
    }

    // ---------- Confirmation et envoi ----------

    /// <summary>
    /// Entrée sur l'aperçu : envoie le retour avec le remboursement affiché (<c>ExpectedRefund</c>).
    /// Verrouillé pendant l'envoi : un double appui ne produit qu'un seul envoi.
    /// </summary>
    [RelayCommand]
    public Task ConfirmAsync()
    {
        if (Step != ReturnStep.Confirm || IsSending || IsBusy || Preview is not { } shown || Sale is not { } ticket
            || SelectedReason is not { } reason)
        {
            return Task.CompletedTask;
        }

        var lines = shown.Lines.Select(line => new ReturnLineRequest(line.ProductId, line.Quantity)).ToList();
        var note = Note.Trim().Length == 0 ? null : Note.Trim();
        var signature = string.Create(
            CultureInfo.InvariantCulture,
            $"{ticket.SaleId}|{reason.Reason.Id}|{note}|{shown.RefundAmount}|{string.Join(';', lines.Select(line => FormattableString.Invariant($"{line.ProductId}:{line.Quantity}")))}");
        var request = new CreateReturnRequest(
            returnOperationIds.For(signature), ticket.SaleId, reason.Reason.Id, lines, note, shown.RefundAmount);
        return SendAsync(request);
    }

    /// <summary>« Réessayer » après un retour non confirmé : la même demande, le même identifiant d'opération.</summary>
    [RelayCommand]
    public Task RetryAsync() =>
        Step == ReturnStep.Unconfirmed && unconfirmedReturn is { } request && !IsSending ? SendAsync(request) : Task.CompletedTask;

    private async Task SendAsync(CreateReturnRequest request)
    {
        IsSending = true;
        StepError = string.Empty;
        try
        {
            var recorded = await returns.CreateReturnAsync(request, CancellationToken.None);
            returnOperationIds.Forget();
            unconfirmedReturn = null;
            Finish(recorded);
        }
        catch (ServerRefusedException refused) when (refused.Code != SalesViewModel.UnreadableAnswerCode)
        {
            // Le serveur a répondu non : rien n'est enregistré, la tentative est close.
            returnOperationIds.Forget();
            unconfirmedReturn = null;
            await OnRefusedAsync(refused, request);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or ServerRefusedException)
        {
            // Aucune réponse lisible : le retour est peut-être enregistré. On ne rend pas l'argent avant de savoir.
            unconfirmedReturn = request;
            Step = ReturnStep.Unconfirmed;
        }
        finally
        {
            IsSending = false;
        }
    }

    private async Task OnRefusedAsync(ServerRefusedException refused, CreateReturnRequest request)
    {
        if (requireIdentificationIfLost(refused))
        {
            BackToChoice(refused.Message);
            return;
        }

        if (refused.Code == ReturnErrorCodes.RefundMismatch)
        {
            // Le montant a changé : nouvel aperçu, à reconfirmer. Rien n'est enregistré.
            var reread = await ShowPreviewAsync(new PreviewReturnRequest(request.SaleId, request.ReasonId, request.Lines));
            if (reread)
            {
                RefundChangedMessage = refused.Message;
            }

            return;
        }

        BackToChoice(refused.Message);
        ErrorMessage = refused.Message;
        StepError = string.Empty;

        // Ce que le serveur sait a peut-être changé (autre retour, créance, motif désactivé) : tout est relu,
        // les quantités saisies sont gardées et le refus reste affiché.
        if (refused.Code == ReturnErrorCodes.InvalidReason)
        {
            reasonsLoaded = false;
            await LoadReasonsAsync();
        }

        if (Sale is { } ticket)
        {
            try
            {
                if (await returns.FindReturnableAsync(ticket.TicketNumber, CancellationToken.None) is { } fresh)
                {
                    ShowTicket(fresh, keepQuantities: true);
                }
            }
            catch (Exception exception) when (exception is ServerRefusedException or HttpRequestException or TaskCanceledException)
            {
                // Le refus d'origine est ce qui compte : il reste affiché.
            }
        }

        ErrorMessage = refused.Message;
    }

    private void Finish(ReturnDetail recorded)
    {
        ClearTicket();
        ClearMessages();
        CompletedReturn = recorded;
        Step = ReturnStep.Done;

        // Point d'accroche de l'impression : rien n'est imprimé ni simulé pour l'instant.
        printer.PrintReturn(recorded);
    }

    /// <summary>Un seul appui après l'écran de fin : retour à la recherche de ticket.</summary>
    [RelayCommand]
    public void StartNextReturn()
    {
        if (Step != ReturnStep.Done)
        {
            return;
        }

        CompletedReturn = null;
        TicketQuery = string.Empty;
        Step = ReturnStep.Search;
    }

    // ---------- Historique ----------

    /// <summary>Ctrl+H : historique des retours. Sans effet pendant un retour en cours de confirmation.</summary>
    [RelayCommand]
    public async Task ShowHistoryAsync()
    {
        if (IsStepOpen || IsSending)
        {
            return;
        }

        IsHistoryShown = true;
        await History.ActivateAsync();
    }

    [RelayCommand]
    public void HideHistory() => IsHistoryShown = false;

    /// <summary>Lecture au serveur : un refus ou une panne s'affiche, l'écran reste utilisable.</summary>
    private async Task<bool> ReadAsync(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action();
            return true;
        }
        catch (ServerRefusedException refused)
        {
            ErrorMessage = refused.Message;
            requireIdentificationIfLost(refused);
            return false;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            ErrorMessage = ModuleViewModel.ServerUnreachableMessage;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>Motif proposé à l'employé : son numéro (chiffre au clavier), son libellé et sa destination.</summary>
public sealed class ReasonChoiceViewModel(ReturnReason reason, int number)
{
    public ReturnReason Reason { get; } = reason;

    public int Number { get; } = number;

    public string NumberText => Number.ToString(CultureInfo.InvariantCulture);

    public string Label => Reason.Label;

    public string DestinationText => SaleFormat.StockDestination(Reason.StockDestination);

    public bool IsNotRestocked => Reason.StockDestination == ReturnStockDestinations.None;

    public override string ToString() => Label;
}
