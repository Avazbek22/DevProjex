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
		Assert.True(index.ContainsOrHasDescendant(Path.Combine(repositoryRoot, "src")));
		Assert.True(index.ContainsOrHasDescendant(Path.Combine(repositoryRoot, "src", "main.cs")));
		Assert.False(index.Contains(Path.Combine(repositoryRoot, "src", "missing.cs")));
		Assert.False(index.HasDescendant(Path.Combine(repositoryRoot, "other")));
		Assert.False(index.ContainsOrHasDescendant(Path.Combine(repositoryRoot, "other")));
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
	public void DescendantLookups_DoNotConfuseSiblingPrefixes()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateFolder("repo");
		var index = new GitTrackedPathIndex(
			repositoryRoot,
			[
				"src-old/file.txt",
				"source/file.txt",
				"tests/deep/test.cs"
			]);

		Assert.False(index.HasDescendant(Path.Combine(repositoryRoot, "src")));
		Assert.False(index.ContainsOrHasDescendant(Path.Combine(repositoryRoot, "test")));
		Assert.True(index.HasDescendant(Path.Combine(repositoryRoot, "src-old")));
		Assert.True(index.HasDescendant(Path.Combine(repositoryRoot, "source")));
		Assert.True(index.HasDescendant(Path.Combine(repositoryRoot, "tests", "deep")));
		Assert.False(index.HasDescendant(repositoryRoot + "-other"));
	}

	[Fact]
	public void RepositoryAtFilesystemRoot_ResolvesTrackedChildren()
	{
		var filesystemRoot = Path.GetPathRoot(Path.GetTempPath());
		Assert.NotNull(filesystemRoot);
		var index = new GitTrackedPathIndex(filesystemRoot, ["tracked.cs"]);

		Assert.True(index.Contains(Path.Combine(filesystemRoot, "tracked.cs")));
		Assert.True(index.HasDescendant(filesystemRoot));
	}

	[Fact]
	public void Lookups_FollowPlatformPathCaseSemantics()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateFolder("repo");
		var index = new GitTrackedPathIndex(repositoryRoot, ["Src/App.cs"]);
		var differentlyCasedDirectory = Path.Combine(repositoryRoot, "src");
		var differentlyCasedFile = Path.Combine(differentlyCasedDirectory, "app.cs");
		var expectedMatch = OperatingSystem.IsWindows();

		Assert.Equal(expectedMatch, index.HasDescendant(differentlyCasedDirectory));
		Assert.Equal(expectedMatch, index.Contains(differentlyCasedFile));
	}

	[Fact]
	public void ExplicitCaseInsensitiveSemantics_DeduplicateAndMatchRepositoryPaths()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateFolder("repo");
		var index = new GitTrackedPathIndex(
			repositoryRoot,
			["Src/App.cs", "src/app.cs"],
			new GitPathComparisonSemantics(
				IgnoreCase: true,
				NormalizeUnicode: false));

		Assert.Equal(1, index.Count);
		Assert.True(index.Contains(Path.Combine(repositoryRoot, "SRC", "APP.CS")));
		Assert.True(index.HasDescendant(Path.Combine(repositoryRoot, "sRc")));
	}

	[Fact]
	public void ExplicitCaseSensitiveSemantics_DoNotMatchDifferentCasing()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateFolder("repo");
		var index = new GitTrackedPathIndex(
			repositoryRoot,
			["Src/App.cs"],
			new GitPathComparisonSemantics(
				IgnoreCase: false,
				NormalizeUnicode: false));

		Assert.False(index.Contains(Path.Combine(repositoryRoot, "src", "app.cs")));
		Assert.False(index.HasDescendant(Path.Combine(repositoryRoot, "src")));
	}

	[Fact]
	public void UnicodeNormalizationSemantics_MatchCanonicallyEquivalentGitPaths()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateFolder("repo");
		const string precomposedName = "caf\u00e9.cs";
		const string decomposedName = "cafe\u0301.cs";
		var index = new GitTrackedPathIndex(
			repositoryRoot,
			[precomposedName, decomposedName],
			new GitPathComparisonSemantics(
				IgnoreCase: false,
				NormalizeUnicode: true));

		Assert.Equal(1, index.Count);
		Assert.True(index.Contains(Path.Combine(repositoryRoot, decomposedName)));
	}

	[Fact]
	public void CaseSensitiveNonNormalizingSemantics_PreserveDistinctUnixNames()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateFolder("repo");
		const string precomposedName = "caf\u00e9.cs";
		const string decomposedName = "cafe\u0301.cs";
		var index = new GitTrackedPathIndex(
			repositoryRoot,
			[precomposedName],
			new GitPathComparisonSemantics(
				IgnoreCase: false,
				NormalizeUnicode: false));

		Assert.False(index.Contains(Path.Combine(repositoryRoot, decomposedName)));
	}

	[Fact]
	public void ScanContext_UsesRepositorySpecificCaseAndUnicodeSemanticsToResolveIndex()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateFolder("r\u00e9po");
		var index = new GitTrackedPathIndex(
			repositoryRoot,
			["Src/caf\u00e9.cs"],
			new GitPathComparisonSemantics(
				IgnoreCase: true,
				NormalizeUnicode: true));
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			UseTrackedGitFilesOnly = true,
			EnableGitIgnoreTraversal = true
		};
		var alternateRootPath = Path.Combine(
			Path.GetDirectoryName(repositoryRoot)!,
			"RE\u0301PO");
		var alternateFilePath = Path.Combine(
			alternateRootPath,
			"src",
			"cafe\u0301.cs");
		var context = rules
			.CreateGitIgnoreScanContext(repositoryRoot)
			.WithTrackedPathIndex(index);

		var evaluation = context.Evaluate(
			alternateFilePath,
			"src/cafe\u0301.cs",
			isDirectory: false,
			"cafe\u0301.cs");

		Assert.True(context.ContainsTrackedPathIndex(alternateRootPath));
		Assert.False(evaluation.IsIgnored);
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
	public void TrackedOnlyScanContext_HidesUntrackedEntriesButTraversesDirectoriesForNestedRepositories()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateFolder("repo");
		var trackedFile = temp.CreateFile("repo/src/tracked.cs", "tracked");
		var untrackedFile = temp.CreateFile("repo/src/local.cs", "untracked");
		var untrackedDirectory = temp.CreateFolder("repo/untracked/deep");
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			UseTrackedGitFilesOnly = true,
			EnableGitIgnoreTraversal = true
		};
		var context = rules
			.CreateGitIgnoreScanContext(repositoryRoot)
			.WithTrackedPathIndex(new GitTrackedPathIndex(repositoryRoot, ["src/tracked.cs"]));

		var tracked = context.Evaluate(trackedFile, "src/tracked.cs", isDirectory: false, "tracked.cs");
		var untracked = context.Evaluate(untrackedFile, "src/local.cs", isDirectory: false, "local.cs");
		var traversalOnly = context.Evaluate(
			untrackedDirectory,
			"untracked/deep",
			isDirectory: true,
			"deep");

		Assert.False(tracked.IsIgnored);
		Assert.True(untracked.IsIgnored);
		Assert.False(untracked.ShouldTraverseIgnoredDirectory);
		Assert.True(traversalOnly.IsIgnored);
		Assert.True(traversalOnly.ShouldTraverseIgnoredDirectory);
	}

	[Fact]
	public void TrackedOnlyScanContext_NeverExposesGitAdministrativeDirectory()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateFolder("repo");
		var gitDirectory = temp.CreateFolder("repo/.git");
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			UseTrackedGitFilesOnly = true,
			EnableGitIgnoreTraversal = true
		};
		var context = rules
			.CreateGitIgnoreScanContext(repositoryRoot)
			.WithTrackedPathIndex(new GitTrackedPathIndex(repositoryRoot, [".git/config"]));

		var evaluation = context.Evaluate(
			gitDirectory,
			".git",
			isDirectory: true,
			".git");

		Assert.True(evaluation.IsIgnored);
		Assert.False(evaluation.ShouldTraverseIgnoredDirectory);
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
