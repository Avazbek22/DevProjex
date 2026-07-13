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

		// Hybrid ignore has two controller modes:
		// - In mixed workspaces, .gitignore and Smart Ignore are independent because some
		//   scopes may have repository rules while other scopes only have generated artifacts.
		// - In a single .gitignore scope, Smart Ignore is intentionally hidden and follows
		//   Use .gitignore. Users get one practical "respect project ignore policy" switch
		//   instead of two overlapping switches that hide the same build output.
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

		// Candidate smart rules are built even when Smart Ignore is currently unchecked or
		// hidden under Use .gitignore. The scanner uses candidates to measure whether a
		// controller would affect the visible tree; without that evidence, a controller can
		// hide its own root-level artifacts and then disappear from the UI.
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
			SmartArtifactIgnoreMatcher = useSmartIgnore
				? SmartArtifactIgnoreMatcher.Default
				: SmartArtifactIgnoreMatcher.Empty,
			SmartArtifactIgnoreCandidateMatcher = SmartArtifactIgnoreMatcher.Default,
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

		// Runtime availability is broader than UI availability. The rule builder must be
		// able to construct candidate matchers for impact probes even when the UI later
		// decides a controller has zero visible effect and hides the checkbox.
		var includeGitIgnore = context.HasAnyGitIgnore;
		var includeSmartIgnore = !context.IsSingleScopeWithGitIgnore && context.HasAnyWithoutGitIgnore;
		return new IgnoreOptionsAvailability(
			includeGitIgnore,
			includeSmartIgnore,
			SmartIgnoreFollowsGitIgnore: context.IsSingleScopeWithGitIgnore);
	}

	private IgnoreOptionsAvailability BuildUiIgnoreOptionsAvailability(ProjectScanContext context)
	{
		if (context.Scopes.Count == 0)
			return new IgnoreOptionsAvailability(IncludeGitIgnore: false, IncludeSmartIgnore: false);

		// UI availability is intentionally evidence-based. A Smart Ignore checkbox should
		// appear only when there is a project marker, a rule-specific root artifact, or a
		// signature-backed generic artifact candidate. That keeps clean workspaces quiet
		// while still surfacing the option for messy polyglot folders.
		var includeGitIgnore = context.HasAnyGitIgnore;
		var includeSmartIgnore = !context.IsSingleScopeWithGitIgnore &&
								 context.HasAnyWithoutGitIgnore &&
								 HasRelevantSmartIgnoreCandidates(context);
		return new IgnoreOptionsAvailability(
			includeGitIgnore,
			includeSmartIgnore,
			SmartIgnoreFollowsGitIgnore: context.IsSingleScopeWithGitIgnore);
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

			var rootFacts = smartIgnore.RootFactsProvider.Get(scope.RootPath);
			if (SmartArtifactIgnoreMatcher.Default.HasConfirmedArtifactDirectory(rootFacts))
				return true;
		}

		return false;
	}

	private bool HasSmartCandidatesInRootEntries(ProjectScanContext context, string rootPath)
	{
		var smart = context.GetSmartIgnoreResult(rootPath, smartIgnore);
		if (smart.FolderNames.Count == 0)
			return false;

		var rootFacts = smartIgnore.RootFactsProvider.Get(rootPath);
		return rootFacts.HasAnyDirectoryName(smart.FolderNames);
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
			var matcher = TryBuildGitIgnoreMatcher(scope.RootPath, smartIgnore.RootFactsProvider.Get(scope.RootPath));
			if (ReferenceEquals(matcher, GitIgnoreMatcher.Empty))
				continue;

			yield return new ScopedGitIgnoreMatcher(scope.RootPath, matcher);
		}
	}

	private ProjectScanContext DiscoverProjectScanContext(
		string rootPath,
		IReadOnlyCollection<string>? selectedRootFolders) =>
		_projectScopeDiscovery.Discover(rootPath, selectedRootFolders);

	private static GitIgnoreMatcher TryBuildGitIgnoreMatcher(string rootPath, ProjectRootFacts? rootFacts = null)
	{
		if (string.IsNullOrWhiteSpace(rootPath))
			return GitIgnoreMatcher.Empty;

		var gitIgnorePath = Path.Combine(rootPath, ".gitignore");
		if (rootFacts is not null && !rootFacts.HasGitIgnoreFile)
			return GitIgnoreMatcher.Empty;

		try
		{
			var cacheKey = Path.GetFullPath(gitIgnorePath);
			var signature = ProjectRootFactsProvider.TryGetFileSignature(gitIgnorePath);

			if (signature.HasValue)
			{
				lock (CacheSync)
				{
					if (GitIgnoreCache.TryGetValue(cacheKey, out var cached) &&
						cached.Signature.Equals(signature.GetValueOrDefault()))
					{
						return cached.Matcher;
					}
				}
			}

			var matcher = GitIgnoreMatcher.Build(rootPath, File.ReadLines(gitIgnorePath));
			if (signature.HasValue)
			{
				lock (CacheSync)
				{
					GitIgnoreCache[cacheKey] = new GitIgnoreCacheEntry(signature.GetValueOrDefault(), matcher);
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

	private sealed record GitIgnoreCacheEntry(ProjectRootFileSignature Signature, GitIgnoreMatcher Matcher);

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
