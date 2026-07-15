using Avalonia.Media;
using DevProjex.Application.Preview;
using DevProjex.Avalonia.Controls;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class VirtualizedPreviewTextControlTests
{
    [Fact]
    public void DetachedConstruction_DoesNotRequirePlatformCursorFactory()
    {
        var control = new VirtualizedPreviewTextControl();

        Assert.True(control.Focusable);
        Assert.Null(control.Cursor);
    }

    [AvaloniaFact]
    public void SelectAll_WithDocument_SelectsFullNormalizedTextAndRange()
    {
        using var document = new InMemoryPreviewTextDocument("alpha\r\nbeta\ngamma");
        var control = new VirtualizedPreviewTextControl
        {
            Document = document
        };

        var changeCount = 0;
        control.PreviewSelectionChanged += (_, _) => changeCount++;

        control.SelectAll();

        Assert.True(control.HasSelection);
        Assert.Equal("alpha\nbeta\ngamma", control.GetSelectedText());
        Assert.True(control.TryGetSelectionRange(out var selectionRange));
        Assert.Equal(new PreviewSelectionRange(1, 0, 3, 5), selectionRange);
        Assert.Equal(1, changeCount);
    }

    [AvaloniaFact]
    public void SelectAll_WithTextFallback_SelectsEntireText()
    {
        var control = new VirtualizedPreviewTextControl
        {
            Text = "one\r\ntwo"
        };

        control.SelectAll();

        Assert.True(control.HasSelection);
        Assert.Equal("one\ntwo", control.GetSelectedText());
        Assert.True(control.TryGetSelectionRange(out var selectionRange));
        Assert.Equal(new PreviewSelectionRange(1, 0, 2, 3), selectionRange);
    }

    [AvaloniaFact]
    public void ClearSelection_AfterSelectAll_RemovesSelectionAndRaisesEvent()
    {
        using var document = new InMemoryPreviewTextDocument("alpha\nbeta");
        var control = new VirtualizedPreviewTextControl
        {
            Document = document
        };

        var changeCount = 0;
        control.PreviewSelectionChanged += (_, _) => changeCount++;

        control.SelectAll();
        control.ClearSelection();

        Assert.False(control.HasSelection);
        Assert.False(control.TryGetSelectionRange(out _));
        Assert.Equal(string.Empty, control.GetSelectedText());
        Assert.Equal(2, changeCount);
    }

    [AvaloniaFact]
    public void ChangingDocument_ClearsExistingSelection()
    {
        using var firstDocument = new InMemoryPreviewTextDocument("alpha\nbeta");
        using var secondDocument = new InMemoryPreviewTextDocument("gamma");
        var control = new VirtualizedPreviewTextControl
        {
            Document = firstDocument
        };

        control.SelectAll();
        Assert.True(control.HasSelection);

        control.Document = secondDocument;

        Assert.False(control.HasSelection);
        Assert.False(control.TryGetSelectionRange(out _));
        Assert.Equal(string.Empty, control.GetSelectedText());
    }

    [AvaloniaFact]
    public void SelectAll_WithEmptyDocument_LeavesSelectionEmpty()
    {
        using var document = new InMemoryPreviewTextDocument(string.Empty);
        var control = new VirtualizedPreviewTextControl
        {
            Document = document
        };

        control.SelectAll();

        Assert.False(control.HasSelection);
        Assert.False(control.TryGetSelectionRange(out _));
    }

    [AvaloniaFact]
    public void GetLineNumberAtVerticalOffset_RecalculatesMetricsWhenFontSizeChanges()
    {
        var control = new VirtualizedPreviewTextControl
        {
            Text = "one\ntwo\nthree",
            TopPadding = 0,
            TextFontSize = 10
        };

        var smallLineHeight = InvokeResolveLineHeight(control);
        Assert.Equal(2, control.GetLineNumberAtVerticalOffset(smallLineHeight + 0.1));

        control.TextFontSize = 30;

        var largeLineHeight = InvokeResolveLineHeight(control);
        Assert.True(largeLineHeight > smallLineHeight);
        Assert.Equal(1, control.GetLineNumberAtVerticalOffset(smallLineHeight + 0.1));
        Assert.Equal(2, control.GetLineNumberAtVerticalOffset(largeLineHeight + 0.1));
    }

    [AvaloniaFact]
    public void HugeDocumentOffset_MapsToExpectedLineWithoutInt32CoordinateOverflow()
    {
        using var document = new SyntheticLargePreviewDocument(lineCount: 100_000_000);
        var control = new VirtualizedPreviewTextControl
        {
            Document = document,
            TopPadding = 10,
            TextFontSize = 16
        };
        var lineHeight = InvokeResolveLineHeight(control);
        var targetLine = 99_999_990;
        var verticalOffset = control.TopPadding + ((targetLine - 1) * lineHeight) + (lineHeight / 2);

        var actualLine = control.GetLineNumberAtVerticalOffset(verticalOffset);

        Assert.Equal(targetLine, actualLine);
    }

    [Fact]
    public void ViewportRelativeOrigin_RemainsSmallAtHundredMillionthLine()
    {
        const int firstVisibleLine = 99_999_990;
        const double contentTopPadding = 10;
        const double lineHeight = 18.5;
        var viewportTop = contentTopPadding + ((firstVisibleLine - 1) * lineHeight) + 4.25;

        var originY = VirtualizedPreviewTextControl.CalculateViewportRelativeLineOriginY(
            firstVisibleLine,
            contentTopPadding,
            lineHeight,
            viewportTop);

        Assert.Equal(-4.25, originY, precision: 5);
        Assert.InRange(originY, -lineHeight, 0);
    }

    [AvaloniaFact]
    public void SelectionHitTesting_UsesRenderedPreviewTextGeometry()
    {
        var lineText = "mmmmiiWW preview selection geometry check 12345";
        var startColumn = 7;
        var endColumn = 38;
        var control = new VirtualizedPreviewTextControl
        {
            Text = $"before\n{lineText}\nafter",
            Width = 720,
            Height = 180,
            TopPadding = 8,
            BottomPadding = 8,
            LeftPadding = 12,
            RightPadding = 12,
            TextFontFamily = FontFamily.Default,
            TextFontSize = 18,
            TextBrush = Brushes.White
        };
        var typeface = ResolveTestTypeface(control);
        var lineHeight = InvokeResolveLineHeight(control);
        var y = control.TopPadding + lineHeight + (lineHeight / 2.0);
        var startX = control.LeftPadding + MeasureRenderedPrefixWidth(control, lineText, startColumn, typeface);
        var endX = control.LeftPadding + MeasureRenderedPrefixWidth(control, lineText, endColumn, typeface);
        var startPosition = InvokeHitTestSelectionPosition(control, new Point(startX, y));

        SetSelectionAnchor(control, startPosition);
        InvokeUpdateSelectionActivePosition(
            control,
            InvokeHitTestSelectionPosition(control, new Point(endX, y)));

        Assert.True(control.TryGetSelectionRange(out var selectionRange));
        Assert.Equal(new PreviewSelectionRange(2, startColumn, 2, endColumn), selectionRange);
        Assert.Equal(lineText[startColumn..endColumn], control.GetSelectedText());
    }

    [AvaloniaFact]
    public void ResolveDistanceFromColumn_IncludesRenderedTrailingWhitespace()
    {
        var control = new VirtualizedPreviewTextControl
        {
            TextFontFamily = FontFamily.Default,
            TextFontSize = 18,
            TextBrush = Brushes.White
        };
        var typeface = ResolveTestTypeface(control);
        const string lineText = "abc   ";

        var beforeTrailingSpaces = InvokeResolveDistanceFromColumn(control, lineText, 3, typeface);
        var fullLineWidth = InvokeResolveDistanceFromColumn(control, lineText, lineText.Length, typeface);

        Assert.Equal(
            MeasureRenderedPrefixWidth(control, lineText, lineText.Length, typeface),
            fullLineWidth,
            precision: 6);
        Assert.True(fullLineWidth > beforeTrailingSpaces);
    }

    [AvaloniaFact]
    public void ClearingLargeStringPreview_ReleasesOversizedLineMetadataBuffer()
    {
        var control = new VirtualizedPreviewTextControl
        {
            Text = string.Join('\n', Enumerable.Repeat("line", 10_000))
        };

        Assert.True(GetLineStartsCapacity(control) >= 10_000);

        control.Text = string.Empty;

        Assert.InRange(GetLineStartsCapacity(control), 1, 4096);
    }

    private static double InvokeResolveLineHeight(VirtualizedPreviewTextControl control)
    {
        var method = typeof(VirtualizedPreviewTextControl).GetMethod(
            "ResolveLineHeight",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        return (double)method!.Invoke(control, [])!;
    }

    private static int GetLineStartsCapacity(VirtualizedPreviewTextControl control)
    {
        var field = typeof(VirtualizedPreviewTextControl).GetField(
            "_lineStarts",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        var lineStarts = Assert.IsType<List<int>>(field!.GetValue(control));
        return lineStarts.Capacity;
    }

    private static double InvokeResolveDistanceFromColumn(
        VirtualizedPreviewTextControl control,
        string lineText,
        int column,
        Typeface typeface)
    {
        var method = typeof(VirtualizedPreviewTextControl).GetMethod(
            "ResolveDistanceFromColumn",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        return (double)method!.Invoke(control, [lineText, column, typeface])!;
    }

    private static object InvokeHitTestSelectionPosition(VirtualizedPreviewTextControl control, Point point)
    {
        var method = typeof(VirtualizedPreviewTextControl).GetMethod(
            "HitTestSelectionPosition",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        return method!.Invoke(control, [point])!;
    }

    private static void InvokeUpdateSelectionActivePosition(
        VirtualizedPreviewTextControl control,
        object selectionPosition)
    {
        var method = typeof(VirtualizedPreviewTextControl).GetMethod(
            "UpdateSelectionActivePosition",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        method!.Invoke(control, [selectionPosition]);
    }

    private static void SetSelectionAnchor(VirtualizedPreviewTextControl control, object selectionPosition)
    {
        var field = typeof(VirtualizedPreviewTextControl).GetField(
            "_selectionAnchor",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        field!.SetValue(control, selectionPosition);
    }

    private static Typeface ResolveTestTypeface(VirtualizedPreviewTextControl control)
        => new(control.TextFontFamily ?? FontFamily.Default, FontStyle.Normal, FontWeight.Normal);

    private static double MeasureRenderedPrefixWidth(
        VirtualizedPreviewTextControl control,
        string lineText,
        int column,
        Typeface typeface)
    {
        var clampedColumn = Math.Clamp(column, 0, lineText.Length);
        var formattedText = new FormattedText(
            lineText[..clampedColumn],
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            control.TextFontSize,
            control.TextBrush ?? Brushes.White);

        return formattedText.WidthIncludingTrailingWhitespace;
    }

    private sealed class SyntheticLargePreviewDocument(int lineCount) : IPreviewTextDocument
    {
        public int LineCount { get; } = lineCount;

        public int MaxLineLength => 4;

        public long CharacterCount => (long)LineCount * 5;

        public IReadOnlyList<PreviewDocumentSection> Sections => [];

        public string GetLineText(int lineNumber) => "test";

        public string GetLineRangeText(int firstLine, int lastLine) => "test";

        public void Dispose()
        {
        }
    }

}
