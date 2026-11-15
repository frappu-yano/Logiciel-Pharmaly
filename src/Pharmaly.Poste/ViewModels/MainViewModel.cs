using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services;

namespace Pharmaly.Poste.ViewModels;

/// <summary>
/// Écran d'essai du lot 0 : recherche, lecture de code-barres et lignes, sur des données
/// fictives. Aucune vente n'est enregistrée ; il sert à mesurer la rapidité du poste.
/// </summary>
public sealed partial class MainViewModel(IServerClient server, TimeSpan searchDelay) : ObservableObject
{
    public static readonly TimeSpan DefaultSearchDelay = TimeSpan.FromMilliseconds(120);
    private const int MaxQuantity = 9999;

    private readonly Measurements measurements = new();
    private CancellationTokenSource? searchCancellation;
    private bool resultsHooked;

    [ObservableProperty]
    private string query = string.Empty;

    [ObservableProperty]
    private string statusText = "Connexion au serveur…";

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private string notice = string.Empty;

    [ObservableProperty]
    private ProductSummary? selectedResult;

    [ObservableProperty]
    private SaleLineViewModel? selectedLine;

    [ObservableProperty]
    private string totalText = Money.ToDisplay(0);

    [ObservableProperty]
    private string articleCountText = "0 article";

    [ObservableProperty]
    private string memoryText = "Mémoire : —";

    [ObservableProperty]
    private string searchSpeedText = "Recherche : —";

    [ObservableProperty]
    private string benchmarkText = string.Empty;

    [ObservableProperty]
    private bool isBenchmarkRunning;

    [ObservableProperty]
    private bool hasResults;

    public ObservableCollection<ProductSummary> Results { get; } = [];

    public ObservableCollection<SaleLineViewModel> Lines { get; } = [];

    private void HookResults()
    {
        if (resultsHooked)
        {
            return;
        }

        resultsHooked = true;
        Results.CollectionChanged += (_, _) => HasResults = Results.Count > 0;
    }

    public Measurements Measurements => measurements;

    /// <summary>Temps écoulé entre le lancement du programme et l'affichage de la fenêtre.</summary>
    public double StartupSeconds { get; set; }

    public decimal Total => Lines.Sum(line => line.LineTotal);

    public async Task CheckServerAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var health = await server.GetHealthAsync(cancellationToken);
            IsConnected = health is { DatabaseReachable: true };
            StatusText = health switch
            {
                null => "Serveur injoignable",
                { DatabaseReachable: false } => "Serveur joint, base de données indisponible",
                _ => $"Connecté au serveur — version {health.Version}",
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            IsConnected = false;
            StatusText = "Serveur injoignable — vérifiez le réseau du magasin";
        }
    }

    partial void OnQueryChanged(string value)
    {
        HookResults();
        Notice = string.Empty;
        _ = SearchAfterDelayAsync(value);
    }

    private async Task SearchAfterDelayAsync(string term)
    {
        searchCancellation?.Cancel();
        var cancellation = searchCancellation = new CancellationTokenSource();

        if (string.IsNullOrWhiteSpace(term))
        {
            Results.Clear();
            return;
        }

        try
        {
            await Task.Delay(searchDelay, cancellation.Token);
            await SearchNowAsync(term, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Une frappe plus récente a remplacé cette recherche.
        }
    }

    public async Task SearchNowAsync(string term, CancellationToken cancellationToken = default)
    {
        HookResults();
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var found = await server.SearchProductsAsync(term.Trim(), cancellationToken);
            stopwatch.Stop();
            cancellationToken.ThrowIfCancellationRequested();

            measurements.RecordSearch(stopwatch.Elapsed);
            Results.Clear();
            foreach (var product in found)
            {
                Results.Add(product);
            }

            SelectedResult = Results.FirstOrDefault();
            RefreshSearchSpeed();
        }
        catch (ServerRefusedException refused)
        {
            HandleRefusal(refused);
        }
        catch (HttpRequestException)
        {
            IsConnected = false;
            StatusText = "Serveur injoignable — vérifiez le réseau du magasin";
        }
    }

    /// <summary>Levé quand le serveur ne reconnaît plus l'employé : le poste doit redemander l'identification.</summary>
    public event EventHandler? IdentificationRequired;

    private void HandleRefusal(ServerRefusedException refused)
    {
        Results.Clear();
        Notice = refused.Message;
        if (refused.Code is ApiErrorCodes.EmployeeNotIdentified or ApiErrorCodes.PosteNotSignedIn)
        {
            IdentificationRequired?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Touche Entrée : code-barres exact d'abord, sinon le produit sélectionné.</summary>
    [RelayCommand]
    public async Task SubmitAsync()
    {
        var term = Query.Trim();
        if (term.Length == 0)
        {
            return;
        }

        searchCancellation?.Cancel();
        try
        {
            var scanned = await server.FindByBarcodeAsync(term, CancellationToken.None);
            if (scanned is not null)
            {
                AddLine(scanned);
                return;
            }

            await SearchNowAsync(term);
            if (Results.Count == 1)
            {
                AddLine(Results[0]);
                return;
            }

            if (Results.Count == 0)
            {
                Notice = $"Aucun produit trouvé pour « {term} ».";
            }
        }
        catch (ServerRefusedException refused)
        {
            HandleRefusal(refused);
        }
        catch (HttpRequestException)
        {
            IsConnected = false;
            StatusText = "Serveur injoignable — vérifiez le réseau du magasin";
        }
    }

    [RelayCommand]
    public void AddSelectedResult()
    {
        if (SelectedResult is not null)
        {
            AddLine(SelectedResult);
        }
    }

    public void AddLine(ProductSummary product)
    {
        var existing = Lines.FirstOrDefault(line => line.Product.Id == product.Id);
        if (existing is null)
        {
            existing = new SaleLineViewModel(product);
            Lines.Add(existing);
        }
        else if (existing.Quantity < MaxQuantity)
        {
            existing.Quantity++;
        }

        SelectedLine = existing;
        Query = string.Empty;
        Results.Clear();
        RefreshTotals();
    }

    [RelayCommand]
    public void IncreaseSelectedLine() => ChangeSelectedQuantity(+1);

    [RelayCommand]
    public void DecreaseSelectedLine() => ChangeSelectedQuantity(-1);

    [RelayCommand]
    public void RemoveSelectedLine()
    {
        if (SelectedLine is null)
        {
            return;
        }

        Lines.Remove(SelectedLine);
        SelectedLine = Lines.LastOrDefault();
        RefreshTotals();
    }

    [RelayCommand]
    public void ClearLines()
    {
        Lines.Clear();
        SelectedLine = null;
        RefreshTotals();
    }

    private void ChangeSelectedQuantity(int delta)
    {
        if (SelectedLine is null)
        {
            return;
        }

        var quantity = SelectedLine.Quantity + delta;
        if (quantity <= 0)
        {
            RemoveSelectedLine();
            return;
        }

        SelectedLine.Quantity = Math.Min(quantity, MaxQuantity);
        RefreshTotals();
    }

    private void RefreshTotals()
    {
        TotalText = Money.ToDisplay(Total);
        var count = Lines.Sum(line => line.Quantity);
        ArticleCountText = count > 1 ? $"{count} articles" : $"{count} article";
    }

    public void RefreshMemory() =>
        MemoryText = $"Mémoire : {Measurements.ReadWorkingSetMegabytes():F0} Mo";

    private void RefreshSearchSpeed() =>
        SearchSpeedText =
            $"Recherche : {measurements.LastSearchMilliseconds:F0} ms (médiane {measurements.Percentile(0.5):F0} ms, "
            + $"95 % sous {measurements.Percentile(0.95):F0} ms, {measurements.SearchCount} recherches)";

    /// <summary>Essai de rapidité automatique : recherches et ajouts de lignes en série.</summary>
    [RelayCommand]
    private async Task RunBenchmarkAsync() => await ExecuteBenchmarkAsync();

    public async Task<BenchmarkReport> ExecuteBenchmarkAsync()
    {
        IsBenchmarkRunning = true;
        BenchmarkText = "Essai en cours…";
        try
        {
            var report = await Benchmark.RunAsync(this);
            BenchmarkText = report.ToDisplay();
            return report;
        }
        finally
        {
            IsBenchmarkRunning = false;
        }
    }
}
