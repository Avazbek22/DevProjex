using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DevProjex.Application.Services;

public sealed class ProjectScopeDiscoveryService(SmartIgnoreService smartIgnore)
{
	private const int ScopeCacheLimit = 128;
	private static readonly TimeSpan ScopeCacheTtl = TimeSpan.FromSeconds(5);
	private const int DefaultNestedProjectProbeMaxDepth = 2;
	private const int DefaultNestedProjectProbeMaxDirectoriesPerScope = 256;
	private const int MonorepoNestedProjectProbeMaxDepth = 8;
	private const int MonorepoNestedProjectProbeMaxDirectoriesPerScope = 1_000;
	private static readonly EnumerationOptions TopLevelEnumerationOptions = new()
	{
		RecurseSubdirectories = false,
		ReturnSpecialDirectories = false,
		AttributesToSkip = 0,
		IgnoreInaccessible = false
	};

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

	private static readonly StringComparer PathStringComparer = OperatingSystem.IsLinux()
		? StringComparer.Ordinal
		: StringComparer.OrdinalIgnoreCase;

	private readonly object _scopeCacheSync = new();
	private readonly Dictionary<string, ScopeCacheEntry> _scopeCache = new(PathStringComparer);

	public ProjectScanContext Discover(
		string rootPath,
		IReadOnlyCollection<string>? selectedRootFolders)
	{
		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
			return ProjectScanContext.Empty;

		var normalizedRoot = Path.GetFullPath(rootPath);
		var cacheKey = BuildScopeCacheKey(normalizedRoot, selectedRootFolders);
		var now = DateTime.UtcNow;

		lock (_scopeCacheSync)
		{
			if (_scopeCache.TryGetValue(cacheKey, out var cached) &&
				now - cached.CachedAtUtc <= ScopeCacheTtl)
			{
				return cached.Context;
			}
		}

		var context = BuildProjectScanContext(normalizedRoot, selectedRootFolders);
		lock (_scopeCacheSync)
		{
			_scopeCache[cacheKey] = new ScopeCacheEntry(now, context);
			if (_scopeCache.Count > ScopeCacheLimit)
				_scopeCache.Clear();
		}

		return context;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static string BuildScopeCacheKey(
		string rootPath,
		IReadOnlyCollection<string>? selectedRootFolders)
	{
		if (selectedRootFolders is null || selectedRootFolders.Count == 0)
			return rootPath;

		var uniqueNames = new HashSet<string>(PathStringComparer);
		foreach (var name in selectedRootFolders)
		{
			if (!string.IsNullOrWhiteSpace(name))
				uniqueNames.Add(name.Trim());
		}

		if (uniqueNames.Count == 0)
			return rootPath;

		var sorted = new List<string>(uniqueNames);
		sorted.Sort(PathStringComparer);

		var capacity = rootPath.Length + 2;
		foreach (var name in sorted)
			capacity += name.Length + 1;

		var builder = new StringBuilder(capacity);
		builder.Append(rootPath).Append("::");
		for (var index = 0; index < sorted.Count; index++)
		{
			if (index > 0)
				builder.Append('|');
			builder.Append(sorted[index]);
		}

		return builder.ToString();
	}

	private ProjectScanContext BuildProjectScanContext(
		string rootPath,
		IReadOnlyCollection<string>? selectedRootFolders)
	{
		var hasExplicitRootSelection = selectedRootFolders is not null && selectedRootFolders.Count > 0;
		var rootHasGitIgnore = HasGitIgnoreFile(rootPath);
		var rootHasProjectMarker = HasProjectMarker(rootPath);
		var candidateDirectories = ResolveCandidateDirectories(rootPath, selectedRootFolders);

		if (candidateDirectories.Count == 0)
		{
			if (hasExplicitRootSelection)
				return ProjectScanContext.Empty;

			return ProjectScanContext.FromScopes([
				new ProjectScope(
					rootPath,
					rootHasGitIgnore,
					HasProjectMarker: rootHasProjectMarker,
					LooksLikeProject: rootHasGitIgnore || rootHasProjectMarker)
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
				var hasGitIgnore = HasGitIgnoreFile(directoryPath);
				var hasMarker = HasProjectMarker(directoryPath);
				if (ShouldSkipProjectScopeCandidate(
					    directoryPath,
					    hasGitIgnore,
					    hasMarker,
					    isExplicitRootSelection: hasExplicitRootSelection))
				{
					return localCandidates;
				}

				localCandidates.Add(new ProjectScope(
					directoryPath,
					hasGitIgnore,
					HasProjectMarker: hasMarker,
					LooksLikeProject: hasGitIgnore || hasMarker));
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
		var expandedCandidates = ExpandCandidatesWithNestedProjectScopes(candidates);

		if (hasExplicitRootSelection)
		{
			var rootLooksLikeProject = rootHasGitIgnore || rootHasProjectMarker;
			var selectedScopes = new List<ProjectScope>(expandedCandidates.Length + (rootLooksLikeProject ? 1 : 0));
			if (rootLooksLikeProject)
			{
				selectedScopes.Add(new ProjectScope(
					rootPath,
					rootHasGitIgnore,
					HasProjectMarker: rootHasProjectMarker,
					LooksLikeProject: true));
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
					LooksLikeProject: rootHasGitIgnore || rootHasProjectMarker)
			]);
		}

		var rootLooksLikeProjectForWorkspace = rootHasGitIgnore || rootHasProjectMarker;
		var scopes = new List<ProjectScope>(expandedCandidates.Length + (rootLooksLikeProjectForWorkspace ? 1 : 0));
		if (rootLooksLikeProjectForWorkspace)
			scopes.Add(new ProjectScope(
				rootPath,
				rootHasGitIgnore,
				HasProjectMarker: rootHasProjectMarker,
				LooksLikeProject: true));
		scopes.AddRange(expandedCandidates);

		return ProjectScanContext.FromScopes(scopes);
	}

	private ProjectScope[] ExpandCandidatesWithNestedProjectScopes(
		IReadOnlyList<ProjectScope> candidates)
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
				var probe = ResolveNestedProjectProbe(candidate.RootPath);

				foreach (var childPath in EnumerateDescendantDirectoriesSafe(
							 candidate.RootPath,
							 probe.MaxDepth,
							 probe.MaxDirectories))
				{
					var hasGitIgnore = HasGitIgnoreFile(childPath);
					var hasMarker = HasProjectMarker(childPath);
					if (ShouldSkipProjectScopeCandidate(
						    childPath,
						    hasGitIgnore,
						    hasMarker,
						    isExplicitRootSelection: false))
					{
						continue;
					}
					if (!hasGitIgnore && !hasMarker)
						continue;

					localScopes.Add(new ProjectScope(
						childPath,
						hasGitIgnore,
						HasProjectMarker: hasMarker,
						LooksLikeProject: true));
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

	private static NestedProjectProbe ResolveNestedProjectProbe(
		string candidateRootPath)
	{
		var candidateName = GetDirectoryName(candidateRootPath);
		var isKnownMonorepoContainer =
			!string.IsNullOrWhiteSpace(candidateName) &&
			MonorepoContainerDirectoryNames.Contains(candidateName);

		// Keep the default probe intentionally shallow. Only obvious monorepo
		// roots/containers get the wider BFS so normal folder opens do not turn
		// into expensive dependency-style discovery scans.
		if (isKnownMonorepoContainer || HasMonorepoMarker(candidateRootPath))
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

	private static IEnumerable<string> EnumerateDescendantDirectoriesSafe(
		string rootPath,
		int maxDepth,
		int maxDirectories)
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

			var children = GetChildDirectoriesSafe(currentPath);

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
		string rootPath,
		IReadOnlyCollection<string>? selectedRootFolders)
	{
		var uniqueCandidates = new HashSet<string>(PathStringComparer);

		if (selectedRootFolders is not null && selectedRootFolders.Count > 0)
		{
			foreach (var folderName in selectedRootFolders)
			{
				if (string.IsNullOrWhiteSpace(folderName))
					continue;

				var fullPath = Path.Combine(rootPath, folderName);
				if (Directory.Exists(fullPath) && !IsReparsePointDirectory(fullPath))
					uniqueCandidates.Add(Path.GetFullPath(fullPath));
			}
		}
		else
		{
			foreach (var dir in GetChildDirectoriesSafe(rootPath))
				uniqueCandidates.Add(Path.GetFullPath(dir));
		}

		var candidates = new List<string>(uniqueCandidates);
		candidates.Sort(PathComparer.Default);
		return candidates;
	}

	private static bool HasGitIgnoreFile(string directoryPath)
	{
		try
		{
			return File.Exists(Path.Combine(directoryPath, ".gitignore"));
		}
		catch
		{
			return false;
		}
	}

	private static bool ShouldSkipProjectScopeCandidate(
		string directoryPath,
		bool hasGitIgnore,
		bool hasProjectMarker,
		bool isExplicitRootSelection)
	{
		var name = GetDirectoryName(directoryPath);
		if (string.IsNullOrWhiteSpace(name))
			return false;

		if (!isExplicitRootSelection && ScopeDiscoveryPruneDirectoryNames.Contains(name))
			return true;

		if (hasProjectMarker)
			return false;

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
		return !string.IsNullOrWhiteSpace(name) &&
			   ScopeDiscoveryPruneDirectoryNames.Contains(name);
	}

	private static string GetDirectoryName(string directoryPath)
	{
		var trimmedPath = directoryPath.TrimEnd(
			Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar);
		return Path.GetFileName(trimmedPath);
	}

	private bool HasProjectMarker(string directoryPath)
	{
		foreach (var markerFile in ProjectMarkerFiles)
		{
			try
			{
				if (File.Exists(Path.Combine(directoryPath, markerFile)))
					return true;
			}
			catch
			{
				// Continue with other marker checks.
			}
		}

		try
		{
			foreach (var file in EnumerateTopLevelFiles(directoryPath))
			{
				if (!string.IsNullOrWhiteSpace(file.Extension) &&
				    ProjectMarkerExtensions.Contains(file.Extension))
				{
					return true;
				}
				if (smartIgnore.IsKnownProjectMarker(file.FileName, file.Extension))
					return true;
			}
		}
		catch
		{
			// Full enumeration is only a fallback path for extension/custom markers.
			// Keep descriptor-specific marker files discoverable even on partial IO failures.
			return smartIgnore.HasKnownProjectMarker(directoryPath);
		}

		return false;
	}

	private static bool HasMonorepoMarker(string directoryPath)
	{
		foreach (var markerFile in MonorepoMarkerFiles)
		{
			try
			{
				if (File.Exists(Path.Combine(directoryPath, markerFile)))
					return true;
			}
			catch
			{
				// Monorepo detection is an optimization hint; IO failures fall back to the shallow probe.
			}
		}

		return false;
	}

	private static List<string> GetChildDirectoriesSafe(string rootPath)
	{
		var directories = new List<string>();
		try
		{
			foreach (var directoryPath in EnumerateChildDirectories(rootPath))
				directories.Add(directoryPath);
		}
		catch
		{
			// Scope discovery is best-effort: inaccessible children must not break project load.
		}

		return directories;
	}

	private static IEnumerable<string> EnumerateChildDirectories(string rootPath)
	{
		var enumerable = new FileSystemEnumerable<string>(
			rootPath,
			static (ref FileSystemEntry entry) => entry.ToSpecifiedFullPath(),
			TopLevelEnumerationOptions);
		enumerable.ShouldIncludePredicate = static (ref FileSystemEntry entry) =>
			entry.IsDirectory && !IsReparsePoint(ref entry);
		return enumerable;
	}

	private static IEnumerable<ProjectMarkerFileCandidate> EnumerateTopLevelFiles(string rootPath)
	{
		var enumerable = new FileSystemEnumerable<ProjectMarkerFileCandidate>(
			rootPath,
			static (ref FileSystemEntry entry) =>
			{
				var fileName = entry.FileName.ToString();
				return new ProjectMarkerFileCandidate(fileName, Path.GetExtension(fileName));
			},
			TopLevelEnumerationOptions);
		enumerable.ShouldIncludePredicate = static (ref FileSystemEntry entry) =>
			!entry.IsDirectory && !IsReparsePoint(ref entry);
		return enumerable;
	}

	private static bool IsReparsePointDirectory(string directoryPath)
	{
		try
		{
			return Directory.Exists(directoryPath) &&
				   File.GetAttributes(directoryPath).HasFlag(FileAttributes.ReparsePoint);
		}
		catch
		{
			return true;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool IsReparsePoint(ref FileSystemEntry entry) =>
		(entry.Attributes & FileAttributes.ReparsePoint) != 0;

	private sealed record ScopeCacheEntry(DateTime CachedAtUtc, ProjectScanContext Context);

	private readonly record struct ProjectMarkerFileCandidate(string FileName, string Extension);

	private readonly record struct NestedProjectProbe(int MaxDepth, int MaxDirectories);
}

public sealed record ProjectScope(
	string RootPath,
	bool HasGitIgnore,
	bool HasProjectMarker,
	bool LooksLikeProject);

public sealed record ProjectScanContext(
	IReadOnlyList<ProjectScope> Scopes,
	bool IsSingleScopeWithGitIgnore,
	bool HasAnyGitIgnore,
	bool HasAnyWithoutGitIgnore,
	ConcurrentDictionary<string, SmartIgnoreResult> SmartIgnoreResultCache)
{
	private static readonly StringComparer PathStringComparer = OperatingSystem.IsLinux()
		? StringComparer.Ordinal
		: StringComparer.OrdinalIgnoreCase;

	public static ProjectScanContext Empty => new(
		[],
		IsSingleScopeWithGitIgnore: false,
		HasAnyGitIgnore: false,
		HasAnyWithoutGitIgnore: false,
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
		var hasAnyWithoutGitIgnore = false;
		foreach (var scope in scopesArray)
		{
			if (scope.HasGitIgnore)
				hasAnyGitIgnore = true;
			else
				hasAnyWithoutGitIgnore = true;

			if (hasAnyGitIgnore && hasAnyWithoutGitIgnore)
				break;
		}

		var isSingleScopeWithGitIgnore = scopesArray.Length == 1 && scopesArray[0].HasGitIgnore;

		return new ProjectScanContext(
			Scopes: scopesArray,
			IsSingleScopeWithGitIgnore: isSingleScopeWithGitIgnore,
			HasAnyGitIgnore: hasAnyGitIgnore,
			HasAnyWithoutGitIgnore: hasAnyWithoutGitIgnore,
			SmartIgnoreResultCache: []);
	}

	public SmartIgnoreResult GetSmartIgnoreResult(string rootPath, SmartIgnoreService smartIgnore)
	{
		var normalizedPath = Path.GetFullPath(rootPath);
		return SmartIgnoreResultCache.GetOrAdd(normalizedPath, smartIgnore.Build);
	}
}
