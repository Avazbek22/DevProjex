using DevProjex.Application.Preview;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class PreviewSelectionMetricsPolicyTests
{
    [Fact]
    public void TryGetCachedMetrics_FullTreeSelection_ReturnsTreeMetrics()
    {
        using var document = new InMemoryPreviewTextDocument("alpha\nbeta");
        var selection = new PreviewSelectionRange(1, 0, 2, 4);
        var treeMetrics = new ExportOutputMetrics(10, 20, 30);
        var contentMetrics = new ExportOutputMetrics(1, 2, 3);

        var result = PreviewSelectionMetricsPolicy.TryGetCachedMetrics(
            hasStatusMetricsSnapshot: true,
            selectedMode: PreviewContentMode.Tree,
            document,
            selection,
            treeMetrics,
            contentMetrics,
            out var metrics);

        Assert.True(result);
        Assert.Equal(treeMetrics, metrics);
    }

    [Fact]
    public void TryGetCachedMetrics_FullCombinedSelection_ReturnsSummedMetrics()
    {
        using var document = new InMemoryPreviewTextDocument("alpha\nbeta");
        var selection = new PreviewSelectionRange(1, 0, 2, 4);
        var treeMetrics = new ExportOutputMetrics(10, 20, 30);
        var contentMetrics = new ExportOutputMetrics(1, 2, 3);

        var result = PreviewSelectionMetricsPolicy.TryGetCachedMetrics(
            hasStatusMetricsSnapshot: true,
            selectedMode: PreviewContentMode.TreeAndContent,
            document,
            selection,
            treeMetrics,
            contentMetrics,
            out var metrics);

        Assert.True(result);
        Assert.Equal(new ExportOutputMetrics(11, 22, 33), metrics);
    }

    [Fact]
    public void TryGetCachedMetrics_PartialSelection_ReturnsFalse()
    {
        using var document = new InMemoryPreviewTextDocument("alpha\nbeta");
        var selection = new PreviewSelectionRange(1, 1, 2, 4);

        var result = PreviewSelectionMetricsPolicy.TryGetCachedMetrics(
            hasStatusMetricsSnapshot: true,
            selectedMode: PreviewContentMode.Content,
            document,
            selection,
            new ExportOutputMetrics(10, 20, 30),
            new ExportOutputMetrics(1, 2, 3),
            out var metrics);

        Assert.False(result);
        Assert.Equal(ExportOutputMetrics.Empty, metrics);
    }

    [Fact]
    public void FormatStatusMetricsText_CompactMode_ShowsOnlyLines()
    {
        var text = PreviewSelectionMetricsPolicy.FormatStatusMetricsText(
            new ExportOutputMetrics(12345, 67890, 1200),
            new StatusMetricLabels("Lines:", "Chars:", "≈Tokens:"),
            useCompactMode: true);

        var expectedValue = (12345 / 1_000.0).ToString("F1") + "K";
        Assert.Equal($"[Lines: {expectedValue}]", text);
    }

    [Fact]
    public void AddMetrics_ValuesBeyondInt32RemainExact()
    {
        var result = PreviewSelectionMetricsPolicy.AddMetrics(
            new ExportOutputMetrics(1_500_000_000, 2_000_000_000, 500_000_000),
            new ExportOutputMetrics(1_200_000_000, 1_500_000_000, 375_000_000));

        Assert.Equal(new ExportOutputMetrics(2_700_000_000, 3_500_000_000, 875_000_000), result);
    }
}
