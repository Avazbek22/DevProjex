namespace DevProjex.Avalonia.Converters;

public sealed class TreeIndentConverter : IValueConverter
{
    public double IndentSize { get; set; } = 16;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value is int intValue ? intValue : 0;
        var indent = Math.Max(0, level) * IndentSize;
        return new GridLength(indent);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
