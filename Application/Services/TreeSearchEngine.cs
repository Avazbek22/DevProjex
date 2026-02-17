namespace DevProjex.Application.Services;

public static class TreeSearchEngine
{
    public static IReadOnlyList<TNode> CollectMatches<TNode>(
        IEnumerable<TNode> roots,
        string query,
        Func<TNode, string> getName,
        Func<TNode, IEnumerable<TNode>> getChildren,
        StringComparison comparison)
    {
        var matches = new List<TNode>();
        foreach (var node in Traverse(roots, getChildren))
        {
            if (getName(node).Contains(query, comparison))
                matches.Add(node);
        }

        return matches;
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
}
