using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace DevProjex.Avalonia.Converters;

/// <summary>
/// Converts a boolean value to opacity (1.0 for true, 0.0 for false).
/// Used to hide elements while preserving their layout space.
/// </summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public static readonly BoolToOpacityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? 1.0 : 0.0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
