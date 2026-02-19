namespace DevProjex.Avalonia.Controls;

/// <summary>
/// Draws only visible line numbers for preview text.
/// This avoids creating large line-number strings for big exports.
/// </summary>
public sealed class VirtualizedLineNumbersControl : Control
{
    public static readonly StyledProperty<int> LineCountProperty =
        AvaloniaProperty.Register<VirtualizedLineNumbersControl, int>(nameof(LineCount), 1);

    public static readonly StyledProperty<double> VerticalOffsetProperty =
        AvaloniaProperty.Register<VirtualizedLineNumbersControl, double>(nameof(VerticalOffset));

    public static readonly StyledProperty<double> TopPaddingProperty =
        AvaloniaProperty.Register<VirtualizedLineNumbersControl, double>(nameof(TopPadding), 10.0);

    public static readonly StyledProperty<double> BottomPaddingProperty =
        AvaloniaProperty.Register<VirtualizedLineNumbersControl, double>(nameof(BottomPadding), 10.0);

    public static readonly StyledProperty<double> LeftPaddingProperty =
        AvaloniaProperty.Register<VirtualizedLineNumbersControl, double>(nameof(LeftPadding), 10.0);

    public static readonly StyledProperty<double> RightPaddingProperty =
        AvaloniaProperty.Register<VirtualizedLineNumbersControl, double>(nameof(RightPadding), 8.0);

    public static readonly StyledProperty<double> ExtentHeightProperty =
        AvaloniaProperty.Register<VirtualizedLineNumbersControl, double>(nameof(ExtentHeight));

    public static readonly StyledProperty<double> ViewportHeightProperty =
        AvaloniaProperty.Register<VirtualizedLineNumbersControl, double>(nameof(ViewportHeight));

    public static readonly StyledProperty<FontFamily?> NumberFontFamilyProperty =
        AvaloniaProperty.Register<VirtualizedLineNumbersControl, FontFamily?>(
            nameof(NumberFontFamily),
            FontFamily.Default);

    public static readonly StyledProperty<double> NumberFontSizeProperty =
        AvaloniaProperty.Register<VirtualizedLineNumbersControl, double>(nameof(NumberFontSize), 15.0);

    public static readonly StyledProperty<IBrush?> NumberBrushProperty =
        AvaloniaProperty.Register<VirtualizedLineNumbersControl, IBrush?>(nameof(NumberBrush));

    static VirtualizedLineNumbersControl()
    {
        AffectsRender<VirtualizedLineNumbersControl>(
            LineCountProperty,
            VerticalOffsetProperty,
            TopPaddingProperty,
            BottomPaddingProperty,
            LeftPaddingProperty,
            RightPaddingProperty,
            ExtentHeightProperty,
            ViewportHeightProperty,
            NumberFontFamilyProperty,
            NumberFontSizeProperty,
            NumberBrushProperty);

        AffectsMeasure<VirtualizedLineNumbersControl>(
            LineCountProperty,
            LeftPaddingProperty,
            RightPaddingProperty,
            NumberFontFamilyProperty,
            NumberFontSizeProperty);
    }

    public int LineCount
    {
        get => GetValue(LineCountProperty);
        set => SetValue(LineCountProperty, value);
    }

    public double VerticalOffset
    {
        get => GetValue(VerticalOffsetProperty);
        set => SetValue(VerticalOffsetProperty, value);
    }

    public double TopPadding
    {
        get => GetValue(TopPaddingProperty);
        set => SetValue(TopPaddingProperty, value);
    }

    public double LeftPadding
    {
        get => GetValue(LeftPaddingProperty);
        set => SetValue(LeftPaddingProperty, value);
    }

    public double BottomPadding
    {
        get => GetValue(BottomPaddingProperty);
        set => SetValue(BottomPaddingProperty, value);
    }

    public double RightPadding
    {
        get => GetValue(RightPaddingProperty);
        set => SetValue(RightPaddingProperty, value);
    }

    public FontFamily? NumberFontFamily
    {
        get => GetValue(NumberFontFamilyProperty);
        set => SetValue(NumberFontFamilyProperty, value);
    }

    public double NumberFontSize
    {
        get => GetValue(NumberFontSizeProperty);
        set => SetValue(NumberFontSizeProperty, value);
    }

    public IBrush? NumberBrush
    {
        get => GetValue(NumberBrushProperty);
        set => SetValue(NumberBrushProperty, value);
    }

    public double ExtentHeight
    {
        get => GetValue(ExtentHeightProperty);
        set => SetValue(ExtentHeightProperty, value);
    }

    public double ViewportHeight
    {
        get => GetValue(ViewportHeightProperty);
        set => SetValue(ViewportHeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = CalculateRequiredWidth();
        var height = double.IsFinite(availableSize.Height) ? availableSize.Height : 0;

        return new Size(width, height);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var totalLines = Math.Max(1, LineCount);
        if (Bounds.Height <= 0 || Bounds.Width <= 0)
            return;

        var typeface = new Typeface(NumberFontFamily ?? FontFamily.Default, FontStyle.Normal, FontWeight.Normal);
        var lineHeight = ResolveLineHeight(totalLines, typeface);
        if (lineHeight <= 0)
            return;

        var viewportTop = Math.Max(0, VerticalOffset);
        var viewportHeight = ViewportHeight > 0 ? ViewportHeight : Bounds.Height;
        var contentTop = TopPadding;

        var firstVisibleLine = Math.Max(1, (int)Math.Floor((viewportTop - contentTop) / lineHeight) + 1);
        var visibleLineCount = Math.Max(1, (int)Math.Ceiling(viewportHeight / lineHeight));
        var lastVisibleLine = Math.Min(totalLines, firstVisibleLine + visibleLineCount - 1);

        const int renderBuffer = 3;
        firstVisibleLine = Math.Max(1, firstVisibleLine - renderBuffer);
        lastVisibleLine = Math.Min(totalLines, lastVisibleLine + renderBuffer);
        if (firstVisibleLine > totalLines)
            firstVisibleLine = totalLines;
        if (lastVisibleLine < firstVisibleLine)
            return;

        var linesCount = lastVisibleLine - firstVisibleLine + 1;
        var builder = new StringBuilder(linesCount * 6);
        for (var line = firstVisibleLine; line <= lastVisibleLine; line++)
        {
            builder.Append(line.ToString(CultureInfo.InvariantCulture));
            if (line < lastVisibleLine)
                builder.Append('\n');
        }

        var text = BuildFormattedText(builder.ToString(), typeface);
        var originY = contentTop + (firstVisibleLine - 1) * lineHeight - viewportTop;
        context.DrawText(text, new Point(LeftPadding, originY));
    }

    private double CalculateRequiredWidth()
    {
        var digits = Math.Max(1, Math.Max(1, LineCount).ToString(CultureInfo.InvariantCulture).Length);
        var sampleDigits = new string('8', digits);
        var typeface = new Typeface(NumberFontFamily ?? FontFamily.Default, FontStyle.Normal, FontWeight.Normal);
        var sample = BuildFormattedText(sampleDigits, typeface);

        return Math.Ceiling(sample.Width + LeftPadding + RightPadding);
    }

    private FormattedText BuildFormattedText(string text, Typeface typeface)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            NumberFontSize,
            NumberBrush ?? Brushes.Gray);
    }

    private double ResolveLineHeight(int totalLines, Typeface typeface)
    {
        // Prefer deriving line height from actual scroll extent to avoid cumulative drift
        // on very large previews.
        if (ExtentHeight > 0 && totalLines > 0)
        {
            var verticalPadding = Math.Max(0, TopPadding) + Math.Max(0, BottomPadding);
            var textHeight = ExtentHeight - verticalPadding;
            if (textHeight > 0)
            {
                var extentLineHeight = textHeight / totalLines;
                if (extentLineHeight > 0.25)
                    return extentLineHeight;
            }
        }

        var sample = BuildFormattedText("8", typeface);
        return Math.Max(1.0, sample.Height);
    }
}
