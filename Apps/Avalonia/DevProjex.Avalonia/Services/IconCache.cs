using Avalonia.Media;
using Avalonia.Media.Imaging;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Avalonia.Services;

/// <summary>
/// Thread-safe LRU cache for file type icons.
/// Supports concurrent access for parallel tree building operations.
/// </summary>
public sealed class IconCache
{
    /// <summary>
    /// Maximum number of cached icons. Icons are typically limited (~50-100 types),
    /// but this provides a safety bound to prevent unbounded growth.
    /// </summary>
    private const int MaxCacheSize = 256;

    /// <summary>
    /// Number of least-recently-used items to evict when cache is full.
    /// </summary>
    private const int EvictionCount = 32;

    private readonly IIconStore _iconStore;
    private readonly Dictionary<string, IImage> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _accessOrder = new();
    private readonly Dictionary<string, LinkedListNode<string>> _accessNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public IconCache(IIconStore iconStore)
    {
        _iconStore = iconStore;
    }

    public IImage? GetIcon(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                // Move to end (most recently used)
                if (_accessNodes.TryGetValue(key, out var node))
                {
                    _accessOrder.Remove(node);
                    _accessOrder.AddLast(node);
                }
                return cached;
            }

            // LRU eviction: remove oldest entries when cache is full
            if (_cache.Count >= MaxCacheSize)
                EvictOldestUnsafe();

            var bytes = _iconStore.GetIconBytes(key);
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);
            _cache[key] = bitmap;

            var newNode = _accessOrder.AddLast(key);
            _accessNodes[key] = newNode;

            return bitmap;
        }
    }

    /// <summary>
    /// Evicts oldest entries. Must be called within lock.
    /// </summary>
    private void EvictOldestUnsafe()
    {
        var toEvict = Math.Min(EvictionCount, _cache.Count);
        for (int i = 0; i < toEvict && _accessOrder.First is not null; i++)
        {
            var oldest = _accessOrder.First!.Value;
            _accessOrder.RemoveFirst();
            _accessNodes.Remove(oldest);
            _cache.Remove(oldest);
        }
    }

    /// <summary>
    /// Clears all cached icons. Call when switching projects to free memory.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _accessOrder.Clear();
            _accessNodes.Clear();
        }
    }
}
