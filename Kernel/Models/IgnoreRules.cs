using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace DevProjex.Kernel.Models;

public sealed record IgnoreRules(
	bool IgnoreHiddenFolders,
	bool IgnoreHiddenFiles,
	bool IgnoreDotFolders,
	bool IgnoreDotFiles,
	IReadOnlySet<string> SmartIgnoredFolders,
	IReadOnlySet<string> SmartIgnoredFiles)
{
	private static readonly StringComparison PathComparison = OperatingSystem.IsLinux()
		? StringComparison.Ordinal
		: StringComparison.OrdinalIgnoreCase;
	private static readonly StringComparer PathStringComparer = OperatingSystem.IsLinux()
		? StringComparer.Ordinal
		: StringComparer.OrdinalIgnoreCase;
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

	public bool UseGitIgnore { get; init; }
	public bool EnableGitIgnoreTraversal { get; init; }
	public bool IsGitIgnoreTraversalEnabled => UseGitIgnore || EnableGitIgnoreTraversal;
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

	public bool SmartIgnoreFollowsGitIgnore { get; init; }

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
		var context = CreateGitIgnoreScanContext(scanRootPath);
		foreach (var matcher in additionalMatchers)
		{
			if (!TryGetScopeRelativePath(scanRootPath, matcher.ScopeRootPath, out var scopeRelativePath))
				continue;

			context = context.WithScope(matcher, scopeRelativePath);
		}

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
				return GitIgnoreScanContext.Disabled(this, useCandidates);
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

		foreach (var scoped in scopedMatchers)
		{
			if (isDirectory && scoped.Matcher.HasNegationRules)
				hasNegationAwareScope = true;

			var evaluation = scoped.Matcher.Evaluate(fullPath, isDirectory, name);
			if (!evaluation.HasMatch)
				continue;

			hasMatch = true;
			ignored = evaluation.IsIgnored;
		}

		if (!hasMatch || !ignored)
			return GitIgnoreEvaluation.NotIgnored;

		if (!isDirectory || !hasNegationAwareScope)
			return new GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: false);

		foreach (var scoped in scopedMatchers)
		{
			if (scoped.Matcher.ShouldTraverseIgnoredDirectory(fullPath, name))
				return new GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: true);
		}

		return new GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: false);
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

		cache[probePath] = applies;
		if (cache.Count > SmartScopeApplicabilityCacheLimit)
			cache.Clear();

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
		// Hybrid contract: stack descriptors and stack-adjacent fingerprints remain inside
		// their discovered project scope. Only signature-confirmed portable dependency
		// stores may cross that boundary, so user-level package caches are removed without
		// treating ordinary sibling folders named bin, obj, packages, or build as artifacts.
		if (IsSmartArtifactIgnoredDirectory(
				SmartArtifactIgnoreMatcher,
				fullPath,
				name,
				portableOnly: !appliesToProjectScope))
			return true;

		if (!appliesToProjectScope)
			return false;

		if (!SmartIgnoredFolders.Contains(name))
			return false;

		if (ScopedSmartIgnoreMatchers.Count == 0)
			return true;

		foreach (var scoped in ScopedSmartIgnoreMatchers)
		{
			if (scoped.FolderNames.Contains(name) && IsPathInsideScope(fullPath, scoped.ScopeRootPath))
				return true;
		}

		return false;
	}

	public bool IsSmartIgnoredDirectoryCandidate(string fullPath, string name)
	{
		var appliesToProjectScope = ShouldApplySmartIgnoreCandidate(fullPath, isDirectory: true);
		if (IsSmartArtifactIgnoredDirectory(
				SmartArtifactIgnoreCandidateMatcher,
				fullPath,
				name,
				portableOnly: !appliesToProjectScope))
			return true;

		if (!appliesToProjectScope)
			return false;

		var candidateFolders = SmartIgnoreCandidateFolders ?? SmartIgnoredFolders;
		if (!candidateFolders.Contains(name))
			return false;

		var scopedMatchers = GetScopedSmartIgnoreMatchers(useCandidates: true);
		if (scopedMatchers.Count == 0)
			return true;

		foreach (var scoped in scopedMatchers)
		{
			if (scoped.FolderNames.Contains(name) && IsPathInsideScope(fullPath, scoped.ScopeRootPath))
				return true;
		}

		return false;
	}

	public bool IsSmartIgnoredFile(string fullPath, string name, bool shouldApplySmartIgnore)
	{
		if (!UseSmartIgnore)
			return false;

		if (SmartArtifactIgnoreMatcher.IsIgnoredFile(name))
			return true;

		if (!shouldApplySmartIgnore)
			return false;

		if (!SmartIgnoredFiles.Contains(name))
			return false;

		if (ScopedSmartIgnoreMatchers.Count == 0)
			return true;

		foreach (var scoped in ScopedSmartIgnoreMatchers)
		{
			if (scoped.FileNames.Contains(name) && IsPathInsideScope(fullPath, scoped.ScopeRootPath))
				return true;
		}

		return false;
	}

	public bool IsSmartIgnoredFileCandidate(string fullPath, string name, bool shouldApplySmartIgnore)
	{
		if (SmartArtifactIgnoreCandidateMatcher.IsIgnoredFile(name))
			return true;

		if (!shouldApplySmartIgnore)
			return false;

		var candidateFiles = SmartIgnoreCandidateFiles ?? SmartIgnoredFiles;
		if (!candidateFiles.Contains(name))
			return false;

		var scopedMatchers = GetScopedSmartIgnoreMatchers(useCandidates: true);
		if (scopedMatchers.Count == 0)
			return true;

		foreach (var scoped in scopedMatchers)
		{
			if (scoped.FileNames.Contains(name) && IsPathInsideScope(fullPath, scoped.ScopeRootPath))
				return true;
		}

		return false;
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
		cache[cacheKeyPath] = resolved;
		if (cache.Count > ScopedMatcherChainCacheLimit)
			cache.Clear();

		return resolved;
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

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool IsPathInsideScope(string fullPath, string scopeRootPath)
	{
		if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(scopeRootPath))
			return false;

		// Use Span for faster comparison
		var fullSpan = fullPath.AsSpan();
		var scopeSpan = scopeRootPath.AsSpan();

		if (!fullSpan.StartsWith(scopeSpan, PathComparison))
			return false;

		if (fullSpan.Length == scopeSpan.Length)
			return true;

		var next = fullSpan[scopeSpan.Length];
		return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
	}

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

		private GitIgnoreScanContext(
			IgnoreRules rules,
			GitIgnoreMatcher? relativeMatcher,
			string baseRelativePath,
			bool useCandidates,
			bool evaluateRulesFallback = false,
			AdditionalGitIgnoreScope? additionalScopes = null)
		{
			_rules = rules;
			_relativeMatcher = relativeMatcher;
			_baseRelativePath = baseRelativePath;
			_useCandidates = useCandidates;
			_evaluateRulesFallback = evaluateRulesFallback;
			_additionalScopes = additionalScopes;
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
					scopeRelativePath));
		}

		public GitIgnoreEvaluation Evaluate(
			string fullPath,
			string relativePath,
			bool isDirectory,
			string name)
		{
			if (!_useCandidates && !_rules.IsGitIgnoreTraversalEnabled)
				return GitIgnoreEvaluation.NotIgnored;

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

			if (_additionalScopes is not null)
				evaluation = _additionalScopes.Evaluate(relativePath, isDirectory, name, evaluation);

			if (isDirectory && evaluation.IsIgnored && !evaluation.ShouldTraverseIgnoredDirectory &&
			    (_rules.HasGitIgnoreTraversalCandidate(fullPath, name, _useCandidates) ||
			     _additionalScopes?.HasTraversalCandidate(relativePath, name) == true))
			{
				return new GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: true);
			}

			return evaluation;
		}

		private string BuildMatcherRelativePath(string scanRelativePath)
		{
			if (_baseRelativePath.Length == 0)
				return scanRelativePath;

			if (string.IsNullOrEmpty(scanRelativePath))
				return _baseRelativePath;

			return $"{_baseRelativePath}/{scanRelativePath}";
		}

		private sealed class AdditionalGitIgnoreScope
		{
			private readonly AdditionalGitIgnoreScope? _parent;
			private readonly ScopedGitIgnoreMatcher _scopedMatcher;
			private readonly string _scopeRelativePath;

			public AdditionalGitIgnoreScope(
				AdditionalGitIgnoreScope? parent,
				ScopedGitIgnoreMatcher scopedMatcher,
				string scopeRelativePath)
			{
				_parent = parent;
				_scopedMatcher = scopedMatcher;
				_scopeRelativePath = scopeRelativePath;
			}

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
				GitIgnoreEvaluation inherited)
			{
				var evaluation = _parent?.Evaluate(scanRelativePath, isDirectory, name, inherited) ?? inherited;
				if (!TryGetMatcherRelativePath(scanRelativePath, out var matcherRelativePath))
					return evaluation;

				var local = _scopedMatcher.Matcher.EvaluateRelativeNormalized(
					matcherRelativePath,
					isDirectory,
					name);
				if (local.HasMatch)
				{
					evaluation = local.IsIgnored
						? new GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: false)
						: GitIgnoreEvaluation.NotIgnored;
				}

				return evaluation;
			}

			public bool HasTraversalCandidate(string scanRelativePath, string name)
			{
				if (_parent?.HasTraversalCandidate(scanRelativePath, name) == true)
					return true;

				return TryGetMatcherRelativePath(scanRelativePath, out var matcherRelativePath) &&
				       _scopedMatcher.Matcher.ShouldTraverseIgnoredDirectoryRelativeNormalized(
					       matcherRelativePath,
					       name);
			}

			private bool TryGetMatcherRelativePath(
				string scanRelativePath,
				out ReadOnlySpan<char> matcherRelativePath)
			{
				var relativePath = scanRelativePath.AsSpan();
				if (_scopeRelativePath.Length == 0)
				{
					matcherRelativePath = relativePath;
					return relativePath.Length > 0;
				}

				if (relativePath.Length <= _scopeRelativePath.Length ||
				    relativePath[_scopeRelativePath.Length] != '/' ||
				    !relativePath[.._scopeRelativePath.Length].Equals(
					    _scopeRelativePath.AsSpan(),
					    PathComparison))
				{
					matcherRelativePath = default;
					return false;
				}

				matcherRelativePath = relativePath[(_scopeRelativePath.Length + 1)..];
				return matcherRelativePath.Length > 0;
			}
		}
	}
}

public sealed record ScopedGitIgnoreMatcher(
	string ScopeRootPath,
	GitIgnoreMatcher Matcher);

public sealed record ScopedSmartIgnoreMatcher(
	string ScopeRootPath,
	IReadOnlySet<string> FolderNames,
	IReadOnlySet<string> FileNames);
