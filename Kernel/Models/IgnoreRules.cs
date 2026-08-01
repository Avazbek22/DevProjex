using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Kernel.Models;

public sealed record IgnoreRules(
	bool IgnoreHiddenFolders,
	bool IgnoreHiddenFiles,
	bool IgnoreDotFolders,
	bool IgnoreDotFiles,
	IReadOnlySet<string> SmartIgnoredFolders,
	IReadOnlySet<string> SmartIgnoredFiles)
{
	private static readonly StringComparison PathComparison = PathComparer.Comparison;
	private static readonly StringComparer PathStringComparer = PathComparer.Default;
	private const int ScopedMatcherChainCacheLimit = 2048;
	private const int SmartScopeApplicabilityCacheLimit = 2048;
	private readonly ConcurrentDictionary<string, ScopedGitIgnoreMatcher[]> _scopedMatcherChainCache =
		new(PathStringComparer);
	private readonly ConcurrentDictionary<string, ScopedGitIgnoreMatcher[]> _candidateScopedMatcherChainCache =
		new(PathStringComparer);
	private readonly ConcurrentDictionary<string, bool> _smartScopeApplicabilityCache =
		new(PathStringComparer);
	private readonly ConcurrentDictionary<string, bool> _candidateSmartScopeApplicabilityCache =
		new(PathStringComparer);
	private readonly ConcurrentQueue<string> _scopedMatcherChainCacheOrder = new();
	private readonly ConcurrentQueue<string> _candidateScopedMatcherChainCacheOrder = new();
	private readonly ConcurrentQueue<string> _smartScopeApplicabilityCacheOrder = new();
	private readonly ConcurrentQueue<string> _candidateSmartScopeApplicabilityCacheOrder = new();

	public bool UseGitIgnore { get; init; }
	public bool UseTrackedGitFilesOnly { get; init; }
	public bool EnableGitIgnoreTraversal { get; init; }
	public bool IsGitIgnoreTraversalEnabled =>
		UseGitIgnore ||
		UseTrackedGitFilesOnly ||
		EnableGitIgnoreTraversal;
	public GitFilteringMode GitFilteringMode =>
		UseTrackedGitFilesOnly
			? GitFilteringMode.TrackedFilesOnly
			: UseGitIgnore
				? GitFilteringMode.RespectGitIgnore
				: GitFilteringMode.None;
	public bool UseSmartIgnore { get; init; }
	public bool GitIgnoreCandidateMatchesActiveRules { get; init; }
	public bool SmartIgnoreCandidateMatchesActiveRules { get; init; }
	public bool IgnoreEmptyFolders { get; init; }
	public bool IgnoreEmptyFiles { get; init; }
	public bool IgnoreExtensionlessFiles { get; init; }
	public string? ExcludedRootFolderName { get; init; }

	public GitIgnoreMatcher GitIgnoreMatcher { get; init; } = GitIgnoreMatcher.Empty;

	public IReadOnlyList<ScopedGitIgnoreMatcher> ScopedGitIgnoreMatchers { get; init; } =
		[];

	public GitIgnoreMatcher GitIgnoreCandidateMatcher { get; init; } = GitIgnoreMatcher.Empty;

	public IReadOnlyList<ScopedGitIgnoreMatcher> ScopedGitIgnoreCandidateMatchers { get; init; } =
		[];

	public IReadOnlyList<string> SmartIgnoreScopeRoots { get; init; } =
		[];

	public IReadOnlyList<ScopedSmartIgnoreMatcher> ScopedSmartIgnoreMatchers { get; init; } =
		[];

	public IReadOnlyList<string> SmartIgnoreCandidateScopeRoots { get; init; } =
		[];

	public IReadOnlyList<ScopedSmartIgnoreMatcher> ScopedSmartIgnoreCandidateMatchers { get; init; } =
		[];

	public IReadOnlySet<string>? SmartIgnoreCandidateFolders { get; init; }

	public IReadOnlySet<string>? SmartIgnoreCandidateFiles { get; init; }

	public ISmartIgnoreScopeResolver? SmartIgnoreScopeResolver { get; init; }

	public SmartArtifactIgnoreMatcher SmartArtifactIgnoreMatcher { get; init; } =
		SmartArtifactIgnoreMatcher.Empty;

	public SmartArtifactIgnoreMatcher SmartArtifactIgnoreCandidateMatcher { get; init; } =
		SmartArtifactIgnoreMatcher.Empty;

	public readonly record struct GitIgnoreEvaluation(bool IsIgnored, bool ShouldTraverseIgnoredDirectory)
	{
		public static readonly GitIgnoreEvaluation NotIgnored = new(false, false);
	}

	public GitIgnoreScanContext CreateGitIgnoreScanContext(string scanRootPath)
	{
		if (!IsGitIgnoreTraversalEnabled || string.IsNullOrWhiteSpace(scanRootPath))
			return GitIgnoreScanContext.Disabled(this);

		return CreateGitIgnoreScanContextCore(scanRootPath, useCandidates: false);
	}

	public GitIgnoreScanContext CreateGitIgnoreScanContext(
		string scanRootPath,
		IReadOnlyList<ScopedGitIgnoreMatcher> additionalMatchers)
	{
		return CreateGitIgnoreScanContext(
			scanRootPath,
			additionalMatchers,
			additionalTrackedPathIndexes: []);
	}

	public GitIgnoreScanContext CreateGitIgnoreScanContext(
		string scanRootPath,
		IReadOnlyList<ScopedGitIgnoreMatcher> additionalMatchers,
		IReadOnlyList<GitTrackedPathIndex> additionalTrackedPathIndexes)
	{
		var context = CreateGitIgnoreScanContext(scanRootPath);
		foreach (var matcher in additionalMatchers)
		{
			if (TryGetScopeRelativePath(scanRootPath, matcher.ScopeRootPath, out var scopeRelativePath))
			{
				context = context.WithScope(matcher, scopeRelativePath);
			}
			else if (IsPathInsideScope(scanRootPath, matcher.ScopeRootPath))
			{
				context = context.WithAncestorScope(matcher, scanRootPath);
			}
		}

		foreach (var trackedPathIndex in additionalTrackedPathIndexes)
			context = context.WithTrackedPathIndex(trackedPathIndex);

		return context;
	}

	private static bool TryGetScopeRelativePath(
		string scanRootPath,
		string scopeRootPath,
		out string scopeRelativePath)
	{
		scopeRelativePath = string.Empty;
		try
		{
			if (!IsPathInsideScope(scopeRootPath, scanRootPath))
				return false;

			var relativePath = Path.GetRelativePath(scanRootPath, scopeRootPath);
			scopeRelativePath = relativePath == "."
				? string.Empty
				: relativePath.Replace('\\', '/').Trim('/');
			return true;
		}
		catch
		{
			return false;
		}
	}

	public GitIgnoreScanContext CreateGitIgnoreCandidateScanContext(string scanRootPath)
	{
		if (string.IsNullOrWhiteSpace(scanRootPath))
			return GitIgnoreScanContext.Disabled(this, useCandidates: true);

		return CreateGitIgnoreScanContextCore(scanRootPath, useCandidates: true);
	}

	private GitIgnoreScanContext CreateGitIgnoreScanContextCore(
		string scanRootPath,
		bool useCandidates)
	{
		var scopedMatchers = GetScopedGitIgnoreMatchers(useCandidates);
		if (scopedMatchers.Count == 0)
		{
			var matcher = GetGitIgnoreMatcher(useCandidates);
			if (ReferenceEquals(matcher, GitIgnoreMatcher.Empty) ||
			    !matcher.TryGetRelativePath(scanRootPath, out var baseRelativePath, allowRoot: true))
			{
				// An active Git policy still owns the administrative .git boundary when
				// there are no pattern files. Disabled is reserved for an unselected mode.
				return !useCandidates && (UseGitIgnore || UseTrackedGitFilesOnly)
					? GitIgnoreScanContext.Scoped(
						this,
						useCandidates: false,
						evaluateRulesFallback: false)
					: GitIgnoreScanContext.Disabled(this, useCandidates);
			}

			return GitIgnoreScanContext.Relative(this, matcher, baseRelativePath, useCandidates);
		}

		var requiresRulesFallback = false;
		foreach (var scoped in scopedMatchers)
		{
			if (!PathStringComparer.Equals(scoped.ScopeRootPath, scanRootPath) &&
			    IsPathInsideScope(scanRootPath, scoped.ScopeRootPath))
			{
				requiresRulesFallback = true;
				break;
			}
		}

		// Descendant scopes are activated by the filesystem walk when their directory is
		// entered. Eagerly evaluating every known project scope for every path is both
		// redundant and quadratic in large multi-repository workspaces.
		var context = GitIgnoreScanContext.Scoped(this, useCandidates, requiresRulesFallback);
		foreach (var scoped in scopedMatchers)
		{
			if (PathStringComparer.Equals(scoped.ScopeRootPath, scanRootPath))
				context = context.WithScope(scoped, scopeRelativePath: string.Empty);
		}

		return context;
	}

	public GitIgnoreMatcher ResolveGitIgnoreMatcher(string fullPath)
	{
		if (!IsGitIgnoreTraversalEnabled)
			return GitIgnoreMatcher.Empty;

		if (ScopedGitIgnoreMatchers.Count == 0)
			return GitIgnoreMatcher;

		ScopedGitIgnoreMatcher? bestMatch = null;
		foreach (var scoped in ScopedGitIgnoreMatchers)
		{
			if (IsPathInsideScope(fullPath, scoped.ScopeRootPath))
			{
				if (bestMatch is null || scoped.ScopeRootPath.Length > bestMatch.ScopeRootPath.Length)
					bestMatch = scoped;
			}
		}

		return bestMatch?.Matcher ?? GitIgnoreMatcher.Empty;
	}

	public GitIgnoreEvaluation EvaluateGitIgnore(string fullPath, bool isDirectory, string name)
	{
		if (!IsGitIgnoreTraversalEnabled)
			return GitIgnoreEvaluation.NotIgnored;

		return EvaluateGitIgnoreCore(fullPath, isDirectory, name, useCandidates: false);
	}

	public GitIgnoreEvaluation EvaluateGitIgnoreCandidate(string fullPath, bool isDirectory, string name) =>
		EvaluateGitIgnoreCore(fullPath, isDirectory, name, useCandidates: true);

	private GitIgnoreEvaluation EvaluateGitIgnoreCore(
		string fullPath,
		bool isDirectory,
		string name,
		bool useCandidates)
	{
		// Git administrative entries are outside the working tree and must never be traversed as ignore-pattern content.
		if ((useCandidates || UseGitIgnore || UseTrackedGitFilesOnly) && IsGitAdministrativeEntry(name))
			return new GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: false);

		var scopedMatchersSource = GetScopedGitIgnoreMatchers(useCandidates);
		var singleMatcher = GetGitIgnoreMatcher(useCandidates);
		var scopedCount = scopedMatchersSource.Count;
		if (scopedCount == 0)
			return EvaluateWithSingleMatcher(singleMatcher, fullPath, isDirectory, name);

		if (scopedCount == 1)
		{
			var scoped = scopedMatchersSource[0];
			if (!IsPathInsideScope(fullPath, scoped.ScopeRootPath))
				return GitIgnoreEvaluation.NotIgnored;

			return EvaluateWithSingleMatcher(scoped.Matcher, fullPath, isDirectory, name);
		}

		var scopedMatchers = GetApplicableGitIgnoreMatchers(fullPath, isDirectory, useCandidates);
		if (scopedMatchers.Length == 0)
			return GitIgnoreEvaluation.NotIgnored;

		var hasMatch = false;
		var ignored = false;
		var hasNegationAwareScope = false;
		var reIncludedIgnoredPath = false;

		foreach (var scoped in scopedMatchers)
		{
			if (isDirectory && scoped.Matcher.HasNegationRules)
				hasNegationAwareScope = true;

			var evaluation = scoped.Matcher.Evaluate(fullPath, isDirectory, name);
			if (!evaluation.HasMatch)
				continue;

			hasMatch = true;
			if (ignored && !evaluation.IsIgnored)
				reIncludedIgnoredPath = true;
			ignored = evaluation.IsIgnored;
		}

		if (!hasMatch)
			return GitIgnoreEvaluation.NotIgnored;
		if (!ignored)
		{
			return reIncludedIgnoredPath && HasExplicitlyIgnoredAncestor(fullPath, scopedMatchers)
				? new GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: false)
				: GitIgnoreEvaluation.NotIgnored;
		}

		if (!isDirectory || !hasNegationAwareScope)
			return new GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: false);
		if (EvaluateRulesOnlyAcrossScopes(scopedMatchers, fullPath, isDirectory: true, name).IsIgnored)
			return new GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: false);

		foreach (var scoped in scopedMatchers)
		{
			if (scoped.Matcher.ShouldTraverseIgnoredDirectory(fullPath, name))
				return new GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: true);
		}

		return new GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: false);
	}

	private GitIgnoreMatcher.IgnoreEvaluation EvaluateGitIgnoreRulesOnly(
		string fullPath,
		bool isDirectory,
		string name,
		bool useCandidates)
	{
		var scopedMatchers = GetScopedGitIgnoreMatchers(useCandidates);
		if (scopedMatchers.Count == 0)
			return GetGitIgnoreMatcher(useCandidates).EvaluateRulesOnly(fullPath, isDirectory, name);

		return EvaluateRulesOnlyAcrossScopes(
			GetApplicableGitIgnoreMatchers(fullPath, isDirectory, useCandidates),
			fullPath,
			isDirectory,
			name);
	}

	private static GitIgnoreMatcher.IgnoreEvaluation EvaluateRulesOnlyAcrossScopes(
		IReadOnlyList<ScopedGitIgnoreMatcher> scopedMatchers,
		string fullPath,
		bool isDirectory,
		string name)
	{
		var hasMatch = false;
		var ignored = false;
		foreach (var scoped in scopedMatchers)
		{
			var evaluation = scoped.Matcher.EvaluateRulesOnly(fullPath, isDirectory, name);
			if (!evaluation.HasMatch)
				continue;

			hasMatch = true;
			ignored = evaluation.IsIgnored;
		}

		return new GitIgnoreMatcher.IgnoreEvaluation(hasMatch, ignored);
	}

	private static bool HasExplicitlyIgnoredAncestor(
		string fullPath,
		IReadOnlyList<ScopedGitIgnoreMatcher> scopedMatchers)
	{
		for (var parent = Path.GetDirectoryName(fullPath);
		     !string.IsNullOrWhiteSpace(parent);
		     parent = Path.GetDirectoryName(parent))
		{
			var name = Path.GetFileName(parent);
			if (EvaluateRulesOnlyAcrossScopes(scopedMatchers, parent, isDirectory: true, name).IsIgnored)
				return true;
		}

		return false;
	}

	public bool IsGitIgnored(string fullPath, bool isDirectory, string name)
	{
		return EvaluateGitIgnore(fullPath, isDirectory, name).IsIgnored;
	}

	public bool ShouldTraverseGitIgnoredDirectory(string fullPath, string name)
	{
		return EvaluateGitIgnore(fullPath, isDirectory: true, name).ShouldTraverseIgnoredDirectory;
	}

	private bool HasGitIgnoreTraversalCandidate(
		string fullPath,
		string name,
		bool useCandidates)
	{
		var scopedMatchers = GetScopedGitIgnoreMatchers(useCandidates);
		if (scopedMatchers.Count == 0)
			return GetGitIgnoreMatcher(useCandidates).ShouldTraverseIgnoredDirectory(fullPath, name);

		foreach (var scoped in GetApplicableGitIgnoreMatchers(fullPath, isDirectory: true, useCandidates))
		{
			if (scoped.Matcher.ShouldTraverseIgnoredDirectory(fullPath, name))
				return true;
		}

		return false;
	}

	public bool ShouldApplySmartIgnore(string fullPath)
	{
		return ShouldApplySmartIgnore(fullPath, isDirectory: true);
	}

	public bool ShouldApplySmartIgnore(string fullPath, bool isDirectory)
	{
		if (!UseSmartIgnore)
			return false;

		return ShouldApplySmartIgnoreCore(fullPath, isDirectory, useCandidates: false);
	}

	public bool ShouldApplySmartIgnoreCandidate(string fullPath, bool isDirectory) =>
		ShouldApplySmartIgnoreCore(fullPath, isDirectory, useCandidates: true);

	private bool ShouldApplySmartIgnoreCore(string fullPath, bool isDirectory, bool useCandidates)
	{
		var scopeRoots = useCandidates && SmartIgnoreCandidateScopeRoots.Count > 0
			? SmartIgnoreCandidateScopeRoots
			: SmartIgnoreScopeRoots;
		if (scopeRoots.Count == 0)
			return true;

		if (string.IsNullOrWhiteSpace(fullPath))
			return false;

		var probePath = fullPath;
		if (!isDirectory)
		{
			var parentDirectory = Path.GetDirectoryName(fullPath);
			if (!string.IsNullOrWhiteSpace(parentDirectory))
				probePath = parentDirectory;
		}

		var cache = useCandidates
			? _candidateSmartScopeApplicabilityCache
			: _smartScopeApplicabilityCache;
		var cacheOrder = useCandidates
			? _candidateSmartScopeApplicabilityCacheOrder
			: _smartScopeApplicabilityCacheOrder;
		if (cache.TryGetValue(probePath, out var cached))
			return cached;

		var applies = false;
		foreach (var scopeRoot in scopeRoots)
		{
			if (!IsPathInsideScope(probePath, scopeRoot))
				continue;

			applies = true;
			break;
		}

		if (cache.TryAdd(probePath, applies))
		{
			cacheOrder.Enqueue(probePath);
			TrimCache(cache, cacheOrder, SmartScopeApplicabilityCacheLimit);
		}

		return applies;
	}

	public bool IsSmartIgnoredDirectory(string fullPath, string name)
	{
		if (!UseSmartIgnore)
			return false;

		var appliesToProjectScope = ShouldApplySmartIgnoreCore(
			fullPath,
			isDirectory: true,
			useCandidates: false);
		// Smart Ignore contract: stack descriptors and stack-adjacent fingerprints remain inside
		// their discovered project scope. Only signature-confirmed portable dependency
		// stores may cross that boundary, so user-level package caches are removed without
		// treating ordinary sibling folders named bin, obj, packages, or build as artifacts.
		if (IsSmartArtifactIgnoredDirectory(
				SmartArtifactIgnoreMatcher,
				fullPath,
				name,
				portableOnly: true))
			return true;

		if (SmartIgnoreScopeResolver?.EvaluateDirectory(fullPath, name) is
		    { IsResolved: true } scopeDecision)
		{
			return scopeDecision.IsIgnored;
		}

		if (appliesToProjectScope && IsSmartArtifactIgnoredDirectory(
				SmartArtifactIgnoreMatcher,
				fullPath,
				name,
				portableOnly: false))
		{
			return true;
		}

		if (!SmartIgnoredFolders.Contains(name))
			return false;

		var scoped = GetMostSpecificSmartIgnoreMatcher(fullPath, ScopedSmartIgnoreMatchers);
		if (scoped is not null)
		{
			if (!scoped.FolderNames.Contains(name))
				return false;

			return !scoped.EvidenceRequiredFolderNames.Contains(name);
		}
		if (SmartIgnoreScopeResolver is not null)
			return false;

		return appliesToProjectScope && ScopedSmartIgnoreMatchers.Count == 0;
	}

	public bool IsSmartIgnoredDirectoryCandidate(string fullPath, string name)
	{
		var scopedMatchers = GetScopedSmartIgnoreMatchers(useCandidates: true);
		var appliesToProjectScope = ShouldApplySmartIgnoreCandidate(fullPath, isDirectory: true);
		if (IsSmartArtifactIgnoredDirectory(
				SmartArtifactIgnoreCandidateMatcher,
				fullPath,
				name,
				portableOnly: true))
			return true;

		if (SmartIgnoreScopeResolver?.EvaluateDirectory(fullPath, name) is
		    { IsResolved: true } scopeDecision)
		{
			return scopeDecision.IsIgnored;
		}

		if (appliesToProjectScope && IsSmartArtifactIgnoredDirectory(
				SmartArtifactIgnoreCandidateMatcher,
				fullPath,
				name,
				portableOnly: false))
		{
			return true;
		}

		var candidateFolders = SmartIgnoreCandidateFolders ?? SmartIgnoredFolders;
		if (!candidateFolders.Contains(name))
			return false;

		var scoped = GetMostSpecificSmartIgnoreMatcher(fullPath, scopedMatchers);
		if (scoped is not null)
		{
			if (!scoped.FolderNames.Contains(name))
				return false;

			return !scoped.EvidenceRequiredFolderNames.Contains(name);
		}
		if (SmartIgnoreScopeResolver is not null)
			return false;

		return appliesToProjectScope && scopedMatchers.Count == 0;
	}

	public bool IsSmartIgnoredFile(string fullPath, string name, bool shouldApplySmartIgnore)
	{
		if (!UseSmartIgnore)
			return false;

		if (SmartArtifactIgnoreMatcher.IsIgnoredFile(name))
			return true;

		if (!SmartIgnoredFiles.Contains(name))
			return false;

		if (shouldApplySmartIgnore)
		{
			if (SmartIgnoreScopeResolver?.EvaluateFile(fullPath, name) is
			    { IsResolved: true } scopeDecision)
			{
				return scopeDecision.IsIgnored;
			}

			var scoped = GetMostSpecificSmartIgnoreMatcher(fullPath, ScopedSmartIgnoreMatchers);
			if (scoped is not null)
				return scoped.FileNames.Contains(name);
			if (SmartIgnoreScopeResolver is not null)
				return false;
		}

		return shouldApplySmartIgnore && ScopedSmartIgnoreMatchers.Count == 0;
	}

	public bool IsSmartIgnoredFileCandidate(string fullPath, string name, bool shouldApplySmartIgnore)
	{
		if (SmartArtifactIgnoreCandidateMatcher.IsIgnoredFile(name))
			return true;

		var candidateFiles = SmartIgnoreCandidateFiles ?? SmartIgnoredFiles;
		if (!candidateFiles.Contains(name))
			return false;

		var scopedMatchers = GetScopedSmartIgnoreMatchers(useCandidates: true);
		if (shouldApplySmartIgnore)
		{
			if (SmartIgnoreScopeResolver?.EvaluateFile(fullPath, name) is
			    { IsResolved: true } scopeDecision)
			{
				return scopeDecision.IsIgnored;
			}

			var scoped = GetMostSpecificSmartIgnoreMatcher(fullPath, scopedMatchers);
			if (scoped is not null)
				return scoped.FileNames.Contains(name);
			if (SmartIgnoreScopeResolver is not null)
				return false;
		}

		return shouldApplySmartIgnore && scopedMatchers.Count == 0;
	}

	private static bool IsSmartArtifactIgnoredDirectory(
		SmartArtifactIgnoreMatcher matcher,
		string fullPath,
		string name,
		bool portableOnly)
	{
		if (!matcher.IsCandidateName(name))
			return false;

		// Signature results describe mutable filesystem state. IgnoreRules instances are
		// reused by refresh pipelines, so retaining either a positive or negative result
		// here would keep stale visibility after a build, restore, clean, or package update.
		// Candidate-name and scope checks remain cached/constant; only the bounded local
		// signature probe is intentionally repeated for each new filesystem observation.
		return portableOnly
			? matcher.IsPortableIgnoredDirectory(fullPath, name)
			: matcher.IsIgnoredDirectory(fullPath, name);
	}

	private ScopedGitIgnoreMatcher[] GetApplicableGitIgnoreMatchers(
		string fullPath,
		bool isDirectory,
		bool useCandidates)
	{
		var scopedMatchersSource = GetScopedGitIgnoreMatchers(useCandidates);
		if (scopedMatchersSource.Count == 0 || string.IsNullOrWhiteSpace(fullPath))
			return [];

		var cacheKeyPath = fullPath;
		if (!isDirectory)
		{
			var parentDirectory = Path.GetDirectoryName(fullPath);
			if (string.IsNullOrWhiteSpace(parentDirectory))
				return [];

			cacheKeyPath = parentDirectory;
		}

		var cache = useCandidates
			? _candidateScopedMatcherChainCache
			: _scopedMatcherChainCache;
		var cacheOrder = useCandidates
			? _candidateScopedMatcherChainCacheOrder
			: _scopedMatcherChainCacheOrder;
		if (cache.TryGetValue(cacheKeyPath, out var cached))
			return cached;

		var matched = new List<ScopedGitIgnoreMatcher>();
		foreach (var scoped in scopedMatchersSource)
		{
			if (IsPathInsideScope(cacheKeyPath, scoped.ScopeRootPath))
				matched.Add(scoped);
		}

		ScopedGitIgnoreMatcher[] resolved = matched.Count == 0
			? Array.Empty<ScopedGitIgnoreMatcher>()
			: [.. matched];
		if (cache.TryAdd(cacheKeyPath, resolved))
		{
			cacheOrder.Enqueue(cacheKeyPath);
			TrimCache(cache, cacheOrder, ScopedMatcherChainCacheLimit);
		}

		return resolved;
	}

	private static void TrimCache<TValue>(
		ConcurrentDictionary<string, TValue> cache,
		ConcurrentQueue<string> insertionOrder,
		int limit)
	{
		// Traversal keys are normally written once. Insertion-order eviction avoids a lock
		// and per-hit LRU writes while preventing the full-cache flush cliff on large trees.
		while (cache.Count > limit && insertionOrder.TryDequeue(out var oldest))
			cache.TryRemove(oldest, out _);
	}

	private GitIgnoreMatcher GetGitIgnoreMatcher(bool useCandidates)
	{
		if (!useCandidates)
			return GitIgnoreMatcher;

		return ReferenceEquals(GitIgnoreCandidateMatcher, GitIgnoreMatcher.Empty)
			? GitIgnoreMatcher
			: GitIgnoreCandidateMatcher;
	}

	private IReadOnlyList<ScopedGitIgnoreMatcher> GetScopedGitIgnoreMatchers(bool useCandidates)
	{
		if (!useCandidates || ScopedGitIgnoreCandidateMatchers.Count == 0)
			return ScopedGitIgnoreMatchers;

		return ScopedGitIgnoreCandidateMatchers;
	}

	private IReadOnlyList<ScopedSmartIgnoreMatcher> GetScopedSmartIgnoreMatchers(bool useCandidates)
	{
		if (!useCandidates || ScopedSmartIgnoreCandidateMatchers.Count == 0)
			return ScopedSmartIgnoreMatchers;

		return ScopedSmartIgnoreCandidateMatchers;
	}

	private static ScopedSmartIgnoreMatcher? GetMostSpecificSmartIgnoreMatcher(
		string fullPath,
		IReadOnlyList<ScopedSmartIgnoreMatcher> matchers)
	{
		ScopedSmartIgnoreMatcher? mostSpecific = null;
		foreach (var matcher in matchers)
		{
			if (!IsPathInsideScope(fullPath, matcher.ScopeRootPath))
				continue;

			if (mostSpecific is null || matcher.ScopeRootPath.Length > mostSpecific.ScopeRootPath.Length)
				mostSpecific = matcher;
		}

		return mostSpecific;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool IsPathInsideScope(string fullPath, string scopeRootPath)
	{
		if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(scopeRootPath))
			return false;

		var fullSpan = fullPath.AsSpan();
		var scopeSpan = scopeRootPath.AsSpan();

		if (!StartsWithPathPrefix(fullSpan, scopeSpan))
			return false;

		if (fullSpan.Length == scopeSpan.Length)
			return true;

		var next = fullSpan[scopeSpan.Length];
		return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
	}

	private static bool StartsWithPathPrefix(
		ReadOnlySpan<char> fullPath,
		ReadOnlySpan<char> scopeRootPath)
	{
		if (scopeRootPath.Length > fullPath.Length)
			return false;

		for (var index = 0; index < scopeRootPath.Length; index++)
		{
			var fullCharacter = fullPath[index];
			var scopeCharacter = scopeRootPath[index];
			if (fullCharacter == scopeCharacter ||
			    IsDirectorySeparator(fullCharacter) && IsDirectorySeparator(scopeCharacter))
			{
				continue;
			}

			if (!fullPath.Slice(index, 1).Equals(scopeRootPath.Slice(index, 1), PathComparison))
				return false;
		}

		return true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool IsDirectorySeparator(char character) =>
		character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static GitIgnoreEvaluation EvaluateWithSingleMatcher(
		GitIgnoreMatcher matcher,
		string fullPath,
		bool isDirectory,
		string name)
	{
		var evaluation = matcher.Evaluate(fullPath, isDirectory, name);
		if (!evaluation.HasMatch || !evaluation.IsIgnored)
			return GitIgnoreEvaluation.NotIgnored;

		if (!isDirectory)
			return new GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: false);

		return new GitIgnoreEvaluation(
			IsIgnored: true,
			ShouldTraverseIgnoredDirectory: matcher.ShouldTraverseIgnoredDirectory(fullPath, name));
	}

	private static GitIgnoreEvaluation EvaluateWithSingleMatcherRelative(
		GitIgnoreMatcher matcher,
		string relativePath,
		bool isDirectory,
		string name)
	{
		var evaluation = matcher.EvaluateRelative(relativePath, isDirectory, name);
		if (!evaluation.HasMatch || !evaluation.IsIgnored)
			return GitIgnoreEvaluation.NotIgnored;

		if (!isDirectory)
			return new GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: false);

		return new GitIgnoreEvaluation(
			IsIgnored: true,
			ShouldTraverseIgnoredDirectory: matcher.ShouldTraverseIgnoredDirectoryRelative(relativePath, name));
	}

	public readonly struct GitIgnoreScanContext
	{
		private readonly IgnoreRules _rules;
		private readonly GitIgnoreMatcher? _relativeMatcher;
		private readonly string _baseRelativePath;
		private readonly bool _useCandidates;
		private readonly bool _evaluateRulesFallback;
		private readonly AdditionalGitIgnoreScope? _additionalScopes;
		private readonly AdditionalGitTrackedPathIndex? _trackedPathIndexes;

		private GitIgnoreScanContext(
			IgnoreRules rules,
			GitIgnoreMatcher? relativeMatcher,
			string baseRelativePath,
			bool useCandidates,
			bool evaluateRulesFallback = false,
			AdditionalGitIgnoreScope? additionalScopes = null,
			AdditionalGitTrackedPathIndex? trackedPathIndexes = null)
		{
			_rules = rules;
			_relativeMatcher = relativeMatcher;
			_baseRelativePath = baseRelativePath;
			_useCandidates = useCandidates;
			_evaluateRulesFallback = evaluateRulesFallback;
			_additionalScopes = additionalScopes;
			_trackedPathIndexes = trackedPathIndexes;
		}

		public static GitIgnoreScanContext Disabled(IgnoreRules rules, bool useCandidates = false) =>
			new(rules, relativeMatcher: null, baseRelativePath: string.Empty, useCandidates);

		public static GitIgnoreScanContext Relative(
			IgnoreRules rules,
			GitIgnoreMatcher matcher,
			string baseRelativePath,
			bool useCandidates) =>
			new(rules, matcher, baseRelativePath, useCandidates);

		public static GitIgnoreScanContext Scoped(
			IgnoreRules rules,
			bool useCandidates,
			bool evaluateRulesFallback) =>
			new(
				rules,
				relativeMatcher: null,
				baseRelativePath: string.Empty,
				useCandidates,
				evaluateRulesFallback);

		public bool ContainsScope(string scopeRootPath)
		{
			if (string.IsNullOrWhiteSpace(scopeRootPath))
				return false;

			if (_additionalScopes?.Contains(scopeRootPath) == true)
				return true;

			return _relativeMatcher?.IsRootPath(scopeRootPath) == true;
		}

		public bool HasIgnoreRules
		{
			get
			{
				if (_relativeMatcher is not null ||
				    _additionalScopes is not null)
				{
					return true;
				}

				return _evaluateRulesFallback &&
				       (!ReferenceEquals(_rules.GetGitIgnoreMatcher(_useCandidates), GitIgnoreMatcher.Empty) ||
				        _rules.GetScopedGitIgnoreMatchers(_useCandidates).Count > 0);
			}
		}

		public bool RequiresTrackedPathIndex =>
			(!_useCandidates && _rules.UseTrackedGitFilesOnly) ||
			HasIgnoreRules;

		public GitIgnoreScanContext WithScope(
			ScopedGitIgnoreMatcher scopedMatcher,
			string scopeRelativePath)
		{
			if (ReferenceEquals(scopedMatcher.Matcher, GitIgnoreMatcher.Empty) ||
			    ContainsScope(scopedMatcher.ScopeRootPath))
			{
				return this;
			}

			return new GitIgnoreScanContext(
				_rules,
				_relativeMatcher,
				_baseRelativePath,
				_useCandidates,
				_evaluateRulesFallback,
				new AdditionalGitIgnoreScope(
					_additionalScopes,
					scopedMatcher,
					scopeRelativePath),
				_trackedPathIndexes);
		}

		public GitIgnoreScanContext WithAncestorScope(
			ScopedGitIgnoreMatcher scopedMatcher,
			string scanRootPath)
		{
			if (ReferenceEquals(scopedMatcher.Matcher, GitIgnoreMatcher.Empty) ||
			    ContainsScope(scopedMatcher.ScopeRootPath) ||
			    !scopedMatcher.Matcher.TryGetRelativePath(
				    scanRootPath,
				    out var matcherBaseRelativePath,
				    allowRoot: true))
			{
				return this;
			}

			return new GitIgnoreScanContext(
				_rules,
				_relativeMatcher,
				_baseRelativePath,
				_useCandidates,
				_evaluateRulesFallback,
				new AdditionalGitIgnoreScope(
					_additionalScopes,
					scopedMatcher,
					scopeRelativePath: string.Empty,
					matcherBaseRelativePath: matcherBaseRelativePath),
				_trackedPathIndexes);
		}

		public bool ContainsTrackedPathIndex(string repositoryRootPath)
		{
			try
			{
				return _trackedPathIndexes?.Contains(PathUtility.Normalize(repositoryRootPath)) == true;
			}
			catch
			{
				return false;
			}
		}

		public GitIgnoreScanContext WithTrackedPathIndex(GitTrackedPathIndex trackedPathIndex)
		{
			ArgumentNullException.ThrowIfNull(trackedPathIndex);
			if (ContainsTrackedPathIndex(trackedPathIndex.RepositoryRootPath))
				return this;

			return new GitIgnoreScanContext(
				_rules,
				_relativeMatcher,
				_baseRelativePath,
				_useCandidates,
				_evaluateRulesFallback,
				_additionalScopes,
				new AdditionalGitTrackedPathIndex(_trackedPathIndexes, trackedPathIndex));
		}

		public GitIgnoreEvaluation Evaluate(
			string fullPath,
			string relativePath,
			bool isDirectory,
			string name)
		{
			if (!_useCandidates && !_rules.IsGitIgnoreTraversalEnabled)
				return GitIgnoreEvaluation.NotIgnored;
			if (IsGitAdministrativeEntryForActiveScope(fullPath, name))
				return new GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: false);

			if (!_useCandidates && _rules.UseTrackedGitFilesOnly)
				return EvaluateTrackedFilesOnly(fullPath, isDirectory);

			GitIgnoreEvaluation evaluation;
			if (_relativeMatcher is null)
			{
				evaluation = _evaluateRulesFallback
					? _useCandidates
						? _rules.EvaluateGitIgnoreCandidate(fullPath, isDirectory, name)
						: _rules.EvaluateGitIgnore(fullPath, isDirectory, name)
					: GitIgnoreEvaluation.NotIgnored;
			}
			else
			{
				var matcherRelativePath = BuildMatcherRelativePath(relativePath);
				evaluation = matcherRelativePath.Length == 0
					? GitIgnoreEvaluation.NotIgnored
					: EvaluateWithSingleMatcherRelative(
						_relativeMatcher,
						matcherRelativePath,
						isDirectory,
						name);
			}

			var reIncludedIgnoredPath = false;
			if (_additionalScopes is not null)
			{
				evaluation = _additionalScopes.Evaluate(
					relativePath,
					isDirectory,
					name,
					evaluation,
					out reIncludedIgnoredPath);
			}

			if (!evaluation.IsIgnored && reIncludedIgnoredPath &&
			    HasExplicitlyIgnoredAncestor(fullPath, relativePath))
			{
				evaluation = new GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: false);
			}

			if (isDirectory && evaluation.IsIgnored && !evaluation.ShouldTraverseIgnoredDirectory &&
			    !EvaluateRulesOnly(fullPath, relativePath, isDirectory: true, name).IsIgnored &&
			    (_rules.HasGitIgnoreTraversalCandidate(fullPath, name, _useCandidates) ||
			     _additionalScopes?.HasTraversalCandidate(relativePath, name) == true))
			{
				evaluation = new GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: true);
			}

			return ApplyTrackedPathOverride(fullPath, isDirectory, evaluation);
		}

		private GitIgnoreEvaluation EvaluateTrackedFilesOnly(
			string fullPath,
			bool isDirectory)
		{
			if (_trackedPathIndexes?.Resolve(fullPath) is not { } trackedPath)
			{
				// Outside a known repository, directories remain traversal-only containers
				// so a workspace root can still discover repositories at any descendant depth.
				return new GitIgnoreEvaluation(
					IsIgnored: true,
					ShouldTraverseIgnoredDirectory: isDirectory);
			}

			if (!isDirectory)
			{
				return trackedPath.Index.ContainsNormalizedRelativePath(trackedPath.RelativePath)
					? GitIgnoreEvaluation.NotIgnored
					: new GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: false);
			}

			if (trackedPath.Index.ContainsOrHasDescendantNormalizedRelativePath(trackedPath.RelativePath))
				return GitIgnoreEvaluation.NotIgnored;

			// Untracked directories are traversal-only containers. This lets the scanner
			// discover an independent nested repository at any depth without exposing the
			// container when it has no tracked descendants.
			return new GitIgnoreEvaluation(
				IsIgnored: true,
				ShouldTraverseIgnoredDirectory: true);
		}

		private GitIgnoreEvaluation ApplyTrackedPathOverride(
			string fullPath,
			bool isDirectory,
			GitIgnoreEvaluation evaluation)
		{
			if (!evaluation.IsIgnored)
				return evaluation;

			if (_trackedPathIndexes?.Resolve(fullPath) is not { } trackedPath)
				return evaluation;

			if (!isDirectory)
			{
				return trackedPath.Index.ContainsNormalizedRelativePath(trackedPath.RelativePath)
					? GitIgnoreEvaluation.NotIgnored
					: evaluation;
			}

			if (trackedPath.Index.ContainsNormalizedRelativePath(trackedPath.RelativePath))
				return GitIgnoreEvaluation.NotIgnored;

			return trackedPath.Index.HasDescendantNormalizedRelativePath(trackedPath.RelativePath)
				? new GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: true)
				: evaluation;
		}

		private bool IsGitAdministrativeEntryForActiveScope(string fullPath, string name)
		{
			if (!IsGitAdministrativeEntry(name))
				return false;

			if (_useCandidates || _rules.UseGitIgnore || _rules.UseTrackedGitFilesOnly)
				return true;

			var parentPath = Path.GetDirectoryName(fullPath);
			return !string.IsNullOrWhiteSpace(parentPath) && ContainsScope(parentPath);
		}

		private GitIgnoreMatcher.IgnoreEvaluation EvaluateRulesOnly(
			string fullPath,
			string relativePath,
			bool isDirectory,
			string name)
		{
			GitIgnoreMatcher.IgnoreEvaluation evaluation;
			if (_relativeMatcher is null)
			{
				evaluation = _evaluateRulesFallback
					? _rules.EvaluateGitIgnoreRulesOnly(fullPath, isDirectory, name, _useCandidates)
					: default;
			}
			else
			{
				var matcherRelativePath = BuildMatcherRelativePath(relativePath);
				evaluation = matcherRelativePath.Length == 0
					? default
					: _relativeMatcher.EvaluateRelativeRulesOnlyNormalized(
						matcherRelativePath.AsSpan(),
						isDirectory,
						name);
			}

			return _additionalScopes?.EvaluateRulesOnly(relativePath, isDirectory, name, evaluation) ?? evaluation;
		}

		private bool HasExplicitlyIgnoredAncestor(string fullPath, string relativePath)
		{
			var ancestorFullPath = fullPath;
			var ancestorRelativePath = relativePath;
			while (true)
			{
				var separatorIndex = ancestorRelativePath.LastIndexOf('/');
				if (separatorIndex < 0)
					return false;

				ancestorRelativePath = ancestorRelativePath[..separatorIndex];
				ancestorFullPath = Path.GetDirectoryName(ancestorFullPath) ?? string.Empty;
				if (ancestorFullPath.Length == 0)
					return false;

				var ancestorName = Path.GetFileName(ancestorFullPath);
				if (EvaluateRulesOnly(
					    ancestorFullPath,
					    ancestorRelativePath,
					    isDirectory: true,
					    ancestorName)
				    .IsIgnored)
				{
					return true;
				}
			}
		}

		private string BuildMatcherRelativePath(string scanRelativePath)
		{
			if (_baseRelativePath.Length == 0)
				return scanRelativePath;

			if (string.IsNullOrEmpty(scanRelativePath))
				return _baseRelativePath;

			return $"{_baseRelativePath}/{scanRelativePath}";
		}

		private sealed class AdditionalGitIgnoreScope(
			AdditionalGitIgnoreScope? parent,
			ScopedGitIgnoreMatcher scopedMatcher,
			string scopeRelativePath,
			string? matcherBaseRelativePath = null)
		{
			private readonly AdditionalGitIgnoreScope? _parent = parent;
			private readonly ScopedGitIgnoreMatcher _scopedMatcher = scopedMatcher;

			public bool Contains(string scopeRootPath)
			{
				for (var current = this; current is not null; current = current._parent)
				{
					if (PathStringComparer.Equals(current._scopedMatcher.ScopeRootPath, scopeRootPath))
						return true;
				}

				return false;
			}

			public GitIgnoreEvaluation Evaluate(
				string scanRelativePath,
				bool isDirectory,
				string name,
				GitIgnoreEvaluation inherited,
				out bool reIncludedIgnoredPath)
			{
				var evaluation = inherited;
				reIncludedIgnoredPath = false;
				if (_parent is not null)
				{
					evaluation = _parent.Evaluate(
						scanRelativePath,
						isDirectory,
						name,
						inherited,
						out reIncludedIgnoredPath);
				}
				GitIgnoreMatcher.IgnoreEvaluation local;
				if (matcherBaseRelativePath is not null)
				{
					var matcherRelativePath = BuildAncestorMatcherRelativePath(scanRelativePath);
					if (matcherRelativePath.Length == 0)
						return evaluation;
					local = _scopedMatcher.Matcher.EvaluateRelativeNormalized(
						matcherRelativePath.AsSpan(),
						isDirectory,
						name);
				}
				else
				{
					if (!TryGetMatcherRelativePath(scanRelativePath, out var matcherRelativePath))
						return evaluation;
					local = _scopedMatcher.Matcher.EvaluateRelativeNormalized(
						matcherRelativePath,
						isDirectory,
						name);
				}
				if (local.HasMatch)
				{
					if (evaluation.IsIgnored && !local.IsIgnored)
						reIncludedIgnoredPath = true;
					evaluation = local.IsIgnored
						? new GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: false)
						: GitIgnoreEvaluation.NotIgnored;
				}

				return evaluation;
			}

			public GitIgnoreMatcher.IgnoreEvaluation EvaluateRulesOnly(
				string scanRelativePath,
				bool isDirectory,
				string name,
				GitIgnoreMatcher.IgnoreEvaluation inherited)
			{
				var evaluation = _parent?.EvaluateRulesOnly(
					scanRelativePath,
					isDirectory,
					name,
					inherited) ?? inherited;
				GitIgnoreMatcher.IgnoreEvaluation local;
				if (matcherBaseRelativePath is not null)
				{
					var matcherRelativePath = BuildAncestorMatcherRelativePath(scanRelativePath);
					if (matcherRelativePath.Length == 0)
						return evaluation;
					local = _scopedMatcher.Matcher.EvaluateRelativeRulesOnlyNormalized(
						matcherRelativePath.AsSpan(),
						isDirectory,
						name);
				}
				else
				{
					if (!TryGetMatcherRelativePath(scanRelativePath, out var matcherRelativePath))
						return evaluation;
					local = _scopedMatcher.Matcher.EvaluateRelativeRulesOnlyNormalized(
						matcherRelativePath,
						isDirectory,
						name);
				}
				return local.HasMatch ? local : evaluation;
			}

			public bool HasTraversalCandidate(string scanRelativePath, string name)
			{
				if (_parent?.HasTraversalCandidate(scanRelativePath, name) == true)
					return true;

				if (matcherBaseRelativePath is not null)
				{
					var matcherRelativePath = BuildAncestorMatcherRelativePath(scanRelativePath);
					return matcherRelativePath.Length > 0 &&
					       _scopedMatcher.Matcher.ShouldTraverseIgnoredDirectoryRelativeNormalized(
						       matcherRelativePath.AsSpan(),
						       name);
				}

				return TryGetMatcherRelativePath(scanRelativePath, out var descendantRelativePath) &&
				       _scopedMatcher.Matcher.ShouldTraverseIgnoredDirectoryRelativeNormalized(
					       descendantRelativePath,
					       name);
			}

			private string BuildAncestorMatcherRelativePath(string scanRelativePath)
			{
				if (matcherBaseRelativePath!.Length == 0)
					return scanRelativePath;
				if (scanRelativePath.Length == 0)
					return matcherBaseRelativePath;

				return $"{matcherBaseRelativePath}/{scanRelativePath}";
			}

			private bool TryGetMatcherRelativePath(
				string scanRelativePath,
				out ReadOnlySpan<char> matcherRelativePath)
			{
				var relativePath = scanRelativePath.AsSpan();
				if (scopeRelativePath.Length == 0)
				{
					matcherRelativePath = relativePath;
					return relativePath.Length > 0;
				}

				if (relativePath.Length <= scopeRelativePath.Length ||
				    relativePath[scopeRelativePath.Length] != '/' ||
				    !relativePath[..scopeRelativePath.Length].Equals(
					    scopeRelativePath.AsSpan(),
					    PathComparison))
				{
					matcherRelativePath = default;
					return false;
				}

				matcherRelativePath = relativePath[(scopeRelativePath.Length + 1)..];
				return matcherRelativePath.Length > 0;
			}
		}

		private sealed class AdditionalGitTrackedPathIndex(
			AdditionalGitTrackedPathIndex? parent,
			GitTrackedPathIndex trackedPathIndex)
		{
			private readonly AdditionalGitTrackedPathIndex? _parent = parent;
			private readonly GitTrackedPathIndex _trackedPathIndex = trackedPathIndex;

			public bool Contains(string repositoryRootPath)
			{
				for (var current = this; current is not null; current = current._parent)
				{
					if (current._trackedPathIndex.MatchesRepositoryRoot(repositoryRootPath))
					{
						return true;
					}
				}

				return false;
			}

			public ResolvedGitTrackedPath? Resolve(string fullPath)
			{
				ResolvedGitTrackedPath? bestMatch = null;
				for (var current = this; current is not null; current = current._parent)
				{
					var candidate = current._trackedPathIndex;
					if (!candidate.TryGetNormalizedRelativePath(fullPath, out var relativePath))
						continue;
					if (bestMatch is null ||
					    candidate.RepositoryRootPath.Length > bestMatch.Value.Index.RepositoryRootPath.Length)
					{
						bestMatch = new ResolvedGitTrackedPath(candidate, relativePath);
					}
				}

				return bestMatch;
			}
		}

		private readonly record struct ResolvedGitTrackedPath(
			GitTrackedPathIndex Index,
			string RelativePath);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool IsGitAdministrativeEntry(string name) =>
		PathStringComparer.Equals(name, ".git");
}

public sealed record ScopedGitIgnoreMatcher(
	string ScopeRootPath,
	GitIgnoreMatcher Matcher);

public sealed record ScopedSmartIgnoreMatcher(
	string ScopeRootPath,
	IReadOnlySet<string> FolderNames,
	IReadOnlySet<string> FileNames,
	IReadOnlySet<string> EvidenceRequiredFolderNames);
