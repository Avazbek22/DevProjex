using System.Collections.Generic;
using System.IO;
using System.Linq;
using DevProjex.Kernel.Models;

namespace DevProjex.Application.Services;

public sealed class IgnoreRulesService
{
	private readonly SmartIgnoreService _smartIgnore;

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
			return GitIgnoreMatcher.Build(rootPath, File.ReadLines(gitIgnorePath));
		}
		catch
		{
			return GitIgnoreMatcher.Empty;
		}
	}
}
