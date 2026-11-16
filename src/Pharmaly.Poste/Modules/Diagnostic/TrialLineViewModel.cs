using CommunityToolkit.Mvvm.ComponentModel;
using Pharmaly.Contracts;
using Pharmaly.Domain;

namespace Pharmaly.Poste.Modules.Diagnostic;

/// <summary>Ligne de l'écran d'essai. Ce n'est pas une ligne de vente : rien n'est enregistré.</summary>
public sealed partial class TrialLineViewModel(ProductSummary product) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineTotal), nameof(LineTotalText))]
    private int quantity = 1;

    public ProductSummary Product { get; } = product;

    public string Name => Product.Name;

    public string UnitPriceText => Money.ToDisplay(Product.SalePrice);

    public decimal LineTotal => Product.SalePrice * Quantity;

    public string LineTotalText => Money.ToDisplay(LineTotal);
}
