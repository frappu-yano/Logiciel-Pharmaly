using System.Globalization;
using Avalonia.Data.Converters;
using Pharmaly.Domain;

namespace Pharmaly.Poste.Views;

public sealed class MoneyConverter : IValueConverter
{
    public static readonly MoneyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is decimal amount ? Money.ToDisplay(amount) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
