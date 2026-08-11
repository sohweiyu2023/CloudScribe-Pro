using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace CloudScribe.App.Design;

public sealed class GeometryPathConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string pathData || string.IsNullOrWhiteSpace(pathData))
        {
            return AvaloniaProperty.UnsetValue;
        }

        try
        {
            return Geometry.Parse(pathData);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
        {
            return AvaloniaProperty.UnsetValue;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        AvaloniaProperty.UnsetValue;
}
