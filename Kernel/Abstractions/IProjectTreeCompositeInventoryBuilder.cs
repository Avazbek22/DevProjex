namespace DevProjex.Kernel.Abstractions;

public interface IProjectTreeCompositeInventoryBuilder
{
	ProjectTreeInventorySnapshot ReadCompositeInventory(
		string rootPath,
		IReadOnlySet<string> allowedRootFolders,
		IgnoreRules discoveryRules,
		IgnoreRules projectionRules,
		CancellationToken cancellationToken = default);
}
