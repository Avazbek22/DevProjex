namespace DevProjex.Tests.Unit;

public sealed class ProjectRootFactsProviderTests
{
	[Fact]
	public void ProjectRootFacts_OrdinaryDirectoryLookupPreservesWhitespaceOnlyNames()
	{
		var directory = new ProjectRootDirectoryFact(" ", "root/ ", IsReparsePoint: false);
		var facts = new ProjectRootFacts("root", true, true, [], [directory], null);

		Assert.True(facts.HasDirectory(" "));
		Assert.True(facts.TryGetDirectory(" ", out var resolved));
		Assert.Equal(directory, resolved);
	}

	[Theory]
	[InlineData(15)]
	[InlineData(16)]
	[InlineData(17)]
	[InlineData(31)]
	[InlineData(32)]
	[InlineData(33)]
	[InlineData(63)]
	[InlineData(64)]
	[InlineData(65)]
	[InlineData(127)]
	[InlineData(128)]
	[InlineData(129)]
	public void ProjectRootFacts_AdaptiveLookupBoundary_PreservesEveryQueryContract(int entryCount)
	{
		var files = Enumerable.Range(0, entryCount)
			.Select(index => index switch
			{
				0 => new ProjectRootFileFact(".git", string.Empty),
				1 => new ProjectRootFileFact(".gitignore", string.Empty),
				_ => new ProjectRootFileFact($"marker-{index:D2}.Json", ".Json")
			})
			.ToArray();
		var directories = Enumerable.Range(0, entryCount)
			.Select(index => index == entryCount - 1
				? new ProjectRootDirectoryFact("linked", "root/linked", IsReparsePoint: true)
				: new ProjectRootDirectoryFact(
					$"folder-{index:D2}",
					Path.Combine("root", $"folder-{index:D2}"),
					IsReparsePoint: false))
			.ToArray();
		var facts = new ProjectRootFacts("root", true, true, files, directories, null);

		Assert.True(facts.HasFile("marker-02.Json"));
		Assert.True(facts.HasMarkerFile("MARKER-02.JSON"));
		Assert.True(facts.HasAnyMarkerFile(["missing", "MARKER-03.JSON"]));
		Assert.True(facts.HasAnyFileExtension([".missing", ".JSON"]));
		Assert.True(facts.HasDirectory("folder-00"));
		Assert.True(facts.TryGetDirectory("folder-01", out var directory));
		Assert.Equal("folder-01", directory.Name);
		Assert.True(facts.HasAnyDirectoryName(["FOLDER-02"]));
		Assert.False(facts.HasAnyDirectoryName(["LINKED"]));
		Assert.True(facts.HasAnyDirectoryName(["LINKED"], includeReparsePoints: true));
		Assert.True(facts.HasGitMetadataEntry);
		Assert.True(facts.HasGitIgnoreFile);
	}

	[Fact]
	public void Get_CapturesTopLevelFilesDirectoriesAndGitIgnoreSignature()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "bin/\n");
		temp.CreateFile("package.json", "{}");
		temp.CreateFile("src/App.cs", "class App {}");
		temp.CreateFolder("node_modules");

		var provider = new ProjectRootFactsProvider();
		var facts = provider.Get(temp.Path);

		Assert.True(facts.Exists);
		Assert.True(facts.IsAccessible);
		Assert.True(facts.HasGitIgnoreFile);
		Assert.NotNull(facts.GitIgnoreSignature);
		Assert.True(facts.HasMarkerFile("package.json"));
		Assert.True(facts.HasDirectory("src"));
		Assert.True(facts.HasAnyDirectoryName(["NODE_MODULES"]));
		Assert.False(facts.HasMarkerFile("src/App.cs"));
	}

	#pragma warning disable xUnit1051 // This test verifies a caller-owned cancellation token.
	[Fact]
	public void GetWithCancellation_PreCancelledRequestDoesNotBuildOrPopulateCache()
	{
		var rootPath = Path.GetFullPath(Path.Combine(
			Path.GetTempPath(),
			"DevProjex",
			"RootFactsCancellation",
			Guid.NewGuid().ToString("N")));
		var buildCount = 0;
		var provider = new ProjectRootFactsProvider(
			cacheTtl: TimeSpan.FromMinutes(5),
			cacheLimit: 4,
			utcNowProvider: null,
			factsBuilder: path =>
			{
				buildCount++;
				return CreateFacts(path, "package.json");
			});
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		Assert.ThrowsAny<OperationCanceledException>(() =>
			provider.GetWithCancellation(rootPath, forceRefresh: false, cancellation.Token));
		Assert.Equal(0, buildCount);

		var facts = provider.Get(rootPath);
		Assert.True(facts.HasMarkerFile("package.json"));
		Assert.Equal(1, buildCount);
	}
	#pragma warning restore xUnit1051

	[Fact]
	public void Get_ReusesCachedSnapshotWithinTtl_AndForceRefreshUpdates()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("package.json", "{}");

		var provider = new ProjectRootFactsProvider(cacheTtl: TimeSpan.FromMinutes(5));
		var initial = provider.Get(temp.Path);

		temp.CreateFile("pyproject.toml", "[project]\nname = \"api\"\n");

		var cached = provider.Get(temp.Path);
		var refreshed = provider.Get(temp.Path, forceRefresh: true);

		Assert.True(initial.HasMarkerFile("package.json"));
		Assert.False(cached.HasMarkerFile("pyproject.toml"));
		Assert.True(refreshed.HasMarkerFile("pyproject.toml"));
	}

	[Fact]
	public void Get_TrailingSeparatorAliasSharesCacheEntry()
	{
		var rootPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
		var buildCount = 0;
		var provider = new ProjectRootFactsProvider(
			cacheTtl: TimeSpan.FromMinutes(5),
			cacheLimit: 4,
			utcNowProvider: null,
			factsBuilder: path => CreateFacts(path, $"build-{++buildCount}.marker"));

		var first = provider.Get(rootPath);
		var alias = provider.Get(rootPath + Path.DirectorySeparatorChar);

		Assert.Same(first, alias);
		Assert.Equal(1, buildCount);
	}

	[Fact]
	public void Get_WhenClockMovesBackward_DoesNotExtendCacheLifetime()
	{
		var rootPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
		var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
		var buildCount = 0;
		var provider = new ProjectRootFactsProvider(
			cacheTtl: TimeSpan.FromMinutes(5),
			cacheLimit: 4,
			utcNowProvider: () => now,
			factsBuilder: path => CreateFacts(path, $"build-{++buildCount}.marker"));

		var first = provider.Get(rootPath);
		now = now.AddMinutes(-10);
		var afterClockRollback = provider.Get(rootPath);

		Assert.NotSame(first, afterClockRollback);
		Assert.Equal(2, buildCount);
	}

	[Fact]
	public void TryGetFileSignature_SameLengthAndTimestampButDifferentContent_ChangesFingerprint()
	{
		using var temp = new TemporaryDirectory();
		var gitIgnorePath = temp.CreateFile(".gitignore", "old/\n");
		var originalTimestamp = File.GetLastWriteTimeUtc(gitIgnorePath);
		var originalSignature = ProjectRootFactsProvider.TryGetFileSignature(gitIgnorePath);

		File.WriteAllText(gitIgnorePath, "new/\n");
		File.SetLastWriteTimeUtc(gitIgnorePath, originalTimestamp);
		var rewrittenSignature = ProjectRootFactsProvider.TryGetFileSignature(gitIgnorePath);

		Assert.NotNull(originalSignature);
		Assert.NotNull(rewrittenSignature);
		Assert.Equal(originalSignature.Value.LastWriteTicksUtc, rewrittenSignature.Value.LastWriteTicksUtc);
		Assert.Equal(originalSignature.Value.LengthBytes, rewrittenSignature.Value.LengthBytes);
		Assert.NotEqual(originalSignature.Value.ContentFingerprint, rewrittenSignature.Value.ContentFingerprint);
	}

	[Fact]
	public void TryGetFileSignature_RejectsOversizedGitIgnoreBeforeHashingContent()
	{
		using var temp = new TemporaryDirectory();
		var gitIgnorePath = Path.Combine(temp.Path, ".gitignore");
		using (var stream = new FileStream(
		       gitIgnorePath,
		       FileMode.CreateNew,
		       FileAccess.Write,
		       FileShare.None))
		{
			stream.SetLength(GitIgnoreFileReader.MaximumFileSizeBytes + 1);
		}

		var signature = ProjectRootFactsProvider.TryGetFileSignature(gitIgnorePath);

		Assert.Null(signature);
	}

	[Fact]
	public void ContentFingerprint_RejectsFileThatGrewAfterMetadataProbe()
	{
		using var stream = new StaleLengthMemoryStream(
			Encoding.UTF8.GetBytes("root-length-overflow"),
			reportedLength: 4);

		var exception = Assert.Throws<IOException>(() =>
			ProjectRootFactsProvider.ComputeContentFingerprint(stream, expectedLength: 4));

		Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(5, stream.Position);
	}

	[Fact]
	public void HasMatchingFileMetadata_ReportsContentRewriteOnlyWhenMetadataChanges()
	{
		using var temp = new TemporaryDirectory();
		var gitIgnorePath = temp.CreateFile(".gitignore", "old/\n");
		var originalSignature = Assert.IsType<ProjectRootFileSignature>(
			ProjectRootFactsProvider.TryGetFileSignature(gitIgnorePath));

		Assert.True(ProjectRootFactsProvider.HasMatchingFileMetadata(gitIgnorePath, originalSignature));

		File.WriteAllText(gitIgnorePath, "expanded-rule/\n");

		Assert.False(ProjectRootFactsProvider.HasMatchingFileMetadata(gitIgnorePath, originalSignature));
	}

	[Fact]
	public void Invalidate_WithDescendants_RemovesRootAndNestedSnapshotsOnly()
	{
		using var workspace = new TemporaryDirectory();
		using var unrelated = new TemporaryDirectory();
		workspace.CreateFile("package.json", "{}");
		workspace.CreateFile("apps/api/package.json", "{}");
		unrelated.CreateFile("package.json", "{}");
		var nestedPath = Path.Combine(workspace.Path, "apps", "api");
		var provider = new ProjectRootFactsProvider(
			cacheTtl: TimeSpan.FromMinutes(5),
			cacheLimit: 3);

		_ = provider.Get(workspace.Path);
		_ = provider.Get(nestedPath);
		var unrelatedBeforeInvalidation = provider.Get(unrelated.Path);
		workspace.CreateFile("pyproject.toml", "[project]");
		workspace.CreateFile("apps/api/pyproject.toml", "[project]");
		unrelated.CreateFile("pyproject.toml", "[project]");

		provider.Invalidate(workspace.Path, includeDescendants: true);

		Assert.True(provider.Get(workspace.Path).HasMarkerFile("pyproject.toml"));
		Assert.True(provider.Get(nestedPath).HasMarkerFile("pyproject.toml"));
		var unrelatedAfterInvalidation = provider.Get(unrelated.Path);
		Assert.Same(unrelatedBeforeInvalidation, unrelatedAfterInvalidation);
		Assert.False(unrelatedAfterInvalidation.HasMarkerFile("pyproject.toml"));
	}

	[Fact]
	public void Invalidate_FromFileSystemRoot_RemovesDescendantSnapshot()
	{
		var fileSystemRoot = Path.GetPathRoot(Path.GetTempPath())!;
		var descendant = Path.Combine(fileSystemRoot, "DevProjex", "Tests", Guid.NewGuid().ToString("N"));
		var buildCount = 0;
		var provider = new ProjectRootFactsProvider(
			cacheTtl: TimeSpan.FromMinutes(5),
			cacheLimit: 4,
			utcNowProvider: null,
			factsBuilder: path => CreateFacts(path, $"build-{++buildCount}.marker"));

		var initial = provider.Get(descendant);
		provider.Invalidate(fileSystemRoot, includeDescendants: true);
		var refreshed = provider.Get(descendant);

		Assert.NotSame(initial, refreshed);
		Assert.Equal(2, buildCount);
	}

	[Fact]
	public void Get_CacheLimitEvictsOldSnapshots()
	{
		using var first = new TemporaryDirectory();
		using var second = new TemporaryDirectory();
		first.CreateFile("package.json", "{}");
		second.CreateFile("go.mod", "module sample\n");

		var provider = new ProjectRootFactsProvider(cacheTtl: TimeSpan.FromMinutes(5), cacheLimit: 1);
		_ = provider.Get(first.Path);
		first.CreateFile("pyproject.toml", "[project]\nname = \"api\"\n");
		_ = provider.Get(second.Path);

		var firstAfterEviction = provider.Get(first.Path);

		Assert.True(firstAfterEviction.HasMarkerFile("pyproject.toml"));
	}

	[Fact]
	public void Get_CacheLimitEvictsLeastRecentlyUsedSnapshotAndRetainsMostRecentlyUsedSnapshot()
	{
		using var first = new TemporaryDirectory();
		using var second = new TemporaryDirectory();
		using var third = new TemporaryDirectory();
		first.CreateFile("first.marker", "first");
		second.CreateFile("second.marker", "second");
		third.CreateFile("third.marker", "third");
		var provider = new ProjectRootFactsProvider(
			cacheTtl: TimeSpan.FromMinutes(5),
			cacheLimit: 2);

		_ = provider.Get(first.Path);
		_ = provider.Get(second.Path);
		var promotedFirst = provider.Get(first.Path);
		first.CreateFile("first.updated", "updated");
		second.CreateFile("second.updated", "updated");

		_ = provider.Get(third.Path);

		var retainedFirst = provider.Get(first.Path);
		var rebuiltSecond = provider.Get(second.Path);
		Assert.Same(promotedFirst, retainedFirst);
		Assert.False(retainedFirst.HasMarkerFile("first.updated"));
		Assert.True(rebuiltSecond.HasMarkerFile("second.updated"));
	}

	[Fact]
	public async Task Invalidate_DuringBuild_DoesNotCacheTheLateSnapshot()
	{
		var rootPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
		using var firstBuildStarted = new ManualResetEventSlim();
		using var releaseFirstBuild = new ManualResetEventSlim();
		var buildCount = 0;
		var provider = new ProjectRootFactsProvider(
			cacheTtl: TimeSpan.FromMinutes(5),
			cacheLimit: 4,
			utcNowProvider: null,
			factsBuilder: path =>
			{
				var build = Interlocked.Increment(ref buildCount);
				if (build == 1)
				{
					firstBuildStarted.Set();
					Assert.True(releaseFirstBuild.Wait(TimeSpan.FromSeconds(5)));
				}

				return CreateFacts(path, build == 1 ? "old.marker" : "new.marker");
			});

		var lateBuild = Task.Run(() => provider.Get(rootPath, forceRefresh: true));
		Assert.True(firstBuildStarted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
		provider.Invalidate(rootPath);
		releaseFirstBuild.Set();
		Assert.True((await lateBuild).HasFile("old.marker"));

		var refreshed = provider.Get(rootPath);
		var cached = provider.Get(rootPath);
		Assert.True(refreshed.HasFile("new.marker"));
		Assert.Same(refreshed, cached);
		Assert.Equal(2, buildCount);
	}

	[Fact]
	public async Task ConcurrentForceRefresh_NewerCompletionCannotBeOverwrittenByOlderBuild()
	{
		var rootPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
		using var olderBuildStarted = new ManualResetEventSlim();
		using var releaseOlderBuild = new ManualResetEventSlim();
		var buildCount = 0;
		var provider = new ProjectRootFactsProvider(
			cacheTtl: TimeSpan.FromMinutes(5),
			cacheLimit: 4,
			utcNowProvider: null,
			factsBuilder: path =>
			{
				var build = Interlocked.Increment(ref buildCount);
				if (build == 1)
				{
					olderBuildStarted.Set();
					Assert.True(releaseOlderBuild.Wait(TimeSpan.FromSeconds(5)));
				}

				return CreateFacts(path, build == 1 ? "older.marker" : "newer.marker");
			});

		var olderBuild = Task.Run(() => provider.Get(rootPath, forceRefresh: true));
		Assert.True(olderBuildStarted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
		var newerFacts = await Task.Run(() => provider.Get(rootPath, forceRefresh: true));
		releaseOlderBuild.Set();
		var olderFacts = await olderBuild;

		Assert.True(olderFacts.HasFile("older.marker"));
		Assert.True(newerFacts.HasFile("newer.marker"));
		Assert.Same(newerFacts, provider.Get(rootPath));
		Assert.Equal(2, buildCount);
	}

	[Fact]
	public async Task DescendantInvalidation_DuringChildBuild_PreventsLateCacheCommit()
	{
		var parentPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
		var childPath = Path.Combine(parentPath, "child");
		using var firstBuildStarted = new ManualResetEventSlim();
		using var releaseFirstBuild = new ManualResetEventSlim();
		var buildCount = 0;
		var provider = new ProjectRootFactsProvider(
			cacheTtl: TimeSpan.FromMinutes(5),
			cacheLimit: 4,
			utcNowProvider: null,
			factsBuilder: path =>
			{
				var build = Interlocked.Increment(ref buildCount);
				if (build == 1)
				{
					firstBuildStarted.Set();
					Assert.True(releaseFirstBuild.Wait(TimeSpan.FromSeconds(5)));
				}

				return CreateFacts(path, build == 1 ? "stale.marker" : "fresh.marker");
			});

		var lateChildBuild = Task.Run(() => provider.Get(childPath, forceRefresh: true));
		Assert.True(firstBuildStarted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
		provider.Invalidate(parentPath, includeDescendants: true);
		releaseFirstBuild.Set();
		_ = await lateChildBuild;

		Assert.True(provider.Get(childPath).HasFile("fresh.marker"));
		Assert.Equal(2, buildCount);
	}

	[Fact]
	public void Get_MissingRoot_ReturnsSafeEmptyFacts()
	{
		var missingPath = Path.Combine(Path.GetTempPath(), "DevProjex", "Tests", "Missing", Guid.NewGuid().ToString("N"));
		var provider = new ProjectRootFactsProvider();

		var facts = provider.Get(missingPath);

		Assert.False(facts.Exists);
		Assert.False(facts.IsAccessible);
		Assert.Empty(facts.Files);
		Assert.Empty(facts.Directories);
		Assert.False(facts.HasGitIgnoreFile);
		Assert.Null(facts.GitIgnoreSignature);
	}

	[Fact]
	public void HasGitMetadataEntry_RejectsReparseFilesAndDirectories()
	{
		var fileLinkFacts = new ProjectRootFacts(
			rootPath: "project",
			exists: true,
			isAccessible: true,
			files: [new ProjectRootFileFact(".git", string.Empty, IsReparsePoint: true)],
			directories: [],
			gitIgnoreSignature: null);
		var directoryLinkFacts = new ProjectRootFacts(
			rootPath: "project",
			exists: true,
			isAccessible: true,
			files: [],
			directories: [new ProjectRootDirectoryFact(".git", "project/.git", IsReparsePoint: true)],
			gitIgnoreSignature: null);

		Assert.False(fileLinkFacts.HasGitMetadataEntry);
		Assert.False(directoryLinkFacts.HasGitMetadataEntry);
	}

	[Fact]
	public void HasGitMetadataEntry_AcceptsDirectoryAndWorktreeFile()
	{
		var fileFacts = new ProjectRootFacts(
			rootPath: "project",
			exists: true,
			isAccessible: true,
			files: [new ProjectRootFileFact(".git", string.Empty)],
			directories: [],
			gitIgnoreSignature: null);
		var directoryFacts = new ProjectRootFacts(
			rootPath: "project",
			exists: true,
			isAccessible: true,
			files: [],
			directories: [new ProjectRootDirectoryFact(".git", "project/.git", IsReparsePoint: false)],
			gitIgnoreSignature: null);

		Assert.True(fileFacts.HasGitMetadataEntry);
		Assert.True(directoryFacts.HasGitMetadataEntry);
	}

	private static ProjectRootFacts CreateFacts(string rootPath, string markerName) =>
		new(
			rootPath,
			exists: true,
			isAccessible: true,
			files: [new ProjectRootFileFact(markerName, Path.GetExtension(markerName))],
			directories: [],
			gitIgnoreSignature: null);

	private sealed class StaleLengthMemoryStream(byte[] buffer, long reportedLength) :
		MemoryStream(buffer, writable: false)
	{
		public override long Length => reportedLength;
	}

	[Fact]
	public void HasGitIgnoreFile_RejectsReparseFileAndAcceptsRegularFile()
	{
		var linkFacts = new ProjectRootFacts(
			rootPath: "project",
			exists: true,
			isAccessible: true,
			files: [new ProjectRootFileFact(".gitignore", string.Empty, IsReparsePoint: true)],
			directories: [],
			gitIgnoreSignature: null);
		var regularFacts = new ProjectRootFacts(
			rootPath: "project",
			exists: true,
			isAccessible: true,
			files: [new ProjectRootFileFact(".gitignore", string.Empty)],
			directories: [],
			gitIgnoreSignature: default);

		Assert.False(linkFacts.HasGitIgnoreFile);
		Assert.True(regularFacts.HasGitIgnoreFile);
	}

	[Fact]
	public void Get_SymlinkedGitIgnoreIsNotWorkingTreeRuleEvidence()
	{
		using var temp = new TemporaryDirectory();
		var targetPath = temp.CreateFile("ignore-rules.txt", "*.secret\n");
		var linkPath = Path.Combine(temp.Path, ".gitignore");
		try
		{
			File.CreateSymbolicLink(linkPath, targetPath);
			if (!File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint))
				Assert.Skip("The created file link is not reported as a reparse point.");
		}
		catch (Exception exception) when (exception is
		       IOException or
		       UnauthorizedAccessException or
		       PlatformNotSupportedException)
		{
			Assert.Skip($"File symbolic links are unavailable: {exception.GetType().Name}.");
		}

		var facts = new ProjectRootFactsProvider().Get(temp.Path);

		Assert.False(facts.HasGitIgnoreFile);
		Assert.Null(facts.GitIgnoreSignature);
		Assert.Null(ProjectRootFactsProvider.TryGetFileSignature(linkPath));
	}
}
