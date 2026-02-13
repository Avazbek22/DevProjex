using System.IO;
using System.Text;
using System.Text.Json;
using DevProjex.Kernel.Contracts;
using DevProjex.Kernel.Models;

namespace DevProjex.Application.Services;

public sealed class TreeAndContentExportService
{
	private const string ClipboardBlankLine = "\u00A0"; // NBSP: looks empty but won't collapse on paste
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	private readonly TreeExportService _treeExport;
	private readonly SelectedContentExportService _contentExport;

	public TreeAndContentExportService(TreeExportService treeExport, SelectedContentExportService contentExport)
	{
		_treeExport = treeExport;
		_contentExport = contentExport;
	}

	public string Build(string rootPath, TreeNodeDescriptor root, IReadOnlySet<string> selectedPaths)
		=> Build(rootPath, root, selectedPaths, TreeTextFormat.Ascii);

	public string Build(
		string rootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths,
		TreeTextFormat format)
		=> BuildAsync(rootPath, root, selectedPaths, format, CancellationToken.None).GetAwaiter().GetResult();

	public async Task<string> BuildAsync(string rootPath, TreeNodeDescriptor root, IReadOnlySet<string> selectedPaths, CancellationToken cancellationToken)
		=> await BuildAsync(rootPath, root, selectedPaths, TreeTextFormat.Ascii, cancellationToken).ConfigureAwait(false);

	public async Task<string> BuildAsync(
		string rootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths,
		TreeTextFormat format,
		CancellationToken cancellationToken)
	{
		bool hasSelection = selectedPaths.Count > 0 && TreeExportService.HasSelectedDescendantOrSelf(root, selectedPaths);

		string tree = hasSelection
			? _treeExport.BuildSelectedTree(rootPath, root, selectedPaths, format)
			: _treeExport.BuildFullTree(rootPath, root, format);

		if (hasSelection && string.IsNullOrWhiteSpace(tree))
			tree = _treeExport.BuildFullTree(rootPath, root, format);

		var files = hasSelection
			? GetSelectedFiles(selectedPaths)
			: GetAllFilePaths(root);

		var content = await _contentExport.BuildAsync(files, cancellationToken).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(content))
			return tree;

		if (format == TreeTextFormat.Json)
			return BuildJsonTreeAndContent(tree, content, rootPath);

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

	private static void AppendClipboardBlankLine(StringBuilder sb) => sb.AppendLine(ClipboardBlankLine);

	private static string BuildJsonTreeAndContent(string tree, string content, string rootPath)
	{
		var normalizedRootPath = Path.GetFullPath(rootPath);
		JsonElement treeElement;
		var effectiveRootPath = normalizedRootPath;
		try
		{
			using var treeDocument = JsonDocument.Parse(tree);
			var treeRootElement = treeDocument.RootElement;
			if (treeRootElement.ValueKind == JsonValueKind.Object)
			{
				if (treeRootElement.TryGetProperty("rootPath", out var documentRootPath) &&
				    documentRootPath.ValueKind == JsonValueKind.String &&
				    !string.IsNullOrWhiteSpace(documentRootPath.GetString()))
				{
					effectiveRootPath = documentRootPath.GetString()!;
				}

				if (treeRootElement.TryGetProperty("root", out var compactTreeRoot))
				{
					treeElement = compactTreeRoot.Clone();
				}
				else
				{
					treeElement = treeRootElement.Clone();
				}
			}
			else
			{
				treeElement = treeRootElement.Clone();
			}
		}
		catch
		{
			using var emptyDocument = JsonDocument.Parse("{}");
			treeElement = emptyDocument.RootElement.Clone();
		}

		var payload = new TreeAndContentJsonExport(
			RootPath: effectiveRootPath,
			Tree: treeElement,
			Content: content);

		return JsonSerializer.Serialize(payload, JsonOptions);
	}

	private sealed record TreeAndContentJsonExport(
		string RootPath,
		JsonElement Tree,
		string Content);
}
