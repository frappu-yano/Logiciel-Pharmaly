using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pharmaly.Contracts;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.Products;

/// <summary>Ligne d'un transfert en cours de saisie.</summary>
public sealed partial class TransferLineViewModel(ProductDetail product) : ObservableObject
{
    public Guid ProductId => product.Id;

    public string Name => product.Name;

    public string BarcodeText => product.Barcodes.Count > 0 ? product.Barcodes[0] : "—";

    public bool SoldByUnit => product.SoldByUnit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuantityText))]
    private decimal quantity = 1;

    public string QuantityText => Quantities.Format(Quantity);

    /// <summary>Stock connu à l'emplacement de départ au moment où la ligne a été ajoutée.</summary>
    [ObservableProperty]
    private string availableText = "—";

    public void ShowAvailabilityAt(Guid? locationId)
    {
        var line = product.Stock.FirstOrDefault(stock => stock.LocationId == locationId);
        AvailableText = line is null ? "—" : Quantities.Format(line.Quantity);
    }
}

/// <summary>Ligne du résultat d'un transfert enregistré.</summary>
public sealed record TransferResultLineViewModel(string Name, string QuantityText, string FromAfterText, string ToAfterText);

/// <summary>Résultat affiché après un transfert enregistré.</summary>
public sealed record TransferResultViewModel(string Title, IReadOnlyList<TransferResultLineViewModel> Lines);

/// <summary>
/// Transfert simple entre deux emplacements : départ, arrivée, lignes saisies au lecteur ou
/// par recherche, confirmation, puis résultat. Si le serveur refuse (stock insuffisant…),
/// son message est affiché tel quel et toute la saisie est conservée.
/// </summary>
public sealed partial class StockTransferViewModel : ProductFormViewModel
{
    private readonly ICatalogClient catalog;
    private readonly IStockClient stock;
    private readonly Func<StockTransferResult, Task> transferred;

    public StockTransferViewModel(
        ICatalogClient catalog,
        IStockClient stock,
        IReadOnlyList<StockLocationSummary> locations,
        TimeSpan searchDelay,
        Func<StockTransferResult, Task> transferred,
        Action cancelled)
        : base(cancelled)
    {
        this.catalog = catalog;
        this.stock = stock;
        this.transferred = transferred;
        Locations = locations.Select(LocationChoice.From).ToList();
        from = Locations.FirstOrDefault();
        to = Locations.Skip(1).FirstOrDefault();
        Lookup = new ProductLookupViewModel(catalog, searchDelay, RelayRefusal);
    }

    public IReadOnlyList<LocationChoice> Locations { get; }

    /// <summary>Recherche du produit à ajouter.</summary>
    public ProductLookupViewModel Lookup { get; }

    public ObservableCollection<TransferLineViewModel> Lines { get; } = [];

    [ObservableProperty]
    private LocationChoice? from;

    [ObservableProperty]
    private LocationChoice? to;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedLine))]
    private TransferLineViewModel? selectedLine;

    /// <summary>Quantité de la ligne sélectionnée, telle qu'elle est saisie.</summary>
    [ObservableProperty]
    private string lineQuantity = string.Empty;

    [ObservableProperty]
    private string note = string.Empty;

    [ObservableProperty]
    private string confirmationText = string.Empty;

    /// <summary>Dernier transfert enregistré, affiché jusqu'à la saisie suivante.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private TransferResultViewModel? result;

    [ObservableProperty]
    private bool hasLines;

    public bool HasSelectedLine => SelectedLine is not null;

    public bool HasResult => Result is not null;

    partial void OnFromChanged(LocationChoice? value)
    {
        foreach (var line in Lines)
        {
            line.ShowAvailabilityAt(value?.Id);
        }
    }

    partial void OnSelectedLineChanged(TransferLineViewModel? value) =>
        LineQuantity = value is null ? string.Empty : value.QuantityText;

    /// <summary>
    /// Entrée dans la recherche : ajoute le produit désigné (une unité), ou une unité de plus
    /// s'il est déjà sur une ligne. Renvoie vrai si une ligne a été ajoutée ou augmentée.
    /// </summary>
    public async Task<bool> AddFromQueryAsync()
    {
        if (IsBusy || IsConfirming)
        {
            return false;
        }

        ErrorMessage = string.Empty;
        var product = await Lookup.ResolveAsync();
        return product is not null && await AddProductAsync(product);
    }

    private async Task<bool> AddProductAsync(ProductSummary product)
    {
        var line = Lines.FirstOrDefault(candidate => candidate.ProductId == product.Id);
        if (line is not null)
        {
            line.Quantity++;
        }
        else
        {
            // La fiche dit si le produit se vend à l'unité et ce qu'il reste au départ.
            ProductDetail? detail = null;
            if (!await RunReadAsync(async () => detail = await catalog.GetProductAsync(product.Id, CancellationToken.None)))
            {
                return false;
            }

            line = new TransferLineViewModel(detail!);
            line.ShowAvailabilityAt(From?.Id);
            Lines.Add(line);
        }

        Result = null;
        HasLines = true;
        SelectedLine = line;
        LineQuantity = line.QuantityText;
        Lookup.Clear();
        return true;
    }

    /// <summary>Entrée dans le champ quantité : applique la saisie à la ligne sélectionnée.</summary>
    public bool ApplyLineQuantity()
    {
        if (SelectedLine is not { } line || IsConfirming)
        {
            return false;
        }

        if (DecimalInput.ReadQuantity(LineQuantity, line.SoldByUnit, allowSign: false, out var quantity) is { } refusal)
        {
            ErrorMessage = refusal;
            return false;
        }

        if (quantity <= 0)
        {
            ErrorMessage = "La quantité à transférer doit être supérieure à zéro.";
            return false;
        }

        ErrorMessage = string.Empty;
        line.Quantity = quantity;
        LineQuantity = line.QuantityText;
        return true;
    }

    [RelayCommand]
    public void RemoveSelectedLine()
    {
        if (SelectedLine is not { } line || IsConfirming || IsBusy)
        {
            return;
        }

        var index = Lines.IndexOf(line);
        Lines.Remove(line);
        HasLines = Lines.Count > 0;
        SelectedLine = Lines.Count == 0 ? null : Lines[Math.Min(index, Lines.Count - 1)];
    }

    /// <summary>↑ ↓ : change la ligne sélectionnée. Renvoie la nouvelle position, ou -1.</summary>
    public int MoveLineSelection(int delta)
    {
        if (Lines.Count == 0 || IsConfirming)
        {
            return -1;
        }

        var current = SelectedLine is null ? -1 : Lines.IndexOf(SelectedLine);
        var next = Math.Clamp(current + delta, 0, Lines.Count - 1);
        SelectedLine = Lines[next];
        return next;
    }

    /// <summary>Ctrl+Entrée : demande confirmation ; Entrée sur la confirmation : envoie le transfert.</summary>
    [RelayCommand]
    public async Task SubmitAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (!IsConfirming)
        {
            Review();
            return;
        }

        var (departure, arrival) = (From!, To!);
        var lines = Lines.ToList();
        var request = new StockTransferRequest(
            Guid.Empty, departure.Id, arrival.Id,
            lines.Select(line => new StockTransferLine(line.ProductId, line.Quantity)).ToList(),
            ProductCreateViewModel.NullIfEmpty(Note));
        StockTransferResult? outcome = null;
        var sent = await RunWriteAsync(async operationId =>
            outcome = await stock.TransferAsync(request with { OperationId = operationId }, CancellationToken.None));
        if (!sent)
        {
            // Refus : retour à la saisie, intacte. Panne de réseau : Entrée retente le même envoi.
            IsConfirming = OutcomeUnknown;
            return;
        }

        Result = new TransferResultViewModel(
            $"Transfert enregistré de « {departure.Label} » vers « {arrival.Label} »",
            outcome!.Lines.Select(done => new TransferResultLineViewModel(
                lines.FirstOrDefault(line => line.ProductId == done.ProductId)?.Name ?? "Produit",
                Quantities.Format(done.Quantity),
                Quantities.Format(done.FromQuantityAfter),
                Quantities.Format(done.ToQuantityAfter))).ToList());
        Lines.Clear();
        HasLines = false;
        SelectedLine = null;
        Note = string.Empty;
        IsConfirming = false;
        await transferred(outcome);
    }

    private void Review()
    {
        if (From is not { } departure || To is not { } arrival)
        {
            ErrorMessage = "Choisissez l'emplacement de départ et l'emplacement d'arrivée.";
            return;
        }

        if (departure.Id == arrival.Id)
        {
            ErrorMessage = "L'emplacement de départ et celui d'arrivée doivent être différents.";
            return;
        }

        if (Lines.Count == 0)
        {
            ErrorMessage = "Ajoutez au moins un produit à transférer.";
            return;
        }

        ErrorMessage = string.Empty;
        var products = Lines.Count > 1 ? $"{Lines.Count} produits" : $"« {Lines[0].Name} » (quantité {Lines[0].QuantityText})";
        ConfirmationText = $"Transférer {products} de « {departure.Label} » vers « {arrival.Label} » ?";
        IsConfirming = true;
    }

    /// <summary>Arrête la recherche en cours quand le formulaire est fermé.</summary>
    public void Stop() => Lookup.Stop();
}
