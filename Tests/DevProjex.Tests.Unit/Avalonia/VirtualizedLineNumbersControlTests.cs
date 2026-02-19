using Avalonia.Media;
using DevProjex.Avalonia.Controls;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class VirtualizedLineNumbersControlTests
{
    [Fact]
    public void Defaults_AreStable()
    {
        var control = new VirtualizedLineNumbersControl();

        Assert.Equal(1, control.LineCount);
        Assert.Equal(0, control.VerticalOffset, 3);
        Assert.Equal(10, control.TopPadding, 3);
        Assert.Equal(10, control.BottomPadding, 3);
        Assert.Equal(10, control.LeftPadding, 3);
        Assert.Equal(8, control.RightPadding, 3);
        Assert.Equal(15, control.NumberFontSize, 3);
        Assert.Equal(0, control.ExtentHeight, 3);
        Assert.Equal(0, control.ViewportHeight, 3);
    }

    [Theory]
    [InlineData(1000, 2020, 10, 10, 2.0)]
    [InlineData(500, 1510, 5, 5, 3.0)]
    [InlineData(200, 600, 0, 0, 3.0)]
    public void ResolveLineHeight_UsesExtentBasedHeight_WhenAvailable(
        int lineCount,
        double extentHeight,
        double topPadding,
        double bottomPadding,
        double expected)
    {
        var control = new VirtualizedLineNumbersControl
        {
            LineCount = lineCount,
            ExtentHeight = extentHeight,
            TopPadding = topPadding,
            BottomPadding = bottomPadding
        };

        var height = InvokeResolveLineHeight(control, lineCount);

        Assert.Equal(expected, height, 6);
    }

    [Fact]
    public void ResolveLineHeight_HandlesLargeLineCountsWithoutOverflow()
    {
        var control = new VirtualizedLineNumbersControl
        {
            LineCount = 500000,
            ExtentHeight = 5_000_020,
            TopPadding = 10,
            BottomPadding = 10
        };

        var height = InvokeResolveLineHeight(control, 500000);

        Assert.Equal(10.0, height, 6);
    }

    [Theory]
    [InlineData(1000, 0, 10, 10)]
    [InlineData(1000, 10, 10, 10)]
    [InlineData(1000, 100, 1000, 1000)]
    public void ResolveLineHeight_InvalidExtent_DoesNotUseExtentBranch(
        int lineCount,
        double extentHeight,
        double topPadding,
        double bottomPadding)
    {
        var control = new VirtualizedLineNumbersControl
        {
            LineCount = lineCount,
            ExtentHeight = extentHeight,
            TopPadding = topPadding,
            BottomPadding = bottomPadding
        };

        Assert.False(TryCalculateExtentLineHeight(control, lineCount, out _));
    }

    private static double InvokeResolveLineHeight(VirtualizedLineNumbersControl control, int lineCount)
    {
        var method = typeof(VirtualizedLineNumbersControl).GetMethod(
            "ResolveLineHeight",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var typeface = new Typeface(control.NumberFontFamily ?? FontFamily.Default, FontStyle.Normal, FontWeight.Normal);
        return (double)method!.Invoke(control, [lineCount, typeface])!;
    }

    private static bool TryCalculateExtentLineHeight(
        VirtualizedLineNumbersControl control,
        int totalLines,
        out double lineHeight)
    {
        lineHeight = 0;
        if (control.ExtentHeight <= 0 || totalLines <= 0)
            return false;

        var verticalPadding = Math.Max(0, control.TopPadding) + Math.Max(0, control.BottomPadding);
        var textHeight = control.ExtentHeight - verticalPadding;
        if (textHeight <= 0)
            return false;

        var extentLineHeight = textHeight / totalLines;
        if (extentLineHeight <= 0.25)
            return false;

        lineHeight = extentLineHeight;
        return true;
    }
}
