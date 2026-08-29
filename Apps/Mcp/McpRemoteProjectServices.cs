namespace DevProjex.Mcp;

internal sealed class McpRemoteProjectServices(
	IRepoCacheService repoCacheService,
	IGitRepositoryService gitRepositoryService) : IDisposable
{
	public IRepoCacheService RepoCacheService { get; } =
		repoCacheService ?? throw new ArgumentNullException(nameof(repoCacheService));
	public IGitRepositoryService GitRepositoryService { get; } =
		gitRepositoryService ?? throw new ArgumentNullException(nameof(gitRepositoryService));

	public static McpRemoteProjectServices Create(Func<string>? appDataPathProvider)
	{
		var cache = appDataPathProvider is null
			? new RepoCacheService()
			: new RepoCacheService(Path.Combine(appDataPathProvider(), "RepoCache"));
		return new McpRemoteProjectServices(cache, new GitRepositoryService());
	}

	public void Dispose() => RepoCacheService.Dispose();
}
