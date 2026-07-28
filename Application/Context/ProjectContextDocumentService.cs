using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Xml;

namespace DevProjex.Application.Context;

public enum ProjectContextView
{
	Tree,
	Content,
	TreeContent
}

public enum ProjectContextDocumentFormat
{
	Text,
	Markdown,
	Json,
	Xml
}

public sealed record ProjectContextDocumentLimits(
	int MaximumTreeNodes = 2_000,
	int MaximumFiles = 80,
	int MaximumCharacters = 256 * 1024,
	long MaximumFileBytes = 256 * 1024);

public sealed class ProjectContextDocumentService(
	TreeExportService treeExportService,
	IFileContentAnalyzer contentAnalyzer)
{
	private const string SchemaVersion = "1";
	private const string Kind = "devprojex-context";
	private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

	public async Task<string> BuildAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		CancellationToken cancellationToken = default,
		ProjectContextDocumentLimits? limits = null)
	{
		ArgumentNullException.ThrowIfNull(plan);
		if (limits is null)
		{
			using var destination = new MemoryStream();
			await WriteCompleteAsync(plan, view, format, destination, cancellationToken)
				.ConfigureAwait(false);
			return Utf8WithoutBom.GetString(destination.GetBuffer(), 0, checked((int)destination.Length));
		}

		var (renderedTree, treeTruncated) = IncludesTree(view)
			? BuildBoundedTree(plan.ProjectedTree, limits?.MaximumTreeNodes)
			: (plan.ProjectedTree, false);
		var fileResult = IncludesContent(view)
			? await ReadFilesAsync(plan, limits, cancellationToken).ConfigureAwait(false)
			: new ContextFileReadResult([], false);
		var renderedPlan = ReferenceEquals(renderedTree, plan.ProjectedTree)
			? plan
			: plan with { ProjectedTree = renderedTree };
		var truncated = treeTruncated || fileResult.IsTruncated;

		return format switch
		{
			ProjectContextDocumentFormat.Markdown => BuildMarkdown(renderedPlan, view, fileResult.Files, truncated),
			ProjectContextDocumentFormat.Json => BuildJson(renderedPlan, view, fileResult.Files, truncated),
			ProjectContextDocumentFormat.Xml => BuildXml(renderedPlan, view, fileResult.Files, truncated),
			_ => BuildText(renderedPlan, view, fileResult.Files, truncated)
		};
	}

	public async Task WriteCompleteAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		Stream destination,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(destination);
		if (!destination.CanRead || !destination.CanSeek || !destination.CanWrite)
			throw new ArgumentException("Destination must be a readable, seekable, writable stream.", nameof(destination));

		switch (format)
		{
			case ProjectContextDocumentFormat.Markdown:
				await WriteCompleteMarkdownAsync(plan, view, destination, cancellationToken)
					.ConfigureAwait(false);
				break;
			case ProjectContextDocumentFormat.Json:
				await WriteCompleteJsonAsync(plan, view, destination, cancellationToken)
					.ConfigureAwait(false);
				break;
			case ProjectContextDocumentFormat.Xml:
				await WriteCompleteXmlAsync(plan, view, destination, cancellationToken)
					.ConfigureAwait(false);
				break;
			default:
				await WriteCompleteTextAsync(plan, view, destination, cancellationToken)
					.ConfigureAwait(false);
				break;
		}
	}

	private async Task WriteCompleteTextAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		Stream destination,
		CancellationToken cancellationToken)
	{
		await using var writer = CreateStreamWriter(destination);
		var hasOutput = false;
		if (IncludesTree(view))
		{
			var tree = treeExportService.BuildFullTree(
				plan.SourceRoot,
				plan.ProjectedTree,
				TreeTextFormat.Ascii,
				GetDocumentRoot(plan),
				GetProjectName(plan));
			await writer.WriteAsync(tree.AsMemory(), cancellationToken).ConfigureAwait(false);
			hasOutput = tree.Length > 0;
		}

		if (IncludesContent(view))
		{
			foreach (var path in plan.IncludedFiles)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var file = await ReadCompleteFileAsync(plan.SourceRoot, path, cancellationToken)
					.ConfigureAwait(false);
				if (hasOutput)
				{
					await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
					await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
				}

				await writer.WriteAsync(file.Path.AsMemory(), cancellationToken).ConfigureAwait(false);
				await WriteLineAsync(writer, ":", cancellationToken).ConfigureAwait(false);
				await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
				await writer.WriteAsync(GetTextContent(file).AsMemory(), cancellationToken).ConfigureAwait(false);
				hasOutput = true;
			}
		}

		await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
		TrimTrailingLineEndings(destination);
	}

	private async Task WriteCompleteMarkdownAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		Stream destination,
		CancellationToken cancellationToken)
	{
		await using var writer = CreateStreamWriter(destination);
		await writer.WriteAsync("# ".AsMemory(), cancellationToken).ConfigureAwait(false);
		await WriteLineAsync(writer, EscapeMarkdownHeading(GetProjectName(plan)), cancellationToken)
			.ConfigureAwait(false);
		await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);

		if (IncludesTree(view))
		{
			await WriteLineAsync(writer, "## Project tree", cancellationToken).ConfigureAwait(false);
			await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
			var tree = treeExportService.BuildFullTree(
					plan.SourceRoot,
					plan.ProjectedTree,
					TreeTextFormat.Ascii,
					GetDocumentRoot(plan),
					GetProjectName(plan))
				.TrimEnd('\r', '\n');
			await WriteMarkdownFenceAsync(writer, tree, "text", cancellationToken).ConfigureAwait(false);
		}

		if (IncludesContent(view))
		{
			foreach (var path in plan.IncludedFiles)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var file = await ReadCompleteFileAsync(plan.SourceRoot, path, cancellationToken)
					.ConfigureAwait(false);
				await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
				await writer.WriteAsync("## ".AsMemory(), cancellationToken).ConfigureAwait(false);
				await WriteLineAsync(writer, BuildMarkdownCodeSpan(file.Path), cancellationToken)
					.ConfigureAwait(false);
				await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
				if (file.IsBinary)
				{
					await WriteLineAsync(
							writer,
							"_Binary file; content omitted._",
							cancellationToken)
						.ConfigureAwait(false);
				}
				else
				{
					await WriteMarkdownFenceAsync(
							writer,
							file.Content ?? string.Empty,
							ResolveFenceLanguage(file.Path),
							cancellationToken)
						.ConfigureAwait(false);
				}
			}
		}

		await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
		TrimTrailingLineEndings(destination);
	}

	private async Task WriteCompleteJsonAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		Stream destination,
		CancellationToken cancellationToken)
	{
		using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions
		{
			Indented = true,
			Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
		});

		writer.WriteStartObject();
		writer.WriteString("schemaVersion", SchemaVersion);
		writer.WriteString("kind", Kind);
		writer.WriteStartObject("project");
		writer.WriteString("root", NormalizePath(GetDocumentRoot(plan)));
		writer.WriteString("name", GetProjectName(plan));
		WriteRepositorySource(writer, plan.SourceIdentity);
		writer.WriteEndObject();
		WriteSelection(writer, plan);
		WriteMetrics(writer, plan);
		writer.WritePropertyName("tree");
		if (IncludesTree(view))
			WriteTreeNode(writer, plan.ProjectedTree, plan.SourceRoot);
		else
			writer.WriteNullValue();
		writer.WriteStartArray("files");
		if (IncludesContent(view))
		{
			foreach (var path in plan.IncludedFiles)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var file = await ReadCompleteFileAsync(plan.SourceRoot, path, cancellationToken)
					.ConfigureAwait(false);
				WriteContextFileJson(writer, file);
			}
		}
		writer.WriteEndArray();
		WriteDiagnostics(writer, plan.Diagnostics);
		writer.WriteString("fingerprint", plan.Fingerprint);
		writer.WriteEndObject();
		await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task WriteCompleteXmlAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		Stream destination,
		CancellationToken cancellationToken)
	{
		await using var streamWriter = CreateStreamWriter(destination);
		var encodingWriter = new EncodingReportingTextWriter(streamWriter, Encoding.Unicode);
		using var writer = XmlWriter.Create(encodingWriter, new XmlWriterSettings
		{
			Indent = true,
			OmitXmlDeclaration = false,
			Encoding = Utf8WithoutBom,
			CloseOutput = false
		});

		writer.WriteStartDocument();
		writer.WriteStartElement("devprojexContext");
		writer.WriteAttributeString("schemaVersion", SchemaVersion);
		writer.WriteAttributeString("kind", Kind);
		writer.WriteStartElement("project");
		writer.WriteElementString("root", NormalizePath(GetDocumentRoot(plan)));
		writer.WriteElementString("name", GetProjectName(plan));
		WriteRepositorySourceXml(writer, plan.SourceIdentity);
		writer.WriteEndElement();
		WriteSelectionXml(writer, plan);
		WriteMetricsXml(writer, plan);
		writer.WriteStartElement("tree");
		if (IncludesTree(view))
			WriteTreeNodeXml(writer, plan.ProjectedTree, plan.SourceRoot);
		writer.WriteEndElement();
		writer.WriteStartElement("files");
		if (IncludesContent(view))
		{
			foreach (var path in plan.IncludedFiles)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var file = await ReadCompleteFileAsync(plan.SourceRoot, path, cancellationToken)
					.ConfigureAwait(false);
				WriteContextFileXml(writer, file);
			}
		}
		writer.WriteEndElement();
		writer.WriteStartElement("diagnostics");
		foreach (var diagnostic in plan.Diagnostics)
		{
			writer.WriteStartElement("diagnostic");
			writer.WriteAttributeString("code", diagnostic.Code);
			writer.WriteAttributeString("severity", ToToken(diagnostic.Severity));
			if (!string.IsNullOrWhiteSpace(diagnostic.Path))
				writer.WriteAttributeString("path", NormalizePath(diagnostic.Path));
			writer.WriteString(diagnostic.Message);
			writer.WriteEndElement();
		}
		writer.WriteEndElement();
		writer.WriteElementString("fingerprint", plan.Fingerprint);
		writer.WriteEndElement();
		writer.WriteEndDocument();
		writer.Flush();
		await streamWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task<ContextFileDocument> ReadCompleteFileAsync(
		string sourceRoot,
		string path,
		CancellationToken cancellationToken)
	{
		var relativePath = NormalizeRelativePath(sourceRoot, path);
		var content = await contentAnalyzer
			.TryReadAsTextAsync(path, long.MaxValue, cancellationToken)
			.ConfigureAwait(false);
		return content is null
			? new ContextFileDocument(relativePath, IsBinary: true, Content: null)
			: new ContextFileDocument(relativePath, IsBinary: false, content.Content);
	}

	private static StreamWriter CreateStreamWriter(Stream destination) =>
		new(destination, Utf8WithoutBom, bufferSize: 8192, leaveOpen: true);

	private static async Task WriteLineAsync(
		TextWriter writer,
		string? value,
		CancellationToken cancellationToken)
	{
		if (value is not null)
			await writer.WriteAsync(value.AsMemory(), cancellationToken).ConfigureAwait(false);
		await writer.WriteAsync(Environment.NewLine.AsMemory(), cancellationToken).ConfigureAwait(false);
	}

	private static async Task WriteMarkdownFenceAsync(
		TextWriter writer,
		string content,
		string language,
		CancellationToken cancellationToken)
	{
		var fence = new string('`', Math.Max(3, FindLongestBacktickRun(content) + 1));
		await writer.WriteAsync(fence.AsMemory(), cancellationToken).ConfigureAwait(false);
		await WriteLineAsync(writer, language, cancellationToken).ConfigureAwait(false);
		await WriteLineAsync(writer, content, cancellationToken).ConfigureAwait(false);
		await WriteLineAsync(writer, fence, cancellationToken).ConfigureAwait(false);
	}

	private static string GetTextContent(ContextFileDocument file) =>
		file.IsBinary
			? "[Binary file; content omitted]"
			: file.Content ?? string.Empty;

	private static void WriteContextFileJson(Utf8JsonWriter writer, ContextFileDocument file)
	{
		writer.WriteStartObject();
		writer.WriteString("path", file.Path);
		writer.WriteBoolean("isBinary", file.IsBinary);
		if (file.IsBinary)
			writer.WriteNull("content");
		else
			writer.WriteString("content", file.Content);
		writer.WriteEndObject();
	}

	private static void WriteContextFileXml(XmlWriter writer, ContextFileDocument file)
	{
		writer.WriteStartElement("file");
		writer.WriteAttributeString("path", file.Path);
		writer.WriteAttributeString("isBinary", XmlConvert.ToString(file.IsBinary));
		if (!file.IsBinary)
			writer.WriteElementString("content", file.Content ?? string.Empty);
		writer.WriteEndElement();
	}

	private static void TrimTrailingLineEndings(Stream destination)
	{
		var length = destination.Length;
		Span<byte> value = stackalloc byte[1];
		while (length > 0)
		{
			destination.Position = length - 1;
			destination.ReadExactly(value);
			if (value[0] is not ((byte)'\r') and not ((byte)'\n'))
				break;
			length--;
		}

		destination.SetLength(length);
		destination.Position = length;
	}

	private async Task<ContextFileReadResult> ReadFilesAsync(
		ProjectContextPlan plan,
		ProjectContextDocumentLimits? limits,
		CancellationToken cancellationToken)
	{
		var maximumFiles = Math.Max(0, limits?.MaximumFiles ?? int.MaxValue);
		var maximumCharacters = Math.Max(0, limits?.MaximumCharacters ?? int.MaxValue);
		var maximumFileBytes = Math.Max(0, limits?.MaximumFileBytes ?? long.MaxValue);
		var files = new List<ContextFileDocument>(
			Math.Min(plan.IncludedFiles.Count, maximumFiles));
		var remainingCharacters = maximumCharacters;
		var isTruncated = plan.IncludedFiles.Count > maximumFiles;
		foreach (var path in plan.IncludedFiles.Take(maximumFiles))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relativePath = NormalizeRelativePath(plan.SourceRoot, path);
			var content = await contentAnalyzer
				.TryReadAsTextAsync(path, maximumFileBytes, cancellationToken)
				.ConfigureAwait(false);
			if (content is null)
			{
				files.Add(new ContextFileDocument(relativePath, IsBinary: true, Content: null));
				continue;
			}

			if (content.IsEstimated)
			{
				files.Add(new ContextFileDocument(
					relativePath,
					IsBinary: false,
					Content: null,
					IsOmitted: true));
				isTruncated = true;
				continue;
			}

			var fileContent = content.Content;
			if (fileContent.Length > remainingCharacters)
			{
				fileContent = fileContent[..remainingCharacters];
				isTruncated = true;
			}
			files.Add(new ContextFileDocument(
				relativePath,
				IsBinary: false,
				fileContent,
				IsTruncated: fileContent.Length != content.Content.Length));
			remainingCharacters -= fileContent.Length;
			if (remainingCharacters == 0 &&
			    files.Count < Math.Min(plan.IncludedFiles.Count, maximumFiles))
			{
				isTruncated = true;
				break;
			}
		}

		return new ContextFileReadResult(files, isTruncated);
	}

	private string BuildText(
		ProjectContextPlan plan,
		ProjectContextView view,
		IReadOnlyList<ContextFileDocument> files,
		bool truncated)
	{
		var output = new StringBuilder();
		if (IncludesTree(view))
		{
			output.Append(treeExportService.BuildFullTree(
				plan.SourceRoot,
				plan.ProjectedTree,
				TreeTextFormat.Ascii,
				GetDocumentRoot(plan),
				GetProjectName(plan)));
		}

		AppendTextFiles(output, files);
		AppendTruncationNotice(output, truncated);
		return output.ToString().TrimEnd('\r', '\n');
	}

	private string BuildMarkdown(
		ProjectContextPlan plan,
		ProjectContextView view,
		IReadOnlyList<ContextFileDocument> files,
		bool truncated)
	{
		var output = new StringBuilder();
		output.Append("# ").AppendLine(EscapeMarkdownHeading(GetProjectName(plan)));
		output.AppendLine();
		if (IncludesTree(view))
		{
			output.AppendLine("## Project tree");
			output.AppendLine();
			var tree = treeExportService.BuildFullTree(
				plan.SourceRoot,
				plan.ProjectedTree,
				TreeTextFormat.Ascii,
				GetDocumentRoot(plan),
				GetProjectName(plan)).TrimEnd('\r', '\n');
			AppendMarkdownFence(output, tree, "text");
		}

		foreach (var file in files)
		{
			output.AppendLine();
			output.Append("## ").AppendLine(BuildMarkdownCodeSpan(file.Path));
			output.AppendLine();
			if (file.IsBinary)
			{
				output.AppendLine("_Binary file; content omitted._");
				continue;
			}
			if (file.IsOmitted)
			{
				output.AppendLine("_Large text file; content omitted from bounded preview._");
				continue;
			}

			AppendMarkdownFence(output, file.Content ?? string.Empty, ResolveFenceLanguage(file.Path));
			if (file.IsTruncated)
				output.AppendLine("_File preview truncated._");
		}

		if (truncated)
			output.AppendLine().AppendLine("_Preview truncated._");
		return output.ToString().TrimEnd('\r', '\n');
	}

	private static string BuildJson(
		ProjectContextPlan plan,
		ProjectContextView view,
		IReadOnlyList<ContextFileDocument> files,
		bool truncated)
	{
		var buffer = new ArrayBufferWriter<byte>();
		using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
		{
			Indented = true,
			Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
		});

		writer.WriteStartObject();
		writer.WriteString("schemaVersion", SchemaVersion);
		writer.WriteString("kind", Kind);
		writer.WriteStartObject("project");
		writer.WriteString("root", NormalizePath(GetDocumentRoot(plan)));
		writer.WriteString("name", GetProjectName(plan));
		WriteRepositorySource(writer, plan.SourceIdentity);
		writer.WriteEndObject();
		WriteSelection(writer, plan);
		WriteMetrics(writer, plan);
		writer.WritePropertyName("tree");
		if (IncludesTree(view))
			WriteTreeNode(writer, plan.ProjectedTree, plan.SourceRoot);
		else
			writer.WriteNullValue();
		writer.WriteStartArray("files");
		foreach (var file in files)
		{
			writer.WriteStartObject();
			writer.WriteString("path", file.Path);
			writer.WriteBoolean("isBinary", file.IsBinary);
			if (file.IsBinary || file.IsOmitted)
				writer.WriteNull("content");
			else
				writer.WriteString("content", file.Content);
			if (file.IsOmitted)
				writer.WriteBoolean("omitted", true);
			if (file.IsTruncated)
				writer.WriteBoolean("truncated", true);
			writer.WriteEndObject();
		}
		writer.WriteEndArray();
		WriteDiagnostics(writer, plan.Diagnostics);
		if (truncated)
			writer.WriteBoolean("truncated", true);
		writer.WriteString("fingerprint", plan.Fingerprint);
		writer.WriteEndObject();
		writer.Flush();
		return Encoding.UTF8.GetString(buffer.WrittenSpan);
	}

	private static string BuildXml(
		ProjectContextPlan plan,
		ProjectContextView view,
		IReadOnlyList<ContextFileDocument> files,
		bool truncated)
	{
		var output = new StringBuilder();
		using var writer = XmlWriter.Create(output, new XmlWriterSettings
		{
			Indent = true,
			OmitXmlDeclaration = false,
			Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
		});

		writer.WriteStartDocument();
		writer.WriteStartElement("devprojexContext");
		writer.WriteAttributeString("schemaVersion", SchemaVersion);
		writer.WriteAttributeString("kind", Kind);
		writer.WriteStartElement("project");
		writer.WriteElementString("root", NormalizePath(GetDocumentRoot(plan)));
		writer.WriteElementString("name", GetProjectName(plan));
		WriteRepositorySourceXml(writer, plan.SourceIdentity);
		writer.WriteEndElement();
		WriteSelectionXml(writer, plan);
		WriteMetricsXml(writer, plan);
		writer.WriteStartElement("tree");
		if (IncludesTree(view))
			WriteTreeNodeXml(writer, plan.ProjectedTree, plan.SourceRoot);
		writer.WriteEndElement();
		writer.WriteStartElement("files");
		foreach (var file in files)
		{
			writer.WriteStartElement("file");
			writer.WriteAttributeString("path", file.Path);
			writer.WriteAttributeString("isBinary", XmlConvert.ToString(file.IsBinary));
			if (file.IsOmitted)
				writer.WriteAttributeString("omitted", XmlConvert.ToString(true));
			if (file.IsTruncated)
				writer.WriteAttributeString("truncated", XmlConvert.ToString(true));
			if (!file.IsBinary && !file.IsOmitted)
				writer.WriteElementString("content", file.Content ?? string.Empty);
			writer.WriteEndElement();
		}
		writer.WriteEndElement();
		writer.WriteStartElement("diagnostics");
		foreach (var diagnostic in plan.Diagnostics)
		{
			writer.WriteStartElement("diagnostic");
			writer.WriteAttributeString("code", diagnostic.Code);
			writer.WriteAttributeString("severity", ToToken(diagnostic.Severity));
			if (!string.IsNullOrWhiteSpace(diagnostic.Path))
				writer.WriteAttributeString("path", NormalizePath(diagnostic.Path));
			writer.WriteString(diagnostic.Message);
			writer.WriteEndElement();
		}
		writer.WriteEndElement();
		if (truncated)
			writer.WriteElementString("truncated", XmlConvert.ToString(true));
		writer.WriteElementString("fingerprint", plan.Fingerprint);
		writer.WriteEndElement();
		writer.WriteEndDocument();
		writer.Flush();
		return output.ToString();
	}

	private static void AppendTextFiles(
		StringBuilder output,
		IReadOnlyList<ContextFileDocument> files)
	{
		foreach (var file in files)
		{
			if (output.Length > 0)
				output.AppendLine().AppendLine();

			output.Append(file.Path).AppendLine(":");
			output.AppendLine();
			output.Append(file.IsBinary
				? "[Binary file; content omitted]"
				: file.IsOmitted
					? "[Large text file; content omitted from bounded preview]"
					: file.Content);
			if (file.IsTruncated)
				output.AppendLine().Append("[File preview truncated]");
		}
	}

	private static void AppendTruncationNotice(StringBuilder output, bool truncated)
	{
		if (!truncated)
			return;
		if (output.Length > 0)
			output.AppendLine().AppendLine();
		output.Append("[Preview truncated]");
	}

	private static (TreeNodeDescriptor Tree, bool IsTruncated) BuildBoundedTree(
		TreeNodeDescriptor root,
		int? maximumNodes)
	{
		if (maximumNodes is null)
			return (root, false);

		var remaining = Math.Max(1, maximumNodes.Value);
		var truncated = false;
		var tree = Clone(root);
		return (tree, truncated);

		TreeNodeDescriptor Clone(TreeNodeDescriptor node)
		{
			remaining--;
			if (!node.IsDirectory || node.Children.Count == 0)
				return node;

			var children = new List<TreeNodeDescriptor>();
			foreach (var child in node.Children)
			{
				if (remaining <= 0)
				{
					truncated = true;
					break;
				}
				children.Add(Clone(child));
			}
			return node with { Children = children };
		}
	}

	private static void AppendMarkdownFence(StringBuilder output, string content, string language)
	{
		var fence = new string('`', Math.Max(3, FindLongestBacktickRun(content) + 1));
		output.Append(fence).AppendLine(language);
		output.AppendLine(content);
		output.AppendLine(fence);
	}

	private static int FindLongestBacktickRun(string value)
	{
		var longest = 0;
		var current = 0;
		foreach (var character in value)
		{
			if (character == '`')
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

	private static void WriteSelection(Utf8JsonWriter writer, ProjectContextPlan plan)
	{
		writer.WriteStartObject("selection");
		writer.WriteString("gitMode", ProjectSelectionTokens.ToToken(plan.Selection.GitMode!.Value));
		WriteStringArray(
			writer,
			"exclusions",
			plan.Selection.Exclusions!.Select(ProjectSelectionTokens.ToToken));
		WriteStringArray(writer, "roots", plan.SelectedRoots);
		WriteStringArray(writer, "extensions", plan.SelectedExtensions);
		WriteStringArray(writer, "selectedPaths", plan.Selection.SelectedPaths ?? []);
		writer.WriteEndObject();
	}

	private static void WriteRepositorySource(
		Utf8JsonWriter writer,
		ProjectSourceIdentity? identity)
	{
		if (identity is not
		    {
			    SourceType: ProjectSourceType.GitClone,
			    RepositoryUrl.Length: > 0
		    })
		{
			return;
		}

		writer.WriteStartObject("source");
		writer.WriteString("type", "git");
		writer.WriteString("repositoryUrl", identity.RepositoryUrl);
		if (!string.IsNullOrWhiteSpace(identity.Branch))
			writer.WriteString("branch", identity.Branch);
		if (!string.IsNullOrWhiteSpace(identity.CommitHash))
			writer.WriteString("commit", identity.CommitHash);
		writer.WriteEndObject();
	}

	private static void WriteMetrics(Utf8JsonWriter writer, ProjectContextPlan plan)
	{
		var tree = plan.Analysis.Inventory.Tree;
		var content = plan.Analysis.Metrics.Content;
		writer.WriteStartObject("metrics");
		writer.WriteNumber("files", tree.FileCount);
		writer.WriteNumber("folders", tree.DirectoryCount);
		writer.WriteNumber("bytes", plan.IncludedBytes);
		writer.WriteNumber("characters", content.Chars);
		writer.WriteNumber("estimatedTokens", content.Tokens);
		writer.WriteEndObject();
	}

	private static void WriteTreeNode(
		Utf8JsonWriter writer,
		TreeNodeDescriptor node,
		string sourceRoot)
	{
		writer.WriteStartObject();
		writer.WriteString("path", NormalizeRelativePath(sourceRoot, node.FullPath));
		writer.WriteString("name", node.DisplayName);
		writer.WriteString("type", node.IsDirectory ? "directory" : "file");
		if (node.IsDirectory)
		{
			writer.WriteStartArray("children");
			foreach (var child in node.Children)
				WriteTreeNode(writer, child, sourceRoot);
			writer.WriteEndArray();
		}
		writer.WriteEndObject();
	}

	private static void WriteDiagnostics(
		Utf8JsonWriter writer,
		IReadOnlyList<ContextDiagnostic> diagnostics)
	{
		writer.WriteStartArray("diagnostics");
		foreach (var diagnostic in diagnostics)
		{
			writer.WriteStartObject();
			writer.WriteString("code", diagnostic.Code);
			writer.WriteString("severity", ToToken(diagnostic.Severity));
			writer.WriteString("message", diagnostic.Message);
			if (!string.IsNullOrWhiteSpace(diagnostic.Path))
				writer.WriteString("path", NormalizePath(diagnostic.Path));
			writer.WriteEndObject();
		}
		writer.WriteEndArray();
	}

	private static void WriteStringArray(
		Utf8JsonWriter writer,
		string propertyName,
		IEnumerable<string> values)
	{
		writer.WriteStartArray(propertyName);
		foreach (var value in values)
			writer.WriteStringValue(value);
		writer.WriteEndArray();
	}

	private static void WriteSelectionXml(XmlWriter writer, ProjectContextPlan plan)
	{
		writer.WriteStartElement("selection");
		writer.WriteElementString("gitMode", ProjectSelectionTokens.ToToken(plan.Selection.GitMode!.Value));
		WriteStringCollectionXml(
			writer,
			"exclusions",
			"exclusion",
			plan.Selection.Exclusions!.Select(ProjectSelectionTokens.ToToken));
		WriteStringCollectionXml(writer, "roots", "root", plan.SelectedRoots);
		WriteStringCollectionXml(writer, "extensions", "extension", plan.SelectedExtensions);
		WriteStringCollectionXml(writer, "selectedPaths", "path", plan.Selection.SelectedPaths ?? []);
		writer.WriteEndElement();
	}

	private static void WriteRepositorySourceXml(
		XmlWriter writer,
		ProjectSourceIdentity? identity)
	{
		if (identity is not
		    {
			    SourceType: ProjectSourceType.GitClone,
			    RepositoryUrl.Length: > 0
		    })
		{
			return;
		}

		writer.WriteStartElement("source");
		writer.WriteAttributeString("type", "git");
		writer.WriteElementString("repositoryUrl", identity.RepositoryUrl);
		if (!string.IsNullOrWhiteSpace(identity.Branch))
			writer.WriteElementString("branch", identity.Branch);
		if (!string.IsNullOrWhiteSpace(identity.CommitHash))
			writer.WriteElementString("commit", identity.CommitHash);
		writer.WriteEndElement();
	}

	private static void WriteMetricsXml(XmlWriter writer, ProjectContextPlan plan)
	{
		var tree = plan.Analysis.Inventory.Tree;
		var content = plan.Analysis.Metrics.Content;
		writer.WriteStartElement("metrics");
		writer.WriteElementString("files", XmlConvert.ToString(tree.FileCount));
		writer.WriteElementString("folders", XmlConvert.ToString(tree.DirectoryCount));
		writer.WriteElementString("bytes", XmlConvert.ToString(plan.IncludedBytes));
		writer.WriteElementString("characters", XmlConvert.ToString(content.Chars));
		writer.WriteElementString("estimatedTokens", XmlConvert.ToString(content.Tokens));
		writer.WriteEndElement();
	}

	private static void WriteTreeNodeXml(
		XmlWriter writer,
		TreeNodeDescriptor node,
		string sourceRoot)
	{
		writer.WriteStartElement(node.IsDirectory ? "directory" : "file");
		writer.WriteAttributeString("path", NormalizeRelativePath(sourceRoot, node.FullPath));
		writer.WriteAttributeString("name", node.DisplayName);
		foreach (var child in node.Children)
			WriteTreeNodeXml(writer, child, sourceRoot);
		writer.WriteEndElement();
	}

	private static void WriteStringCollectionXml(
		XmlWriter writer,
		string containerName,
		string itemName,
		IEnumerable<string> values)
	{
		writer.WriteStartElement(containerName);
		foreach (var value in values)
			writer.WriteElementString(itemName, value);
		writer.WriteEndElement();
	}

	private static bool IncludesTree(ProjectContextView view) =>
		view is ProjectContextView.Tree or ProjectContextView.TreeContent;

	private static bool IncludesContent(ProjectContextView view) =>
		view is ProjectContextView.Content or ProjectContextView.TreeContent;

	private static string NormalizeRelativePath(string root, string path)
	{
		var relative = Path.GetRelativePath(root, path);
		return relative == "." ? "." : NormalizePath(relative);
	}

	private static string NormalizePath(string path) => path.Replace('\\', '/');

	private static string GetProjectName(ProjectContextPlan plan) =>
		plan.SourceIdentity?.DisplayName is { Length: > 0 } displayName
			? displayName
			: Path.GetFileName(Path.TrimEndingDirectorySeparator(plan.SourceRoot)) is { Length: > 0 } name
			? name
			: "project";

	private static string GetDocumentRoot(ProjectContextPlan plan) =>
		plan.SourceIdentity is
		{
			SourceType: ProjectSourceType.GitClone,
			SourceReference.Length: > 0
		} identity
			? identity.SourceReference
			: plan.SourceRoot;

	private static string EscapeMarkdownHeading(string value) =>
		value.Replace("\\", "\\\\").Replace("#", "\\#").Replace("\r", " ").Replace("\n", " ");

	private static string BuildMarkdownCodeSpan(string value)
	{
		var normalized = value.Replace("\r", "\\r").Replace("\n", "\\n");
		var delimiter = new string('`', Math.Max(1, FindLongestBacktickRun(normalized) + 1));
		var needsPadding =
			normalized.StartsWith('`') ||
			normalized.EndsWith('`') ||
			(normalized.StartsWith(' ') && normalized.EndsWith(' '));
		return needsPadding
			? $"{delimiter} {normalized} {delimiter}"
			: $"{delimiter}{normalized}{delimiter}";
	}

	private static string ResolveFenceLanguage(string path)
	{
		var extension = Path.GetExtension(path).TrimStart('.');
		return extension.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
			? extension
			: string.Empty;
	}

	private static string ToToken(ContextDiagnosticSeverity severity) =>
		severity.ToString().ToLowerInvariant();

	private sealed record ContextFileReadResult(
		IReadOnlyList<ContextFileDocument> Files,
		bool IsTruncated);

	private sealed record ContextFileDocument(
		string Path,
		bool IsBinary,
		string? Content,
		bool IsOmitted = false,
		bool IsTruncated = false);

	private sealed class EncodingReportingTextWriter(
		TextWriter inner,
		Encoding encoding) : TextWriter
	{
		public override Encoding Encoding => encoding;

		public override void Flush() => inner.Flush();

		public override void Write(char value) => inner.Write(value);

		public override void Write(char[] buffer, int index, int count) =>
			inner.Write(buffer, index, count);

		public override void Write(string? value) => inner.Write(value);
	}
}
