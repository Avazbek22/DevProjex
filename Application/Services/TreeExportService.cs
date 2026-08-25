using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Xml;

namespace DevProjex.Application.Services;

public sealed class TreeExportService
{
	private static readonly StringComparer StructuredTreeNameComparer = StringComparer.OrdinalIgnoreCase;

	private static readonly JsonWriterOptions JsonWriterOptions = new()
	{
		Indented = true,
		Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
	};

	private static readonly XmlWriterSettings XmlWriterSettings = new()
	{
		OmitXmlDeclaration = true,
		Indent = true
	};

	// Pre-allocated indent segments to avoid string allocation in recursive tree rendering
	private const string IndentPipe = "│   ";
	private const string IndentSpace = "    ";
	private const string BranchMiddle = "├── ";
	private const string BranchLast = "└── ";
	private const string PlainIndentPipe = "|   ";
	private const string PlainBranchMiddle = "|-- ";
	private const string PlainBranchLast = "`-- ";
	private const int MaximumTreeTextWriteCharacters = 4 * 1024;

	public string BuildFullTree(string rootPath, TreeNodeDescriptor root)
		=> BuildFullTree(rootPath, root, TreeTextFormat.Ascii);

	public string BuildFullTree(
		string rootPath,
		TreeNodeDescriptor root,
		TreeTextFormat format,
		string? displayRootPath = null,
		string? displayRootName = null,
		bool includeRootPath = true)
	{
		ValidateFormat(format);
		var outputRootPath = string.IsNullOrWhiteSpace(displayRootPath) ? rootPath : displayRootPath;
		var outputRootName = ResolveRootDisplayName(root, displayRootName);

		return format switch
		{
			TreeTextFormat.Ascii =>
				BuildFullTreeAscii(outputRootPath, outputRootName, root, includeRootPath),
			TreeTextFormat.Json => includeRootPath
				? BuildFullTreeJson(outputRootPath, root)
				: BuildNamedTreeJson(outputRootName, root),
			TreeTextFormat.Xml => includeRootPath
				? BuildFullTreeXml(outputRootPath, root)
				: BuildNamedTreeXml(outputRootName, root),
			TreeTextFormat.Markdown => includeRootPath
				? BuildFullTreeMarkdown(outputRootPath, root)
				: BuildNamedTreeMarkdown(outputRootName, root),
			_ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
		};
	}

	public string BuildFullTreePlain(
		string rootPath,
		TreeNodeDescriptor root,
		string? displayRootPath = null,
		string? displayRootName = null,
		bool includeRootPath = true)
	{
		var outputRootPath = string.IsNullOrWhiteSpace(displayRootPath)
			? rootPath
			: displayRootPath;
		var outputRootName = ResolveRootDisplayName(root, displayRootName);
		var output = new StringBuilder();
		if (includeRootPath)
		{
			output.Append(outputRootPath).AppendLine(":");
			output.AppendLine();
			output.Append(PlainBranchMiddle).AppendLine(outputRootName);
			AppendPlain(root, PlainIndentPipe, output);
		}
		else
		{
			output.AppendLine(outputRootName);
			AppendPlain(root, string.Empty, output);
		}
		return output.ToString();
	}

	public Task WriteFullTreeAsync(
		TextWriter destination,
		string rootPath,
		TreeNodeDescriptor root,
		string? displayRootPath = null,
		string? displayRootName = null,
		bool includeRootPath = true,
		bool includeFinalLineEnding = true,
		CancellationToken cancellationToken = default) =>
		WriteFullTreeTextAsync(
			destination,
			rootPath,
			root,
			displayRootPath,
			displayRootName,
			includeRootPath,
			includeFinalLineEnding,
			plain: false,
			cancellationToken);

	public Task WriteFullTreePlainAsync(
		TextWriter destination,
		string rootPath,
		TreeNodeDescriptor root,
		string? displayRootPath = null,
		string? displayRootName = null,
		bool includeRootPath = true,
		bool includeFinalLineEnding = true,
		CancellationToken cancellationToken = default) =>
		WriteFullTreeTextAsync(
			destination,
			rootPath,
			root,
			displayRootPath,
			displayRootName,
			includeRootPath,
			includeFinalLineEnding,
			plain: true,
			cancellationToken);

	public int CalculateFullTreeLongestBacktickRun(
		string rootPath,
		TreeNodeDescriptor root,
		string? displayRootPath = null,
		string? displayRootName = null,
		bool includeRootPath = true,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(root);
		cancellationToken.ThrowIfCancellationRequested();

		var outputRootPath = string.IsNullOrWhiteSpace(displayRootPath)
			? rootPath
			: displayRootPath;
		var longest = includeRootPath
			? FindLongestCharacterRun(outputRootPath, '`')
			: 0;
		longest = Math.Max(
			longest,
			FindLongestCharacterRun(ResolveRootDisplayName(root, displayRootName), '`'));

		var frames = new List<TreeTraversalFrame> { new(root) };
		while (frames.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var frame = frames[^1];
			if (frame.NextChildIndex >= frame.Node.Children.Count)
			{
				frames.RemoveAt(frames.Count - 1);
				continue;
			}

			var child = frame.Node.Children[frame.NextChildIndex++];
			longest = Math.Max(
				longest,
				FindLongestCharacterRun(child.DisplayName, '`'));
			if (child.Children.Count > 0)
				frames.Add(new TreeTraversalFrame(child));
		}

		return longest;
	}

	private static string BuildFullTreeAscii(
		string outputRootPath,
		string outputRootName,
		TreeNodeDescriptor root,
		bool includeRootPath = true)
	{
		var sb = new StringBuilder();
		if (includeRootPath)
		{
			sb.Append(outputRootPath).AppendLine(":");
			sb.AppendLine();
			sb.Append("├── ").AppendLine(outputRootName);
			AppendAscii(root, "│   ", sb);
		}
		else
		{
			sb.AppendLine(outputRootName);
			AppendAscii(root, string.Empty, sb);
		}

		return sb.ToString();
	}

	public ExportOutputMetrics CalculateFullTreeMetrics(
		string rootPath,
		TreeNodeDescriptor root,
		TreeTextFormat format,
		string? displayRootPath = null,
		string? displayRootName = null)
	{
		ValidateFormat(format);
		if (format != TreeTextFormat.Ascii)
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
		ValidateFormat(format);
		var includedPaths = new HashSet<string>(PathComparer.Default);
		if (!CollectIncludedPaths(root, selectedPaths, includedPaths))
			return string.Empty;

		return BuildSelectedTreeFromIncludedPaths(
			rootPath,
			root,
			includedPaths,
			format,
			displayRootPath,
			displayRootName);
	}

	private static string BuildSelectedTreeFromIncludedPaths(
		string rootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> includedPaths,
		TreeTextFormat format,
		string? displayRootPath,
		string? displayRootName)
	{
		var outputRootPath = string.IsNullOrWhiteSpace(displayRootPath) ? rootPath : displayRootPath;
		var outputRootName = ResolveRootDisplayName(root, displayRootName);

		return format switch
		{
			TreeTextFormat.Ascii =>
				BuildSelectedTreeAscii(outputRootPath, outputRootName, root, includedPaths),
			TreeTextFormat.Json => BuildSelectedTreeJson(outputRootPath, root, includedPaths),
			TreeTextFormat.Xml => BuildSelectedTreeXml(outputRootPath, root, includedPaths),
			TreeTextFormat.Markdown => BuildSelectedTreeMarkdown(outputRootPath, root, includedPaths),
			_ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
		};
	}

	private static string BuildSelectedTreeAscii(
		string outputRootPath,
		string outputRootName,
		TreeNodeDescriptor root,
		IReadOnlySet<string> includedPaths)
	{
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
		ValidateFormat(format);
		var includedPaths = new HashSet<string>(PathComparer.Default);
		if (!CollectIncludedPaths(root, selectedPaths, includedPaths))
			return ExportOutputMetrics.Empty;

		if (format != TreeTextFormat.Ascii)
			return ExportOutputMetricsCalculator.FromText(
				BuildSelectedTreeFromIncludedPaths(
					rootPath,
					root,
					includedPaths,
					format,
					displayRootPath,
					displayRootName));

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

	private static void ValidateFormat(TreeTextFormat format)
	{
		if (format is not (
			    TreeTextFormat.Ascii or
			    TreeTextFormat.Json or
			    TreeTextFormat.Xml or
			    TreeTextFormat.Markdown))
		{
			throw new ArgumentOutOfRangeException(nameof(format), format, null);
		}
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

	private static async Task WriteFullTreeTextAsync(
		TextWriter destination,
		string rootPath,
		TreeNodeDescriptor root,
		string? displayRootPath,
		string? displayRootName,
		bool includeRootPath,
		bool includeFinalLineEnding,
		bool plain,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(destination);
		ArgumentNullException.ThrowIfNull(root);
		cancellationToken.ThrowIfCancellationRequested();

		var outputRootPath = string.IsNullOrWhiteSpace(displayRootPath)
			? rootPath
			: displayRootPath;
		var outputRootName = ResolveRootDisplayName(root, displayRootName);
		var output = new TreeTextLineWriter(destination, cancellationToken);
		var ancestorBranches = new List<bool>();

		if (includeRootPath)
		{
			await output.BeginLineAsync().ConfigureAwait(false);
			await output.WriteAsync(outputRootPath).ConfigureAwait(false);
			await output.WriteAsync(":").ConfigureAwait(false);
			await output.BeginLineAsync().ConfigureAwait(false);
			await output.BeginLineAsync().ConfigureAwait(false);
			await output.WriteAsync(plain ? PlainBranchMiddle : BranchMiddle)
				.ConfigureAwait(false);
			await output.WriteAsync(outputRootName).ConfigureAwait(false);
			ancestorBranches.Add(false);
		}
		else
		{
			await output.BeginLineAsync().ConfigureAwait(false);
			await output.WriteAsync(outputRootName).ConfigureAwait(false);
		}

		// Frames retain only the current directory chain, so a wide tree cannot
		// turn traversal state into another full-document allocation.
		var frames = new List<TreeTraversalFrame> { new(root) };
		while (frames.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var frame = frames[^1];
			if (frame.NextChildIndex >= frame.Node.Children.Count)
			{
				frames.RemoveAt(frames.Count - 1);
				if (frames.Count > 0)
					ancestorBranches.RemoveAt(ancestorBranches.Count - 1);
				continue;
			}

			var childIndex = frame.NextChildIndex++;
			var child = frame.Node.Children[childIndex];
			var isLast = childIndex == frame.Node.Children.Count - 1;
			await output.BeginLineAsync().ConfigureAwait(false);
			foreach (var ancestorIsLast in ancestorBranches)
			{
				await output.WriteAsync(
						ancestorIsLast
							? IndentSpace
							: plain
								? PlainIndentPipe
								: IndentPipe)
					.ConfigureAwait(false);
			}
			await output.WriteAsync(
					isLast
						? plain ? PlainBranchLast : BranchLast
						: plain ? PlainBranchMiddle : BranchMiddle)
				.ConfigureAwait(false);
			await output.WriteAsync(child.DisplayName).ConfigureAwait(false);

			if (child.Children.Count == 0)
				continue;

			ancestorBranches.Add(isLast);
			frames.Add(new TreeTraversalFrame(child));
		}

		await output.CompleteAsync(includeFinalLineEnding).ConfigureAwait(false);
	}

	private static int FindLongestCharacterRun(string value, char target)
	{
		var longest = 0;
		var current = 0;
		foreach (var character in value)
		{
			if (character == target)
			{
				current++;
				longest = Math.Max(longest, current);
			}
			else
			{
				current = 0;
			}
		}

		return longest;
	}

	private sealed class TreeTraversalFrame(TreeNodeDescriptor node)
	{
		public TreeNodeDescriptor Node { get; } = node;
		public int NextChildIndex { get; set; }
	}

	private sealed class TreeTextLineWriter(
		TextWriter destination,
		CancellationToken cancellationToken)
	{
		private bool _hasLine;

		public async ValueTask BeginLineAsync()
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (_hasLine)
			{
				await destination.WriteAsync(
						Environment.NewLine.AsMemory(),
						cancellationToken)
					.ConfigureAwait(false);
			}
			_hasLine = true;
		}

		public async ValueTask WriteAsync(string value)
		{
			for (var offset = 0; offset < value.Length;)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var length = Math.Min(
					MaximumTreeTextWriteCharacters,
					value.Length - offset);
				await destination.WriteAsync(
						value.AsMemory(offset, length),
						cancellationToken)
					.ConfigureAwait(false);
				offset += length;
			}
		}

		public async ValueTask CompleteAsync(bool includeFinalLineEnding)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!includeFinalLineEnding || !_hasLine)
				return;

			await destination.WriteAsync(
					Environment.NewLine.AsMemory(),
					cancellationToken)
				.ConfigureAwait(false);
		}
	}

	private static void AppendPlain(
		TreeNodeDescriptor node,
		string indent,
		StringBuilder output)
	{
		for (var index = 0; index < node.Children.Count; index++)
		{
			var child = node.Children[index];
			var isLast = index == node.Children.Count - 1;
			output
				.Append(indent)
				.Append(isLast ? PlainBranchLast : PlainBranchMiddle)
				.AppendLine(child.DisplayName);
			if (child.Children.Count == 0)
				continue;

			AppendPlain(
				child,
				indent + (isLast ? IndentSpace : PlainIndentPipe),
				output);
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

	private static string BuildFullTreeXml(
		string localRootPath,
		TreeNodeDescriptor root)
	{
		return BuildXmlDocument(localRootPath, root, includedPaths: null);
	}

	private static string BuildSelectedTreeXml(
		string localRootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> includedPaths)
	{
		if (!includedPaths.Contains(root.FullPath) &&
		    !HasIncludedChild(root.Children, includedPaths))
		{
			return string.Empty;
		}

		return BuildXmlDocument(localRootPath, root, includedPaths);
	}

	private static string BuildFullTreeMarkdown(
		string localRootPath,
		TreeNodeDescriptor root)
	{
		return BuildMarkdownDocument(localRootPath, root, includedPaths: null);
	}

	private static string BuildNamedTreeJson(
		string rootName,
		TreeNodeDescriptor root)
	{
		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer, JsonWriterOptions))
		{
			writer.WriteStartObject();
			writer.WritePropertyName(rootName);
			WriteJsonTreeContents(writer, root, includedPaths: null);
			writer.WriteEndObject();
			writer.Flush();
		}

		return Encoding.UTF8.GetString(buffer.WrittenSpan);
	}

	private static string BuildNamedTreeXml(
		string rootName,
		TreeNodeDescriptor root)
	{
		var output = new StringBuilder();
		using (var writer = XmlWriter.Create(output, XmlWriterSettings))
		{
			writer.WriteStartElement("d");
			writer.WriteAttributeString("n", rootName);
			WriteXmlTreeContents(writer, root, includedPaths: null);
			writer.WriteEndElement();
		}

		return output.ToString();
	}

	private static string BuildNamedTreeMarkdown(
		string rootName,
		TreeNodeDescriptor root)
	{
		var output = new StringBuilder();
		AppendMarkdownItem(output, level: 0, rootName, root.IsDirectory);
		if (root.IsDirectory)
			AppendMarkdownChildren(output, root.Children, includedPaths: null, level: 1);
		return output.ToString();
	}

	private static string BuildSelectedTreeMarkdown(
		string localRootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> includedPaths)
	{
		if (!includedPaths.Contains(root.FullPath) &&
		    !HasIncludedChild(root.Children, includedPaths))
		{
			return string.Empty;
		}

		return BuildMarkdownDocument(localRootPath, root, includedPaths);
	}

	private static string BuildJsonDocument(
		string localRootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string>? includedPaths)
	{
		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer, JsonWriterOptions))
		{
			writer.WriteStartObject();
			writer.WriteString("rootPath", ResolveStructuredRootPath(localRootPath));
			writer.WritePropertyName("tree");
			WriteJsonTreeContents(writer, root, includedPaths);
			writer.WriteEndObject();
			writer.Flush();
		}

		return Encoding.UTF8.GetString(buffer.WrittenSpan);
	}

	private static string BuildXmlDocument(
		string localRootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string>? includedPaths)
	{
		var sb = new StringBuilder();
		using (var writer = XmlWriter.Create(sb, XmlWriterSettings))
		{
			writer.WriteStartElement("t");
			writer.WriteAttributeString("r", ResolveStructuredRootPath(localRootPath));
			WriteXmlTreeContents(writer, root, includedPaths);
			writer.WriteEndElement();
		}

		return sb.ToString();
	}

	private static string BuildMarkdownDocument(
		string localRootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string>? includedPaths)
	{
		var sb = new StringBuilder();
		sb.Append("Root: ").AppendLine(ResolveStructuredRootPath(localRootPath));
		sb.AppendLine();
		WriteMarkdownTreeContents(sb, root, includedPaths);
		return sb.ToString();
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
		var orderedChildren = GetOrderedStructuredChildren(children, includedPaths);
		WriteJsonDirectoryProperties(writer, orderedChildren, includedPaths);
		WriteJsonCurrentFolderFilesIfAny(writer, orderedChildren);
	}

	private static void WriteJsonDirectoryValue(
		Utf8JsonWriter writer,
		TreeNodeDescriptor directory,
		IReadOnlySet<string>? includedPaths)
	{
		var orderedChildren = GetOrderedStructuredChildren(directory.Children, includedPaths);
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

	private static void WriteXmlTreeContents(
		XmlWriter writer,
		TreeNodeDescriptor root,
		IReadOnlySet<string>? includedPaths)
	{
		if (root.IsDirectory)
		{
			WriteXmlChildren(writer, root.Children, includedPaths);
			return;
		}

		if (includedPaths is null || includedPaths.Contains(root.FullPath))
			WriteXmlFile(writer, root);
	}

	private static void WriteXmlChildren(
		XmlWriter writer,
		IReadOnlyList<TreeNodeDescriptor> children,
		IReadOnlySet<string>? includedPaths)
	{
		var orderedChildren = GetOrderedStructuredChildren(children, includedPaths);
		foreach (var child in orderedChildren)
		{
			if (child.IsDirectory)
				WriteXmlDirectory(writer, child, includedPaths);
			else
				WriteXmlFile(writer, child);
		}
	}

	private static void WriteXmlDirectory(
		XmlWriter writer,
		TreeNodeDescriptor directory,
		IReadOnlySet<string>? includedPaths)
	{
		writer.WriteStartElement("d");
		writer.WriteAttributeString("n", directory.DisplayName);
		WriteXmlChildren(writer, directory.Children, includedPaths);
		writer.WriteEndElement();
	}

	private static void WriteXmlFile(XmlWriter writer, TreeNodeDescriptor file)
	{
		writer.WriteStartElement("f");
		writer.WriteString(file.DisplayName);
		writer.WriteEndElement();
	}

	private static void WriteMarkdownTreeContents(
		StringBuilder sb,
		TreeNodeDescriptor root,
		IReadOnlySet<string>? includedPaths)
	{
		if (root.IsDirectory)
		{
			AppendMarkdownChildren(sb, root.Children, includedPaths, level: 0);
			return;
		}

		if (includedPaths is null || includedPaths.Contains(root.FullPath))
			AppendMarkdownItem(sb, level: 0, root.DisplayName, isDirectory: false);
	}

	private static void AppendMarkdownChildren(
		StringBuilder sb,
		IReadOnlyList<TreeNodeDescriptor> children,
		IReadOnlySet<string>? includedPaths,
		int level)
	{
		var orderedChildren = GetOrderedStructuredChildren(children, includedPaths);
		foreach (var child in orderedChildren)
		{
			AppendMarkdownItem(sb, level, child.DisplayName, child.IsDirectory);
			if (child.IsDirectory)
				AppendMarkdownChildren(sb, child.Children, includedPaths, level + 1);
		}
	}

	private static void AppendMarkdownItem(StringBuilder sb, int level, string name, bool isDirectory)
	{
		sb.Append(' ', level * 2);
		sb.Append("- ");
		sb.Append(EscapeMarkdownListText(name));
		if (isDirectory)
			sb.Append('/');
		sb.AppendLine();
	}

	private static string EscapeMarkdownListText(string name)
	{
		if (string.IsNullOrEmpty(name))
			return name;

		var sanitized = name.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
		return sanitized[0] is '-' or '*' or '+' or '['
			? "\\" + sanitized
			: sanitized;
	}

	private static IReadOnlyList<TreeNodeDescriptor> GetOrderedStructuredChildren(
		IReadOnlyList<TreeNodeDescriptor> children,
		IReadOnlySet<string>? includedPaths)
	{
		if (includedPaths is null && IsStructuredTreeOrder(children))
		{
			// Inventory projection already establishes this order for normal trees. Reusing
			// the immutable child view avoids one list allocation and one sort per directory
			// for every JSON, XML, and Markdown render while retaining a defensive fallback
			// for synthetic/custom descriptors.
			return children;
		}

		var ordered = new List<TreeNodeDescriptor>(children.Count);
		foreach (var child in children)
		{
			if (includedPaths is null || includedPaths.Contains(child.FullPath))
				ordered.Add(child);
		}

		if (!IsStructuredTreeOrder(ordered))
			ordered.Sort(CompareStructuredTreeNodes);

		return ordered;
	}

	private static bool IsStructuredTreeOrder(IReadOnlyList<TreeNodeDescriptor> children)
	{
		for (var index = 1; index < children.Count; index++)
		{
			if (CompareStructuredTreeNodes(children[index - 1], children[index]) > 0)
				return false;
		}

		return true;
	}

	private static int CompareStructuredTreeNodes(TreeNodeDescriptor left, TreeNodeDescriptor right)
	{
		if (left.IsDirectory != right.IsDirectory)
			return left.IsDirectory ? -1 : 1;

		var comparison = StructuredTreeNameComparer.Compare(left.DisplayName, right.DisplayName);
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

	private static string ResolveStructuredRootPath(string localRootPath)
	{
		if (IsAbsoluteDisplayUri(localRootPath))
			return NormalizeStructuredPath(localRootPath.TrimEnd('/'));

		try
		{
			return NormalizeStructuredPath(Path.GetFullPath(localRootPath));
		}
		catch
		{
			return NormalizeStructuredPath(localRootPath);
		}
	}

	private static bool IsAbsoluteDisplayUri(string value)
		=> Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile;

	private static string NormalizeStructuredPath(string path) => PathUtility.NormalizeSeparators(path);
}
