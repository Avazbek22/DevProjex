using System.Collections.Frozen;
using DevProjex.Application.Diagnostics;

namespace DevProjex.Application.Services;

public sealed class IgnoreRulesService(
	SmartIgnoreService smartIgnore,
	ProjectScopeDiscoveryService? projectScopeDiscovery = null,
	IGitPathComparisonSemanticsResolver? pathComparisonSemanticsResolver = null)
{
	private const int CacheLimit = 512;
	private const long CacheByteLimit = 16L * 1024 * 1024;
	private static readonly object CacheSync = new();
	private static readonly Dictionary<string, LinkedListNode<GitIgnoreCacheEntry>> GitIgnoreCache =
		new(PathComparer.Default);
	private static readonly LinkedList<GitIgnoreCacheEntry> GitIgnoreCacheLru = new();
	private static long _gitIgnoreCacheSizeBytes;
	private readonly ProjectScopeDiscoveryService _projectScopeDiscovery =
		projectScopeDiscovery ?? new ProjectScopeDiscoveryService(smartIgnore);
	private readonly IGitPathComparisonSemanticsResolver _pathComparisonSemanticsResolver =
		pathComparisonSemanticsResolver ?? PlatformGitPathComparisonSemanticsResolver.Instance;

	private static readonly StringComparer PathStringComparer = PathComparer.Default;
	private static readonly StringComparison PathStringComparison = PathComparer.Comparison;

	public IgnoreRules Build(string rootPath, IReadOnlyCollection<IgnoreOptionId> selectedOptions) =>
		Build(rootPath, selectedOptions, selectedRootFolders: null);

	public void InvalidateCaches(string rootPath)
	{
		_projectScopeDiscovery.Invalidate(rootPath);
		_pathComparisonSemanticsResolver.Invalidate(rootPath);
		InvalidateGitIgnoreMatchers(rootPath);
	}

	public bool RevalidateCaches(string rootPath, CancellationToken cancellationToken = default)
	{
		_pathComparisonSemanticsResolver.Invalidate(rootPath);
		var reusedDiscovery = _projectScopeDiscovery.Revalidate(rootPath, cancellationToken);
		if (!reusedDiscovery)
			InvalidateGitIgnoreMatchers(rootPath);
		return reusedDiscovery;
	}

	private static void InvalidateGitIgnoreMatchers(string rootPath)
	{
		string normalizedRoot;
		try
		{
			normalizedRoot = Path.GetFullPath(rootPath);
		}
		catch
		{
			return;
		}

		lock (CacheSync)
		{
			foreach (var cachePath in GitIgnoreCache.Keys.ToArray())
			{
				if (IsSameOrDescendantPath(cachePath, normalizedRoot))
					RemoveGitIgnoreCacheEntry(cachePath);
			}
		}
	}

	public IgnoreRules Build(
		string rootPath,
		IReadOnlyCollection<IgnoreOptionId> selectedOptions,
		IReadOnlyCollection<string>? selectedRootFolders)
	{
		IgnorePipelineDiagnostics.RecordIgnoreRulesBuild();
		var context = DiscoverProjectScanContext(rootPath, selectedRootFolders);
		// A nested .gitignore can be discovered by the scanner after bounded project-scope
		// discovery has completed. An explicit/default selection must therefore activate the
		// traversal controller even when no prebuilt scope matcher exists yet.
		var gitFilteringMode = GitFilteringModeResolver.Resolve(selectedOptions);
		var requestedGitIgnore = gitFilteringMode == GitFilteringMode.RespectGitIgnore;
		var useTrackedGitFilesOnly = gitFilteringMode == GitFilteringMode.TrackedFilesOnly;
		var useSmartIgnore = selectedOptions.Contains(IgnoreOptionId.SmartIgnore);

		var candidateScopedGitMatchers = context.HasAnyGitIgnore
			? BuildScopedGitIgnoreMatchers(context.Scopes).ToArray()
			: [];
		var candidateGitIgnoreMatcher = candidateScopedGitMatchers.Length == 1
			? candidateScopedGitMatchers[0].Matcher
			: GitIgnoreMatcher.Empty;
		// Git mode is user intent, not a side effect of discovering a control file.
		// A missing .gitignore means an active empty rule set; it must never downgrade
		// RespectGitIgnore or expose Git administrative data from an otherwise valid scan.
		var useGitIgnore = requestedGitIgnore;
		var scopedMatchers = requestedGitIgnore
			? candidateScopedGitMatchers
			: Array.Empty<ScopedGitIgnoreMatcher>();
		var gitIgnoreMatcher = requestedGitIgnore && scopedMatchers.Length == 1
			? scopedMatchers[0].Matcher
			: GitIgnoreMatcher.Empty;

		// Candidate rules are independent from selection so disabling Git Ignore can expose
		// Smart Ignore without requiring a project reload.
		var smartCandidate = BuildScopedSmartIgnore(context);
		var candidateSmartScopeRoots = smartCandidate.ScopedMatchers
			.Select(static matcher => matcher.ScopeRootPath)
			.Distinct(PathStringComparer)
			.ToArray();
		var smartScopeResolver = smartIgnore.Descriptors.Count == 0
			? null
			: smartIgnore.CreateScopeResolver(rootPath);

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
			UseTrackedGitFilesOnly = useTrackedGitFilesOnly,
			EnableGitIgnoreTraversal = requestedGitIgnore || useTrackedGitFilesOnly,
			UseSmartIgnore = useSmartIgnore,
			GitIgnoreCandidateMatchesActiveRules = requestedGitIgnore,
			SmartIgnoreCandidateMatchesActiveRules = useSmartIgnore,
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
			SmartIgnoreScopeResolver = smartScopeResolver
		};
	}

	public IgnoreOptionsAvailability GetIgnoreOptionsAvailability(
		string rootPath,
		IReadOnlyCollection<string> selectedRootFolders)
	{
		var context = DiscoverProjectScanContext(rootPath, selectedRootFolders);
		return BuildUiIgnoreOptionsAvailability(rootPath, context);
	}

	private static readonly IReadOnlySet<string> EmptyStringSet =
		Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private IgnoreOptionsAvailability BuildUiIgnoreOptionsAvailability(
		string rootPath,
		ProjectScanContext context)
	{
		// UI availability is intentionally evidence-based. A Smart Ignore checkbox should
		// appear only when there is a project marker, a rule-specific root artifact, or a
		// signature-backed generic artifact candidate. That keeps clean workspaces quiet
		// while still surfacing the option for messy polyglot folders.
		var includeGitIgnore = context.HasAnyGitIgnore;
		var includeTrackedGitFilesOnly =
			context.HasAnyGitRepository ||
			HasGitMetadataAtOrAbove(rootPath);
		var includeSmartIgnore =
			context.Scopes.Count > 0 &&
			HasRelevantSmartIgnoreCandidates(context);
		return new IgnoreOptionsAvailability(
			IncludeGitIgnore: includeGitIgnore,
			IncludeSmartIgnore: includeSmartIgnore,
			IncludeTrackedGitFilesOnly: includeTrackedGitFilesOnly);
	}

	private static bool HasGitMetadataAtOrAbove(string rootPath)
	{
		string? currentPath;
		try
		{
			currentPath = Path.GetFullPath(rootPath);
		}
		catch
		{
			return false;
		}

		while (!string.IsNullOrWhiteSpace(currentPath))
		{
			var gitMetadataPath = Path.Combine(currentPath, ".git");
			if (!Directory.Exists(gitMetadataPath) && !File.Exists(gitMetadataPath))
			{
				currentPath = GetParentPath(currentPath);
				continue;
			}

			try
			{
				var attributes = File.GetAttributes(gitMetadataPath);
				// Git index discovery fails closed at reparse metadata boundaries. Keep
				// option availability aligned so an unsafe boundary cannot expose a mode
				// that the scanner will never use.
				if (attributes.HasFlag(FileAttributes.ReparsePoint))
					return false;

				return true;
			}
			catch
			{
				// An entry that cannot be inspected is not safe structural evidence.
				return false;
			}
		}

		return false;
	}

	private static string? GetParentPath(string path)
	{
		var parentPath = Path.GetDirectoryName(path);
		return string.IsNullOrWhiteSpace(parentPath) ||
		       PathComparer.Default.Equals(parentPath, path)
			? null
			: parentPath;
	}

	private bool HasRelevantSmartIgnoreCandidates(ProjectScanContext context)
	{
		// Direct iteration avoids allocation - early return on first match
		foreach (var scope in context.Scopes)
		{
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
		// Merged name sets are only fast candidate indexes. Scoped matchers remain the
		// authority for ownership; matching the merged names globally leaks one stack's
		// artifacts into sibling projects that merely reuse the same folder name.
		var mergeSync = new object();
		var folderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		folderNames.UnionWith(smartIgnore.DescriptorFolderNames);
		fileNames.UnionWith(smartIgnore.DescriptorFileNames);
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
						FreezeOrEmpty(smart.FileNames),
						FreezeOrEmpty(smart.EvidenceRequiredFolderNames)));
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
			var matcher = TryBuildGitIgnoreMatcher(
				scope.RootPath,
				smartIgnore.RootFactsProvider.Get(scope.RootPath));
			if (ReferenceEquals(matcher, GitIgnoreMatcher.Empty))
				continue;

			yield return new ScopedGitIgnoreMatcher(scope.RootPath, matcher);
		}
	}

	private ProjectScanContext DiscoverProjectScanContext(
		string rootPath,
		IReadOnlyCollection<string>? selectedRootFolders) =>
		_projectScopeDiscovery.Discover(rootPath, selectedRootFolders);

	private GitIgnoreMatcher TryBuildGitIgnoreMatcher(string rootPath, ProjectRootFacts? rootFacts = null)
	{
		if (string.IsNullOrWhiteSpace(rootPath))
			return GitIgnoreMatcher.Empty;

		var gitIgnorePath = Path.Combine(rootPath, ".gitignore");
		if (rootFacts is not null && !rootFacts.HasGitIgnoreFile)
			return GitIgnoreMatcher.Empty;

		try
		{
			var cacheKey = Path.GetFullPath(gitIgnorePath);
			var comparisonSemantics = _pathComparisonSemanticsResolver.Resolve(rootPath);
			if (!comparisonSemantics.IsAuthoritative)
			{
				// Guessing case or Unicode behavior is not monotonic for escaped literals,
				// character classes, and negations. The scanner will surface the dynamic
				// source as unavailable and exclude the affected scope fail-closed.
				return GitIgnoreMatcher.Empty;
			}
			// The root-facts cache is intentionally short-lived, but timestamp and length
			// alone cannot identify a .gitignore rewrite. Always obtain the current content
			// fingerprint before reusing a compiled matcher.
			var signature = ProjectRootFactsProvider.TryGetFileSignature(gitIgnorePath);
			// A missing signature means that this is not a readable regular working-tree
			// file. The filesystem scanner owns the visible partial-access diagnostic;
			// this prebuild path must neither follow a link nor invent an empty rule set.
			if (!signature.HasValue)
				return GitIgnoreMatcher.Empty;

			lock (CacheSync)
			{
				if (GitIgnoreCache.TryGetValue(cacheKey, out var cachedNode) &&
				    cachedNode.Value.Signature.Equals(signature.GetValueOrDefault()) &&
				    cachedNode.Value.ComparisonSemantics.Equals(comparisonSemantics))
				{
					GitIgnoreCacheLru.Remove(cachedNode);
					GitIgnoreCacheLru.AddFirst(cachedNode);
					return cachedNode.Value.Matcher;
				}

				RemoveGitIgnoreCacheEntry(cacheKey);
			}

			var source = GitIgnoreFileReader.Read(gitIgnorePath);
			if (source.LengthBytes != signature.GetValueOrDefault().LengthBytes ||
			    !string.Equals(
				    source.ContentFingerprint,
				    signature.GetValueOrDefault().ContentFingerprint,
				    StringComparison.Ordinal))
			{
				return GitIgnoreMatcher.Empty;
			}

			var matcher = GitIgnoreMatcher.Build(
				rootPath,
				GitIgnoreFileReader.SplitLines(source.Content),
				comparisonSemantics);
			lock (CacheSync)
			{
				RemoveGitIgnoreCacheEntry(cacheKey);
				var sourceSizeBytes = signature.GetValueOrDefault().LengthBytes;
				// A pathological but valid source remains usable for this scan. Retaining
				// it would let the static cache grow by hundreds of megabytes per scope.
				if (sourceSizeBytes <= CacheByteLimit)
				{
					var entry = new GitIgnoreCacheEntry(
						cacheKey,
						signature.GetValueOrDefault(),
						comparisonSemantics,
						matcher);
					GitIgnoreCache[cacheKey] = GitIgnoreCacheLru.AddFirst(entry);
					_gitIgnoreCacheSizeBytes += sourceSizeBytes;

					// Evict cold matchers one at a time. Count and source weight are both
					// bounded because compiled matcher cost scales with pattern input.
					while ((GitIgnoreCache.Count > CacheLimit ||
					        _gitIgnoreCacheSizeBytes > CacheByteLimit) &&
					       GitIgnoreCacheLru.Last is { } leastRecentlyUsed)
					{
						RemoveGitIgnoreCacheEntry(leastRecentlyUsed.Value.CacheKey);
					}
				}
			}

			return matcher;
		}
		catch (Exception exception) when (exception is
		       IOException or
		       UnauthorizedAccessException or
		       System.Security.SecurityException or
		       NotSupportedException or
		       ArgumentException)
		{
			return GitIgnoreMatcher.Empty;
		}
	}

	private static void RemoveGitIgnoreCacheEntry(string cacheKey)
	{
		if (!GitIgnoreCache.Remove(cacheKey, out var node))
			return;

		_gitIgnoreCacheSizeBytes = Math.Max(
			0,
			_gitIgnoreCacheSizeBytes - node.Value.Signature.LengthBytes);
		GitIgnoreCacheLru.Remove(node);
	}

	private sealed record GitIgnoreCacheEntry(
		string CacheKey,
		ProjectRootFileSignature Signature,
		GitPathComparisonSemantics ComparisonSemantics,
		GitIgnoreMatcher Matcher);

	private static bool IsSameOrDescendantPath(string candidatePath, string rootPath)
	{
		if (PathStringComparer.Equals(candidatePath, rootPath))
			return true;
		if (!candidatePath.StartsWith(rootPath, PathStringComparison))
			return false;

		return candidatePath.Length > rootPath.Length &&
		       IsDirectorySeparator(candidatePath[rootPath.Length]);
	}

	private static bool IsDirectorySeparator(char value) =>
		value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;

	private sealed record ScopedSmartIgnoreBuildResult(
		IReadOnlySet<string> FolderNames,
		IReadOnlySet<string> FileNames,
		IReadOnlyList<ScopedSmartIgnoreMatcher> ScopedMatchers);

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
