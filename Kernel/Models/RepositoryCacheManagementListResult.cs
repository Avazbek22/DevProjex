namespace DevProjex.Kernel.Models;

public sealed record RepositoryCacheManagementListResult(
	IReadOnlyList<RepositoryCacheCatalogEntry> Entries,
	int UnavailableRootCount,
	int BusyRootCount = 0)
{
	public bool IsComplete => UnavailableRootCount == 0;
	public int NonBusyUnavailableRootCount => Math.Max(0, UnavailableRootCount - BusyRootCount);
}
