namespace DevProjex.Application.Selection;

/// <summary>
/// Retains only the latest rules graph. Building under the lock prevents duplicate
/// filesystem-backed rule construction without keeping per-project entries alive.
/// </summary>
public sealed class IgnoreRulesBuildCache(
	Func<string, IReadOnlyCollection<IgnoreOptionId>, IReadOnlyCollection<string>?, IgnoreRules> buildRules)
{
	private readonly Lock _sync = new();
	private CacheEntry? _entry;

	public IgnoreRules GetOrBuild(
		string path,
		IReadOnlyCollection<IgnoreOptionId> selectedIgnoreOptions,
		IReadOnlyCollection<string>? selectedRootFolders)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(selectedIgnoreOptions);

		var key = IgnoreRulesBuildCacheKeyBuilder.Build(path, selectedIgnoreOptions, selectedRootFolders);
		lock (_sync)
		{
			if (_entry is not null && string.Equals(_entry.Key, key, StringComparison.Ordinal))
				return _entry.Rules;

			var rules = buildRules(path, selectedIgnoreOptions, selectedRootFolders);
			_entry = new CacheEntry(key, rules);
			return rules;
		}
	}

	public void Invalidate()
	{
		lock (_sync)
			_entry = null;
	}

	private sealed record CacheEntry(string Key, IgnoreRules Rules);
}
