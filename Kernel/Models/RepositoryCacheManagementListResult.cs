namespace DevProjex.Kernel.Models;

public sealed record RepositoryCacheManagementListResult(
	IReadOnlyList<RepositoryCacheCatalogEntry> Entries,
	int UnavailableRootCount)
{
	public bool IsComplete => UnavailableRootCount == 0;
}
