using System;
using System.Collections.Generic;
using System.IO;

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

	public bool UseGitIgnore { get; init; }
	public bool UseSmartIgnore { get; init; }

	public GitIgnoreMatcher GitIgnoreMatcher { get; init; } = GitIgnoreMatcher.Empty;

	public IReadOnlyList<ScopedGitIgnoreMatcher> ScopedGitIgnoreMatchers { get; init; } =
		Array.Empty<ScopedGitIgnoreMatcher>();

	public IReadOnlyList<string> SmartIgnoreScopeRoots { get; init; } =
		Array.Empty<string>();

	public GitIgnoreMatcher ResolveGitIgnoreMatcher(string fullPath)
	{
		if (!UseGitIgnore)
			return GitIgnoreMatcher.Empty;

		if (ScopedGitIgnoreMatchers.Count == 0)
			return GitIgnoreMatcher;

		foreach (var scoped in ScopedGitIgnoreMatchers)
		{
			if (IsPathInsideScope(fullPath, scoped.ScopeRootPath))
				return scoped.Matcher;
		}

		return GitIgnoreMatcher.Empty;
	}

	public bool ShouldApplySmartIgnore(string fullPath)
	{
		if (!UseSmartIgnore)
			return false;

		if (SmartIgnoreScopeRoots.Count == 0)
			return true;

		foreach (var scopeRoot in SmartIgnoreScopeRoots)
		{
			if (IsPathInsideScope(fullPath, scopeRoot))
				return true;
		}

		return false;
	}

	private static bool IsPathInsideScope(string fullPath, string scopeRootPath)
	{
		if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(scopeRootPath))
			return false;

		if (!fullPath.StartsWith(scopeRootPath, PathComparison))
			return false;

		if (fullPath.Length == scopeRootPath.Length)
			return true;

		var next = fullPath[scopeRootPath.Length];
		return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
	}
}

public sealed record ScopedGitIgnoreMatcher(
	string ScopeRootPath,
	GitIgnoreMatcher Matcher);
