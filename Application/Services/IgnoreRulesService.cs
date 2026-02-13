using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
		var availability = BuildIgnoreOptionsAvailability(context);
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
			smartScopeRoots = context.Scopes
				.Select(scope => scope.RootPath)
				.Distinct(PathStringComparer)
				.ToArray();
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
		return BuildIgnoreOptionsAvailability(context);
	}

	private static readonly IReadOnlySet<string> EmptyStringSet =
		new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private static IgnoreOptionsAvailability BuildIgnoreOptionsAvailability(ProjectScanContext context)
	{
		if (context.Scopes.Count == 0)
			return new IgnoreOptionsAvailability(IncludeGitIgnore: false, IncludeSmartIgnore: false);

		var includeGitIgnore = context.HasAnyGitIgnore;
		var includeSmartIgnore = !context.IsSingleScopeWithGitIgnore && context.HasAnyWithoutGitIgnore;
		return new IgnoreOptionsAvailability(includeGitIgnore, includeSmartIgnore);
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
		var scopesWithGitIgnore = scopes
			.Where(scope => scope.HasGitIgnore)
			.OrderBy(scope => scope.RootPath, PathComparer.Default)
			.ToArray();
		if (scopesWithGitIgnore.Length == 0)
			yield break;

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

	private static string BuildScopeCacheKey(string rootPath, IReadOnlyCollection<string>? selectedRootFolders)
	{
		if (selectedRootFolders is null || selectedRootFolders.Count == 0)
			return rootPath;

		var sorted = selectedRootFolders
			.Where(name => !string.IsNullOrWhiteSpace(name))
			.Select(name => name.Trim())
			.Distinct(PathStringComparer)
			.OrderBy(name => name, PathStringComparer)
			.ToArray();

		return sorted.Length == 0
			? rootPath
			: $"{rootPath}::{string.Join("|", sorted)}";
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
				new ProjectScope(rootPath, rootHasGitIgnore, LooksLikeProject: true)
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
				scopedCandidates.Add(new ProjectScope(directoryPath, hasGitIgnore, hasGitIgnore || hasMarker));
			});

		var candidates = scopedCandidates
			.OrderBy(scope => scope.RootPath, PathComparer.Default)
			.ToArray();

		if (hasExplicitRootSelection)
		{
			var selectedScopes = new List<ProjectScope>(candidates.Length + (rootHasGitIgnore ? 1 : 0));
			if (rootHasGitIgnore)
				selectedScopes.Add(new ProjectScope(rootPath, HasGitIgnore: true, LooksLikeProject: true));
			selectedScopes.AddRange(candidates);

			return ProjectScanContext.FromScopes(selectedScopes);
		}

		var workspaceDetected = candidates.Count(scope => scope.LooksLikeProject) >= 2;
		if (!workspaceDetected)
		{
			return ProjectScanContext.FromScopes(new[]
			{
				new ProjectScope(rootPath, rootHasGitIgnore, LooksLikeProject: true)
			});
		}

		var scopes = new List<ProjectScope>(candidates.Length + (rootHasGitIgnore ? 1 : 0));
		if (rootHasGitIgnore)
			scopes.Add(new ProjectScope(rootPath, HasGitIgnore: true, LooksLikeProject: true));
		scopes.AddRange(candidates);

		return ProjectScanContext.FromScopes(scopes);
	}

	private static List<string> ResolveCandidateDirectories(
		string rootPath,
		IReadOnlyCollection<string>? selectedRootFolders)
	{
		var candidates = new List<string>();

		if (selectedRootFolders is not null && selectedRootFolders.Count > 0)
		{
			foreach (var folderName in selectedRootFolders)
			{
				if (string.IsNullOrWhiteSpace(folderName))
					continue;

				var fullPath = Path.Combine(rootPath, folderName);
				if (Directory.Exists(fullPath))
					candidates.Add(Path.GetFullPath(fullPath));
			}
		}
		else
		{
			try
			{
				candidates.AddRange(Directory.GetDirectories(rootPath));
			}
			catch
			{
				// Ignore scan errors and return best-effort list.
			}
		}

		return candidates
			.Distinct(PathStringComparer)
			.OrderBy(path => path, PathComparer.Default)
			.ToList();
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
			var normalizedScopes = scopes
				.Select(scope => scope with { RootPath = Path.GetFullPath(scope.RootPath) })
				.DistinctBy(scope => scope.RootPath, PathStringComparer)
				.OrderBy(scope => scope.RootPath, PathComparer.Default)
				.ToArray();

			if (normalizedScopes.Length == 0)
				return Empty;

			var hasAnyGitIgnore = normalizedScopes.Any(scope => scope.HasGitIgnore);
			var hasAnyWithoutGitIgnore = normalizedScopes.Any(scope => !scope.HasGitIgnore);
			var isSingleScopeWithGitIgnore = normalizedScopes.Length == 1 && normalizedScopes[0].HasGitIgnore;

			return new ProjectScanContext(
				Scopes: normalizedScopes,
				IsSingleScopeWithGitIgnore: isSingleScopeWithGitIgnore,
				HasAnyGitIgnore: hasAnyGitIgnore,
				HasAnyWithoutGitIgnore: hasAnyWithoutGitIgnore);
		}
	}
}
