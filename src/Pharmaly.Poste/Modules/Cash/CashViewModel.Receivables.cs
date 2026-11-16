using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.Cash;

// Impayés : tableau, filtres, recherche (ticket scanné, numéro, tableau, client) et sélection.
public sealed partial class CashViewModel
{
    private static readonly string[] DateFormats = ["d/M/yyyy", "d/M/yy", "d/M", "d-M-yyyy", "d.M.yyyy"];

    /// <summary>Tickets choisis pour le règlement, tous du même client ; gardés même s'ils sortent du tableau affiché.</summary>
    private readonly List<ReceivableLine> selection = [];

    /// <summary>Pendant qu'un filtre est posé par le programme, ses changements ne relancent pas la lecture.</summary>
    private bool applyingFilters;

    public IReadOnlyList<ReceivableStatusChoice> StatusChoices { get; } =
    [
        new(ReceivableStatuses.Open, "Ouverts"),
        new(ReceivableStatuses.Settled, "Soldés"),
        new(null, "Tous"),
    ];

    public ObservableCollection<ReceivableRowViewModel> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    /// <summary>Scan du code-barres du ticket, numéro de ticket (ou une partie), ou nom du client.</summary>
    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private ReceivableStatusChoice selectedStatus;

    /// <summary>Premier jour, « jj/mm/aaaa » ; vide pour ne pas borner.</summary>
    [ObservableProperty]
    private string fromText = string.Empty;

    [ObservableProperty]
    private string toText = string.Empty;

    /// <summary>Client dont on n'affiche que les tickets ; <c>null</c> pour tous.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomerFilter), nameof(CustomerFilterText))]
    private CustomerSummary? customerFilter;

    public bool HasCustomerFilter => CustomerFilter is not null;

    public string CustomerFilterText => CustomerFilter is { } customer ? $"Client : {customer.Name}" : string.Empty;

    /// <summary>Ligne sous le curseur (↑ ↓) : Espace la coche ou la décoche.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAnswerCurrentPending))]
    private ReceivableRowViewModel? currentRow;

    // ---------- Indicateurs ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpenAmountText), nameof(OpenCountText), nameof(TotalAmountText), nameof(TotalCountText),
        nameof(SettledAmountText), nameof(SettledCountText))]
    private ReceivableIndicators? indicators;

    public string OpenAmountText => Money.ToDisplay(Indicators?.OpenAmount ?? 0m);

    public string OpenCountText => (Indicators?.OpenCount ?? 0).ToString(CultureInfo.InvariantCulture);

    public string TotalAmountText => Money.ToDisplay(Indicators?.TotalAmount ?? 0m);

    public string TotalCountText => ReceivableFormat.Tickets(Indicators?.TotalCount ?? 0);

    public string SettledAmountText => Money.ToDisplay(Indicators?.SettledAmount ?? 0m);

    public string SettledCountText => ReceivableFormat.Tickets(Indicators?.SettledCount ?? 0);

    public string StatusFilterKeyLabel => CashShortcuts.StatusFilterKey.ToString();

    // ---------- Sélection ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectionTotalText), nameof(SelectionCustomerText),
        nameof(SelectionTicketsText), nameof(SelectionTotal))]
    private int selectionCount;

    public bool HasSelection => SelectionCount > 0;

    /// <summary>Montant à encaisser : exactement la somme due des tickets choisis. Jamais saisi.</summary>
    public decimal SelectionTotal => selection.Sum(line => line.Amount);

    public string SelectionTotalText => Money.ToDisplay(SelectionTotal);

    public string SelectionCustomerText => selection.Count == 0 ? string.Empty : selection[0].CustomerName;

    public string SelectionTicketsText => selection.Count == 0
        ? "Aucun ticket choisi : Espace coche la ligne sous le curseur."
        : $"{ReceivableFormat.Tickets(selection.Count)} : {string.Join(", ", selection.Select(line => line.TicketNumber))}";

    public IReadOnlyList<ReceivableLine> Selection => selection;

    public bool CanSettle => Rights.CanViewReceivables && Rights.CanSettle;

    public bool CanAnswerCurrentPending => CanSettle && CurrentRow is { IsCardPending: true };

    public string CashKeyLabel => CashShortcuts.CashLabel;

    public string CardKeyLabel => CashShortcuts.CardLabel;

    // ---------- Filtres ----------

    partial void OnSelectedStatusChanged(ReceivableStatusChoice value)
    {
        if (!applyingFilters)
        {
            _ = ReloadAsync();
        }
    }

    /// <summary>F7 : ouverts → soldés → tous.</summary>
    [RelayCommand]
    public void CycleStatus()
    {
        if (IsStepOpen)
        {
            return;
        }

        var index = StatusChoices.ToList().IndexOf(SelectedStatus);
        SelectedStatus = StatusChoices[(index + 1) % StatusChoices.Count];
    }

    /// <summary>Historique des créances soldées : le tableau montre les tickets soldés, sans borne de dates.</summary>
    [RelayCommand]
    public Task ShowSettledHistoryAsync()
    {
        if (IsStepOpen || !Rights.CanViewReceivables)
        {
            return Task.CompletedTask;
        }

        SetFilters(StatusChoices[1], CustomerFilter, string.Empty, string.Empty);
        return ReloadAsync();
    }

    [RelayCommand]
    public Task ClearCustomerFilterAsync()
    {
        if (IsStepOpen || CustomerFilter is null)
        {
            return Task.CompletedTask;
        }

        CustomerFilter = null;
        return ReloadAsync();
    }

    private void SetFilters(ReceivableStatusChoice status, CustomerSummary? customer, string from, string to)
    {
        applyingFilters = true;
        try
        {
            SelectedStatus = status;
            CustomerFilter = customer;
            FromText = from;
            ToText = to;
        }
        finally
        {
            applyingFilters = false;
        }
    }

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

    private bool TryBuildQuery(string? ticket, out ReceivablesQuery query)
    {
        query = new ReceivablesQuery();
        if (!TryParseDate(FromText, out var from) || !TryParseDate(ToText, out var to))
        {
            ErrorMessage = "Date invalide : saisissez jj/mm/aaaa, ou rien pour ne pas borner.";
            return false;
        }

        query = new ReceivablesQuery(SelectedStatus.Code, CustomerFilter?.Id, from, to, string.IsNullOrWhiteSpace(ticket) ? null : ticket.Trim());
        return true;
    }

    // ---------- Lecture du tableau ----------

    /// <summary>Relit le tableau avec les filtres affichés (sans la recherche de ticket) et les indicateurs.</summary>
    [RelayCommand]
    public async Task ReloadAsync()
    {
        if (!Rights.CanViewReceivables || !TryBuildQuery(null, out var query))
        {
            return;
        }

        await LoadRowsAsync(query);
    }

    /// <summary>Renvoie le nombre de lignes lues, ou -1 si la lecture a échoué.</summary>
    private async Task<int> LoadRowsAsync(ReceivablesQuery query, Guid? focusId = null)
    {
        ReceivablesTable? table = null;
        if (!await RunServerAsync(async () => table = await receivables.GetReceivablesAsync(query, CancellationToken.None)) || table is null)
        {
            return -1;
        }

        Indicators = table.Indicators;
        ShowRows(table.Lines, focusId);
        return table.Lines.Count;
    }

    private void ShowRows(IReadOnlyList<ReceivableLine> lines, Guid? focusId)
    {
        var previous = CurrentRow?.Id;
        Rows.Clear();
        foreach (var line in lines)
        {
            Rows.Add(new ReceivableRowViewModel(line));
        }

        // Une ligne relue qui n'est plus réglable quitte la sélection (soldée par un autre poste, TPE en attente…).
        foreach (var line in lines)
        {
            var index = selection.FindIndex(selected => selected.Id == line.Id);
            if (index < 0)
            {
                continue;
            }

            if (IsSettleable(line))
            {
                selection[index] = line;
            }
            else
            {
                selection.RemoveAt(index);
            }
        }

        RefreshSelection();
        var target = focusId ?? previous;
        CurrentRow = Rows.FirstOrDefault(row => row.Id == target) ?? Rows.FirstOrDefault();
    }

    private static bool IsSettleable(ReceivableLine line) =>
        line.Status == ReceivableStatuses.Open && line.PendingSettlementId is null;

    public void MoveCurrent(int delta)
    {
        if (Rows.Count == 0 || IsStepOpen)
        {
            return;
        }

        var index = CurrentRow is null ? -1 : Rows.IndexOf(CurrentRow);
        CurrentRow = Rows[Math.Clamp(index + delta, 0, Rows.Count - 1)];
    }

    // ---------- Recherche ----------

    /// <summary>
    /// Entrée dans la recherche. Un code-barres ou un numéro exact de ticket montre les tickets
    /// de son client et coche ce ticket s'il est à régler. Sinon le texte cherche dans les
    /// numéros de ticket du tableau, puis dans les noms des clients. Vide : relit le tableau.
    /// </summary>
    [RelayCommand]
    public async Task SubmitSearchAsync()
    {
        if (IsStepOpen || IsSending || !Rights.CanViewReceivables)
        {
            return;
        }

        ClearMessages();
        var text = SearchText.Trim();
        if (text.Length == 0)
        {
            await ReloadAsync();
            return;
        }

        ReceivableLine? exact = null;
        if (!await RunServerAsync(async () => exact = await receivables.FindByTicketAsync(text, CancellationToken.None)))
        {
            return;
        }

        if (exact is not null)
        {
            await ShowTicketAsync(exact);
            return;
        }

        if (!TryBuildQuery(text, out var query))
        {
            return;
        }

        var count = await LoadRowsAsync(query);
        if (count != 0)
        {
            return;
        }

        IReadOnlyList<CustomerSummary> found = [];
        if (!await RunServerAsync(async () => found = await customers.SearchCustomersAsync(text, CancellationToken.None)))
        {
            return;
        }

        switch (found.Count)
        {
            case 0:
                ErrorMessage = $"Aucun ticket ni client ne correspond à « {text} ».";
                break;
            case 1:
                await FilterOnCustomerAsync(found[0]);
                break;
            default:
                OpenCustomerChoice(found);
                break;
        }
    }

    /// <summary>Ticket retrouvé : les tickets de son client, ce ticket sous le curseur et coché s'il est à régler.</summary>
    private async Task ShowTicketAsync(ReceivableLine ticket)
    {
        SearchText = string.Empty;
        var status = IsSettleable(ticket) ? StatusChoices[0] : StatusChoices[2];
        SetFilters(status, new CustomerSummary(ticket.CustomerId, ticket.CustomerName, null), string.Empty, string.Empty);
        if (await LoadRowsAsync(new ReceivablesQuery(status.Code, ticket.CustomerId), ticket.Id) < 0)
        {
            return;
        }

        var row = Rows.FirstOrDefault(candidate => candidate.Id == ticket.Id);
        if (row is null)
        {
            return;
        }

        if (!IsSettleable(row.Line) || !CanSettle)
        {
            InfoMessage = $"Ticket {row.TicketNumber} — {row.CustomerName} — {row.StatusText}.";
            return;
        }

        if (!row.IsChecked)
        {
            Toggle(row);
        }
    }

    private async Task FilterOnCustomerAsync(CustomerSummary customer)
    {
        SearchText = string.Empty;
        SetFilters(SelectedStatus, customer, FromText, ToText);
        await ReloadAsync();
        if (Rows.Count == 0 && ErrorMessage.Length == 0)
        {
            InfoMessage = $"{customer.Name} n'a aucun ticket {(SelectedStatus.Code == ReceivableStatuses.Open ? "impayé ouvert" : "dans ce filtre")}.";
        }
    }

    // ---------- Sélection ----------

    /// <summary>Espace (ou clic sur la case) : coche ou décoche la ligne sous le curseur.</summary>
    [RelayCommand]
    public void ToggleCurrent()
    {
        if (CurrentRow is { } row)
        {
            Toggle(row);
        }
    }

    /// <summary>
    /// Coche ou décoche un ticket. Seuls les tickets ouverts, sans règlement par carte en
    /// attente, d'un seul et même client peuvent être réunis : le serveur refuse les autres.
    /// </summary>
    [RelayCommand]
    public void Toggle(ReceivableRowViewModel? row)
    {
        if (row is null || IsStepOpen || IsSending || !CanSettle)
        {
            return;
        }

        CurrentRow = row;
        ErrorMessage = string.Empty;
        var index = selection.FindIndex(line => line.Id == row.Id);
        if (index >= 0)
        {
            selection.RemoveAt(index);
            RefreshSelection();
            return;
        }

        if (row.IsCardPending)
        {
            ErrorMessage =
                $"Un règlement par carte est en attente sur le ticket {row.TicketNumber} : donnez la réponse du TPE (Ctrl+T) avant de le régler autrement.";
            return;
        }

        if (!row.IsOpen)
        {
            ErrorMessage = $"Le ticket {row.TicketNumber} est {(row.IsSettled ? "déjà soldé" : "annulé")} : il n'y a rien à régler.";
            return;
        }

        if (selection.Count > 0 && selection[0].CustomerId != row.Line.CustomerId)
        {
            ErrorMessage =
                $"Le ticket {row.TicketNumber} appartient à {row.CustomerName}, pas à {selection[0].CustomerName} : "
                + "un règlement groupé concerne un seul client. Réglez d'abord la sélection en cours, ou décochez-la.";
            return;
        }

        selection.Add(row.Line);
        RefreshSelection();
    }

    /// <summary>Vide la sélection.</summary>
    [RelayCommand]
    public void ClearSelection()
    {
        if (IsStepOpen || IsSending)
        {
            return;
        }

        selection.Clear();
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        foreach (var row in Rows)
        {
            row.IsChecked = selection.Exists(line => line.Id == row.Id);
        }

        SelectionCount = selection.Count;
        OnPropertyChanged(nameof(SelectionTotal));
        OnPropertyChanged(nameof(SelectionTotalText));
        OnPropertyChanged(nameof(SelectionCustomerText));
        OnPropertyChanged(nameof(SelectionTicketsText));
    }
}
