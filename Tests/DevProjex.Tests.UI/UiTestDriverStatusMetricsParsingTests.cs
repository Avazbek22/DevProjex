using DevProjex.Application.Services;

namespace DevProjex.Tests.UI;

public sealed class UiTestDriverStatusMetricsParsingTests
{
    [Theory]
    [InlineData("[Lines: 23 | Chars: 4,698 | ~Tokens: 1,175]", 23, 4698, 1175)]
    [InlineData("[Lines: 23 | Chars: 4 698 | ~Tokens: 1 175]", 23, 4698, 1175)]
    [InlineData("[Lines: 23 | Chars: 4\u00A0698 | ~Tokens: 1\u00A0175]", 23, 4698, 1175)]
    public void TryParseStatusMetrics_GroupedIntegersAcrossCultures_ParsesCorrectly(
        string text,
        int expectedLines,
        int expectedChars,
        int expectedTokens)
    {
        var parsed = UiTestDriver.TryParseStatusMetrics(text, out var metrics);

        Assert.True(parsed);
        Assert.Equal(new ExportOutputMetrics(expectedLines, expectedChars, expectedTokens), metrics);
    }

    [Theory]
    [InlineData("[Lines: 12.3K | Chars: 1.5M | ~Tokens: 2.5K]", 12300, 1500000, 2500)]
    [InlineData("[Lines: 12,3K | Chars: 1,5M | ~Tokens: 2,5K]", 12300, 1500000, 2500)]
    public void TryParseStatusMetrics_CompactMetricSuffixes_ParsesCorrectly(
        string text,
        int expectedLines,
        int expectedChars,
        int expectedTokens)
    {
        var parsed = UiTestDriver.TryParseStatusMetrics(text, out var metrics);

        Assert.True(parsed);
        Assert.Equal(new ExportOutputMetrics(expectedLines, expectedChars, expectedTokens), metrics);
    }
}
