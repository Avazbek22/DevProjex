using System.Text.Json;

namespace DevProjex.Application.Services;

public sealed class TreeExportService
{
	private static readonly StringComparer JsonTreeNameComparer = StringComparer.OrdinalIgnoreCase;

	private static readonly JsonWriterOptions JsonWriterOptions = new()
	{
		Indented = true
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
			return BuildFullTreeJson(rootPath, root);

		var sb = new StringBuilder();
		sb.Append(outputRootPath).AppendLine(":");
		sb.AppendLine();

		sb.Append("├── ").AppendLine(outputRootName);
		AppendAscii(root, "│   ", sb);

		return sb.ToString();
	}

	public ExportOutputMetrics CalculateFullTreeMetrics(
		string rootPath,
		TreeNodeDescriptor root,
		TreeTextFormat format,
		string? displayRootPath = null,
		string? displayRootName = null)
	{
		if (format == TreeTextFormat.Json)
			return ExportOutputMetricsCalculator.FromText(
				BuildFullTree(rootPath, root, format, displayRootPath, displayRootName));

		var outputRootPath = string.IsNullOrWhiteSpace(displayRootPath) ? rootPath : displayRootPath;
		var outputRootName = ResolveRootDisplayName(root, displayRootName);
		return CalculateAsciiFullTreeMetrics(outputRootPath, root, outputRootName);
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
				root,
				includedPaths);

		var sb = new StringBuilder();
		sb.Append(outputRootPath).AppendLine(":");
		sb.AppendLine();

		sb.Append("├── ").AppendLine(outputRootName);
		AppendSelectedAscii(root, includedPaths, "│   ", sb);

		return sb.ToString();
	}

	public ExportOutputMetrics CalculateSelectedTreeMetrics(
		string rootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths,
		TreeTextFormat format,
		string? displayRootPath = null,
		string? displayRootName = null)
	{
		var includedPaths = new HashSet<string>(PathComparer.Default);
		if (!CollectIncludedPaths(root, selectedPaths, includedPaths))
			return ExportOutputMetrics.Empty;

		if (format == TreeTextFormat.Json)
			return ExportOutputMetricsCalculator.FromText(
				BuildSelectedTree(rootPath, root, selectedPaths, format, displayRootPath, displayRootName));

		var outputRootPath = string.IsNullOrWhiteSpace(displayRootPath) ? rootPath : displayRootPath;
		var outputRootName = ResolveRootDisplayName(root, displayRootName);
		return CalculateAsciiSelectedTreeMetrics(outputRootPath, root, includedPaths, outputRootName);
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
		TreeNodeDescriptor root)
	{
		return BuildJsonDocument(localRootPath, root, includedPaths: null);
	}

	private static string BuildSelectedTreeJson(
		string localRootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> includedPaths)
	{
		if (!includedPaths.Contains(root.FullPath) &&
		    !HasIncludedChild(root.Children, includedPaths))
		{
			return string.Empty;
		}

		return BuildJsonDocument(localRootPath, root, includedPaths);
	}

	private static string BuildJsonDocument(
		string localRootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string>? includedPaths)
	{
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream, JsonWriterOptions))
		{
			writer.WriteStartObject();
			writer.WriteString("rootPath", ResolveJsonRootPath(localRootPath));
			writer.WritePropertyName("tree");
			WriteJsonTreeContents(writer, root, includedPaths);
			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private static void WriteJsonTreeContents(
		Utf8JsonWriter writer,
		TreeNodeDescriptor root,
		IReadOnlySet<string>? includedPaths)
	{
		writer.WriteStartObject();

		if (root.IsDirectory)
		{
			WriteJsonRootContents(writer, root.Children, includedPaths);
		}
		else if (includedPaths is null || includedPaths.Contains(root.FullPath))
		{
			WriteJsonRootFiles(writer, [root]);
		}

		writer.WriteEndObject();
	}

	private static void WriteJsonRootContents(
		Utf8JsonWriter writer,
		IReadOnlyList<TreeNodeDescriptor> children,
		IReadOnlySet<string>? includedPaths)
	{
		var orderedChildren = GetOrderedJsonChildren(children, includedPaths);
		WriteJsonDirectoryProperties(writer, orderedChildren, includedPaths);
		WriteJsonCurrentFolderFilesIfAny(writer, orderedChildren);
	}

	private static void WriteJsonDirectoryValue(
		Utf8JsonWriter writer,
		TreeNodeDescriptor directory,
		IReadOnlySet<string>? includedPaths)
	{
		var orderedChildren = GetOrderedJsonChildren(directory.Children, includedPaths);
		var hasDirectories = HasDirectoryChild(orderedChildren);
		var hasFiles = HasFileChild(orderedChildren);

		if (!hasDirectories)
		{
			WriteJsonFileArray(writer, orderedChildren);
			return;
		}

		writer.WriteStartObject();
		WriteJsonDirectoryProperties(writer, orderedChildren, includedPaths);
		if (hasFiles)
			WriteJsonCurrentFolderFiles(writer, orderedChildren);
		writer.WriteEndObject();
	}

	private static void WriteJsonDirectoryProperties(
		Utf8JsonWriter writer,
		IReadOnlyList<TreeNodeDescriptor> orderedChildren,
		IReadOnlySet<string>? includedPaths)
	{
		foreach (var child in orderedChildren)
		{
			if (!child.IsDirectory)
				continue;

			writer.WritePropertyName(child.DisplayName);
			WriteJsonDirectoryValue(writer, child, includedPaths);
		}
	}

	private static void WriteJsonCurrentFolderFilesIfAny(
		Utf8JsonWriter writer,
		IReadOnlyList<TreeNodeDescriptor> orderedChildren)
	{
		if (HasFileChild(orderedChildren))
			WriteJsonCurrentFolderFiles(writer, orderedChildren);
	}

	private static void WriteJsonCurrentFolderFiles(
		Utf8JsonWriter writer,
		IReadOnlyList<TreeNodeDescriptor> orderedChildren)
	{
		writer.WritePropertyName("/");
		WriteJsonFileArray(writer, orderedChildren);
	}

	private static void WriteJsonRootFiles(Utf8JsonWriter writer, IReadOnlyList<TreeNodeDescriptor> files)
	{
		writer.WritePropertyName("/");
		WriteJsonFileArray(writer, files);
	}

	private static void WriteJsonFileArray(Utf8JsonWriter writer, IReadOnlyList<TreeNodeDescriptor> orderedChildren)
	{
		writer.WriteStartArray();
		foreach (var child in orderedChildren)
		{
			if (!child.IsDirectory)
				writer.WriteStringValue(child.DisplayName);
		}
		writer.WriteEndArray();
	}

	private static List<TreeNodeDescriptor> GetOrderedJsonChildren(
		IReadOnlyList<TreeNodeDescriptor> children,
		IReadOnlySet<string>? includedPaths)
	{
		var ordered = new List<TreeNodeDescriptor>(children.Count);
		foreach (var child in children)
		{
			if (includedPaths is null || includedPaths.Contains(child.FullPath))
				ordered.Add(child);
		}

		ordered.Sort(CompareJsonTreeNodes);
		return ordered;
	}

	private static int CompareJsonTreeNodes(TreeNodeDescriptor left, TreeNodeDescriptor right)
	{
		if (left.IsDirectory != right.IsDirectory)
			return left.IsDirectory ? -1 : 1;

		var comparison = JsonTreeNameComparer.Compare(left.DisplayName, right.DisplayName);
		return comparison != 0
			? comparison
			: StringComparer.Ordinal.Compare(left.DisplayName, right.DisplayName);
	}

	private static bool HasIncludedChild(
		IReadOnlyList<TreeNodeDescriptor> children,
		IReadOnlySet<string> includedPaths)
	{
		foreach (var child in children)
		{
			if (includedPaths.Contains(child.FullPath))
				return true;
		}

		return false;
	}

	private static bool HasDirectoryChild(IReadOnlyList<TreeNodeDescriptor> children)
	{
		foreach (var child in children)
		{
			if (child.IsDirectory)
				return true;
		}

		return false;
	}

	private static bool HasFileChild(IReadOnlyList<TreeNodeDescriptor> children)
	{
		foreach (var child in children)
		{
			if (!child.IsDirectory)
				return true;
		}

		return false;
	}

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

	private static ExportOutputMetrics CalculateAsciiFullTreeMetrics(
		string outputRootPath,
		TreeNodeDescriptor root,
		string outputRootName)
	{
		var chars = 0;
		var lineBreaks = 0;

		AppendAsciiLineMetrics(outputRootPath.Length + 1, ref chars, ref lineBreaks); // "<rootPath>:"
		AppendAsciiLineMetrics(0, ref chars, ref lineBreaks); // blank separator line
		AppendAsciiLineMetrics(BranchMiddle.Length + outputRootName.Length, ref chars, ref lineBreaks);
		AppendFullAsciiChildMetrics(root, IndentPipe.Length, ref chars, ref lineBreaks);

		return CreateMetricsFromNormalizedCounts(chars, lineBreaks);
	}

	private static ExportOutputMetrics CalculateAsciiSelectedTreeMetrics(
		string outputRootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> includedPaths,
		string outputRootName)
	{
		var chars = 0;
		var lineBreaks = 0;

		AppendAsciiLineMetrics(outputRootPath.Length + 1, ref chars, ref lineBreaks); // "<rootPath>:"
		AppendAsciiLineMetrics(0, ref chars, ref lineBreaks); // blank separator line
		AppendAsciiLineMetrics(BranchMiddle.Length + outputRootName.Length, ref chars, ref lineBreaks);
		AppendSelectedAsciiChildMetrics(root, includedPaths, IndentPipe.Length, ref chars, ref lineBreaks);

		return CreateMetricsFromNormalizedCounts(chars, lineBreaks);
	}

	private static void AppendFullAsciiChildMetrics(
		TreeNodeDescriptor node,
		int indentLength,
		ref int chars,
		ref int lineBreaks)
	{
		var childCount = node.Children.Count;
		for (var index = 0; index < childCount; index++)
		{
			var child = node.Children[index];
			var branchLength = index == childCount - 1 ? BranchLast.Length : BranchMiddle.Length;

			AppendAsciiLineMetrics(indentLength + branchLength + child.DisplayName.Length, ref chars, ref lineBreaks);

			if (child.Children.Count > 0)
				AppendFullAsciiChildMetrics(child, indentLength + IndentPipe.Length, ref chars, ref lineBreaks);
		}
	}

	private static void AppendSelectedAsciiChildMetrics(
		TreeNodeDescriptor node,
		IReadOnlySet<string> includedPaths,
		int indentLength,
		ref int chars,
		ref int lineBreaks)
	{
		var visibleCount = 0;
		foreach (var child in node.Children)
		{
			if (includedPaths.Contains(child.FullPath))
				visibleCount++;
		}

		var visibleIndex = 0;
		foreach (var child in node.Children)
		{
			if (!includedPaths.Contains(child.FullPath))
				continue;

			visibleIndex++;
			var branchLength = visibleIndex == visibleCount ? BranchLast.Length : BranchMiddle.Length;

			AppendAsciiLineMetrics(indentLength + branchLength + child.DisplayName.Length, ref chars, ref lineBreaks);

			if (child.Children.Count > 0)
				AppendSelectedAsciiChildMetrics(child, includedPaths, indentLength + IndentPipe.Length, ref chars, ref lineBreaks);
		}
	}

	private static void AppendAsciiLineMetrics(int renderedChars, ref int chars, ref int lineBreaks)
	{
		chars += renderedChars + 1; // Normalize any platform newline to a single logical line-break char.
		lineBreaks++;
	}

	private static ExportOutputMetrics CreateMetricsFromNormalizedCounts(int chars, int lineBreaks)
	{
		if (chars <= 0)
			return ExportOutputMetrics.Empty;

		var lines = lineBreaks + 1;
		var tokens = (chars + 3) / 4;
		return new ExportOutputMetrics(lines, chars, tokens);
	}

	private static string ResolveRootDisplayName(TreeNodeDescriptor root, string? displayRootName)
		=> string.IsNullOrWhiteSpace(displayRootName) ? root.DisplayName : displayRootName;

	private static string ResolveJsonRootPath(string localRootPath)
	{
		try
		{
			return NormalizeJsonPath(Path.GetFullPath(localRootPath));
		}
		catch
		{
			return NormalizeJsonPath(localRootPath);
		}
	}

	private static string NormalizeJsonPath(string path) => path.Replace('\\', '/');
}
