namespace DevProjex.Avalonia.Coordinators;

internal readonly record struct TreeDescriptorSearchResult(
    TreeDescriptorSearchIndex Index,
    int[] MatchIndices,
    bool UsedCache);

internal sealed class TreeDescriptorSearchIndex
{
    internal readonly record struct Entry(
        TreeNodeDescriptor Descriptor,
        int ParentIndex,
        int ChildIndex);

    private readonly Entry[] _entries;

    private TreeDescriptorSearchIndex(Entry[] entries)
    {
        _entries = entries;
    }

    public int Count => _entries.Length;

    public Entry this[int index] => _entries[index];

    public static TreeDescriptorSearchIndex Build(
        TreeNodeDescriptor root,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);

        var entries = new List<Entry>();
        var stack = new Stack<(
            TreeNodeDescriptor Node,
            int ParentIndex,
            int ChildIndex)>();
        stack.Push((root, -1, -1));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (node, parentIndex, childIndex) = stack.Pop();
            var nodeIndex = entries.Count;
            entries.Add(new Entry(node, parentIndex, childIndex));

            for (var index = node.Children.Count - 1; index >= 0; index--)
                stack.Push((node.Children[index], nodeIndex, index));
        }

        return new TreeDescriptorSearchIndex([.. entries]);
    }
}

internal sealed class TreeDescriptorSearchSession
{
    private const int QueryCacheLimit = 8;
    private const int MaxCachedMatchCount = 4096;

    private readonly object _sync = new();
    private readonly Dictionary<string, int[]> _queryCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _queryCacheLru = [];
    private readonly Dictionary<string, LinkedListNode<string>> _queryCacheNodes =
        new(StringComparer.OrdinalIgnoreCase);

    private TreeNodeDescriptor? _root;
    private string? _rootDisplayName;
    private TreeDescriptorSearchIndex? _index;
    private string? _lastQuery;
    private int[] _lastMatches = [];
    private long _requestVersion;
    private long _publishedRequestVersion;

    public TreeDescriptorSearchResult Search(
        TreeNodeDescriptor root,
        string rootDisplayName,
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rootDisplayName);
        ArgumentNullException.ThrowIfNull(query);

        cancellationToken.ThrowIfCancellationRequested();
        var requestVersion = Interlocked.Increment(ref _requestVersion);
        var index = GetOrBuildIndex(
            root,
            rootDisplayName,
            requestVersion,
            cancellationToken);

        int[]? source = null;
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isCurrentRoot =
                ReferenceEquals(_root, root) &&
                string.Equals(_rootDisplayName, rootDisplayName, StringComparison.Ordinal) &&
                ReferenceEquals(_index, index);
            if (isCurrentRoot && TryGetCachedMatches(query, out var cachedMatches))
                return new TreeDescriptorSearchResult(index, cachedMatches, UsedCache: true);

            if (isCurrentRoot &&
                !string.IsNullOrWhiteSpace(_lastQuery) &&
                query.StartsWith(_lastQuery, StringComparison.OrdinalIgnoreCase))
            {
                source = _lastMatches;
            }
            else if (isCurrentRoot)
            {
                string? bestPrefix = null;
                foreach (var cachedQuery in _queryCache.Keys)
                {
                    if (query.StartsWith(cachedQuery, StringComparison.OrdinalIgnoreCase) &&
                        (bestPrefix is null || cachedQuery.Length > bestPrefix.Length))
                    {
                        bestPrefix = cachedQuery;
                    }
                }

                if (bestPrefix is not null && TryGetCachedMatches(bestPrefix, out var prefixMatches))
                    source = prefixMatches;
            }
        }

        var matches = CollectMatches(index, rootDisplayName, source, query, cancellationToken);

        lock (_sync)
        {
            if (!ReferenceEquals(_root, root) ||
                !string.Equals(_rootDisplayName, rootDisplayName, StringComparison.Ordinal))
            {
                return new TreeDescriptorSearchResult(index, matches, UsedCache: false);
            }

            _lastQuery = query;
            _lastMatches = matches;
            CacheMatches(query, matches);
        }

        return new TreeDescriptorSearchResult(index, matches, UsedCache: false);
    }

    public void Clear()
    {
        lock (_sync)
        {
            _root = null;
            _rootDisplayName = null;
            _index = null;
            _lastQuery = null;
            _lastMatches = [];
            _queryCache.Clear();
            _queryCacheLru.Clear();
            _queryCacheNodes.Clear();
            _publishedRequestVersion = Interlocked.Increment(ref _requestVersion);
        }
    }

    private TreeDescriptorSearchIndex GetOrBuildIndex(
        TreeNodeDescriptor root,
        string rootDisplayName,
        long requestVersion,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_root, root) &&
                string.Equals(_rootDisplayName, rootDisplayName, StringComparison.Ordinal) &&
                _index is not null)
            {
                return _index;
            }
        }

        var index = TreeDescriptorSearchIndex.Build(root, cancellationToken);

        lock (_sync)
        {
            if (requestVersion < _publishedRequestVersion)
                return index;

            if (!ReferenceEquals(_root, root) ||
                !string.Equals(_rootDisplayName, rootDisplayName, StringComparison.Ordinal))
            {
                _root = root;
                _rootDisplayName = rootDisplayName;
                _lastQuery = null;
                _lastMatches = [];
                _queryCache.Clear();
                _queryCacheLru.Clear();
                _queryCacheNodes.Clear();
            }

            _publishedRequestVersion = requestVersion;
            _index = index;
            return index;
        }
    }

    private static int[] CollectMatches(
        TreeDescriptorSearchIndex index,
        string rootDisplayName,
        int[]? source,
        string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || index.Count == 0)
            return [];

        var capacity = source is null
            ? Math.Min(index.Count, 1024)
            : Math.Min(source.Length, 1024);
        var matches = new List<int>(capacity);

        if (source is null)
        {
            for (var entryIndex = 0; entryIndex < index.Count; entryIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (GetDisplayName(index, rootDisplayName, entryIndex)
                    .Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(entryIndex);
                }
            }
        }
        else
        {
            for (var indexInSource = 0; indexInSource < source.Length; indexInSource++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entryIndex = source[indexInSource];
                if (GetDisplayName(index, rootDisplayName, entryIndex)
                    .Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(entryIndex);
                }
            }
        }

        return [.. matches];
    }

    private static string GetDisplayName(
        TreeDescriptorSearchIndex index,
        string rootDisplayName,
        int entryIndex) =>
        entryIndex == 0
            ? rootDisplayName
            : index[entryIndex].Descriptor.DisplayName;

    private bool TryGetCachedMatches(string query, out int[] matches)
    {
        if (!_queryCache.TryGetValue(query, out matches!))
            return false;

        if (_queryCacheNodes.TryGetValue(query, out var node))
        {
            _queryCacheLru.Remove(node);
            _queryCacheLru.AddFirst(node);
        }

        return true;
    }

    private void CacheMatches(string query, int[] matches)
    {
        if (matches.Length > MaxCachedMatchCount)
            return;

        _queryCache[query] = matches;
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
            var oldest = _queryCacheLru.Last;
            if (oldest is null)
                break;

            _queryCacheLru.RemoveLast();
            _queryCacheNodes.Remove(oldest.Value);
            _queryCache.Remove(oldest.Value);
        }
    }
}
