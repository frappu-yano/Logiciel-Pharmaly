namespace Pharmaly.Domain;

/// <summary>
/// Remboursement d'un retour, en <see cref="decimal"/> exact, au prorata de ce qui a
/// réellement été payé (remise de la vente déduite).
/// <para>
/// Tout se calcule par cumul, pour que les arrondis ne puissent jamais faire rembourser
/// plus que le payé :
/// </para>
/// <para>
/// 1. Le total payé du ticket est réparti entre ses lignes : la part cumulée des k premières
/// lignes est « total × (somme de leurs montants) ÷ sous-total », arrondie au centime (D25) ;
/// la part d'une ligne est la différence de deux cumuls. La somme des parts vaut exactement
/// le total du ticket.
/// </para>
/// <para>
/// 2. Pour une ligne, le remboursement dû quand r unités sur q vendues ont été retournées
/// est « part × r ÷ q », arrondi au centime ; un retour rembourse la différence entre le dû
/// après ce retour et le dû avant. Tout retourner rembourse exactement la part de la ligne.
/// </para>
/// </summary>
public static class ReturnMath
{
    /// <summary>
    /// Montant réellement payé pour chaque ligne, dans l'ordre reçu. La somme vaut <paramref name="saleTotal"/>.
    /// </summary>
    public static IReadOnlyList<decimal> PaidPerLine(IReadOnlyList<decimal> lineTotals, decimal saleSubtotal, decimal saleTotal)
    {
        var shares = new decimal[lineTotals.Count];
        var cumulatedLines = 0m;
        var cumulatedPaid = 0m;
        for (var index = 0; index < lineTotals.Count; index++)
        {
            cumulatedLines += lineTotals[index];
            var paidSoFar = saleSubtotal == 0 ? 0m : SaleMath.RoundToCentime(saleTotal * cumulatedLines / saleSubtotal);
            shares[index] = paidSoFar - cumulatedPaid;
            cumulatedPaid = paidSoFar;
        }

        return shares;
    }

    /// <summary>Remboursement dû pour une ligne quand <paramref name="returnedQuantity"/> en a été retourné en tout.</summary>
    public static decimal DueForLine(decimal linePaid, decimal soldQuantity, decimal returnedQuantity) =>
        returnedQuantity >= soldQuantity
            ? linePaid
            : SaleMath.RoundToCentime(linePaid * returnedQuantity / soldQuantity);

    /// <summary>
    /// Remboursement d'un retour de <paramref name="quantity"/> sur une ligne dont
    /// <paramref name="alreadyReturned"/> a déjà été retourné.
    /// </summary>
    public static decimal RefundForLine(decimal linePaid, decimal soldQuantity, decimal alreadyReturned, decimal quantity) =>
        DueForLine(linePaid, soldQuantity, alreadyReturned + quantity) - DueForLine(linePaid, soldQuantity, alreadyReturned);
}
