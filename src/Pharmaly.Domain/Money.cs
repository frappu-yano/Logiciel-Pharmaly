using System.Globalization;

namespace Pharmaly.Domain;

public static class Money
{
    public const string CurrencySymbol = "DA";

    private static readonly NumberFormatInfo Format = new()
    {
        NumberGroupSeparator = " ",
        NumberDecimalSeparator = ",",
        NumberDecimalDigits = 2,
    };

    /// <summary>1250.5 devient « 1 250,50 DA ».</summary>
    public static string ToDisplay(decimal amount) =>
        $"{amount.ToString("N", Format)} {CurrencySymbol}";
}
