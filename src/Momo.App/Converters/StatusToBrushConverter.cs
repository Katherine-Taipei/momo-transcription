using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Momo.App.Converters;

/// <summary>
/// Converts a revision status string to a background brush for the status badge.
/// "accepted" → green, "rejected" → red, "pending" (default) → muted blue/grey.
/// </summary>
public class StatusToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value?.ToString()?.ToLowerInvariant() switch
        {
            "accepted" => new SolidColorBrush(Color.Parse("#065F46")),
            "rejected" => new SolidColorBrush(Color.Parse("#7F1D1D")),
            "pending"  => new SolidColorBrush(Color.Parse("#1E3A5F")),
            _          => new SolidColorBrush(Color.Parse("#25273C")),
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
