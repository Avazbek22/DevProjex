using DevProjex.Infrastructure.Git;
using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalServiceFactoryTests
{
	[Fact]
	public void DefaultServicesSeparateConfigurationStateAndCacheRoots()
	{
		using var services = new TerminalServiceFactory().Create(AppLanguage.En);

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

		using var services = new TerminalServiceFactory(() => dataRoot).Create(AppLanguage.En);

		var cache = Assert.IsType<RepoCacheService>(services.RepoCacheService);
		Assert.Equal(
			Path.Combine(dataRoot, "RepoCache"),
			cache.CacheRootPath,
			PathComparer.Default);
	}

	[Fact]
	public void DefaultScopeDisposesOwnedSessionResources()
	{
		using var workspace = new TemporaryDirectory();
		var scope = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.CreateScope(AppLanguage.En);
		var services = scope.Services;

		scope.Dispose();

		Assert.Throws<ObjectDisposedException>(services.SecretRedactionSession.Reset);
		Assert.Throws<ObjectDisposedException>(() =>
			services.CodeCompressionSession.BeginMeasurement(workspace.Path));
	}

	[Fact]
	public void InjectedScopeBorrowsSharedSessionResources()
	{
		using var workspace = new TemporaryDirectory();
		using var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		using (var scope = new TerminalServiceFactory(_ => services).CreateScope(AppLanguage.En))
		{
			Assert.Same(services, scope.Services);
		}

		services.SecretRedactionSession.Reset();
		using var measurement = services.CodeCompressionSession.BeginMeasurement(workspace.Path);
	}
}
