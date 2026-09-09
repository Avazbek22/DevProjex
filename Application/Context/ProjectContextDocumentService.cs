using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Xml;
using DevProjex.Application.Diagnostics;
using DevProjex.Application.Ranking;
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
	ProjectContextTokenBudgetReport? TokenBudget = null,
	ImportanceRankingReport? Ranking = null)
{
	public static ProjectContextWriteResult Empty { get; } = new([]);
}

public sealed class ProjectContextDocumentService(
	TreeExportService treeExportService,
	IFileContentAnalyzer contentAnalyzer,
	Func<FileContentClassification, string>? omissionMessageProvider = null,
	SecretRedactionSession? secretRedactionSession = null,
	CodeCompressionSession? codeCompressionSession = null,
	OutputPathRedactionDecision? outputPathRedactionDecision = null,
	IFileContentAnalyzer? preparedContentAnalyzer = null)
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
		long? maximumEstimatedTokens = null,
		ImportanceRankingReport? ranking = null,
		ProjectContextTokenBudgetReport? precomputedTokenBudget = null)
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
				maximumEstimatedTokens,
				ranking,
				precomputedTokenBudget)
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
		long? maximumEstimatedTokens = null,
		ImportanceRankingReport? ranking = null,
		ProjectContextTokenBudgetReport? precomputedTokenBudget = null)
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
			view,
			format,
			useSourceMappedStructuredPaths);
		var orderedPaths = ResolveOrderedPaths(plan.IncludedFiles, ranking);
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
					maximumEstimatedTokens,
					ranking)
				.ConfigureAwait(false);
		}
		var tokenBudget = CreateTokenBudget(maximumEstimatedTokens, precomputedTokenBudget);
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
						orderedPaths,
						ranking,
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
						orderedPaths,
						ranking,
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
						orderedPaths,
						ranking,
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
						orderedPaths,
						ranking,
						cancellationToken)
					.ConfigureAwait(false);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(format), format, null);
		}
		return new ProjectContextWriteResult([], tokenBudget?.CreateReport(), ranking);
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
		long? maximumEstimatedTokens = null,
		ImportanceRankingReport? ranking = null,
		ProjectContextTokenBudgetReport? precomputedTokenBudget = null,
		bool preserveContentMetrics = false)
	{
		ArgumentNullException.ThrowIfNull(prepared);
		var analyzer = CreatePreparedAnalyzer(prepared);
		await EnsureRankingSourceVersionsAsync(
				plan.IncludedFiles,
				ranking,
				cancellationToken,
				path => analyzer.IsApplicationOwnedImmutableContent(path))
			.ConfigureAwait(false);
		var pathRedaction = outputPathRedactionDecision ??
		                    OutputRootPathPresentation.CaptureRedactionDecision(
			                    CreateTransformationContext(plan));
		plan = preserveContentMetrics ? plan : await RefreshStructuredContentMetricsAsync(
				plan,
				view,
				format,
				analyzer,
				prepared,
				cancellationToken)
			.ConfigureAwait(false);
		var service = new ProjectContextDocumentService(
			treeExportService,
			analyzer,
			omissionMessageProvider,
			secretRedactionSession: null,
			codeCompressionSession: null,
			outputPathRedactionDecision: pathRedaction);
		var result = await service.WriteCompleteWithReportAsync(
				plan,
				view,
				format,
				destination,
				cancellationToken,
				plain,
				useSourceMappedStructuredPaths,
				writeProgress,
				maximumEstimatedTokens,
				ranking,
				precomputedTokenBudget)
			.ConfigureAwait(false);
		return result with { UnscannableFiles = prepared.UnscannableFiles };
	}

	public async Task<ProjectContextWriteResult> EvaluateTokenBudgetAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		long maximumEstimatedTokens,
		CancellationToken cancellationToken = default,
		ImportanceRankingReport? ranking = null)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ValidateView(view);
		ValidateDocumentFormat(format);
		var tokenBudget = CreateTokenBudget(maximumEstimatedTokens)!;
		var orderedPaths = ResolveOrderedPaths(plan.IncludedFiles, ranking);
		if (!IncludesContent(view))
			return new ProjectContextWriteResult([], tokenBudget.CreateReport(), ranking);

		var transformationContext = CreateTransformationContext(plan);
		if (transformationContext is null)
		{
			await EvaluateTokenBudgetCoreAsync(
					plan,
					view,
					format,
					tokenBudget,
					orderedPaths,
					ranking,
					cancellationToken)
				.ConfigureAwait(false);
			return new ProjectContextWriteResult([], tokenBudget.CreateReport(), ranking);
		}

		var preparer = new SecretRedactionOutputPreparer(contentAnalyzer);
		await using var prepared = await preparer
			.PrepareAsync(transformationContext, plan.IncludedFiles, cancellationToken)
			.ConfigureAwait(false);
		var service = new ProjectContextDocumentService(
			treeExportService,
			CreatePreparedAnalyzer(prepared),
			omissionMessageProvider,
			secretRedactionSession: null,
			codeCompressionSession: null,
			outputPathRedactionDecision: OutputRootPathPresentation.CaptureRedactionDecision(
				transformationContext));
		await service.EvaluateTokenBudgetCoreAsync(
				plan,
				view,
				format,
				tokenBudget,
				orderedPaths,
				ranking,
				cancellationToken)
			.ConfigureAwait(false);
		return new ProjectContextWriteResult(prepared.UnscannableFiles, tokenBudget.CreateReport(), ranking);
	}

	public async Task<ProjectContextWriteResult> EvaluateMeasuredTokenBudgetAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		long maximumEstimatedTokens,
		PreparedSecretRedactionOutput measured,
		ImportanceRankingReport? ranking = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(measured);
		ValidateView(view);
		ValidateDocumentFormat(format);
		await EnsureRankingSourceVersionsAsync(plan.IncludedFiles, ranking, cancellationToken)
			.ConfigureAwait(false);
		var tokenBudget = CreateTokenBudget(maximumEstimatedTokens)!;
		if (!IncludesContent(view))
			return new ProjectContextWriteResult(measured.UnscannableFiles, tokenBudget.CreateReport(), ranking);

		var effectivePathRedaction = outputPathRedactionDecision ??
			OutputRootPathPresentation.CaptureRedactionDecision(CreateTransformationContext(plan));
		var contentPathMapper = CreateContentPathMapper(
			plan,
			view,
			format,
			useSourceMappedStructuredPaths: true);
		var rankingEntriesByFullPath = CreateRankingEntryLookup(ranking);
		var metricsByPath = measured.TransformedFileMetrics.ToDictionary(
			static metrics => Path.GetFullPath(metrics.Path),
			PathComparer.Default);
		var measuredAnalyzer = CreatePreparedAnalyzer(measured);
		var orderedPaths = ResolveOrderedPaths(plan.IncludedFiles, ranking);
		for (var index = 0; index < orderedPaths.Count; index++)
		{
			var path = orderedPaths[index];
			metricsByPath.TryGetValue(Path.GetFullPath(path), out var metrics);
			var preparedFile = measured.GetFile(path);
			var result = metrics.Path is not null && !metrics.IsEstimated
				? new FileContentMetricsResult(
					preparedFile.Classification,
					ToTextFileMetrics(metrics))
				: await ReadExactMetricsAsync(path, measuredAnalyzer, cancellationToken)
					.ConfigureAwait(false);
			var file = CreateCompleteFileDocument(
				path,
				result,
				contentPathMapper,
				effectivePathRedaction);
			TryIncludeInBudget(
				tokenBudget,
				file.Path,
				path,
				file.Metrics?.CharCount ?? 0,
				rankingEntriesByFullPath,
				index);
		}

		return new ProjectContextWriteResult(measured.UnscannableFiles, tokenBudget.CreateReport(), ranking);
	}

	public static ProjectContextPlan ApplyMeasuredContentMetrics(
		ProjectContextPlan plan,
		PreparedSecretRedactionOutput measured)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(measured);
		return WithContentMetrics(plan, measured.GetTransformedMetrics());
	}

	private static async Task EnsureRankingSourceVersionsAsync(
		IReadOnlyList<string> paths,
		ImportanceRankingReport? ranking,
		CancellationToken cancellationToken,
		Func<string, bool>? shouldValidate = null)
	{
		ArgumentNullException.ThrowIfNull(paths);
		if (ranking?.SourceVersions is null)
			return;
		foreach (var path in paths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (shouldValidate is not null && !shouldValidate(path))
				continue;
			if (ranking.SourceVersions.TryGetValue(Path.GetFullPath(path), out var expectedVersion) &&
			    !await expectedVersion.IsCurrentAsync(path, cancellationToken).ConfigureAwait(false))
			{
				throw new IOException(
					"Selected source content changed during importance ranking; repeat the export.");
			}
		}
	}

	private async Task EvaluateTokenBudgetCoreAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		ProjectContextTokenBudgetAccumulator tokenBudget,
		IReadOnlyList<string> orderedPaths,
		ImportanceRankingReport? ranking,
		CancellationToken cancellationToken)
	{
		var effectivePathRedaction = outputPathRedactionDecision ??
			OutputRootPathPresentation.CaptureRedactionDecision(CreateTransformationContext(plan));
		var contentPathMapper = CreateContentPathMapper(
			plan,
			view,
			format,
			useSourceMappedStructuredPaths: true);
		var rankingEntriesByFullPath = CreateRankingEntryLookup(ranking);
		await foreach (var source in OpenSourceSnapshotsInOrderAsync(
			               plan.SourceRoot,
			               orderedPaths,
			               ranking?.SourceVersions,
			               validateDuringUtf8Copy: false,
			               cancellationToken).ConfigureAwait(false))
		{
			await using var snapshot = source.Snapshot;
			var file = CreateCompleteFileDocument(
				source.Path,
				snapshot.Result,
				contentPathMapper,
				effectivePathRedaction);
			TryIncludeInBudget(
				tokenBudget,
				file.Path,
				source.Path,
				file.Metrics?.CharCount ?? 0,
				rankingEntriesByFullPath,
				source.Index);
		}
	}

	private static ProjectContextTokenBudgetAccumulator? CreateTokenBudget(
		long? maximumEstimatedTokens,
		ProjectContextTokenBudgetReport? precomputedTokenBudget = null) =>
		precomputedTokenBudget is not null
			? new ProjectContextTokenBudgetAccumulator(precomputedTokenBudget)
			: maximumEstimatedTokens is null
				? null
				: new ProjectContextTokenBudgetAccumulator(maximumEstimatedTokens.Value);

	private static bool TryIncludeInBudget(
		ProjectContextTokenBudgetAccumulator tokenBudget,
		string outputPath,
		string fullPath,
		int transformedCharacterCount,
		IReadOnlyDictionary<string, ImportanceRankingEntry>? rankingEntriesByFullPath,
		int admissionIndex)
	{
		ImportanceRankingEntry? entry = null;
		if (rankingEntriesByFullPath is not null)
			rankingEntriesByFullPath.TryGetValue(Path.GetFullPath(fullPath), out entry);
		return tokenBudget.TryInclude(
			outputPath,
			transformedCharacterCount,
			rankingEntriesByFullPath is null ? null : admissionIndex + 1,
			entry?.Hop,
			entry?.BaseImportancePriority,
			entry?.Via,
			fullPath);
	}

	private static IReadOnlyDictionary<string, ImportanceRankingEntry>? CreateRankingEntryLookup(
		ImportanceRankingReport? ranking)
	{
		if (ranking is null)
			return null;
		var result = new Dictionary<string, ImportanceRankingEntry>(PathComparer.Default);
		foreach (var entry in ranking.Entries)
			result.TryAdd(Path.GetFullPath(entry.FullPath), entry);
		return result;
	}

	internal static IReadOnlyList<string> ResolveOrderedPaths(
		IReadOnlyList<string> includedFiles,
		ImportanceRankingReport? ranking)
	{
		if (ranking is null || includedFiles.Count < 2)
			return includedFiles;
		var selected = includedFiles.ToDictionary(Path.GetFullPath, PathComparer.Default);
		var seen = new HashSet<string>(PathComparer.Default);
		var ordered = new List<string>(includedFiles.Count);
		foreach (var entry in ranking.Entries)
		{
			var fullPath = Path.GetFullPath(entry.FullPath);
			if (selected.TryGetValue(fullPath, out var selectedPath) && seen.Add(fullPath))
				ordered.Add(selectedPath);
		}
		foreach (var path in includedFiles)
		{
			if (seen.Add(Path.GetFullPath(path)))
				ordered.Add(path);
		}
		return ordered;
	}

	private PreparedSecretFileContentAnalyzer CreatePreparedAnalyzer(PreparedSecretRedactionOutput prepared) =>
		new PreparedSecretFileContentAnalyzer(
			contentAnalyzer,
			preparedContentAnalyzer ?? contentAnalyzer,
			prepared);

	private static async Task<ProjectContextPlan> RefreshStructuredContentMetricsAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		IFileContentAnalyzer analyzer,
		PreparedSecretRedactionOutput prepared,
		CancellationToken cancellationToken)
	{
		if (!IncludesContent(view) ||
		    format is not (ProjectContextDocumentFormat.Json or ProjectContextDocumentFormat.Xml))
		{
			return plan;
		}

		var preparedMetrics = prepared.TransformedFileMetrics.ToDictionary(
			static metrics => Path.GetFullPath(metrics.Path),
			PathComparer.Default);
		var orderedMetrics = new List<ContentFileMetrics>(plan.IncludedFiles.Count);
		foreach (var path in plan.IncludedFiles)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (preparedMetrics.TryGetValue(Path.GetFullPath(path), out var metrics) &&
			    !metrics.IsEstimated)
			{
				orderedMetrics.Add(metrics);
				continue;
			}

			var result = await ReadExactMetricsAsync(path, analyzer, cancellationToken)
				.ConfigureAwait(false);
			if (result.IsText && result.Metrics is { } textMetrics)
				orderedMetrics.Add(ToContentFileMetrics(path, textMetrics));
		}
		return WithContentMetrics(
			plan,
			ExportOutputMetricsCalculator.FromOrderedContentFiles(orderedMetrics));
	}

	private static async ValueTask<FileContentMetricsResult> ReadExactMetricsAsync(
		string path,
		IFileContentAnalyzer analyzer,
		CancellationToken cancellationToken)
	{
		await using var snapshot = await analyzer
			.OpenCompleteSnapshotAsync(path, cancellationToken)
			.ConfigureAwait(false);
		if (snapshot.Result.Metrics is { IsEstimated: true })
			throw new IOException($"Exact document metrics are unavailable for '{path}'.");
		return snapshot.Result;
	}

	private static ProjectContextPlan WithContentMetrics(
		ProjectContextPlan plan,
		ExportOutputMetrics metrics) =>
		plan with
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

	private static ContentFileMetrics ToContentFileMetrics(string path, TextFileMetrics metrics) =>
		new(
			path,
			metrics.SizeBytes,
			metrics.LineCount,
			metrics.CharCount,
			metrics.IsEmpty,
			metrics.IsWhitespaceOnly,
			metrics.IsEstimated,
			metrics.CrLfPairCount,
			metrics.TrailingNewlineChars,
			metrics.TrailingNewlineLineBreaks);

	private static TextFileMetrics ToTextFileMetrics(ContentFileMetrics metrics) =>
		new(
			metrics.SizeBytes,
			metrics.LineCount,
			metrics.CharCount,
			metrics.IsEmpty,
			metrics.IsWhitespaceOnly,
			metrics.IsEstimated,
			CrLfPairCount: metrics.CrLfPairCount,
			TrailingNewlineChars: metrics.TrailingNewlineChars,
			TrailingNewlineLineBreaks: metrics.TrailingNewlineLineBreaks,
			LongestBacktickRun: 0);

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
		var analyzer = CreatePreparedAnalyzer(prepared);
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
		long? maximumEstimatedTokens,
		ImportanceRankingReport? ranking)
	{
		var context = CreateTransformationContext(plan)!;
		var preparer = new SecretRedactionOutputPreparer(contentAnalyzer);
		await using var prepared = await preparer
			.PrepareAsync(
				context,
				plan.IncludedFiles,
				captureEffectiveFindings: false,
				captureTransformedMetrics: format is
					ProjectContextDocumentFormat.Json or ProjectContextDocumentFormat.Xml,
				cancellationToken)
			.ConfigureAwait(false);
		var analyzer = CreatePreparedAnalyzer(prepared);
		await EnsureRankingSourceVersionsAsync(
				plan.IncludedFiles,
				ranking,
				cancellationToken,
				path => analyzer.IsApplicationOwnedImmutableContent(path))
			.ConfigureAwait(false);
		plan = await RefreshStructuredContentMetricsAsync(
				plan,
				view,
				format,
				analyzer,
				prepared,
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
				maximumEstimatedTokens,
				ranking)
			.ConfigureAwait(false);
		return new ProjectContextWriteResult(prepared.UnscannableFiles, writeResult.TokenBudget, writeResult.Ranking);
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
		IReadOnlyList<string> orderedPaths,
		ImportanceRankingReport? ranking,
		CancellationToken cancellationToken)
	{
		await using var streamWriter = CreateStreamWriter(destination);
		var writer = new TrailingLineEndingTextWriter(streamWriter);
		var rankingEntriesByFullPath = CreateRankingEntryLookup(ranking);
		var hasOutput = false;
		var includesContent = IncludesContent(view) && orderedPaths.Count > 0;
		if (view == ProjectContextView.Content)
		{
			await writer.WriteAsync(
					ContextRootPresentation.FormatLine(
						GetHumanReadableContentRoot(plan, pathRedaction)).AsMemory(),
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
				               orderedPaths,
				               ranking?.SourceVersions,
				               validateDuringUtf8Copy: false,
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
				    !TryIncludeInBudget(
					    tokenBudget,
					    file.Path,
					    source.Path,
					    file.Metrics?.CharCount ?? 0,
					    rankingEntriesByFullPath,
					    index))
				{
					ReportProgress(writeProgress, index + 1, orderedPaths.Count);
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
				ReportProgress(writeProgress, index + 1, orderedPaths.Count);
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
		IReadOnlyList<string> orderedPaths,
		ImportanceRankingReport? ranking,
		CancellationToken cancellationToken)
	{
		await using var writer = CreateStreamWriter(destination);
		var rankingEntriesByFullPath = CreateRankingEntryLookup(ranking);
		await writer.WriteAsync("# ".AsMemory(), cancellationToken).ConfigureAwait(false);
		await writer.WriteAsync(EscapeMarkdownHeading(GetProjectName(plan)).AsMemory(), cancellationToken)
			.ConfigureAwait(false);
		if (view == ProjectContextView.Content)
		{
			await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
			await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
			await writer.WriteAsync(
					ContextRootPresentation.FormatMarkdownLine(
						NormalizePath(GetHumanReadableContentRoot(plan, pathRedaction))).AsMemory(),
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
				               orderedPaths,
				               ranking?.SourceVersions,
				               validateDuringUtf8Copy: true,
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
				    !TryIncludeInBudget(
					    tokenBudget,
					    file.Path,
					    source.Path,
					    file.Metrics?.CharCount ?? 0,
					    rankingEntriesByFullPath,
					    source.Index))
				{
					ReportProgress(writeProgress, ++processedFiles, orderedPaths.Count);
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
					var characterCount = file.Metrics?.CharCount ?? 0;
					if (snapshot is IUtf8FileContentSnapshot utf8Snapshot)
					{
						await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
						await utf8Snapshot.CopyUtf8ToAsync(
								characterCount,
								(chunk, token) => destination.WriteAsync(chunk, token),
								cancellationToken)
							.ConfigureAwait(false);
					}
					else
					{
						await snapshot.CopyTextToAsync(
								characterCount,
								(chunk, token) => new ValueTask(
									writer.WriteAsync(chunk, token)),
								cancellationToken)
							.ConfigureAwait(false);
					}
					await WriteLineAsync(writer, null, cancellationToken).ConfigureAwait(false);
					await writer.WriteAsync(fence.AsMemory(), cancellationToken).ConfigureAwait(false);
				}
				ReportProgress(writeProgress, ++processedFiles, orderedPaths.Count);
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
		IReadOnlyList<string> orderedPaths,
		ImportanceRankingReport? ranking,
		CancellationToken cancellationToken)
	{
		using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions
		{
			Indented = true,
			Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
			MaxDepth = int.MaxValue
		});
		var rankingEntriesByFullPath = CreateRankingEntryLookup(ranking);

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
				               orderedPaths,
				               ranking?.SourceVersions,
				               validateDuringUtf8Copy: false,
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
				    !TryIncludeInBudget(
					    tokenBudget,
					    file.Path,
					    source.Path,
					    file.Metrics?.CharCount ?? 0,
					    rankingEntriesByFullPath,
					    source.Index))
				{
					ReportProgress(writeProgress, ++processedFiles, orderedPaths.Count);
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
				ReportProgress(writeProgress, ++processedFiles, orderedPaths.Count);
			}
		}
		writer.WriteEndArray();
		if (ranking is not null)
			WriteRanking(writer, ranking, tokenBudget?.CreateReport(), pathRedaction);
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
		IReadOnlyList<string> orderedPaths,
		ImportanceRankingReport? ranking,
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
		var rankingEntriesByFullPath = CreateRankingEntryLookup(ranking);

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
				               orderedPaths,
				               ranking?.SourceVersions,
				               validateDuringUtf8Copy: false,
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
				    !TryIncludeInBudget(
					    tokenBudget,
					    file.Path,
					    source.Path,
					    file.Metrics?.CharCount ?? 0,
					    rankingEntriesByFullPath,
					    source.Index))
				{
					ReportProgress(writeProgress, ++processedFiles, orderedPaths.Count);
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
								if (XmlTextSanitizer.TrySanitize(chunk.Span, out var sanitized))
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
				ReportProgress(writeProgress, ++processedFiles, orderedPaths.Count);
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
		IReadOnlyDictionary<string, RankingSourceVersion>? sourceVersions,
		bool validateDuringUtf8Copy,
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
				RankingSourceVersion? expectedVersion = null;
				if (sourceVersions is not null &&
				    sourceVersions.TryGetValue(Path.GetFullPath(path), out var capturedVersion))
				{
					expectedVersion = capturedVersion;
				}
				pendingReads.Enqueue(new PendingCompleteSnapshotRead(
					index,
					path,
					OpenBudgetedSourceSnapshotAsync(
						projectRoot,
						path,
						retainedBytes,
						expectedVersion,
						validateDuringUtf8Copy,
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
		RankingSourceVersion? expectedVersion,
		bool validateDuringUtf8Copy,
		CancellationToken cancellationToken)
	{
		// Start these methods in source order so a full-budget request cannot be
		// blocked by later snapshots whose leases the ordered consumer cannot release yet.
		var lease = await retainedBytes.AcquireAsync(
				EstimateCompleteSnapshotRetainedBytes(path),
				cancellationToken)
			.ConfigureAwait(false);
		IFileContentSnapshot? snapshot = null;
		try
		{
			var isApplicationOwnedImmutable = contentAnalyzer is PreparedSecretFileContentAnalyzer preparedAnalyzer &&
			                                  preparedAnalyzer.IsApplicationOwnedImmutableContent(path);
			snapshot = await OpenSourceSnapshotAsync(
					projectRoot,
					path,
					captureRawContentIdentity: !isApplicationOwnedImmutable && expectedVersion is not null,
					cancellationToken)
				.ConfigureAwait(false);
			if (!isApplicationOwnedImmutable && expectedVersion is { } version)
			{
				if (validateDuringUtf8Copy && snapshot is IUtf8FileContentSnapshot)
				{
					snapshot = new RankingValidatedUtf8SourceSnapshot(snapshot, path, version);
				}
				else if (snapshot is IRawContentIdentitySnapshot identitySnapshot)
				{
					if (!version.HasMatchingContentHash(identitySnapshot.RawContentHash.Span))
					{
						throw new IOException(
							"A selected source file changed after importance facts were indexed; repeat the export.");
					}
					snapshot = new RankingMetadataValidatedSourceSnapshot(snapshot, path, version);
				}
				else if (!await version.IsCurrentAsync(path, cancellationToken).ConfigureAwait(false))
				{
					throw new IOException(
						"A selected source file changed after importance facts were indexed; repeat the export.");
				}
				else
				{
					snapshot = new RankingMetadataValidatedSourceSnapshot(snapshot, path, version);
				}
			}
			IFileContentSnapshot budgeted = snapshot is IUtf8FileContentSnapshot
				? new BudgetedUtf8CompleteSourceSnapshot(snapshot, lease)
				: new BudgetedCompleteSourceSnapshot(snapshot, lease);
			snapshot = null;
			return budgeted;
		}
		catch
		{
			if (snapshot is not null)
			{
				try
				{
					await snapshot.DisposeAsync().ConfigureAwait(false);
				}
				catch
				{
					// Preserve the source-version failure while still attempting to release the handle.
				}
			}
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
		bool captureRawContentIdentity,
		CancellationToken cancellationToken)
	{
		if (contentAnalyzer is PreparedSecretFileContentAnalyzer preparedAnalyzer &&
		    preparedAnalyzer.IsApplicationOwnedImmutableContent(path))
		{
			// Application-owned prepared content is immutable and was captured through this policy.
			// Source-backed pass-through entries still require the checks below when they are opened.
			return await contentAnalyzer
				.OpenCompleteSnapshotAsync(path, cancellationToken)
				.ConfigureAwait(false);
		}

		var classification = ProjectSourcePathPolicy.ClassifyUnavailable(projectRoot, path);
		if (classification is { } unavailable)
			return new UnavailableSourceSnapshot(unavailable);

		var snapshot = captureRawContentIdentity &&
		               contentAnalyzer is IRawContentIdentityFileContentAnalyzer identityAnalyzer
			? await identityAnalyzer
				.OpenCompleteSnapshotWithRawContentIdentityAsync(path, cancellationToken)
				.ConfigureAwait(false)
			: await contentAnalyzer.OpenCompleteSnapshotAsync(path, cancellationToken)
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
				GetHumanReadableContentRoot(plan, protectPrivateData: true)));
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
			output.AppendLine(ContextRootPresentation.FormatMarkdownLine(
				NormalizePath(GetHumanReadableContentRoot(plan, protectPrivateData: true))));
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
			if (file.Priority is { } priority)
				writer.WriteNumber("priority", priority);
			if (file.RemainingEstimatedTokens is { } remaining)
				writer.WriteNumber("remainingEstimatedTokens", remaining);
			writer.WriteEndObject();
		}
		writer.WriteEndArray();
		writer.WriteNumber("additionalSkippedFiles", report.AdditionalSkippedFileCount);
		writer.WriteEndObject();
	}

	private static void WriteRanking(
		Utf8JsonWriter writer,
		ImportanceRankingReport report,
		ProjectContextTokenBudgetReport? tokenBudget,
		OutputPathRedactionDecision? pathRedaction)
	{
		writer.WriteStartObject("ranking");
		writer.WriteString("algorithm", report.Algorithm);
		writer.WriteString("graphVariant", report.GraphVariant);
		writer.WriteNumber("candidateFiles", report.CandidateCount);
		writer.WriteNumber("graphSupportedSources", report.GraphSupportedSources);
		writer.WriteNumber("graphExtractionFailures", report.GraphExtractionFailures);
		writer.WriteNumber("graphCoverage", report.GraphCoverage);
		writer.WriteNumber("gitWindow", report.GitWindow);
		writer.WriteNumber("gitCommits", report.GitCommitCount);
		writer.WriteString("gitUnavailableReason", RankingHistoryReasonToken(report.GitUnavailableReason));
		writer.WriteBoolean("redistributedMissingSignals", report.RedistributedMissingSignals);
		if (report.Focus is { } focus)
		{
			writer.WriteStartObject("focus");
			writer.WriteString("algorithm", focus.Algorithm);
			writer.WriteString("withinHop", focus.WithinHop);
			writer.WriteStartArray("seeds");
			foreach (var seed in focus.Seeds)
			{
				writer.WriteStartObject();
				writer.WriteString(
					"requested",
					OutputRootPathPresentation.ResolvePath(seed.Requested, pathRedaction).Text);
				writer.WriteString("path", NormalizePath(seed.Path));
				writer.WriteString("state", FocusSeedStateToken(seed.State));
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
			writer.WriteStartObject("hops");
			foreach (var hop in focus.Hops.OrderBy(static pair => pair.Key))
				writer.WriteNumber(hop.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), hop.Value);
			writer.WriteEndObject();
			writer.WriteNumber("hopsBeyond", focus.HopsBeyond);
			writer.WriteNumber("maxHop", focus.MaxHop);
			writer.WriteNumber("unreachable", focus.Unreachable);
			writer.WriteEndObject();
		}
		writer.WriteStartArray("top");
		foreach (var entry in report.TopEntries)
		{
			writer.WriteStartObject();
			writer.WriteString("path", NormalizePath(entry.Path));
			writer.WriteNumber("priority", entry.Priority);
			writer.WriteNumber("score", entry.Score);
			writer.WriteNumber("dependents", entry.Dependents);
			writer.WriteNumber("dependencies", entry.Dependencies);
			if (report.Focus is not null)
			{
				if (entry.Hop is { } hop)
					writer.WriteNumber("hop", hop);
				else
					writer.WriteNull("hop");
				writer.WriteNumber("baseImportancePriority", entry.BaseImportancePriority.GetValueOrDefault());
				if (!entry.IsFocusSeed && entry.Hop is > 0 && entry.Via is { } via)
				{
					writer.WriteStartObject("via");
					writer.WriteString("path", NormalizePath(via.Path));
					writer.WriteString("relation", FocusRelationToken(via.Relation));
					writer.WriteEndObject();
				}
			}
			if (entry.Commits is { } commits)
				writer.WriteNumber("commits", commits);
			if (entry.MostRecentCommitPosition is { } position)
				writer.WriteNumber("mostRecentCommitPosition", position);
			if (entry.GitUnavailableReason is { } historyReason)
				writer.WriteString("gitUnavailableReason", RankingHistoryReasonToken(historyReason));
			writer.WriteString("role", RankingRoleToken(entry.Role));
			writer.WriteEndObject();
		}
		writer.WriteEndArray();
		writer.WriteStartArray("skipped");
		foreach (var file in tokenBudget?.RankedSkippedFiles ?? [])
		{
			writer.WriteStartObject();
			writer.WriteString("path", NormalizePath(file.Path));
			writer.WriteNumber("priority", file.Priority.GetValueOrDefault());
			writer.WriteNumber("estimatedTokens", file.EstimatedTokens);
			writer.WriteNumber("remainingEstimatedTokens", file.RemainingEstimatedTokens.GetValueOrDefault());
			if (report.Focus is not null)
			{
				if (file.Hop is { } hop)
					writer.WriteNumber("hop", hop);
				else
					writer.WriteNull("hop");
				writer.WriteNumber("baseImportancePriority", file.BaseImportancePriority.GetValueOrDefault());
				if (file.Hop is > 0 && file.Via is { } via)
				{
					writer.WriteStartObject("via");
					writer.WriteString("path", NormalizePath(via.Path));
					writer.WriteString("relation", FocusRelationToken(via.Relation));
					writer.WriteEndObject();
				}
			}
			writer.WriteString("reason", "does not fit the remaining budget");
			writer.WriteEndObject();
		}
		writer.WriteEndArray();
		writer.WriteEndObject();
	}

	private static string FocusSeedStateToken(FocusSeedState state) => state switch
	{
		FocusSeedState.Resolved => "resolved",
		FocusSeedState.NoResolvedNeighbors => "no-resolved-neighbors",
		FocusSeedState.ExtractionFailed => "extraction-failed",
		FocusSeedState.Unsupported => "unsupported",
		_ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
	};

	private static string FocusRelationToken(FocusRankingRelation relation) => relation switch
	{
		FocusRankingRelation.DependentOf => "dependent-of",
		FocusRankingRelation.DependencyOf => "dependency-of",
		FocusRankingRelation.LinkedWith => "linked-with",
		_ => throw new ArgumentOutOfRangeException(nameof(relation), relation, null)
	};

	private static string RankingHistoryReasonToken(ProjectGitHistoryUnavailableReason reason) => reason switch
	{
		ProjectGitHistoryUnavailableReason.None => "none",
		ProjectGitHistoryUnavailableReason.GitUnavailable => "git-unavailable",
		ProjectGitHistoryUnavailableReason.NotRepository => "not-repository",
		ProjectGitHistoryUnavailableReason.NestedRepository => "nested-repository",
		ProjectGitHistoryUnavailableReason.OldGitPromisorRepository => "old-git-promisor-repository",
		ProjectGitHistoryUnavailableReason.ProcessFailed => "process-failed",
		ProjectGitHistoryUnavailableReason.OutputLimitExceeded => "output-limit-exceeded",
		ProjectGitHistoryUnavailableReason.InvalidOutput => "invalid-output",
		_ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
	};

	private static string RankingRoleToken(ImportanceFileRole role) => role switch
	{
		ImportanceFileRole.Source => "source",
		ImportanceFileRole.TestSource => "test-source",
		ImportanceFileRole.Manifest => "manifest",
		ImportanceFileRole.EntryPoint => "entry-point",
		_ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
	};

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
		writer.WriteAttributeString(localName, XmlTextSanitizer.Sanitize(value));

	private static void WriteSanitizedXmlElementString(
		XmlWriter writer,
		string localName,
		string value) =>
		writer.WriteElementString(localName, XmlTextSanitizer.Sanitize(value));

	private static void WriteSanitizedXmlString(XmlWriter writer, string value) =>
		writer.WriteString(XmlTextSanitizer.Sanitize(value));

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

	private static string GetHumanReadableContentRoot(
		ProjectContextPlan plan,
		bool protectPrivateData = false)
	{
		var displayRootPath = GetHumanReadableContentDisplayRoot(plan);
		return OutputRootPathPresentation.Resolve(
			plan.SourceRoot,
			displayRootPath,
			protectPrivateData && plan.Selection.HidePrivateData == true);
	}

	private static string GetHumanReadableContentRoot(
		ProjectContextPlan plan,
		OutputPathRedactionDecision? pathRedaction) =>
		OutputRootPathPresentation.ResolvePath(
			GetHumanReadableContentDisplayRoot(plan),
			pathRedaction).Text;

	private static string GetHumanReadableContentDisplayRoot(ProjectContextPlan plan)
	{
		if (plan.SourceIdentity is not
		    {
			    SourceType: ProjectSourceType.GitClone,
			    SourceReference.Length: > 0
		    } identity)
		{
			return plan.SourceRoot;
		}

		var displayRootPath = RepositoryWebPathPresentationService.NormalizeForDisplay(identity.SourceReference);
		return displayRootPath.Length > 0 ? displayRootPath : identity.SourceReference;
	}

	private static Func<string, string>? CreateContentPathMapper(
		ProjectContextPlan plan,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		bool useSourceMappedStructuredPaths) =>
		format is ProjectContextDocumentFormat.Text or ProjectContextDocumentFormat.Markdown
			? TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(plan.SourceRoot)
			: CreateSourceContentPathMapper(plan, useSourceMappedStructuredPaths, view);

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
		MarkdownInlineLiteralEncoder.Encode(value);

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

	private sealed class BudgetedUtf8CompleteSourceSnapshot(
		IFileContentSnapshot inner,
		WeightedByteBudget.Lease lease) : IFileContentSnapshot, IUtf8FileContentSnapshot
	{
		private int _disposed;

		public FileContentMetricsResult Result => inner.Result;

		public ValueTask CopyTextToAsync(
			int maximumCharacters,
			Func<ReadOnlyMemory<char>, CancellationToken, ValueTask> writeChunk,
			CancellationToken cancellationToken = default) =>
			inner.CopyTextToAsync(maximumCharacters, writeChunk, cancellationToken);

		public ValueTask CopyUtf8ToAsync(
			int maximumCharacters,
			Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> writeChunk,
			CancellationToken cancellationToken = default) =>
			((IUtf8FileContentSnapshot)inner)
			.CopyUtf8ToAsync(maximumCharacters, writeChunk, cancellationToken);

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

	private sealed class RankingMetadataValidatedSourceSnapshot(
		IFileContentSnapshot inner,
		string sourcePath,
		RankingSourceVersion expectedVersion) : IFileContentSnapshot
	{
		private int _disposed;

		public FileContentMetricsResult Result => inner.Result;

		public async ValueTask CopyTextToAsync(
			int maximumCharacters,
			Func<ReadOnlyMemory<char>, CancellationToken, ValueTask> writeChunk,
			CancellationToken cancellationToken = default)
		{
			await inner.CopyTextToAsync(maximumCharacters, writeChunk, cancellationToken)
				.ConfigureAwait(false);
			EnsureMetadataCurrent(sourcePath, expectedVersion);
		}

		public async ValueTask DisposeAsync()
		{
			if (Interlocked.Exchange(ref _disposed, 1) != 0)
				return;
			await inner.DisposeAsync().ConfigureAwait(false);
		}
	}

	private sealed class RankingValidatedUtf8SourceSnapshot(
		IFileContentSnapshot inner,
		string sourcePath,
		RankingSourceVersion expectedVersion) : IFileContentSnapshot, IUtf8FileContentSnapshot
	{
		private int _disposed;

		public FileContentMetricsResult Result => inner.Result;

		public async ValueTask CopyTextToAsync(
			int maximumCharacters,
			Func<ReadOnlyMemory<char>, CancellationToken, ValueTask> writeChunk,
			CancellationToken cancellationToken = default)
		{
			await inner.CopyTextToAsync(maximumCharacters, writeChunk, cancellationToken)
				.ConfigureAwait(false);
		}

		public async ValueTask CopyUtf8ToAsync(
			int maximumCharacters,
			Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> writeChunk,
			CancellationToken cancellationToken = default)
		{
			using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			ContentPipelineDiagnostics.RecordSourceVersionHashPass();
			await ((IUtf8FileContentSnapshot)inner)
				.CopyUtf8ToAsync(
					maximumCharacters,
					async (chunk, token) =>
					{
						token.ThrowIfCancellationRequested();
						hash.AppendData(chunk.Span);
						ContentPipelineDiagnostics.RecordSourceVersionHashBytes(chunk.Length);
						await writeChunk(chunk, token).ConfigureAwait(false);
					},
					cancellationToken)
				.ConfigureAwait(false);
			var actualHash = hash.GetHashAndReset();
			var expectedHash = expectedVersion.ContentHash is null
				? []
				: Convert.FromHexString(expectedVersion.ContentHash);
			if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
			{
				throw new IOException(
					"A selected source file changed after importance facts were indexed; repeat the export.");
			}
			EnsureMetadataCurrent(sourcePath, expectedVersion);
		}

		public async ValueTask DisposeAsync()
		{
			if (Interlocked.Exchange(ref _disposed, 1) != 0)
				return;
			await inner.DisposeAsync().ConfigureAwait(false);
		}
	}

	private static void EnsureMetadataCurrent(
		string sourcePath,
		RankingSourceVersion expectedVersion)
	{
		if (!expectedVersion.HasMatchingMetadata(sourcePath))
		{
			throw new IOException(
				"A selected source file changed after importance facts were indexed; repeat the export.");
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
