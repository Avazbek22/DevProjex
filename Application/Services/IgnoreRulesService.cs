using System.Collections.Frozen;

namespace DevProjex.Application.Services;

public sealed class IgnoreRulesService(
	SmartIgnoreService smartIgnore,
	ProjectScopeDiscoveryService? projectScopeDiscovery = null)
{
	private const int CacheLimit = 64;
	private static readonly object CacheSync = new();
	private static readonly Dictionary<string, GitIgnoreCacheEntry> GitIgnoreCache =
		new(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
	private readonly ProjectScopeDiscoveryService _projectScopeDiscovery =
		projectScopeDiscovery ?? new ProjectScopeDiscoveryService(smartIgnore);

	private static readonly StringComparer PathStringComparer = OperatingSystem.IsLinux()
		? StringComparer.Ordinal
		: StringComparer.OrdinalIgnoreCase;

	public IgnoreRules Build(string rootPath, IReadOnlyCollection<IgnoreOptionId> selectedOptions) =>
		Build(rootPath, selectedOptions, selectedRootFolders: null);

	public IgnoreRules Build(
		string rootPath,
		IReadOnlyCollection<IgnoreOptionId> selectedOptions,
		IReadOnlyCollection<string>? selectedRootFolders)
	{
		var context = DiscoverProjectScanContext(rootPath, selectedRootFolders);
		var availability = BuildRuntimeIgnoreOptionsAvailability(context);
		var requestedGitIgnore = availability.IncludeGitIgnore &&
								 selectedOptions.Contains(IgnoreOptionId.UseGitIgnore);

		// Smart ignore is hidden for single-project gitignore scenario and follows UseGitIgnore toggle there.
		var smartIgnoreFollowsGitIgnore = !availability.IncludeSmartIgnore &&
		                                  context.IsSingleScopeWithGitIgnore;
		var useSmartIgnore = availability.IncludeSmartIgnore
			? selectedOptions.Contains(IgnoreOptionId.SmartIgnore)
			: context.IsSingleScopeWithGitIgnore && requestedGitIgnore;

		var candidateScopedGitMatchers = availability.IncludeGitIgnore
			? BuildScopedGitIgnoreMatchers(context.Scopes).ToArray()
			: [];
		var candidateGitIgnoreMatcher = candidateScopedGitMatchers.Length == 1
			? candidateScopedGitMatchers[0].Matcher
			: GitIgnoreMatcher.Empty;
		var useGitIgnore = requestedGitIgnore && candidateScopedGitMatchers.Length > 0;
		var scopedMatchers = useGitIgnore
			? candidateScopedGitMatchers
			: Array.Empty<ScopedGitIgnoreMatcher>();
		var gitIgnoreMatcher = useGitIgnore && scopedMatchers.Length == 1
			? scopedMatchers[0].Matcher
			: GitIgnoreMatcher.Empty;

		var smartCandidate = availability.IncludeSmartIgnore || smartIgnoreFollowsGitIgnore
			? BuildScopedSmartIgnore(context)
			: ScopedSmartIgnoreBuildResult.Empty;
		var candidateSmartScopeRoots = smartCandidate.ScopedMatchers
			.Select(static matcher => matcher.ScopeRootPath)
			.Distinct(PathStringComparer)
			.ToArray();

		IReadOnlySet<string> smartFolders;
		IReadOnlySet<string> smartFiles;
		IReadOnlyList<string> smartScopeRoots;
		IReadOnlyList<ScopedSmartIgnoreMatcher> scopedSmartMatchers;
		if (useSmartIgnore)
		{
			// Active smart rules reuse the candidate set built for impact probing. This keeps
			// the selection refresh deterministic and avoids rebuilding stack descriptors.
			smartFolders = smartCandidate.FolderNames;
			smartFiles = smartCandidate.FileNames;
			scopedSmartMatchers = smartCandidate.ScopedMatchers;
			smartScopeRoots = candidateSmartScopeRoots;
		}
		else
		{
			smartFolders = EmptyStringSet;
			smartFiles = EmptyStringSet;
			smartScopeRoots = [];
			scopedSmartMatchers = [];
		}

		return new IgnoreRules(
			IgnoreHiddenFolders: selectedOptions.Contains(IgnoreOptionId.HiddenFolders),
			IgnoreHiddenFiles: selectedOptions.Contains(IgnoreOptionId.HiddenFiles),
			IgnoreDotFolders: selectedOptions.Contains(IgnoreOptionId.DotFolders),
			IgnoreDotFiles: selectedOptions.Contains(IgnoreOptionId.DotFiles),
			SmartIgnoredFolders: smartFolders,
			SmartIgnoredFiles: smartFiles)
		{
			IgnoreEmptyFolders = selectedOptions.Contains(IgnoreOptionId.EmptyFolders),
			IgnoreEmptyFiles = selectedOptions.Contains(IgnoreOptionId.EmptyFiles),
			IgnoreExtensionlessFiles = selectedOptions.Contains(IgnoreOptionId.ExtensionlessFiles),
			UseGitIgnore = useGitIgnore,
			UseSmartIgnore = useSmartIgnore,
			GitIgnoreMatcher = gitIgnoreMatcher,
			ScopedGitIgnoreMatchers = scopedMatchers,
			GitIgnoreCandidateMatcher = candidateGitIgnoreMatcher,
			ScopedGitIgnoreCandidateMatchers = candidateScopedGitMatchers,
			SmartIgnoreScopeRoots = smartScopeRoots,
			ScopedSmartIgnoreMatchers = scopedSmartMatchers,
			SmartIgnoreCandidateScopeRoots = candidateSmartScopeRoots,
			ScopedSmartIgnoreCandidateMatchers = smartCandidate.ScopedMatchers,
			SmartIgnoreCandidateFolders = smartCandidate.FolderNames,
			SmartIgnoreCandidateFiles = smartCandidate.FileNames,
			SmartIgnoreFollowsGitIgnore = smartIgnoreFollowsGitIgnore
		};
	}

	public IgnoreOptionsAvailability GetIgnoreOptionsAvailability(
		string rootPath,
		IReadOnlyCollection<string> selectedRootFolders)
	{
		var context = DiscoverProjectScanContext(rootPath, selectedRootFolders);
		return BuildUiIgnoreOptionsAvailability(context);
	}

	private static readonly IReadOnlySet<string> EmptyStringSet =
		Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private static IgnoreOptionsAvailability BuildRuntimeIgnoreOptionsAvailability(ProjectScanContext context)
	{
		if (context.Scopes.Count == 0)
			return new IgnoreOptionsAvailability(IncludeGitIgnore: false, IncludeSmartIgnore: false);

		var includeGitIgnore = context.HasAnyGitIgnore;
		var includeSmartIgnore = !context.IsSingleScopeWithGitIgnore && context.HasAnyWithoutGitIgnore;
		return new IgnoreOptionsAvailability(includeGitIgnore, includeSmartIgnore);
	}

	private IgnoreOptionsAvailability BuildUiIgnoreOptionsAvailability(ProjectScanContext context)
	{
		if (context.Scopes.Count == 0)
			return new IgnoreOptionsAvailability(IncludeGitIgnore: false, IncludeSmartIgnore: false);

		var includeGitIgnore = context.HasAnyGitIgnore;
		var includeSmartIgnore = !context.IsSingleScopeWithGitIgnore &&
								 context.HasAnyWithoutGitIgnore &&
								 HasRelevantSmartIgnoreCandidates(context);
		return new IgnoreOptionsAvailability(includeGitIgnore, includeSmartIgnore);
	}

	private bool HasRelevantSmartIgnoreCandidates(ProjectScanContext context)
	{
		// Direct iteration avoids allocation - early return on first match
		foreach (var scope in context.Scopes)
		{
			if (scope.HasGitIgnore)
				continue;

			if (scope.HasProjectMarker || HasSmartCandidatesInRootEntries(context, scope.RootPath))
				return true;
		}

		return false;
	}

	private bool HasSmartCandidatesInRootEntries(ProjectScanContext context, string rootPath)
	{
		var smart = context.GetSmartIgnoreResult(rootPath, smartIgnore);
		if (smart.FolderNames.Count == 0)
			return false;

		try
		{
			foreach (var directory in Directory.EnumerateDirectories(rootPath, "*", SearchOption.TopDirectoryOnly))
			{
				var name = Path.GetFileName(directory);
				if (smart.FolderNames.Contains(name))
					return true;
			}
		}
		catch
		{
			// Best-effort check.
		}

		return false;
	}

	private ScopedSmartIgnoreBuildResult BuildScopedSmartIgnore(ProjectScanContext context)
	{
		var mergeSync = new object();
		var folderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var scopedMatchers = new List<ScopedSmartIgnoreMatcher>(context.Scopes.Count);

		Parallel.ForEach(
			context.Scopes,
			ScanParallelismPolicy.CreateOptions(),
			static () => new LocalSmartIgnoreBuildState(),
			(scope, _, localState) =>
			{
				var smart = context.GetSmartIgnoreResult(scope.RootPath, smartIgnore);
				foreach (var folder in smart.FolderNames)
					localState.FolderNames.Add(folder);
				foreach (var file in smart.FileNames)
					localState.FileNames.Add(file);

				if (smart.FolderNames.Count > 0 || smart.FileNames.Count > 0)
				{
					localState.ScopedMatchers.Add(new ScopedSmartIgnoreMatcher(
						scope.RootPath,
						FreezeOrEmpty(smart.FolderNames),
						FreezeOrEmpty(smart.FileNames)));
				}

				return localState;
			},
			localState =>
			{
				if (localState.IsEmpty)
					return;

				lock (mergeSync)
				{
					folderNames.UnionWith(localState.FolderNames);
					fileNames.UnionWith(localState.FileNames);
					scopedMatchers.AddRange(localState.ScopedMatchers);
				}
			});

		var orderedScopedMatchers = scopedMatchers
			.OrderBy(static matcher => matcher.ScopeRootPath.Length)
			.ThenBy(static matcher => matcher.ScopeRootPath, PathComparer.Default)
			.ToArray();

		return new ScopedSmartIgnoreBuildResult(
			FreezeOrEmpty(folderNames),
			FreezeOrEmpty(fileNames),
			orderedScopedMatchers);
	}

	private static IReadOnlySet<string> FreezeOrEmpty(HashSet<string> values) =>
		values.Count == 0
			? EmptyStringSet
			: values.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private static IReadOnlySet<string> FreezeOrEmpty(IReadOnlySet<string> values) =>
		values.Count == 0
			? EmptyStringSet
			: values.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private IEnumerable<ScopedGitIgnoreMatcher> BuildScopedGitIgnoreMatchers(IReadOnlyList<ProjectScope> scopes)
	{
		// Filter and collect in single pass
		var scopesWithGitIgnore = new List<ProjectScope>();
		foreach (var scope in scopes)
		{
			if (scope.HasGitIgnore)
				scopesWithGitIgnore.Add(scope);
		}

		if (scopesWithGitIgnore.Count == 0)
			yield break;

		// GitIgnore precedence is parent -> child, so scopes must be ordered by depth.
		scopesWithGitIgnore.Sort((a, b) =>
		{
			var lengthComparison = a.RootPath.Length.CompareTo(b.RootPath.Length);
			if (lengthComparison != 0)
				return lengthComparison;

			return PathComparer.Default.Compare(a.RootPath, b.RootPath);
		});

		foreach (var scope in scopesWithGitIgnore)
		{
			var matcher = TryBuildGitIgnoreMatcher(scope.RootPath);
			if (ReferenceEquals(matcher, GitIgnoreMatcher.Empty))
				continue;

			yield return new ScopedGitIgnoreMatcher(scope.RootPath, matcher);
		}
	}

	private ProjectScanContext DiscoverProjectScanContext(
		string rootPath,
		IReadOnlyCollection<string>? selectedRootFolders) =>
		_projectScopeDiscovery.Discover(rootPath, selectedRootFolders);

	private static GitIgnoreMatcher TryBuildGitIgnoreMatcher(string rootPath)
	{
		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
			return GitIgnoreMatcher.Empty;

		var gitIgnorePath = Path.Combine(rootPath, ".gitignore");
		if (!File.Exists(gitIgnorePath))
			return GitIgnoreMatcher.Empty;

		try
		{
			var hasSignature = TryGetGitIgnoreSignature(gitIgnorePath, out var cacheKey, out var signature);
			if (hasSignature)
			{
				lock (CacheSync)
				{
					if (GitIgnoreCache.TryGetValue(cacheKey, out var cached) &&
						cached.Signature.Equals(signature))
					{
						return cached.Matcher;
					}
				}
			}

			var matcher = GitIgnoreMatcher.Build(rootPath, File.ReadLines(gitIgnorePath));
			if (hasSignature)
			{
				lock (CacheSync)
				{
					GitIgnoreCache[cacheKey] = new GitIgnoreCacheEntry(signature, matcher);
					if (GitIgnoreCache.Count > CacheLimit)
						GitIgnoreCache.Clear();
				}
			}

			return matcher;
		}
		catch
		{
			return GitIgnoreMatcher.Empty;
		}
	}

	private static bool TryGetGitIgnoreSignature(
		string gitIgnorePath,
		out string cacheKey,
		out GitIgnoreSignature signature)
	{
		cacheKey = Path.GetFullPath(gitIgnorePath);
		signature = default;

		try
		{
			var linkInfo = new FileInfo(gitIgnorePath);
			if (linkInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
			{
				// A symlinked .gitignore is valid project input. Use target metadata for cache
				// invalidation, but keep the link path as the cache key so rules stay scoped
				// to the project root that owns the .gitignore entry.
				var resolvedTarget = linkInfo.ResolveLinkTarget(returnFinalTarget: true);
				if (resolvedTarget is not FileInfo targetInfo || !targetInfo.Exists)
					return false;

				targetInfo.Refresh();
				signature = new GitIgnoreSignature(
					targetInfo.LastWriteTimeUtc.Ticks,
					targetInfo.Length,
					linkInfo.LinkTarget ?? string.Empty);
				return true;
			}

			signature = new GitIgnoreSignature(
				linkInfo.LastWriteTimeUtc.Ticks,
				linkInfo.Length,
				LinkTarget: string.Empty);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private readonly record struct GitIgnoreSignature(long LastWriteTicksUtc, long LengthBytes, string LinkTarget);

	private sealed record GitIgnoreCacheEntry(GitIgnoreSignature Signature, GitIgnoreMatcher Matcher);

	private sealed record ScopedSmartIgnoreBuildResult(
		IReadOnlySet<string> FolderNames,
		IReadOnlySet<string> FileNames,
		IReadOnlyList<ScopedSmartIgnoreMatcher> ScopedMatchers)
	{
		public static readonly ScopedSmartIgnoreBuildResult Empty =
			new(EmptyStringSet, EmptyStringSet, []);
	}

	private sealed class LocalSmartIgnoreBuildState
	{
		public HashSet<string> FolderNames { get; } = new(StringComparer.OrdinalIgnoreCase);

		public HashSet<string> FileNames { get; } = new(StringComparer.OrdinalIgnoreCase);

		public List<ScopedSmartIgnoreMatcher> ScopedMatchers { get; } = [];

		public bool IsEmpty =>
			FolderNames.Count == 0 &&
			FileNames.Count == 0 &&
			ScopedMatchers.Count == 0;
	}
}
