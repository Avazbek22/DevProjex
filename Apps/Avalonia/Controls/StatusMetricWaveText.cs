namespace DevProjex.Avalonia.Controls;

public enum StatusMetricWaveDirection
{
    LeftToRight,
    RightToLeft
}

public sealed class StatusMetricWaveText : Control
{
    private static readonly TimeSpan CharacterRiseDuration =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(260));
    private static readonly TimeSpan MaximumWaveTravelDuration =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(520));
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    private readonly List<WaveGlyph> _glyphs = [];
    private DispatcherTimer? _animationTimer;
    private long _animationStartedTimestamp;
    private bool _isAttached;
    private bool _glyphsDirty = true;
    private bool _revealStarted;
    private bool _revealCompleted;
    private double _contentWidth;
    private double _contentHeight;

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<StatusMetricWaveText, string>(nameof(Label), string.Empty);

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<StatusMetricWaveText, string>(nameof(Text), string.Empty);

    public static readonly StyledProperty<StatusMetricWaveDirection> RevealDirectionProperty =
        AvaloniaProperty.Register<StatusMetricWaveText, StatusMetricWaveDirection>(
            nameof(RevealDirection),
            StatusMetricWaveDirection.LeftToRight);

    public static readonly StyledProperty<FontFamily?> TextFontFamilyProperty =
        AvaloniaProperty.Register<StatusMetricWaveText, FontFamily?>(
            nameof(TextFontFamily),
            FontFamily.Default);

    public static readonly StyledProperty<double> TextFontSizeProperty =
        AvaloniaProperty.Register<StatusMetricWaveText, double>(nameof(TextFontSize), 12d);

    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<StatusMetricWaveText, IBrush?>(nameof(TextBrush));

    static StatusMetricWaveText()
    {
        AffectsMeasure<StatusMetricWaveText>(
            LabelProperty,
            TextProperty,
            TextFontFamilyProperty,
            TextFontSizeProperty);
        AffectsRender<StatusMetricWaveText>(
            LabelProperty,
            TextProperty,
            RevealDirectionProperty,
            TextFontFamilyProperty,
            TextFontSizeProperty,
            TextBrushProperty);
    }

    public StatusMetricWaveText()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
        UseLayoutRounding = true;
        TextOptions.SetTextHintingMode(this, TextHintingMode.Strong);
        TextOptions.SetBaselinePixelAlignment(this, BaselinePixelAlignment.Aligned);
    }

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public StatusMetricWaveDirection RevealDirection
    {
        get => GetValue(RevealDirectionProperty);
        set => SetValue(RevealDirectionProperty, value);
    }

    public FontFamily? TextFontFamily
    {
        get => GetValue(TextFontFamilyProperty);
        set => SetValue(TextFontFamilyProperty, value);
    }

    public double TextFontSize
    {
        get => GetValue(TextFontSizeProperty);
        set => SetValue(TextFontSizeProperty, value);
    }

    public IBrush? TextBrush
    {
        get => GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == LabelProperty ||
            change.Property == TextProperty ||
            change.Property == TextFontFamilyProperty ||
            change.Property == TextFontSizeProperty ||
            change.Property == TextBrushProperty)
        {
            _glyphsDirty = true;
        }

        if (change.Property == TextProperty)
        {
            if (string.IsNullOrEmpty(Text))
                ResetReveal();
            else
                TryStartReveal();
        }
        else if (change.Property == IsVisibleProperty)
        {
            TryStartReveal();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        TryStartReveal();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        StopAnimationTimer();
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureGlyphs();
        return new Size(Math.Ceiling(_contentWidth), Math.Ceiling(_contentHeight));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        EnsureGlyphs();
        if (_glyphs.Count == 0)
            return;

        var elapsed = ResolveAnimationElapsed();
        var top = Math.Max(0, (Bounds.Height - _contentHeight) / 2);
        for (var index = 0; index < _glyphs.Count; index++)
        {
            var glyph = _glyphs[index];
            var progress = ResolveCharacterProgress(index, _glyphs.Count, elapsed);
            if (progress <= 0)
                continue;

            var (offsetY, revealOpacity) = ResolveCharacterPresentation(progress);
            using (context.PushOpacity(glyph.BaseOpacity * revealOpacity))
            {
                context.DrawText(glyph.Text, new Point(glyph.X, top + offsetY));
            }
        }
    }

    private void TryStartReveal()
    {
        if (!_isAttached || !IsVisible || string.IsNullOrEmpty(Text) ||
            _revealStarted || _revealCompleted)
        {
            return;
        }

        _revealStarted = true;
        _animationStartedTimestamp = Stopwatch.GetTimestamp();
        _animationTimer ??= CreateAnimationTimer();
        _animationTimer.Start();
        InvalidateVisual();
    }

    private DispatcherTimer CreateAnimationTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = FrameInterval
        };
        timer.Tick += OnAnimationFrame;
        return timer;
    }

    private void OnAnimationFrame(object? sender, EventArgs e)
    {
        var elapsed = ResolveAnimationElapsed();
        if (elapsed >= CharacterRiseDuration + MaximumWaveTravelDuration)
        {
            _revealCompleted = true;
            StopAnimationTimer();
        }

        InvalidateVisual();
    }

    private TimeSpan ResolveAnimationElapsed() =>
        _revealStarted && !_revealCompleted
            ? Stopwatch.GetElapsedTime(_animationStartedTimestamp)
            : CharacterRiseDuration + MaximumWaveTravelDuration;

    private double ResolveCharacterProgress(int index, int count, TimeSpan elapsed)
    {
        if (_revealCompleted || !_revealStarted)
            return 1;

        var travelIndex = RevealDirection == StatusMetricWaveDirection.LeftToRight
            ? index
            : count - index - 1;
        var delayRatio = count <= 1 ? 0 : (double)travelIndex / (count - 1);
        var delay = MaximumWaveTravelDuration.TotalMilliseconds * delayRatio;
        return Math.Clamp(
            (elapsed.TotalMilliseconds - delay) / CharacterRiseDuration.TotalMilliseconds,
            0,
            1);
    }

    private static (double OffsetY, double Opacity) ResolveCharacterPresentation(double progress)
    {
        const double overshootCue = 0.78;
        double offsetY;
        if (progress < overshootCue)
        {
            var rise = CubicEaseOut(progress / overshootCue);
            offsetY = Lerp(7, -0.65, rise);
        }
        else
        {
            var settle = CubicEaseInOut((progress - overshootCue) / (1 - overshootCue));
            offsetY = Lerp(-0.65, 0, settle);
        }

        var opacity = CubicEaseOut(Math.Clamp(progress / 0.68, 0, 1));
        return (offsetY, opacity);
    }

    private void EnsureGlyphs()
    {
        if (!_glyphsDirty)
            return;

        _glyphsDirty = false;
        _glyphs.Clear();
        _contentWidth = 0;
        _contentHeight = 0;
        if (string.IsNullOrEmpty(Text))
            return;

        var family = TextFontFamily ?? FontFamily.Default;
        var brush = TextBrush ?? Brushes.White;
        AppendGlyphs(Label, new Typeface(family, FontStyle.Normal, FontWeight.SemiBold), brush, 0.9);
        if (!string.IsNullOrEmpty(Label))
            AppendGlyphs(" - ", new Typeface(family, FontStyle.Normal, FontWeight.Normal), brush, 0.6);
        AppendGlyphs(Text, new Typeface(family, FontStyle.Normal, FontWeight.Normal), brush, 0.88);
    }

    private void AppendGlyphs(string value, Typeface typeface, IBrush brush, double opacity)
    {
        var textElements = StringInfo.GetTextElementEnumerator(value);
        while (textElements.MoveNext())
        {
            var element = textElements.GetTextElement();
            var formatted = new FormattedText(
                element,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                TextFontSize,
                brush);
            _glyphs.Add(new WaveGlyph(formatted, _contentWidth, opacity));
            _contentWidth += formatted.WidthIncludingTrailingWhitespace;
            _contentHeight = Math.Max(_contentHeight, formatted.Height);
        }
    }

    private void ResetReveal()
    {
        _revealStarted = false;
        _revealCompleted = false;
        StopAnimationTimer();
        InvalidateVisual();
    }

    private void StopAnimationTimer()
    {
        _animationTimer?.Stop();
    }

    private static double CubicEaseOut(double value)
    {
        var inverse = 1 - value;
        return 1 - inverse * inverse * inverse;
    }

    private static double CubicEaseInOut(double value) =>
        value < 0.5
            ? 4 * value * value * value
            : 1 - Math.Pow(-2 * value + 2, 3) / 2;

    private static double Lerp(double start, double end, double progress) =>
        start + ((end - start) * progress);

    private sealed record WaveGlyph(FormattedText Text, double X, double BaseOpacity);
}
