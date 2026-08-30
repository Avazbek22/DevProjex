using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Runtime.InteropServices;
using System.Xml;
using DevProjex.Application.Secrets;
using DevProjex.Application.Services;

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

public sealed record ProjectContextWriteResult(
	IReadOnlyList<UnscannableFile> UnscannableFiles,
	ProjectContextTokenBudgetReport? TokenBudget = null)
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
	private const int MaximumBoundedReadAhead = 8;
	internal const long MaximumCompleteSnapshotReadAheadRetainedBytes = 4L * 1024 * 1024;
	private const long MaximumBoundedReadAheadRetainedBytes = 4L * 1024 * 1024;
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
		bool useSourceMappedStructuredPaths = false,
		IProgress<ProjectCopyExportProgress>? writeProgress = null,
		long? maximumEstimatedTokens = null)
	{
		_ = await WriteCompleteWithReportAsync(
				plan,
				view,
				format,
				destination,
				cancellationToken,
				plain,
				useSourceMappedStructuredPaths,
				writeProgress,
				maximumEstimatedTokens)
			.ConfigureAwait(false);
	}

	public async Task<ProjectContextWriteResult> WriteCompleteWithReportAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		Stream destination,
		CancellationToken cancellationToken = default,
		bool plain = false,
		bool useSourceMappedStructuredPaths = false,
		IProgress<ProjectCopyExportProgress>? writeProgress = null,
		long? maximumEstimatedTokens = null)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(destination);
		ValidateView(view);
		ValidateDocumentFormat(format);
		if (!destination.CanWrite)
			throw new ArgumentException("Destination must be writable.", nameof(destination));
		var effectivePathRedaction = outputPathRedactionDecision ??
			OutputRootPathPresentation.CaptureRedactionDecision(CreateTransformationContext(plan));
		var contentPathMapper = format is
			ProjectContextDocumentFormat.Text or ProjectContextDocumentFormat.Markdown
			? TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(plan.SourceRoot)
			: CreateSourceContentPathMapper(plan, useSourceMappedStructuredPaths, view);
		if (ShouldRedact(plan, view))
		{
			return await WriteCompleteRedactedAsync(
					plan,
					view,
					format,
					destination,
					cancellationToken,
					plain,
					useSourceMappedStructuredPaths,
					effectivePathRedaction,
					writeProgress,
					maximumEstimatedTokens)
				.ConfigureAwait(false);
		}
		var tokenBudget = CreateTokenBudget(maximumEstimatedTokens);
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
						tokenBudget,
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
						tokenBudget,
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
						useSourceMappedStructuredPaths,
						writeProgress,
						tokenBudget,
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
						useSourceMappedStructuredPaths,
						writeProgress,
						tokenBudget,
						cancellationToken)
					.ConfigureAwait(false);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(format), format, null);
		}
		return new ProjectContextWriteResult([], tokenBudget?.CreateReport());
	}

	public async Task<ProjectContextWriteResult> WritePreparedCompleteAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		Stream destination,
		PreparedSecretRedactionOutput prepared,
		CancellationToken cancellationToken = default,
		bool plain = false,
		bool useSourceMappedStructuredPaths = false,
		IProgress<ProjectCopyExportProgress>? writeProgress = null,
		long? maximumEstimatedTokens = null)
	{
		ArgumentNullException.ThrowIfNull(prepared);
		var pathRedaction = outputPathRedactionDecision ??
		                    OutputRootPathPresentation.CaptureRedactionDecision(
			                    CreateTransformationContext(plan));
		var analyzer = new PreparedSecretFileContentAnalyzer(contentAnalyzer, prepared);
		plan = await RefreshStructuredContentMetricsAsync(
				plan,
				view,
				format,
				analyzer,
				cancellationToken)
			.ConfigureAwait(false);
		var service = new ProjectContextDocumentService(
			treeExportService,
			analyzer,
			omissionMessageProvider,
			secretRedactionSession: null,
			codeCompressionSession: null,
			outputPathRedactionDecision: pathRedaction);
		return await service.WriteCompleteWithReportAsync(
				plan,
				view,
				format,
				destination,
				cancellationToken,
				plain,
				useSourceMappedStructuredPaths,
				writeProgress,
				maximumEstimatedTokens)
			.ConfigureAwait(false);
	}

	public async Task<ProjectContextWriteResult> EvaluateTokenBudgetAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		long maximumEstimatedTokens,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ValidateView(view);
		var tokenBudget = CreateTokenBudget(maximumEstimatedTokens)!;
		if (!IncludesContent(view))
			return new ProjectContextWriteResult([], tokenBudget.CreateReport());

		var transformationContext = CreateTransformationContext(plan);
		if (transformationContext is null)
		{
			await EvaluateTokenBudgetCoreAsync(
					plan,
					view,
					tokenBudget,
					cancellationToken)
				.ConfigureAwait(false);
			return new ProjectContextWriteResult([], tokenBudget.CreateReport());
		}

		var preparer = new SecretRedactionOutputPreparer(contentAnalyzer);
		await using var prepared = await preparer
			.PrepareAsync(transformationContext, plan.IncludedFiles, cancellationToken)
			.ConfigureAwait(false);
		var service = new ProjectContextDocumentService(
			treeExportService,
			new PreparedSecretFileContentAnalyzer(contentAnalyzer, prepared),
			omissionMessageProvider,
			secretRedactionSession: null,
			codeCompressionSession: null,
			outputPathRedactionDecision: OutputRootPathPresentation.CaptureRedactionDecision(
				transformationContext));
		await service.EvaluateTokenBudgetCoreAsync(
				plan,
				view,
				tokenBudget,
				cancellationToken)
			.ConfigureAwait(false);
		return new ProjectContextWriteResult(prepared.UnscannableFiles, tokenBudget.CreateReport());
	}

	private async Task EvaluateTokenBudgetCoreAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		ProjectContextTokenBudgetAccumulator tokenBudget,
		CancellationToken cancellationToken)
	{
		var effectivePathRedaction = outputPathRedactionDecision ??
			OutputRootPathPresentation.CaptureRedactionDecision(CreateTransformationContext(plan));
		var contentPathMapper = CreateSourceContentPathMapper(
			plan,
			useSourceMappedStructuredPaths: true,
			view);
		await foreach (var source in OpenSourceSnapshotsInOrderAsync(
			               plan.SourceRoot,
			               plan.IncludedFiles,
			               cancellationToken).ConfigureAwait(false))
		{
			await using var snapshot = source.Snapshot;
			var file = CreateCompleteFileDocument(
				source.Path,
				snapshot.Result,
				contentPathMapper,
				effectivePathRedaction);
			tokenBudget.TryInclude(file.Path, file.Metrics?.CharCount ?? 0);
		}
	}

	private static ProjectContextTokenBudgetAccumulator? CreateTokenBudget(
		long? maximumEstimatedTokens) =>
		maximumEstimatedTokens is null
			? null
			: new ProjectContextTokenBudgetAccumulator(maximumEstimatedTokens.Value);

	private static async Task<ProjectContextPlan> RefreshStructuredContentMetricsAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		IFileContentAnalyzer analyzer,
		CancellationToken cancellationToken)
	{
		if (!IncludesContent(view) ||
		    format is not (ProjectContextDocumentFormat.Json or ProjectContextDocumentFormat.Xml))
		{
			return plan;
		}

		var metrics = await ProjectContentMetricsCalculator
			.CalculateAsync(analyzer, plan.IncludedFiles, cancellationToken)
			.ConfigureAwait(false);
		return plan with
		{
			Analysis = plan.Analysis with
			{
				Metrics = plan.Analysis.Metrics with
				{
					Content = new ProjectOutputMetricsReport(
						metrics.Lines,
						metrics.Chars,
						metrics.Tokens)
				}
			}
		};
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
		bool useSourceMappedStructuredPaths,
		OutputPathRedactionDecision? pathRedaction,
		IProgress<ProjectCopyExportProgress>? writeProgress,
		long? maximumEstimatedTokens)
	{
		var context = CreateTransformationContext(plan)!;
		var preparer = new SecretRedactionOutputPreparer(contentAnalyzer);
		await using var prepared = await preparer
			.PrepareAsync(context, plan.IncludedFiles, cancellationToken)
			.ConfigureAwait(false);
		var analyzer = new PreparedSecretFileContentAnalyzer(contentAnalyzer, prepared);
		plan = await RefreshStructuredContentMetricsAsync(
				plan,
				view,
				format,
				analyzer,
				cancellationToken)
			.ConfigureAwait(false);
		var service = new ProjectContextDocumentService(
			treeExportService,
			analyzer,
			omissionMessageProvider,
			secretRedactionSession: null,
			codeCompressionSession: null,
			outputPathRedactionDecision: pathRedaction);
		var writeResult = await service.WriteCompleteWithReportAsync(
				plan,
				view,
				format,
				destination,
				cancellationToken,
				plain,
				useSourceMappedStructuredPaths,
				writeProgress,
				maximumEstimatedTokens)
			.ConfigureAwait(false);
		return new ProjectContextWriteResult(prepared.UnscannableFiles, writeResult.TokenBudget);
	}

	private async Task WriteCompleteTextAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		Stream destination,
		bool plain,
		OutputPathRedactionDecision? pathRedaction,
		Func<string, string>? contentPathMapper,
		IProgress<ProjectCopyExportProgress>? writeProgress,
		ProjectContextTokenBudgetAccumulator? tokenBudget,
		CancellationToken cancellationToken)
	{
		await using var streamWriter = CreateStreamWriter(destination);
		var writer = new TrailingLineEndingTextWriter(streamWriter);
		var hasOutput = false;
		var includesContent = IncludesContent(view) && plan.IncludedFiles.Count > 0;
		if (view == ProjectContextView.Content)
		{
			await writer.WriteAsync(
					ContextRootPresentation.FormatLine(GetDocumentRoot(plan, pathRedaction)).AsMemory(),
					cancellationToken)
				.ConfigureAwait(false);
			hasOutput = true;
		}
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
			await foreach (var source in OpenSourceSnapshotsInOrderAsync(
				               plan.SourceRoot,
				               plan.IncludedFiles,
				               cancellationToken).ConfigureAwait(false))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var index = source.Index;
				var path = source.Path;
				await using var snapshot = source.Snapshot;
				var file = CreateCompleteFileDocument(
					path,
					snapshot.Result,
					contentPathMapper,
					pathRedaction);
				if (tokenBudget is not null &&
				    !tokenBudget.TryInclude(file.Path, file.Metrics?.CharCount ?? 0))
				{
					ReportProgress(writeProgress, index + 1, plan.IncludedFiles.Count);
					continue;
				}
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
				var charactersToWrite = file.Classification == FileContentClassification.Text
					? file.Metrics?.CharCount ?? 0
					: GetTextContent(file).Length;
				await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
				await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
				if (charactersToWrite > 0)
				{
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

		await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task WriteCompleteMarkdownAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		Stream destination,
		bool plain,
		OutputPathRedactionDecision? pathRedaction,
		Func<string, string>? contentPathMapper,
		IProgress<ProjectCopyExportProgress>? writeProgress,
		ProjectContextTokenBudgetAccumulator? tokenBudget,
		CancellationToken cancellationToken)
	{
		await using var writer = CreateStreamWriter(destination);
		await writer.WriteAsync("# ".AsMemory(), cancellationToken).ConfigureAwait(false);
		await writer.WriteAsync(EscapeMarkdownHeading(GetProjectName(plan)).AsMemory(), cancellationToken)
			.ConfigureAwait(false);
		if (view == ProjectContextView.Content)
		{
			await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
			await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
			await writer.WriteAsync(
					ContextRootPresentation.FormatLine(
						NormalizePath(GetDocumentRoot(plan, pathRedaction))).AsMemory(),
					cancellationToken)
				.ConfigureAwait(false);
		}

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
			await foreach (var source in OpenSourceSnapshotsInOrderAsync(
				               plan.SourceRoot,
				               plan.IncludedFiles,
				               cancellationToken).ConfigureAwait(false))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var path = source.Path;
				await using var snapshot = source.Snapshot;
				var file = CreateCompleteFileDocument(
					path,
					snapshot.Result,
					contentPathMapper,
					pathRedaction);
				if (tokenBudget is not null &&
				    !tokenBudget.TryInclude(file.Path, file.Metrics?.CharCount ?? 0))
				{
					ReportProgress(writeProgress, ++processedFiles, plan.IncludedFiles.Count);
					continue;
				}
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
		bool useSourceMappedStructuredPaths,
		IProgress<ProjectCopyExportProgress>? writeProgress,
		ProjectContextTokenBudgetAccumulator? tokenBudget,
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
			await foreach (var source in OpenSourceSnapshotsInOrderAsync(
				               plan.SourceRoot,
				               plan.IncludedFiles,
				               cancellationToken).ConfigureAwait(false))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var path = source.Path;
				await using var snapshot = source.Snapshot;
				var file = CreateCompleteFileDocument(
					path,
					snapshot.Result,
					contentPathMapper,
					pathRedaction);
				if (tokenBudget is not null &&
				    !tokenBudget.TryInclude(file.Path, file.Metrics?.CharCount ?? 0))
				{
					ReportProgress(writeProgress, ++processedFiles, plan.IncludedFiles.Count);
					continue;
				}
				writer.WriteStartObject();
				writer.WriteString("path", NormalizePath(file.Path));
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
		if (tokenBudget is not null)
			WriteTokenBudget(writer, tokenBudget.CreateReport());
		var mapDiagnosticPaths = ShouldMapDiagnosticPathsToSource(plan, useSourceMappedStructuredPaths);
		WriteDiagnostics(
			writer,
			plan.Diagnostics,
			mapDiagnosticPaths ? contentPathMapper : null,
			mapDiagnosticPaths ? pathRedaction : null);
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
		bool useSourceMappedStructuredPaths,
		IProgress<ProjectCopyExportProgress>? writeProgress,
		ProjectContextTokenBudgetAccumulator? tokenBudget,
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
			await foreach (var source in OpenSourceSnapshotsInOrderAsync(
				               plan.SourceRoot,
				               plan.IncludedFiles,
				               cancellationToken).ConfigureAwait(false))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var path = source.Path;
				await using var snapshot = source.Snapshot;
				var file = CreateCompleteFileDocument(
					path,
					snapshot.Result,
					contentPathMapper,
					pathRedaction);
				if (tokenBudget is not null &&
				    !tokenBudget.TryInclude(file.Path, file.Metrics?.CharCount ?? 0))
				{
					ReportProgress(writeProgress, ++processedFiles, plan.IncludedFiles.Count);
					continue;
				}
				writer.WriteStartElement("file");
				WriteSanitizedXmlAttributeString(writer, "path", NormalizePath(file.Path));
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
		if (tokenBudget is not null)
			WriteTokenBudgetXml(writer, tokenBudget.CreateReport());
		writer.WriteStartElement("diagnostics");
		var mapDiagnosticPaths = ShouldMapDiagnosticPathsToSource(plan, useSourceMappedStructuredPaths);
		foreach (var diagnostic in plan.Diagnostics)
		{
			writer.WriteStartElement("diagnostic");
			WriteSanitizedXmlAttributeString(writer, "code", diagnostic.Code);
			writer.WriteAttributeString("severity", ToToken(diagnostic.Severity));
			if (!string.IsNullOrWhiteSpace(diagnostic.Path))
			{
				WriteSanitizedXmlAttributeString(
					writer,
					"path",
					ResolveDiagnosticPath(
						diagnostic.Path,
						mapDiagnosticPaths ? contentPathMapper : null,
						mapDiagnosticPaths ? pathRedaction : null));
			}
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

	private async IAsyncEnumerable<CompleteSourceSnapshot> OpenSourceSnapshotsInOrderAsync(
		string projectRoot,
		IReadOnlyList<string> orderedPaths,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		if (orderedPaths.Count == 0)
			yield break;

		var readAheadCount = Math.Min(
			orderedPaths.Count,
			Math.Min(MaximumBoundedReadAhead, ScanParallelismPolicy.MaxDegreeOfParallelism));
		using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		using var retainedBytes = new WeightedByteBudget(MaximumCompleteSnapshotReadAheadRetainedBytes);
		var pendingReads = new Queue<PendingCompleteSnapshotRead>(readAheadCount);
		var nextPathIndex = 0;

		void FillReadAhead()
		{
			while (pendingReads.Count < readAheadCount && nextPathIndex < orderedPaths.Count)
			{
				var index = nextPathIndex++;
				var path = orderedPaths[index];
				pendingReads.Enqueue(new PendingCompleteSnapshotRead(
					index,
					path,
					OpenBudgetedSourceSnapshotAsync(
						projectRoot,
						path,
						retainedBytes,
						readCancellation.Token)));
			}
		}

		FillReadAhead();
		try
		{
			while (pendingReads.Count > 0)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var pending = pendingReads.Dequeue();
				IFileContentSnapshot? snapshot = null;
				try
				{
					snapshot = await pending.ReadTask.ConfigureAwait(false);
					cancellationToken.ThrowIfCancellationRequested();
					var source = new CompleteSourceSnapshot(pending.Index, pending.Path, snapshot);
					snapshot = null;
					yield return source;
				}
				finally
				{
					if (snapshot is not null)
						await snapshot.DisposeAsync().ConfigureAwait(false);
				}

				FillReadAhead();
			}
		}
		finally
		{
			await CancelAndDisposePendingSnapshotsAsync(readCancellation, pendingReads)
				.ConfigureAwait(false);
		}
	}

	private async Task<IFileContentSnapshot> OpenBudgetedSourceSnapshotAsync(
		string projectRoot,
		string path,
		WeightedByteBudget retainedBytes,
		CancellationToken cancellationToken)
	{
		// Start these methods in source order so a full-budget request cannot be
		// blocked by later snapshots whose leases the ordered consumer cannot release yet.
		var lease = await retainedBytes.AcquireAsync(
				EstimateCompleteSnapshotRetainedBytes(path),
				cancellationToken)
			.ConfigureAwait(false);
		try
		{
			var snapshot = await OpenSourceSnapshotAsync(projectRoot, path, cancellationToken)
				.ConfigureAwait(false);
			return new BudgetedCompleteSourceSnapshot(snapshot, lease);
		}
		catch
		{
			lease.Dispose();
			throw;
		}
	}

	internal static long EstimateCompleteSnapshotRetainedBytes(string path)
	{
		try
		{
			var length = new FileInfo(path).Length;
			return length > long.MaxValue / sizeof(char)
				? MaximumCompleteSnapshotReadAheadRetainedBytes
				: Math.Max(1, length * sizeof(char));
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return MaximumCompleteSnapshotReadAheadRetainedBytes;
		}
	}

	private static async Task CancelAndDisposePendingSnapshotsAsync(
		CancellationTokenSource cancellation,
		Queue<PendingCompleteSnapshotRead> pendingReads)
	{
		cancellation.Cancel();
		while (pendingReads.TryDequeue(out var pending))
		{
			try
			{
				var snapshot = await pending.ReadTask.ConfigureAwait(false);
				await snapshot.DisposeAsync().ConfigureAwait(false);
			}
			catch
			{
				// Speculative snapshots are not observable after the writer stops. Draining owns
				// their failures and releases every handle without masking the primary outcome.
				if (pending.ReadTask.IsFaulted)
					_ = pending.ReadTask.Exception;
			}
		}
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
		var pathCount = Math.Min(plan.IncludedFiles.Count, maximumFiles);
		var readAheadCount = ResolveBoundedReadAheadCount(maximumFileBytes, pathCount);
		using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var pendingReads = new Queue<PendingBoundedFileRead>(Math.Max(1, readAheadCount));
		var nextPathIndex = 0;

		void FillReadAhead()
		{
			while (pendingReads.Count < readAheadCount && nextPathIndex < pathCount)
			{
				var path = plan.IncludedFiles[nextPathIndex++];
				pendingReads.Enqueue(new PendingBoundedFileRead(
					path,
					Task.Run(
						async () => await ReadSourceClassifiedAsync(
								plan.SourceRoot,
								path,
								maximumFileBytes,
								readCancellation.Token)
							.ConfigureAwait(false),
						readCancellation.Token)));
			}
		}

		FillReadAhead();
		try
		{
			while (pendingReads.Count > 0)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var pending = pendingReads.Dequeue();
				var result = await pending.ReadTask.ConfigureAwait(false);
				var relativePath = NormalizeRelativePath(plan.SourceRoot, pending.Path);
				var content = result.Content;
				var reachedOutputBoundary = false;
				if (!result.IsText || content is null)
				{
					files.Add(new ContextFileDocument(
						relativePath,
						result.Classification,
						Content: null));
				}
				else if (content.IsEstimated)
				{
					files.Add(new ContextFileDocument(
						relativePath,
						FileContentClassification.TooLarge,
						Content: null,
						IsOmitted: true));
					isTruncated = true;
				}
				else
				{
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
						var preparedFile = prepared.GetFile(pending.Path);
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
					reachedOutputBoundary = truncatedAtCharacterBoundary;
					if (!reachedOutputBoundary && remainingCharacters == 0 && files.Count < pathCount)
					{
						isTruncated = true;
						reachedOutputBoundary = true;
					}
				}

				result = null!;
				content = null;
				if (reachedOutputBoundary)
					break;
				FillReadAhead();
			}
		}
		finally
		{
			await CancelAndObservePendingReadsAsync(readCancellation, pendingReads)
				.ConfigureAwait(false);
		}

		return new ContextFileReadResult(files, isTruncated);
	}

	private static int ResolveBoundedReadAheadCount(long maximumFileBytes, int fileCount)
	{
		if (fileCount <= 1)
			return fileCount;

		var concurrencyLimit = Math.Min(
			fileCount,
			Math.Min(MaximumBoundedReadAhead, ScanParallelismPolicy.MaxDegreeOfParallelism));
		if (maximumFileBytes == 0)
			return concurrencyLimit;

		var maximumRetainedBytesPerFile = maximumFileBytes > long.MaxValue / sizeof(char)
			? long.MaxValue
			: maximumFileBytes * sizeof(char);
		var memoryBoundedLimit = Math.Max(
			1,
			MaximumBoundedReadAheadRetainedBytes / maximumRetainedBytesPerFile);
		return (int)Math.Min(concurrencyLimit, memoryBoundedLimit);
	}

	private static async Task CancelAndObservePendingReadsAsync(
		CancellationTokenSource cancellation,
		Queue<PendingBoundedFileRead> pendingReads)
	{
		cancellation.Cancel();
		if (pendingReads.Count == 0)
			return;

		var tasks = pendingReads.Select(static pending => pending.ReadTask).ToArray();
		try
		{
			await Task.WhenAll(tasks).ConfigureAwait(false);
		}
		catch
		{
			// Speculative reads past the deterministic output boundary are never observable.
			// Draining them still owns every exception and prevents background work escaping.
			foreach (var task in tasks)
			{
				if (task.IsFaulted)
					_ = task.Exception;
			}
		}
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
		if (view == ProjectContextView.Content)
		{
			output.Append(ContextRootPresentation.FormatLine(
				GetDocumentRoot(plan, protectPrivateData: true)));
		}
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
		if (view == ProjectContextView.Content)
		{
			output.AppendLine(ContextRootPresentation.FormatLine(
				NormalizePath(GetDocumentRoot(plan, protectPrivateData: true))));
		}
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
		writer.WriteString("gitMode", ProjectSelectionTokens.ToToken(plan.Selection));
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

	private static void WriteTokenBudget(
		Utf8JsonWriter writer,
		ProjectContextTokenBudgetReport report)
	{
		writer.WriteStartObject("tokenBudget");
		writer.WriteNumber("maximumEstimatedTokens", report.MaximumEstimatedTokens);
		writer.WriteNumber("includedFiles", report.IncludedFileCount);
		writer.WriteNumber("skippedFiles", report.SkippedFileCount);
		writer.WriteNumber("includedEstimatedTokens", report.IncludedEstimatedTokens);
		writer.WriteNumber("skippedEstimatedTokens", report.SkippedEstimatedTokens);
		writer.WriteStartArray("largestSkippedFiles");
		foreach (var file in report.LargestSkippedFiles)
		{
			writer.WriteStartObject();
			writer.WriteString("path", NormalizePath(file.Path));
			writer.WriteNumber("estimatedTokens", file.EstimatedTokens);
			writer.WriteEndObject();
		}
		writer.WriteEndArray();
		writer.WriteNumber("additionalSkippedFiles", report.AdditionalSkippedFileCount);
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
		IReadOnlyList<ContextDiagnostic> diagnostics,
		Func<string, string>? pathMapper = null,
		OutputPathRedactionDecision? pathRedaction = null)
	{
		writer.WriteStartArray("diagnostics");
		foreach (var diagnostic in diagnostics)
		{
			writer.WriteStartObject();
			writer.WriteString("code", diagnostic.Code);
			writer.WriteString("severity", ToToken(diagnostic.Severity));
			writer.WriteString("message", diagnostic.Message);
			if (!string.IsNullOrWhiteSpace(diagnostic.Path))
			{
				writer.WriteString(
					"path",
					ResolveDiagnosticPath(diagnostic.Path, pathMapper, pathRedaction));
			}
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
			ProjectSelectionTokens.ToToken(plan.Selection));
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

	private static string ResolveDiagnosticPath(
		string path,
		Func<string, string>? pathMapper,
		OutputPathRedactionDecision? pathRedaction)
	{
		var displayPath = pathMapper is null ? path : MapContentPath(pathMapper, path);
		return NormalizePath(OutputRootPathPresentation.ResolvePath(displayPath, pathRedaction).Text);
	}

	private static bool ShouldMapDiagnosticPathsToSource(
		ProjectContextPlan plan,
		bool useSourceMappedStructuredPaths) =>
		useSourceMappedStructuredPaths && plan.SourceIdentity?.IsCachedRepository == true;

	private static void WriteTokenBudgetXml(
		XmlWriter writer,
		ProjectContextTokenBudgetReport report)
	{
		writer.WriteStartElement("tokenBudget");
		writer.WriteElementString(
			"maximumEstimatedTokens",
			XmlConvert.ToString(report.MaximumEstimatedTokens));
		writer.WriteElementString("includedFiles", XmlConvert.ToString(report.IncludedFileCount));
		writer.WriteElementString("skippedFiles", XmlConvert.ToString(report.SkippedFileCount));
		writer.WriteElementString(
			"includedEstimatedTokens",
			XmlConvert.ToString(report.IncludedEstimatedTokens));
		writer.WriteElementString(
			"skippedEstimatedTokens",
			XmlConvert.ToString(report.SkippedEstimatedTokens));
		writer.WriteStartElement("largestSkippedFiles");
		foreach (var file in report.LargestSkippedFiles)
		{
			writer.WriteStartElement("file");
			WriteSanitizedXmlAttributeString(writer, "path", NormalizePath(file.Path));
			writer.WriteAttributeString(
				"estimatedTokens",
				XmlConvert.ToString(file.EstimatedTokens));
			writer.WriteEndElement();
		}
		writer.WriteEndElement();
		writer.WriteElementString(
			"additionalSkippedFiles",
			XmlConvert.ToString(report.AdditionalSkippedFileCount));
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

	private static Func<string, string>? CreateSourceContentPathMapper(
		ProjectContextPlan plan,
		bool useSourceMappedStructuredPaths,
		ProjectContextView view)
	{
		if (!useSourceMappedStructuredPaths || view != ProjectContextView.Content)
			return TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(plan.SourceRoot);

		if (plan.SourceIdentity is not
		    {
			    SourceType: ProjectSourceType.GitClone,
			    SourceReference.Length: > 0
		    } identity)
		{
			return null;
		}

		return WebPathPresentation.TryCreatePathMapper(plan.SourceRoot, identity.SourceReference)
		       ?? TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(plan.SourceRoot);
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

	private sealed record PendingBoundedFileRead(
		string Path,
		Task<FileContentReadResult> ReadTask);

	private readonly record struct CompleteSourceSnapshot(
		int Index,
		string Path,
		IFileContentSnapshot Snapshot);

	private sealed record PendingCompleteSnapshotRead(
		int Index,
		string Path,
		Task<IFileContentSnapshot> ReadTask);

	private sealed class BudgetedCompleteSourceSnapshot(
		IFileContentSnapshot inner,
		WeightedByteBudget.Lease lease) : IFileContentSnapshot
	{
		private int _disposed;

		public FileContentMetricsResult Result => inner.Result;

		public ValueTask CopyTextToAsync(
			int maximumCharacters,
			Func<ReadOnlyMemory<char>, CancellationToken, ValueTask> writeChunk,
			CancellationToken cancellationToken = default) =>
			inner.CopyTextToAsync(maximumCharacters, writeChunk, cancellationToken);

		public async ValueTask DisposeAsync()
		{
			if (Interlocked.Exchange(ref _disposed, 1) != 0)
				return;
			try
			{
				await inner.DisposeAsync().ConfigureAwait(false);
			}
			finally
			{
				lease.Dispose();
			}
		}
	}

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
