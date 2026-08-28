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

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void CacheScopeMatchesFullServiceCacheRoot(bool useCustomDataRoot)
	{
		using var workspace = new TemporaryDirectory();
		var dataRoot = workspace.CreateDirectory("app-data");
		Func<string>? dataRootProvider = useCustomDataRoot ? () => dataRoot : null;
		var factory = new TerminalServiceFactory(dataRootProvider);

		using var fullServices = factory.Create(AppLanguage.En);
		using var cacheScope = factory.CreateCacheScope(AppLanguage.En);

		Assert.Equal(
			fullServices.RepoCacheService.CacheRootPath,
			cacheScope.Services.RepoCacheService.CacheRootPath,
			PathComparer.Default);
	}

	[Fact]
	public async Task CachePathUsesNarrowScopeWithoutCreatingFullServices()
	{
		using var workspace = new TemporaryDirectory();
		var dataRoot = workspace.CreateDirectory("app-data");
		var fullServiceCreations = 0;
		var factory = new TerminalServiceFactory(
			() => dataRoot,
			() => fullServiceCreations++);
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment, factory)
			.RunAsync(
				["cache", "path", "--language", "en"],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(0, fullServiceCreations);
		Assert.Equal(
			Path.Combine(dataRoot, "RepoCache") + Environment.NewLine,
			environment.StandardOutput);
		Assert.Empty(environment.StandardError);

		using var fullServices = factory.Create(AppLanguage.En);
		Assert.Equal(1, fullServiceCreations);
	}

	[Fact]
	public void InjectedCacheScopeFallsBackToBorrowedFullServices()
	{
		using var workspace = new TemporaryDirectory();
		using var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var providerCalls = 0;
		var factory = new TerminalServiceFactory(_ =>
		{
			providerCalls++;
			return services;
		});

		using (var scope = factory.CreateCacheScope(AppLanguage.En))
		{
			Assert.Same(services.Localization, scope.Services.Localization);
			Assert.Same(services.RepoCacheService, scope.Services.RepoCacheService);
		}

		Assert.Equal(1, providerCalls);
		services.SecretRedactionSession.Reset();
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
