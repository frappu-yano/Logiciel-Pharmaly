using System.Globalization;

namespace Pharmaly.Poste.Modules.SupplierNotes;

/// <summary>
/// Lecture et écriture des quantités, des prix et des dates saisis au clavier. La virgule et
/// le point sont acceptés ; une saisie invalide donne un message, jamais une valeur devinée.
/// </summary>
public static class NoteInput
{
    /// <summary>
    /// Bornes de l'écran, plus basses que celles du serveur : un code-barres lu par erreur
    /// dans un champ de quantité ou de prix (8 chiffres au moins) est refusé au lieu de
    /// devenir une quantité ou un prix.
    /// </summary>
    public const decimal MaxQuantity = 999_999.999m;
    public const decimal MaxPrice = 9_999_999.99m;

    public const string InvalidQuantity = "Quantité invalide : saisissez un nombre supérieur à zéro (exemple : 12 ou 2,5).";
    public const string InvalidPrice = "Prix invalide : saisissez un montant positif ou nul, avec au plus deux décimales (exemple : 1250,50).";
    public const string InvalidDate = "Date invalide : saisissez-la sous la forme jj/mm/aaaa (exemple : 05/11/2026).";

    private static readonly NumberFormatInfo Comma = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = string.Empty };

    private static readonly string[] DateFormats =
        ["d/M/yyyy", "d/M/yy", "d-M-yyyy", "d-M-yy", "d.M.yyyy", "d.M.yy", "ddMMyyyy", "ddMMyy", "yyyy-M-d"];

    /// <summary>Renvoie le message d'erreur à afficher, ou <c>null</c> si la quantité est acceptée.</summary>
    public static string? ReadQuantity(string text, bool soldByUnit, string productName, out decimal quantity)
    {
        if (!TryReadDecimal(text, 3, out quantity) || quantity <= 0 || quantity > MaxQuantity)
        {
            return InvalidQuantity;
        }

        return soldByUnit && quantity != decimal.Truncate(quantity)
            ? $"« {productName} » se vend à l'unité : saisissez une quantité entière."
            : null;
    }

    /// <summary>Renvoie le message d'erreur à afficher, ou <c>null</c> si le prix est accepté.</summary>
    public static string? ReadPrice(string text, out decimal price) =>
        TryReadDecimal(text, 2, out price) && price <= MaxPrice ? null : InvalidPrice;

    /// <summary>Nombre positif ou nul, virgule ou point, espaces de milliers tolérés.</summary>
    public static bool TryReadDecimal(string? text, int maxDecimals, out decimal value)
    {
        value = 0;
        var cleaned = new string((text ?? string.Empty).Where(character => !char.IsWhiteSpace(character)).ToArray()).Replace(',', '.');
        if (cleaned.Length is 0 or > 16
            || !cleaned.All(character => char.IsAsciiDigit(character) || character == '.')
            || !cleaned.Any(char.IsAsciiDigit)
            || cleaned.Count(character => character == '.') > 1)
        {
            return false;
        }

        return decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
            && decimal.Round(value, maxDecimals) == value;
    }

    public static bool TryReadDate(string? text, out DateOnly date) =>
        DateOnly.TryParseExact((text ?? string.Empty).Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>15 devient « 15 », 2.5 devient « 2,5 ».</summary>
    public static string FormatQuantity(decimal quantity) => quantity.ToString("0.###", Comma);

    /// <summary>Prix tel qu'il est proposé dans un champ de saisie : 1250.5 devient « 1250,50 ».</summary>
    public static string FormatPriceForEntry(decimal price) => price.ToString("0.00", Comma);

    public static string FormatPercent(decimal percent) => $"{percent.ToString("0.##", Comma)} %";

    public static string FormatDate(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    public static string FormatMoment(DateTimeOffset moment) =>
        moment.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Un code lu par le lecteur : uniquement des chiffres, six au moins.</summary>
    public static bool LooksLikeBarcode(string text) => text.Length >= 6 && text.All(char.IsAsciiDigit);
}
