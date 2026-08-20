using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DevProjex.Application.Diagnostics;
using DevProjex.Application.Selection;

namespace DevProjex.Application.Services;

public sealed class ProjectScopeDiscoveryService(
	SmartIgnoreService smartIgnore,
	ProjectRootFactsProvider? rootFactsProvider = null,
	Func<DateTime>? utcNowProvider = null)
{
	private const int ScopeCacheLimit = 128;
	private static readonly TimeSpan ScopeCacheTtl = TimeSpan.FromSeconds(5);
	private const int DefaultNestedProjectProbeMaxDepth = 2;
	private const int DefaultNestedProjectProbeMaxDirectoriesPerScope = 256;
	private const int MonorepoNestedProjectProbeMaxDepth = 8;
	private const int MonorepoNestedProjectProbeMaxDirectoriesPerScope = 1_000;

	private static readonly FrozenSet<string> NonProjectScopeDirectoryNames = new[]
	{
		".git",
		".hg",
		".svn",
		".github",
		".gitlab",
		".circleci",
		".idea",
		".vscode",
		".vs",
		".fleet"
	}.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private static readonly FrozenSet<string> ScopeDiscoveryPruneDirectoryNames = new[]
	{
		".git",
		".hg",
		".svn",
		".github",
		".gitlab",
		".circleci",
		".idea",
		".vscode",
		".vs",
		".fleet",
		"node_modules",
		"bower_components",
		"jspm_packages",
		"vendor",
		".bundle",
		"bin",
		"obj",
		"build",
		"dist",
		"out",
		"target",
		".gradle",
		".next",
		".nuxt",
		".cache",
		".parcel-cache",
		"coverage",
		".nyc_output",
		"tmp",
		"temp",
		"__pycache__",
		".pytest_cache",
		".mypy_cache",
		".ruff_cache",
		".tox",
		".venv",
		"venv",
		"env"
	}.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private static readonly FrozenSet<string> MonorepoContainerDirectoryNames = new[]
	{
		"apps",
		"packages",
		"services",
		"libs",
		"modules",
		"projects",
		"tools",
		"examples"
	}.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private static readonly FrozenSet<string> MonorepoMarkerFiles = new[]
	{
		"pnpm-workspace.yaml",
		"nx.json",
		"turbo.json",
		"lerna.json",
		"rush.json",
		"go.work"
	}.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private static readonly FrozenSet<string> ProjectMarkerFiles = new[]
	{
		"package.json",
		"package-lock.json",
		"pnpm-lock.yaml",
		"yarn.lock",
		"bun.lockb",
		"bun.lock",
		"pnpm-workspace.yaml",
		"nx.json",
		"turbo.json",
		"lerna.json",
		"rush.json",
		"npm-shrinkwrap.json",
		"pyproject.toml",
		"requirements.txt",
		"requirements-dev.txt",
		"setup.py",
		"setup.cfg",
		"Pipfile",
		"poetry.lock",
		"environment.yml",
		"pom.xml",
		"build.gradle",
		"build.gradle.kts",
		"settings.gradle",
		"settings.gradle.kts",
		"go.mod",
		"go.work",
		"Cargo.toml",
		"composer.json",
		"pubspec.yaml",
		"Gemfile",
		"Gemfile.lock"
	}.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private static readonly FrozenSet<string> ProjectMarkerExtensions = new[]
	{
		".sln",
		".csproj",
		".fsproj",
		".vbproj",
		".vcxproj"
	}.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private static readonly StringComparer PathStringComparer = PathComparer.Default;
	private static readonly StringComparison PathStringComparison = PathComparer.Comparison;

	private readonly object _scopeCacheSync = new();
	private readonly Dictionary<string, LinkedListNode<ScopeCacheEntry>> _scopeCache = new(PathStringComparer);
	private readonly LinkedList<ScopeCacheEntry> _scopeCacheLru = new();
	private readonly Dictionary<string, long> _latestDiscoverySequences = new(PathStringComparer);
	private readonly ProjectRootFactsProvider _rootFactsProvider = rootFactsProvider ?? smartIgnore.RootFactsProvider;
	private readonly Func<DateTime> _utcNowProvider = utcNowProvider ?? (() => DateTime.UtcNow);
	private long _scopeCacheGeneration;
	private long _nextDiscoverySequence;

	public ProjectScanContext Discover(
		string rootPath,
		IReadOnlyCollection<string>? selectedRootFolders)
	{
		IgnorePipelineDiagnostics.RecordProjectScopeDiscovery();
		if (string.IsNullOrWhiteSpace(rootPath))
			return ProjectScanContext.Empty;

		string normalizedRoot;
		try
		{
			normalizedRoot = PathUtility.Normalize(rootPath);
		}
		catch
		{
			return ProjectScanContext.Empty;
		}

		var cacheKey = BuildScopeCacheKey(normalizedRoot, selectedRootFolders);
		var now = _utcNowProvider();

		long cacheGeneration;
		long discoverySequence;
		lock (_scopeCacheSync)
		{
			if (_scopeCache.TryGetValue(cacheKey, out var cachedNode))
			{
				var age = now - cachedNode.Value.CachedAtUtc;
				if (age >= TimeSpan.Zero && age <= ScopeCacheTtl)
				{
					_scopeCacheLru.Remove(cachedNode);
					_scopeCacheLru.AddFirst(cachedNode);
					return cachedNode.Value.Context;
				}
			}

			RemoveScopeCacheEntry(cacheKey);
			cacheGeneration = _scopeCacheGeneration;
			discoverySequence = unchecked(++_nextDiscoverySequence);
			_latestDiscoverySequences[cacheKey] = discoverySequence;
		}

		ProjectScanContext context;
		ScopeDiscoveryStamp discoveryStamp;
		try
		{
			var rootFactsCache = new ProjectRootFactsOperationCache(_rootFactsProvider);
			var rootFacts = rootFactsCache.Get(normalizedRoot);
			if (!rootFacts.Exists)
			{
				AbandonDiscoveryReservation(cacheKey, cacheGeneration, discoverySequence);
				return ProjectScanContext.Empty;
			}

			context = BuildProjectScanContext(rootFacts, selectedRootFolders, rootFactsCache);
			discoveryStamp = rootFactsCache.CreateDiscoveryStamp();
		}
		catch
		{
			AbandonDiscoveryReservation(cacheKey, cacheGeneration, discoverySequence);
			throw;
		}

		lock (_scopeCacheSync)
		{
			// Invalidation and a newer discovery both outrank a result computed from an older
			// filesystem snapshot. The current caller may use its result, but it must not be
			// published as the topology for a later operation.
			if (_scopeCacheGeneration != cacheGeneration ||
			    !_latestDiscoverySequences.TryGetValue(cacheKey, out var latestSequence) ||
			    latestSequence != discoverySequence)
			{
				return context;
			}

			_latestDiscoverySequences.Remove(cacheKey);
			RemoveScopeCacheEntry(cacheKey);
			var entry = new ScopeCacheEntry(cacheKey, _utcNowProvider(), context, discoveryStamp);
			_scopeCache[cacheKey] = _scopeCacheLru.AddFirst(entry);

			while (_scopeCache.Count > ScopeCacheLimit &&
			       _scopeCacheLru.Last is { } leastRecentlyUsed)
			{
				RemoveScopeCacheEntry(leastRecentlyUsed.Value.CacheKey);
			}
		}

		return context;
	}

	public void Invalidate(string rootPath)
	{
		string normalizedRoot;
		try
		{
			normalizedRoot = PathUtility.Normalize(rootPath);
		}
		catch
		{
			return;
		}

		lock (_scopeCacheSync)
		{
			_scopeCacheGeneration = unchecked(_scopeCacheGeneration + 1);
			_latestDiscoverySequences.Clear();
			foreach (var cacheKey in _scopeCache.Keys.ToArray())
			{
				if (PathStringComparer.Equals(cacheKey, normalizedRoot) ||
				    cacheKey.StartsWith(normalizedRoot + "::", PathStringComparison))
				{
					RemoveScopeCacheEntry(cacheKey);
				}
			}

			// Keep both cache layers behind one publication boundary. If this happened after
			// releasing the scope lock, a new Discover could reserve the new generation while
			// still reading root facts from the old provider generation.
			_rootFactsProvider.Invalidate(normalizedRoot, includeDescendants: true);
		}
	}

	public bool Revalidate(string rootPath, CancellationToken cancellationToken = default)
	{
		// Repeated F5 refreshes validate the topology already inspected by discovery instead
		// of repeating its bounded recursive probes. Any mismatch discards every related cache
		// entry so a structural change can never be combined with a partially stale scope graph.
		cancellationToken.ThrowIfCancellationRequested();
		string normalizedRoot;
		try
		{
			normalizedRoot = PathUtility.Normalize(rootPath);
		}
		catch
		{
			return false;
		}

		KeyValuePair<string, LinkedListNode<ScopeCacheEntry>>[] candidates;
		lock (_scopeCacheSync)
		{
			candidates = _scopeCache
				.Where(pair => IsCacheKeyForRoot(pair.Key, normalizedRoot))
				.ToArray();
		}

		if (candidates.Length == 0)
			return false;

		var observedFacts = new Dictionary<string, ProjectRootFactsStamp>(PathStringComparer);
		var allCurrent = true;
		foreach (var candidate in candidates)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (candidate.Value.Value.DiscoveryStamp.IsCurrent(
				    _rootFactsProvider,
				    observedFacts,
				    cancellationToken))
			{
				continue;
			}

			allCurrent = false;
			break;
		}
		var now = _utcNowProvider();
		lock (_scopeCacheSync)
		{
			if (!allCurrent)
			{
				_scopeCacheGeneration = unchecked(_scopeCacheGeneration + 1);
				_latestDiscoverySequences.Clear();
				_rootFactsProvider.Invalidate(normalizedRoot, includeDescendants: true);
			}

			foreach (var candidate in candidates)
			{
				if (!_scopeCache.TryGetValue(candidate.Key, out var currentNode) ||
				    !ReferenceEquals(currentNode, candidate.Value))
				{
					continue;
				}

				if (!allCurrent)
				{
					RemoveScopeCacheEntry(candidate.Key);
					continue;
				}

				var refreshed = currentNode.Value with { CachedAtUtc = now };
				currentNode.Value = refreshed;
				_scopeCacheLru.Remove(currentNode);
				_scopeCacheLru.AddFirst(currentNode);
			}
		}

		if (!allCurrent)
			return false;

		return true;
	}

	private static bool IsCacheKeyForRoot(string cacheKey, string normalizedRoot) =>
		PathStringComparer.Equals(cacheKey, normalizedRoot) ||
		cacheKey.StartsWith(normalizedRoot + "::", PathStringComparison);

	private void RemoveScopeCacheEntry(string cacheKey)
	{
		if (!_scopeCache.Remove(cacheKey, out var node))
			return;

		_scopeCacheLru.Remove(node);
	}

	private void AbandonDiscoveryReservation(
		string cacheKey,
		long cacheGeneration,
		long discoverySequence)
	{
		lock (_scopeCacheSync)
		{
			if (_scopeCacheGeneration == cacheGeneration &&
			    _latestDiscoverySequences.TryGetValue(cacheKey, out var latestSequence) &&
			    latestSequence == discoverySequence)
			{
				_latestDiscoverySequences.Remove(cacheKey);
			}
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static string BuildScopeCacheKey(
		string rootPath,
		IReadOnlyCollection<string>? selectedRootFolders)
	{
		if (selectedRootFolders is null || selectedRootFolders.Count == 0)
			return rootPath;

		var encodedSelection = SelectionCacheKeyEncoder.EncodeStrings(selectedRootFolders);
		if (encodedSelection == SelectionCacheKeyEncoder.EncodeStrings([]))
			return rootPath;

		return rootPath + "::" + encodedSelection;
	}

	private ProjectScanContext BuildProjectScanContext(
		ProjectRootFacts rootFacts,
		IReadOnlyCollection<string>? selectedRootFolders,
		ProjectRootFactsOperationCache rootFactsCache)
	{
		var rootPath = rootFacts.RootPath;
		var hasExplicitRootSelection = selectedRootFolders is not null && selectedRootFolders.Count > 0;
		var rootHasGitIgnore = rootFacts.HasGitIgnoreFile;
		var rootHasGitRepository = rootFacts.HasGitMetadataEntry;
		var rootHasProjectMarker = HasProjectMarker(rootFacts);
		// Scope discovery is intentionally about project ownership, not artifact hiding.
		// The generic artifact matcher handles dependency/build/cache folders later, after
		// the selected roots are known. Keeping discovery conservative prevents home-folder
		// and monorepo opens from turning dependency forests into fake project scopes.
		var candidateDirectories = ResolveCandidateDirectories(rootFacts, selectedRootFolders);

		if (candidateDirectories.Count == 0)
		{
			if (hasExplicitRootSelection)
				return ProjectScanContext.Empty;

			return ProjectScanContext.FromScopes([
				new ProjectScope(
					rootPath,
					rootHasGitIgnore,
					HasProjectMarker: rootHasProjectMarker,
					LooksLikeProject: rootHasGitIgnore || rootHasGitRepository || rootHasProjectMarker,
					HasGitRepository: rootHasGitRepository)
			]);
		}

		var scopedCandidates = new List<ProjectScope>(candidateDirectories.Count);
		var scopedCandidatesSync = new object();
		Parallel.ForEach(
			candidateDirectories,
			ScanParallelismPolicy.CreateOptions(),
			static () => new List<ProjectScope>(),
			(directoryPath, _, localCandidates) =>
			{
				var candidateFacts = rootFactsCache.Get(directoryPath);
				var hasGitIgnore = candidateFacts.HasGitIgnoreFile;
				var hasGitRepository = candidateFacts.HasGitMetadataEntry;
				var hasMarker = HasProjectMarker(candidateFacts);
				if (ShouldSkipProjectScopeCandidate(
					    directoryPath,
					    hasGitIgnore,
					    hasGitRepository,
					    hasMarker,
					    isExplicitRootSelection: hasExplicitRootSelection))
				{
					return localCandidates;
				}

				localCandidates.Add(new ProjectScope(
					directoryPath,
					hasGitIgnore,
					HasProjectMarker: hasMarker,
					LooksLikeProject: hasGitIgnore || hasGitRepository || hasMarker,
					HasGitRepository: hasGitRepository));
				return localCandidates;
			},
			localCandidates =>
			{
				if (localCandidates.Count == 0)
					return;

				lock (scopedCandidatesSync)
					scopedCandidates.AddRange(localCandidates);
			});

		var candidates = SortScopes(scopedCandidates).ToArray();
		var expandedCandidates = ExpandCandidatesWithNestedProjectScopes(candidates, rootFactsCache);

		if (hasExplicitRootSelection)
		{
			// A marked opened root still owns its selected child folders. Dropping the root
			// scope here disables its stack rules whenever the UI supplies explicit roots.
			var rootLooksLikeProject = rootHasGitIgnore || rootHasGitRepository || rootHasProjectMarker;
			var selectedScopes = new List<ProjectScope>(expandedCandidates.Length + (rootLooksLikeProject ? 1 : 0));
			if (rootLooksLikeProject)
			{
				selectedScopes.Add(new ProjectScope(
					rootPath,
					rootHasGitIgnore,
					HasProjectMarker: rootHasProjectMarker,
					LooksLikeProject: true,
					HasGitRepository: rootHasGitRepository));
				selectedScopes.AddRange(expandedCandidates.Where(static scope => scope.LooksLikeProject));
			}
			else
			{
				selectedScopes.AddRange(expandedCandidates);
			}

			return ProjectScanContext.FromScopes(selectedScopes);
		}

		var workspaceDetected = expandedCandidates.Any(static scope => scope.LooksLikeProject);
		if (!workspaceDetected)
		{
			return ProjectScanContext.FromScopes([
				new ProjectScope(
					rootPath,
					rootHasGitIgnore,
					HasProjectMarker: rootHasProjectMarker,
					LooksLikeProject: rootHasGitIgnore || rootHasGitRepository || rootHasProjectMarker,
					HasGitRepository: rootHasGitRepository)
			]);
		}

		var rootLooksLikeProjectForWorkspace = rootHasGitIgnore || rootHasGitRepository || rootHasProjectMarker;
		var scopes = new List<ProjectScope>(expandedCandidates.Length + (rootLooksLikeProjectForWorkspace ? 1 : 0));
		if (rootLooksLikeProjectForWorkspace)
			scopes.Add(new ProjectScope(
				rootPath,
				rootHasGitIgnore,
				HasProjectMarker: rootHasProjectMarker,
				LooksLikeProject: true,
				HasGitRepository: rootHasGitRepository));
		scopes.AddRange(expandedCandidates);

		return ProjectScanContext.FromScopes(scopes);
	}

	private ProjectScope[] ExpandCandidatesWithNestedProjectScopes(
		IReadOnlyList<ProjectScope> candidates,
		ProjectRootFactsOperationCache rootFactsCache)
	{
		if (candidates.Count == 0)
			return [];

		var allScopes = new List<ProjectScope>(candidates.Count);
		var allScopesSync = new object();

		Parallel.ForEach(
			candidates,
			ScanParallelismPolicy.CreateOptions(),
			static () => new List<ProjectScope>(),
			(candidate, _, localScopes) =>
			{
				localScopes.Add(candidate);
				var candidateFacts = rootFactsCache.Get(candidate.RootPath);
				var probe = ResolveNestedProjectProbe(candidateFacts);

				foreach (var childPath in EnumerateDescendantDirectoriesSafe(
							 candidate.RootPath,
							 probe.MaxDepth,
							 probe.MaxDirectories,
							 rootFactsCache))
				{
					var childFacts = rootFactsCache.Get(childPath);
					var hasGitIgnore = childFacts.HasGitIgnoreFile;
					var hasGitRepository = childFacts.HasGitMetadataEntry;
					var hasMarker = HasProjectMarker(childFacts);
					if (ShouldSkipProjectScopeCandidate(
						    childPath,
						    hasGitIgnore,
						    hasGitRepository,
						    hasMarker,
						    isExplicitRootSelection: false))
					{
						continue;
					}
					if (!hasGitIgnore && !hasGitRepository && !hasMarker)
						continue;

					localScopes.Add(new ProjectScope(
						childPath,
						hasGitIgnore,
						HasProjectMarker: hasMarker,
						LooksLikeProject: true,
						HasGitRepository: hasGitRepository));
				}
				return localScopes;
			},
			localScopes =>
			{
				if (localScopes.Count == 0)
					return;

				lock (allScopesSync)
					allScopes.AddRange(localScopes);
			});

		var uniqueScopes = new Dictionary<string, ProjectScope>(PathStringComparer);
		foreach (var scope in allScopes)
		{
			ref var cachedScope = ref CollectionsMarshal.GetValueRefOrAddDefault(
				uniqueScopes,
				scope.RootPath,
				out var exists);
			if (!exists)
				cachedScope = scope;
		}

		return SortScopes(uniqueScopes.Values).ToArray();
	}

	private static NestedProjectProbe ResolveNestedProjectProbe(ProjectRootFacts candidateFacts)
	{
		var candidateName = GetDirectoryName(candidateFacts.RootPath);
		var isKnownMonorepoContainer =
			!string.IsNullOrWhiteSpace(candidateName) &&
			MonorepoContainerDirectoryNames.Contains(candidateName);

		// Keep the default probe intentionally shallow. Only obvious monorepo
		// roots/containers get the wider BFS so normal folder opens do not turn
		// into expensive dependency-style discovery scans. Smart artifact filtering is a
		// later, cheaper layer; do not compensate for missed scopes by increasing this
		// depth globally unless benchmarks prove it is safe for large user folders.
		if (isKnownMonorepoContainer ||
		    HasMonorepoMarker(candidateFacts))
		{
			return new NestedProjectProbe(
				MonorepoNestedProjectProbeMaxDepth,
				MonorepoNestedProjectProbeMaxDirectoriesPerScope);
		}

		return new NestedProjectProbe(
			DefaultNestedProjectProbeMaxDepth,
			DefaultNestedProjectProbeMaxDirectoriesPerScope);
	}

	private static List<ProjectScope> SortScopes(IEnumerable<ProjectScope> scopes)
	{
		var result = new List<ProjectScope>(scopes);
		result.Sort((a, b) => PathComparer.Default.Compare(a.RootPath, b.RootPath));
		return result;
	}

	private IEnumerable<string> EnumerateDescendantDirectoriesSafe(
		string rootPath,
		int maxDepth,
		int maxDirectories,
		ProjectRootFactsOperationCache rootFactsCache)
	{
		if (maxDepth <= 0 || maxDirectories <= 0)
			yield break;

		var queue = new Queue<(string Path, int Depth)>();
		queue.Enqueue((rootPath, 0));
		var discovered = 0;

		while (queue.Count > 0 && discovered < maxDirectories)
		{
			var (currentPath, currentDepth) = queue.Dequeue();
			if (currentDepth >= maxDepth)
				continue;

			var children = GetChildDirectoriesSafe(rootFactsCache.Get(currentPath));

			foreach (var childPath in children)
			{
				if (ShouldSkipProjectScopeTraversal(childPath))
					continue;

				yield return childPath;
				discovered++;
				if (discovered >= maxDirectories)
					yield break;

				queue.Enqueue((childPath, currentDepth + 1));
			}
		}
	}

	private static List<string> ResolveCandidateDirectories(
		ProjectRootFacts rootFacts,
		IReadOnlyCollection<string>? selectedRootFolders)
	{
		var uniqueCandidates = new HashSet<string>(PathStringComparer);

		if (selectedRootFolders is not null && selectedRootFolders.Count > 0)
		{
			foreach (var folderName in selectedRootFolders)
			{
				if (string.IsNullOrWhiteSpace(folderName))
					continue;

				if (rootFacts.TryGetDirectory(folderName, out var directory) &&
				    !directory.IsReparsePoint)
				{
					uniqueCandidates.Add(Path.GetFullPath(directory.FullPath));
				}
			}
		}
		else
		{
			foreach (var dir in GetChildDirectoriesSafe(rootFacts))
				uniqueCandidates.Add(Path.GetFullPath(dir));
		}

		var candidates = new List<string>(uniqueCandidates);
		candidates.Sort(PathComparer.Default);
		return candidates;
	}

	private static bool ShouldSkipProjectScopeCandidate(
		string directoryPath,
		bool hasGitIgnore,
		bool hasGitRepository,
		bool hasProjectMarker,
		bool isExplicitRootSelection)
	{
		var name = GetDirectoryName(directoryPath);
		if (string.IsNullOrWhiteSpace(name))
			return false;

		if (!isExplicitRootSelection && ScopeDiscoveryPruneDirectoryNames.Contains(name))
			return true;

		if (hasGitRepository || hasProjectMarker)
			return false;

		// A confirmed generated/dependency layout must never become an independent
		// project scope. The signature probe is bounded and name-gated, so ordinary
		// source folders named packages, registry, build, or vendor remain discoverable.
		if (!isExplicitRootSelection &&
		    SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(directoryPath, name))
		{
			return true;
		}

		if (NonProjectScopeDirectoryNames.Contains(name))
			return true;

		// A git-only dot directory is usually tool metadata (.idea, .github, caches)
		// rather than a user project. Do not surface its internal .gitignore as a
		// project-level ignore controller unless a real project marker exists there.
		return hasGitIgnore && name.StartsWith(".", StringComparison.Ordinal);
	}

	private static bool ShouldSkipProjectScopeTraversal(string directoryPath)
	{
		var name = GetDirectoryName(directoryPath);
		if (string.IsNullOrWhiteSpace(name))
			return false;

		if (ScopeDiscoveryPruneDirectoryNames.Contains(name))
			return true;

		// Prune only after local evidence proves an artifact store. This prevents
		// wide package caches from consuming the nested-scope BFS budget while keeping
		// source monorepo containers with the same names fully traversable.
		return SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(directoryPath, name);
	}

	private static string GetDirectoryName(string directoryPath)
	{
		var trimmedPath = directoryPath.TrimEnd(
			Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar);
		return Path.GetFileName(trimmedPath);
	}

	private bool HasProjectMarker(ProjectRootFacts rootFacts)
	{
		return rootFacts.HasAnyMarkerFile(ProjectMarkerFiles) ||
		       rootFacts.HasAnyFileExtension(ProjectMarkerExtensions) ||
		       smartIgnore.HasKnownProjectMarker(rootFacts);
	}

	private static bool HasMonorepoMarker(ProjectRootFacts rootFacts) =>
		rootFacts.HasAnyMarkerFile(MonorepoMarkerFiles);

	private static List<string> GetChildDirectoriesSafe(ProjectRootFacts rootFacts)
	{
		var directories = new List<string>();
		if (!rootFacts.IsAccessible)
			return directories;

		foreach (var directory in rootFacts.Directories)
		{
			if (!directory.IsReparsePoint)
				directories.Add(directory.FullPath);
		}

		return directories;
	}

	private sealed class ProjectRootFactsOperationCache(ProjectRootFactsProvider provider)
	{
		private readonly ConcurrentDictionary<string, Lazy<ProjectRootFacts>> _facts = new(PathStringComparer);

		public ProjectRootFacts Get(string rootPath)
		{
			var facts = _facts.GetOrAdd(
				rootPath,
				static (path, factsProvider) => new Lazy<ProjectRootFacts>(
					() => factsProvider.Get(path, forceRefresh: true),
					LazyThreadSafetyMode.ExecutionAndPublication),
				provider).Value;

			return facts;
		}

		public ScopeDiscoveryStamp CreateDiscoveryStamp()
		{
			return new ScopeDiscoveryStamp(
				_facts
					.Select(static pair => new { pair.Key, Facts = pair.Value.Value })
					.OrderBy(static pair => pair.Key, PathComparer.Default)
					.Select(static pair => ProjectRootFactsStamp.Capture(pair.Key, pair.Facts))
					.ToArray());
		}
	}

	private sealed record ScopeDiscoveryStamp(IReadOnlyList<ProjectRootFactsStamp> Roots)
	{
		public bool IsCurrent(
			ProjectRootFactsProvider provider,
			IDictionary<string, ProjectRootFactsStamp> observedFacts,
			CancellationToken cancellationToken)
		{
			foreach (var expected in Roots)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!observedFacts.TryGetValue(expected.Path, out var observed))
				{
					// Directory timestamps are not a correctness boundary: they are coarse on
					// some filesystems and can be deliberately restored. Re-enumerating only
					// the already-probed roots is bounded and detects equal-timestamp topology.
					observed = ProjectRootFactsStamp.Capture(
						expected.Path,
						provider.Get(expected.Path, forceRefresh: true));
					observedFacts[expected.Path] = observed;
				}

				if (!expected.IsEquivalentTo(observed))
					return false;
			}

			return Roots.Count > 0;
		}
	}

	private sealed record ProjectRootFactsStamp(
		string Path,
		bool Exists,
		bool IsAccessible,
		IReadOnlyList<ProjectRootEntryStamp> Entries,
		ProjectRootFileSignature? GitIgnoreSignature)
	{
		public static ProjectRootFactsStamp Capture(string path, ProjectRootFacts facts)
		{
			var entries = new List<ProjectRootEntryStamp>(facts.Files.Count + facts.Directories.Count);
			entries.AddRange(facts.Files.Select(static file =>
				new ProjectRootEntryStamp(file.Name, IsDirectory: false, file.IsReparsePoint)));
			entries.AddRange(facts.Directories.Select(static directory =>
				new ProjectRootEntryStamp(directory.Name, IsDirectory: true, directory.IsReparsePoint)));
			entries.Sort(ProjectRootEntryStampComparer.Instance);

			return new ProjectRootFactsStamp(
				path,
				facts.Exists,
				facts.IsAccessible,
				entries,
				facts.GitIgnoreSignature);
		}

		public bool IsEquivalentTo(ProjectRootFactsStamp other)
		{
			return Exists == other.Exists &&
			       IsAccessible == other.IsAccessible &&
			       Nullable.Equals(GitIgnoreSignature, other.GitIgnoreSignature) &&
			       Entries.SequenceEqual(other.Entries);
		}
	}

	private readonly record struct ProjectRootEntryStamp(
		string Name,
		bool IsDirectory,
		bool IsReparsePoint);

	private sealed class ProjectRootEntryStampComparer : IComparer<ProjectRootEntryStamp>
	{
		public static ProjectRootEntryStampComparer Instance { get; } = new();

		public int Compare(ProjectRootEntryStamp left, ProjectRootEntryStamp right)
		{
			var nameComparison = PathStringComparer.Compare(left.Name, right.Name);
			if (nameComparison != 0)
				return nameComparison;
			var directoryComparison = left.IsDirectory.CompareTo(right.IsDirectory);
			return directoryComparison != 0
				? directoryComparison
				: left.IsReparsePoint.CompareTo(right.IsReparsePoint);
		}
	}

	private sealed record ScopeCacheEntry(
		string CacheKey,
		DateTime CachedAtUtc,
		ProjectScanContext Context,
		ScopeDiscoveryStamp DiscoveryStamp);

	private readonly record struct NestedProjectProbe(int MaxDepth, int MaxDirectories);
}

public sealed record ProjectScope(
	string RootPath,
	bool HasGitIgnore,
	bool HasProjectMarker,
	bool LooksLikeProject,
	bool HasGitRepository = false);

public sealed record ProjectScanContext(
	IReadOnlyList<ProjectScope> Scopes,
	bool HasAnyGitIgnore,
	ConcurrentDictionary<string, SmartIgnoreResult> SmartIgnoreResultCache)
{
	private static readonly StringComparer PathStringComparer = PathComparer.Default;

	public bool HasAnyGitRepository => Scopes.Any(static scope => scope.HasGitRepository);

	public static ProjectScanContext Empty => new(
		[],
		HasAnyGitIgnore: false,
		SmartIgnoreResultCache: []);

	public static ProjectScanContext FromScopes(IEnumerable<ProjectScope> scopes)
	{
		var uniqueScopes = new Dictionary<string, ProjectScope>(PathStringComparer);
		foreach (var scope in scopes)
		{
			var normalizedPath = Path.GetFullPath(scope.RootPath);
			ref var cachedScope = ref CollectionsMarshal.GetValueRefOrAddDefault(
				uniqueScopes,
				normalizedPath,
				out var exists);
			if (!exists)
				cachedScope = scope with { RootPath = normalizedPath };
		}

		if (uniqueScopes.Count == 0)
			return Empty;

		var normalizedScopes = new List<ProjectScope>(uniqueScopes.Values);
		normalizedScopes.Sort((a, b) => PathComparer.Default.Compare(a.RootPath, b.RootPath));
		var scopesArray = normalizedScopes.ToArray();

		var hasAnyGitIgnore = false;
		foreach (var scope in scopesArray)
		{
			if (scope.HasGitIgnore)
				hasAnyGitIgnore = true;
			if (hasAnyGitIgnore)
				break;
		}

		return new ProjectScanContext(
			Scopes: scopesArray,
			HasAnyGitIgnore: hasAnyGitIgnore,
			SmartIgnoreResultCache: []);
	}

	public SmartIgnoreResult GetSmartIgnoreResult(string rootPath, SmartIgnoreService smartIgnore)
	{
		var normalizedPath = Path.GetFullPath(rootPath);
		return SmartIgnoreResultCache.GetOrAdd(normalizedPath, smartIgnore.Build);
	}
}
