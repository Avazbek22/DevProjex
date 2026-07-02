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
    protected override Type StyleKeyOverride => typeof(TreeViewItem);

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
