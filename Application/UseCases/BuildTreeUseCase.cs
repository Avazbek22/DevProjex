namespace DevProjex.Application.UseCases;

public sealed class BuildTreeUseCase(ITreeBuilder treeBuilder, TreeNodePresentationService presenter)
{
	public BuildTreeResult Execute(BuildTreeRequest request, CancellationToken cancellationToken = default)
	{
		var result = treeBuilder.Build(request.RootPath, request.Filter, cancellationToken);
		var root = presenter.Build(result.Root);

		return new BuildTreeResult(root, result.RootAccessDenied, result.HadAccessDenied);
	}
}
