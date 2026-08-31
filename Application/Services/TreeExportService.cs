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
	private const int StructuredTreeFlushNodeInterval = 512;

	public string BuildFullTree(string rootPath, TreeNodeDescriptor root)
		=> BuildFullTree(rootPath, root, TreeTextFormat.Ascii);

	public string BuildFullTree(
		string rootPath,
		TreeNodeDescriptor root,
		TreeTextFormat format,
		string? displayRootPath = null,
		string? displayRootName = null,
		bool includeRootPath = true) =>
		BuildFullTreeWithCancellation(
			rootPath,
			root,
			format,
			displayRootPath,
			displayRootName,
			includeRootPath,
			CancellationToken.None);

	public string BuildFullTreeWithCancellation(
		string rootPath,
		TreeNodeDescriptor root,
		TreeTextFormat format,
		string? displayRootPath,
		string? displayRootName,
		bool includeRootPath,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ValidateFormat(format);
		var outputRootPath = string.IsNullOrWhiteSpace(displayRootPath) ? rootPath : displayRootPath;
		var outputRootName = ResolveRootDisplayName(root, displayRootName);

		return format switch
		{
			TreeTextFormat.Ascii =>
				BuildFullTreeAscii(outputRootPath, outputRootName, root, includeRootPath, cancellationToken),
			TreeTextFormat.Json => includeRootPath
				? BuildFullTreeJson(outputRootPath, root, cancellationToken)
				: BuildNamedTreeJson(outputRootName, root, cancellationToken),
			TreeTextFormat.Xml => includeRootPath
				? BuildFullTreeXml(outputRootPath, root, cancellationToken)
				: BuildNamedTreeXml(outputRootName, root, cancellationToken),
			TreeTextFormat.Markdown => includeRootPath
				? BuildFullTreeMarkdown(outputRootPath, root, cancellationToken)
				: BuildNamedTreeMarkdown(outputRootName, root, cancellationToken),
			_ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
		};
	}

	public string BuildFullTreePlain(
		string rootPath,
		TreeNodeDescriptor root,
		string? displayRootPath = null,
		string? displayRootName = null,
		bool includeRootPath = true) =>
		BuildFullTreePlainWithCancellation(
			rootPath,
			root,
			displayRootPath,
			displayRootName,
			includeRootPath,
			CancellationToken.None);

	public string BuildFullTreePlainWithCancellation(
		string rootPath,
		TreeNodeDescriptor root,
		string? displayRootPath,
		string? displayRootName,
		bool includeRootPath,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var outputRootPath = EscapeTextValue(string.IsNullOrWhiteSpace(displayRootPath)
			? rootPath
			: displayRootPath);
		var outputRootName = EscapeTextValue(ResolveRootDisplayName(root, displayRootName));
		var output = new StringBuilder();
		if (includeRootPath)
		{
			output.Append(outputRootPath).AppendLine(":");
			AppendPlain(root, string.Empty, output, cancellationToken);
		}
		else
		{
			output.AppendLine(outputRootName);
			AppendPlain(root, string.Empty, output, cancellationToken);
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

	public Task WriteFullTreeAsync(
		TextWriter destination,
		string rootPath,
		TreeNodeDescriptor root,
		TreeTextFormat format,
		string? displayRootPath = null,
		string? displayRootName = null,
		bool includeRootPath = true,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(destination);
		ArgumentNullException.ThrowIfNull(root);
		cancellationToken.ThrowIfCancellationRequested();
		ValidateFormat(format);

		return format switch
		{
			TreeTextFormat.Ascii => WriteFullTreeAsync(
				destination,
				rootPath,
				root,
				displayRootPath,
				displayRootName,
				includeRootPath,
				includeFinalLineEnding: true,
				cancellationToken),
			TreeTextFormat.Markdown => WriteFullTreeMarkdownAsync(
				destination,
				rootPath,
				root,
				displayRootPath,
				displayRootName,
				includeRootPath,
				cancellationToken),
			TreeTextFormat.Json => WriteFullTreeJsonAsync(
				destination,
				rootPath,
				root,
				displayRootPath,
				displayRootName,
				includeRootPath,
				cancellationToken),
			TreeTextFormat.Xml => WriteFullTreeXmlAsync(
				destination,
				rootPath,
				root,
				displayRootPath,
				displayRootName,
				includeRootPath,
				cancellationToken),
			_ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
		};
	}

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
		if (!includeRootPath)
		{
			longest = Math.Max(
				longest,
				FindLongestCharacterRun(ResolveRootDisplayName(root, displayRootName), '`'));
		}

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
		bool includeRootPath,
		CancellationToken cancellationToken)
	{
		outputRootPath = EscapeTextValue(outputRootPath);
		outputRootName = EscapeTextValue(outputRootName);
		var sb = new StringBuilder();
		if (includeRootPath)
		{
			sb.Append(outputRootPath).AppendLine(":");
			AppendAscii(root, string.Empty, sb, cancellationToken);
		}
		else
		{
			sb.AppendLine(outputRootName);
			AppendAscii(root, string.Empty, sb, cancellationToken);
		}

		return sb.ToString();
	}

	public ExportOutputMetrics CalculateFullTreeMetrics(
		string rootPath,
		TreeNodeDescriptor root,
		TreeTextFormat format,
		string? displayRootPath = null,
		string? displayRootName = null)
		=> CalculateFullTreeMetricsWithCancellation(
			rootPath,
			root,
			format,
			displayRootPath,
			displayRootName,
			CancellationToken.None);

	public ExportOutputMetrics CalculateFullTreeMetricsWithCancellation(
		string rootPath,
		TreeNodeDescriptor root,
		TreeTextFormat format,
		string? displayRootPath,
		string? displayRootName,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ValidateFormat(format);
		if (format == TreeTextFormat.Markdown)
		{
			var markdownRootPath = string.IsNullOrWhiteSpace(displayRootPath) ? rootPath : displayRootPath;
			return CalculateMarkdownTreeMetrics(
				markdownRootPath,
				root,
				includedPaths: null,
				cancellationToken);
		}

		if (format != TreeTextFormat.Ascii)
		{
			using var metricsWriter = ExportOutputMetricsCalculator.CreateTextWriter();
			WriteFullTreeAsync(
					metricsWriter,
					rootPath,
					root,
					format,
					displayRootPath,
					displayRootName,
					includeRootPath: true,
					cancellationToken)
				.GetAwaiter()
				.GetResult();
			return metricsWriter.Complete(cancellationToken);
		}

		var outputRootPath = string.IsNullOrWhiteSpace(displayRootPath) ? rootPath : displayRootPath;
		return CalculateAsciiFullTreeMetrics(
			outputRootPath,
			root,
			cancellationToken);
	}

	public string BuildSelectedTree(string rootPath, TreeNodeDescriptor root, IReadOnlySet<string> selectedPaths)
		=> BuildSelectedTree(rootPath, root, selectedPaths, TreeTextFormat.Ascii);

	public string BuildSelectedTree(
		string rootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths,
		TreeTextFormat format,
		string? displayRootPath = null,
		string? displayRootName = null) =>
		BuildSelectedTreeWithCancellation(
			rootPath,
			root,
			selectedPaths,
			format,
			displayRootPath,
			displayRootName,
			CancellationToken.None);

	public string BuildSelectedTreeWithCancellation(
		string rootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths,
		TreeTextFormat format,
		string? displayRootPath,
		string? displayRootName,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ValidateFormat(format);
		var includedPaths = new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);
		if (!CollectIncludedPaths(root, selectedPaths, includedPaths, cancellationToken))
			return string.Empty;

		return BuildSelectedTreeFromIncludedPaths(
			rootPath,
			root,
			includedPaths,
			format,
			displayRootPath,
			displayRootName,
			cancellationToken);
	}

	private static string BuildSelectedTreeFromIncludedPaths(
		string rootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> includedPaths,
		TreeTextFormat format,
		string? displayRootPath,
		string? displayRootName,
		CancellationToken cancellationToken)
	{
		var outputRootPath = string.IsNullOrWhiteSpace(displayRootPath) ? rootPath : displayRootPath;

		return format switch
		{
			TreeTextFormat.Ascii =>
				BuildSelectedTreeAscii(outputRootPath, root, includedPaths, cancellationToken),
			TreeTextFormat.Json => BuildSelectedTreeJson(outputRootPath, root, includedPaths, cancellationToken),
			TreeTextFormat.Xml => BuildSelectedTreeXml(outputRootPath, root, includedPaths, cancellationToken),
			TreeTextFormat.Markdown => BuildSelectedTreeMarkdown(outputRootPath, root, includedPaths, cancellationToken),
			_ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
		};
	}

	private static string BuildSelectedTreeAscii(
		string outputRootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> includedPaths,
		CancellationToken cancellationToken)
	{
		outputRootPath = EscapeTextValue(outputRootPath);
		var sb = new StringBuilder();
		sb.Append(outputRootPath).AppendLine(":");
		AppendSelectedAscii(root, includedPaths, string.Empty, sb, cancellationToken);

		return sb.ToString();
	}

	public ExportOutputMetrics CalculateSelectedTreeMetrics(
		string rootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths,
		TreeTextFormat format,
		string? displayRootPath = null,
		string? displayRootName = null)
		=> CalculateSelectedTreeMetricsWithCancellation(
			rootPath,
			root,
			selectedPaths,
			format,
			displayRootPath,
			displayRootName,
			CancellationToken.None);

	public ExportOutputMetrics CalculateSelectedTreeMetricsWithCancellation(
		string rootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths,
		TreeTextFormat format,
		string? displayRootPath,
		string? displayRootName,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ValidateFormat(format);
		var includedPaths = new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);
		if (!CollectIncludedPaths(root, selectedPaths, includedPaths, cancellationToken))
			return ExportOutputMetrics.Empty;

		var outputRootPath = string.IsNullOrWhiteSpace(displayRootPath) ? rootPath : displayRootPath;
		if (format == TreeTextFormat.Markdown)
		{
			return CalculateMarkdownTreeMetrics(
				outputRootPath,
				root,
				includedPaths,
				cancellationToken);
		}

		if (format != TreeTextFormat.Ascii)
		{
			using var metricsWriter = ExportOutputMetricsCalculator.CreateTextWriter();
			var writeTask = format switch
			{
				TreeTextFormat.Json => WriteTreeJsonAsync(
					metricsWriter,
					rootPath,
					root,
					outputRootPath,
					displayRootName,
					includeRootPath: true,
					includedPaths,
					cancellationToken),
				TreeTextFormat.Xml => WriteTreeXmlAsync(
					metricsWriter,
					rootPath,
					root,
					outputRootPath,
					displayRootName,
					includeRootPath: true,
					includedPaths,
					cancellationToken),
				_ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
			};
			writeTask.GetAwaiter().GetResult();
			return metricsWriter.Complete(cancellationToken);
		}

		return CalculateAsciiSelectedTreeMetrics(
			outputRootPath,
			root,
			includedPaths,
			cancellationToken);
	}

	public static bool HasSelectedDescendantOrSelf(
		TreeNodeDescriptor node,
		IReadOnlySet<string> selectedPaths) =>
		HasSelectedDescendantOrSelfWithCancellation(
			node,
			selectedPaths,
			CancellationToken.None);

	internal static bool HasSelectedDescendantOrSelfWithCancellation(
		TreeNodeDescriptor node,
		IReadOnlySet<string> selectedPaths,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(node);
		ArgumentNullException.ThrowIfNull(selectedPaths);
		cancellationToken.ThrowIfCancellationRequested();

		var pending = new Stack<TreeNodeDescriptor>();
		pending.Push(node);
		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var current = pending.Pop();
			if (selectedPaths.Contains(current.FullPath))
				return true;

			for (var index = current.Children.Count - 1; index >= 0; index--)
			{
				cancellationToken.ThrowIfCancellationRequested();
				pending.Push(current.Children[index]);
			}
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

	private static void AppendAscii(
		TreeNodeDescriptor node,
		string indent,
		StringBuilder sb,
		CancellationToken cancellationToken) =>
		AppendAsciiCore(node, includedPaths: null, indent, sb, plain: false, cancellationToken);

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
		using var output = new TreeTextLineWriter(destination, cancellationToken);
		var ancestorBranches = new List<bool>();

		if (includeRootPath)
		{
			await output.BeginLineAsync().ConfigureAwait(false);
			await output.WriteAsync(outputRootPath).ConfigureAwait(false);
			await output.WriteAsync(":").ConfigureAwait(false);
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

	private static async Task WriteFullTreeMarkdownAsync(
		TextWriter destination,
		string rootPath,
		TreeNodeDescriptor root,
		string? displayRootPath,
		string? displayRootName,
		bool includeRootPath,
		CancellationToken cancellationToken)
	{
		var outputRootPath = string.IsNullOrWhiteSpace(displayRootPath)
			? rootPath
			: displayRootPath;
		using var output = new TreeTextLineWriter(destination, cancellationToken);
		if (includeRootPath)
		{
			await output.BeginLineAsync().ConfigureAwait(false);
			await output.WriteAsync(
					ContextRootPresentation.FormatLine(ResolveStructuredRootPath(outputRootPath)))
				.ConfigureAwait(false);
			await output.BeginLineAsync().ConfigureAwait(false);
			if (root.IsDirectory)
			{
				await WriteMarkdownChildrenAsync(
						output,
						root.Children,
						level: 0,
						cancellationToken)
					.ConfigureAwait(false);
			}
			else
			{
				await WriteMarkdownItemAsync(
						output,
						level: 0,
						root.DisplayName,
						isDirectory: false)
					.ConfigureAwait(false);
			}
		}
		else
		{
			await WriteMarkdownItemAsync(
					output,
					level: 0,
					ResolveRootDisplayName(root, displayRootName),
					root.IsDirectory)
				.ConfigureAwait(false);
			if (root.IsDirectory)
			{
				await WriteMarkdownChildrenAsync(
						output,
						root.Children,
						level: 1,
						cancellationToken)
					.ConfigureAwait(false);
			}
		}

		await output.CompleteAsync(includeFinalLineEnding: true).ConfigureAwait(false);
	}

	private static async Task WriteMarkdownChildrenAsync(
		TreeTextLineWriter output,
		IReadOnlyList<TreeNodeDescriptor> children,
		int level,
		CancellationToken cancellationToken)
	{
		var pending = new Stack<MarkdownTreeWriteOperation>();
		PushMarkdownChildren(
			pending,
			children,
			includedPaths: null,
			level,
			cancellationToken);
		while (pending.TryPop(out var operation))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var node = operation.Node;
			await WriteMarkdownItemAsync(
					output,
					operation.Level,
					node.DisplayName,
					node.IsDirectory)
				.ConfigureAwait(false);
			if (node.IsDirectory)
			{
				PushMarkdownChildren(
					pending,
					node.Children,
					includedPaths: null,
					operation.Level + 1,
					cancellationToken);
			}
		}
	}

	private static async ValueTask WriteMarkdownItemAsync(
		TreeTextLineWriter output,
		int level,
		string name,
		bool isDirectory)
	{
		await output.BeginLineAsync().ConfigureAwait(false);
		await output.WriteRepeatedAsync(' ', checked(level * 2)).ConfigureAwait(false);
		await output.WriteAsync("- ").ConfigureAwait(false);
		await output.WriteAsync(EscapeMarkdownListText(name)).ConfigureAwait(false);
		if (isDirectory)
			await output.WriteAsync("/").ConfigureAwait(false);
	}

	private static Task WriteFullTreeJsonAsync(
		TextWriter destination,
		string rootPath,
		TreeNodeDescriptor root,
		string? displayRootPath,
		string? displayRootName,
		bool includeRootPath,
		CancellationToken cancellationToken) =>
		WriteTreeJsonAsync(
			destination,
			rootPath,
			root,
			displayRootPath,
			displayRootName,
			includeRootPath,
			includedPaths: null,
			cancellationToken);

	private static Task WriteTreeJsonAsync(
		TextWriter destination,
		string rootPath,
		TreeNodeDescriptor root,
		string? displayRootPath,
		string? displayRootName,
		bool includeRootPath,
		IReadOnlySet<string>? includedPaths,
		CancellationToken cancellationToken)
	{
		var outputRootPath = string.IsNullOrWhiteSpace(displayRootPath)
			? rootPath
			: displayRootPath;
		using var buffer = new TextWriterUtf8BufferWriter(destination, cancellationToken);
		using var writer = new Utf8JsonWriter(buffer, JsonWriterOptions);
		writer.WriteStartObject();
		if (includeRootPath)
		{
			writer.WriteString("rootPath", ResolveStructuredRootPath(outputRootPath));
			writer.WritePropertyName("tree");
		}
		else
		{
			writer.WritePropertyName(ResolveRootDisplayName(root, displayRootName));
		}

		writer.Flush();
		cancellationToken.ThrowIfCancellationRequested();
		var processedNodes = 0;
		WriteJsonTreeContents(
			writer,
			root,
			includedPaths,
			cancellationToken,
			() =>
			{
				if (++processedNodes % StructuredTreeFlushNodeInterval != 0)
					return;

				writer.Flush();
				cancellationToken.ThrowIfCancellationRequested();
			});
		writer.WriteEndObject();
		writer.Flush();
		buffer.Complete();
		return Task.CompletedTask;
	}

	private static Task WriteFullTreeXmlAsync(
		TextWriter destination,
		string rootPath,
		TreeNodeDescriptor root,
		string? displayRootPath,
		string? displayRootName,
		bool includeRootPath,
		CancellationToken cancellationToken) =>
		WriteTreeXmlAsync(
			destination,
			rootPath,
			root,
			displayRootPath,
			displayRootName,
			includeRootPath,
			includedPaths: null,
			cancellationToken);

	private static Task WriteTreeXmlAsync(
		TextWriter destination,
		string rootPath,
		TreeNodeDescriptor root,
		string? displayRootPath,
		string? displayRootName,
		bool includeRootPath,
		IReadOnlySet<string>? includedPaths,
		CancellationToken cancellationToken)
	{
		var outputRootPath = string.IsNullOrWhiteSpace(displayRootPath)
			? rootPath
			: displayRootPath;
		using var writer = XmlWriter.Create(destination, XmlWriterSettings);
		writer.WriteStartElement(includeRootPath ? "t" : "d");
		writer.WriteAttributeString(
			includeRootPath ? "r" : "n",
			XmlTextSanitizer.Sanitize(includeRootPath
				? ResolveStructuredRootPath(outputRootPath)
				: ResolveRootDisplayName(root, displayRootName)));
		writer.Flush();
		cancellationToken.ThrowIfCancellationRequested();
		var processedNodes = 0;
		WriteXmlTreeContents(
			writer,
			root,
			includedPaths,
			cancellationToken,
			() =>
			{
				if (++processedNodes % StructuredTreeFlushNodeInterval != 0)
					return;

				writer.Flush();
				cancellationToken.ThrowIfCancellationRequested();
			});
		writer.WriteEndElement();
		writer.Flush();
		return Task.CompletedTask;
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

	internal sealed class TreeTextLineWriter : IDisposable
	{
		private readonly TextWriter _destination;
		private readonly CancellationToken _cancellationToken;
		private char[] _buffer;
		private int _bufferedCharacters;
		private bool _hasLine;
		private bool _completed;
		private bool _disposed;

		public TreeTextLineWriter(
			TextWriter destination,
			CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(destination);
			_destination = destination;
			_cancellationToken = cancellationToken;
			_buffer = ArrayPool<char>.Shared.Rent(MaximumTreeTextWriteCharacters);
		}

		public async ValueTask BeginLineAsync()
		{
			ThrowIfNotWritable();
			_cancellationToken.ThrowIfCancellationRequested();
			if (_hasLine)
				await FlushCurrentLineAsync(includeLineEnding: true).ConfigureAwait(false);
			_hasLine = true;
		}

		public async ValueTask WriteAsync(string value)
		{
			ArgumentNullException.ThrowIfNull(value);
			ThrowIfNotWritable();
			for (var offset = 0; offset < value.Length;)
			{
				_cancellationToken.ThrowIfCancellationRequested();
				if (_bufferedCharacters == MaximumTreeTextWriteCharacters)
					await FlushBufferAsync().ConfigureAwait(false);

				var length = Math.Min(
					MaximumTreeTextWriteCharacters - _bufferedCharacters,
					value.Length - offset);
				value.AsSpan(offset, length).CopyTo(_buffer.AsSpan(_bufferedCharacters));
				_bufferedCharacters += length;
				offset += length;
			}
		}

		public async ValueTask WriteRepeatedAsync(char value, int count)
		{
			if (count <= 0)
				return;

			ThrowIfNotWritable();
			for (var remaining = count; remaining > 0;)
			{
				_cancellationToken.ThrowIfCancellationRequested();
				if (_bufferedCharacters == MaximumTreeTextWriteCharacters)
					await FlushBufferAsync().ConfigureAwait(false);

				var length = Math.Min(
					MaximumTreeTextWriteCharacters - _bufferedCharacters,
					remaining);
				_buffer.AsSpan(_bufferedCharacters, length).Fill(value);
				_bufferedCharacters += length;
				remaining -= length;
			}
		}

		public async ValueTask CompleteAsync(bool includeFinalLineEnding)
		{
			ThrowIfNotWritable();
			_cancellationToken.ThrowIfCancellationRequested();
			if (!_hasLine)
			{
				await FlushBufferAsync().ConfigureAwait(false);
				_completed = true;
				return;
			}

			await FlushCurrentLineAsync(includeFinalLineEnding).ConfigureAwait(false);
			_completed = true;
		}

		public void Dispose()
		{
			if (_disposed)
				return;

			ArrayPool<char>.Shared.Return(_buffer, clearArray: true);
			_buffer = [];
			_bufferedCharacters = 0;
			_disposed = true;
		}

		private async ValueTask FlushCurrentLineAsync(bool includeLineEnding)
		{
			if (includeLineEnding)
				await WriteAsync(Environment.NewLine).ConfigureAwait(false);
			await FlushBufferAsync().ConfigureAwait(false);
		}

		private async ValueTask FlushBufferAsync()
		{
			_cancellationToken.ThrowIfCancellationRequested();
			if (_bufferedCharacters == 0)
				return;

			await _destination.WriteAsync(
					_buffer.AsMemory(0, _bufferedCharacters),
					_cancellationToken)
				.ConfigureAwait(false);
			_bufferedCharacters = 0;
		}

		private void ThrowIfNotWritable()
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_completed)
				throw new InvalidOperationException("Tree text output was already finalized.");
		}
	}

	private static void AppendPlain(
		TreeNodeDescriptor node,
		string indent,
		StringBuilder output,
		CancellationToken cancellationToken) =>
		AppendAsciiCore(node, includedPaths: null, indent, output, plain: true, cancellationToken);

	private static void AppendSelectedAscii(
		TreeNodeDescriptor node,
		IReadOnlySet<string> selectedPaths,
		string indent,
		StringBuilder sb,
		CancellationToken cancellationToken) =>
		AppendAsciiCore(node, selectedPaths, indent, sb, plain: false, cancellationToken);

	private static void AppendAsciiCore(
		TreeNodeDescriptor node,
		IReadOnlySet<string>? includedPaths,
		string indent,
		StringBuilder output,
		bool plain,
		CancellationToken cancellationToken)
	{
		var pending = new Stack<AsciiTreeWriteOperation>();
		PushAsciiChildren(pending, node, includedPaths, indent, cancellationToken);
		while (pending.TryPop(out var operation))
		{
			cancellationToken.ThrowIfCancellationRequested();
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
			PushAsciiChildren(pending, operation.Node, includedPaths, nextIndent, cancellationToken);
		}
	}

	private static void PushAsciiChildren(
		Stack<AsciiTreeWriteOperation> pending,
		TreeNodeDescriptor parent,
		IReadOnlySet<string>? includedPaths,
		string indent,
		CancellationToken cancellationToken)
	{
		var isLast = true;
		for (var index = parent.Children.Count - 1; index >= 0; index--)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var child = parent.Children[index];
			if (includedPaths is not null && !includedPaths.Contains(child.FullPath))
				continue;

			pending.Push(new AsciiTreeWriteOperation(child, indent, isLast));
			isLast = false;
		}
	}

	private sealed class TextWriterUtf8BufferWriter(
		TextWriter destination,
		CancellationToken cancellationToken) : IBufferWriter<byte>, IDisposable
	{
		private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
		private byte[] _buffer = ArrayPool<byte>.Shared.Rent(MaximumTreeTextWriteCharacters);
		private bool _disposed;

		public void Advance(int count)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			ArgumentOutOfRangeException.ThrowIfNegative(count);
			ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer.Length);
			cancellationToken.ThrowIfCancellationRequested();
			if (count == 0)
				return;

			var characters = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(count));
			try
			{
				_decoder.Convert(
					_buffer.AsSpan(0, count),
					characters,
					flush: false,
					out var bytesUsed,
					out var charactersUsed,
					out _);
				if (bytesUsed != count)
					throw new InvalidOperationException("The UTF-8 output buffer could not be decoded completely.");
				destination.Write(characters.AsSpan(0, charactersUsed));
			}
			finally
			{
				ArrayPool<char>.Shared.Return(characters, clearArray: true);
			}
		}

		public Memory<byte> GetMemory(int sizeHint = 0)
		{
			EnsureCapacity(sizeHint);
			return _buffer;
		}

		public Span<byte> GetSpan(int sizeHint = 0)
		{
			EnsureCapacity(sizeHint);
			return _buffer;
		}

		public void Complete()
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			cancellationToken.ThrowIfCancellationRequested();
			Span<char> trailing = stackalloc char[2];
			_decoder.Convert(
				ReadOnlySpan<byte>.Empty,
				trailing,
				flush: true,
				out _,
				out var charactersUsed,
				out _);
			if (charactersUsed > 0)
				destination.Write(trailing[..charactersUsed]);
		}

		public void Dispose()
		{
			if (_disposed)
				return;

			_disposed = true;
			ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
			_buffer = [];
		}

		private void EnsureCapacity(int sizeHint)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
			var required = Math.Max(1, sizeHint);
			if (_buffer.Length >= required)
				return;

			var replacement = ArrayPool<byte>.Shared.Rent(required);
			ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
			_buffer = replacement;
		}
	}

	private static string BuildFullTreeJson(
		string localRootPath,
		TreeNodeDescriptor root,
		CancellationToken cancellationToken)
	{
		return BuildJsonDocument(localRootPath, root, includedPaths: null, cancellationToken);
	}

	private static string BuildSelectedTreeJson(
		string localRootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> includedPaths,
		CancellationToken cancellationToken)
	{
		if (!includedPaths.Contains(root.FullPath) &&
		    !HasIncludedChild(root.Children, includedPaths))
		{
			return string.Empty;
		}

		return BuildJsonDocument(localRootPath, root, includedPaths, cancellationToken);
	}

	private static string BuildFullTreeXml(
		string localRootPath,
		TreeNodeDescriptor root,
		CancellationToken cancellationToken)
	{
		return BuildXmlDocument(localRootPath, root, includedPaths: null, cancellationToken);
	}

	private static string BuildSelectedTreeXml(
		string localRootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> includedPaths,
		CancellationToken cancellationToken)
	{
		if (!includedPaths.Contains(root.FullPath) &&
		    !HasIncludedChild(root.Children, includedPaths))
		{
			return string.Empty;
		}

		return BuildXmlDocument(localRootPath, root, includedPaths, cancellationToken);
	}

	private static string BuildFullTreeMarkdown(
		string localRootPath,
		TreeNodeDescriptor root,
		CancellationToken cancellationToken)
	{
		return BuildMarkdownDocument(localRootPath, root, includedPaths: null, cancellationToken);
	}

	private static string BuildNamedTreeJson(
		string rootName,
		TreeNodeDescriptor root,
		CancellationToken cancellationToken)
	{
		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer, JsonWriterOptions))
		{
			writer.WriteStartObject();
			writer.WritePropertyName(rootName);
			WriteJsonTreeContents(writer, root, includedPaths: null, cancellationToken);
			writer.WriteEndObject();
			writer.Flush();
		}

		return Encoding.UTF8.GetString(buffer.WrittenSpan);
	}

	private static string BuildNamedTreeXml(
		string rootName,
		TreeNodeDescriptor root,
		CancellationToken cancellationToken)
	{
		var output = new StringBuilder();
		using (var writer = XmlWriter.Create(output, XmlWriterSettings))
		{
			writer.WriteStartElement("d");
			writer.WriteAttributeString("n", XmlTextSanitizer.Sanitize(rootName));
			WriteXmlTreeContents(writer, root, includedPaths: null, cancellationToken);
			writer.WriteEndElement();
		}

		return output.ToString();
	}

	private static string BuildNamedTreeMarkdown(
		string rootName,
		TreeNodeDescriptor root,
		CancellationToken cancellationToken)
	{
		var output = new StringBuilder();
		AppendMarkdownItem(output, level: 0, rootName, root.IsDirectory);
		if (root.IsDirectory)
			AppendMarkdownChildren(
				output,
				root.Children,
				includedPaths: null,
				level: 1,
				cancellationToken);
		return output.ToString();
	}

	private static string BuildSelectedTreeMarkdown(
		string localRootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> includedPaths,
		CancellationToken cancellationToken)
	{
		if (!includedPaths.Contains(root.FullPath) &&
		    !HasIncludedChild(root.Children, includedPaths))
		{
			return string.Empty;
		}

		return BuildMarkdownDocument(localRootPath, root, includedPaths, cancellationToken);
	}

	private static string BuildJsonDocument(
		string localRootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string>? includedPaths,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer, JsonWriterOptions))
		{
			writer.WriteStartObject();
			writer.WriteString("rootPath", ResolveStructuredRootPath(localRootPath));
			writer.WritePropertyName("tree");
			WriteJsonTreeContents(writer, root, includedPaths, cancellationToken);
			writer.WriteEndObject();
			writer.Flush();
		}

		return Encoding.UTF8.GetString(buffer.WrittenSpan);
	}

	private static string BuildXmlDocument(
		string localRootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string>? includedPaths,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var sb = new StringBuilder();
		using (var writer = XmlWriter.Create(sb, XmlWriterSettings))
		{
			writer.WriteStartElement("t");
			writer.WriteAttributeString(
				"r",
				XmlTextSanitizer.Sanitize(ResolveStructuredRootPath(localRootPath)));
			WriteXmlTreeContents(writer, root, includedPaths, cancellationToken);
			writer.WriteEndElement();
		}

		return sb.ToString();
	}

	private static string BuildMarkdownDocument(
		string localRootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string>? includedPaths,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var sb = new StringBuilder();
		sb.AppendLine(ContextRootPresentation.FormatLine(ResolveStructuredRootPath(localRootPath)));
		sb.AppendLine();
		WriteMarkdownTreeContents(sb, root, includedPaths, cancellationToken);
		return sb.ToString();
	}

	private static void WriteJsonTreeContents(
		Utf8JsonWriter writer,
		TreeNodeDescriptor root,
		IReadOnlySet<string>? includedPaths,
		CancellationToken cancellationToken,
		Action? nodeWritten = null)
	{
		writer.WriteStartObject();

		if (root.IsDirectory)
		{
			WriteJsonRootContents(
				writer,
				root.Children,
				includedPaths,
				cancellationToken,
				nodeWritten);
		}
		else if (includedPaths is null || includedPaths.Contains(root.FullPath))
		{
			WriteJsonRootFiles(writer, [root], cancellationToken, nodeWritten);
		}

		writer.WriteEndObject();
	}

	private static void WriteJsonRootContents(
		Utf8JsonWriter writer,
		IReadOnlyList<TreeNodeDescriptor> children,
		IReadOnlySet<string>? includedPaths,
		CancellationToken cancellationToken,
		Action? nodeWritten)
	{
		var orderedChildren = GetOrderedStructuredChildren(children, includedPaths, cancellationToken);
		var operations = new Stack<JsonTreeWriteOperation>();
		PushJsonDirectoryContents(operations, orderedChildren, cancellationToken);

		while (operations.TryPop(out var operation))
		{
			cancellationToken.ThrowIfCancellationRequested();
			switch (operation.Kind)
			{
				case JsonTreeWriteOperationKind.Directory:
				{
					var directory = operation.Directory!;
					writer.WritePropertyName(directory.DisplayName);
					nodeWritten?.Invoke();
					var directoryChildren = GetOrderedStructuredChildren(
						directory.Children,
						includedPaths,
						cancellationToken);
					if (!HasDirectoryChild(directoryChildren, cancellationToken))
					{
						WriteJsonFileArray(
							writer,
							directoryChildren,
							cancellationToken,
							nodeWritten);
						break;
					}

					writer.WriteStartObject();
					operations.Push(new JsonTreeWriteOperation(
						JsonTreeWriteOperationKind.EndObject));
					PushJsonDirectoryContents(operations, directoryChildren, cancellationToken);
					break;
				}
				case JsonTreeWriteOperationKind.Files:
					WriteJsonCurrentFolderFiles(
						writer,
						operation.Children!,
						cancellationToken,
						nodeWritten);
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
		IReadOnlyList<TreeNodeDescriptor> orderedChildren,
		CancellationToken cancellationToken)
	{
		if (HasFileChild(orderedChildren, cancellationToken))
		{
			operations.Push(new JsonTreeWriteOperation(
				JsonTreeWriteOperationKind.Files,
				Children: orderedChildren));
		}

		for (var index = orderedChildren.Count - 1; index >= 0; index--)
		{
			cancellationToken.ThrowIfCancellationRequested();
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
		IReadOnlyList<TreeNodeDescriptor> orderedChildren,
		CancellationToken cancellationToken,
		Action? nodeWritten)
	{
		writer.WritePropertyName("/");
		WriteJsonFileArray(writer, orderedChildren, cancellationToken, nodeWritten);
	}

	private static void WriteJsonRootFiles(
		Utf8JsonWriter writer,
		IReadOnlyList<TreeNodeDescriptor> files,
		CancellationToken cancellationToken,
		Action? nodeWritten = null)
	{
		writer.WritePropertyName("/");
		WriteJsonFileArray(writer, files, cancellationToken, nodeWritten);
	}

	private static void WriteJsonFileArray(
		Utf8JsonWriter writer,
		IReadOnlyList<TreeNodeDescriptor> orderedChildren,
		CancellationToken cancellationToken,
		Action? nodeWritten)
	{
		writer.WriteStartArray();
		foreach (var child in orderedChildren)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!child.IsDirectory)
			{
				writer.WriteStringValue(child.DisplayName);
				nodeWritten?.Invoke();
			}
		}
		writer.WriteEndArray();
	}

	private static void WriteXmlTreeContents(
		XmlWriter writer,
		TreeNodeDescriptor root,
		IReadOnlySet<string>? includedPaths,
		CancellationToken cancellationToken,
		Action? nodeWritten = null)
	{
		if (!root.IsDirectory)
		{
			if (includedPaths is null || includedPaths.Contains(root.FullPath))
			{
				WriteXmlFile(writer, root);
				nodeWritten?.Invoke();
			}
			return;
		}

		var operations = new Stack<XmlTreeWriteOperation>();
		PushXmlChildren(
			operations,
			GetOrderedStructuredChildren(root.Children, includedPaths, cancellationToken),
			cancellationToken);

		while (operations.TryPop(out var operation))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (operation.IsEndElement)
			{
				writer.WriteEndElement();
				continue;
			}

			var node = operation.Node!;
			if (!node.IsDirectory)
			{
				WriteXmlFile(writer, node);
				nodeWritten?.Invoke();
				continue;
			}

			writer.WriteStartElement("d");
			writer.WriteAttributeString("n", XmlTextSanitizer.Sanitize(node.DisplayName));
			nodeWritten?.Invoke();
			operations.Push(XmlTreeWriteOperation.EndElement);
			PushXmlChildren(
				operations,
				GetOrderedStructuredChildren(node.Children, includedPaths, cancellationToken),
				cancellationToken);
		}
	}

	private static void PushXmlChildren(
		Stack<XmlTreeWriteOperation> operations,
		IReadOnlyList<TreeNodeDescriptor> orderedChildren,
		CancellationToken cancellationToken)
	{
		for (var index = orderedChildren.Count - 1; index >= 0; index--)
		{
			cancellationToken.ThrowIfCancellationRequested();
			operations.Push(new XmlTreeWriteOperation(orderedChildren[index]));
		}
	}

	private static void WriteXmlFile(XmlWriter writer, TreeNodeDescriptor file)
	{
		writer.WriteStartElement("f");
		writer.WriteString(XmlTextSanitizer.Sanitize(file.DisplayName));
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
		IReadOnlySet<string>? includedPaths,
		CancellationToken cancellationToken)
	{
		if (root.IsDirectory)
		{
			AppendMarkdownChildren(sb, root.Children, includedPaths, level: 0, cancellationToken);
			return;
		}

		if (includedPaths is null || includedPaths.Contains(root.FullPath))
			AppendMarkdownItem(sb, level: 0, root.DisplayName, isDirectory: false);
	}

	private static void AppendMarkdownChildren(
		StringBuilder sb,
		IReadOnlyList<TreeNodeDescriptor> children,
		IReadOnlySet<string>? includedPaths,
		int level,
		CancellationToken cancellationToken)
	{
		var pending = new Stack<MarkdownTreeWriteOperation>();
		PushMarkdownChildren(pending, children, includedPaths, level, cancellationToken);
		while (pending.TryPop(out var operation))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var node = operation.Node;
			AppendMarkdownItem(sb, operation.Level, node.DisplayName, node.IsDirectory);
			if (node.IsDirectory)
				PushMarkdownChildren(
					pending,
					node.Children,
					includedPaths,
					operation.Level + 1,
					cancellationToken);
		}
	}

	private static void PushMarkdownChildren(
		Stack<MarkdownTreeWriteOperation> pending,
		IReadOnlyList<TreeNodeDescriptor> children,
		IReadOnlySet<string>? includedPaths,
		int level,
		CancellationToken cancellationToken)
	{
		var orderedChildren = GetOrderedStructuredChildren(children, includedPaths, cancellationToken);
		for (var index = orderedChildren.Count - 1; index >= 0; index--)
		{
			cancellationToken.ThrowIfCancellationRequested();
			pending.Push(new MarkdownTreeWriteOperation(orderedChildren[index], level));
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

		var sanitized = EscapeTextValue(name);
		return sanitized[0] is '-' or '*' or '+' or '['
			? "\\" + sanitized
			: sanitized;
	}

	private static IReadOnlyList<TreeNodeDescriptor> GetOrderedStructuredChildren(
		IReadOnlyList<TreeNodeDescriptor> children,
		IReadOnlySet<string>? includedPaths,
		CancellationToken cancellationToken)
	{
		if (includedPaths is null && IsStructuredTreeOrder(children, cancellationToken))
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
			cancellationToken.ThrowIfCancellationRequested();
			if (includedPaths is null || includedPaths.Contains(child.FullPath))
				ordered.Add(child);
		}

		if (!IsStructuredTreeOrder(ordered, cancellationToken))
			CancellationAwareSort.Sort(ordered, CompareStructuredTreeNodes, cancellationToken);

		return ordered;
	}

	private static bool IsStructuredTreeOrder(
		IReadOnlyList<TreeNodeDescriptor> children,
		CancellationToken cancellationToken)
	{
		for (var index = 1; index < children.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
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

	private static bool HasDirectoryChild(
		IReadOnlyList<TreeNodeDescriptor> children,
		CancellationToken cancellationToken)
	{
		foreach (var child in children)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (child.IsDirectory)
				return true;
		}

		return false;
	}

	private static bool HasFileChild(
		IReadOnlyList<TreeNodeDescriptor> children,
		CancellationToken cancellationToken)
	{
		foreach (var child in children)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!child.IsDirectory)
				return true;
		}

		return false;
	}

	private static bool CollectIncludedPaths(
		TreeNodeDescriptor node,
		IReadOnlySet<string> selectedPaths,
		HashSet<string> includedPaths,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (selectedPaths.Contains(node.FullPath))
		{
			CollectSubtreePaths(node, includedPaths, cancellationToken);
			return true;
		}

		var pending = new Stack<IncludedPathFrame>();
		pending.Push(new IncludedPathFrame(node));
		while (pending.TryPeek(out var frame))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (frame.NextChildIndex < frame.Node.Children.Count)
			{
				var child = frame.Node.Children[frame.NextChildIndex++];
				if (selectedPaths.Contains(child.FullPath))
				{
					// Selecting a directory includes its complete subtree without realizing UI nodes.
					CollectSubtreePaths(child, includedPaths, cancellationToken);
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

	private static void CollectSubtreePaths(
		TreeNodeDescriptor node,
		HashSet<string> includedPaths,
		CancellationToken cancellationToken)
	{
		var stack = new Stack<TreeNodeDescriptor>();
		stack.Push(node);

		while (stack.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
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
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		long chars = 0;
		long lineBreaks = 0;

		AppendAsciiLineMetrics(
			(long)EscapeTextValue(outputRootPath).Length + 1,
			ref chars,
			ref lineBreaks); // "<rootPath>:"
		AppendFullAsciiChildMetrics(
			root,
			indentLength: 0,
			ref chars,
			ref lineBreaks,
			cancellationToken);

		return CreateMetricsFromNormalizedCounts(chars, lineBreaks);
	}

	private static ExportOutputMetrics CalculateAsciiSelectedTreeMetrics(
		string outputRootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> includedPaths,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		long chars = 0;
		long lineBreaks = 0;

		AppendAsciiLineMetrics(
			(long)EscapeTextValue(outputRootPath).Length + 1,
			ref chars,
			ref lineBreaks); // "<rootPath>:"
		AppendSelectedAsciiChildMetrics(
			root,
			includedPaths,
			indentLength: 0,
			ref chars,
			ref lineBreaks,
			cancellationToken);

		return CreateMetricsFromNormalizedCounts(chars, lineBreaks);
	}

	private static void AppendFullAsciiChildMetrics(
		TreeNodeDescriptor node,
		int indentLength,
		ref long chars,
		ref long lineBreaks,
		CancellationToken cancellationToken)
		=> AppendAsciiChildMetrics(
			node,
			includedPaths: null,
			indentLength,
			ref chars,
			ref lineBreaks,
			cancellationToken);

	private static void AppendSelectedAsciiChildMetrics(
		TreeNodeDescriptor node,
		IReadOnlySet<string> includedPaths,
		int indentLength,
		ref long chars,
		ref long lineBreaks,
		CancellationToken cancellationToken)
		=> AppendAsciiChildMetrics(
			node,
			includedPaths,
			indentLength,
			ref chars,
			ref lineBreaks,
			cancellationToken);

	private static void AppendAsciiChildMetrics(
		TreeNodeDescriptor node,
		IReadOnlySet<string>? includedPaths,
		int indentLength,
		ref long chars,
		ref long lineBreaks,
		CancellationToken cancellationToken)
	{
		var pending = new Stack<AsciiMetricOperation>();
		PushAsciiMetricChildren(pending, node, includedPaths, indentLength, cancellationToken);
		while (pending.TryPop(out var operation))
		{
			cancellationToken.ThrowIfCancellationRequested();
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
					operation.IndentLength + IndentPipe.Length,
					cancellationToken);
			}
		}
	}

	private static void PushAsciiMetricChildren(
		Stack<AsciiMetricOperation> pending,
		TreeNodeDescriptor parent,
		IReadOnlySet<string>? includedPaths,
		int indentLength,
		CancellationToken cancellationToken)
	{
		var isLast = true;
		for (var index = parent.Children.Count - 1; index >= 0; index--)
		{
			cancellationToken.ThrowIfCancellationRequested();
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

	private static ExportOutputMetrics CalculateMarkdownTreeMetrics(
		string outputRootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string>? includedPaths,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		long chars = 0;
		long lineBreaks = 0;
		var rootHeaderChars = ContextRootPresentation
			.FormatLine(ResolveStructuredRootPath(outputRootPath))
			.Length;
		AppendAsciiLineMetrics(rootHeaderChars, ref chars, ref lineBreaks);
		AppendAsciiLineMetrics(renderedChars: 0, ref chars, ref lineBreaks);

		if (!root.IsDirectory)
		{
			if (includedPaths is null || includedPaths.Contains(root.FullPath))
			{
				AppendMarkdownItemMetrics(
					root,
					level: 0,
					ref chars,
					ref lineBreaks);
			}

			return CreateMetricsFromNormalizedCounts(chars, lineBreaks);
		}

		var pending = new Stack<MarkdownTreeWriteOperation>();
		PushMarkdownChildren(
			pending,
			root.Children,
			includedPaths,
			level: 0,
			cancellationToken);
		while (pending.TryPop(out var operation))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var node = operation.Node;
			AppendMarkdownItemMetrics(
				node,
				operation.Level,
				ref chars,
				ref lineBreaks);
			if (node.IsDirectory)
			{
				PushMarkdownChildren(
					pending,
					node.Children,
					includedPaths,
					operation.Level + 1,
					cancellationToken);
			}
		}

		return CreateMetricsFromNormalizedCounts(chars, lineBreaks);
	}

	private static void AppendMarkdownItemMetrics(
		TreeNodeDescriptor node,
		int level,
		ref long chars,
		ref long lineBreaks)
	{
		var escapedName = EscapeMarkdownListText(node.DisplayName);
		var renderedChars = checked((long)level * 2 + 2 + escapedName.Length + (node.IsDirectory ? 1 : 0));
		AppendAsciiLineMetrics(renderedChars, ref chars, ref lineBreaks);
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
