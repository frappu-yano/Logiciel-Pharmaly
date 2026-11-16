using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.Sales;

/// <summary>Ligne de l'historique des retours.</summary>
public sealed class ReturnRowViewModel(ReturnSummary summary)
{
    public ReturnSummary Summary { get; } = summary;

    public Guid Id => Summary.Id;

    public string ReturnNumber => Summary.ReturnNumber;

    public string TicketNumber => Summary.TicketNumber;

    public string DateText => SaleFormat.DateTime(Summary.CreatedAt);

    public string ReasonLabel => Summary.ReasonLabel;

    public string RefundText => Money.ToDisplay(Summary.RefundAmount);

    public string MethodText => Summary.RefundMethod is { } method ? SaleFormat.PaymentMode(method) : "—";
}

/// <summary>Détail d'un retour, prêt à afficher.</summary>
public sealed class ReturnDetailViewModel(ReturnDetail detail)
{
    public ReturnDetail Detail { get; } = detail;

    public string Title => $"Retour {Detail.ReturnNumber}";

    public string Subtitle =>
        $"{SaleFormat.DateTime(Detail.CreatedAt)} — ticket {Detail.TicketNumber} — {Detail.EmployeeName} — {Detail.RegisterName}";

    public string ReasonText => $"Motif : {Detail.ReasonLabel}";

    public string DestinationText => Detail.DestinationLocationName is { } location
        ? $"{SaleFormat.StockDestination(Detail.StockDestination)} — {location}"
        : SaleFormat.StockDestination(Detail.StockDestination);

    public bool HasNote => !string.IsNullOrWhiteSpace(Detail.Note);

    public string NoteText => $"Note : {Detail.Note}";

    public string RefundText => Money.ToDisplay(Detail.RefundAmount);

    public string MethodText => SaleFormat.RefundMethod(Detail.RefundMethod);

    public IReadOnlyList<string> Lines { get; } = detail.Lines
        .Select(line =>
            $"{line.ProductName} — {SaleFormat.Quantity(line.Quantity)} rendu"
            + (line.NotRestockedQuantity > 0 ? $" ({SaleFormat.Quantity(line.NotRestockedQuantity)} non remis en stock)" : string.Empty)
            + $" — {Money.ToDisplay(line.RefundAmount)}")
        .ToList();
}

/// <summary>Historique des retours du site, filtré par dates et par ticket, avec le détail du retour choisi.</summary>
public sealed partial class ReturnsHistoryViewModel : ObservableObject
{
    private static readonly string[] DateFormats = ["d/M/yyyy", "d/M/yy", "d/M", "d-M-yyyy", "d.M.yyyy"];

    private readonly IReturnsClient returns;
    private readonly Func<ServerRefusedException, bool> requireIdentificationIfLost;
    private readonly Func<DateOnly> today;
    private bool datesInitialised;
    private int detailVersion;

    public ReturnsHistoryViewModel(
        IReturnsClient returns, Func<ServerRefusedException, bool> requireIdentificationIfLost, Func<DateOnly>? today = null)
    {
        this.returns = returns;
        this.requireIdentificationIfLost = requireIdentificationIfLost;
        this.today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
    }

    public ObservableCollection<ReturnRowViewModel> Returns { get; } = [];

    [ObservableProperty]
    private string fromText = string.Empty;

    [ObservableProperty]
    private string toText = string.Empty;

    /// <summary>Numéro de ticket (ou une partie), code-barres du ticket, ou numéro du retour.</summary>
    [ObservableProperty]
    private string ticketQuery = string.Empty;

    [ObservableProperty]
    private ReturnRowViewModel? selectedReturn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private ReturnDetailViewModel? detail;

    public bool HasDetail => Detail is not null;

    [ObservableProperty]
    private string errorMessage = string.Empty;

    [ObservableProperty]
    private string infoMessage = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    /// <summary>À chaque arrivée : les retours du jour, ou ceux des filtres déjà saisis.</summary>
    public Task ActivateAsync()
    {
        if (!datesInitialised)
        {
            datesInitialised = true;
            FromText = ToText = today().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        }

        return SearchAsync();
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

        date = cleaned.Count(character => character is '/' or '-' or '.') == 1 ? new DateOnly(today().Year, parsed.Month, parsed.Day) : parsed;
        return true;
    }

    /// <summary>Entrée : applique les filtres (dates, ticket ou numéro de retour). Une date vide ne borne pas.</summary>
    [RelayCommand]
    public async Task SearchAsync()
    {
        if (IsBusy)
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
        IsBusy = true;
        try
        {
            var found = await returns.ListReturnsAsync(
                new ReturnsHistoryQuery(from, to, ticket.Length == 0 ? null : ticket), CancellationToken.None);
            detailVersion++;
            Returns.Clear();
            foreach (var row in found)
            {
                Returns.Add(new ReturnRowViewModel(row));
            }

            Detail = null;
            SelectedReturn = Returns.FirstOrDefault();
            if (found.Count == 0)
            {
                InfoMessage = "Aucun retour ne correspond à ces filtres.";
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
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedReturnChanged(ReturnRowViewModel? value)
    {
        if (value is null)
        {
            detailVersion++;
            Detail = null;
        }
        else if (Detail?.Detail.Id != value.Id)
        {
            _ = LoadDetailAsync(value.Id);
        }
    }

    public void MoveSelection(int delta)
    {
        if (Returns.Count == 0)
        {
            return;
        }

        var current = SelectedReturn is null ? -1 : Returns.IndexOf(SelectedReturn);
        SelectedReturn = Returns[Math.Clamp(current + delta, 0, Returns.Count - 1)];
    }

    /// <summary>Lit le détail du retour sélectionné ; une sélection plus récente remplace la lecture en cours.</summary>
    public async Task LoadDetailAsync(Guid returnId)
    {
        var version = ++detailVersion;
        try
        {
            var read = await returns.GetReturnAsync(returnId, CancellationToken.None);
            if (version != detailVersion)
            {
                return;
            }

            Detail = read is null ? null : new ReturnDetailViewModel(read);
            if (read is null)
            {
                ErrorMessage = "Retour introuvable.";
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
}
