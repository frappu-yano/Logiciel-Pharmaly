using CommunityToolkit.Mvvm.ComponentModel;
using Pharmaly.Contracts;
using Pharmaly.Domain;

namespace Pharmaly.Poste.Modules.Sales;

/// <summary>
/// Ligne d'un ticket à retourner : ce que le serveur dit de la ligne (vendu, déjà retourné,
/// encore retournable, payé) et la quantité que l'employé rend. Le poste ne calcule aucun
/// montant : le remboursement vient de l'aperçu du serveur.
/// </summary>
public sealed partial class ReturnLineViewModel(ReturnableLine line) : ObservableObject
{
    public ReturnableLine Line { get; } = line;

    public Guid ProductId => Line.ProductId;

    public string Name => Line.ProductName;

    public string SoldText => SaleFormat.Quantity(Line.SoldQuantity);

    public string ReturnedText => SaleFormat.Quantity(Line.ReturnedQuantity);

    public string ReturnableText => SaleFormat.Quantity(Line.ReturnableQuantity);

    public string PaidText => Money.ToDisplay(Line.PaidAmount);

    /// <summary>Il reste quelque chose à retourner sur cette ligne : sa quantité est modifiable.</summary>
    public bool IsReturnable => Line.ReturnableQuantity > 0;

    /// <summary>Quantité rendue, telle que saisie ; 0 par défaut.</summary>
    [ObservableProperty]
    private string quantityText = "0";

    /// <summary>La saisie de cette ligne a été refusée : elle est marquée jusqu'à la prochaine frappe.</summary>
    [ObservableProperty]
    private bool hasError;

    /// <summary>Vrai ou faux une fois la fiche du produit lue ; <c>null</c> tant qu'on ne le sait pas.</summary>
    public bool? SoldByUnit { get; set; }

    partial void OnQuantityTextChanged(string value) => HasError = false;

    /// <summary>Lit la quantité saisie : vide ou 0 pour ne rien rendre, sinon une quantité positive (3 décimales au plus).</summary>
    public bool TryReadQuantity(out decimal quantity)
    {
        quantity = 0;
        var cleaned = QuantityText.Trim();
        if (cleaned.Length == 0)
        {
            return true;
        }

        if (SaleInput.TryParseQuantity(cleaned, out quantity))
        {
            return true;
        }

        // « 0 », « 0,0 » : rien n'est rendu sur cette ligne.
        return SaleInput.TryParseAmount(cleaned, out var zero) && zero == 0;
    }
}
