namespace DevProjex.Kernel.Contracts;

public sealed record BuildTreeResult(
	TreeNodeDescriptor Root,
	bool RootAccessDenied,
	bool HadAccessDenied,
	IReadOnlyList<string>? OrderedFilePaths = null);
