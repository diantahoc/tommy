using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Tommy.App.Converters;

/// <summary>
/// True when the bound enum value equals the (string) parameter — used to light up
/// the matching radio item in an autonomy / effort flyout menu.
/// </summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null &&
        string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
