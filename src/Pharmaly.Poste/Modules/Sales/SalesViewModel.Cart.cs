using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.Sales;

// Panier : recherche, lecture de codes-barres, lignes, quantités, remise et totaux.
public sealed partial class SalesViewModel
{
    private readonly List<Task> pendingScans = [];
    private CancellationTokenSource? searchCancellation;

    /// <summary>Texte pour lequel <see cref="Results"/> a été calculé.</summary>
    private string resultsTerm = string.Empty;

    /// <summary>Ligne dont la quantité est en cours de saisie.</summary>
    private SaleLineViewModel? editedLine;

    private bool isCheckingQuantity;

    [ObservableProperty]
    private string query = string.Empty;

    [ObservableProperty]
    private ProductSummary? selectedResult;

    [ObservableProperty]
    private bool hasResults;

    [ObservableProperty]
    private SaleLineViewModel? selectedLine;

    /// <summary>Saisie directe de la quantité.</summary>
    [ObservableProperty]
    private string quantityText = string.Empty;

    [ObservableProperty]
    private string quantityTitle = string.Empty;

    /// <summary>Saisie de la remise : « 500 » (montant) ou « 10 % » (pourcentage).</summary>
    [ObservableProperty]
    private string discountInput = string.Empty;

    public ObservableCollection<ProductSummary> Results { get; } = [];

    public ObservableCollection<SaleLineViewModel> Lines { get; } = [];

    /// <summary>Type de la remise sur la vente entière (<see cref="DiscountKinds"/>), ou <c>null</c> sans remise.</summary>
    public string? DiscountKind { get; private set; }

    /// <summary>Valeur saisie : un montant ou un pourcentage selon <see cref="DiscountKind"/>.</summary>
    public decimal DiscountValue { get; private set; }

    // ---------- Totaux : mêmes calculs que le serveur (SaleMath) ----------

    public decimal Subtotal => Lines.Sum(line => line.LineTotal);

    public decimal DiscountAmount => DiscountKind switch
    {
        DiscountKinds.Percent => SaleMath.PercentDiscount(Subtotal, DiscountValue),
        DiscountKinds.Amount => DiscountValue,
        _ => 0m,
    };

    public decimal Total => Subtotal - DiscountAmount;

    public bool HasDiscount => DiscountKind is not null;

    /// <summary>Une remise ne dépasse jamais le total (D12) : le panier a diminué après la saisie de la remise.</summary>
    public bool IsDiscountTooLarge => DiscountAmount > Subtotal;

    public bool HasLines => Lines.Count > 0;

    public string SubtotalText => Money.ToDisplay(Subtotal);

    public string DiscountAmountText => $"− {Money.ToDisplay(DiscountAmount)}";

    public string DiscountTitle => DiscountKind == DiscountKinds.Percent ? $"Remise {SaleFormat.Percent(DiscountValue)}" : "Remise";

    public string TotalText => Money.ToDisplay(Total);

    public string LineCountText => Lines.Count switch
    {
        0 => "Panier vide",
        1 => "1 ligne",
        var count => $"{count} lignes",
    };

    /// <summary>Lignes dont la quantité dépasse le stock connu : signalé tout de suite, le serveur tranchera.</summary>
    public string StockWarning
    {
        get
        {
            var shortLines = Lines.Where(line => line.IsOverStock)
                .Select(line => $"« {line.Name} » (demandé {line.QuantityText}, en stock {line.StockText})")
                .ToList();
            return shortLines.Count == 0 ? string.Empty : "Stock insuffisant d'après le dernier stock connu : " + string.Join(" ; ", shortLines) + ".";
        }
    }

    private void RefreshTotals()
    {
        OnPropertyChanged(nameof(Subtotal));
        OnPropertyChanged(nameof(DiscountAmount));
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(HasDiscount));
        OnPropertyChanged(nameof(IsDiscountTooLarge));
        OnPropertyChanged(nameof(HasLines));
        OnPropertyChanged(nameof(SubtotalText));
        OnPropertyChanged(nameof(DiscountAmountText));
        OnPropertyChanged(nameof(DiscountTitle));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(LineCountText));
        OnPropertyChanged(nameof(StockWarning));
    }

    // ---------- Recherche et codes-barres ----------

    partial void OnQueryChanged(string value) => _ = SearchAfterDelayAsync(value);

    private void CancelSearch()
    {
        searchCancellation?.Cancel();
        searchCancellation = null;
    }

    private async Task SearchAfterDelayAsync(string term)
    {
        CancelSearch();
        if (string.IsNullOrWhiteSpace(term))
        {
            Results.Clear();
            resultsTerm = string.Empty;
            return;
        }

        var cancellation = searchCancellation = new CancellationTokenSource();
        try
        {
            await Task.Delay(searchDelay, cancellation.Token);
            await SearchNowAsync(term.Trim(), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Une frappe plus récente a remplacé cette recherche.
        }
    }

    /// <summary>Cherche tout de suite ; renvoie faux si le serveur a refusé ou n'a pas répondu.</summary>
    private async Task<bool> SearchNowAsync(string term, CancellationToken cancellationToken)
    {
        try
        {
            var found = await catalog.SearchProductsAsync(term, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Results.Clear();
            foreach (var product in found)
            {
                Results.Add(product);
            }

            resultsTerm = term;
            SelectedResult = Results.FirstOrDefault();
            return true;
        }
        catch (ServerRefusedException refused)
        {
            ErrorMessage = refused.Message;
            RequireIdentificationIfLost(refused);
            return false;
        }
        catch (Exception exception) when (IsUnreachable(exception) && !cancellationToken.IsCancellationRequested)
        {
            ErrorMessage = ServerUnreachableMessage;
            return false;
        }
    }

    public void MoveResultSelection(int delta)
    {
        if (Results.Count == 0)
        {
            return;
        }

        var current = SelectedResult is null ? -1 : Results.IndexOf(SelectedResult);
        SelectedResult = Results[Math.Clamp(current + delta, 0, Results.Count - 1)];
    }

    /// <summary>
    /// Entrée dans la recherche. Un code-barres (le lecteur termine par Entrée) ajoute le produit ;
    /// un nom ajoute le résultat choisi. Deux lectures rapprochées sont traitées toutes les deux.
    /// </summary>
    public Task SubmitQueryAsync()
    {
        var text = Query.Trim();
        if (!IsCartEditable || text.Length == 0)
        {
            return Task.CompletedTask;
        }

        ClearMessages();
        if (LooksLikeBarcode(text))
        {
            Query = string.Empty;
            return TrackScan(AddByBarcodeAsync(text));
        }

        if (Results.Count > 0 && SelectedResult is { } chosen && resultsTerm == text)
        {
            Query = string.Empty;
            AddProduct(chosen);
            return Task.CompletedTask;
        }

        return TrackScan(AddBySearchAsync(text));
    }

    /// <summary>Retient la lecture tant qu'elle n'a pas abouti (voir <see cref="EnterStepAsync"/>).</summary>
    private async Task TrackScan(Task scan)
    {
        pendingScans.Add(scan);
        try
        {
            await scan;
        }
        finally
        {
            pendingScans.Remove(scan);
        }
    }

    private async Task AddByBarcodeAsync(string barcode)
    {
        try
        {
            if (await catalog.FindByBarcodeAsync(barcode, CancellationToken.None) is { } product)
            {
                AddProduct(product);
            }
            else
            {
                ErrorMessage = $"Code-barres inconnu : {barcode}. Rien n'a été ajouté.";
            }
        }
        catch (ServerRefusedException refused)
        {
            ErrorMessage = refused.Message;
            RequireIdentificationIfLost(refused);
        }
        catch (Exception exception) when (IsUnreachable(exception))
        {
            ErrorMessage = $"{ServerUnreachableMessage} Le code {barcode} n'a pas été ajouté.";
        }
    }

    /// <summary>Entrée frappée avant l'arrivée des résultats : un seul produit trouvé est ajouté, sinon la liste s'affiche.</summary>
    private async Task AddBySearchAsync(string text)
    {
        CancelSearch();
        if (!await SearchNowAsync(text, CancellationToken.None))
        {
            return;
        }

        if (Results.Count == 1)
        {
            var only = Results[0];
            Query = string.Empty;
            AddProduct(only);
        }
        else if (Results.Count == 0)
        {
            // Un code-barres qui n'est pas fait que de chiffres n'est pas reconnu comme tel plus haut.
            try
            {
                if (await catalog.FindByBarcodeAsync(text, CancellationToken.None) is { } product)
                {
                    Query = string.Empty;
                    AddProduct(product);
                    return;
                }
            }
            catch (Exception exception) when (exception is ServerRefusedException || IsUnreachable(exception))
            {
                // La recherche par nom a déjà répondu : son résultat (aucun produit) est affiché.
            }

            ErrorMessage = $"Aucun produit trouvé pour « {text} ».";
        }
    }

    private static bool LooksLikeBarcode(string text) => text.Length >= 8 && text.All(char.IsAsciiDigit);

    /// <summary>Ajoute le produit, ou augmente la quantité de sa ligne : jamais deux lignes du même produit (D26).</summary>
    public void AddProduct(ProductSummary product)
    {
        var line = Lines.FirstOrDefault(existing => existing.ProductId == product.Id);
        if (line is null)
        {
            line = new SaleLineViewModel(product);
            Lines.Add(line);
        }
        else
        {
            line.Product = product;
            line.Quantity = Math.Min(line.Quantity + 1, SaleInput.MaxQuantity);
        }

        SelectedLine = line;
        RefreshTotals();
    }

    // ---------- Lignes ----------

    public void MoveLineSelection(int delta)
    {
        if (!IsCartEditable || Lines.Count == 0)
        {
            return;
        }

        var current = SelectedLine is null ? -1 : Lines.IndexOf(SelectedLine);
        SelectedLine = Lines[Math.Clamp(current + delta, 0, Lines.Count - 1)];
    }

    [RelayCommand]
    public void IncreaseSelectedLine()
    {
        if (IsCartEditable && SelectedLine is { } line && line.Quantity + 1 <= SaleInput.MaxQuantity)
        {
            ClearMessages();
            line.Quantity += 1;
            RefreshTotals();
        }
    }

    /// <summary>La quantité ne descend pas à zéro : retirer une ligne est un geste à part (Suppr).</summary>
    [RelayCommand]
    public void DecreaseSelectedLine()
    {
        if (IsCartEditable && SelectedLine is { } line && line.Quantity - 1 > 0)
        {
            ClearMessages();
            line.Quantity -= 1;
            RefreshTotals();
        }
    }

    [RelayCommand]
    public void RemoveSelectedLine()
    {
        if (!IsCartEditable || SelectedLine is not { } line)
        {
            return;
        }

        ClearMessages();
        var index = Lines.IndexOf(line);
        Lines.Remove(line);
        SelectedLine = Lines.Count == 0 ? null : Lines[Math.Min(index, Lines.Count - 1)];
        RefreshTotals();
    }

    /// <summary>
    /// Attend les lectures de codes-barres encore en route : une touche (+, −, Suppr…) frappée
    /// juste après un scan porte sur la ligne que ce scan vient d'ajouter.
    /// </summary>
    public async Task WaitForPendingScansAsync()
    {
        while (pendingScans.Count > 0)
        {
            await Task.WhenAll(pendingScans.ToArray());
            pendingScans.RemoveAll(scan => scan.IsCompleted);
        }
    }

    /// <summary>
    /// Avant d'ouvrir une étape, les lectures de codes-barres encore en route sont attendues :
    /// le total ne change jamais sous les yeux du caissier pendant un encaissement.
    /// </summary>
    private async Task<bool> EnterStepAsync()
    {
        if (!IsCartEditable)
        {
            return false;
        }

        await WaitForPendingScansAsync();
        if (!IsCartEditable)
        {
            return false;
        }

        CancelSearch();
        Query = string.Empty;
        StepError = string.Empty;
        return true;
    }

    private void CloseStep()
    {
        StepError = string.Empty;
        Step = CheckoutStep.None;
    }

    // ---------- Quantité saisie directement ----------

    [RelayCommand]
    public async Task BeginQuantityEditAsync()
    {
        if (SelectedLine is null || !await EnterStepAsync() || SelectedLine is not { } line)
        {
            return;
        }

        ClearMessages();
        editedLine = line;
        QuantityTitle = line.Name;
        QuantityText = line.QuantityText;
        Step = CheckoutStep.Quantity;
    }

    [RelayCommand]
    public async Task ConfirmQuantityAsync()
    {
        if (Step != CheckoutStep.Quantity || editedLine is not { } line || isCheckingQuantity)
        {
            return;
        }

        if (!SaleInput.TryParseQuantity(QuantityText, out var quantity))
        {
            StepError = "Quantité invalide : saisissez un nombre positif (virgule ou point acceptés).";
            return;
        }

        if (quantity != decimal.Truncate(quantity) && !await AllowsFractionAsync(line))
        {
            return;
        }

        line.Quantity = quantity;
        editedLine = null;
        CloseStep();
        RefreshTotals();
    }

    /// <summary>Pas de fraction pour un produit vendu à l'unité. La fiche du produit n'est lue qu'à ce moment.</summary>
    private async Task<bool> AllowsFractionAsync(SaleLineViewModel line)
    {
        if (line.SoldByUnit is null)
        {
            isCheckingQuantity = true;
            try
            {
                line.SoldByUnit = await sales.IsSoldByUnitAsync(line.ProductId, CancellationToken.None);
            }
            catch (ServerRefusedException refused)
            {
                StepError = refused.Message;
                RequireIdentificationIfLost(refused);
                return false;
            }
            catch (Exception exception) when (IsUnreachable(exception))
            {
                StepError = "Serveur injoignable : impossible de vérifier si ce produit se vend au détail.";
                return false;
            }
            finally
            {
                isCheckingQuantity = false;
            }
        }

        if (line.SoldByUnit == true)
        {
            StepError = $"« {line.Name} » se vend à l'unité : la quantité doit être un nombre entier.";
            return false;
        }

        return true;
    }

    // ---------- Remise sur la vente entière ----------

    [RelayCommand]
    public async Task BeginDiscountEditAsync()
    {
        if (!CanDiscount || !await EnterStepAsync())
        {
            return;
        }

        ClearMessages();
        DiscountInput = DiscountKind switch
        {
            DiscountKinds.Percent => SaleFormat.Percent(DiscountValue),
            DiscountKinds.Amount => DiscountValue.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ','),
            _ => string.Empty,
        };
        Step = CheckoutStep.Discount;
    }

    /// <summary>Entrée sur la remise. Vide ou zéro : la remise est retirée.</summary>
    [RelayCommand]
    public void ConfirmDiscount()
    {
        if (Step != CheckoutStep.Discount || !CanDiscount)
        {
            return;
        }

        if (!SaleInput.TryParseDiscount(DiscountInput, out var kind, out var value))
        {
            StepError = "Remise invalide : un montant (500 ou 500,50) ou un pourcentage de 0 à 100 (10 %).";
            return;
        }

        if (kind == DiscountKinds.Amount && value > Subtotal)
        {
            StepError = $"La remise ({Money.ToDisplay(value)}) dépasse le total de la vente ({Money.ToDisplay(Subtotal)}).";
            return;
        }

        DiscountKind = kind;
        DiscountValue = kind is null ? 0m : value;
        CloseStep();
        RefreshTotals();
    }

    // ---------- Abandon du panier ----------

    [RelayCommand]
    public async Task RequestAbandonAsync()
    {
        if (Lines.Count > 0 && await EnterStepAsync())
        {
            ClearMessages();
            Step = CheckoutStep.Abandon;
        }
    }

    [RelayCommand]
    public void ConfirmAbandon()
    {
        if (Step == CheckoutStep.Abandon)
        {
            ClearCart();
            CloseStep();
        }
    }

    private void ClearCart()
    {
        Lines.Clear();
        SelectedLine = null;
        DiscountKind = null;
        DiscountValue = 0m;
        Customer = null;
        RefreshTotals();
    }

    /// <summary>
    /// Relit prix et stock de chaque ligne sur le serveur. Renvoie faux si tout n'a pas pu être relu.
    /// </summary>
    private async Task<bool> ReloadProductsAsync()
    {
        try
        {
            foreach (var line in Lines.ToList())
            {
                var code = line.Product.Code.ToString(CultureInfo.InvariantCulture);
                var fresh = (await catalog.SearchProductsAsync(code, CancellationToken.None))
                    .FirstOrDefault(product => product.Id == line.ProductId);
                if (fresh is null && line.Product.Barcode is { } barcode
                    && await catalog.FindByBarcodeAsync(barcode, CancellationToken.None) is { } scanned && scanned.Id == line.ProductId)
                {
                    fresh = scanned;
                }

                if (fresh is not null)
                {
                    line.Product = fresh;
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is ServerRefusedException || IsUnreachable(exception))
        {
            return false;
        }
        finally
        {
            RefreshTotals();
        }
    }
}
