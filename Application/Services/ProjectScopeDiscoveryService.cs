using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace DevProjex.Application.Services;

public sealed class ProjectScopeDiscoveryService(SmartIgnoreService smartIgnore)
{
	private const int ScopeCacheLimit = 128;
	private static readonly TimeSpan ScopeCacheTtl = TimeSpan.FromSeconds(5);
	private const int NestedProjectProbeMaxDepth = 2;
	private const int NestedProjectProbeMaxDirectoriesPerScope = 256;

	private static readonly HashSet<string> NonProjectScopeDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
	{
		".git",
		".hg",
		".svn",
		".idea",
		".vscode",
		".vs",
		".fleet"
	};

	private static readonly string[] ProjectMarkerFiles =
	[
		"package.json",
		"package-lock.json",
		"pnpm-lock.yaml",
		"yarn.lock",
		"bun.lockb",
		"bun.lock",
		"pnpm-workspace.yaml",
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
	];

	private static readonly HashSet<string> ProjectMarkerExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".sln",
		".csproj",
		".fsproj",
		".vbproj",
		".vcxproj"
	};

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

		var scopedCandidates = new ConcurrentBag<ProjectScope>();
		var maxDegree = Math.Min(8, Math.Max(1, Environment.ProcessorCount / 2));
		Parallel.ForEach(
			candidateDirectories,
			new ParallelOptions { MaxDegreeOfParallelism = maxDegree },
			directoryPath =>
			{
				var hasGitIgnore = HasGitIgnoreFile(directoryPath);
				var hasMarker = HasProjectMarker(directoryPath);
				if (ShouldSkipProjectScopeCandidate(directoryPath, hasGitIgnore, hasMarker))
					return;

				scopedCandidates.Add(new ProjectScope(
					directoryPath,
					hasGitIgnore,
					HasProjectMarker: hasMarker,
					LooksLikeProject: hasGitIgnore || hasMarker));
			});

		var candidates = SortScopes(scopedCandidates).ToArray();
		var expandedCandidates = ExpandCandidatesWithNestedProjectScopes(candidates, maxDegree);

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
		IReadOnlyList<ProjectScope> candidates,
		int maxDegree)
	{
		if (candidates.Count == 0)
			return [];

		var allScopes = new ConcurrentBag<ProjectScope>();
		var parallelDegree = Math.Min(4, Math.Max(1, maxDegree));

		Parallel.ForEach(
			candidates,
			new ParallelOptions { MaxDegreeOfParallelism = parallelDegree },
			candidate =>
			{
				allScopes.Add(candidate);

				foreach (var childPath in EnumerateDescendantDirectoriesSafe(
					         candidate.RootPath,
					         NestedProjectProbeMaxDepth,
					         NestedProjectProbeMaxDirectoriesPerScope))
				{
					var hasGitIgnore = HasGitIgnoreFile(childPath);
					var hasMarker = HasProjectMarker(childPath);
					if (ShouldSkipProjectScopeCandidate(childPath, hasGitIgnore, hasMarker))
						continue;
					if (!hasGitIgnore && !hasMarker)
						continue;

					allScopes.Add(new ProjectScope(
						childPath,
						hasGitIgnore,
						HasProjectMarker: hasMarker,
						LooksLikeProject: true));
				}
			});

		var uniqueScopes = new Dictionary<string, ProjectScope>(PathStringComparer);
		foreach (var scope in allScopes)
		{
			if (!uniqueScopes.ContainsKey(scope.RootPath))
				uniqueScopes[scope.RootPath] = scope;
		}

		return SortScopes(uniqueScopes.Values).ToArray();
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

			string[] children;
			try
			{
				// Materialize eagerly so access errors are handled inside this try/catch
				// and don't escape later from deferred enumeration in parallel scan.
				children = Directory.GetDirectories(currentPath, "*", SearchOption.TopDirectoryOnly);
			}
			catch
			{
				continue;
			}

			foreach (var childPath in children)
			{
				if (IsReparsePointDirectory(childPath))
					continue;
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
			try
			{
				foreach (var dir in Directory.GetDirectories(rootPath))
				{
					if (!IsReparsePointDirectory(dir))
						uniqueCandidates.Add(dir);
				}
			}
			catch
			{
				// Ignore scan errors and return best-effort list.
			}
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
		bool hasProjectMarker)
	{
		if (hasProjectMarker)
			return false;

		var name = GetDirectoryName(directoryPath);
		if (string.IsNullOrWhiteSpace(name))
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
		       NonProjectScopeDirectoryNames.Contains(name);
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

		if (smartIgnore.HasKnownProjectMarker(directoryPath))
			return true;

		try
		{
			foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly))
			{
				var extension = Path.GetExtension(filePath);
				if (!string.IsNullOrWhiteSpace(extension) && ProjectMarkerExtensions.Contains(extension))
					return true;
			}
		}
		catch
		{
			// Ignore marker scan failures.
		}

		return false;
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

	private sealed record ScopeCacheEntry(DateTime CachedAtUtc, ProjectScanContext Context);
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
			if (!uniqueScopes.ContainsKey(normalizedPath))
				uniqueScopes[normalizedPath] = scope with { RootPath = normalizedPath };
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
