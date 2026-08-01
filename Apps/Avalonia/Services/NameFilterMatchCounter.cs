namespace DevProjex.Avalonia.Services;

internal static class NameFilterMatchCounter
{
    public static int CountMatchesUnderRoot(TreeNodeDescriptor root, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return 0;

        var count = 0;
        CountMatches(root.Children, query, ref count);
        return count;
    }

    private static void CountMatches(IReadOnlyList<TreeNodeDescriptor> nodes, string query, ref int count)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                count++;

            if (node.Children.Count > 0)
                CountMatches(node.Children, query, ref count);
        }
    }
}
