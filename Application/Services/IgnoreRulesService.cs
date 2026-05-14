using System.Collections.Concurrent;

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
		IReadOnlyList<ScopedSmartIgnoreMatcher> scopedSmartMatchers;
		if (useSmartIgnore)
		{
			var smart = BuildScopedSmartIgnore(context);
			smartFolders = smart.FolderNames;
			smartFiles = smart.FileNames;
			scopedSmartMatchers = smart.ScopedMatchers;
			smartScopeRoots = smart.ScopedMatchers
				.Select(static matcher => matcher.ScopeRootPath)
				.Distinct(PathStringComparer)
				.ToArray();
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
			SmartIgnoreScopeRoots = smartScopeRoots,
			ScopedSmartIgnoreMatchers = scopedSmartMatchers
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
		var folderNames = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
		var fileNames = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
		var scopedMatchers = new ConcurrentBag<ScopedSmartIgnoreMatcher>();

		Parallel.ForEach(
			context.Scopes,
			ScanParallelismPolicy.CreateOptions(),
			scope =>
			{
				var smart = context.GetSmartIgnoreResult(scope.RootPath, smartIgnore);
				foreach (var folder in smart.FolderNames)
					folderNames.TryAdd(folder, 0);
				foreach (var file in smart.FileNames)
					fileNames.TryAdd(file, 0);

				if (smart.FolderNames.Count > 0 || smart.FileNames.Count > 0)
				{
					scopedMatchers.Add(new ScopedSmartIgnoreMatcher(
						scope.RootPath,
						new HashSet<string>(smart.FolderNames, StringComparer.OrdinalIgnoreCase),
						new HashSet<string>(smart.FileNames, StringComparer.OrdinalIgnoreCase)));
				}
			});

		var orderedScopedMatchers = scopedMatchers
			.OrderBy(static matcher => matcher.ScopeRootPath.Length)
			.ThenBy(static matcher => matcher.ScopeRootPath, PathComparer.Default)
			.ToArray();

		return new ScopedSmartIgnoreBuildResult(
			new HashSet<string>(folderNames.Keys, StringComparer.OrdinalIgnoreCase),
			new HashSet<string>(fileNames.Keys, StringComparer.OrdinalIgnoreCase),
			orderedScopedMatchers);
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

	private sealed record ScopedSmartIgnoreBuildResult(
		IReadOnlySet<string> FolderNames,
		IReadOnlySet<string> FileNames,
		IReadOnlyList<ScopedSmartIgnoreMatcher> ScopedMatchers);
}
