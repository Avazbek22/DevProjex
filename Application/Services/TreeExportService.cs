using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevProjex.Kernel.Contracts;
using DevProjex.Kernel.Models;

namespace DevProjex.Application.Services;

public sealed class TreeExportService
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	// Pre-allocated indent segments to avoid string allocation in recursive tree rendering
	private const string IndentPipe = "│   ";
	private const string IndentSpace = "    ";
	private const string BranchMiddle = "├── ";
	private const string BranchLast = "└── ";

	public string BuildFullTree(string rootPath, TreeNodeDescriptor root)
		=> BuildFullTree(rootPath, root, TreeTextFormat.Ascii);

	public string BuildFullTree(string rootPath, TreeNodeDescriptor root, TreeTextFormat format)
	{
		if (format == TreeTextFormat.Json)
			return BuildFullTreeJson(rootPath, root);

		var sb = new StringBuilder();
		sb.Append(rootPath).AppendLine(":");
		sb.AppendLine();

		sb.Append("├── ").AppendLine(root.DisplayName);
		AppendAscii(root, "│   ", sb);

		return sb.ToString();
	}

	public string BuildSelectedTree(string rootPath, TreeNodeDescriptor root, IReadOnlySet<string> selectedPaths)
		=> BuildSelectedTree(rootPath, root, selectedPaths, TreeTextFormat.Ascii);

	public string BuildSelectedTree(
		string rootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths,
		TreeTextFormat format)
	{
		if (!HasSelectedDescendantOrSelf(root, selectedPaths))
			return string.Empty;

		if (format == TreeTextFormat.Json)
			return BuildSelectedTreeJson(rootPath, root, selectedPaths);

		var sb = new StringBuilder();
		sb.Append(rootPath).AppendLine(":");
		sb.AppendLine();

		sb.Append("├── ").AppendLine(root.DisplayName);
		AppendSelectedAscii(root, selectedPaths, "│   ", sb);

		return sb.ToString();
	}

	public static bool HasSelectedDescendantOrSelf(TreeNodeDescriptor node, IReadOnlySet<string> selectedPaths)
	{
		if (selectedPaths.Contains(node.FullPath)) return true;

		foreach (var child in node.Children)
		{
			if (HasSelectedDescendantOrSelf(child, selectedPaths))
				return true;
		}

		return false;
	}

	private static void AppendAscii(TreeNodeDescriptor node, string indent, StringBuilder sb)
	{
		var childCount = node.Children.Count;
		for (int i = 0; i < childCount; i++)
		{
			var child = node.Children[i];
			bool last = i == childCount - 1;

			sb.Append(indent).Append(last ? BranchLast : BranchMiddle).AppendLine(child.DisplayName);

			if (child.Children.Count > 0)
			{
				// Build indent in StringBuilder directly to avoid string allocation
				var indentLength = indent.Length;
				var nextIndent = string.Create(indentLength + 4, (indent, last), static (span, state) =>
				{
					state.indent.AsSpan().CopyTo(span);
					(state.last ? IndentSpace : IndentPipe).AsSpan().CopyTo(span[state.indent.Length..]);
				});
				AppendAscii(child, nextIndent, sb);
			}
		}
	}

	private static void AppendSelectedAscii(TreeNodeDescriptor node, IReadOnlySet<string> selectedPaths, string indent, StringBuilder sb)
	{
		// Count visible children without allocating a list
		int visibleCount = 0;
		foreach (var child in node.Children)
		{
			if (HasSelectedDescendantOrSelf(child, selectedPaths))
				visibleCount++;
		}

		int currentIndex = 0;
		foreach (var child in node.Children)
		{
			if (!HasSelectedDescendantOrSelf(child, selectedPaths))
				continue;

			currentIndex++;
			bool last = currentIndex == visibleCount;

			sb.Append(indent).Append(last ? BranchLast : BranchMiddle).AppendLine(child.DisplayName);

			if (child.Children.Count > 0)
			{
				// Build indent using string.Create to avoid intermediate allocations
				var indentLength = indent.Length;
				var nextIndent = string.Create(indentLength + 4, (indent, last), static (span, state) =>
				{
					state.indent.AsSpan().CopyTo(span);
					(state.last ? IndentSpace : IndentPipe).AsSpan().CopyTo(span[state.indent.Length..]);
				});
				AppendSelectedAscii(child, selectedPaths, nextIndent, sb);
			}
		}
	}

	private static string BuildFullTreeJson(string rootPath, TreeNodeDescriptor root)
	{
		var normalizedRootPath = Path.GetFullPath(rootPath);
		var payload = new TreeJsonExportDocument(
			RootPath: normalizedRootPath,
			Root: BuildJsonNode(normalizedRootPath, root));

		return JsonSerializer.Serialize(payload, JsonOptions);
	}

	private static string BuildSelectedTreeJson(
		string rootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths)
	{
		var normalizedRootPath = Path.GetFullPath(rootPath);
		var selectedRoot = BuildSelectedJsonNode(normalizedRootPath, root, selectedPaths);
		if (selectedRoot is null)
			return string.Empty;

		var payload = new TreeJsonExportDocument(
			RootPath: normalizedRootPath,
			Root: selectedRoot);

		return JsonSerializer.Serialize(payload, JsonOptions);
	}

	private static TreeJsonNode BuildJsonNode(string rootPath, TreeNodeDescriptor node)
	{
		var directories = new List<TreeJsonNode>();
		var files = new List<string>();
		foreach (var child in node.Children)
		{
			if (child.IsDirectory)
				directories.Add(BuildJsonNode(rootPath, child));
			else
				files.Add(child.DisplayName);
		}

		return new TreeJsonNode(
			Name: node.DisplayName,
			Path: ToRelativeJsonPath(rootPath, node.FullPath),
			AccessDenied: node.IsAccessDenied ? true : null,
			Dirs: directories.Count > 0 ? directories : null,
			Files: files.Count > 0 ? files : null);
	}

	private static TreeJsonNode? BuildSelectedJsonNode(
		string rootPath,
		TreeNodeDescriptor node,
		IReadOnlySet<string> selectedPaths)
	{
		var includeSelf = selectedPaths.Contains(node.FullPath);
		var selectedDirectories = new List<TreeJsonNode>();
		var selectedFiles = new List<string>();

		foreach (var child in node.Children)
		{
			if (child.IsDirectory)
			{
				var selectedChild = BuildSelectedJsonNode(rootPath, child, selectedPaths);
				if (selectedChild is not null)
					selectedDirectories.Add(selectedChild);
			}
			else if (selectedPaths.Contains(child.FullPath))
			{
				selectedFiles.Add(child.DisplayName);
			}
		}

		if (!includeSelf && selectedDirectories.Count == 0 && selectedFiles.Count == 0)
			return null;

		return new TreeJsonNode(
			Name: node.DisplayName,
			Path: ToRelativeJsonPath(rootPath, node.FullPath),
			AccessDenied: node.IsAccessDenied ? true : null,
			Dirs: selectedDirectories.Count > 0 ? selectedDirectories : null,
			Files: selectedFiles.Count > 0 ? selectedFiles : null);
	}

	private static string ToRelativeJsonPath(string rootPath, string fullPath)
	{
		try
		{
			var relativePath = Path.GetRelativePath(rootPath, fullPath);
			if (string.IsNullOrWhiteSpace(relativePath) || relativePath == ".")
				return ".";

			return relativePath.Replace('\\', '/');
		}
		catch
		{
			return fullPath.Replace('\\', '/');
		}
	}

	private sealed record TreeJsonExportDocument(string RootPath, TreeJsonNode Root);

	private sealed record TreeJsonNode(
		string Name,
		string Path,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AccessDenied,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TreeJsonNode>? Dirs,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Files);
}
