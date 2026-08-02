using Avalonia.Data;

namespace DevProjex.Avalonia.Controls;

public class ProjectTreeView : TreeView
{
    public static readonly StyledProperty<bool> IsExpansionAnimationEnabledProperty =
        AvaloniaProperty.Register<ProjectTreeView, bool>(
            nameof(IsExpansionAnimationEnabled),
            defaultValue: true);

    protected override Type StyleKeyOverride => typeof(TreeView);

    public bool IsExpansionAnimationEnabled
    {
        get => GetValue(IsExpansionAnimationEnabledProperty);
        set => SetValue(IsExpansionAnimationEnabledProperty, value);
    }

    protected override Control CreateContainerForItemOverride(
        object? item,
        int index,
        object? recycleKey)
    {
        var container = new ProjectTreeViewItem();
        container.SetExpansionAnimationEnabled(IsExpansionAnimationEnabled);
        return container;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != IsExpansionAnimationEnabledProperty)
            return;

        // Nested TreeViewItem instances are created by this owner as well. Updating the
        // realized graph keeps the setting immediate without adding a binding per row.
        foreach (var container in GetRealizedTreeContainers()
                     .OfType<ProjectTreeViewItem>())
        {
            container.SetExpansionAnimationEnabled(
                IsExpansionAnimationEnabled);
        }
    }

    protected override bool ShouldTriggerSelection(Visual selectable, PointerEventArgs eventArgs)
    {
        if (IsCheckBoxInteractionSource(eventArgs.Source))
            return false;

        return base.ShouldTriggerSelection(selectable, eventArgs);
    }

    internal static bool IsCheckBoxInteractionSource(object? source)
    {
        if (source is not Visual visual)
            return false;

        // Avalonia's selection trigger receives the TreeViewItem as the selectable
        // visual. The routed event source still points at the checkbox template part,
        // so inspect that source to keep checkbox clicks from selecting tree rows.
        return visual is CheckBox || visual.FindAncestorOfType<CheckBox>() is not null;
    }
}

internal sealed class ProjectTreeViewItem : TreeViewItem
{
    private const string ChevronThemeResourceKey =
        "DevProjexTreeExpandCollapseChevronTheme";
    private const string ExpansionAnimationClass =
        "expansion-animation-enabled";

    private IDisposable? _chevronVisibilityBinding;
    private ToggleButton? _chevron;
    private AnimatedTreeChildrenHost? _childrenHost;
    private bool _animateNextExpansionChange;
    private bool _isExpansionAnimationEnabled = true;

    protected override Type StyleKeyOverride => typeof(TreeViewItem);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        DetachChevronInteractionHandlers();
        base.OnApplyTemplate(e);

        _chevronVisibilityBinding?.Dispose();
        _chevronVisibilityBinding = null;
        _childrenHost = e.NameScope.Find<AnimatedTreeChildrenHost>(
            "PART_AnimatedChildrenHost");
        _childrenHost?.SetExpanded(IsExpanded, animate: false);

        if (e.NameScope.Find<ToggleButton>(
                "PART_ExpandCollapseChevron") is { } chevron)
        {
            _chevron = chevron;
            ApplyAnimatedChevronTheme(chevron);
            ApplyChevronAnimationClass();
            AttachChevronInteractionHandlers(chevron);

            // Fluent derives chevron visibility from ItemsSource emptiness. A lazy node
            // intentionally has an empty source before expansion, so descriptor metadata
            // must own this property at local-binding priority across container recycling.
            _chevronVisibilityBinding = chevron.Bind(
                IsVisibleProperty,
                new Binding(nameof(TreeNodeViewModel.HasChildren)));
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != IsExpandedProperty)
            return;

        var animate =
            _animateNextExpansionChange &&
            _isExpansionAnimationEnabled;
        _animateNextExpansionChange = false;
        _childrenHost?.SetExpanded(IsExpanded, animate);
    }

    internal void SetExpansionAnimationEnabled(bool enabled)
    {
        if (_isExpansionAnimationEnabled == enabled)
            return;

        _isExpansionAnimationEnabled = enabled;
        ApplyChevronAnimationClass();

        if (enabled)
            return;

        // Disabling motion is an immediate contract: finish any in-flight branch at
        // its logical state and cancel the delayed collapsed-presenter lifetime.
        _animateNextExpansionChange = false;
        _childrenHost?.SetExpanded(IsExpanded, animate: false);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var previousExpandedState = IsExpanded;
        var isSingleBranchToggle =
            e.KeyModifiers == KeyModifiers.None &&
            e.Key is Key.Left or Key.Right or Key.Enter or Key.Add or Key.Subtract;

        if (isSingleBranchToggle)
            _animateNextExpansionChange = true;

        base.OnKeyDown(e);

        if (IsExpanded == previousExpandedState)
            _animateNextExpansionChange = false;
    }

    private static void ApplyAnimatedChevronTheme(ToggleButton chevron)
    {
        var application = global::Avalonia.Application.Current;
        var themeVariant = application?.ActualThemeVariant ?? ThemeVariant.Light;
        if (application?.TryFindResource(
                ChevronThemeResourceKey,
                themeVariant,
                out var resource) == true &&
            resource is ControlTheme theme)
        {
            // The Fluent TreeViewItem template assigns its chevron theme at template
            // priority. A local value is required here so the lazy-node container can
            // preserve Fluent behavior while replacing only the abrupt glyph swap.
            chevron.Theme = theme;
        }
    }

    protected override void OnHeaderDoubleTapped(TappedEventArgs e)
    {
        if (ProjectTreeView.IsCheckBoxInteractionSource(e.Source))
        {
            // TreeViewItem still owns branch expand/collapse on header double-tap.
            // Checkbox double-clicks are handled here so selection cleanup does not
            // reintroduce the old accidental expand/collapse behavior.
            e.Handled = true;
            return;
        }

        var previousExpandedState = IsExpanded;
        _animateNextExpansionChange = true;
        base.OnHeaderDoubleTapped(e);

        if (IsExpanded == previousExpandedState)
            _animateNextExpansionChange = false;
    }

    private void AttachChevronInteractionHandlers(ToggleButton chevron)
    {
        chevron.AddHandler(
            PointerPressedEvent,
            OnChevronPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        chevron.Click += OnChevronClick;
        chevron.PointerCaptureLost += OnChevronPointerCaptureLost;
    }

    private void DetachChevronInteractionHandlers()
    {
        if (_chevron is null)
            return;

        _chevron.RemoveHandler(PointerPressedEvent, OnChevronPointerPressed);
        _chevron.Click -= OnChevronClick;
        _chevron.PointerCaptureLost -= OnChevronPointerCaptureLost;
        _chevron = null;
    }

    private void ApplyChevronAnimationClass()
        => _chevron?.Classes.Set(
            ExpansionAnimationClass,
            _isExpansionAnimationEnabled);

    private void OnChevronPointerPressed(
        object? sender,
        PointerPressedEventArgs e)
        => _animateNextExpansionChange = true;

    private void OnChevronClick(object? sender, RoutedEventArgs e)
        => _animateNextExpansionChange = false;

    private void OnChevronPointerCaptureLost(
        object? sender,
        PointerCaptureLostEventArgs e)
        => _animateNextExpansionChange = false;
}
