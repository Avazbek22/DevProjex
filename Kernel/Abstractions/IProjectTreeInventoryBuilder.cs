namespace DevProjex.Kernel.Abstractions;

public interface IProjectTreeInventoryBuilder
{
	ProjectTreeInventorySnapshot ReadInventory(
		string rootPath,
		TreeFilterOptions options,
		CancellationToken cancellationToken = default);

	TreeBuildResult Build(
		ProjectTreeInventorySnapshot inventory,
		TreeFilterOptions options,
		CancellationToken cancellationToken = default);
}
