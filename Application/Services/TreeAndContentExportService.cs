using DevProjex.Application.Selection;
using DevProjex.Application.Secrets;

namespace DevProjex.Application.Services;

public sealed class TreeAndContentExportService(
	TreeExportService treeExport,
	SelectedContentExportService contentExport)
{
	private const string ClipboardBlankLine = "\u00A0"; // NBSP: looks empty but won't collapse on paste
	private static readonly IReadOnlySet<string> EmptySelection = new HashSet<string>(PathComparer.Default);

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
		ExportPathPresentation? pathPresentation = null,
		SecretRedactionContext? redactionContext = null)
	{
		var displayRootPath = pathPresentation?.DisplayRootPath;
		var displayRootName = pathPresentation?.DisplayRootName;
		bool hasSelection = selectedPaths.Count > 0 && TreeExportService.HasSelectedDescendantOrSelf(root, selectedPaths);

		string tree = hasSelection
			? treeExport.BuildSelectedTree(rootPath, root, selectedPaths, format, displayRootPath, displayRootName)
			: treeExport.BuildFullTree(rootPath, root, format, displayRootPath, displayRootName);

		if (hasSelection && string.IsNullOrWhiteSpace(tree))
			tree = treeExport.BuildFullTree(rootPath, root, format, displayRootPath, displayRootName);

		var files = ProjectTreeSelectionProjection.BuildOrderedSelectedFilePaths(
			root,
			hasSelection ? selectedPaths : EmptySelection,
			ensureExists: hasSelection);
		var contentPathMapper = CreateRelativeContentHeaderPathMapper(rootPath);

		var contentResult = await contentExport.BuildResultAsync(
			files,
			cancellationToken,
			contentPathMapper,
			redactionContext,
			includeLegend: false).ConfigureAwait(false);
		var content = contentResult.Text;
		if (string.IsNullOrWhiteSpace(content))
			return tree;

		// The selected format applies only to the tree block; file content stays plain text.
		var sb = new StringBuilder();
		if (contentResult.Redaction is { RedactedCount: > 0 } redaction)
		{
			sb.AppendLine(SecretRedactionLegend.CreatePlainText(
				redaction.RedactedCount,
				contentResult.PlaceholderExample!,
				contentResult.LegendText!));
			AppendClipboardBlankLine(sb);
			AppendClipboardBlankLine(sb);
		}
		sb.Append(tree.TrimEnd('\r', '\n'));
		sb.AppendLine();
		AppendClipboardBlankLine(sb);
		AppendClipboardBlankLine(sb);
		sb.Append(content);

		return sb.ToString();
	}

	public static Func<string, string> CreateRelativeContentHeaderPathMapper(string rootPath)
	{
		string? normalizedRootPath;
		try
		{
			// The mapper runs once per exported file. Resolve the invariant root once instead
			// of repeating Path.GetFullPath throughout large preview and metrics traversals.
			normalizedRootPath = Path.GetFullPath(rootPath);
		}
		catch
		{
			normalizedRootPath = null;
		}

		return filePath => MapRelativeContentHeaderPathFromNormalizedRoot(normalizedRootPath, filePath);
	}

	public static string MapRelativeContentHeaderPath(string rootPath, string filePath)
	{
		string? normalizedRootPath;
		try
		{
			normalizedRootPath = Path.GetFullPath(rootPath);
		}
		catch
		{
			normalizedRootPath = null;
		}

		return MapRelativeContentHeaderPathFromNormalizedRoot(normalizedRootPath, filePath);
	}

	private static string MapRelativeContentHeaderPathFromNormalizedRoot(
		string? normalizedRootPath,
		string filePath)
	{
		try
		{
			if (normalizedRootPath is null)
				return GetFallbackContentHeaderPath(filePath);

			var relativePath = Path.GetRelativePath(normalizedRootPath, filePath);
			if (!string.IsNullOrWhiteSpace(relativePath) &&
			    relativePath != "." &&
			    !IsOutsideRoot(relativePath) &&
			    !Path.IsPathRooted(relativePath))
			{
				return relativePath.Replace('\\', '/');
			}
		}
		catch
		{
			// Tree + Content already carries the root path in the tree block, so file
			// sections should stay short and portable even when relative path calculation fails.
		}

		return GetFallbackContentHeaderPath(filePath);
	}

	private static string GetFallbackContentHeaderPath(string filePath)
	{
		var fileName = Path.GetFileName(filePath);
		return string.IsNullOrWhiteSpace(fileName) ? filePath.Replace('\\', '/') : fileName;
	}

	private static bool IsOutsideRoot(string relativePath) =>
		relativePath.Equals("..", StringComparison.Ordinal) ||
		relativePath.StartsWith("../", StringComparison.Ordinal) ||
		relativePath.StartsWith(@"..\", StringComparison.Ordinal);

	private static void AppendClipboardBlankLine(StringBuilder sb) => sb.AppendLine(ClipboardBlankLine);
}
