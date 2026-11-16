using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pharmaly.Contracts;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.Products;

/// <summary>Emplacement proposé dans une liste de choix, avec le stock connu du produit affiché.</summary>
public sealed record LocationChoice(Guid Id, string Label, decimal Quantity = 0)
{
    public static LocationChoice From(ProductStockLine line) =>
        new(line.LocationId, $"{line.StoreName} — {line.LocationName}", line.Quantity);

    public static LocationChoice From(StockLocationSummary location) =>
        new(location.Id, $"{location.StoreName} — {location.Name}");

    public override string ToString() => Label;
}

/// <summary>
/// Ajustement d'inventaire d'un produit à un emplacement : quantité comptée ou correction,
/// motif obligatoire, puis confirmation qui montre le stock avant et après.
/// </summary>
public sealed partial class StockAdjustmentViewModel : ProductFormViewModel
{
    private readonly IStockClient stock;
    private readonly ProductDetail product;
    private readonly Func<StockAdjustmentResult, LocationChoice, Task> adjusted;
    private decimal confirmedQuantity;

    public StockAdjustmentViewModel(
        IStockClient stock, ProductDetail product, Func<StockAdjustmentResult, LocationChoice, Task> adjusted, Action cancelled)
        : base(cancelled)
    {
        this.stock = stock;
        this.product = product;
        this.adjusted = adjusted;
        Locations = product.Stock.Select(LocationChoice.From).ToList();
        location = Locations.FirstOrDefault();
    }

    public string ProductName => product.Name;

    public IReadOnlyList<LocationChoice> Locations { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStockText))]
    private LocationChoice? location;

    /// <summary>Vrai : la saisie est la quantité comptée. Faux : c'est une correction en plus ou en moins.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCorrection), nameof(QuantityLabel))]
    private bool isCount = true;

    public bool IsCorrection
    {
        get => !IsCount;
        set => IsCount = !value;
    }

    [ObservableProperty]
    private string quantity = string.Empty;

    [ObservableProperty]
    private string reason = string.Empty;

    public string QuantityLabel => IsCount ? "Quantité comptée" : "Correction (+ ou -)";

    public string CurrentStockText => Location is { } chosen ? Quantities.Format(chosen.Quantity) : "—";

    // Résumé affiché dans la confirmation.
    [ObservableProperty]
    private string beforeText = string.Empty;

    [ObservableProperty]
    private string afterText = string.Empty;

    [ObservableProperty]
    private string changeText = string.Empty;

    /// <summary>Entrée : lit la saisie et demande confirmation, puis, une fois confirmé, envoie l'ajustement.</summary>
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

        var chosen = Location!;
        StockAdjustmentResult? result = null;
        var sent = await RunWriteAsync(async operationId => result = await stock.AdjustAsync(
            new StockAdjustmentRequest(
                operationId, product.Id, chosen.Id,
                IsCount ? confirmedQuantity : null, IsCount ? null : confirmedQuantity, Reason.Trim()),
            CancellationToken.None));
        if (sent)
        {
            await adjusted(result!, chosen);
        }
        else
        {
            // Refus : retour à la saisie. Panne de réseau : Entrée retente le même envoi.
            IsConfirming = OutcomeUnknown;
        }
    }

    private void Review()
    {
        if (Location is not { } chosen)
        {
            ErrorMessage = "Choisissez l'emplacement à ajuster.";
            return;
        }

        if (DecimalInput.ReadQuantity(Quantity, product.SoldByUnit, allowSign: IsCorrection, out var value) is { } refusal)
        {
            ErrorMessage = refusal;
            return;
        }

        if (IsCorrection && value == 0)
        {
            ErrorMessage = "Une correction de zéro ne change rien : saisissez l'écart à appliquer.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Reason))
        {
            ErrorMessage = "Le motif de l'ajustement est obligatoire.";
            return;
        }

        var after = IsCount ? value : chosen.Quantity + value;
        ErrorMessage = string.Empty;
        confirmedQuantity = value;
        BeforeText = Quantities.Format(chosen.Quantity);
        AfterText = Quantities.Format(after);
        ChangeText = Quantities.FormatSigned(after - chosen.Quantity);
        IsConfirming = true;
    }
}
