using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit;

public sealed class GitTrackedPathIndexTests
{
	[Fact]
	public void GitDirectoryPointer_RejectsActualContentBeyondMaximumAfterLengthProbe()
	{
		var bytes = new byte[GitTrackedPathIndexCache.GitFileMaximumLength + 1];
		Encoding.UTF8.GetBytes("gitdir: metadata", bytes);
		using var stream = new StaleLengthMemoryStream(bytes, reportedLength: 16);

		var result = GitTrackedPathIndexCache.TryReadGitDirectoryPointer(stream, out var target);

		Assert.False(result);
		Assert.Empty(target);
		Assert.Equal(GitTrackedPathIndexCache.GitFileMaximumLength + 1, stream.Position);
	}

	[Fact]
	public async Task NullDelimitedReader_WhenRetainedBudgetIsExceeded_AbortsBeforeMaterializingRemainder()
	{
		var payload = Encoding.UTF8.GetBytes("first.cs\0second-long-path.cs\0third.cs\0");
		await using var stream = new MemoryStream(payload);
		using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false);
		var abortCount = 0;

		var paths = await GitTrackedPathIndexCache.ReadNullDelimitedPathsAsync(
			reader,
			TestContext.Current.CancellationToken,
			() => abortCount++,
			maximumRetainedBytes: 64 + IntPtr.Size + 32 + ("first.cs".Length * sizeof(char)),
			maximumPathLength: 32768);

		Assert.Null(paths);
		Assert.Equal(1, abortCount);
	}

	[Fact]
	public async Task NullDelimitedReader_WhenWithinBudget_ReturnsEveryPathWithoutAborting()
	{
		var payload = Encoding.UTF8.GetBytes("src/app.cs\0docs/readme.md\0");
		await using var stream = new MemoryStream(payload);
		using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false);
		var abortCount = 0;

		var paths = await GitTrackedPathIndexCache.ReadNullDelimitedPathsAsync(
			reader,
			TestContext.Current.CancellationToken,
			() => abortCount++);

		Assert.Equal(["src/app.cs", "docs/readme.md"], paths);
		Assert.Equal(0, abortCount);
	}

	[Fact]
	public async Task NullDelimitedReader_WhenSinglePathExceedsLimit_AbortsWithoutReturningPartialIndex()
	{
		var payload = Encoding.UTF8.GetBytes("path-that-is-too-long\0");
		await using var stream = new MemoryStream(payload);
		using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false);
		var processKilled = false;

		var paths = await GitTrackedPathIndexCache.ReadNullDelimitedPathsAsync(
			reader,
			TestContext.Current.CancellationToken,
			() => processKilled = true,
			maximumPathLength: 8);

		Assert.Null(paths);
		Assert.True(processKilled);
	}

	[Theory]
	[InlineData(64, true)]
	[InlineData(16 * 1024 * 1024, true)]
	[InlineData(16 * 1024 * 1024 + 1, false)]
	[InlineData(64 * 1024 * 1024, false)]
	public void CacheRetentionPolicy_EnforcesTheGlobalRetainedByteBudget(
		long estimatedRetainedBytes,
		bool expected)
	{
		Assert.Equal(
			expected,
			GitTrackedPathIndexCache.CanRetainCacheEntry(estimatedRetainedBytes));
	}

	[Theory]
	[InlineData("src/App.cs", true, false, true)]
	[InlineData("src", false, true, true)]
	[InlineData("src/Missing.cs", false, false, false)]
	public void NormalizedRelativeProbes_MatchPublicFullPathContract(
		string relativePath,
		bool expectedContains,
		bool expectedDescendant,
		bool expectedContainsOrDescendant)
	{
		using var temp = new TemporaryDirectory();
		var index = new GitTrackedPathIndex(
			temp.Path,
			["src/App.cs", "docs/readme.md"],
			new GitPathComparisonSemantics(IgnoreCase: false, NormalizeUnicode: false));
		var fullPath = Path.Combine(
			temp.Path,
			relativePath.Replace('/', Path.DirectorySeparatorChar));

		Assert.True(index.TryGetNormalizedRelativePath(fullPath, out var normalizedRelativePath));
		Assert.Equal(expectedContains, index.Contains(fullPath));
		Assert.Equal(expectedContains, index.ContainsNormalizedRelativePath(normalizedRelativePath));
		Assert.Equal(expectedDescendant, index.HasDescendant(fullPath));
		Assert.Equal(expectedDescendant, index.HasDescendantNormalizedRelativePath(normalizedRelativePath));
		Assert.Equal(expectedContainsOrDescendant, index.ContainsOrHasDescendant(fullPath));
		Assert.Equal(
			expectedContainsOrDescendant,
			index.ContainsOrHasDescendantNormalizedRelativePath(normalizedRelativePath));
	}

	[Fact]
	public void ExplicitAndPlatformComparisonSemanticsAreAuthoritativeByDefault()
	{
		var explicitSemantics = new GitPathComparisonSemantics(
			IgnoreCase: false,
			NormalizeUnicode: false);
		var uncertainSemantics = explicitSemantics with { IsAuthoritative = false };

		Assert.True(explicitSemantics.IsAuthoritative);
		Assert.True(GitPathComparisonSemantics.PlatformDefault.IsAuthoritative);
		Assert.False(uncertainSemantics.IsAuthoritative);
	}

	[Fact]
	public void TrackedIndexCommandsDoNotAcquireTheParentTerminal()
	{
		var startInfo = GitTrackedPathIndexCache.CreateStartInfo(
			Path.GetTempPath());

		Assert.False(startInfo.UseShellExecute);
		Assert.True(startInfo.RedirectStandardInput);
		Assert.True(startInfo.RedirectStandardOutput);
		Assert.True(startInfo.RedirectStandardError);
		Assert.Equal("0", startInfo.Environment["GIT_TERMINAL_PROMPT"]);
	}

	[Theory]
	[InlineData(2, true)]
	[InlineData(5, false)]
	[InlineData(8, false)]
	[InlineData(11, false)]
	public void GitStartFailureCaching_DistinguishesMissingExecutableFromTransientFailures(
		int nativeErrorCode,
		bool expectedPermanent)
	{
		var exception = new System.ComponentModel.Win32Exception(nativeErrorCode);

		Assert.Equal(
			expectedPermanent,
			GitTrackedPathIndexCache.IsPermanentGitStartFailure(exception));
	}

	[Theory]
	[InlineData(3)]
	[InlineData(193)]
	[InlineData(216)]
	public void WindowsSpecificGitStartFailures_ArePermanentOnlyOnWindows(int nativeErrorCode)
	{
		var exception = new System.ComponentModel.Win32Exception(nativeErrorCode);

		Assert.Equal(
			OperatingSystem.IsWindows(),
			GitTrackedPathIndexCache.IsPermanentGitStartFailure(exception));
	}

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
	public void ExplicitCaseInsensitiveSemantics_UseGitAsciiFoldWithoutMergingUnicodeNames()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateFolder("repo");
		var index = new GitTrackedPathIndex(
			repositoryRoot,
			["ASCII/FILE.cs", "Ä.cs"],
			new GitPathComparisonSemantics(
				IgnoreCase: true,
				NormalizeUnicode: false));

		Assert.True(index.Contains(Path.Combine(repositoryRoot, "ascii", "file.CS")));
		Assert.False(index.Contains(Path.Combine(repositoryRoot, "ä.cs")));
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
			"Re\u0301PO");
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

	private sealed class StaleLengthMemoryStream(byte[] buffer, long reportedLength) :
		MemoryStream(buffer, writable: false)
	{
		public override long Length => reportedLength;
	}
}
