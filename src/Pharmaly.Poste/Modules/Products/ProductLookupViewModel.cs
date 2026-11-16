using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.Products;

/// <summary>Ligne d'un résultat de recherche.</summary>
public sealed class ProductRowViewModel(ProductSummary product)
{
    public ProductSummary Product { get; } = product;

    public Guid Id => Product.Id;

    public string Name => Product.Name;

    public string BarcodeText => Product.Barcode ?? "—";

    public string PriceText => Money.ToDisplay(Product.SalePrice);

    /// <summary>Stock vendable du site du poste.</summary>
    public string StockText => Quantities.Format(Product.SellableStock);

    public bool IsOutOfStock => Product.SellableStock <= 0;
}

/// <summary>
/// Champ de recherche de produits, au clavier ou au lecteur : la liste se met à jour pendant
/// la frappe, et Entrée désigne un produit (code-barres exact d'abord, sinon le résultat
/// sélectionné). Sert à la liste du module et aux lignes d'un transfert.
/// </summary>
public sealed partial class ProductLookupViewModel(ICatalogClient catalog, TimeSpan searchDelay, Action<ServerRefusedException> refused)
    : ObservableObject
{
    private const int MinScannedDigits = 8;
    private const int MinBarcodeLength = 4;

    private CancellationTokenSource? searchCancellation;

    /// <summary>Saisie pour laquelle <see cref="Results"/> a été chargé.</summary>
    private string? loadedTerm;

    [ObservableProperty]
    private string query = string.Empty;

    /// <summary>Message sous le champ : aucun résultat, refus du serveur, serveur injoignable.</summary>
    [ObservableProperty]
    private string notice = string.Empty;

    [ObservableProperty]
    private ProductRowViewModel? selectedResult;

    [ObservableProperty]
    private bool hasResults;

    public ObservableCollection<ProductRowViewModel> Results { get; } = [];

    /// <summary>Code-barres qui vient d'être scanné sans correspondre à aucun produit, sinon <c>null</c>.</summary>
    public string? UnknownBarcode { get; private set; }

    partial void OnQueryChanged(string value)
    {
        UnknownBarcode = null;
        Notice = string.Empty;
        _ = SearchAfterDelayAsync(value);
    }

    private async Task SearchAfterDelayAsync(string term)
    {
        searchCancellation?.Cancel();
        var cancellation = searchCancellation = new CancellationTokenSource();
        if (string.IsNullOrWhiteSpace(term))
        {
            SetResults([], null);
            return;
        }

        try
        {
            await Task.Delay(searchDelay, cancellation.Token);
            await SearchNowAsync(term.Trim(), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Une frappe plus récente a remplacé cette recherche.
        }
        catch (ServerRefusedException exception)
        {
            Report(exception);
        }
        catch (HttpRequestException)
        {
            Notice = ModuleViewModel.ServerUnreachableMessage;
        }
    }

    private async Task SearchNowAsync(string term, CancellationToken cancellationToken)
    {
        var found = await catalog.SearchProductsAsync(term, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        SetResults(found, term);
    }

    private void SetResults(IReadOnlyList<ProductSummary> found, string? term, Guid? keepSelected = null)
    {
        Results.Clear();
        foreach (var product in found)
        {
            Results.Add(new ProductRowViewModel(product));
        }

        loadedTerm = term;
        HasResults = Results.Count > 0;
        SelectedResult = Results.FirstOrDefault(row => row.Id == keepSelected) ?? Results.FirstOrDefault();
    }

    /// <summary>
    /// Touche Entrée : renvoie le produit désigné, ou <c>null</c> s'il reste à choisir dans
    /// la liste (ou s'il n'y a rien : <see cref="Notice"/> l'explique).
    /// </summary>
    public async Task<ProductSummary?> ResolveAsync()
    {
        var term = Query.Trim();
        if (term.Length == 0)
        {
            return null;
        }

        searchCancellation?.Cancel();
        var alreadyLoaded = loadedTerm == term;
        try
        {
            // Un lecteur envoie le code puis Entrée avant que la liste ait eu le temps d'arriver.
            if (LooksScanned(term) || (!alreadyLoaded && CouldBeBarcode(term)))
            {
                if (await catalog.FindByBarcodeAsync(term, CancellationToken.None) is { } scanned)
                {
                    return scanned;
                }
            }

            if (!alreadyLoaded)
            {
                await SearchNowAsync(term, CancellationToken.None);
            }

            if (Results.Count == 0)
            {
                Notice = $"Aucun produit trouvé pour « {term} ».";
                UnknownBarcode = LooksScanned(term) ? term : null;
                return null;
            }

            return Results.Count == 1 || alreadyLoaded ? (SelectedResult ?? Results[0]).Product : null;
        }
        catch (ServerRefusedException exception)
        {
            Report(exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Notice = ModuleViewModel.ServerUnreachableMessage;
        }

        return null;
    }

    /// <summary>Relit la liste affichée après une écriture (prix, stock, désignation), sans changer la sélection.</summary>
    public async Task RefreshAsync()
    {
        if (loadedTerm is not { } term)
        {
            return;
        }

        try
        {
            var found = await catalog.SearchProductsAsync(term, CancellationToken.None);
            if (loadedTerm == term)
            {
                SetResults(found, term, SelectedResult?.Id);
            }
        }
        catch (Exception exception) when (exception is ServerRefusedException or HttpRequestException or TaskCanceledException)
        {
            // La liste garde ses anciennes valeurs : l'écriture, elle, a déjà été confirmée.
        }
    }

    /// <summary>↑ ↓ : déplace la sélection dans la liste. Renvoie la nouvelle position, ou -1.</summary>
    public int MoveSelection(int delta)
    {
        if (Results.Count == 0)
        {
            return -1;
        }

        var current = SelectedResult is null ? -1 : Results.IndexOf(SelectedResult);
        var next = Math.Clamp(current + delta, 0, Results.Count - 1);
        SelectedResult = Results[next];
        return next;
    }

    public void Clear() => Query = string.Empty;

    /// <summary>Arrête la recherche en cours (verrouillage du poste, fermeture du formulaire).</summary>
    public void Stop() => searchCancellation?.Cancel();

    private void Report(ServerRefusedException exception)
    {
        SetResults([], null);
        Notice = exception.Message;
        refused(exception);
    }

    private static bool LooksScanned(string term) => term.Length >= MinScannedDigits && term.All(char.IsAsciiDigit);

    private static bool CouldBeBarcode(string term) =>
        term.Length >= MinBarcodeLength && term.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-');
}
