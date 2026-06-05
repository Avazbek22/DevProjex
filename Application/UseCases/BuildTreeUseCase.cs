namespace DevProjex.Application.UseCases;

public sealed class BuildTreeUseCase(ITreeBuilder treeBuilder, TreeNodePresentationService presenter)
{
	public BuildTreeResult Execute(BuildTreeRequest request, CancellationToken cancellationToken = default)
	{
		var result = treeBuilder.Build(request.RootPath, request.Filter, cancellationToken);
		var root = presenter.Build(result.Root);

		return new BuildTreeResult(root, result.RootAccessDenied, result.HadAccessDenied);
	}

	public BuildTreeSnapshotResult ExecuteWithInventory(
		BuildTreeRequest request,
		CancellationToken cancellationToken = default)
	{
		if (treeBuilder is not IProjectTreeInventoryBuilder inventoryBuilder)
			return new BuildTreeSnapshotResult(Execute(request, cancellationToken), Inventory: null);

		// Project-load can now keep the filesystem inventory that produced the tree.
		// Future ignore projections can consume the same snapshot instead of starting
		// from MainWindow or re-enumerating the project as a black-box tree build.
		var inventory = inventoryBuilder.ReadInventory(
			request.RootPath,
			request.Filter,
			cancellationToken);
		var result = inventoryBuilder.Build(
			inventory,
			request.Filter,
			cancellationToken);
		var root = presenter.Build(result.Root);
		return new BuildTreeSnapshotResult(
			new BuildTreeResult(root, result.RootAccessDenied, result.HadAccessDenied),
			inventory);
	}
}
