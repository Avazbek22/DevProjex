namespace DevProjex.Avalonia.Coordinators;

internal static class ProjectTreeInventoryRetentionPolicy
{
    internal const int MinimumInventoryEntries = 50_000;
    internal const int MinimumReclaimableEntryGap = 25_000;
    internal const int MinimumShrinkRatio = 3;

    public static bool RequiresVisibleTreeMeasurement(int inventoryEntryCount) =>
        inventoryEntryCount >= MinimumInventoryEntries;

    public static bool ShouldReleaseReusedInventory(int inventoryEntryCount, int visibleTreeEntryCount)
    {
        if (!RequiresVisibleTreeMeasurement(inventoryEntryCount) || visibleTreeEntryCount < 0)
            return false;

        var reclaimableEntries = inventoryEntryCount - visibleTreeEntryCount;
        if (reclaimableEntries < MinimumReclaimableEntryGap)
            return false;

        return visibleTreeEntryCount == 0 ||
               inventoryEntryCount / MinimumShrinkRatio >= visibleTreeEntryCount;
    }

    public static int CountTreeEntries(TreeNodeDescriptor root)
    {
        var count = 0;
        var pending = new Stack<TreeNodeDescriptor>();
        pending.Push(root);

        while (pending.TryPop(out var node))
        {
            count++;
            for (var index = 0; index < node.Children.Count; index++)
                pending.Push(node.Children[index]);
        }

        return count;
    }
}
