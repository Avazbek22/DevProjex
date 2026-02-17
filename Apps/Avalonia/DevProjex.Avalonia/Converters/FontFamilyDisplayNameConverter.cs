using System.Globalization;
using Avalonia.Data.Converters;

namespace DevProjex.Avalonia.Converters;

public sealed class FontFamilyDisplayNameConverter : IValueConverter, IMultiValueConverter
{
    public static readonly FontFamilyDisplayNameConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var defaultText = parameter as string ?? "Default";

        if (value is not FontFamily family)
            return defaultText;

        var name = family.Name ?? string.Empty;
        if (name.StartsWith("$", StringComparison.Ordinal))
            return defaultText;

        return name;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value ?? (parameter as string ?? "Default");

    // IMultiValueConverter implementation for use with MultiBinding
    // values[0] = FontFamily, values[1] = localized default text
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var defaultText = values.Count > 1 && values[1] is string text ? text : "Default";

        if (values.Count == 0 || values[0] is not FontFamily family)
            return defaultText;

        var name = family.Name ?? string.Empty;
        if (name.StartsWith("$", StringComparison.Ordinal))
            return defaultText;

        return name;
    }
}
