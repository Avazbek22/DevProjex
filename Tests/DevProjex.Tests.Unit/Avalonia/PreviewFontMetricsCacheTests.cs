using Avalonia.Media;
using DevProjex.Avalonia.Controls;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class PreviewFontMetricsCacheTests
{
    [AvaloniaFact]
    public void GetMetrics_ReusesEntryForSameFontAndSize()
    {
        var cache = new PreviewFontMetricsCache();

        var first = cache.GetMetrics(FontFamily.Default, 15);
        var second = cache.GetMetrics(FontFamily.Default, 15);

        Assert.Equal(first, second);
        Assert.Equal(1, cache.MetricsEntryCount);
        Assert.True(first.LineHeight > 0);
        Assert.True(first.WideGlyphWidth > 0);
        Assert.True(first.SpaceWidth >= 0);
    }

    [AvaloniaFact]
    public void GetMetrics_SeparatesDifferentFontSizes()
    {
        var cache = new PreviewFontMetricsCache();

        var small = cache.GetMetrics(FontFamily.Default, 12);
        var large = cache.GetMetrics(FontFamily.Default, 24);

        Assert.Equal(2, cache.MetricsEntryCount);
        Assert.True(large.LineHeight > small.LineHeight);
        Assert.True(large.WideGlyphWidth > small.WideGlyphWidth);
    }

    [AvaloniaFact]
    public void GetSampleWidth_ReusesEntryAndSeparatesSampleText()
    {
        var cache = new PreviewFontMetricsCache();

        var oneDigit = cache.GetSampleWidth("8", FontFamily.Default, 15);
        var oneDigitAgain = cache.GetSampleWidth("8", FontFamily.Default, 15);
        var fourDigits = cache.GetSampleWidth("8888", FontFamily.Default, 15);

        Assert.Equal(oneDigit, oneDigitAgain);
        Assert.Equal(2, cache.SampleWidthEntryCount);
        Assert.True(fourDigits > oneDigit);
    }
}
