using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Runtime.InteropServices;
using System.Xml;
using DevProjex.Application.Secrets;

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

public sealed record ProjectContextWriteResult(IReadOnlyList<UnscannableFile> UnscannableFiles)
{
	public static ProjectContextWriteResult Empty { get; } = new([]);
}

public sealed class ProjectContextDocumentService(
	TreeExportService treeExportService,
	IFileContentAnalyzer contentAnalyzer,
	Func<FileContentClassification, string>? omissionMessageProvider = null,
	SecretRedactionSession? secretRedactionSession = null,
	CodeCompressionSession? codeCompressionSession = null,
	OutputPathRedactionDecision? outputPathRedactionDecision = null)
{
	private const int SchemaVersion = 1;
	private const string Kind = "devprojex-context";
	private const int StructuredTreeFlushNodeInterval = 512;
	private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
	private static readonly RepositoryWebPathPresentationService WebPathPresentation = new();

	public async Task<string> BuildAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		ProjectContextDocumentLimits limits,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(limits);
		ValidateView(view);
		ValidateDocumentFormat(format);
		if (ShouldRedact(plan, view))
		{
			return await BuildRedactedAsync(plan, view, format, limits, cancellationToken)
				.ConfigureAwait(false);
		}
		return await BuildBoundedAsync(plan, view, format, limits, cancellationToken)
			.ConfigureAwait(false);
	}

	private async Task<string> BuildBoundedAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		ProjectContextDocumentLimits limits,
		CancellationToken cancellationToken,
		PreparedSecretRedactionOutput? prepared = null)
	{
		var (renderedTree, treeTruncated) = IncludesTree(view)
			? BuildBoundedTree(
				plan.ProjectedTree,
				limits.MaximumTreeNodes,
				cancellationToken)
			: (plan.ProjectedTree, false);
		var fileResult = IncludesContent(view)
			? await ReadFilesAsync(plan, limits, cancellationToken, prepared).ConfigureAwait(false)
			: new ContextFileReadResult([], false);
		var renderedPlan = ReferenceEquals(renderedTree, plan.ProjectedTree)
			? plan
			: plan with { ProjectedTree = renderedTree };
		var truncated = treeTruncated || fileResult.IsTruncated;

		return format switch
		{
			ProjectContextDocumentFormat.Text => BuildText(
				renderedPlan,
				view,
				fileResult.Files,
				truncated,
				cancellationToken),
			ProjectContextDocumentFormat.Markdown => BuildMarkdown(
				renderedPlan,
				view,
				fileResult.Files,
				truncated,
				cancellationToken),
			ProjectContextDocumentFormat.Json => BuildJson(renderedPlan, view, fileResult.Files, truncated),
			ProjectContextDocumentFormat.Xml => BuildXml(renderedPlan, view, fileResult.Files, truncated),
			_ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
		};
	}

	public async Task WriteCompleteAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		Stream destination,
		CancellationToken cancellationToken = default,
		bool plain = false,
		bool useUnifiedContentHeaders = false,
		IProgress<ProjectCopyExportProgress>? writeProgress = null)
	{
		_ = await WriteCompleteWithReportAsync(
				plan,
				view,
				format,
				destination,
				cancellationToken,
				plain,
				useUnifiedContentHeaders,
				writeProgress)
			.ConfigureAwait(false);
	}

	public async Task<ProjectContextWriteResult> WriteCompleteWithReportAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		Stream destination,
		CancellationToken cancellationToken = default,
		bool plain = false,
		bool useUnifiedContentHeaders = false,
		IProgress<ProjectCopyExportProgress>? writeProgress = null)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(destination);
		ValidateView(view);
		ValidateDocumentFormat(format);
		if (!destination.CanWrite)
			throw new ArgumentException("Destination must be writable.", nameof(destination));
		var effectivePathRedaction = outputPathRedactionDecision ??
			OutputRootPathPresentation.CaptureRedactionDecision(CreateTransformationContext(plan));
		var contentPathMapper = CreateContentPathMapper(
			plan,
			useUnifiedContentHeaders,
			view);
		if (ShouldRedact(plan, view))
		{
			return await WriteCompleteRedactedAsync(
					plan,
					view,
					format,
					destination,
					cancellationToken,
					plain,
					useUnifiedContentHeaders,
					effectivePathRedaction,
					writeProgress)
				.ConfigureAwait(false);
		}
		using var cancellationDestination = new CancellationBoundWriteStream(
			destination,
			cancellationToken);

		switch (format)
		{
			case ProjectContextDocumentFormat.Text:
				await WriteCompleteTextAsync(
						plan,
						view,
						cancellationDestination,
						plain,
						effectivePathRedaction,
						contentPathMapper,
						writeProgress,
						cancellationToken)
					.ConfigureAwait(false);
				break;
			case ProjectContextDocumentFormat.Markdown:
				await WriteCompleteMarkdownAsync(
						plan,
						view,
						cancellationDestination,
						plain,
						effectivePathRedaction,
						contentPathMapper,
						writeProgress,
						cancellationToken)
					.ConfigureAwait(false);
				break;
			case ProjectContextDocumentFormat.Json:
				await WriteCompleteJsonAsync(
						plan,
						view,
						cancellationDestination,
						effectivePathRedaction,
						contentPathMapper,
						writeProgress,
						cancellationToken)
					.ConfigureAwait(false);
				break;
			case ProjectContextDocumentFormat.Xml:
				await WriteCompleteXmlAsync(
						plan,
						view,
						cancellationDestination,
						effectivePathRedaction,
						contentPathMapper,
						writeProgress,
						cancellationToken)
					.ConfigureAwait(false);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(format), format, null);
		}
		return ProjectContextWriteResult.Empty;
	}

	public async Task WritePreparedCompleteAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		Stream destination,
		PreparedSecretRedactionOutput prepared,
		CancellationToken cancellationToken = default,
		bool plain = false,
		bool useUnifiedContentHeaders = false,
		IProgress<ProjectCopyExportProgress>? writeProgress = null)
	{
		ArgumentNullException.ThrowIfNull(prepared);
		var pathRedaction = outputPathRedactionDecision ??
		                    OutputRootPathPresentation.CaptureRedactionDecision(
			                    CreateTransformationContext(plan));
		var analyzer = new PreparedSecretFileContentAnalyzer(contentAnalyzer, prepared);
		var service = new ProjectContextDocumentService(
			treeExportService,
			analyzer,
			omissionMessageProvider,
			secretRedactionSession: null,
			codeCompressionSession: null,
			outputPathRedactionDecision: pathRedaction);
		await service.WriteCompleteAsync(
				plan,
				view,
				format,
				destination,
				cancellationToken,
				plain,
				useUnifiedContentHeaders,
				writeProgress)
			.ConfigureAwait(false);
	}

	// One gate for both transformations: whichever is enabled, the document is built from prepared
	// text rather than from the files on disk, so every format sees the same bytes.
	private bool ShouldRedact(ProjectContextPlan plan, ProjectContextView view) =>
		IncludesContent(view) && CreateTransformationContext(plan) is not null;

	private ContentTransformationContext? CreateTransformationContext(ProjectContextPlan plan)
	{
		var kinds = CodeTransformIdentity.Resolve(
			plan.Selection.CompressCode == true,
			plan.Selection.StripComments == true,
			plan.Selection.StripBlankLines == true);
		return ContentTransformationContext.For(
			codeCompressionSession is not null && kinds != CodeTransformKinds.None
				? new CodeCompressionContext(plan.SourceRoot, codeCompressionSession, kinds)
				: null,
			CreateRedactionContext(plan));
	}

	private SecretRedactionContext? CreateRedactionContext(ProjectContextPlan plan)
	{
		if (secretRedactionSession is null)
			return null;
		var features = SecretRedactionFeatureSelection.Resolve(
			plan.Selection.HideSecrets == true,
			plan.Selection.HidePrivateData == true);
		return features == SecretRedactionFeatures.None
			? null
			: new SecretRedactionContext(plan.SourceRoot, secretRedactionSession, features);
	}

	private async Task<string> BuildRedactedAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		ProjectContextDocumentLimits limits,
		CancellationToken cancellationToken)
	{
		var context = CreateTransformationContext(plan)!;
		var preparer = new SecretRedactionOutputPreparer(contentAnalyzer);
		await using var prepared = await preparer
			.PrepareAsync(context, plan.IncludedFiles, cancellationToken)
			.ConfigureAwait(false);
		var analyzer = new PreparedSecretFileContentAnalyzer(contentAnalyzer, prepared);
		var service = new ProjectContextDocumentService(
			treeExportService,
			analyzer,
			omissionMessageProvider,
			secretRedactionSession: null,
			codeCompressionSession: null);
		return await service.BuildBoundedAsync(
				plan,
				view,
				format,
				limits,
				cancellationToken,
				prepared)
			.ConfigureAwait(false);
	}

	private async Task<ProjectContextWriteResult> WriteCompleteRedactedAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		Stream destination,
		CancellationToken cancellationToken,
		bool plain,
		bool useUnifiedContentHeaders,
		OutputPathRedactionDecision? pathRedaction,
		IProgress<ProjectCopyExportProgress>? writeProgress)
	{
		var context = CreateTransformationContext(plan)!;
		var preparer = new SecretRedactionOutputPreparer(contentAnalyzer);
		await using var prepared = await preparer
			.PrepareAsync(context, plan.IncludedFiles, cancellationToken)
			.ConfigureAwait(false);
		var analyzer = new PreparedSecretFileContentAnalyzer(contentAnalyzer, prepared);
		var service = new ProjectContextDocumentService(
			treeExportService,
			analyzer,
			omissionMessageProvider,
			secretRedactionSession: null,
			codeCompressionSession: null,
			outputPathRedactionDecision: pathRedaction);
		await service.WriteCompleteAsync(
				plan,
				view,
				format,
				destination,
				cancellationToken,
				plain,
				useUnifiedContentHeaders,
				writeProgress)
			.ConfigureAwait(false);
		return new ProjectContextWriteResult(prepared.UnscannableFiles);
	}

	private async Task WriteCompleteTextAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		Stream destination,
		bool plain,
		OutputPathRedactionDecision? pathRedaction,
		Func<string, string>? contentPathMapper,
		IProgress<ProjectCopyExportProgress>? writeProgress,
		CancellationToken cancellationToken)
	{
		await using var writer = CreateStreamWriter(destination);
		var hasOutput = false;
		var includesContent = IncludesContent(view) && plan.IncludedFiles.Count > 0;
		if (IncludesTree(view))
		{
			await WriteCompleteTreeAsync(
					writer,
					plan,
					plain,
					pathRedaction,
					includeFinalLineEnding: includesContent,
					cancellationToken)
				.ConfigureAwait(false);
			hasOutput = true;
		}

		if (includesContent)
		{
			for (var index = 0; index < plan.IncludedFiles.Count; index++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var path = plan.IncludedFiles[index];
				await using var snapshot = await OpenSourceSnapshotAsync(
						plan.SourceRoot,
						path,
						cancellationToken)
					.ConfigureAwait(false);
				var file = CreateCompleteFileDocument(
					path,
					snapshot.Result,
					contentPathMapper,
					pathRedaction);
				if (hasOutput)
				{
					await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
					await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
				}

				await writer.WriteAsync(
						SingleLineTextEscaping.Escape(file.Path).AsMemory(),
						cancellationToken)
					.ConfigureAwait(false);
				await writer.WriteAsync(":".AsMemory(), cancellationToken).ConfigureAwait(false);
				var isLast = index == plan.IncludedFiles.Count - 1;
				var charactersToWrite = file.Classification == FileContentClassification.Text
					? Math.Max(
						0,
						(file.Metrics?.CharCount ?? 0) -
						(isLast ? file.Metrics?.TrailingNewlineChars ?? 0 : 0))
					: GetTextContent(file).Length;
				if (charactersToWrite > 0 || !isLast)
				{
					await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
					await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
					if (file.Classification == FileContentClassification.Text)
					{
						await snapshot.CopyTextToAsync(
								charactersToWrite,
								(chunk, token) => new ValueTask(
									writer.WriteAsync(chunk, token)),
								cancellationToken)
							.ConfigureAwait(false);
					}
					else
					{
						await writer.WriteAsync(
								GetTextContent(file).AsMemory(),
								cancellationToken)
							.ConfigureAwait(false);
					}
				}
				hasOutput = true;
				ReportProgress(writeProgress, index + 1, plan.IncludedFiles.Count);
			}
		}

		await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task WriteCompleteMarkdownAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		Stream destination,
		bool plain,
		OutputPathRedactionDecision? pathRedaction,
		Func<string, string>? contentPathMapper,
		IProgress<ProjectCopyExportProgress>? writeProgress,
		CancellationToken cancellationToken)
	{
		await using var writer = CreateStreamWriter(destination);
		await writer.WriteAsync("# ".AsMemory(), cancellationToken).ConfigureAwait(false);
		await writer.WriteAsync(EscapeMarkdownHeading(GetProjectName(plan)).AsMemory(), cancellationToken)
			.ConfigureAwait(false);

		if (IncludesTree(view))
		{
			await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
			await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
			await WriteLineAsync(writer, "## Project tree", cancellationToken).ConfigureAwait(false);
			await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
			await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
			var fence = new string(
				'`',
				Math.Max(
					3,
					treeExportService.CalculateFullTreeLongestBacktickRun(
						plan.SourceRoot,
						plan.ProjectedTree,
						GetDocumentRoot(plan, pathRedaction),
						GetProjectName(plan),
						cancellationToken: cancellationToken) + 1));
			await writer.WriteAsync(fence.AsMemory(), cancellationToken).ConfigureAwait(false);
			await WriteLineAsync(writer, "text", cancellationToken).ConfigureAwait(false);
			await WriteCompleteTreeAsync(
					writer,
					plan,
					plain,
					pathRedaction,
					includeFinalLineEnding: false,
					cancellationToken)
				.ConfigureAwait(false);
			await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
			await writer.WriteAsync(fence.AsMemory(), cancellationToken).ConfigureAwait(false);
		}

		if (IncludesContent(view))
		{
			var processedFiles = 0;
			foreach (var path in plan.IncludedFiles)
			{
				cancellationToken.ThrowIfCancellationRequested();
				await using var snapshot = await OpenSourceSnapshotAsync(
						plan.SourceRoot,
						path,
						cancellationToken)
					.ConfigureAwait(false);
				var file = CreateCompleteFileDocument(
					path,
					snapshot.Result,
					contentPathMapper,
					pathRedaction);
				await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
				await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
				await writer.WriteAsync("## ".AsMemory(), cancellationToken).ConfigureAwait(false);
				await WriteLineAsync(writer, BuildMarkdownCodeSpan(file.Path), cancellationToken)
					.ConfigureAwait(false);
				await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
				if (file.Classification != FileContentClassification.Text)
				{
					await writer.WriteAsync(
							$"_{GetOmissionText(file.Classification)}_".AsMemory(),
							cancellationToken)
						.ConfigureAwait(false);
				}
				else
				{
					var fence = new string(
						'`',
						Math.Max(3, (file.Metrics?.LongestBacktickRun ?? 0) + 1));
					await writer.WriteAsync(fence.AsMemory(), cancellationToken).ConfigureAwait(false);
					await WriteLineAsync(
							writer,
							ResolveFenceLanguage(file.Path),
							cancellationToken)
						.ConfigureAwait(false);
					await snapshot.CopyTextToAsync(
							file.Metrics?.CharCount ?? 0,
							(chunk, token) => new ValueTask(
								writer.WriteAsync(chunk, token)),
							cancellationToken)
						.ConfigureAwait(false);
					await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
					await writer.WriteAsync(fence.AsMemory(), cancellationToken).ConfigureAwait(false);
				}
				ReportProgress(writeProgress, ++processedFiles, plan.IncludedFiles.Count);
			}
		}

		await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task WriteCompleteJsonAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		Stream destination,
		OutputPathRedactionDecision? pathRedaction,
		Func<string, string>? contentPathMapper,
		IProgress<ProjectCopyExportProgress>? writeProgress,
		CancellationToken cancellationToken)
	{
		using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions
		{
			Indented = true,
			Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
			MaxDepth = int.MaxValue
		});

		writer.WriteStartObject();
		writer.WriteNumber("schemaVersion", SchemaVersion);
		writer.WriteString("kind", Kind);
		writer.WriteStartObject("project");
		writer.WriteString("root", NormalizePath(GetDocumentRoot(plan, pathRedaction)));
		writer.WriteString("name", GetProjectName(plan));
		WriteRepositorySource(writer, plan.SourceIdentity);
		writer.WriteEndObject();
		WriteSelection(writer, plan);
		WriteMetrics(writer, plan);
		writer.WritePropertyName("tree");
		if (IncludesTree(view))
		{
			await WriteTreeNodeAsync(
					writer,
					plan.ProjectedTree,
					plan.SourceRoot,
					cancellationToken)
				.ConfigureAwait(false);
		}
		else
			writer.WriteNullValue();
		writer.WriteStartArray("files");
		if (IncludesContent(view))
		{
			var processedFiles = 0;
			foreach (var path in plan.IncludedFiles)
			{
				cancellationToken.ThrowIfCancellationRequested();
				await using var snapshot = await OpenSourceSnapshotAsync(
						plan.SourceRoot,
						path,
						cancellationToken)
					.ConfigureAwait(false);
				var file = CreateCompleteFileDocument(
					path,
					snapshot.Result,
					contentPathMapper,
					pathRedaction);
				writer.WriteStartObject();
				writer.WriteString("path", file.Path);
				writer.WriteBoolean("isBinary", file.IsBinary);
				writer.WriteString("classification", ToToken(file.Classification));
				if (file.Classification != FileContentClassification.Text)
				{
					writer.WriteNull("content");
				}
				else
				{
					writer.WritePropertyName("content");
					await snapshot.CopyTextToAsync(
							file.Metrics?.CharCount ?? 0,
							async (chunk, token) =>
							{
								writer.WriteStringValueSegment(
									chunk.Span,
									isFinalSegment: false);
								await writer.FlushAsync(token).ConfigureAwait(false);
							},
							cancellationToken)
						.ConfigureAwait(false);
					writer.WriteStringValueSegment(
						ReadOnlySpan<char>.Empty,
						isFinalSegment: true);
				}
				writer.WriteEndObject();
				ReportProgress(writeProgress, ++processedFiles, plan.IncludedFiles.Count);
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
		OutputPathRedactionDecision? pathRedaction,
		Func<string, string>? contentPathMapper,
		IProgress<ProjectCopyExportProgress>? writeProgress,
		CancellationToken cancellationToken)
	{
		using var writer = XmlWriter.Create(destination, new XmlWriterSettings
		{
			Indent = true,
			OmitXmlDeclaration = false,
			Encoding = Utf8WithoutBom,
			CloseOutput = false,
			Async = true
		});

		writer.WriteStartDocument();
		writer.WriteStartElement("devprojexContext");
		writer.WriteAttributeString("schemaVersion", XmlConvert.ToString(SchemaVersion));
		writer.WriteAttributeString("kind", Kind);
		writer.WriteStartElement("project");
		WriteSanitizedXmlElementString(
			writer,
			"root",
			NormalizePath(GetDocumentRoot(plan, pathRedaction)));
		WriteSanitizedXmlElementString(writer, "name", GetProjectName(plan));
		WriteRepositorySourceXml(writer, plan.SourceIdentity);
		writer.WriteEndElement();
		WriteSelectionXml(writer, plan);
		WriteMetricsXml(writer, plan);
		writer.WriteStartElement("tree");
		if (IncludesTree(view))
		{
			await WriteTreeNodeXmlAsync(
					writer,
					plan.ProjectedTree,
					plan.SourceRoot,
					cancellationToken)
				.ConfigureAwait(false);
		}
		writer.WriteEndElement();
		writer.WriteStartElement("files");
		if (IncludesContent(view))
		{
			var processedFiles = 0;
			foreach (var path in plan.IncludedFiles)
			{
				cancellationToken.ThrowIfCancellationRequested();
				await using var snapshot = await OpenSourceSnapshotAsync(
						plan.SourceRoot,
						path,
						cancellationToken)
					.ConfigureAwait(false);
				var file = CreateCompleteFileDocument(
					path,
					snapshot.Result,
					contentPathMapper,
					pathRedaction);
				writer.WriteStartElement("file");
				WriteSanitizedXmlAttributeString(writer, "path", file.Path);
				writer.WriteAttributeString("isBinary", XmlConvert.ToString(file.IsBinary));
				writer.WriteAttributeString("classification", ToToken(file.Classification));
				if (file.Classification == FileContentClassification.Text)
				{
					writer.WriteStartElement("content");
					await snapshot.CopyTextToAsync(
							file.Metrics?.CharCount ?? 0,
							async (chunk, _) =>
							{
								if (TrySanitizeXmlText(chunk.Span, out var sanitized))
								{
									await writer.WriteStringAsync(sanitized)
										.ConfigureAwait(false);
								}
								else if (MemoryMarshal.TryGetArray(chunk, out var segment))
								{
									await writer.WriteCharsAsync(
											segment.Array!,
											segment.Offset,
											segment.Count)
										.ConfigureAwait(false);
								}
								else
								{
									await writer.WriteStringAsync(chunk.ToString())
										.ConfigureAwait(false);
								}
							},
							cancellationToken)
						.ConfigureAwait(false);
					writer.WriteEndElement();
				}
				writer.WriteEndElement();
				ReportProgress(writeProgress, ++processedFiles, plan.IncludedFiles.Count);
			}
		}
		writer.WriteEndElement();
		writer.WriteStartElement("diagnostics");
		foreach (var diagnostic in plan.Diagnostics)
		{
			writer.WriteStartElement("diagnostic");
			WriteSanitizedXmlAttributeString(writer, "code", diagnostic.Code);
			writer.WriteAttributeString("severity", ToToken(diagnostic.Severity));
			if (!string.IsNullOrWhiteSpace(diagnostic.Path))
				WriteSanitizedXmlAttributeString(writer, "path", NormalizePath(diagnostic.Path));
			WriteSanitizedXmlString(writer, diagnostic.Message);
			writer.WriteEndElement();
		}
		writer.WriteEndElement();
		WriteSanitizedXmlElementString(writer, "fingerprint", plan.Fingerprint);
		writer.WriteEndElement();
		writer.WriteEndDocument();
		await writer.FlushAsync()
			.ConfigureAwait(false);
	}

	private static void ReportProgress(
		IProgress<ProjectCopyExportProgress>? progress,
		int processedFiles,
		int totalFiles)
	{
		if (progress is null)
			return;
		var percentage = totalFiles == 0
			? 100d
			: Math.Clamp(processedFiles * 100d / totalFiles, 0d, 100d);
		progress.Report(new ProjectCopyExportProgress(
			processedFiles,
			totalFiles,
			BytesWritten: 0,
			Percentage: percentage));
	}

	private async ValueTask<IFileContentSnapshot> OpenSourceSnapshotAsync(
		string projectRoot,
		string path,
		CancellationToken cancellationToken)
	{
		var classification = ProjectSourcePathPolicy.ClassifyUnavailable(projectRoot, path);
		if (classification is { } unavailable)
			return new UnavailableSourceSnapshot(unavailable);

		var snapshot = await contentAnalyzer
			.OpenCompleteSnapshotAsync(path, cancellationToken)
			.ConfigureAwait(false);
		classification = ProjectSourcePathPolicy.ClassifyUnavailable(projectRoot, path);
		if (classification is null)
			return snapshot;

		await snapshot.DisposeAsync().ConfigureAwait(false);
		return new UnavailableSourceSnapshot(classification.Value);
	}

	private async ValueTask<FileContentReadResult> ReadSourceClassifiedAsync(
		string projectRoot,
		string path,
		long maximumFileBytes,
		CancellationToken cancellationToken)
	{
		var classification = ProjectSourcePathPolicy.ClassifyUnavailable(projectRoot, path);
		if (classification is { } unavailable)
			return new FileContentReadResult(unavailable);

		var result = await contentAnalyzer
			.ReadClassifiedAsync(path, maximumFileBytes, cancellationToken)
			.ConfigureAwait(false);
		classification = ProjectSourcePathPolicy.ClassifyUnavailable(projectRoot, path);
		return classification is { } unavailableAfterRead
			? new FileContentReadResult(unavailableAfterRead)
			: result;
	}

	private static ContextFileDocument CreateCompleteFileDocument(
		string path,
		FileContentMetricsResult result,
		Func<string, string>? contentPathMapper,
		OutputPathRedactionDecision? pathRedaction)
	{
		var displayPath = contentPathMapper is null
			? path
			: MapContentPath(contentPathMapper, path);
		displayPath = OutputRootPathPresentation.ResolvePath(displayPath, pathRedaction).Text;
		return new ContextFileDocument(
			displayPath,
			result.Classification,
			Content: null,
			Metrics: result.Metrics);
	}

	private static string MapContentPath(Func<string, string> contentPathMapper, string path)
	{
		try
		{
			var mapped = contentPathMapper(path);
			return string.IsNullOrEmpty(mapped) ? path : mapped;
		}
		catch
		{
			return path;
		}
	}

	private static StreamWriter CreateStreamWriter(Stream destination) =>
		new(destination, Utf8WithoutBom, bufferSize: 8192, leaveOpen: true);

	private Task WriteCompleteTreeAsync(
		TextWriter writer,
		ProjectContextPlan plan,
		bool plain,
		OutputPathRedactionDecision? pathRedaction,
		bool includeFinalLineEnding,
		CancellationToken cancellationToken) =>
		plain
			? treeExportService.WriteFullTreePlainAsync(
				writer,
				plan.SourceRoot,
				plan.ProjectedTree,
				GetDocumentRoot(plan, pathRedaction),
				GetProjectName(plan),
				includeFinalLineEnding: includeFinalLineEnding,
				cancellationToken: cancellationToken)
			: treeExportService.WriteFullTreeAsync(
				writer,
				plan.SourceRoot,
				plan.ProjectedTree,
				GetDocumentRoot(plan, pathRedaction),
				GetProjectName(plan),
				includeFinalLineEnding: includeFinalLineEnding,
				cancellationToken: cancellationToken);

	private static async Task WriteLineAsync(
		TextWriter writer,
		string? value,
		CancellationToken cancellationToken)
	{
		if (value is not null)
			await writer.WriteAsync(value.AsMemory(), cancellationToken).ConfigureAwait(false);
		await writer.WriteAsync(Environment.NewLine.AsMemory(), cancellationToken).ConfigureAwait(false);
	}

	private string GetTextContent(ContextFileDocument file) =>
		file.Classification == FileContentClassification.Text
			? file.Content ?? string.Empty
			: $"[{GetOmissionText(file.Classification)}]";

	private static void WriteContextFileJson(Utf8JsonWriter writer, ContextFileDocument file)
	{
		writer.WriteStartObject();
		writer.WriteString("path", file.Path);
		writer.WriteBoolean("isBinary", file.IsBinary);
		writer.WriteString("classification", ToToken(file.Classification));
		if (file.Classification != FileContentClassification.Text)
			writer.WriteNull("content");
		else
			writer.WriteString("content", file.Content);
		writer.WriteEndObject();
	}

	private static void WriteContextFileXml(XmlWriter writer, ContextFileDocument file)
	{
		writer.WriteStartElement("file");
		WriteSanitizedXmlAttributeString(writer, "path", file.Path);
		writer.WriteAttributeString("isBinary", XmlConvert.ToString(file.IsBinary));
		writer.WriteAttributeString("classification", ToToken(file.Classification));
		if (file.Classification == FileContentClassification.Text)
			WriteSanitizedXmlElementString(writer, "content", file.Content ?? string.Empty);
		writer.WriteEndElement();
	}

	private async Task<ContextFileReadResult> ReadFilesAsync(
		ProjectContextPlan plan,
		ProjectContextDocumentLimits limits,
		CancellationToken cancellationToken,
		PreparedSecretRedactionOutput? prepared = null)
	{
		var maximumFiles = Math.Max(0, limits.MaximumFiles);
		var maximumCharacters = Math.Max(0, limits.MaximumCharacters);
		var maximumFileBytes = Math.Max(0, limits.MaximumFileBytes);
		var files = new List<ContextFileDocument>(
			Math.Min(plan.IncludedFiles.Count, maximumFiles));
		var remainingCharacters = maximumCharacters;
		var isTruncated = plan.IncludedFiles.Count > maximumFiles;
		foreach (var path in plan.IncludedFiles.Take(maximumFiles))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relativePath = NormalizeRelativePath(plan.SourceRoot, path);
			var result = await ReadSourceClassifiedAsync(
					plan.SourceRoot,
					path,
					maximumFileBytes,
					cancellationToken)
				.ConfigureAwait(false);
			var content = result.Content;
			if (!result.IsText || content is null)
			{
				files.Add(new ContextFileDocument(
					relativePath,
					result.Classification,
					Content: null));
				continue;
			}

			if (content.IsEstimated)
			{
				files.Add(new ContextFileDocument(
					relativePath,
					FileContentClassification.TooLarge,
					Content: null,
					IsOmitted: true));
				isTruncated = true;
				continue;
			}

			var fileContent = content.Content;
			var truncatedAtCharacterBoundary = false;
			if (fileContent.Length > remainingCharacters)
			{
				fileContent = fileContent[..ClampToCompleteUnicodeScalar(fileContent, remainingCharacters)];
				isTruncated = true;
				truncatedAtCharacterBoundary = true;
			}
			if (prepared is not null)
			{
				var preparedFile = prepared.GetFile(path);
				var completeLength = preparedFile.ClampLengthToCompleteRedactions(fileContent.Length);
				if (completeLength != fileContent.Length)
				{
					fileContent = fileContent[..completeLength];
					isTruncated = true;
					truncatedAtCharacterBoundary = true;
				}

			}
			files.Add(new ContextFileDocument(
				relativePath,
				FileContentClassification.Text,
				fileContent,
				IsTruncated: fileContent.Length != content.Content.Length));
			remainingCharacters -= fileContent.Length;
			// Once a file is truncated, later files are not part of the bounded prefix.
			// Continuing merely because a placeholder was removed at the boundary would
			// make the limit select non-contiguous content and violate deterministic ordering.
			if (truncatedAtCharacterBoundary)
				break;
			if (remainingCharacters == 0 &&
			    files.Count < Math.Min(plan.IncludedFiles.Count, maximumFiles))
			{
				isTruncated = true;
				break;
			}
		}

		return new ContextFileReadResult(files, isTruncated);
	}

	private static int ClampToCompleteUnicodeScalar(string value, int maximumLength)
	{
		var length = Math.Min(value.Length, maximumLength);
		return length > 0 &&
		       length < value.Length &&
		       char.IsHighSurrogate(value[length - 1]) &&
		       char.IsLowSurrogate(value[length])
			? length - 1
			: length;
	}

	private string BuildText(
		ProjectContextPlan plan,
		ProjectContextView view,
		IReadOnlyList<ContextFileDocument> files,
		bool truncated,
		CancellationToken cancellationToken)
	{
		var output = new StringBuilder();
		if (IncludesTree(view))
		{
			output.Append(treeExportService.BuildFullTreeWithCancellation(
				plan.SourceRoot,
				plan.ProjectedTree,
				TreeTextFormat.Ascii,
				GetDocumentRoot(plan),
				GetProjectName(plan),
				includeRootPath: true,
				cancellationToken: cancellationToken));
		}
		AppendTextFiles(output, files);
		AppendTruncationNotice(output, truncated);
		TrailingLineEndingTrimming.Trim(output);
		return output.ToString();
	}

	private string BuildMarkdown(
		ProjectContextPlan plan,
		ProjectContextView view,
		IReadOnlyList<ContextFileDocument> files,
		bool truncated,
		CancellationToken cancellationToken)
	{
		var output = new StringBuilder();
		output.Append("# ").AppendLine(EscapeMarkdownHeading(GetProjectName(plan)));
		output.AppendLine();
		if (IncludesTree(view))
		{
			output.AppendLine("## Project tree");
			output.AppendLine();
			var tree = treeExportService.BuildFullTreeWithCancellation(
				plan.SourceRoot,
				plan.ProjectedTree,
				TreeTextFormat.Ascii,
				GetDocumentRoot(plan),
				GetProjectName(plan),
				includeRootPath: true,
				cancellationToken: cancellationToken);
			AppendMarkdownFence(
				output,
				tree.AsSpan(0, TrailingLineEndingTrimming.GetTrimmedLength(tree)),
				"text");
		}

		foreach (var file in files)
		{
			output.AppendLine();
			output.Append("## ").AppendLine(BuildMarkdownCodeSpan(file.Path));
			output.AppendLine();
			if (file.Classification != FileContentClassification.Text &&
			    !file.IsOmitted)
			{
				output.Append('_')
					.Append(GetOmissionText(file.Classification))
					.AppendLine("_");
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
		TrailingLineEndingTrimming.Trim(output);
		return output.ToString();
	}

	private string BuildJson(
		ProjectContextPlan plan,
		ProjectContextView view,
		IReadOnlyList<ContextFileDocument> files,
		bool truncated)
	{
		var buffer = new ArrayBufferWriter<byte>();
		using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
		{
			Indented = true,
			Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
			MaxDepth = int.MaxValue
		});

		writer.WriteStartObject();
		writer.WriteNumber("schemaVersion", SchemaVersion);
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
			writer.WriteString("classification", ToToken(file.Classification));
			if (file.Classification != FileContentClassification.Text || file.IsOmitted)
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

	private string BuildXml(
		ProjectContextPlan plan,
		ProjectContextView view,
		IReadOnlyList<ContextFileDocument> files,
		bool truncated)
	{
		var output = new StringBuilder();
		using var stringWriter = new StringWriter(
			output,
			System.Globalization.CultureInfo.InvariantCulture);
		using var encodingWriter = new EncodingReportingTextWriter(
			stringWriter,
			Utf8WithoutBom);
		using var writer = XmlWriter.Create(encodingWriter, new XmlWriterSettings
		{
			Indent = true,
			OmitXmlDeclaration = false,
			Encoding = Utf8WithoutBom
		});

		writer.WriteStartDocument();
		writer.WriteStartElement("devprojexContext");
		writer.WriteAttributeString("schemaVersion", XmlConvert.ToString(SchemaVersion));
		writer.WriteAttributeString("kind", Kind);
		writer.WriteStartElement("project");
		WriteSanitizedXmlElementString(writer, "root", NormalizePath(GetDocumentRoot(plan)));
		WriteSanitizedXmlElementString(writer, "name", GetProjectName(plan));
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
			WriteSanitizedXmlAttributeString(writer, "path", file.Path);
			writer.WriteAttributeString("isBinary", XmlConvert.ToString(file.IsBinary));
			writer.WriteAttributeString("classification", ToToken(file.Classification));
			if (file.IsOmitted)
				writer.WriteAttributeString("omitted", XmlConvert.ToString(true));
			if (file.IsTruncated)
				writer.WriteAttributeString("truncated", XmlConvert.ToString(true));
			if (file.Classification == FileContentClassification.Text && !file.IsOmitted)
				WriteSanitizedXmlElementString(writer, "content", file.Content ?? string.Empty);
			writer.WriteEndElement();
		}
		writer.WriteEndElement();
		writer.WriteStartElement("diagnostics");
		foreach (var diagnostic in plan.Diagnostics)
		{
			writer.WriteStartElement("diagnostic");
			WriteSanitizedXmlAttributeString(writer, "code", diagnostic.Code);
			writer.WriteAttributeString("severity", ToToken(diagnostic.Severity));
			if (!string.IsNullOrWhiteSpace(diagnostic.Path))
				WriteSanitizedXmlAttributeString(writer, "path", NormalizePath(diagnostic.Path));
			WriteSanitizedXmlString(writer, diagnostic.Message);
			writer.WriteEndElement();
		}
		writer.WriteEndElement();
		if (truncated)
			writer.WriteElementString("truncated", XmlConvert.ToString(true));
		WriteSanitizedXmlElementString(writer, "fingerprint", plan.Fingerprint);
		writer.WriteEndElement();
		writer.WriteEndDocument();
		writer.Flush();
		return output.ToString();
	}

	private void AppendTextFiles(
		StringBuilder output,
		IReadOnlyList<ContextFileDocument> files)
	{
		foreach (var file in files)
		{
			if (output.Length > 0)
				output.AppendLine().AppendLine();

			output.Append(SingleLineTextEscaping.Escape(file.Path)).AppendLine(":");
			output.AppendLine();
			output.Append(file.IsOmitted
					? "[Large text file; content omitted from bounded preview]"
					: file.Classification == FileContentClassification.Text
						? file.Content
						: $"[{GetOmissionText(file.Classification)}]");
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
		int maximumNodes,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var remaining = Math.Max(1, maximumNodes);
		var truncated = false;
		remaining--;
		if (!root.IsDirectory || root.Children.Count == 0)
			return (root, false);

		var frames = new Stack<BoundedTreeCloneFrame>();
		frames.Push(new BoundedTreeCloneFrame(root));
		TreeNodeDescriptor? completedTree = null;

		while (frames.TryPeek(out var frame))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (remaining > 0 && frame.NextChildIndex < frame.Source.Children.Count)
			{
				var child = frame.Source.Children[frame.NextChildIndex++];
				remaining--;
				if (!child.IsDirectory || child.Children.Count == 0)
				{
					frame.Children.Add(child);
					continue;
				}

				if (remaining == 0)
				{
					truncated = true;
					frame.Children.Add(child with { Children = [] });
					continue;
				}

				frames.Push(new BoundedTreeCloneFrame(child));
				continue;
			}

			if (frame.NextChildIndex < frame.Source.Children.Count)
				truncated = true;

			var completedNode = frame.Source with { Children = frame.Children };
			frames.Pop();
			if (frames.TryPeek(out var parent))
				parent.Children.Add(completedNode);
			else
				completedTree = completedNode;
		}

		return (completedTree!, truncated);
	}

	private static void AppendMarkdownFence(
		StringBuilder output,
		ReadOnlySpan<char> content,
		string language)
	{
		var fence = new string('`', Math.Max(3, FindLongestBacktickRun(content) + 1));
		output.Append(fence).AppendLine(language);
		output.Append(content).AppendLine();
		output.AppendLine(fence);
	}

	private static int FindLongestBacktickRun(ReadOnlySpan<char> value)
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
		var frames = new List<ContextTreeWriteFrame> { new(node) };
		while (frames.Count > 0)
		{
			var frame = frames[^1];
			if (!frame.Started)
			{
				frame.Started = true;
				var current = frame.Node;
				writer.WriteStartObject();
				writer.WriteString("path", NormalizeRelativePath(sourceRoot, current.FullPath));
				writer.WriteString("name", current.DisplayName);
				writer.WriteString("type", current.IsDirectory ? "directory" : "file");
				if (!current.IsDirectory)
				{
					writer.WriteEndObject();
					frames.RemoveAt(frames.Count - 1);
					continue;
				}
				writer.WriteStartArray("children");
			}

			if (frame.NextChildIndex < frame.Node.Children.Count)
			{
				frames.Add(new ContextTreeWriteFrame(
					frame.Node.Children[frame.NextChildIndex++]));
				continue;
			}

			writer.WriteEndArray();
			writer.WriteEndObject();
			frames.RemoveAt(frames.Count - 1);
		}
	}

	private static async Task WriteTreeNodeAsync(
		Utf8JsonWriter writer,
		TreeNodeDescriptor node,
		string sourceRoot,
		CancellationToken cancellationToken)
	{
		var frames = new List<ContextTreeWriteFrame> { new(node) };
		var processedNodes = 0;
		while (frames.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var frame = frames[^1];
			if (!frame.Started)
			{
				frame.Started = true;
				var current = frame.Node;
				writer.WriteStartObject();
				writer.WriteString("path", NormalizeRelativePath(sourceRoot, current.FullPath));
				writer.WriteString("name", current.DisplayName);
				writer.WriteString("type", current.IsDirectory ? "directory" : "file");
				if (!current.IsDirectory)
				{
					writer.WriteEndObject();
					frames.RemoveAt(frames.Count - 1);
				}
				else
				{
					writer.WriteStartArray("children");
				}

				if (++processedNodes % StructuredTreeFlushNodeInterval == 0)
					await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
				if (!current.IsDirectory)
					continue;
			}

			if (frame.NextChildIndex < frame.Node.Children.Count)
			{
				frames.Add(new ContextTreeWriteFrame(
					frame.Node.Children[frame.NextChildIndex++]));
				continue;
			}

			writer.WriteEndArray();
			writer.WriteEndObject();
			frames.RemoveAt(frames.Count - 1);
		}
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
		WriteSanitizedXmlElementString(
			writer,
			"gitMode",
			ProjectSelectionTokens.ToToken(plan.Selection.GitMode!.Value));
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
		WriteSanitizedXmlElementString(writer, "repositoryUrl", identity.RepositoryUrl);
		if (!string.IsNullOrWhiteSpace(identity.Branch))
			WriteSanitizedXmlElementString(writer, "branch", identity.Branch);
		if (!string.IsNullOrWhiteSpace(identity.CommitHash))
			WriteSanitizedXmlElementString(writer, "commit", identity.CommitHash);
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
		var frames = new List<ContextTreeWriteFrame> { new(node) };
		while (frames.Count > 0)
		{
			var frame = frames[^1];
			if (!frame.Started)
			{
				frame.Started = true;
				writer.WriteStartElement(frame.Node.IsDirectory ? "directory" : "file");
				WriteSanitizedXmlAttributeString(
					writer,
					"path",
					NormalizeRelativePath(sourceRoot, frame.Node.FullPath));
				WriteSanitizedXmlAttributeString(writer, "name", frame.Node.DisplayName);
			}

			if (frame.NextChildIndex < frame.Node.Children.Count)
			{
				frames.Add(new ContextTreeWriteFrame(
					frame.Node.Children[frame.NextChildIndex++]));
				continue;
			}

			writer.WriteEndElement();
			frames.RemoveAt(frames.Count - 1);
		}
	}

	private static async Task WriteTreeNodeXmlAsync(
		XmlWriter writer,
		TreeNodeDescriptor node,
		string sourceRoot,
		CancellationToken cancellationToken)
	{
		var frames = new List<ContextTreeWriteFrame> { new(node) };
		var processedNodes = 0;
		while (frames.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var frame = frames[^1];
			if (!frame.Started)
			{
				frame.Started = true;
				writer.WriteStartElement(frame.Node.IsDirectory ? "directory" : "file");
				WriteSanitizedXmlAttributeString(
					writer,
					"path",
					NormalizeRelativePath(sourceRoot, frame.Node.FullPath));
				WriteSanitizedXmlAttributeString(writer, "name", frame.Node.DisplayName);
				if (++processedNodes % StructuredTreeFlushNodeInterval == 0)
					await writer.FlushAsync().ConfigureAwait(false);
			}

			if (frame.NextChildIndex < frame.Node.Children.Count)
			{
				frames.Add(new ContextTreeWriteFrame(
					frame.Node.Children[frame.NextChildIndex++]));
				continue;
			}

			writer.WriteEndElement();
			frames.RemoveAt(frames.Count - 1);
		}
	}

	private sealed class BoundedTreeCloneFrame(TreeNodeDescriptor source)
	{
		public TreeNodeDescriptor Source { get; } = source;
		public List<TreeNodeDescriptor> Children { get; } = [];
		public int NextChildIndex { get; set; }
	}

	private sealed class ContextTreeWriteFrame(TreeNodeDescriptor node)
	{
		public TreeNodeDescriptor Node { get; } = node;
		public bool Started { get; set; }
		public int NextChildIndex { get; set; }
	}

	private static void WriteStringCollectionXml(
		XmlWriter writer,
		string containerName,
		string itemName,
		IEnumerable<string> values)
	{
		writer.WriteStartElement(containerName);
		foreach (var value in values)
			WriteSanitizedXmlElementString(writer, itemName, value);
		writer.WriteEndElement();
	}

	private static void WriteSanitizedXmlAttributeString(
		XmlWriter writer,
		string localName,
		string value) =>
		writer.WriteAttributeString(localName, SanitizeXmlText(value));

	private static void WriteSanitizedXmlElementString(
		XmlWriter writer,
		string localName,
		string value) =>
		writer.WriteElementString(localName, SanitizeXmlText(value));

	private static void WriteSanitizedXmlString(XmlWriter writer, string value) =>
		writer.WriteString(SanitizeXmlText(value));

	private static string SanitizeXmlText(string value) =>
		TrySanitizeXmlText(value.AsSpan(), out var sanitized)
			? sanitized
			: value;

	private static bool TrySanitizeXmlText(
		ReadOnlySpan<char> value,
		out string sanitized)
	{
		StringBuilder? builder = null;
		for (var index = 0; index < value.Length; index++)
		{
			var character = value[index];
			if (char.IsHighSurrogate(character) &&
			    index + 1 < value.Length &&
			    char.IsLowSurrogate(value[index + 1]))
			{
				if (builder is not null)
				{
					builder.Append(character);
					builder.Append(value[++index]);
				}
				else
				{
					index++;
				}
				continue;
			}

			if (XmlConvert.IsXmlChar(character))
			{
				builder?.Append(character);
				continue;
			}

			builder ??= new StringBuilder(value.Length)
				.Append(value[..index]);
			builder.Append('\uFFFD');
		}

		sanitized = builder?.ToString() ?? string.Empty;
		return builder is not null;
	}

	private static bool IncludesTree(ProjectContextView view) =>
		view is ProjectContextView.Tree or ProjectContextView.TreeContent;

	private static bool IncludesContent(ProjectContextView view) =>
		view is ProjectContextView.Content or ProjectContextView.TreeContent;

	private static void ValidateView(ProjectContextView view)
	{
		if (view is not (
			    ProjectContextView.Tree or
			    ProjectContextView.Content or
			    ProjectContextView.TreeContent))
		{
			throw new ArgumentOutOfRangeException(nameof(view), view, null);
		}
	}

	private static void ValidateDocumentFormat(ProjectContextDocumentFormat format)
	{
		if (format is not (
			    ProjectContextDocumentFormat.Text or
			    ProjectContextDocumentFormat.Markdown or
			    ProjectContextDocumentFormat.Json or
			    ProjectContextDocumentFormat.Xml))
		{
			throw new ArgumentOutOfRangeException(nameof(format), format, null);
		}
	}

	private static string NormalizeRelativePath(string root, string path)
	{
		var relative = PathUtility.GetPortableRelativePath(root, path);
		return relative == "." ? "." : relative;
	}

	private static string NormalizePath(string path) => PathUtility.NormalizeSeparators(path);

	private static string GetProjectName(ProjectContextPlan plan) =>
		plan.SourceIdentity?.DisplayName is { Length: > 0 } displayName
			? displayName
			: Path.GetFileName(Path.TrimEndingDirectorySeparator(plan.SourceRoot)) is { Length: > 0 } name
			? name
			: "project";

	private static string GetDocumentRoot(ProjectContextPlan plan, bool protectPrivateData = false)
	{
		var displayRootPath = plan.SourceIdentity is
		{
			SourceType: ProjectSourceType.GitClone,
			SourceReference.Length: > 0
		} identity
			? identity.SourceReference
			: plan.SourceRoot;
		return OutputRootPathPresentation.Resolve(
			plan.SourceRoot,
			displayRootPath,
			protectPrivateData && plan.Selection.HidePrivateData == true);
	}

	private static string GetDocumentRoot(
		ProjectContextPlan plan,
		OutputPathRedactionDecision? pathRedaction)
	{
		var displayRootPath = plan.SourceIdentity is
		{
			SourceType: ProjectSourceType.GitClone,
			SourceReference.Length: > 0
		} identity
			? identity.SourceReference
			: plan.SourceRoot;
		return OutputRootPathPresentation.ResolvePath(displayRootPath, pathRedaction).Text;
	}

	private static Func<string, string>? CreateContentPathMapper(
		ProjectContextPlan plan,
		bool useUnifiedContentHeaders,
		ProjectContextView view)
	{
		if (!useUnifiedContentHeaders || view != ProjectContextView.Content)
			return TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(plan.SourceRoot);

		if (plan.SourceIdentity is not
		    {
			    SourceType: ProjectSourceType.GitClone,
			    SourceReference.Length: > 0
		    } identity)
		{
			return null;
		}

		return WebPathPresentation
			.TryCreate(plan.SourceRoot, identity.SourceReference)
			?.MapFilePath;
	}

	private static string EscapeMarkdownHeading(string value) =>
		SingleLineTextEscaping.Escape(value.Replace("\\", "\\\\").Replace("#", "\\#"));

	private static string BuildMarkdownCodeSpan(string value)
	{
		var normalized = SingleLineTextEscaping.Escape(value);
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
		severity switch
		{
			ContextDiagnosticSeverity.Information => "information",
			ContextDiagnosticSeverity.Warning => "warning",
			ContextDiagnosticSeverity.Error => "error",
			_ => throw new ArgumentOutOfRangeException(nameof(severity), severity, null)
		};

	private static string ToToken(FileContentClassification classification) =>
		classification switch
		{
			FileContentClassification.Text => "text",
			FileContentClassification.Binary => "binary",
			FileContentClassification.TooLarge => "too-large",
			FileContentClassification.Unreadable => "unreadable",
			FileContentClassification.AccessDenied => "access-denied",
			FileContentClassification.Missing => "missing",
			FileContentClassification.UnsupportedEncoding => "unsupported-encoding",
			_ => throw new ArgumentOutOfRangeException(
				nameof(classification),
				classification,
				null)
		};

	private string GetOmissionText(FileContentClassification classification)
	{
		if (classification is FileContentClassification.Text)
			throw new ArgumentOutOfRangeException(nameof(classification), classification, null);
		if (!Enum.IsDefined(classification))
			throw new ArgumentOutOfRangeException(nameof(classification), classification, null);

		return omissionMessageProvider?.Invoke(classification) ??
		       classification switch
		{
			FileContentClassification.Binary => "Binary file; content omitted.",
			FileContentClassification.TooLarge => "File is too large for interactive preview.",
			FileContentClassification.Unreadable => "File could not be read.",
			FileContentClassification.AccessDenied => "Access denied while reading file.",
			FileContentClassification.Missing => "File disappeared while it was being read.",
			FileContentClassification.UnsupportedEncoding => "Text encoding is unsupported.",
			_ => throw new ArgumentOutOfRangeException(
				nameof(classification),
				classification,
				null)
		};
	}

	private sealed record ContextFileReadResult(
		IReadOnlyList<ContextFileDocument> Files,
		bool IsTruncated);

	private sealed record ContextFileDocument(
		string Path,
		FileContentClassification Classification,
		string? Content,
		bool IsOmitted = false,
		bool IsTruncated = false,
		TextFileMetrics? Metrics = null)
	{
		public bool IsBinary => Classification == FileContentClassification.Binary;
	}

	private sealed class UnavailableSourceSnapshot(
		FileContentClassification classification) : IFileContentSnapshot
	{
		public FileContentMetricsResult Result { get; } = new(classification);

		public ValueTask CopyTextToAsync(
			int maximumCharacters,
			Func<ReadOnlyMemory<char>, CancellationToken, ValueTask> writeChunk,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException(new IOException("The source snapshot does not contain readable text."));

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

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
