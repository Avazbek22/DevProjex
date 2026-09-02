namespace DevProjex.Application.Selection;

/// <summary>
/// Retains only the latest rules graph. Each invalidation starts a new generation so
/// filesystem-backed construction from the previous generation cannot block or publish
/// into the current one.
/// </summary>
public sealed class IgnoreRulesBuildCache
{
	private readonly Func<
		string,
		IReadOnlyCollection<IgnoreOptionId>,
		IReadOnlyCollection<string>?,
		CancellationToken,
		IgnoreRules> _buildRules;
	private CacheGeneration _generation = new();

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
		while (true)
		{
			var generation = Volatile.Read(ref _generation);
			generation.Gate.Wait(cancellationToken);
			try
			{
				if (!ReferenceEquals(generation, Volatile.Read(ref _generation)))
					continue;

				if (generation.Entry is not null &&
				    string.Equals(generation.Entry.Key, key, StringComparison.Ordinal))
				{
					return generation.Entry.Rules;
				}

				var rules = _buildRules(path, selectedIgnoreOptions, selectedRootFolders, cancellationToken);
				if (ReferenceEquals(generation, Volatile.Read(ref _generation)))
					generation.Entry = new CacheEntry(key, rules);
				return rules;
			}
			finally
			{
				generation.Gate.Release();
			}
		}
	}

	public void Invalidate() => Interlocked.Exchange(ref _generation, new CacheGeneration());

	private sealed class CacheGeneration
	{
		public SemaphoreSlim Gate { get; } = new(1, 1);
		public CacheEntry? Entry { get; set; }
	}

	private sealed record CacheEntry(string Key, IgnoreRules Rules);
}
