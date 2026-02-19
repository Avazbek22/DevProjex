using DevProjex.Application.Services;

namespace DevProjex.Application.UseCases;

public sealed class BuildTreeUseCase
{
	private readonly ITreeBuilder _treeBuilder;
	private readonly TreeNodePresentationService _presenter;

	public BuildTreeUseCase(ITreeBuilder treeBuilder, TreeNodePresentationService presenter)
	{
		_treeBuilder = treeBuilder;
		_presenter = presenter;
	}

	public BuildTreeResult Execute(BuildTreeRequest request, CancellationToken cancellationToken = default)
	{
		var result = _treeBuilder.Build(request.RootPath, request.Filter, cancellationToken);
		var root = _presenter.Build(result.Root);

		return new BuildTreeResult(root, result.RootAccessDenied, result.HadAccessDenied);
	}
}
