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
		Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
		MaxDepth = int.MaxValue
	};

	private static readonly XmlWriterSettings XmlWriterSettings = new()
	{
		OmitXmlDeclaration = true,
		Indent = true
	};

	// Reuse indent segments to avoid allocating one prefix per rendered tree node.
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
		var outputRootPath = EscapeTextValue(string.IsNullOrWhiteSpace(displayRootPath)
			? rootPath
			: displayRootPath);
		var outputRootName = EscapeTextValue(ResolveRootDisplayName(root, displayRootName));
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
		outputRootPath = EscapeTextValue(outputRootPath);
		outputRootName = EscapeTextValue(outputRootName);
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
		outputRootPath = EscapeTextValue(outputRootPath);
		outputRootName = EscapeTextValue(outputRootName);
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
		var pending = new Stack<TreeNodeDescriptor>();
		pending.Push(node);
		while (pending.Count > 0)
		{
			var current = pending.Pop();
			if (selectedPaths.Contains(current.FullPath))
				return true;

			for (var index = current.Children.Count - 1; index >= 0; index--)
				pending.Push(current.Children[index]);
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
		=> AppendAsciiCore(node, includedPaths: null, indent, sb, plain: false);

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

		var outputRootPath = EscapeTextValue(string.IsNullOrWhiteSpace(displayRootPath)
			? rootPath
			: displayRootPath);
		var outputRootName = EscapeTextValue(ResolveRootDisplayName(root, displayRootName));
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
			await output.WriteAsync(EscapeTextValue(child.DisplayName)).ConfigureAwait(false);

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

	private readonly record struct AsciiTreeWriteOperation(
		TreeNodeDescriptor Node,
		string Indent,
		bool IsLast);

	private readonly record struct AsciiMetricOperation(
		TreeNodeDescriptor Node,
		int IndentLength,
		bool IsLast);

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
		=> AppendAsciiCore(node, includedPaths: null, indent, output, plain: true);

	private static void AppendSelectedAscii(TreeNodeDescriptor node, IReadOnlySet<string> selectedPaths, string indent, StringBuilder sb)
		=> AppendAsciiCore(node, selectedPaths, indent, sb, plain: false);

	private static void AppendAsciiCore(
		TreeNodeDescriptor node,
		IReadOnlySet<string>? includedPaths,
		string indent,
		StringBuilder output,
		bool plain)
	{
		var pending = new Stack<AsciiTreeWriteOperation>();
		PushAsciiChildren(pending, node, includedPaths, indent);
		while (pending.TryPop(out var operation))
		{
			output
				.Append(operation.Indent)
				.Append(operation.IsLast
					? plain ? PlainBranchLast : BranchLast
					: plain ? PlainBranchMiddle : BranchMiddle)
				.AppendLine(EscapeTextValue(operation.Node.DisplayName));
			if (operation.Node.Children.Count == 0)
				continue;

			var nextIndent = string.Concat(
				operation.Indent,
				operation.IsLast
					? IndentSpace
					: plain ? PlainIndentPipe : IndentPipe);
			PushAsciiChildren(pending, operation.Node, includedPaths, nextIndent);
		}
	}

	private static void PushAsciiChildren(
		Stack<AsciiTreeWriteOperation> pending,
		TreeNodeDescriptor parent,
		IReadOnlySet<string>? includedPaths,
		string indent)
	{
		var isLast = true;
		for (var index = parent.Children.Count - 1; index >= 0; index--)
		{
			var child = parent.Children[index];
			if (includedPaths is not null && !includedPaths.Contains(child.FullPath))
				continue;

			pending.Push(new AsciiTreeWriteOperation(child, indent, isLast));
			isLast = false;
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
		sb.Append("Root: ").AppendLine(EscapeTextValue(ResolveStructuredRootPath(localRootPath)));
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
		var operations = new Stack<JsonTreeWriteOperation>();
		PushJsonDirectoryContents(operations, orderedChildren);

		while (operations.TryPop(out var operation))
		{
			switch (operation.Kind)
			{
				case JsonTreeWriteOperationKind.Directory:
				{
					var directory = operation.Directory!;
					writer.WritePropertyName(directory.DisplayName);
					var directoryChildren = GetOrderedStructuredChildren(
						directory.Children,
						includedPaths);
					if (!HasDirectoryChild(directoryChildren))
					{
						WriteJsonFileArray(writer, directoryChildren);
						break;
					}

					writer.WriteStartObject();
					operations.Push(new JsonTreeWriteOperation(
						JsonTreeWriteOperationKind.EndObject));
					PushJsonDirectoryContents(operations, directoryChildren);
					break;
				}
				case JsonTreeWriteOperationKind.Files:
					WriteJsonCurrentFolderFiles(writer, operation.Children!);
					break;
				case JsonTreeWriteOperationKind.EndObject:
					writer.WriteEndObject();
					break;
				default:
					throw new InvalidOperationException("Unsupported JSON tree write operation.");
			}
		}
	}

	private static void PushJsonDirectoryContents(
		Stack<JsonTreeWriteOperation> operations,
		IReadOnlyList<TreeNodeDescriptor> orderedChildren)
	{
		if (HasFileChild(orderedChildren))
		{
			operations.Push(new JsonTreeWriteOperation(
				JsonTreeWriteOperationKind.Files,
				Children: orderedChildren));
		}

		for (var index = orderedChildren.Count - 1; index >= 0; index--)
		{
			var child = orderedChildren[index];
			if (!child.IsDirectory)
				continue;

			operations.Push(new JsonTreeWriteOperation(
				JsonTreeWriteOperationKind.Directory,
				Directory: child));
		}
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
		if (!root.IsDirectory)
		{
			if (includedPaths is null || includedPaths.Contains(root.FullPath))
				WriteXmlFile(writer, root);
			return;
		}

		var operations = new Stack<XmlTreeWriteOperation>();
		PushXmlChildren(
			operations,
			GetOrderedStructuredChildren(root.Children, includedPaths));

		while (operations.TryPop(out var operation))
		{
			if (operation.IsEndElement)
			{
				writer.WriteEndElement();
				continue;
			}

			var node = operation.Node!;
			if (!node.IsDirectory)
			{
				WriteXmlFile(writer, node);
				continue;
			}

			writer.WriteStartElement("d");
			writer.WriteAttributeString("n", node.DisplayName);
			operations.Push(XmlTreeWriteOperation.EndElement);
			PushXmlChildren(
				operations,
				GetOrderedStructuredChildren(node.Children, includedPaths));
		}
	}

	private static void PushXmlChildren(
		Stack<XmlTreeWriteOperation> operations,
		IReadOnlyList<TreeNodeDescriptor> orderedChildren)
	{
		for (var index = orderedChildren.Count - 1; index >= 0; index--)
			operations.Push(new XmlTreeWriteOperation(orderedChildren[index]));
	}

	private static void WriteXmlFile(XmlWriter writer, TreeNodeDescriptor file)
	{
		writer.WriteStartElement("f");
		writer.WriteString(file.DisplayName);
		writer.WriteEndElement();
	}

	private enum JsonTreeWriteOperationKind
	{
		Directory,
		Files,
		EndObject
	}

	private readonly record struct JsonTreeWriteOperation(
		JsonTreeWriteOperationKind Kind,
		TreeNodeDescriptor? Directory = null,
		IReadOnlyList<TreeNodeDescriptor>? Children = null);

	private readonly record struct XmlTreeWriteOperation(
		TreeNodeDescriptor? Node,
		bool IsEndElement = false)
	{
		public static XmlTreeWriteOperation EndElement { get; } = new(null, true);
	}

	private readonly record struct MarkdownTreeWriteOperation(
		TreeNodeDescriptor Node,
		int Level);

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
		var pending = new Stack<MarkdownTreeWriteOperation>();
		PushMarkdownChildren(pending, children, includedPaths, level);
		while (pending.TryPop(out var operation))
		{
			var node = operation.Node;
			AppendMarkdownItem(sb, operation.Level, node.DisplayName, node.IsDirectory);
			if (node.IsDirectory)
				PushMarkdownChildren(pending, node.Children, includedPaths, operation.Level + 1);
		}
	}

	private static void PushMarkdownChildren(
		Stack<MarkdownTreeWriteOperation> pending,
		IReadOnlyList<TreeNodeDescriptor> children,
		IReadOnlySet<string>? includedPaths,
		int level)
	{
		var orderedChildren = GetOrderedStructuredChildren(children, includedPaths);
		for (var index = orderedChildren.Count - 1; index >= 0; index--)
			pending.Push(new MarkdownTreeWriteOperation(orderedChildren[index], level));
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

		var sanitized = EscapeTextValue(name);
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
		if (selectedPaths.Contains(node.FullPath))
		{
			CollectSubtreePaths(node, includedPaths);
			return true;
		}

		var pending = new Stack<IncludedPathFrame>();
		pending.Push(new IncludedPathFrame(node));
		while (pending.TryPeek(out var frame))
		{
			if (frame.NextChildIndex < frame.Node.Children.Count)
			{
				var child = frame.Node.Children[frame.NextChildIndex++];
				if (selectedPaths.Contains(child.FullPath))
				{
					// Selecting a directory includes its complete subtree without realizing UI nodes.
					CollectSubtreePaths(child, includedPaths);
					frame.HasIncludedChild = true;
					continue;
				}

				pending.Push(new IncludedPathFrame(child));
				continue;
			}

			pending.Pop();
			if (!frame.HasIncludedChild)
				continue;

			includedPaths.Add(frame.Node.FullPath);
			if (pending.TryPeek(out var parent))
				parent.HasIncludedChild = true;
			else
				return true;
		}

		return false;
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

	private sealed class IncludedPathFrame(TreeNodeDescriptor node)
	{
		public TreeNodeDescriptor Node { get; } = node;
		public int NextChildIndex { get; set; }
		public bool HasIncludedChild { get; set; }
	}

	private static ExportOutputMetrics CalculateAsciiFullTreeMetrics(
		string outputRootPath,
		TreeNodeDescriptor root,
		string outputRootName)
	{
		long chars = 0;
		long lineBreaks = 0;

		AppendAsciiLineMetrics(
			(long)EscapeTextValue(outputRootPath).Length + 1,
			ref chars,
			ref lineBreaks); // "<rootPath>:"
		AppendAsciiLineMetrics(0, ref chars, ref lineBreaks); // blank separator line
		AppendAsciiLineMetrics(
			(long)BranchMiddle.Length + EscapeTextValue(outputRootName).Length,
			ref chars,
			ref lineBreaks);
		AppendFullAsciiChildMetrics(root, IndentPipe.Length, ref chars, ref lineBreaks);

		return CreateMetricsFromNormalizedCounts(chars, lineBreaks);
	}

	private static ExportOutputMetrics CalculateAsciiSelectedTreeMetrics(
		string outputRootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> includedPaths,
		string outputRootName)
	{
		long chars = 0;
		long lineBreaks = 0;

		AppendAsciiLineMetrics(
			(long)EscapeTextValue(outputRootPath).Length + 1,
			ref chars,
			ref lineBreaks); // "<rootPath>:"
		AppendAsciiLineMetrics(0, ref chars, ref lineBreaks); // blank separator line
		AppendAsciiLineMetrics(
			(long)BranchMiddle.Length + EscapeTextValue(outputRootName).Length,
			ref chars,
			ref lineBreaks);
		AppendSelectedAsciiChildMetrics(root, includedPaths, IndentPipe.Length, ref chars, ref lineBreaks);

		return CreateMetricsFromNormalizedCounts(chars, lineBreaks);
	}

	private static void AppendFullAsciiChildMetrics(
		TreeNodeDescriptor node,
		int indentLength,
		ref long chars,
		ref long lineBreaks)
		=> AppendAsciiChildMetrics(node, includedPaths: null, indentLength, ref chars, ref lineBreaks);

	private static void AppendSelectedAsciiChildMetrics(
		TreeNodeDescriptor node,
		IReadOnlySet<string> includedPaths,
		int indentLength,
		ref long chars,
		ref long lineBreaks)
		=> AppendAsciiChildMetrics(node, includedPaths, indentLength, ref chars, ref lineBreaks);

	private static void AppendAsciiChildMetrics(
		TreeNodeDescriptor node,
		IReadOnlySet<string>? includedPaths,
		int indentLength,
		ref long chars,
		ref long lineBreaks)
	{
		var pending = new Stack<AsciiMetricOperation>();
		PushAsciiMetricChildren(pending, node, includedPaths, indentLength);
		while (pending.TryPop(out var operation))
		{
			AppendAsciiLineMetrics(
				(long)operation.IndentLength +
				(operation.IsLast ? BranchLast.Length : BranchMiddle.Length) +
				EscapeTextValue(operation.Node.DisplayName).Length,
				ref chars,
				ref lineBreaks);
			if (operation.Node.Children.Count > 0)
			{
				PushAsciiMetricChildren(
					pending,
					operation.Node,
					includedPaths,
					operation.IndentLength + IndentPipe.Length);
			}
		}
	}

	private static void PushAsciiMetricChildren(
		Stack<AsciiMetricOperation> pending,
		TreeNodeDescriptor parent,
		IReadOnlySet<string>? includedPaths,
		int indentLength)
	{
		var isLast = true;
		for (var index = parent.Children.Count - 1; index >= 0; index--)
		{
			var child = parent.Children[index];
			if (includedPaths is not null && !includedPaths.Contains(child.FullPath))
				continue;

			pending.Push(new AsciiMetricOperation(child, indentLength, isLast));
			isLast = false;
		}
	}

	private static void AppendAsciiLineMetrics(long renderedChars, ref long chars, ref long lineBreaks)
	{
		chars += renderedChars + 1; // Normalize any platform newline to a single logical line-break char.
		lineBreaks++;
	}

	private static ExportOutputMetrics CreateMetricsFromNormalizedCounts(long chars, long lineBreaks)
	{
		if (chars <= 0)
			return ExportOutputMetrics.Empty;

		var lines = lineBreaks + 1;
		var tokens = (chars / 4) + (chars % 4 == 0 ? 0 : 1);
		return new ExportOutputMetrics(lines, chars, tokens);
	}

	private static string ResolveRootDisplayName(TreeNodeDescriptor root, string? displayRootName)
		=> string.IsNullOrWhiteSpace(displayRootName) ? root.DisplayName : displayRootName;

	private static string EscapeTextValue(string value) => SingleLineTextEscaping.Escape(value);

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
