namespace DevProjex.Tests.Unit;

public sealed class ProjectRootFactsProviderTests
{
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
