namespace DevProjex.Avalonia.Coordinators;

internal sealed class TreeNameFilterSession
{
    private const int QueryCacheLimit = 8;
    private const int ParallelRootChildrenThreshold = 8;
    private static readonly int MaxParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8);

    private readonly object _sync = new();
    private readonly Dictionary<string, TreeNodeDescriptor> _queryCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _queryCacheLru = [];
    private readonly Dictionary<string, LinkedListNode<string>> _queryCacheNodes =
        new(StringComparer.OrdinalIgnoreCase);

    private TreeNodeDescriptor? _baseRoot;
    private TreeNodeDescriptor? _lastFilteredRoot;
    private string? _lastQuery;
    private int _generation;

    public BuildTreeResult Build(
        BuildTreeResult baseTree,
        string? query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseTree);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedQuery = query?.Trim() ?? string.Empty;
        TreeNodeDescriptor sourceRoot;
        int generation;

        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureBaseTree(baseTree.Root);

            if (normalizedQuery.Length == 0)
            {
                _generation++;
                ClearQueryState();
                return baseTree;
            }

            if (TryGetCachedRoot(normalizedQuery, out var cachedRoot))
            {
                _lastFilteredRoot = cachedRoot;
                _lastQuery = normalizedQuery;
                return CreateResult(baseTree, cachedRoot);
            }

            sourceRoot = ResolveBestSource(normalizedQuery);
            generation = _generation;
        }

        var filteredRoot = FilterRoot(sourceRoot, normalizedQuery, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_generation == generation && ReferenceEquals(_baseRoot, baseTree.Root))
            {
                _lastFilteredRoot = filteredRoot;
                _lastQuery = normalizedQuery;
                Cache(normalizedQuery, filteredRoot);
            }
        }

        return CreateResult(baseTree, filteredRoot);
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _baseRoot = null;
            _generation++;
            ClearQueryState();
        }
    }

    private void EnsureBaseTree(TreeNodeDescriptor root)
    {
        if (ReferenceEquals(_baseRoot, root))
            return;

        _baseRoot = root;
        _generation++;
        ClearQueryState();
    }

    private TreeNodeDescriptor ResolveBestSource(string query)
    {
        if (_baseRoot is null)
            throw new InvalidOperationException("The filter session has no base tree.");

        if (_lastFilteredRoot is not null &&
            !string.IsNullOrWhiteSpace(_lastQuery) &&
            query.StartsWith(_lastQuery, StringComparison.OrdinalIgnoreCase))
        {
            return _lastFilteredRoot;
        }

        string? bestPrefix = null;
        foreach (var cachedQuery in _queryCache.Keys)
        {
            if (query.StartsWith(cachedQuery, StringComparison.OrdinalIgnoreCase) &&
                (bestPrefix is null || cachedQuery.Length > bestPrefix.Length))
            {
                bestPrefix = cachedQuery;
            }
        }

        return bestPrefix is not null && TryGetCachedRoot(bestPrefix, out var prefixRoot)
            ? prefixRoot
            : _baseRoot;
    }

    private bool TryGetCachedRoot(string query, out TreeNodeDescriptor root)
    {
        if (!_queryCache.TryGetValue(query, out root!))
            return false;

        if (_queryCacheNodes.TryGetValue(query, out var node))
        {
            _queryCacheLru.Remove(node);
            _queryCacheLru.AddFirst(node);
        }

        return true;
    }

    private void Cache(string query, TreeNodeDescriptor root)
    {
        _queryCache[query] = root;
        if (_queryCacheNodes.TryGetValue(query, out var existingNode))
        {
            _queryCacheLru.Remove(existingNode);
            _queryCacheLru.AddFirst(existingNode);
            return;
        }

        var node = new LinkedListNode<string>(query);
        _queryCacheLru.AddFirst(node);
        _queryCacheNodes[query] = node;

        while (_queryCacheNodes.Count > QueryCacheLimit)
        {
            var last = _queryCacheLru.Last;
            if (last is null)
                break;

            _queryCacheLru.RemoveLast();
            _queryCacheNodes.Remove(last.Value);
            _queryCache.Remove(last.Value);
        }
    }

    private void ClearQueryState()
    {
        _lastFilteredRoot = null;
        _lastQuery = null;
        _queryCache.Clear();
        _queryCacheLru.Clear();
        _queryCacheNodes.Clear();
    }

    private static BuildTreeResult CreateResult(BuildTreeResult baseTree, TreeNodeDescriptor root) =>
        new(
            Root: root,
            RootAccessDenied: baseTree.RootAccessDenied,
            HadAccessDenied: baseTree.HadAccessDenied);

    private static TreeNodeDescriptor FilterRoot(
        TreeNodeDescriptor root,
        string query,
        CancellationToken cancellationToken)
    {
        var children = root.Children;
        if (children.Count < ParallelRootChildrenThreshold || MaxParallelism <= 1)
            return FilterRootSequential(root, query, cancellationToken);

        var filteredChildren = new TreeNodeDescriptor?[children.Count];
        Parallel.For(
            fromInclusive: 0,
            toExclusive: children.Count,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Min(MaxParallelism, children.Count)
            },
            index => filteredChildren[index] = FilterNode(children[index], query, cancellationToken));

        var changed = false;
        var matchedChildren = new List<TreeNodeDescriptor>(children.Count);
        for (var index = 0; index < children.Count; index++)
        {
            var filteredChild = filteredChildren[index];
            if (filteredChild is null)
            {
                changed = true;
                continue;
            }

            changed |= !ReferenceEquals(filteredChild, children[index]);
            matchedChildren.Add(filteredChild);
        }

        return changed ? root with { Children = matchedChildren } : root;
    }

    private static TreeNodeDescriptor FilterRootSequential(
        TreeNodeDescriptor root,
        string query,
        CancellationToken cancellationToken)
    {
        List<TreeNodeDescriptor>? filteredChildren = null;
        var originalChildren = root.Children;

        for (var index = 0; index < originalChildren.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var originalChild = originalChildren[index];
            var filteredChild = FilterNode(originalChild, query, cancellationToken);

            if (filteredChild is null)
            {
                filteredChildren ??= CopyPrefix(originalChildren, index, capacityLimit: 16);
                continue;
            }

            if (filteredChildren is not null)
            {
                filteredChildren.Add(filteredChild);
                continue;
            }

            if (!ReferenceEquals(filteredChild, originalChild))
            {
                filteredChildren = CopyPrefix(originalChildren, index, capacityLimit: 16);
                filteredChildren.Add(filteredChild);
            }
        }

        return filteredChildren is null ? root : root with { Children = filteredChildren };
    }

    private static TreeNodeDescriptor? FilterNode(
        TreeNodeDescriptor node,
        string query,
        CancellationToken cancellationToken)
    {
        var pending = new List<FilterFrame>
        {
            new(node, query)
        };
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frameIndex = pending.Count - 1;
            var frame = pending[frameIndex];
            if (frame.NextChildIndex < frame.Node.Children.Count)
            {
                var child = frame.Node.Children[frame.NextChildIndex++];
                pending[frameIndex] = frame;
                pending.Add(new FilterFrame(child, query));
                continue;
            }

            pending.RemoveAt(frameIndex);
            var filteredNode = frame.Complete();
            if (pending.Count == 0)
                return filteredNode;

            var parentIndex = pending.Count - 1;
            var parent = pending[parentIndex];
            parent.AcceptChild(filteredNode);
            pending[parentIndex] = parent;
        }

        throw new InvalidOperationException("Tree filtering did not produce a root node.");
    }

    private struct FilterFrame(TreeNodeDescriptor node, string query)
    {
        private List<TreeNodeDescriptor>? _filteredChildren;
        private int _matchedChildrenCount;

        public TreeNodeDescriptor Node { get; } = node;
        public bool SelfMatches { get; } = node.DisplayName.Contains(
            query,
            StringComparison.OrdinalIgnoreCase);
        public int NextChildIndex { get; set; }

        public void AcceptChild(TreeNodeDescriptor? filteredChild)
        {
            var childIndex = NextChildIndex - 1;
            if (filteredChild is null)
            {
                _filteredChildren ??= CopyPrefix(Node.Children, childIndex, capacityLimit: 8);
                return;
            }

            _matchedChildrenCount++;
            if (_filteredChildren is not null)
            {
                _filteredChildren.Add(filteredChild);
                return;
            }

            var originalChild = Node.Children[childIndex];
            if (!ReferenceEquals(filteredChild, originalChild))
            {
                _filteredChildren = CopyPrefix(Node.Children, childIndex, capacityLimit: 8);
                _filteredChildren.Add(filteredChild);
            }
        }

        public TreeNodeDescriptor? Complete() =>
            !SelfMatches && _matchedChildrenCount == 0
                ? null
                : _filteredChildren is null
                    ? Node
                    : Node with { Children = _filteredChildren };
    }

    private static List<TreeNodeDescriptor> CopyPrefix(
        IReadOnlyList<TreeNodeDescriptor> source,
        int count,
        int capacityLimit)
    {
        var capacity = Math.Min(source.Count, Math.Max(count, capacityLimit));
        var result = new List<TreeNodeDescriptor>(capacity);
        for (var index = 0; index < count; index++)
            result.Add(source[index]);
        return result;
    }
}
