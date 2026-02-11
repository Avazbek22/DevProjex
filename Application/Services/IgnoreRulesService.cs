using System.Collections.Generic;
using System.IO;
using System.Linq;
using DevProjex.Kernel.Models;

namespace DevProjex.Application.Services;

public sealed class IgnoreRulesService
{
	private readonly SmartIgnoreService _smartIgnore;
	private const int CacheLimit = 64;
	private static readonly object CacheSync = new();
	private static readonly Dictionary<string, GitIgnoreCacheEntry> GitIgnoreCache =
		new(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

	public IgnoreRulesService(SmartIgnoreService smartIgnore)
	{
		_smartIgnore = smartIgnore;
	}

	public IgnoreRules Build(string rootPath, IReadOnlyCollection<IgnoreOptionId> selectedOptions)
	{
		var smart = _smartIgnore.Build(rootPath);
		var useGitIgnore = selectedOptions.Contains(IgnoreOptionId.UseGitIgnore);
		var gitIgnoreMatcher = GitIgnoreMatcher.Empty;
		if (useGitIgnore)
		{
			gitIgnoreMatcher = TryBuildGitIgnoreMatcher(rootPath);
			useGitIgnore = !ReferenceEquals(gitIgnoreMatcher, GitIgnoreMatcher.Empty);
		}

		return new IgnoreRules(
			IgnoreBinFolders: selectedOptions.Contains(IgnoreOptionId.BinFolders),
			IgnoreObjFolders: selectedOptions.Contains(IgnoreOptionId.ObjFolders),
			IgnoreHiddenFolders: selectedOptions.Contains(IgnoreOptionId.HiddenFolders),
			IgnoreHiddenFiles: selectedOptions.Contains(IgnoreOptionId.HiddenFiles),
			IgnoreDotFolders: selectedOptions.Contains(IgnoreOptionId.DotFolders),
			IgnoreDotFiles: selectedOptions.Contains(IgnoreOptionId.DotFiles),
			SmartIgnoredFolders: smart.FolderNames,
			SmartIgnoredFiles: smart.FileNames)
		{
			UseGitIgnore = useGitIgnore,
			GitIgnoreMatcher = gitIgnoreMatcher
		};
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
}
