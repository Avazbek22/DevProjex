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
	private readonly ConcurrentDictionary<string, bool> _smartScopeApplicabilityCache =
		new(PathStringComparer);

	public bool UseGitIgnore { get; init; }
	public bool UseSmartIgnore { get; init; }
	public bool IgnoreEmptyFolders { get; init; }
	public bool IgnoreEmptyFiles { get; init; }
	public bool IgnoreExtensionlessFiles { get; init; }

	public GitIgnoreMatcher GitIgnoreMatcher { get; init; } = GitIgnoreMatcher.Empty;

	public IReadOnlyList<ScopedGitIgnoreMatcher> ScopedGitIgnoreMatchers { get; init; } =
		[];

	public IReadOnlyList<string> SmartIgnoreScopeRoots { get; init; } =
		[];

	public IReadOnlyList<ScopedSmartIgnoreMatcher> ScopedSmartIgnoreMatchers { get; init; } =
		[];

	public readonly record struct GitIgnoreEvaluation(bool IsIgnored, bool ShouldTraverseIgnoredDirectory)
	{
		public static readonly GitIgnoreEvaluation NotIgnored = new(false, false);
	}

	public GitIgnoreScanContext CreateGitIgnoreScanContext(string scanRootPath)
	{
		if (!UseGitIgnore || string.IsNullOrWhiteSpace(scanRootPath))
			return GitIgnoreScanContext.Disabled(this);

		var matcher = ResolveSingleMatcherForScanRoot(scanRootPath);
		if (matcher is null ||
		    !matcher.TryGetRelativePath(scanRootPath, out var baseRelativePath, allowRoot: true))
		{
			return GitIgnoreScanContext.Disabled(this);
		}

		return GitIgnoreScanContext.Relative(this, matcher, baseRelativePath);
	}

	public GitIgnoreMatcher ResolveGitIgnoreMatcher(string fullPath)
	{
		if (!UseGitIgnore)
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
		if (!UseGitIgnore)
			return GitIgnoreEvaluation.NotIgnored;

		var scopedCount = ScopedGitIgnoreMatchers.Count;
		if (scopedCount == 0)
			return EvaluateWithSingleMatcher(GitIgnoreMatcher, fullPath, isDirectory, name);

		if (scopedCount == 1)
		{
			var scoped = ScopedGitIgnoreMatchers[0];
			if (!IsPathInsideScope(fullPath, scoped.ScopeRootPath))
				return GitIgnoreEvaluation.NotIgnored;

			return EvaluateWithSingleMatcher(scoped.Matcher, fullPath, isDirectory, name);
		}

		var scopedMatchers = GetApplicableGitIgnoreMatchers(fullPath, isDirectory);
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

	private GitIgnoreMatcher? ResolveSingleMatcherForScanRoot(string scanRootPath)
	{
		if (ScopedGitIgnoreMatchers.Count == 0)
			return ReferenceEquals(GitIgnoreMatcher, GitIgnoreMatcher.Empty) ? null : GitIgnoreMatcher;

		if (ScopedGitIgnoreMatchers.Count != 1)
			return null;

		var scoped = ScopedGitIgnoreMatchers[0];
		return IsPathInsideScope(scanRootPath, scoped.ScopeRootPath)
			? scoped.Matcher
			: null;
	}

	public bool ShouldApplySmartIgnore(string fullPath)
	{
		return ShouldApplySmartIgnore(fullPath, isDirectory: true);
	}

	public bool ShouldApplySmartIgnore(string fullPath, bool isDirectory)
	{
		if (!UseSmartIgnore)
			return false;

		if (SmartIgnoreScopeRoots.Count == 0)
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

		if (_smartScopeApplicabilityCache.TryGetValue(probePath, out var cached))
			return cached;

		var applies = false;
		foreach (var scopeRoot in SmartIgnoreScopeRoots)
		{
			if (!IsPathInsideScope(probePath, scopeRoot))
				continue;

			applies = true;
			break;
		}

		_smartScopeApplicabilityCache[probePath] = applies;
		if (_smartScopeApplicabilityCache.Count > SmartScopeApplicabilityCacheLimit)
			_smartScopeApplicabilityCache.Clear();

		return applies;
	}

	public bool IsSmartIgnoredDirectory(string fullPath, string name)
	{
		if (!ShouldApplySmartIgnore(fullPath, isDirectory: true))
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

	public bool IsSmartIgnoredFile(string fullPath, string name, bool shouldApplySmartIgnore)
	{
		if (!shouldApplySmartIgnore || !UseSmartIgnore)
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

	private ScopedGitIgnoreMatcher[] GetApplicableGitIgnoreMatchers(string fullPath, bool isDirectory)
	{
		if (ScopedGitIgnoreMatchers.Count == 0 || string.IsNullOrWhiteSpace(fullPath))
			return [];

		var cacheKeyPath = fullPath;
		if (!isDirectory)
		{
			var parentDirectory = Path.GetDirectoryName(fullPath);
			if (string.IsNullOrWhiteSpace(parentDirectory))
				return [];

			cacheKeyPath = parentDirectory;
		}

		if (_scopedMatcherChainCache.TryGetValue(cacheKeyPath, out var cached))
			return cached;

		var matched = new List<ScopedGitIgnoreMatcher>();
		foreach (var scoped in ScopedGitIgnoreMatchers)
		{
			if (IsPathInsideScope(cacheKeyPath, scoped.ScopeRootPath))
				matched.Add(scoped);
		}

		ScopedGitIgnoreMatcher[] resolved = matched.Count == 0
			? Array.Empty<ScopedGitIgnoreMatcher>()
			: [.. matched];
		_scopedMatcherChainCache[cacheKeyPath] = resolved;
		if (_scopedMatcherChainCache.Count > ScopedMatcherChainCacheLimit)
			_scopedMatcherChainCache.Clear();

		return resolved;
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

		private GitIgnoreScanContext(IgnoreRules rules, GitIgnoreMatcher? relativeMatcher, string baseRelativePath)
		{
			_rules = rules;
			_relativeMatcher = relativeMatcher;
			_baseRelativePath = baseRelativePath;
		}

		public static GitIgnoreScanContext Disabled(IgnoreRules rules) =>
			new(rules, relativeMatcher: null, baseRelativePath: string.Empty);

		public static GitIgnoreScanContext Relative(
			IgnoreRules rules,
			GitIgnoreMatcher matcher,
			string baseRelativePath) =>
			new(rules, matcher, baseRelativePath);

		public GitIgnoreEvaluation Evaluate(
			string fullPath,
			string relativePath,
			bool isDirectory,
			string name)
		{
			if (!_rules.UseGitIgnore)
				return GitIgnoreEvaluation.NotIgnored;

			if (_relativeMatcher is null)
				return _rules.EvaluateGitIgnore(fullPath, isDirectory, name);

			var matcherRelativePath = BuildMatcherRelativePath(relativePath);
			if (matcherRelativePath.Length == 0)
				return GitIgnoreEvaluation.NotIgnored;

			return EvaluateWithSingleMatcherRelative(
				_relativeMatcher,
				matcherRelativePath,
				isDirectory,
				name);
		}

		private string BuildMatcherRelativePath(string scanRelativePath)
		{
			if (_baseRelativePath.Length == 0)
				return scanRelativePath;

			if (string.IsNullOrEmpty(scanRelativePath))
				return _baseRelativePath;

			return $"{_baseRelativePath}/{scanRelativePath}";
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
