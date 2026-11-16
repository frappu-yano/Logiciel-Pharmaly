using System.Globalization;
using Pharmaly.Domain;

namespace Pharmaly.Poste.Modules.Products;

/// <summary>
/// Lecture des montants et des quantités saisis au clavier : virgule ou point, espaces de
/// milliers tolérés, rien d'autre. Une saisie douteuse est refusée plutôt que devinée.
/// </summary>
public static class DecimalInput
{
    public const int AmountDecimals = 2;
    public const int QuantityDecimals = 3;

    // Bornes des colonnes numeric(12,2) et numeric(12,3) du serveur.
    private const int MaxIntegerDigits = 9;

    public const string InvalidAmountMessage =
        "Montant invalide : saisissez un nombre positif avec au plus deux décimales, par exemple 1250,50.";

    /// <summary>Lit un nombre décimal. <paramref name="allowSign"/> accepte un « + » ou un « - » en tête.</summary>
    public static bool TryParse(string? text, int maxDecimals, bool allowSign, out decimal value)
    {
        value = 0;
        var cleaned = (text ?? string.Empty).Trim().Replace(" ", string.Empty).Replace(" ", string.Empty).Replace(" ", string.Empty);
        var negative = false;
        if (allowSign && cleaned.Length > 0 && cleaned[0] is '+' or '-' or '−')
        {
            negative = cleaned[0] != '+';
            cleaned = cleaned[1..];
        }

        var parts = cleaned.Split(',', '.');
        if (parts.Length > 2 || parts[0].Length is 0 or > MaxIntegerDigits || !parts[0].All(char.IsAsciiDigit))
        {
            return false;
        }

        var decimals = parts.Length == 2 ? parts[1] : string.Empty;
        if ((parts.Length == 2 && decimals.Length == 0) || !decimals.All(char.IsAsciiDigit))
        {
            return false;
        }

        // Des zéros en trop (« 12,500 » pour un montant) ne changent pas la valeur : ils sont admis.
        if (decimals.TrimEnd('0').Length > maxDecimals)
        {
            return false;
        }

        var invariant = decimals.Length == 0 ? parts[0] : $"{parts[0]}.{decimals}";
        if (!decimal.TryParse(invariant, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        value = negative ? -parsed : parsed;
        return true;
    }

    /// <summary>Montant positif ou nul, deux décimales au plus.</summary>
    public static bool TryParseAmount(string? text, out decimal amount) =>
        TryParse(text, AmountDecimals, allowSign: false, out amount);

    /// <summary>
    /// Quantité : entière pour un produit vendu à l'unité, trois décimales au plus sinon.
    /// Renvoie le message à afficher quand la saisie est refusée, <c>null</c> quand elle est lue.
    /// </summary>
    public static string? ReadQuantity(string? text, bool soldByUnit, bool allowSign, out decimal quantity)
    {
        if (!TryParse(text, QuantityDecimals, allowSign, out quantity))
        {
            return allowSign
                ? "Quantité invalide : saisissez un nombre, précédé de - pour retirer du stock (par exemple -2)."
                : "Quantité invalide : saisissez un nombre positif, par exemple 12.";
        }

        if (soldByUnit && quantity != decimal.Truncate(quantity))
        {
            return "Ce produit se vend à l'unité : la quantité doit être un nombre entier.";
        }

        return null;
    }
}

/// <summary>Affichage des quantités : « 12 », « 1,5 », et avec signe « +3 », « −2 ».</summary>
public static class Quantities
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public static string Format(decimal quantity) =>
        (quantity < 0 ? "−" : string.Empty) + Math.Abs(quantity).ToString("0.###", French);

    public static string FormatSigned(decimal quantity) =>
        quantity > 0 ? "+" + Format(quantity) : Format(quantity);

    /// <summary>Date et heure locales du poste.</summary>
    public static string FormatDate(DateTimeOffset moment) => moment.ToLocalTime().ToString("dd/MM/yyyy HH:mm", French);
}

/// <summary>Libellés français des codes du journal de stock.</summary>
public static class StockLabels
{
    public static string MovementType(string type) => type switch
    {
        StockMovementTypes.NoteEntry => "Entrée par bon",
        StockMovementTypes.NoteCorrection => "Correction de bon",
        StockMovementTypes.NoteCancellation => "Annulation de bon",
        StockMovementTypes.Sale => "Vente",
        StockMovementTypes.SaleCancellation => "Annulation de vente",
        StockMovementTypes.Return => "Retour client",
        StockMovementTypes.TransferOut => "Transfert (sortie)",
        StockMovementTypes.TransferIn => "Transfert (entrée)",
        StockMovementTypes.Adjustment => "Ajustement d'inventaire",
        _ => type,
    };

    public static string LocationKind(string kind) => kind switch
    {
        StockLocationKinds.Sellable => "Vendable",
        StockLocationKinds.SetAside => "Mis à l'écart",
        _ => kind,
    };
}
