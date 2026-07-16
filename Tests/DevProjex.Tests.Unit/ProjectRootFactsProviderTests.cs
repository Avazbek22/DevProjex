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
		var provider = new ProjectRootFactsProvider(cacheTtl: TimeSpan.FromMinutes(5));

		_ = provider.Get(workspace.Path);
		_ = provider.Get(nestedPath);
		_ = provider.Get(unrelated.Path);
		workspace.CreateFile("pyproject.toml", "[project]");
		workspace.CreateFile("apps/api/pyproject.toml", "[project]");
		unrelated.CreateFile("pyproject.toml", "[project]");

		provider.Invalidate(workspace.Path, includeDescendants: true);

		Assert.True(provider.Get(workspace.Path).HasMarkerFile("pyproject.toml"));
		Assert.True(provider.Get(nestedPath).HasMarkerFile("pyproject.toml"));
		Assert.False(provider.Get(unrelated.Path).HasMarkerFile("pyproject.toml"));
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
}
