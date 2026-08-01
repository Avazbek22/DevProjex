using Avalonia.Animation;
using Avalonia.Animation.Easings;

namespace DevProjex.Avalonia.Controls;

public sealed class AnimatedTreeChildrenHost : Decorator
{
    internal static readonly TimeSpan ExpansionDuration =
        TimeSpan.FromMilliseconds(120);

    public static readonly StyledProperty<double> ExpansionProgressProperty =
        AvaloniaProperty.Register<AnimatedTreeChildrenHost, double>(
            nameof(ExpansionProgress),
            defaultValue: 1d,
            coerce: static (_, value) => Math.Clamp(value, 0d, 1d));

    private IDisposable? _collapseCompletion;
    private int _stateRevision;
    private bool _isExpanded;

    static AnimatedTreeChildrenHost()
    {
        AffectsMeasure<AnimatedTreeChildrenHost>(ExpansionProgressProperty);
    }

    public AnimatedTreeChildrenHost()
    {
        ClipToBounds = true;
        Transitions =
        [
            new DoubleTransition
            {
                Property = ExpansionProgressProperty,
                Duration = ExpansionDuration,
                Easing = new CubicEaseInOut()
            },
            new DoubleTransition
            {
                Property = OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(100),
                Easing = new CubicEaseOut()
            }
        ];
    }

    public double ExpansionProgress
    {
        get => GetValue(ExpansionProgressProperty);
        private set => SetCurrentValue(ExpansionProgressProperty, value);
    }

    internal void SetExpanded(bool expanded, bool animate)
    {
        _isExpanded = expanded;
        var revision = ++_stateRevision;
        CancelCollapseCompletion();

        if (!animate)
        {
            SetStateWithoutTransitions(expanded);
            return;
        }

        if (expanded)
        {
            IsHitTestVisible = true;

            if (!IsVisible)
            {
                // Keep the collapsed value for one render turn. The presenter can then
                // realize and measure lazy children before the visible interpolation starts.
                SetStateWithoutTransitions(expanded: false, keepVisible: true);
                IsHitTestVisible = true;
                Dispatcher.UIThread.Post(
                    () => StartExpansionIfCurrent(revision),
                    DispatcherPriority.Render);
                return;
            }

            ExpansionProgress = 1d;
            Opacity = 1d;
            return;
        }

        if (!IsVisible)
        {
            SetStateWithoutTransitions(expanded: false);
            return;
        }

        IsHitTestVisible = false;
        ExpansionProgress = 0d;
        Opacity = 0d;
        _collapseCompletion = DispatcherTimer.RunOnce(
            () => CompleteCollapseIfCurrent(revision),
            ExpansionDuration,
            DispatcherPriority.Render);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is null)
            return default;

        // The child must keep its natural height while only the host's contribution to
        // layout is interpolated. Animating the child Height would compress row templates
        // and a guessed MaxHeight would break for large or deeply nested branches.
        Child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        var desiredSize = Child.DesiredSize;
        return new Size(
            desiredSize.Width,
            desiredSize.Height * ExpansionProgress);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is not null)
        {
            var childHeight = Math.Max(finalSize.Height, Child.DesiredSize.Height);
            Child.Arrange(new Rect(0, 0, finalSize.Width, childHeight));
        }

        return finalSize;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ++_stateRevision;
        CancelCollapseCompletion();
        base.OnDetachedFromVisualTree(e);
    }

    private void StartExpansionIfCurrent(int revision)
    {
        if (revision != _stateRevision || !_isExpanded)
            return;

        ExpansionProgress = 1d;
        Opacity = 1d;
    }

    private void CompleteCollapseIfCurrent(int revision)
    {
        _collapseCompletion = null;
        if (revision != _stateRevision || _isExpanded)
            return;

        IsVisible = false;
    }

    private void SetStateWithoutTransitions(
        bool expanded,
        bool keepVisible = false)
    {
        var transitions = Transitions;
        Transitions = null;
        try
        {
            IsVisible = expanded || keepVisible;
            IsHitTestVisible = expanded;
            ExpansionProgress = expanded ? 1d : 0d;
            Opacity = expanded ? 1d : 0d;
        }
        finally
        {
            Transitions = transitions;
        }
    }

    private void CancelCollapseCompletion()
    {
        _collapseCompletion?.Dispose();
        _collapseCompletion = null;
    }
}
