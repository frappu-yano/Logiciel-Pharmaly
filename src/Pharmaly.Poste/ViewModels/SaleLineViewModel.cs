using CommunityToolkit.Mvvm.ComponentModel;
using Pharmaly.Contracts;
using Pharmaly.Domain;

namespace Pharmaly.Poste.ViewModels;

public sealed partial class SaleLineViewModel(ProductSummary product) : ObservableObject
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
