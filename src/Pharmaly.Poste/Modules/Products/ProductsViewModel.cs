using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.Products;

/// <summary>Stock d'un produit à un emplacement.</summary>
public sealed record StockLineViewModel(string StoreName, string LocationName, string KindText, string QuantityText);

/// <summary>Mouvement du journal de stock, prêt à afficher.</summary>
public sealed record MovementLineViewModel(
    string DateText, string TypeText, string LocationName, string QuantityText, string BalanceText, string EmployeeName, string Note)
{
    /// <summary>Deuxième ligne : où, par qui, et le motif s'il y en a un.</summary>
    public string ContextText => Note.Length > 0 ? $"{LocationName} · {EmployeeName} · {Note}" : $"{LocationName} · {EmployeeName}";
}

/// <summary>Changement du prix de vente, prêt à afficher.</summary>
public sealed record PriceLineViewModel(string DateText, string OldPriceText, string NewPriceText, string EmployeeName);

/// <summary>Fiche d'un produit telle que le serveur vient de la renvoyer.</summary>
public sealed class ProductDetailViewModel(ProductDetail product)
{
    public ProductDetail Product { get; } = product;

    public Guid Id => Product.Id;

    public string Name => Product.Name;

    public string CodeText => $"Code interne {Product.Code}";

    public bool IsActive => Product.IsActive;

    public string StateText => Product.IsActive ? "Actif" : "Inactif";

    public string BrandText => Product.Brand ?? "—";

    public string CategoryText => Product.Category ?? "—";

    public string UnitText => Product.SoldByUnit ? "Oui" : "Non (quantités fractionnaires admises)";

    public string PriceText => Money.ToDisplay(Product.SalePrice);

    public string BarcodesText => Product.Barcodes.Count > 0 ? string.Join("   ", Product.Barcodes) : "—";

    public IReadOnlyList<StockLineViewModel> Stock { get; } = product.Stock
        .Select(line => new StockLineViewModel(
            line.StoreName, line.LocationName, StockLabels.LocationKind(line.LocationKind), Quantities.Format(line.Quantity)))
        .ToList();

    public IReadOnlyList<MovementLineViewModel> Movements { get; } = product.RecentMovements
        .Select(line => new MovementLineViewModel(
            Quantities.FormatDate(line.OccurredAt), StockLabels.MovementType(line.Type), line.LocationName,
            Quantities.FormatSigned(line.Quantity), Quantities.Format(line.BalanceAfter),
            string.IsNullOrWhiteSpace(line.EmployeeName) ? "—" : line.EmployeeName, line.Note ?? string.Empty))
        .ToList();

    public IReadOnlyList<PriceLineViewModel> Prices { get; } = product.SalePriceHistory
        .Select(line => new PriceLineViewModel(
            Quantities.FormatDate(line.ChangedAt),
            line.OldPrice is { } old ? Money.ToDisplay(old) : "Prix initial",
            Money.ToDisplay(line.NewPrice),
            string.IsNullOrWhiteSpace(line.EmployeeName) ? "—" : line.EmployeeName))
        .ToList();

    public bool HasNoMovement => Movements.Count == 0;
}

/// <summary>
/// Module Produits : recherche, fiche, création, modification, prix de vente, ajustement
/// d'inventaire et transfert. Le poste n'applique aucune règle : il envoie la demande et
/// affiche la réponse du serveur. Les droits ne servent qu'à masquer ce qui serait refusé.
/// </summary>
public sealed partial class ProductsViewModel : ModuleViewModel
{
    public const string NoViewRightMessage = "Vous n'avez pas le droit de consulter les produits.";

    private readonly ICatalogClient catalog;
    private readonly IStockClient stock;
    private readonly TimeSpan searchDelay;

    public ProductsViewModel(ICatalogClient catalog, IStockClient stock, Func<string, bool> hasPermission, TimeSpan searchDelay)
    {
        this.catalog = catalog;
        this.stock = stock;
        this.searchDelay = searchDelay;
        CanView = hasPermission(Permissions.ProductsView);
        CanCreate = hasPermission(Permissions.ProductsCreate);
        CanEdit = hasPermission(Permissions.ProductsEdit);
        CanChangePrice = hasPermission(Permissions.SalePriceEdit);
        CanAdjust = hasPermission(Permissions.StockAdjust);
        CanTransfer = hasPermission(Permissions.StockTransfer);
        Lookup = new ProductLookupViewModel(catalog, searchDelay, refused => RequireIdentificationIfLost(refused));
    }

    // Droits de l'employé identifié : chaque action n'est affichée que s'il a le droit.
    public bool CanView { get; }

    public bool CanCreate { get; }

    public bool CanEdit { get; }

    public bool CanChangePrice { get; }

    public bool CanAdjust { get; }

    public bool CanTransfer { get; }

    /// <summary>Vrai si au moins une action sur la fiche affichée est permise.</summary>
    public bool HasDetailActions => CanEdit || CanChangePrice || CanAdjust;

    /// <summary>Recherche et liste des résultats.</summary>
    public ProductLookupViewModel Lookup { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    [NotifyCanExecuteChangedFor(nameof(StartEditCommand), nameof(StartPriceChangeCommand), nameof(StartAdjustmentCommand))]
    private ProductDetailViewModel? detail;

    /// <summary>Formulaire ouvert par-dessus l'écran (création, modification, prix, ajustement, transfert).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveForm))]
    private ProductFormViewModel? activeForm;

    /// <summary>Confirmation d'une opération réussie.</summary>
    [ObservableProperty]
    private string infoMessage = string.Empty;

    public bool HasDetail => Detail is not null;

    public bool HasActiveForm => ActiveForm is not null;

    private bool CanStartEdit => CanEdit && HasDetail;

    private bool CanStartPriceChange => CanChangePrice && HasDetail;

    private bool CanStartAdjustment => CanAdjust && HasDetail;

    /// <summary>Entrée dans la recherche : ouvre la fiche du produit désigné.</summary>
    public async Task OpenFromSearchAsync()
    {
        if (await Lookup.ResolveAsync() is { } product)
        {
            await OpenProductAsync(product.Id);
        }
    }

    /// <summary>Ouvre la fiche du résultat sélectionné (clic dans la liste).</summary>
    public Task OpenSelectedAsync() =>
        Lookup.SelectedResult is { } row ? OpenProductAsync(row.Id) : Task.CompletedTask;

    public async Task<bool> OpenProductAsync(Guid productId)
    {
        InfoMessage = string.Empty;
        return await LoadDetailAsync(productId);
    }

    private Task<bool> LoadDetailAsync(Guid productId) => RunServerAsync(async () =>
        Detail = new ProductDetailViewModel(await catalog.GetProductAsync(productId, CancellationToken.None)));

    /// <summary>Après une écriture réussie : ferme le formulaire, relit la fiche et la liste, confirme.</summary>
    private async Task ShowWrittenAsync(Guid productId, string message)
    {
        ActiveForm = null;
        if (CanView && await LoadDetailAsync(productId))
        {
            await Lookup.RefreshAsync();
        }

        InfoMessage = message;
    }

    private void Open(ProductFormViewModel form)
    {
        ErrorMessage = string.Empty;
        InfoMessage = string.Empty;
        form.IdentificationRequired += (_, _) => RequireIdentification();
        ActiveForm = form;
    }

    private void CloseForm()
    {
        (ActiveForm as StockTransferViewModel)?.Stop();
        ActiveForm = null;
    }

    /// <summary>
    /// Ouvre la création d'un produit. Si un code-barres vient d'être scanné sans être trouvé,
    /// il est déjà inscrit dans le formulaire.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    public void StartCreate()
    {
        if (!CanCreate || HasActiveForm)
        {
            return;
        }

        Open(new ProductCreateViewModel(
            catalog, product => ShowWrittenAsync(product.Id, $"« {product.Name} » est créé."), CloseForm, Lookup.UnknownBarcode));
    }

    [RelayCommand(CanExecute = nameof(CanStartEdit))]
    public void StartEdit()
    {
        if (!CanStartEdit || HasActiveForm)
        {
            return;
        }

        Open(new ProductEditViewModel(
            catalog, Detail!.Product, product => ShowWrittenAsync(product.Id, $"« {product.Name} » est enregistré."), CloseForm));
    }

    [RelayCommand(CanExecute = nameof(CanStartPriceChange))]
    public void StartPriceChange()
    {
        if (!CanStartPriceChange || HasActiveForm)
        {
            return;
        }

        var product = Detail!.Product;
        Open(new SalePriceViewModel(
            catalog, product,
            result => ShowWrittenAsync(
                product.Id,
                $"Prix de vente de « {product.Name} » : {Money.ToDisplay(result.OldPrice)} → {Money.ToDisplay(result.NewPrice)}."),
            CloseForm));
    }

    [RelayCommand(CanExecute = nameof(CanStartAdjustment))]
    public void StartAdjustment()
    {
        if (!CanStartAdjustment || HasActiveForm)
        {
            return;
        }

        var product = Detail!.Product;
        Open(new StockAdjustmentViewModel(
            stock, product,
            (result, location) => ShowWrittenAsync(
                product.Id,
                $"Stock de « {product.Name} » à « {location.Label} » : {Quantities.Format(result.QuantityBefore)} → "
                + $"{Quantities.Format(result.QuantityAfter)} ({Quantities.FormatSigned(result.QuantityChange)})."),
            CloseForm));
    }

    /// <summary>Ouvre le transfert, après avoir demandé la liste des emplacements au serveur.</summary>
    [RelayCommand(CanExecute = nameof(CanTransfer))]
    public async Task StartTransferAsync()
    {
        if (!CanTransfer || HasActiveForm)
        {
            return;
        }

        InfoMessage = string.Empty;
        IReadOnlyList<StockLocationSummary> locations = [];
        if (!await RunServerAsync(async () => locations = await stock.ListLocationsAsync(CancellationToken.None)))
        {
            return;
        }

        Open(new StockTransferViewModel(catalog, stock, locations, searchDelay, RefreshAfterTransferAsync, CloseForm));
    }

    /// <summary>Le transfert reste ouvert sur son résultat ; la fiche et la liste derrière lui sont relues.</summary>
    private async Task RefreshAfterTransferAsync(StockTransferResult result)
    {
        if (Detail is { } shown && result.Lines.Any(line => line.ProductId == shown.Id))
        {
            await LoadDetailAsync(shown.Id);
        }

        await Lookup.RefreshAsync();
    }

    public override void Close()
    {
        Lookup.Stop();
        (ActiveForm as StockTransferViewModel)?.Stop();
    }
}
