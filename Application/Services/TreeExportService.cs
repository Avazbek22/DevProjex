using System.Text.Json;
using System.Text.Json.Serialization;

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

	public string BuildFullTree(
		string rootPath,
		TreeNodeDescriptor root,
		TreeTextFormat format,
		string? displayRootPath = null,
		string? displayRootName = null)
	{
		var outputRootPath = string.IsNullOrWhiteSpace(displayRootPath) ? rootPath : displayRootPath;
		var outputRootName = ResolveRootDisplayName(root, displayRootName);

		if (format == TreeTextFormat.Json)
			return BuildFullTreeJson(rootPath, ResolveJsonRootPath(rootPath, displayRootPath), root, outputRootName);

		var sb = new StringBuilder();
		sb.Append(outputRootPath).AppendLine(":");
		sb.AppendLine();

		sb.Append("├── ").AppendLine(outputRootName);
		AppendAscii(root, "│   ", sb);

		return sb.ToString();
	}

	public string BuildSelectedTree(string rootPath, TreeNodeDescriptor root, IReadOnlySet<string> selectedPaths)
		=> BuildSelectedTree(rootPath, root, selectedPaths, TreeTextFormat.Ascii);

	public string BuildSelectedTree(
		string rootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths,
		TreeTextFormat format,
		string? displayRootPath = null,
		string? displayRootName = null)
	{
		var includedPaths = new HashSet<string>(PathComparer.Default);
		if (!CollectIncludedPaths(root, selectedPaths, includedPaths))
			return string.Empty;

		var outputRootPath = string.IsNullOrWhiteSpace(displayRootPath) ? rootPath : displayRootPath;
		var outputRootName = ResolveRootDisplayName(root, displayRootName);

		if (format == TreeTextFormat.Json)
			return BuildSelectedTreeJson(
				rootPath,
				ResolveJsonRootPath(rootPath, displayRootPath),
				root,
				includedPaths,
				outputRootName);

		var sb = new StringBuilder();
		sb.Append(outputRootPath).AppendLine(":");
		sb.AppendLine();

		sb.Append("├── ").AppendLine(outputRootName);
		AppendSelectedAscii(root, includedPaths, "│   ", sb);

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
			if (selectedPaths.Contains(child.FullPath))
				visibleCount++;
		}

		int currentIndex = 0;
		foreach (var child in node.Children)
		{
			if (!selectedPaths.Contains(child.FullPath))
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

	private static string BuildFullTreeJson(
		string localRootPath,
		string displayRootPath,
		TreeNodeDescriptor root,
		string rootDisplayName)
	{
		var normalizedRootPath = Path.GetFullPath(localRootPath);
		var payload = new TreeJsonExportDocument(
			RootPath: displayRootPath,
			Root: BuildJsonNode(normalizedRootPath, root, rootDisplayName));

		return JsonSerializer.Serialize(payload, JsonOptions);
	}

	private static string BuildSelectedTreeJson(
		string localRootPath,
		string displayRootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> includedPaths,
		string rootDisplayName)
	{
		var normalizedRootPath = Path.GetFullPath(localRootPath);
		var selectedRoot = BuildSelectedJsonNode(normalizedRootPath, root, includedPaths, rootDisplayName);
		if (selectedRoot is null)
			return string.Empty;

		var payload = new TreeJsonExportDocument(
			RootPath: displayRootPath,
			Root: selectedRoot);

		return JsonSerializer.Serialize(payload, JsonOptions);
	}

	private static string ResolveJsonRootPath(string localRootPath, string? displayRootPath)
	{
		if (!string.IsNullOrWhiteSpace(displayRootPath))
			return displayRootPath;

		try
		{
			return Path.GetFullPath(localRootPath);
		}
		catch
		{
			return localRootPath;
		}
	}

	private static TreeJsonNode BuildJsonNode(string rootPath, TreeNodeDescriptor node, string? rootDisplayName = null)
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
			Name: string.IsNullOrWhiteSpace(rootDisplayName) ? node.DisplayName : rootDisplayName,
			Path: ToRelativeJsonPath(rootPath, node.FullPath),
			AccessDenied: node.IsAccessDenied ? true : null,
			Dirs: directories.Count > 0 ? directories : null,
			Files: files.Count > 0 ? files : null);
	}

	private static TreeJsonNode? BuildSelectedJsonNode(
		string rootPath,
		TreeNodeDescriptor node,
		IReadOnlySet<string> includedPaths,
		string? rootDisplayName = null)
	{
		var includeSelf = includedPaths.Contains(node.FullPath);
		var selectedDirectories = new List<TreeJsonNode>();
		var selectedFiles = new List<string>();

		foreach (var child in node.Children)
		{
			if (child.IsDirectory)
			{
				var selectedChild = BuildSelectedJsonNode(rootPath, child, includedPaths);
				if (selectedChild is not null)
					selectedDirectories.Add(selectedChild);
			}
			else if (includedPaths.Contains(child.FullPath))
			{
				selectedFiles.Add(child.DisplayName);
			}
		}

		if (!includeSelf && selectedDirectories.Count == 0 && selectedFiles.Count == 0)
			return null;

		return new TreeJsonNode(
			Name: string.IsNullOrWhiteSpace(rootDisplayName) ? node.DisplayName : rootDisplayName,
			Path: ToRelativeJsonPath(rootPath, node.FullPath),
			AccessDenied: node.IsAccessDenied ? true : null,
			Dirs: selectedDirectories.Count > 0 ? selectedDirectories : null,
			Files: selectedFiles.Count > 0 ? selectedFiles : null);
	}

	private static string ResolveRootDisplayName(TreeNodeDescriptor root, string? displayRootName)
		=> string.IsNullOrWhiteSpace(displayRootName) ? root.DisplayName : displayRootName;

	private static bool CollectIncludedPaths(
		TreeNodeDescriptor node,
		IReadOnlySet<string> selectedPaths,
		HashSet<string> includedPaths)
	{
		var includeSelf = selectedPaths.Contains(node.FullPath);
		if (includeSelf)
		{
			// Selecting a directory semantically means selecting the whole subtree.
			// Expanding descendants here keeps tree export aligned with preview/content
			// metrics without forcing the UI tree to materialize child view-models.
			CollectSubtreePaths(node, includedPaths);
			return true;
		}

		var includeByChildren = false;

		foreach (var child in node.Children)
		{
			if (CollectIncludedPaths(child, selectedPaths, includedPaths))
				includeByChildren = true;
		}

		if (!includeSelf && !includeByChildren)
			return false;

		includedPaths.Add(node.FullPath);
		return true;
	}

	private static void CollectSubtreePaths(TreeNodeDescriptor node, HashSet<string> includedPaths)
	{
		var stack = new Stack<TreeNodeDescriptor>();
		stack.Push(node);

		while (stack.Count > 0)
		{
			var current = stack.Pop();
			includedPaths.Add(current.FullPath);

			for (var index = current.Children.Count - 1; index >= 0; index--)
				stack.Push(current.Children[index]);
		}
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
