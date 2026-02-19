namespace DevProjex.Application.Services;

public sealed class TreeNodePresentationService
{
	private readonly LocalizationService _localization;
	private readonly IIconMapper _iconMapper;

	public TreeNodePresentationService(LocalizationService localization, IIconMapper iconMapper)
	{
		_localization = localization;
		_iconMapper = iconMapper;
	}

	public TreeNodeDescriptor Build(FileSystemNode root)
	{
		return BuildNode(root, isRoot: true);
	}

	private TreeNodeDescriptor BuildNode(FileSystemNode node, bool isRoot)
	{
		var displayName = node.IsAccessDenied
			? (isRoot ? _localization["Tree.AccessDeniedRoot"] : _localization["Tree.AccessDenied"])
			: node.Name;

		var iconKey = _iconMapper.GetIconKey(node);

		// Pre-allocate capacity to avoid list resizing
		var children = new List<TreeNodeDescriptor>(node.Children.Count);
		foreach (var child in node.Children)
			children.Add(BuildNode(child, isRoot: false));

		return new TreeNodeDescriptor(
			DisplayName: displayName,
			FullPath: node.FullPath,
			IsDirectory: node.IsDirectory,
			IsAccessDenied: node.IsAccessDenied,
			IconKey: iconKey,
			Children: children);
	}
}
