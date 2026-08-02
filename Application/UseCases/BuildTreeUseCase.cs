namespace DevProjex.Application.UseCases;

public sealed class BuildTreeUseCase(ITreeBuilder treeBuilder, TreeNodePresentationService presenter)
{
	public bool SupportsCompositeInventory => treeBuilder is IProjectTreeCompositeInventoryBuilder;

	public ProjectTreeInventorySnapshot ReadCompositeInventory(
		string rootPath,
		IReadOnlySet<string> allowedRootFolders,
		IgnoreRules discoveryRules,
		IgnoreRules projectionRules,
		CancellationToken cancellationToken = default)
	{
		if (treeBuilder is not IProjectTreeCompositeInventoryBuilder compositeBuilder)
			throw new NotSupportedException("The configured tree builder does not support composite inventory.");

		return compositeBuilder.ReadCompositeInventory(
			rootPath,
			allowedRootFolders,
			discoveryRules,
			projectionRules,
			cancellationToken);
	}

	public BuildTreeResult Execute(BuildTreeRequest request, CancellationToken cancellationToken = default)
	{
		var result = treeBuilder.Build(request.RootPath, request.Filter, cancellationToken);
		var presentation = presenter.BuildWithFilePaths(result.Root);

		return new BuildTreeResult(
			presentation.Root,
			result.RootAccessDenied,
			result.HadAccessDenied,
			presentation.OrderedFilePaths);
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
		var presentation = presenter.BuildWithFilePaths(result.Root);
		return new BuildTreeSnapshotResult(
			new BuildTreeResult(
				presentation.Root,
				result.RootAccessDenied,
				result.HadAccessDenied,
				presentation.OrderedFilePaths),
			inventory);
	}

	public BuildTreeSnapshotResult ExecuteWithInventory(
		BuildTreeRequest request,
		ProjectTreeInventorySnapshot inventory,
		CancellationToken cancellationToken = default)
	{
		if (treeBuilder is not IProjectTreeInventoryBuilder inventoryBuilder)
			return new BuildTreeSnapshotResult(Execute(request, cancellationToken), Inventory: null);

		// The caller owns the inventory provenance. This overload only projects it
		// through the same tree builder, so behavior stays identical to ReadInventory + Build.
		var result = inventoryBuilder.Build(
			inventory,
			request.Filter,
			cancellationToken);
		var presentation = presenter.BuildWithFilePaths(result.Root);
		return new BuildTreeSnapshotResult(
			new BuildTreeResult(
				presentation.Root,
				result.RootAccessDenied,
				result.HadAccessDenied,
				presentation.OrderedFilePaths),
			inventory);
	}
}
