namespace DevProjex.Tests.Unit;

public sealed class GitTrackedPathIndexTests
{
	[Fact]
	public void ExactAndDescendantLookups_NormalizeSeparatorsDeduplicateAndStayInsideRepository()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateFolder("repo");
		var index = new GitTrackedPathIndex(
			repositoryRoot,
			[
				"src/main.cs",
				Path.Combine("src", "main.cs"),
				"src/deep/файл без расширения",
				"../outside.txt",
				Path.DirectorySeparatorChar + "absolute.txt",
				string.Empty
			]);

		Assert.Equal(2, index.Count);
		Assert.True(index.Contains(Path.Combine(repositoryRoot, "src", "main.cs")));
		Assert.True(index.Contains(Path.Combine(repositoryRoot, "src", "deep", "файл без расширения")));
		Assert.True(index.HasDescendant(Path.Combine(repositoryRoot, "src")));
		Assert.True(index.HasDescendant(Path.Combine(repositoryRoot, "src", "deep")));
		Assert.False(index.Contains(Path.Combine(repositoryRoot, "src", "missing.cs")));
		Assert.False(index.HasDescendant(Path.Combine(repositoryRoot, "other")));
		Assert.False(index.Contains(Path.Combine(temp.Path, "outside.txt")));
	}

	[Fact]
	public void BackslashInUnixFileNameIsNotTreatedAsDirectorySeparator()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("Windows does not allow a backslash inside a file name.");

		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateFolder("repo");
		const string trackedName = @"literal\name.tmp";
		var index = new GitTrackedPathIndex(repositoryRoot, [trackedName]);

		Assert.Equal(1, index.Count);
		Assert.True(index.Contains(Path.Combine(repositoryRoot, trackedName)));
		Assert.False(index.HasDescendant(Path.Combine(repositoryRoot, "literal")));
	}

	[Fact]
	public void ScanContext_TrackedFilesOverrideGitIgnoreButNotOtherIgnoreControllers()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateFolder("repo");
		var trackedFile = temp.CreateFile("repo/cache/tracked.tmp", "tracked");
		var untrackedFile = temp.CreateFile("repo/cache/untracked.tmp", "untracked");
		var matcher = GitIgnoreMatcher.Build(repositoryRoot, ["cache/", "*.tmp"]);
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(["tracked.tmp"], StringComparer.OrdinalIgnoreCase))
		{
			UseGitIgnore = true,
			EnableGitIgnoreTraversal = true,
			UseSmartIgnore = true,
			GitIgnoreMatcher = matcher
		};
		var context = rules
			.CreateGitIgnoreScanContext(repositoryRoot)
			.WithTrackedPathIndex(new GitTrackedPathIndex(repositoryRoot, ["cache/tracked.tmp"]));

		var directory = context.Evaluate(
			Path.Combine(repositoryRoot, "cache"),
			"cache",
			isDirectory: true,
			"cache");
		var tracked = context.Evaluate(trackedFile, "cache/tracked.tmp", isDirectory: false, "tracked.tmp");
		var untracked = context.Evaluate(untrackedFile, "cache/untracked.tmp", isDirectory: false, "untracked.tmp");

		Assert.True(directory.IsIgnored);
		Assert.True(directory.ShouldTraverseIgnoredDirectory);
		Assert.False(tracked.IsIgnored);
		Assert.True(untracked.IsIgnored);
		Assert.Equal(
			IgnoreDecisionOwner.SmartIgnore,
			IgnoreDecisionEngine.EvaluateFile(
				trackedFile,
				"tracked.tmp",
				isHidden: false,
				length: 7,
				rules,
				shouldApplySmartIgnore: true,
				tracked).Owner);
	}

	[Fact]
	public void ScanContext_ExactTrackedDirectoryEntryOverridesGitIgnore()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateFolder("repo");
		var trackedDirectory = temp.CreateFolder("repo/submodule");
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			UseGitIgnore = true,
			EnableGitIgnoreTraversal = true,
			GitIgnoreMatcher = GitIgnoreMatcher.Build(repositoryRoot, ["submodule/"])
		};
		var context = rules
			.CreateGitIgnoreScanContext(repositoryRoot)
			.WithTrackedPathIndex(new GitTrackedPathIndex(repositoryRoot, ["submodule"]));

		var evaluation = context.Evaluate(
			trackedDirectory,
			"submodule",
			isDirectory: true,
			"submodule");

		Assert.False(evaluation.IsIgnored);
	}

	[Fact]
	public void ScanContext_DeepestRepositoryIndexOwnsNestedWorkingTree()
	{
		using var temp = new TemporaryDirectory();
		var outerRoot = Path.GetFullPath(temp.CreateFolder("outer"));
		var nestedRoot = Path.GetFullPath(temp.CreateFolder("outer/modules/nested"));
		var nestedTrackedFile = Path.GetFullPath(temp.CreateFile("outer/modules/nested/tracked.tmp", "tracked"));
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			UseGitIgnore = true,
			EnableGitIgnoreTraversal = true,
			GitIgnoreMatcher = GitIgnoreMatcher.Build(outerRoot, ["*.tmp"])
		};
		var nestedIndex = new GitTrackedPathIndex(nestedRoot, ["tracked.tmp"]);
		Assert.True(nestedIndex.Contains(nestedTrackedFile));
		Assert.True(
			rules.CreateGitIgnoreScanContext(outerRoot)
				.WithTrackedPathIndex(nestedIndex)
				.ContainsTrackedPathIndex(nestedRoot));
		var context = rules
			.CreateGitIgnoreScanContext(outerRoot)
			.WithTrackedPathIndex(new GitTrackedPathIndex(outerRoot, []))
			.WithTrackedPathIndex(nestedIndex);
		Assert.True(context.ContainsTrackedPathIndex(outerRoot));
		Assert.True(context.ContainsTrackedPathIndex(nestedRoot));

		var evaluation = context.Evaluate(
			nestedTrackedFile,
			"modules/nested/tracked.tmp",
			isDirectory: false,
			"tracked.tmp");

		Assert.False(evaluation.IsIgnored);
	}
}
