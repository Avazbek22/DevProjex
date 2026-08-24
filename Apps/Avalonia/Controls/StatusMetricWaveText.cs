namespace DevProjex.Avalonia.Controls;

public enum StatusMetricWaveDirection
{
    LeftToRight,
    RightToLeft
}

public sealed class StatusMetricWaveText : Control
{
    private static readonly TimeSpan CharacterRiseDuration =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(300));
    private static readonly TimeSpan MaximumWaveTravelDuration =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(500));
    private static readonly TimeSpan MetricRollDuration =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(380));
    private static readonly TimeSpan MetricRollStagger =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(20));
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan MaximumFrameAdvance = TimeSpan.FromMilliseconds(34);

    private readonly List<WaveGlyph> _glyphs = [];
    private DispatcherTimer? _animationTimer;
    private long _lastAnimationFrameTimestamp;
    private TimeSpan _animationElapsed;
    private MetricRollTransition? _metricRollTransition;
    private string _lastText = string.Empty;
    private bool _isAttached;
    private bool _glyphsDirty = true;
    private bool _revealStarted;
    private bool _revealCompleted;
    private double _layoutWidth;
    private double _reservedContentWidth;
    private double _contentWidth;
    private double _contentHeight;

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<StatusMetricWaveText, string>(nameof(Label), string.Empty);

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<StatusMetricWaveText, string>(nameof(Text), string.Empty);

    public static readonly StyledProperty<bool> IsAnimationEnabledProperty =
        AvaloniaProperty.Register<StatusMetricWaveText, bool>(nameof(IsAnimationEnabled), true);

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
            IsAnimationEnabledProperty,
            RevealDirectionProperty,
            TextFontFamilyProperty,
            TextFontSizeProperty,
            TextBrushProperty);
    }

    public StatusMetricWaveText()
    {
        ClipToBounds = false;
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

    public bool IsAnimationEnabled
    {
        get => GetValue(IsAnimationEnabledProperty);
        set => SetValue(IsAnimationEnabledProperty, value);
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

    internal bool IsAnimationActive =>
        _revealStarted && !_revealCompleted ||
        _metricRollTransition is not null;

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

        if (change.Property == LabelProperty ||
            change.Property == TextFontFamilyProperty ||
            change.Property == TextFontSizeProperty)
        {
            _reservedContentWidth = 0;
        }

        if (change.Property == TextProperty)
        {
            var previousText = _lastText;
            _lastText = Text;
            if (string.IsNullOrEmpty(Text))
                ResetReveal();
            else if (!IsAnimationEnabled)
                CompleteAnimationImmediately();
            else if (_revealCompleted &&
                     !string.IsNullOrEmpty(previousText) &&
                     !string.Equals(previousText, Text, StringComparison.Ordinal))
            {
                StartMetricRoll(previousText, Text);
            }
            else
                TryStartReveal();
        }
        else if (change.Property == IsAnimationEnabledProperty)
        {
            CompleteAnimationImmediately();
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
        if (_metricRollTransition is { } metricRoll)
        {
            RenderMetricRoll(context, metricRoll, top);
            return;
        }

        for (var index = 0; index < _glyphs.Count; index++)
        {
            var glyph = _glyphs[index];
            var progress = ResolveCharacterProgress(index, _glyphs.Count, elapsed);
            if (progress <= 0)
                continue;

            var (offsetY, revealOpacity) = ResolveCharacterPresentation(
                progress,
                _contentHeight);
            using (context.PushOpacity(glyph.BaseOpacity * revealOpacity))
            {
                context.DrawText(
                    glyph.Text,
                    new Point(glyph.X + ResolveLayoutOffset(_layoutWidth), top + offsetY));
            }
        }
    }

    private void TryStartReveal()
    {
        if (!IsAnimationEnabled)
        {
            CompleteAnimationImmediately();
            return;
        }

        if (!_isAttached || !IsVisible || string.IsNullOrEmpty(Text) ||
            _revealStarted || _revealCompleted)
        {
            return;
        }

        _revealStarted = true;
        RestartAnimationClock();
        _animationTimer ??= CreateAnimationTimer();
        _animationTimer.Start();
        InvalidateVisual();
    }

    private void StartMetricRoll(string previousText, string currentText)
    {
        if (!IsAnimationEnabled)
        {
            CompleteAnimationImmediately();
            return;
        }

        if (!_isAttached || !IsVisible)
            return;

        var previousLayout = BuildGlyphLayout(previousText);
        var currentLayout = BuildGlyphLayout(currentText);
        var previousRuns = FindNumericRuns(previousLayout);
        var currentRuns = FindNumericRuns(currentLayout);
        var changedGlyphs = new bool[currentLayout.Glyphs.Count];
        var cells = new List<MetricRollCell>();
        var maximumDelayRank = 0;
        var transitionWidth = Math.Max(
            _reservedContentWidth,
            Math.Max(previousLayout.Width, currentLayout.Width));
        var previousLayoutOffset = RevealDirection == StatusMetricWaveDirection.RightToLeft
            ? transitionWidth - previousLayout.Width
            : 0;
        var currentLayoutOffset = RevealDirection == StatusMetricWaveDirection.RightToLeft
            ? transitionWidth - currentLayout.Width
            : 0;

        for (var runIndex = 0; runIndex < Math.Min(previousRuns.Count, currentRuns.Count); runIndex++)
        {
            var previousRun = previousRuns[runIndex];
            var currentRun = currentRuns[runIndex];
            if (Math.Abs(previousRun.Value - currentRun.Value) < double.Epsilon)
                continue;

            var increases = currentRun.Value > previousRun.Value;
            var previousCount = previousRun.EndExclusive - previousRun.Start;
            var currentCount = currentRun.EndExclusive - currentRun.Start;
            var cellCount = Math.Max(previousCount, currentCount);
            var currentRight = GetGlyphRight(currentLayout, currentRun.EndExclusive - 1);
            var previousRight = GetGlyphRight(previousLayout, previousRun.EndExclusive - 1);
            var previousXAdjustment =
                currentLayoutOffset + currentRight - previousLayoutOffset - previousRight;

            for (var rank = 0; rank < cellCount; rank++)
            {
                var previousIndex = rank < previousCount
                    ? previousRun.EndExclusive - rank - 1
                    : (int?)null;
                var currentIndex = rank < currentCount
                    ? currentRun.EndExclusive - rank - 1
                    : (int?)null;
                if (previousIndex is { } oldIndex &&
                    currentIndex is { } newIndex &&
                    string.Equals(
                        previousLayout.Glyphs[oldIndex].Element,
                        currentLayout.Glyphs[newIndex].Element,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (currentIndex is { } changedIndex)
                    changedGlyphs[changedIndex] = true;
                cells.Add(new MetricRollCell(
                    previousIndex,
                    currentIndex,
                    rank,
                    increases,
                    previousXAdjustment));
                maximumDelayRank = Math.Max(maximumDelayRank, rank);
            }
        }

        _reservedContentWidth = transitionWidth;
        ApplyGlyphLayout(currentLayout);
        _glyphsDirty = false;
        if (cells.Count == 0)
        {
            _metricRollTransition = null;
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }

        _contentHeight = Math.Max(previousLayout.Height, currentLayout.Height);
        _metricRollTransition = new MetricRollTransition(
            previousLayout,
            currentLayout,
            cells,
            changedGlyphs,
            previousLayoutOffset,
            currentLayoutOffset,
            MetricRollDuration + TimeSpan.FromTicks(MetricRollStagger.Ticks * maximumDelayRank));
        RestartAnimationClock();
        _animationTimer ??= CreateAnimationTimer();
        _animationTimer.Start();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void RenderMetricRoll(
        DrawingContext context,
        MetricRollTransition transition,
        double currentTop)
    {
        for (var index = 0; index < transition.Current.Glyphs.Count; index++)
        {
            if (transition.ChangedCurrentGlyphs[index])
                continue;

            DrawGlyph(
                context,
                transition.Current.Glyphs[index],
                currentTop,
                0,
                1,
                transition.CurrentLayoutOffset);
        }

        var elapsed = _animationElapsed;
        var previousTop = Math.Max(0, (Bounds.Height - transition.Previous.Height) / 2);
        var travelDistance = Math.Max(transition.Previous.Height, transition.Current.Height) + 1;
        using (context.PushClip(new Rect(0, 0, Bounds.Width, Bounds.Height)))
        {
            foreach (var cell in transition.Cells)
            {
                var delay = MetricRollStagger.TotalMilliseconds * cell.DelayRank;
                var progress = Math.Clamp(
                    (elapsed.TotalMilliseconds - delay) / MetricRollDuration.TotalMilliseconds,
                    0,
                    1);
                var movement = SmootherStep(progress);
                var outgoingDirection = cell.Increases ? -1 : 1;

                if (cell.PreviousGlyphIndex is { } previousIndex)
                {
                    DrawGlyph(
                        context,
                        transition.Previous.Glyphs[previousIndex],
                        previousTop,
                        outgoingDirection * travelDistance * movement,
                        1,
                        transition.PreviousLayoutOffset + cell.PreviousXAdjustment);
                }

                if (cell.CurrentGlyphIndex is { } currentIndex)
                {
                    DrawGlyph(
                        context,
                        transition.Current.Glyphs[currentIndex],
                        currentTop,
                        -outgoingDirection * travelDistance * (1 - movement),
                        1,
                        transition.CurrentLayoutOffset);
                }
            }
        }
    }

    private static void DrawGlyph(
        DrawingContext context,
        WaveGlyph glyph,
        double top,
        double offsetY,
        double opacity,
        double offsetX = 0)
    {
        if (opacity <= 0)
            return;

        using (context.PushOpacity(glyph.BaseOpacity * opacity))
        {
            context.DrawText(glyph.Text, new Point(glyph.X + offsetX, top + offsetY));
        }
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
        AdvanceAnimationClock();
        if (_metricRollTransition is { } metricRoll)
        {
            if (_animationElapsed >= metricRoll.Duration)
            {
                _metricRollTransition = null;
                ApplyGlyphLayout(metricRoll.Current);
                StopAnimationTimer();
                InvalidateMeasure();
            }

            InvalidateVisual();
            return;
        }

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
            ? _animationElapsed
            : CharacterRiseDuration + MaximumWaveTravelDuration;

    private void RestartAnimationClock()
    {
        _animationElapsed = TimeSpan.Zero;
        _lastAnimationFrameTimestamp = Stopwatch.GetTimestamp();
    }

    private void AdvanceAnimationClock()
    {
        var timestamp = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastAnimationFrameTimestamp, timestamp);
        _lastAnimationFrameTimestamp = timestamp;
        _animationElapsed += elapsed > MaximumFrameAdvance
            ? MaximumFrameAdvance
            : elapsed;
    }

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

    private static (double OffsetY, double Opacity) ResolveCharacterPresentation(
        double progress,
        double characterHeight)
    {
        const double overshootCue = 0.82;
        var startingOffset = Math.Max(15, characterHeight + 4);
        double offsetY;
        if (progress < overshootCue)
        {
            var rise = CubicEaseOut(progress / overshootCue);
            offsetY = Lerp(startingOffset, -0.45, rise);
        }
        else
        {
            var settle = CubicEaseInOut((progress - overshootCue) / (1 - overshootCue));
            offsetY = Lerp(-0.45, 0, settle);
        }

        var opacity = CubicEaseOut(Math.Clamp(progress / 0.34, 0, 1));
        return (offsetY, opacity);
    }

    private void EnsureGlyphs()
    {
        if (!_glyphsDirty)
            return;

        _glyphsDirty = false;
        ApplyGlyphLayout(BuildGlyphLayout(Text));
    }

    private GlyphLayout BuildGlyphLayout(string metricText)
    {
        var glyphs = new List<WaveGlyph>();
        var contentWidth = 0d;
        var contentHeight = 0d;
        if (string.IsNullOrEmpty(metricText))
            return new GlyphLayout(glyphs, contentWidth, contentHeight);

        var family = TextFontFamily ?? FontFamily.Default;
        var brush = TextBrush ?? Brushes.White;
        AppendGlyphs(
            glyphs,
            Label,
            new Typeface(family, FontStyle.Normal, FontWeight.SemiBold),
            brush,
            TextFontSize,
            0.9,
            isMetricText: false,
            ref contentWidth,
            ref contentHeight);
        if (!string.IsNullOrEmpty(Label))
        {
            AppendGlyphs(
                glyphs,
                " - ",
                new Typeface(family, FontStyle.Normal, FontWeight.Normal),
                brush,
                TextFontSize,
                0.6,
                isMetricText: false,
                ref contentWidth,
                ref contentHeight);
        }
        AppendGlyphs(
            glyphs,
            metricText,
            new Typeface(family, FontStyle.Normal, FontWeight.Normal),
            brush,
            TextFontSize,
            0.88,
            isMetricText: true,
            ref contentWidth,
            ref contentHeight);
        return new GlyphLayout(glyphs, contentWidth, contentHeight);
    }

    private void ApplyGlyphLayout(GlyphLayout layout)
    {
        _glyphs.Clear();
        _glyphs.AddRange(layout.Glyphs);
        _layoutWidth = layout.Width;
        _reservedContentWidth = Math.Max(_reservedContentWidth, layout.Width);
        _contentWidth = _reservedContentWidth;
        _contentHeight = layout.Height;
    }

    private double ResolveLayoutOffset(double layoutWidth) =>
        RevealDirection == StatusMetricWaveDirection.RightToLeft
            ? Math.Max(0, _contentWidth - layoutWidth)
            : 0;

    private static void AppendGlyphs(
        List<WaveGlyph> glyphs,
        string value,
        Typeface typeface,
        IBrush brush,
        double fontSize,
        double opacity,
        bool isMetricText,
        ref double contentWidth,
        ref double contentHeight)
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
                fontSize,
                brush);
            glyphs.Add(new WaveGlyph(element, formatted, contentWidth, opacity, isMetricText));
            contentWidth += formatted.WidthIncludingTrailingWhitespace;
            contentHeight = Math.Max(contentHeight, formatted.Height);
        }
    }

    private static List<NumericGlyphRun> FindNumericRuns(GlyphLayout layout)
    {
        var runs = new List<NumericGlyphRun>();
        var glyphs = layout.Glyphs;
        for (var index = 0; index < glyphs.Count;)
        {
            if (!IsDigitGlyph(glyphs[index]))
            {
                index++;
                continue;
            }

            var start = index;
            var end = index + 1;
            while (end < glyphs.Count)
            {
                if (IsDigitGlyph(glyphs[end]))
                {
                    end++;
                    continue;
                }

                if (IsNumberSeparatorGlyph(glyphs[end]) &&
                    end + 1 < glyphs.Count &&
                    IsDigitGlyph(glyphs[end + 1]))
                {
                    end++;
                    continue;
                }

                if (IsMagnitudeSuffixGlyph(glyphs[end]))
                    end++;
                break;
            }

            var token = string.Concat(glyphs.Skip(start).Take(end - start).Select(static glyph => glyph.Element));
            if (TryParseMetricValue(token, out var value))
                runs.Add(new NumericGlyphRun(start, end, value));
            index = end;
        }

        return runs;
    }

    private static bool IsDigitGlyph(WaveGlyph glyph)
    {
        if (!glyph.IsMetricText)
            return false;

        var hasRune = false;
        foreach (var rune in glyph.Element.EnumerateRunes())
        {
            hasRune = true;
            if (!Rune.IsDigit(rune))
                return false;
        }

        return hasRune;
    }

    private static bool IsNumberSeparatorGlyph(WaveGlyph glyph)
    {
        if (!glyph.IsMetricText)
            return false;

        var numberFormat = CultureInfo.CurrentCulture.NumberFormat;
        return string.Equals(glyph.Element, numberFormat.NumberGroupSeparator, StringComparison.Ordinal) ||
               string.Equals(glyph.Element, numberFormat.NumberDecimalSeparator, StringComparison.Ordinal);
    }

    private static bool IsMagnitudeSuffixGlyph(WaveGlyph glyph) =>
        glyph.IsMetricText && glyph.Element is "K" or "M";

    private static bool TryParseMetricValue(string token, out double value)
    {
        var multiplier = 1d;
        if (token.EndsWith('K'))
        {
            multiplier = 1_000d;
            token = token[..^1];
        }
        else if (token.EndsWith('M'))
        {
            multiplier = 1_000_000d;
            token = token[..^1];
        }

        if (!double.TryParse(token, NumberStyles.Number, CultureInfo.CurrentCulture, out value))
            return false;

        value *= multiplier;
        return true;
    }

    private static double GetGlyphRight(GlyphLayout layout, int index)
    {
        var glyph = layout.Glyphs[index];
        return glyph.X + glyph.Text.WidthIncludingTrailingWhitespace;
    }

    private void ResetReveal()
    {
        _revealStarted = false;
        _revealCompleted = false;
        _metricRollTransition = null;
        _layoutWidth = 0;
        _reservedContentWidth = 0;
        _contentWidth = 0;
        StopAnimationTimer();
        InvalidateVisual();
    }

    private void CompleteAnimationImmediately()
    {
        _metricRollTransition = null;
        StopAnimationTimer();
        _revealStarted = !string.IsNullOrEmpty(Text);
        _revealCompleted = _revealStarted;
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

    private static double SmootherStep(double value) =>
        value * value * value * (value * ((value * 6) - 15) + 10);

    private static double Lerp(double start, double end, double progress) =>
        start + ((end - start) * progress);

    private sealed record WaveGlyph(
        string Element,
        FormattedText Text,
        double X,
        double BaseOpacity,
        bool IsMetricText);

    private sealed record GlyphLayout(
        IReadOnlyList<WaveGlyph> Glyphs,
        double Width,
        double Height);

    private readonly record struct NumericGlyphRun(
        int Start,
        int EndExclusive,
        double Value);

    private sealed record MetricRollCell(
        int? PreviousGlyphIndex,
        int? CurrentGlyphIndex,
        int DelayRank,
        bool Increases,
        double PreviousXAdjustment);

    private sealed record MetricRollTransition(
        GlyphLayout Previous,
        GlyphLayout Current,
        IReadOnlyList<MetricRollCell> Cells,
        bool[] ChangedCurrentGlyphs,
        double PreviousLayoutOffset,
        double CurrentLayoutOffset,
        TimeSpan Duration);
}
