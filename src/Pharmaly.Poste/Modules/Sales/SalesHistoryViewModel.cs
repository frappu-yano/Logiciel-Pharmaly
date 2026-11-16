using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.Sales;

/// <summary>Choix du filtre « statut » de l'historique.</summary>
/// <param name="Code">Un état de <see cref="SaleStatuses"/>, ou <c>null</c> pour tous.</param>
public sealed record SaleStatusChoice(string? Code, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Ligne de l'historique des ventes.</summary>
public sealed class SaleRowViewModel(SaleSummary sale)
{
    public SaleSummary Sale { get; } = sale;

    public Guid Id => Sale.Id;

    public string TicketNumber => Sale.TicketNumber;

    public string DateText => SaleFormat.DateTime(Sale.CreatedAt);

    public string StatusText => SaleFormat.Status(Sale.Status);

    /// <summary>Mode de paiement ; une vente impayée dont la créance est encore ouverte est marquée IMPAYÉ.</summary>
    public string ModeText => Sale.IsUnpaid ? "IMPAYÉ" : SaleFormat.PaymentMode(Sale.PaymentMode);

    public string TotalText => Money.ToDisplay(Sale.Total);

    public string EmployeeName => Sale.EmployeeName;

    public string CustomerName => Sale.CustomerName ?? string.Empty;

    public bool IsCancelled => Sale.Status == SaleStatuses.Cancelled;

    public bool IsCardPending => Sale.Status == SaleStatuses.CardPending;

    public static SaleRowViewModel From(SaleDetail detail) => new(new SaleSummary(
        detail.Id, detail.TicketNumber, detail.TicketBarcode, detail.CreatedAt, detail.Status, detail.PaymentMode, detail.IsUnpaid,
        detail.Total, detail.EmployeeId, detail.EmployeeName, detail.CustomerName));
}

/// <summary>Ligne d'un ticket affiché en détail.</summary>
public sealed class SaleDetailLineViewModel(SaleLineDetail line)
{
    public string Name => line.ProductName;

    public string QuantityText => SaleFormat.Quantity(line.Quantity);

    public string UnitPriceText => Money.ToDisplay(line.UnitPrice);

    public string LineTotalText => Money.ToDisplay(line.LineTotal);
}

/// <summary>Détail d'un ticket, prêt à afficher.</summary>
public sealed class SaleDetailViewModel(SaleDetail sale, SalesRights rights)
{
    public SaleDetail Sale { get; } = sale;

    public string Title => $"Ticket {Sale.TicketNumber}";

    public string Subtitle =>
        $"{SaleFormat.DateTime(Sale.CreatedAt)} — {Sale.EmployeeName} — {Sale.RegisterName} — code-barres {Sale.TicketBarcode}";

    public string StatusText => SaleFormat.Status(Sale.Status);

    public bool IsCancelled => Sale.Status == SaleStatuses.Cancelled;

    public bool IsCardPending => Sale.Status == SaleStatuses.CardPending;

    public IReadOnlyList<SaleDetailLineViewModel> Lines { get; } = sale.Lines.Select(line => new SaleDetailLineViewModel(line)).ToList();

    public string SubtotalText => Money.ToDisplay(Sale.Subtotal);

    public bool HasDiscount => Sale.DiscountAmount != 0;

    public string DiscountTitle => Sale is { DiscountKind: DiscountKinds.Percent, DiscountValue: { } percent }
        ? $"Remise {SaleFormat.Percent(percent)}"
        : "Remise";

    public string DiscountText => $"− {Money.ToDisplay(Sale.DiscountAmount)}";

    public string TotalText => Money.ToDisplay(Sale.Total);

    /// <summary>Ce qui a été payé, ou ce qui reste dû.</summary>
    public string PaymentText
    {
        get
        {
            if (Sale.PaymentMode == SalePaymentModes.Unpaid)
            {
                var customer = Sale.CustomerName ?? "client inconnu";
                return Sale.Receivable?.Status switch
                {
                    ReceivableStatuses.Settled when Sale.Receivable.SettledAt is { } settledAt =>
                        $"Vente impayée — {customer} — réglée le {SaleFormat.DateTime(settledAt)}",
                    ReceivableStatuses.Settled => $"Vente impayée — {customer} — réglée",
                    ReceivableStatuses.Cancelled => $"Vente impayée — {customer} — créance annulée",
                    _ => $"IMPAYÉ — {customer} — {Money.ToDisplay(Sale.Total)} dus",
                };
            }

            if (Sale.Payment is not { } payment)
            {
                return SaleFormat.PaymentMode(Sale.PaymentMode);
            }

            var state = payment.Status switch
            {
                PaymentStatuses.Pending => "en attente de la réponse du terminal",
                PaymentStatuses.Failed => "paiement refusé",
                PaymentStatuses.Refunded => "remboursé",
                _ => "encaissé",
            };
            return payment is { Method: PaymentMethods.Cash, AmountReceived: { } received, ChangeGiven: { } change }
                ? $"Espèces — {state} — reçu {Money.ToDisplay(received)}, rendu {Money.ToDisplay(change)}"
                : $"{SaleFormat.PaymentMode(payment.Method)} — {state}";
        }
    }

    public bool HasCancellation => Sale.Cancellation is not null;

    public string CancellationText
    {
        get
        {
            if (Sale.Cancellation is not { } cancellation)
            {
                return string.Empty;
            }

            var refund = cancellation.RefundMethod switch
            {
                PaymentMethods.Cash => $" Remboursement en espèces : {Money.ToDisplay(cancellation.RefundAmount)}.",
                PaymentMethods.Card => $" Remboursement par carte : {Money.ToDisplay(cancellation.RefundAmount)}.",
                _ => string.Empty,
            };
            return $"Annulée le {SaleFormat.DateTime(cancellation.OccurredAt)} par {cancellation.EmployeeName} — {cancellation.Reason}.{refund}";
        }
    }

    /// <summary>L'annulation n'est proposée que pour une vente validée ; le serveur applique les autres règles (journée clôturée…).</summary>
    public bool CanCancel => rights.CanCancel && Sale.Status == SaleStatuses.Completed;

    /// <summary>Une vente restée « en attente TPE » peut recevoir sa réponse ici (D23).</summary>
    public bool CanAnswerCard => rights.CanCollect && Sale.Status == SaleStatuses.CardPending;

    /// <summary>
    /// « Retour sur ce ticket » : proposé pour une vente validée qui n'est pas un impayé encore ouvert (D32) ;
    /// le serveur dit ce qui reste retournable.
    /// </summary>
    public bool CanReturn => rights.CanReturn && Sale.Status == SaleStatuses.Completed && !Sale.IsUnpaid;
}

/// <summary>Étape affichée par-dessus l'historique.</summary>
public enum HistoryStep
{
    None,

    /// <summary>Saisie du motif d'annulation (obligatoire).</summary>
    CancelReason,

    /// <summary>Confirmation forte de l'annulation.</summary>
    CancelConfirm,

    /// <summary>Réponse du TPE pour une vente restée en attente.</summary>
    CardAnswer,
}

/// <summary>
/// Historique des ventes du site, détail d'un ticket, annulation et réponse aux paiements par
/// carte restés en attente. Les refus du serveur sont affichés tels quels.
/// </summary>
public sealed partial class SalesHistoryViewModel : ObservableObject
{
    public const string ReasonRequiredMessage = "Le motif de l'annulation est obligatoire.";

    private static readonly string[] DateFormats = ["d/M/yyyy", "d/M/yy", "d/M", "d-M-yyyy", "d.M.yyyy"];

    private readonly ISalesClient sales;
    private readonly SalesRights rights;
    private readonly Func<ServerRefusedException, bool> requireIdentificationIfLost;
    private readonly Func<DateOnly> today;
    private readonly OperationIdKeeper cancelOperationIds = new();
    private readonly OperationIdKeeper cardOperationIds = new();
    private bool datesInitialised;
    private int detailVersion;

    public SalesHistoryViewModel(
        ISalesClient sales,
        SalesRights rights,
        Func<ServerRefusedException, bool> requireIdentificationIfLost,
        Func<DateOnly>? today = null)
    {
        this.sales = sales;
        this.rights = rights;
        this.requireIdentificationIfLost = requireIdentificationIfLost;
        this.today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
        selectedStatus = StatusChoices[0];
    }

    public IReadOnlyList<SaleStatusChoice> StatusChoices { get; } =
    [
        new(null, "Tous les statuts"),
        new(SaleStatuses.Completed, "Validées"),
        new(SaleStatuses.CardPending, "En attente TPE"),
        new(SaleStatuses.Cancelled, "Annulées"),
    ];

    public ObservableCollection<SaleRowViewModel> Sales { get; } = [];

    public string CardAcceptedKeyLabel => SalesShortcuts.CardAcceptedLabel;

    public string CardRefusedKeyLabel => SalesShortcuts.CardRefusedLabel;

    public string CancelConfirmKeyLabel => SalesShortcuts.CancelSaleConfirmLabel;

    /// <summary>Premier jour affiché, « jj/mm/aaaa » ; vide pour ne pas borner.</summary>
    [ObservableProperty]
    private string fromText = string.Empty;

    [ObservableProperty]
    private string toText = string.Empty;

    [ObservableProperty]
    private SaleStatusChoice selectedStatus;

    /// <summary>Numéro de ticket (ou une partie), ou code-barres du ticket lu au lecteur.</summary>
    [ObservableProperty]
    private string ticketQuery = string.Empty;

    [ObservableProperty]
    private SaleRowViewModel? selectedSale;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail), nameof(CanCancelSelected), nameof(CanAnswerCardSelected), nameof(CanReturnSelected))]
    private SaleDetailViewModel? detail;

    [ObservableProperty]
    private string errorMessage = string.Empty;

    [ObservableProperty]
    private string infoMessage = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStepOpen))]
    private HistoryStep step;

    [ObservableProperty]
    private string stepError = string.Empty;

    [ObservableProperty]
    private string cancelReason = string.Empty;

    [ObservableProperty]
    private string stepTitle = string.Empty;

    /// <summary>Ce que l'annulation va produire, affiché sur la confirmation.</summary>
    [ObservableProperty]
    private string cancelConsequence = string.Empty;

    public bool IsStepOpen => Step != HistoryStep.None;

    public bool HasDetail => Detail is not null;

    public bool CanCancelSelected => Detail?.CanCancel == true;

    public bool CanAnswerCardSelected => Detail?.CanAnswerCard == true;

    public bool CanReturnSelected => Detail?.CanReturn == true && OpenReturn is not null;

    public string ReturnsKeyLabel => SalesShortcuts.ReturnsLabel;

    /// <summary>Ouvre l'écran des retours sur un ticket. Fourni par le module.</summary>
    public Func<SaleDetail, Task>? OpenReturn { get; init; }

    /// <summary>À chaque arrivée sur l'historique : les ventes du jour.</summary>
    public Task ActivateAsync()
    {
        if (!datesInitialised)
        {
            datesInitialised = true;
            FromText = ToText = FormatDate(today());
        }

        return SearchAsync();
    }

    /// <summary>Changer de statut relance la recherche : pas besoin de valider.</summary>
    partial void OnSelectedStatusChanged(SaleStatusChoice value)
    {
        if (datesInitialised)
        {
            _ = SearchAsync();
        }
    }

    private static string FormatDate(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private bool TryParseDate(string text, out DateOnly? date)
    {
        date = null;
        var cleaned = text.Trim();
        if (cleaned.Length == 0)
        {
            return true;
        }

        if (!DateOnly.TryParseExact(cleaned, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        // « 16/11 » : l'année en cours.
        date = cleaned.Count(character => character is '/' or '-' or '.') == 1 ? new DateOnly(today().Year, parsed.Month, parsed.Day) : parsed;
        return true;
    }

    /// <summary>
    /// Entrée : applique les filtres. Un numéro exact ou un code-barres de ticket ouvre le
    /// ticket directement, quelles que soient les dates.
    /// </summary>
    [RelayCommand]
    public async Task SearchAsync()
    {
        if (IsBusy || IsStepOpen)
        {
            return;
        }

        ErrorMessage = string.Empty;
        InfoMessage = string.Empty;
        if (!TryParseDate(FromText, out var from) || !TryParseDate(ToText, out var to))
        {
            ErrorMessage = "Date invalide : saisissez jj/mm/aaaa, ou rien pour ne pas borner.";
            return;
        }

        var ticket = TicketQuery.Trim();
        await RunAsync(async () =>
        {
            if (ticket.Length > 0 && await sales.FindSaleByTicketAsync(ticket, CancellationToken.None) is { } exact)
            {
                ShowRows([SaleRowViewModel.From(exact)], exact);
                return;
            }

            var found = await sales.ListSalesAsync(
                new SalesHistoryQuery(from, to, SelectedStatus.Code, ticket.Length == 0 ? null : ticket), CancellationToken.None);
            ShowRows(found.Select(sale => new SaleRowViewModel(sale)).ToList(), null);
            if (found.Count == 0)
            {
                InfoMessage = "Aucun ticket ne correspond à ces filtres.";
            }
        });
    }

    private void ShowRows(IReadOnlyList<SaleRowViewModel> rows, SaleDetail? knownDetail)
    {
        detailVersion++;
        Sales.Clear();
        foreach (var row in rows)
        {
            Sales.Add(row);
        }

        Detail = knownDetail is null ? null : new SaleDetailViewModel(knownDetail, rights);
        SelectedSale = Sales.FirstOrDefault();
    }

    partial void OnSelectedSaleChanged(SaleRowViewModel? value)
    {
        if (value is null)
        {
            detailVersion++;
            Detail = null;
        }
        else if (Detail?.Sale.Id != value.Id)
        {
            _ = LoadDetailAsync(value.Id);
        }
    }

    public void MoveSelection(int delta)
    {
        if (Sales.Count == 0 || IsStepOpen)
        {
            return;
        }

        var current = SelectedSale is null ? -1 : Sales.IndexOf(SelectedSale);
        SelectedSale = Sales[Math.Clamp(current + delta, 0, Sales.Count - 1)];
    }

    /// <summary>Lit le détail du ticket sélectionné ; une sélection plus récente remplace la lecture en cours.</summary>
    public async Task LoadDetailAsync(Guid saleId)
    {
        var version = ++detailVersion;
        try
        {
            var read = await sales.GetSaleAsync(saleId, CancellationToken.None);
            if (version != detailVersion)
            {
                return;
            }

            Detail = read is null ? null : new SaleDetailViewModel(read, rights);
            if (read is null)
            {
                ErrorMessage = "Ticket introuvable.";
            }
        }
        catch (ServerRefusedException refused)
        {
            ErrorMessage = refused.Message;
            requireIdentificationIfLost(refused);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            ErrorMessage = ModuleViewModel.ServerUnreachableMessage;
        }
    }

    private async Task<bool> RunAsync(Func<Task> action)
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

    /// <summary>Remplace le ticket affiché (liste et détail) par son nouvel état.</summary>
    private void Replace(SaleDetail updated)
    {
        var index = Sales.ToList().FindIndex(row => row.Id == updated.Id);
        if (index >= 0)
        {
            var row = SaleRowViewModel.From(updated);
            Sales[index] = row;
            SelectedSale = row;
        }

        // En dernier : remplacer la ligne a pu relancer une lecture du détail, qui est ainsi ignorée.
        detailVersion++;
        Detail = new SaleDetailViewModel(updated, rights);
    }

    [RelayCommand]
    public void CloseStep()
    {
        if (IsBusy)
        {
            return;
        }

        if (Step == HistoryStep.CancelConfirm)
        {
            // Échap sur la confirmation : retour au motif, rien n'est envoyé.
            StepError = string.Empty;
            Step = HistoryStep.CancelReason;
            return;
        }

        StepError = string.Empty;
        Step = HistoryStep.None;
    }

    /// <summary>Affiche un ticket précis (venu de l'écran des retours), quelles que soient les dates.</summary>
    public async Task ShowTicketAsync(string ticketNumber)
    {
        if (!datesInitialised)
        {
            datesInitialised = true;
            FromText = ToText = FormatDate(today());
        }

        TicketQuery = ticketNumber;
        await SearchAsync();
    }

    /// <summary>« Retour sur ce ticket » (Ctrl+R) : l'écran des retours s'ouvre sur le ticket affiché.</summary>
    [RelayCommand]
    public async Task BeginReturnAsync()
    {
        if (IsStepOpen || IsBusy || !CanReturnSelected || Detail is not { } ticket)
        {
            return;
        }

        await OpenReturn!(ticket.Sale);
    }

    // ---------- Annulation ----------

    [RelayCommand]
    public void BeginCancel()
    {
        if (IsStepOpen || IsBusy || Detail is not { CanCancel: true } ticket)
        {
            return;
        }

        ErrorMessage = string.Empty;
        InfoMessage = string.Empty;
        CancelReason = string.Empty;
        StepError = string.Empty;
        StepTitle = $"Annuler le ticket {ticket.Sale.TicketNumber} — {ticket.TotalText}";
        Step = HistoryStep.CancelReason;
    }

    /// <summary>Entrée sur le motif : il est obligatoire. Rien n'est envoyé avant la confirmation forte.</summary>
    [RelayCommand]
    public void ValidateCancelReason()
    {
        if (Step != HistoryStep.CancelReason || Detail is not { } ticket)
        {
            return;
        }

        if (CancelReason.Trim().Length == 0)
        {
            StepError = ReasonRequiredMessage;
            return;
        }

        StepError = string.Empty;
        CancelConsequence = ticket.Sale.PaymentMode switch
        {
            SalePaymentModes.Cash =>
                $"Le stock sera restitué et {ticket.TotalText} seront à rendre au client en espèces, sortis de la caisse de ce poste.",
            SalePaymentModes.Card =>
                $"Le stock sera restitué. Le remboursement par carte de {ticket.TotalText} sera tracé, sans sortie d'espèces.",
            _ => "Le stock sera restitué et la créance du client sera annulée.",
        };
        Step = HistoryStep.CancelConfirm;
    }

    /// <summary>Confirmation forte (touche dédiée, jamais Entrée) : envoie l'annulation.</summary>
    [RelayCommand]
    public async Task ConfirmCancelAsync()
    {
        if (Step != HistoryStep.CancelConfirm || IsBusy || Detail is not { } ticket)
        {
            return;
        }

        var reason = CancelReason.Trim();
        if (reason.Length == 0)
        {
            StepError = ReasonRequiredMessage;
            Step = HistoryStep.CancelReason;
            return;
        }

        var request = new CancelSaleRequest(cancelOperationIds.For($"{ticket.Sale.Id}|{reason}"), reason);
        await SendAsync(
            cancelOperationIds,
            () => sales.CancelSaleAsync(ticket.Sale.Id, request, CancellationToken.None),
            cancelled => $"Le ticket {cancelled.TicketNumber} est annulé." + cancelled.Cancellation?.RefundMethod switch
            {
                PaymentMethods.Cash => $" Rendez {Money.ToDisplay(cancelled.Cancellation.RefundAmount)} en espèces au client.",
                PaymentMethods.Card => $" Remboursement par carte de {Money.ToDisplay(cancelled.Cancellation.RefundAmount)} à faire sur le terminal.",
                _ => string.Empty,
            },
            "Annulation non confirmée : le serveur n'a pas répondu. Appuyez de nouveau pour réessayer.",
            ticket.Sale.Id);
    }

    // ---------- Réponse d'un TPE resté en attente ----------

    [RelayCommand]
    public void BeginCardAnswer()
    {
        if (IsStepOpen || IsBusy || Detail is not { CanAnswerCard: true } ticket)
        {
            return;
        }

        ErrorMessage = string.Empty;
        InfoMessage = string.Empty;
        StepError = string.Empty;
        StepTitle = $"Ticket {ticket.Sale.TicketNumber} — {ticket.TotalText}";
        Step = HistoryStep.CardAnswer;
    }

    public async Task AnswerCardAsync(bool accepted)
    {
        if (Step != HistoryStep.CardAnswer || IsBusy || Detail is not { } ticket)
        {
            return;
        }

        var request = new CardResultRequest(cardOperationIds.For($"{ticket.Sale.Id}|{accepted}"), accepted);
        await SendAsync(
            cardOperationIds,
            () => sales.ResolveCardAsync(ticket.Sale.Id, request, CancellationToken.None),
            resolved => resolved.Status == SaleStatuses.Completed
                ? $"Paiement accepté : le ticket {resolved.TicketNumber} est validé."
                : $"Paiement refusé : la vente {resolved.TicketNumber} est annulée et le stock est restitué.",
            "Réponse non confirmée : le serveur n'a pas répondu. Donnez de nouveau la même réponse.",
            ticket.Sale.Id);
    }

    [RelayCommand]
    public Task AcceptCardAsync() => AnswerCardAsync(true);

    [RelayCommand]
    public Task RefuseCardAsync() => AnswerCardAsync(false);

    /// <summary>
    /// Envoie une commande d'écriture sur un ticket. Refus : affiché tel quel, l'étape se ferme
    /// et le ticket est relu. Aucune réponse : l'étape reste ouverte pour réessayer à l'identique.
    /// </summary>
    private async Task SendAsync(
        OperationIdKeeper operationIds, Func<Task<SaleDetail>> send, Func<SaleDetail, string> success, string unconfirmed, Guid saleId)
    {
        IsBusy = true;
        StepError = string.Empty;
        try
        {
            var updated = await send();
            operationIds.Forget();
            Step = HistoryStep.None;
            Replace(updated);
            InfoMessage = success(updated);
        }
        catch (ServerRefusedException refused) when (refused.Code != SalesViewModel.UnreadableAnswerCode)
        {
            operationIds.Forget();
            Step = HistoryStep.None;
            ErrorMessage = refused.Message;
            if (!requireIdentificationIfLost(refused))
            {
                await ReloadAfterRefusalAsync(saleId, refused.Message);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or ServerRefusedException)
        {
            StepError = unconfirmed;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Après un refus, le ticket a peut-être changé (déjà annulé, déjà réglé…) : il est relu, le refus reste affiché.</summary>
    private async Task ReloadAfterRefusalAsync(Guid saleId, string refusalMessage)
    {
        try
        {
            if (await sales.GetSaleAsync(saleId, CancellationToken.None) is { } fresh)
            {
                Replace(fresh);
            }
        }
        catch (Exception exception) when (exception is ServerRefusedException or HttpRequestException or TaskCanceledException)
        {
            // Le refus d'origine est ce qui compte : il reste affiché.
        }

        ErrorMessage = refusalMessage;
    }
}
