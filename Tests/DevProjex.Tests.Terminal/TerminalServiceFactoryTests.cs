using DevProjex.Infrastructure.Git;
using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalServiceFactoryTests
{
	[Fact]
	public void DefaultServicesSeparateConfigurationStateAndCacheRoots()
	{
		var services = new TerminalServiceFactory().Create(AppLanguage.En);

		Assert.Equal(
			Path.Combine(
				UserDataPathResolver.GetStateRoot(),
				"DevProjex",
				"recent-projects.json"),
			services.RecentProjectsStore.GetPath(),
			PathComparer.Default);
		Assert.Equal(
			Path.Combine(
				UserDataPathResolver.GetCacheRoot(),
				"DevProjex",
				"RepoCache"),
			services.RepoCacheService.CacheRootPath,
			PathComparer.Default);
	}

	[Fact]
	public void CustomDataRootAlsoScopesRepositoryCache()
	{
		using var workspace = new TemporaryDirectory();
		var dataRoot = workspace.CreateDirectory("app-data");

		var services = new TerminalServiceFactory(() => dataRoot).Create(AppLanguage.En);

		var cache = Assert.IsType<RepoCacheService>(services.RepoCacheService);
		Assert.Equal(
			Path.Combine(dataRoot, "RepoCache"),
			cache.CacheRootPath,
			PathComparer.Default);
	}
}
