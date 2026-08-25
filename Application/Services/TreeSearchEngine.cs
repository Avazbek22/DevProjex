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
        ApplySmartExpansion(
            roots,
            query,
            getName,
            getChildren,
            setExpanded,
            collapseWhenNoMatch: hasChildren);
    }

    public static void ApplySmartExpandForFilter<TNode>(
        IEnumerable<TNode> roots,
        string query,
        Func<TNode, string> getName,
        Func<TNode, IEnumerable<TNode>> getChildren,
        Action<TNode, bool> setExpanded)
    {
        ApplySmartExpansion(
            roots,
            query,
            getName,
            getChildren,
            setExpanded,
            collapseWhenNoMatch: static _ => true);
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

    private static void ApplySmartExpansion<TNode>(
        IEnumerable<TNode> roots,
        string query,
        Func<TNode, string> getName,
        Func<TNode, IEnumerable<TNode>> getChildren,
        Action<TNode, bool> setExpanded,
        Func<TNode, bool> collapseWhenNoMatch)
    {
        var entries = new List<SmartExpansionEntry<TNode>>();
        var stack = new Stack<(TNode Node, int ParentIndex)>();
        foreach (var root in roots)
            stack.Push((root, ParentIndex: -1));

        while (stack.Count > 0)
        {
            var (node, parentIndex) = stack.Pop();
            var nodeIndex = entries.Count;
            entries.Add(new SmartExpansionEntry<TNode>(
                node,
                parentIndex,
                getName(node).Contains(query, StringComparison.OrdinalIgnoreCase),
                HasMatchingDescendant: false));

            foreach (var child in getChildren(node))
                stack.Push((child, nodeIndex));
        }

        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index];
            if (entry.HasMatchingDescendant)
                setExpanded(entry.Node, true);
            else if (!entry.SelfMatches && collapseWhenNoMatch(entry.Node))
                setExpanded(entry.Node, false);

            if (entry.ParentIndex < 0 ||
                !entry.SelfMatches && !entry.HasMatchingDescendant)
            {
                continue;
            }

            var parent = entries[entry.ParentIndex];
            entries[entry.ParentIndex] = parent with { HasMatchingDescendant = true };
        }
    }

    private static IEnumerable<TNode> Traverse<TNode>(
        IEnumerable<TNode> roots,
        Func<TNode, IEnumerable<TNode>> getChildren)
    {
        var enumerators = new Stack<IEnumerator<TNode>>();
        enumerators.Push(roots.GetEnumerator());
        try
        {
            while (enumerators.Count > 0)
            {
                var current = enumerators.Peek();
                if (!current.MoveNext())
                {
                    current.Dispose();
                    enumerators.Pop();
                    continue;
                }

                var node = current.Current;
                yield return node;
                enumerators.Push(getChildren(node).GetEnumerator());
            }
        }
        finally
        {
            while (enumerators.TryPop(out var enumerator))
                enumerator.Dispose();
        }
    }

    private readonly record struct FilterPresentationEntry<TNode>(
        TNode Node,
        int ParentIndex,
        bool IsRoot,
        bool SelfMatches,
        bool HasMatchingDescendant);

    private readonly record struct SmartExpansionEntry<TNode>(
        TNode Node,
        int ParentIndex,
        bool SelfMatches,
        bool HasMatchingDescendant);
}
