namespace DevProjex.Application.Services;

public static class TreeSearchEngine
{
    public readonly record struct SearchCollectionResult<TNode>(
        IReadOnlyList<TNode> Matches,
        int VisitedCount);

    public readonly record struct FilterPresentationResult(
        int MatchCount,
        int VisitedCount);

    public static IReadOnlyList<TNode> CollectMatches<TNode>(
        IEnumerable<TNode> roots,
        string query,
        Func<TNode, string> getName,
        Func<TNode, IEnumerable<TNode>> getChildren,
        StringComparison comparison)
    {
        return CollectMatchesWithStats(roots, query, getName, getChildren, comparison).Matches;
    }

    public static SearchCollectionResult<TNode> CollectMatchesWithStats<TNode>(
        IEnumerable<TNode> roots,
        string query,
        Func<TNode, string> getName,
        Func<TNode, IEnumerable<TNode>> getChildren,
        StringComparison comparison)
    {
        var matches = new List<TNode>();
        var visitedCount = 0;
        foreach (var node in Traverse(roots, getChildren))
        {
            visitedCount++;
            if (getName(node).Contains(query, comparison))
                matches.Add(node);
        }

        return new SearchCollectionResult<TNode>(matches, visitedCount);
    }

    public static void ApplySmartExpandForSearch<TNode>(
        IEnumerable<TNode> roots,
        string query,
        Func<TNode, string> getName,
        Func<TNode, IEnumerable<TNode>> getChildren,
        Func<TNode, bool> hasChildren,
        Action<TNode, bool> setExpanded)
    {
        foreach (var node in roots)
            ApplySmartExpandForSearchNode(node, query, getName, getChildren, hasChildren, setExpanded);
    }

    public static void ApplySmartExpandForFilter<TNode>(
        IEnumerable<TNode> roots,
        string query,
        Func<TNode, string> getName,
        Func<TNode, IEnumerable<TNode>> getChildren,
        Action<TNode, bool> setExpanded)
    {
        foreach (var node in roots)
            ApplySmartExpandForFilterNode(node, query, getName, getChildren, setExpanded);
    }

    public static FilterPresentationResult ApplyFilterPresentation<TNode>(
        IEnumerable<TNode> roots,
        string query,
        Func<TNode, string> getName,
        Func<TNode, IEnumerable<TNode>> getChildren,
        Action<TNode, bool> setHighlighted,
        Action<TNode, bool> setExpanded)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(getName);
        ArgumentNullException.ThrowIfNull(getChildren);
        ArgumentNullException.ThrowIfNull(setHighlighted);
        ArgumentNullException.ThrowIfNull(setExpanded);

        var entries = new List<FilterPresentationEntry<TNode>>();
        var stack = new Stack<(TNode Node, int ParentIndex, bool IsRoot)>();
        foreach (var root in roots)
            stack.Push((root, ParentIndex: -1, IsRoot: true));

        while (stack.Count > 0)
        {
            var (node, parentIndex, isRoot) = stack.Pop();
            var selfMatches = getName(node).Contains(query, StringComparison.OrdinalIgnoreCase);
            var nodeIndex = entries.Count;
            entries.Add(new FilterPresentationEntry<TNode>(
                node,
                parentIndex,
                isRoot,
                selfMatches,
                HasMatchingDescendant: false));

            foreach (var child in getChildren(node))
                stack.Push((child, nodeIndex, IsRoot: false));
        }

        var matchCount = 0;
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index];
            setHighlighted(entry.Node, entry.SelfMatches);

            if (entry.HasMatchingDescendant)
                setExpanded(entry.Node, true);
            else if (!entry.SelfMatches)
                setExpanded(entry.Node, false);

            if (entry.SelfMatches && !entry.IsRoot)
                matchCount++;

            if (entry.ParentIndex < 0 ||
                !entry.SelfMatches && !entry.HasMatchingDescendant)
            {
                continue;
            }

            var parent = entries[entry.ParentIndex];
            entries[entry.ParentIndex] = parent with { HasMatchingDescendant = true };
        }

        return new FilterPresentationResult(matchCount, entries.Count);
    }

    private static bool ApplySmartExpandForSearchNode<TNode>(
        TNode node,
        string query,
        Func<TNode, string> getName,
        Func<TNode, IEnumerable<TNode>> getChildren,
        Func<TNode, bool> hasChildren,
        Action<TNode, bool> setExpanded)
    {
        bool hasMatchingDescendant = false;
        bool selfMatches = getName(node).Contains(query, StringComparison.OrdinalIgnoreCase);

        foreach (var child in getChildren(node))
        {
            if (ApplySmartExpandForSearchNode(child, query, getName, getChildren, hasChildren, setExpanded))
                hasMatchingDescendant = true;
        }

        if (hasMatchingDescendant)
            setExpanded(node, true);
        else if (!selfMatches && hasChildren(node))
            setExpanded(node, false);

        return selfMatches || hasMatchingDescendant;
    }

    private static bool ApplySmartExpandForFilterNode<TNode>(
        TNode node,
        string query,
        Func<TNode, string> getName,
        Func<TNode, IEnumerable<TNode>> getChildren,
        Action<TNode, bool> setExpanded)
    {
        bool hasMatchingDescendant = false;
        bool selfMatches = getName(node).Contains(query, StringComparison.OrdinalIgnoreCase);

        foreach (var child in getChildren(node))
        {
            if (ApplySmartExpandForFilterNode(child, query, getName, getChildren, setExpanded))
                hasMatchingDescendant = true;
        }

        if (hasMatchingDescendant)
            setExpanded(node, true);
        else if (!selfMatches)
            setExpanded(node, false);

        return selfMatches || hasMatchingDescendant;
    }

    private static IEnumerable<TNode> Traverse<TNode>(
        IEnumerable<TNode> roots,
        Func<TNode, IEnumerable<TNode>> getChildren)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in Traverse(getChildren(root), getChildren))
                yield return child;
        }
    }

    private readonly record struct FilterPresentationEntry<TNode>(
        TNode Node,
        int ParentIndex,
        bool IsRoot,
        bool SelfMatches,
        bool HasMatchingDescendant);
}
