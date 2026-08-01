using Avalonia.Data;

namespace DevProjex.Avalonia.Controls;

public class ProjectTreeView : TreeView
{
    protected override Type StyleKeyOverride => typeof(TreeView);

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
        => new ProjectTreeViewItem();

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

    private IDisposable? _chevronVisibilityBinding;

    protected override Type StyleKeyOverride => typeof(TreeViewItem);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _chevronVisibilityBinding?.Dispose();
        _chevronVisibilityBinding = null;

        if (e.NameScope.Find<ToggleButton>(
                "PART_ExpandCollapseChevron") is { } chevron)
        {
            ApplyAnimatedChevronTheme(chevron);

            // Fluent derives chevron visibility from ItemsSource emptiness. A lazy node
            // intentionally has an empty source before expansion, so descriptor metadata
            // must own this property at local-binding priority across container recycling.
            _chevronVisibilityBinding = chevron.Bind(
                IsVisibleProperty,
                new Binding(nameof(TreeNodeViewModel.HasChildren)));
        }
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

        base.OnHeaderDoubleTapped(e);
    }
}
