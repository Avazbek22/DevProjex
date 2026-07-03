using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit;

[Collection("AvaloniaUI")]
public sealed class IconCacheTests
{
	[AvaloniaFact]
	public void GetIcon_WhitespaceKey_ReturnsNullAndDoesNotReadStore()
	{
		var store = new CountingIconStore();
		using var cache = new IconCache(store);

		Assert.Null(cache.GetIcon(null!));
		Assert.Null(cache.GetIcon(string.Empty));
		Assert.Null(cache.GetIcon("   "));
		Assert.Equal(0, store.TotalRequests);
	}

	[AvaloniaFact]
	public void GetIcon_SameKeyWithDifferentCase_ReturnsSameCachedInstance()
	{
		var store = new CountingIconStore();
		using var cache = new IconCache(store);

		var first = cache.GetIcon("csharp");
		var second = cache.GetIcon("CSHARP");

		Assert.NotNull(first);
		Assert.Same(first, second);
		Assert.Equal(1, store.TotalRequests);
		Assert.Equal(1, store.GetRequestCount("csharp"));
	}

	[AvaloniaFact]
	public void Clear_RemovesCachedEntries_AndForcesReload()
	{
		var store = new CountingIconStore();
		using var cache = new IconCache(store);

		var first = cache.GetIcon("json");
		cache.Clear();
		var second = cache.GetIcon("json");

		Assert.NotNull(first);
		Assert.NotNull(second);
		Assert.NotSame(first, second);
		Assert.Equal(2, store.GetRequestCount("json"));
	}

	[AvaloniaFact]
	public void GetIcon_WhenCapacityExceeded_EvictsLeastRecentlyUsedEntries()
	{
		var store = new CountingIconStore();
		using var cache = new IconCache(store);

		for (var i = 0; i < 257; i++)
		{
			var key = $"k{i}";
			Assert.NotNull(cache.GetIcon(key));
		}

		Assert.NotNull(cache.GetIcon("k0"));

		Assert.Equal(2, store.GetRequestCount("k0"));
	}

	private sealed class CountingIconStore : IIconStore
	{
		private readonly byte[] _pngBytes = File.ReadAllBytes(ResolveSampleIconPath());

		private readonly Dictionary<string, int> _requests = new(StringComparer.OrdinalIgnoreCase);

		public IReadOnlyCollection<string> Keys => _requests.Keys.ToArray();

		public int TotalRequests => _requests.Values.Sum();

		public byte[] GetIconBytes(string key)
		{
			if (_requests.TryGetValue(key, out var count))
				_requests[key] = count + 1;
			else
				_requests[key] = 1;

			return _pngBytes;
		}

		public int GetRequestCount(string key)
		{
			return _requests.TryGetValue(key, out var count) ? count : 0;
		}

		private static string ResolveSampleIconPath()
		{
			var dir = AppContext.BaseDirectory;
			while (dir is not null)
			{
				var candidate = Path.Combine(dir, "Assets", "IconPacks", "Default", "folder.png");
				if (File.Exists(candidate))
					return candidate;

				if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, "DevProjex.sln")))
					break;

				dir = Directory.GetParent(dir)?.FullName;
			}

			throw new InvalidOperationException("Could not locate sample icon for IconCacheTests.");
		}
	}
}
