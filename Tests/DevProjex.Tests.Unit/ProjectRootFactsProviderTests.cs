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
