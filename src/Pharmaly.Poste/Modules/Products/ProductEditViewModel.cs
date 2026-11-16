using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.Products;

/// <summary>
/// Modification d'un produit : désignation, marque, catégorie, actif ou inactif, ajout et
/// retrait de codes-barres. Le prix de vente se change ailleurs (<see cref="SalePriceViewModel"/>).
/// </summary>
public sealed partial class ProductEditViewModel : ProductFormViewModel
{
    private readonly ICatalogClient catalog;
    private readonly ProductDetail product;
    private readonly Func<ProductSummary, Task> saved;

    public ProductEditViewModel(ICatalogClient catalog, ProductDetail product, Func<ProductSummary, Task> saved, Action cancelled)
        : base(cancelled)
    {
        this.catalog = catalog;
        this.product = product;
        this.saved = saved;
        name = product.Name;
        brand = product.Brand ?? string.Empty;
        category = product.Category ?? string.Empty;
        isActive = product.IsActive;
        Barcodes = new BarcodeListViewModel(product.Barcodes);
    }

    public string Title => $"Modifier « {product.Name} »";

    [ObservableProperty]
    private string name;

    [ObservableProperty]
    private string brand;

    [ObservableProperty]
    private string category;

    /// <summary>Un produit inactif n'est plus proposé à la vente ni dans les recherches.</summary>
    [ObservableProperty]
    private bool isActive;

    public BarcodeListViewModel Barcodes { get; }

    [RelayCommand]
    private void RemoveBarcode(string barcode) => Barcodes.Remove(barcode);

    /// <summary>Envoie la fiche et la différence entre les codes-barres d'origine et ceux de la liste.</summary>
    [RelayCommand]
    public async Task SubmitAsync()
    {
        if (IsBusy)
        {
            return;
        }

        Barcodes.AddEntry();
        var toAdd = Barcodes.Items.Except(product.Barcodes).ToList();
        var toRemove = product.Barcodes.Except(Barcodes.Items).ToList();
        ProductSummary? summary = null;
        var sent = await RunWriteAsync(async operationId => summary = await catalog.UpdateProductAsync(
            product.Id,
            new UpdateProductRequest(
                operationId, Name.Trim(), ProductCreateViewModel.NullIfEmpty(Brand), ProductCreateViewModel.NullIfEmpty(Category),
                IsActive, toAdd, toRemove),
            CancellationToken.None));
        if (sent)
        {
            await saved(summary!);
        }
    }
}

/// <summary>
/// Changement du prix de vente : opération critique, envoyée seulement après une
/// confirmation qui montre l'ancien et le nouveau prix.
/// </summary>
public sealed partial class SalePriceViewModel : ProductFormViewModel
{
    private readonly ICatalogClient catalog;
    private readonly ProductDetail product;
    private readonly Func<SalePriceChangeResult, Task> changed;
    private decimal confirmedPrice;

    public SalePriceViewModel(ICatalogClient catalog, ProductDetail product, Func<SalePriceChangeResult, Task> changed, Action cancelled)
        : base(cancelled)
    {
        this.catalog = catalog;
        this.product = product;
        this.changed = changed;
    }

    public string ProductName => product.Name;

    public string CurrentPriceText => Money.ToDisplay(product.SalePrice);

    [ObservableProperty]
    private string newPrice = string.Empty;

    /// <summary>Nouveau prix tel qu'il sera envoyé, affiché dans la confirmation.</summary>
    [ObservableProperty]
    private string newPriceText = string.Empty;

    /// <summary>Entrée : lit le prix et demande confirmation, puis, une fois confirmé, l'envoie.</summary>
    [RelayCommand]
    public async Task SubmitAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (!IsConfirming)
        {
            if (!DecimalInput.TryParseAmount(NewPrice, out var price))
            {
                ErrorMessage = DecimalInput.InvalidAmountMessage;
                return;
            }

            if (price == product.SalePrice)
            {
                ErrorMessage = "Le nouveau prix est identique au prix actuel.";
                return;
            }

            ErrorMessage = string.Empty;
            confirmedPrice = price;
            NewPriceText = Money.ToDisplay(price);
            IsConfirming = true;
            return;
        }

        SalePriceChangeResult? result = null;
        var sent = await RunWriteAsync(async operationId => result = await catalog.ChangeSalePriceAsync(
            product.Id, new ChangeSalePriceRequest(operationId, confirmedPrice), CancellationToken.None));
        if (sent)
        {
            await changed(result!);
        }
        else
        {
            // Refus : retour à la saisie. Panne de réseau : Entrée retente le même envoi.
            IsConfirming = OutcomeUnknown;
        }
    }
}
