namespace DevProjex.Application.Services;

public sealed class TreeAndContentExportService(
	TreeExportService treeExport,
	SelectedContentExportService contentExport)
{
	private const string ClipboardBlankLine = "\u00A0"; // NBSP: looks empty but won't collapse on paste

	public string Build(string rootPath, TreeNodeDescriptor root, IReadOnlySet<string> selectedPaths)
		=> Build(rootPath, root, selectedPaths, TreeTextFormat.Ascii);

	public string Build(
		string rootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths,
		TreeTextFormat format)
		=> Build(rootPath, root, selectedPaths, format, pathPresentation: null);

	public string Build(
		string rootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths,
		TreeTextFormat format,
		ExportPathPresentation? pathPresentation)
		=> BuildAsync(rootPath, root, selectedPaths, format, CancellationToken.None, pathPresentation).GetAwaiter().GetResult();

	public async Task<string> BuildAsync(string rootPath, TreeNodeDescriptor root, IReadOnlySet<string> selectedPaths, CancellationToken cancellationToken)
		=> await BuildAsync(rootPath, root, selectedPaths, TreeTextFormat.Ascii, cancellationToken).ConfigureAwait(false);

	public async Task<string> BuildAsync(
		string rootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths,
		TreeTextFormat format,
		CancellationToken cancellationToken,
		ExportPathPresentation? pathPresentation = null)
	{
		var displayRootPath = pathPresentation?.DisplayRootPath;
		var displayRootName = pathPresentation?.DisplayRootName;
		bool hasSelection = selectedPaths.Count > 0 && TreeExportService.HasSelectedDescendantOrSelf(root, selectedPaths);

		string tree = hasSelection
			? treeExport.BuildSelectedTree(rootPath, root, selectedPaths, format, displayRootPath, displayRootName)
			: treeExport.BuildFullTree(rootPath, root, format, displayRootPath, displayRootName);

		if (hasSelection && string.IsNullOrWhiteSpace(tree))
			tree = treeExport.BuildFullTree(rootPath, root, format, displayRootPath, displayRootName);

		var files = hasSelection
			? GetSelectedFiles(selectedPaths)
			: GetAllFilePaths(root);
		var contentPathMapper = ResolveContentPathMapper(rootPath, format, pathPresentation);

		var content = await contentExport.BuildAsync(files, cancellationToken, contentPathMapper).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(content))
			return tree;

		// For both ASCII and JSON: tree + separator + content
		// JSON format applies only to tree structure, content remains plain text
		var sb = new StringBuilder();
		sb.Append(tree.TrimEnd('\r', '\n'));
		AppendClipboardBlankLine(sb);
		AppendClipboardBlankLine(sb);
		sb.Append(content);

		return sb.ToString();
	}

	private static IEnumerable<string> GetSelectedFiles(IReadOnlySet<string> selectedPaths)
	{
		foreach (var path in selectedPaths)
		{
			if (File.Exists(path))
				yield return path;
		}
	}

	private static IEnumerable<string> GetAllFilePaths(TreeNodeDescriptor node)
	{
		if (!node.IsDirectory)
			yield return node.FullPath;

		foreach (var child in node.Children)
		{
			foreach (var path in GetAllFilePaths(child))
				yield return path;
		}
	}

	private static Func<string, string>? ResolveContentPathMapper(
		string rootPath,
		TreeTextFormat format,
		ExportPathPresentation? pathPresentation)
	{
		return format == TreeTextFormat.Json
			? filePath => MapJsonContentPath(rootPath, filePath)
			: pathPresentation?.MapFilePath;
	}

	private static string MapJsonContentPath(string rootPath, string filePath)
	{
		try
		{
			var relativePath = Path.GetRelativePath(Path.GetFullPath(rootPath), filePath);
			if (!string.IsNullOrWhiteSpace(relativePath) &&
			    relativePath != "." &&
			    !relativePath.StartsWith("..", StringComparison.Ordinal) &&
			    !Path.IsPathRooted(relativePath))
			{
				return relativePath.Replace('\\', '/');
			}
		}
		catch
		{
			// Fall back to a stable leaf name; JSON tree-content headers should not repeat
			// the absolute root path that is already present in the JSON block.
		}

		var fileName = Path.GetFileName(filePath);
		return string.IsNullOrWhiteSpace(fileName) ? filePath.Replace('\\', '/') : fileName;
	}

	private static void AppendClipboardBlankLine(StringBuilder sb) => sb.AppendLine(ClipboardBlankLine);
}
