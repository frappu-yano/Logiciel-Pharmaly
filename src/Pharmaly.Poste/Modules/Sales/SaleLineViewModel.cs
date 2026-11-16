using CommunityToolkit.Mvvm.ComponentModel;
using Pharmaly.Contracts;
using Pharmaly.Domain;

namespace Pharmaly.Poste.Modules.Sales;

/// <summary>
/// Ligne du panier : un produit, une quantité. Le prix affiché est le dernier prix connu du
/// serveur ; c'est le serveur qui fixe le prix au moment de la vente.
/// </summary>
public sealed partial class SaleLineViewModel(ProductSummary product) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnitPrice), nameof(UnitPriceText), nameof(LineTotal), nameof(LineTotalText), nameof(SellableStock),
        nameof(StockText), nameof(IsOverStock))]
    private ProductSummary product = product;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuantityText), nameof(LineTotal), nameof(LineTotalText), nameof(IsOverStock))]
    private decimal quantity = 1;

    /// <summary>Vrai ou faux une fois la fiche du produit lue ; <c>null</c> tant qu'on ne le sait pas.</summary>
    public bool? SoldByUnit { get; set; }

    public Guid ProductId => Product.Id;

    public string Name => Product.Name;

    public decimal UnitPrice => Product.SalePrice;

    public string UnitPriceText => Money.ToDisplay(UnitPrice);

    public string QuantityText => SaleFormat.Quantity(Quantity);

    /// <summary>Même calcul que le serveur : quantité × prix, arrondi au centime.</summary>
    public decimal LineTotal => SaleMath.LineTotal(Quantity, UnitPrice);

    public string LineTotalText => Money.ToDisplay(LineTotal);

    /// <summary>Stock vendable du site, tel que le serveur l'a donné à la dernière lecture du produit.</summary>
    public decimal SellableStock => Product.SellableStock;

    public string StockText => SaleFormat.Quantity(SellableStock);

    /// <summary>La quantité demandée dépasse le stock connu : le serveur refusera la vente.</summary>
    public bool IsOverStock => Quantity > SellableStock;
}
