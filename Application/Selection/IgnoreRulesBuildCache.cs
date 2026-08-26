namespace DevProjex.Application.Selection;

/// <summary>
/// Retains only the latest rules graph. Building under the lock prevents duplicate
/// filesystem-backed rule construction without keeping per-project entries alive.
/// </summary>
public sealed class IgnoreRulesBuildCache
{
	private readonly Func<
		string,
		IReadOnlyCollection<IgnoreOptionId>,
		IReadOnlyCollection<string>?,
		CancellationToken,
		IgnoreRules> _buildRules;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private CacheEntry? _entry;

	public IgnoreRulesBuildCache(
		Func<string, IReadOnlyCollection<IgnoreOptionId>, IReadOnlyCollection<string>?, IgnoreRules> buildRules)
		: this((path, options, roots, _) => buildRules(path, options, roots))
	{
	}

	public IgnoreRulesBuildCache(
		Func<
			string,
			IReadOnlyCollection<IgnoreOptionId>,
			IReadOnlyCollection<string>?,
			CancellationToken,
			IgnoreRules> buildRules)
	{
		_buildRules = buildRules ?? throw new ArgumentNullException(nameof(buildRules));
	}

	public IgnoreRules GetOrBuild(
		string path,
		IReadOnlyCollection<IgnoreOptionId> selectedIgnoreOptions,
		IReadOnlyCollection<string>? selectedRootFolders) =>
		GetOrBuildWithCancellation(
			path,
			selectedIgnoreOptions,
			selectedRootFolders,
			CancellationToken.None);

	public IgnoreRules GetOrBuildWithCancellation(
		string path,
		IReadOnlyCollection<IgnoreOptionId> selectedIgnoreOptions,
		IReadOnlyCollection<string>? selectedRootFolders,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(selectedIgnoreOptions);
		cancellationToken.ThrowIfCancellationRequested();

		var key = IgnoreRulesBuildCacheKeyBuilder.Build(path, selectedIgnoreOptions, selectedRootFolders);
		_gate.Wait(cancellationToken);
		try
		{
			if (_entry is not null && string.Equals(_entry.Key, key, StringComparison.Ordinal))
				return _entry.Rules;

			var rules = _buildRules(path, selectedIgnoreOptions, selectedRootFolders, cancellationToken);
			_entry = new CacheEntry(key, rules);
			return rules;
		}
		finally
		{
			_gate.Release();
		}
	}

	public void Invalidate()
	{
		_gate.Wait();
		try
		{
			_entry = null;
		}
		finally
		{
			_gate.Release();
		}
	}

	private sealed record CacheEntry(string Key, IgnoreRules Rules);
}
