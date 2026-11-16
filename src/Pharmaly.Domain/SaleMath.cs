namespace Pharmaly.Domain;

/// <summary>
/// Calculs d'une vente, en <see cref="decimal"/> exact. Tout arrondi se fait au centime,
/// la demie arrondie en s'éloignant de zéro (0,005 devient 0,01).
/// </summary>
public static class SaleMath
{
    public const decimal MaxAmount = 9_999_999_999.99m;

    public static decimal RoundToCentime(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>Vrai si le montant est positif ou nul, borné, avec au plus deux décimales.</summary>
    public static bool IsValidAmount(decimal amount) =>
        amount is >= 0 and <= MaxAmount && decimal.Round(amount, 2) == amount;

    /// <summary>Total d'une ligne : quantité × prix unitaire, arrondi au centime.</summary>
    public static decimal LineTotal(decimal quantity, decimal unitPrice) => RoundToCentime(quantity * unitPrice);

    /// <summary>Montant d'une remise en pourcentage sur le total des lignes, arrondi au centime.</summary>
    public static decimal PercentDiscount(decimal subtotal, decimal percent) => RoundToCentime(subtotal * percent / 100m);
}
