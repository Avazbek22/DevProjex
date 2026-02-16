using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using DevProjex.Kernel;
using DevProjex.Kernel.Models;

namespace DevProjex.Application.Services;

public sealed class IgnoreRulesService
{
	private readonly SmartIgnoreService _smartIgnore;
	private const int CacheLimit = 64;
	private static readonly object CacheSync = new();
	private static readonly Dictionary<string, GitIgnoreCacheEntry> GitIgnoreCache =
		new(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
	private const int ScopeCacheLimit = 128;
	private static readonly TimeSpan ScopeCacheTtl = TimeSpan.FromSeconds(5);
	private static readonly object ScopeCacheSync = new();
	private static readonly Dictionary<string, ScopeCacheEntry> ScopeCache =
		new(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

	private static readonly StringComparer PathStringComparer = OperatingSystem.IsLinux()
		? StringComparer.Ordinal
		: StringComparer.OrdinalIgnoreCase;

	private static readonly string[] ProjectMarkerFiles =
	{
		"package.json",
		"pyproject.toml",
		"pom.xml",
		"build.gradle",
		"build.gradle.kts",
		"go.mod",
		"Cargo.toml",
		"composer.json",
		"pubspec.yaml",
		"Gemfile"
	};

	private static readonly HashSet<string> ProjectMarkerExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".sln",
		".csproj",
		".fsproj",
		".vbproj",
		".vcxproj"
	};

	// Prevent expensive directory fan-out when probing nested project scopes.
	private const int NestedProjectProbeMaxDepth = 2;
	private const int NestedProjectProbeMaxDirectoriesPerScope = 256;

	public IgnoreRulesService(SmartIgnoreService smartIgnore)
	{
		_smartIgnore = smartIgnore;
	}

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
		var useSmartIgnore = availability.IncludeSmartIgnore
			? selectedOptions.Contains(IgnoreOptionId.SmartIgnore)
			: context.IsSingleScopeWithGitIgnore && requestedGitIgnore;

		var gitIgnoreMatcher = GitIgnoreMatcher.Empty;
		var scopedMatchers = Array.Empty<ScopedGitIgnoreMatcher>();
		var useGitIgnore = false;
		if (requestedGitIgnore)
		{
			scopedMatchers = BuildScopedGitIgnoreMatchers(context.Scopes)
				.ToArray();
			if (scopedMatchers.Length > 0)
			{
				useGitIgnore = true;
				if (scopedMatchers.Length == 1)
					gitIgnoreMatcher = scopedMatchers[0].Matcher;
			}
		}

		IReadOnlySet<string> smartFolders;
		IReadOnlySet<string> smartFiles;
		IReadOnlyList<string> smartScopeRoots;
		if (useSmartIgnore)
		{
			var smart = BuildScopedSmartIgnore(context.Scopes);
			smartFolders = smart.FolderNames;
			smartFiles = smart.FileNames;
			// Use HashSet for O(1) deduplication
			var uniqueRoots = new HashSet<string>(PathStringComparer);
			foreach (var scope in context.Scopes)
				uniqueRoots.Add(scope.RootPath);
			smartScopeRoots = uniqueRoots.ToArray();
		}
		else
		{
			smartFolders = EmptyStringSet;
			smartFiles = EmptyStringSet;
			smartScopeRoots = Array.Empty<string>();
		}

		return new IgnoreRules(
			IgnoreHiddenFolders: selectedOptions.Contains(IgnoreOptionId.HiddenFolders),
			IgnoreHiddenFiles: selectedOptions.Contains(IgnoreOptionId.HiddenFiles),
			IgnoreDotFolders: selectedOptions.Contains(IgnoreOptionId.DotFolders),
			IgnoreDotFiles: selectedOptions.Contains(IgnoreOptionId.DotFiles),
			SmartIgnoredFolders: smartFolders,
			SmartIgnoredFiles: smartFiles)
		{
			IgnoreExtensionlessFiles = selectedOptions.Contains(IgnoreOptionId.ExtensionlessFiles),
			UseGitIgnore = useGitIgnore,
			UseSmartIgnore = useSmartIgnore,
			GitIgnoreMatcher = gitIgnoreMatcher,
			ScopedGitIgnoreMatchers = scopedMatchers,
			SmartIgnoreScopeRoots = smartScopeRoots
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
		new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

			if (scope.HasProjectMarker || HasSmartCandidatesInRootEntries(scope.RootPath))
				return true;
		}

		return false;
	}

	private bool HasSmartCandidatesInRootEntries(string rootPath)
	{
		var smart = _smartIgnore.Build(rootPath);
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

	private SmartIgnoreResult BuildScopedSmartIgnore(IReadOnlyList<ProjectScope> scopes)
	{
		var folderNames = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
		var fileNames = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

		var maxDegree = Math.Min(8, Math.Max(1, Environment.ProcessorCount / 2));
		Parallel.ForEach(
			scopes,
			new ParallelOptions { MaxDegreeOfParallelism = maxDegree },
			scope =>
			{
				var smart = _smartIgnore.Build(scope.RootPath);
				foreach (var folder in smart.FolderNames)
					folderNames.TryAdd(folder, 0);
				foreach (var file in smart.FileNames)
					fileNames.TryAdd(file, 0);
			});

		return new SmartIgnoreResult(
			new HashSet<string>(folderNames.Keys, StringComparer.OrdinalIgnoreCase),
			new HashSet<string>(fileNames.Keys, StringComparer.OrdinalIgnoreCase));
	}

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

		// Sort in-place
		scopesWithGitIgnore.Sort((a, b) => PathComparer.Default.Compare(a.RootPath, b.RootPath));

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
		IReadOnlyCollection<string>? selectedRootFolders)
	{
		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
			return ProjectScanContext.Empty;

		var normalizedRoot = Path.GetFullPath(rootPath);
		var cacheKey = BuildScopeCacheKey(normalizedRoot, selectedRootFolders);
		var now = DateTime.UtcNow;

		lock (ScopeCacheSync)
		{
			if (ScopeCache.TryGetValue(cacheKey, out var cached) &&
			    now - cached.CachedAtUtc <= ScopeCacheTtl)
			{
				return cached.Context;
			}
		}

		var context = BuildProjectScanContext(normalizedRoot, selectedRootFolders);
		lock (ScopeCacheSync)
		{
			ScopeCache[cacheKey] = new ScopeCacheEntry(now, context);
			if (ScopeCache.Count > ScopeCacheLimit)
				ScopeCache.Clear();
		}

		return context;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static string BuildScopeCacheKey(string rootPath, IReadOnlyCollection<string>? selectedRootFolders)
	{
		if (selectedRootFolders is null || selectedRootFolders.Count == 0)
			return rootPath;

		// Use HashSet for deduplication, then sort in-place
		var uniqueNames = new HashSet<string>(PathStringComparer);
		foreach (var name in selectedRootFolders)
		{
			if (!string.IsNullOrWhiteSpace(name))
				uniqueNames.Add(name.Trim());
		}

		if (uniqueNames.Count == 0)
			return rootPath;

		// Convert to list and sort in-place to avoid LINQ allocation
		var sorted = new List<string>(uniqueNames);
		sorted.Sort(PathStringComparer);

		// Pre-calculate capacity for StringBuilder
		var capacity = rootPath.Length + 2; // "::"
		foreach (var name in sorted)
			capacity += name.Length + 1; // "|"

		var sb = new StringBuilder(capacity);
		sb.Append(rootPath).Append("::");
		for (var i = 0; i < sorted.Count; i++)
		{
			if (i > 0) sb.Append('|');
			sb.Append(sorted[i]);
		}

		return sb.ToString();
	}

	private static ProjectScanContext BuildProjectScanContext(
		string rootPath,
		IReadOnlyCollection<string>? selectedRootFolders)
	{
		var hasExplicitRootSelection = selectedRootFolders is not null && selectedRootFolders.Count > 0;
		var rootHasGitIgnore = HasGitIgnoreFile(rootPath);
		var rootHasProjectMarker = HasProjectMarker(rootPath);
		var candidateDirectories = ResolveCandidateDirectories(rootPath, selectedRootFolders);

		if (rootHasGitIgnore || rootHasProjectMarker || candidateDirectories.Count == 0)
		{
			return ProjectScanContext.FromScopes(new[]
			{
				new ProjectScope(
					rootPath,
					rootHasGitIgnore,
					HasProjectMarker: rootHasProjectMarker,
					LooksLikeProject: rootHasGitIgnore || rootHasProjectMarker)
			});
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
				scopedCandidates.Add(new ProjectScope(
					directoryPath,
					hasGitIgnore,
					HasProjectMarker: hasMarker,
					LooksLikeProject: hasGitIgnore || hasMarker));
			});

		// Convert to list and sort in-place
		var candidatesList = new List<ProjectScope>(scopedCandidates);
		candidatesList.Sort((a, b) => PathComparer.Default.Compare(a.RootPath, b.RootPath));
		var candidates = candidatesList.ToArray();
		var expandedCandidates = ExpandCandidatesWithNestedProjectScopes(candidates, maxDegree);

		if (hasExplicitRootSelection)
		{
			var selectedScopes = new List<ProjectScope>(expandedCandidates.Length + (rootHasGitIgnore ? 1 : 0));
			if (rootHasGitIgnore)
				selectedScopes.Add(new ProjectScope(
					rootPath,
					HasGitIgnore: true,
					HasProjectMarker: rootHasProjectMarker,
					LooksLikeProject: true));
			selectedScopes.AddRange(expandedCandidates);

			return ProjectScanContext.FromScopes(selectedScopes);
		}

		// Treat a parent folder with at least one discovered nested project as a scoped workspace.
		var workspaceDetected = false;
		foreach (var scope in expandedCandidates)
		{
			if (scope.LooksLikeProject)
			{
				workspaceDetected = true;
				break;
			}
		}
		if (!workspaceDetected)
		{
			return ProjectScanContext.FromScopes(new[]
			{
				new ProjectScope(
					rootPath,
					rootHasGitIgnore,
					HasProjectMarker: rootHasProjectMarker,
					LooksLikeProject: rootHasGitIgnore || rootHasProjectMarker)
			});
		}

		var scopes = new List<ProjectScope>(expandedCandidates.Length + (rootHasGitIgnore ? 1 : 0));
		if (rootHasGitIgnore)
			scopes.Add(new ProjectScope(
				rootPath,
				HasGitIgnore: true,
				HasProjectMarker: rootHasProjectMarker,
				LooksLikeProject: true));
		scopes.AddRange(expandedCandidates);

		return ProjectScanContext.FromScopes(scopes);
	}

	private static ProjectScope[] ExpandCandidatesWithNestedProjectScopes(
		IReadOnlyList<ProjectScope> candidates,
		int maxDegree)
	{
		if (candidates.Count == 0)
			return Array.Empty<ProjectScope>();

		var allScopes = new ConcurrentBag<ProjectScope>();
		var parallelDegree = Math.Min(4, Math.Max(1, maxDegree));

		Parallel.ForEach(
			candidates,
			new ParallelOptions { MaxDegreeOfParallelism = parallelDegree },
			candidate =>
			{
				allScopes.Add(candidate);
				if (candidate.LooksLikeProject)
					return;

				foreach (var childPath in EnumerateDescendantDirectoriesSafe(
					         candidate.RootPath,
					         NestedProjectProbeMaxDepth,
					         NestedProjectProbeMaxDirectoriesPerScope))
				{
					var hasGitIgnore = HasGitIgnoreFile(childPath);
					var hasMarker = HasProjectMarker(childPath);
					if (!hasGitIgnore && !hasMarker)
						continue;

					allScopes.Add(new ProjectScope(
						childPath,
						hasGitIgnore,
						HasProjectMarker: hasMarker,
						LooksLikeProject: true));
				}
			});

		// Use Dictionary for O(1) deduplication
		var uniqueScopes = new Dictionary<string, ProjectScope>(PathStringComparer);
		foreach (var scope in allScopes)
		{
			if (!uniqueScopes.ContainsKey(scope.RootPath))
				uniqueScopes[scope.RootPath] = scope;
		}

		// Convert to list and sort in-place
		var result = new List<ProjectScope>(uniqueScopes.Values);
		result.Sort((a, b) => PathComparer.Default.Compare(a.RootPath, b.RootPath));
		return result.ToArray();
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
		// Use HashSet for O(1) deduplication
		var uniqueCandidates = new HashSet<string>(PathStringComparer);

		if (selectedRootFolders is not null && selectedRootFolders.Count > 0)
		{
			foreach (var folderName in selectedRootFolders)
			{
				if (string.IsNullOrWhiteSpace(folderName))
					continue;

				var fullPath = Path.Combine(rootPath, folderName);
				if (Directory.Exists(fullPath))
					uniqueCandidates.Add(Path.GetFullPath(fullPath));
			}
		}
		else
		{
			try
			{
				foreach (var dir in Directory.GetDirectories(rootPath))
					uniqueCandidates.Add(dir);
			}
			catch
			{
				// Ignore scan errors and return best-effort list.
			}
		}

		// Convert to list and sort in-place
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

	private static bool HasProjectMarker(string directoryPath)
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

	private static GitIgnoreMatcher TryBuildGitIgnoreMatcher(string rootPath)
	{
		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
			return GitIgnoreMatcher.Empty;

		var gitIgnorePath = Path.Combine(rootPath, ".gitignore");
		if (!File.Exists(gitIgnorePath))
			return GitIgnoreMatcher.Empty;

		try
		{
			var fileInfo = new FileInfo(gitIgnorePath);
			var cacheKey = fileInfo.FullName;
			var signature = new GitIgnoreSignature(fileInfo.LastWriteTimeUtc.Ticks, fileInfo.Length);

			lock (CacheSync)
			{
				if (GitIgnoreCache.TryGetValue(cacheKey, out var cached) &&
				    cached.Signature.Equals(signature))
				{
					return cached.Matcher;
				}
			}

			var matcher = GitIgnoreMatcher.Build(rootPath, File.ReadLines(gitIgnorePath));
			lock (CacheSync)
			{
				GitIgnoreCache[cacheKey] = new GitIgnoreCacheEntry(signature, matcher);
				if (GitIgnoreCache.Count > CacheLimit)
					GitIgnoreCache.Clear();
			}

			return matcher;
		}
		catch
		{
			return GitIgnoreMatcher.Empty;
		}
	}

	private sealed record GitIgnoreSignature(long LastWriteTicksUtc, long LengthBytes);

	private sealed record GitIgnoreCacheEntry(GitIgnoreSignature Signature, GitIgnoreMatcher Matcher);

	private sealed record ScopeCacheEntry(DateTime CachedAtUtc, ProjectScanContext Context);

	private sealed record ProjectScope(
		string RootPath,
		bool HasGitIgnore,
		bool HasProjectMarker,
		bool LooksLikeProject);

	private sealed record ProjectScanContext(
		IReadOnlyList<ProjectScope> Scopes,
		bool IsSingleScopeWithGitIgnore,
		bool HasAnyGitIgnore,
		bool HasAnyWithoutGitIgnore)
	{
		public static ProjectScanContext Empty { get; } = new(
			Array.Empty<ProjectScope>(),
			IsSingleScopeWithGitIgnore: false,
			HasAnyGitIgnore: false,
			HasAnyWithoutGitIgnore: false);

		public static ProjectScanContext FromScopes(IEnumerable<ProjectScope> scopes)
		{
			// Use Dictionary for O(1) deduplication by RootPath
			var uniqueScopes = new Dictionary<string, ProjectScope>(PathStringComparer);
			foreach (var scope in scopes)
			{
				var normalizedPath = Path.GetFullPath(scope.RootPath);
				// First occurrence wins (matches DistinctBy behavior)
				if (!uniqueScopes.ContainsKey(normalizedPath))
					uniqueScopes[normalizedPath] = scope with { RootPath = normalizedPath };
			}

			if (uniqueScopes.Count == 0)
				return Empty;

			// Convert to list and sort in-place
			var normalizedScopes = new List<ProjectScope>(uniqueScopes.Values);
			normalizedScopes.Sort((a, b) => PathComparer.Default.Compare(a.RootPath, b.RootPath));
			var scopesArray = normalizedScopes.ToArray();

			// Compute flags in single pass
			var hasAnyGitIgnore = false;
			var hasAnyWithoutGitIgnore = false;
			foreach (var scope in scopesArray)
			{
				if (scope.HasGitIgnore)
					hasAnyGitIgnore = true;
				else
					hasAnyWithoutGitIgnore = true;

				// Early exit if both flags are set
				if (hasAnyGitIgnore && hasAnyWithoutGitIgnore)
					break;
			}

			var isSingleScopeWithGitIgnore = scopesArray.Length == 1 && scopesArray[0].HasGitIgnore;

			return new ProjectScanContext(
				Scopes: scopesArray,
				IsSingleScopeWithGitIgnore: isSingleScopeWithGitIgnore,
				HasAnyGitIgnore: hasAnyGitIgnore,
				HasAnyWithoutGitIgnore: hasAnyWithoutGitIgnore);
		}
	}
}
