using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

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

    public static readonly StyledProperty<double> LeftPaddingProperty =
        AvaloniaProperty.Register<VirtualizedLineNumbersControl, double>(nameof(LeftPadding), 10.0);

    public static readonly StyledProperty<double> RightPaddingProperty =
        AvaloniaProperty.Register<VirtualizedLineNumbersControl, double>(nameof(RightPadding), 8.0);

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
            LeftPaddingProperty,
            RightPaddingProperty,
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
        var sample = BuildFormattedText("8", typeface);
        var lineHeight = Math.Max(1.0, sample.Height);
        var viewportTop = Math.Max(0, VerticalOffset);
        var viewportBottom = viewportTop + Bounds.Height;
        var contentTop = TopPadding;

        var firstVisibleLine = Math.Max(1, (int)Math.Floor((viewportTop - contentTop) / lineHeight) + 1);
        var lastVisibleLine = Math.Min(totalLines, (int)Math.Ceiling((viewportBottom - contentTop) / lineHeight) + 1);

        const int renderBuffer = 3;
        firstVisibleLine = Math.Max(1, firstVisibleLine - renderBuffer);
        lastVisibleLine = Math.Min(totalLines, lastVisibleLine + renderBuffer);
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
}
