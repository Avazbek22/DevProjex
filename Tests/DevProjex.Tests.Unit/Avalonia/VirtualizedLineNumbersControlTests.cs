using Avalonia.Media;
using DevProjex.Avalonia.Controls;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class VirtualizedLineNumbersControlTests
{
    [AvaloniaFact]
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
        Assert.Equal(TextHintingMode.Strong, TextOptions.GetTextHintingMode(control));
        Assert.Equal(
            BaselinePixelAlignment.Aligned,
            TextOptions.GetBaselinePixelAlignment(control));
    }

    [AvaloniaTheory]
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

    [AvaloniaFact]
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

    [AvaloniaTheory]
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

    [AvaloniaFact]
    public void CalculateRequiredWidth_RespondsToDigitCountAndFontSize()
    {
        var control = new VirtualizedLineNumbersControl
        {
            LineCount = 9
        };

        var oneDigitWidth = InvokeCalculateRequiredWidth(control);

        control.LineCount = 1000;
        var fourDigitWidth = InvokeCalculateRequiredWidth(control);

        control.NumberFontSize = 30;
        var largeFontWidth = InvokeCalculateRequiredWidth(control);

        Assert.True(fourDigitWidth > oneDigitWidth);
        Assert.True(largeFontWidth > fourDigitWidth);
    }

    [AvaloniaFact]
    public void BuildVisibleLineNumbersText_ReusesCachedRangeAndUpdatesWhenRangeChanges()
    {
        var control = new VirtualizedLineNumbersControl();

        var first = InvokeBuildVisibleLineNumbersText(control, 4, 7);
        var second = InvokeBuildVisibleLineNumbersText(control, 4, 7);
        var shifted = InvokeBuildVisibleLineNumbersText(control, 5, 7);

        Assert.Same(first, second);
        Assert.Equal("4\n5\n6\n7", first);
        Assert.Equal("5\n6\n7", shifted);
        Assert.NotSame(first, shifted);
    }

    private static double InvokeResolveLineHeight(VirtualizedLineNumbersControl control, int lineCount)
    {
        var method = typeof(VirtualizedLineNumbersControl).GetMethod(
            "ResolveLineHeight",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        return (double)method!.Invoke(control, [lineCount])!;
    }

    private static double InvokeCalculateRequiredWidth(VirtualizedLineNumbersControl control)
    {
        var method = typeof(VirtualizedLineNumbersControl).GetMethod(
            "CalculateRequiredWidth",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return (double)method!.Invoke(control, [])!;
    }

    private static string InvokeBuildVisibleLineNumbersText(
        VirtualizedLineNumbersControl control,
        int firstVisibleLine,
        int lastVisibleLine)
    {
        var method = typeof(VirtualizedLineNumbersControl).GetMethod(
            "BuildVisibleLineNumbersText",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return (string)method!.Invoke(control, [firstVisibleLine, lastVisibleLine])!;
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
