namespace DevProjex.Avalonia.Controls;

internal sealed class PreviewFontMetricsCache
{
    private readonly Dictionary<PreviewFontMetricsKey, PreviewFontMetrics> _metricsByKey = [];
    private readonly Dictionary<PreviewSampleWidthKey, double> _sampleWidthsByKey = [];

    internal int MetricsEntryCount => _metricsByKey.Count;

    internal int SampleWidthEntryCount => _sampleWidthsByKey.Count;

    public PreviewFontMetrics GetMetrics(FontFamily? fontFamily, double fontSize)
    {
        var key = PreviewFontMetricsKey.Create(fontFamily, fontSize);
        if (_metricsByKey.TryGetValue(key, out var metrics))
            return metrics;

        var typeface = CreateTypeface(fontFamily);
        metrics = new PreviewFontMetrics(
            LineHeight: MeasureText("8", typeface, fontSize).Height,
            WideGlyphWidth: MeasureText("W", typeface, fontSize).Width,
            SpaceWidth: MeasureText(" ", typeface, fontSize).Width);

        metrics = metrics.Normalize();
        _metricsByKey[key] = metrics;
        return metrics;
    }

    public double GetSampleWidth(string sampleText, FontFamily? fontFamily, double fontSize)
    {
        if (string.IsNullOrEmpty(sampleText))
            return 0;

        var key = PreviewSampleWidthKey.Create(sampleText, fontFamily, fontSize);
        if (_sampleWidthsByKey.TryGetValue(key, out var width))
            return width;

        var typeface = CreateTypeface(fontFamily);
        width = MeasureText(sampleText, typeface, fontSize).Width;
        _sampleWidthsByKey[key] = width;
        return width;
    }

    private static Typeface CreateTypeface(FontFamily? fontFamily) =>
        new(fontFamily ?? FontFamily.Default, FontStyle.Normal, FontWeight.Normal);

    private static FormattedText MeasureText(string text, Typeface typeface, double fontSize)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.White);
    }

    private readonly record struct PreviewFontMetricsKey(
        string FontFamilyName,
        double FontSize,
        string CultureName)
    {
        public static PreviewFontMetricsKey Create(FontFamily? fontFamily, double fontSize)
        {
            var resolvedFamily = fontFamily ?? FontFamily.Default;
            return new PreviewFontMetricsKey(
                resolvedFamily.Name,
                fontSize,
                CultureInfo.CurrentUICulture.Name);
        }
    }

    private readonly record struct PreviewSampleWidthKey(
        string SampleText,
        PreviewFontMetricsKey MetricsKey)
    {
        public static PreviewSampleWidthKey Create(string sampleText, FontFamily? fontFamily, double fontSize) =>
            new(sampleText, PreviewFontMetricsKey.Create(fontFamily, fontSize));
    }
}

internal readonly record struct PreviewFontMetrics(
    double LineHeight,
    double WideGlyphWidth,
    double SpaceWidth)
{
    public PreviewFontMetrics Normalize() =>
        new(
            LineHeight: Math.Max(1.0, LineHeight),
            WideGlyphWidth: Math.Max(1.0, WideGlyphWidth),
            SpaceWidth: Math.Max(0.0, SpaceWidth));
}
