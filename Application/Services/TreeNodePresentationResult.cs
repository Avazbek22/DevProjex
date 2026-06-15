namespace DevProjex.Application.Services;

public sealed record TreeNodePresentationResult(
	TreeNodeDescriptor Root,
	IReadOnlyList<string> OrderedFilePaths);
