namespace DevProjex.Infrastructure.Git;

internal sealed class RepositoryCacheSession : IRepositoryCacheSession
{
	private RepositoryFileLease? _lease;

	public RepositoryCacheSession(
		string repositoryPath,
		string repositoryUrl,
		string? branch,
		RepositoryCacheContentKind contentKind,
		RepositoryFileLease lease)
	{
		RepositoryPath = repositoryPath;
		RepositoryUrl = repositoryUrl;
		Branch = branch;
		ContentKind = contentKind;
		_lease = lease;
	}

	public string RepositoryPath { get; }
	public string RepositoryUrl { get; }
	public string? Branch { get; }
	public RepositoryCacheContentKind ContentKind { get; }

	public void Dispose()
	{
		Interlocked.Exchange(ref _lease, null)?.Dispose();
	}
}
