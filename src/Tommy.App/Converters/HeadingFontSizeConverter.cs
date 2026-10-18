using System.Globalization;
using Avalonia.Data.Converters;

namespace Tommy.App.Converters;

/// <summary>Maps a markdown heading level (1..6) to a font size.</summary>
public sealed class HeadingFontSizeConverter : IValueConverter
{
    public static readonly HeadingFontSizeConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int level
            ? level switch { 1 => 24.0, 2 => 20.0, 3 => 17.0, 4 => 15.0, _ => 14.0 }
            : 14.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
