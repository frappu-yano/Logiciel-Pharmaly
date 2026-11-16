using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pharmaly.Contracts;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.Products;

/// <summary>
/// Création d'un produit. Formulaire autonome et réutilisable : le module Produits l'ouvre
/// avec Ctrl+N, et l'écran des bons fournisseurs peut l'ouvrir en « création rapide » quand
/// un code-barres inconnu est scanné.
/// <para>
/// Pour l'utiliser depuis un autre module : construire ce modèle de vue avec le client du
/// catalogue, le rappel <paramref name="created"/> (reçoit le produit créé), le rappel
/// <paramref name="cancelled"/> (Échap) et, si besoin, le code-barres déjà scanné ; le donner
/// comme <c>DataContext</c> à une <see cref="ProductCreateView"/> ; transmettre les touches à
/// <see cref="ProductCreateView.HandleKeyAsync"/> ; relayer <see cref="ProductFormViewModel.IdentificationRequired"/>.
/// </para>
/// </summary>
public sealed partial class ProductCreateViewModel : ProductFormViewModel
{
    private readonly ICatalogClient catalog;
    private readonly Func<ProductSummary, Task> created;

    public ProductCreateViewModel(
        ICatalogClient catalog, Func<ProductSummary, Task> created, Action cancelled, string? initialBarcode = null)
        : base(cancelled)
    {
        this.catalog = catalog;
        this.created = created;
        var scanned = (initialBarcode ?? string.Empty).Trim();
        Barcodes = new BarcodeListViewModel(scanned.Length > 0 ? [scanned] : []);
    }

    [ObservableProperty]
    private string name = string.Empty;

    [ObservableProperty]
    private string brand = string.Empty;

    [ObservableProperty]
    private string category = string.Empty;

    [ObservableProperty]
    private string salePrice = string.Empty;

    /// <summary>Vendu à l'unité : aucune quantité fractionnaire ne sera acceptée pour ce produit.</summary>
    [ObservableProperty]
    private bool soldByUnit = true;

    public BarcodeListViewModel Barcodes { get; }

    [RelayCommand]
    private void RemoveBarcode(string barcode) => Barcodes.Remove(barcode);

    /// <summary>Envoie la création. Le code-barres encore dans le champ de saisie est pris en compte.</summary>
    [RelayCommand]
    public async Task SubmitAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (!DecimalInput.TryParseAmount(SalePrice, out var price))
        {
            ErrorMessage = DecimalInput.InvalidAmountMessage;
            return;
        }

        Barcodes.AddEntry();
        ProductSummary? product = null;
        var sent = await RunWriteAsync(async operationId => product = await catalog.CreateProductAsync(
            new CreateProductRequest(
                operationId, Name.Trim(), Barcodes.Items.ToList(), NullIfEmpty(Brand), NullIfEmpty(Category), price, SoldByUnit),
            CancellationToken.None));
        if (sent)
        {
            await created(product!);
        }
    }

    internal static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
