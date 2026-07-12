using DevProjex.Application.Services;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class UiTestDriverStatusMetricsParsingTests
{
    [AvaloniaTheory]
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

    [AvaloniaTheory]
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

    [AvaloniaTheory]
    [InlineData("[Lines: 100.0M | Chars: 3000.0M | ~Tokens: 750.0M]", 100_000_000L, 3_000_000_000L, 750_000_000L)]
    [InlineData("[Lines: 2 400 000 006 | Chars: 3 000 000 015 | ~Tokens: 750 000 004]", 2_400_000_006L, 3_000_000_015L, 750_000_004L)]
    public void TryParseStatusMetrics_WorkspaceValuesBeyondInt32_ParsesCorrectly(
        string text,
        long expectedLines,
        long expectedChars,
        long expectedTokens)
    {
        var parsed = UiTestDriver.TryParseStatusMetrics(text, out var metrics);

        Assert.True(parsed);
        Assert.Equal(new ExportOutputMetrics(expectedLines, expectedChars, expectedTokens), metrics);
    }
}
