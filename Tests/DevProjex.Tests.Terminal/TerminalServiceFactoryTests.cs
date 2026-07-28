using DevProjex.Infrastructure.Git;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalServiceFactoryTests
{
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
