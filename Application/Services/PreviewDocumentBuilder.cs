using System.Buffers;
using System.Runtime.CompilerServices;
using DevProjex.Application.Secrets;
using DevProjex.Kernel;

namespace DevProjex.Application.Services;

public readonly record struct PreviewDocumentBuildResult(
	IPreviewTextDocument Document,
	ExportOutputMetrics Metrics);

/// <summary>
/// Builds readable preview documents with bounded memory usage.
/// Large payloads are stored in a temporary UTF-8 backing file instead of one giant managed string.
/// </summary>
public sealed class PreviewDocumentBuilder(
    IFileContentAnalyzer contentAnalyzer,
    Func<FileContentClassification, string>? omissionMessageProvider = null)
{
    private const string ClipboardBlankLine = "\u00A0";
    private const string NoContentMarker = "[No Content, 0 bytes]";
    private const string BinaryMarker = "[Binary file; content omitted]";
    private const string LargeTextMarker = "[File is too large for interactive preview; export to inspect complete content]";
    private const string UnreadableMarker = "[File could not be read]";
    private const string AccessDeniedMarker = "[Access denied while reading file]";
    private const string MissingMarker = "[File disappeared while it was being read]";
    private const string UnsupportedEncodingMarker = "[Unsupported text encoding]";
    private const long MaximumInteractiveFileBytes = 10 * 1024 * 1024;
    private const string WhitespaceMarkerPrefix = "[Whitespace, ";
    private const string WhitespaceMarkerSuffix = " bytes]";
    private const int InMemoryDocumentThresholdChars = 500_000;
    private const long MaximumParallelPreparationFileBytes = 1024 * 1024;
    private const int MaximumParallelPreparations = 8;

    public IPreviewTextDocument CreateInMemory(
		string? text,
		IReadOnlyList<PreviewDocumentSection>? sections = null,
		IReadOnlyList<PreviewRedactionSpan>? redactions = null)
        => new InMemoryPreviewTextDocument(text, sections, redactions);

    public IPreviewTextDocument CreateDocument(
        string? text,
        IReadOnlyList<PreviewDocumentSection>? sections = null)
		=> CreateDocumentWithMetrics(text, sections).Document;

	public PreviewDocumentBuildResult CreateDocumentWithMetrics(
		string? text,
		IReadOnlyList<PreviewDocumentSection>? sections = null)
    {
        var value = text ?? string.Empty;
        if (value.Length <= InMemoryDocumentThresholdChars)
		{
			return new PreviewDocumentBuildResult(
				CreateInMemory(value, sections),
				ExportOutputMetricsCalculator.FromText(value));
		}

        using var builder = new PreviewTextStorageBuilder(InMemoryDocumentThresholdChars);
        builder.AppendExactText(value.AsSpan());
		return builder.BuildResult(sections);
    }

	public IPreviewTextDocument CreateInMemoryWithGeneratedPathRedaction(
		string text,
		OutputPathPresentationResult pathPresentation)
	{
		var redactions = new List<PreviewRedactionSpan>(1);
		AppendGeneratedPathRedactionFromText(redactions, text, pathPresentation);
		return CreateInMemory(text, redactions: redactions);
	}

    public async Task<IPreviewTextDocument> CreateDocumentAsync(
        Func<Stream, CancellationToken, Task> writeAsync,
        CancellationToken cancellationToken)
		=> (await CreateDocumentWithMetricsAsync(writeAsync, cancellationToken).ConfigureAwait(false)).Document;

	public async Task<PreviewDocumentBuildResult> CreateDocumentWithMetricsAsync(
		Func<Stream, CancellationToken, Task> writeAsync,
		CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeAsync);

		await using var stream = new SpillablePreviewWriteStream();
		await writeAsync(stream, cancellationToken).ConfigureAwait(false);
		await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
		if (!stream.HasSpilled)
		{
			var text = await stream.ReadBufferedTextAsync(cancellationToken).ConfigureAwait(false);
			return CreateDocumentWithMetrics(text);
		}

		var storagePath = await stream.DetachStoragePathAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			return await BuildDocumentFromUtf8FileAsync(storagePath, cancellationToken)
				.ConfigureAwait(false);
		}
		catch
		{
			DisposeStorageFile(storagePath);
			throw;
		}
    }

    public async Task<IPreviewTextDocument?> BuildContentDocumentAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken,
		Func<string, string>? displayPathMapper,
		bool includeOmissionMarkers = false,
		ContentTransformationContext? transformationContext = null,
		bool includeSourceCoordinateMaps = false,
		string? displayRootPath = null,
		OutputPathRedactionDecision? outputPathRedaction = null,
		string? projectRoot = null) =>
		(await BuildContentDocumentWithMetricsAsync(
			filePaths,
			cancellationToken,
			displayPathMapper,
			includeOmissionMarkers,
			transformationContext,
			includeSourceCoordinateMaps,
			displayRootPath,
			outputPathRedaction,
			projectRoot).ConfigureAwait(false))?.Document;

    public async Task<PreviewDocumentBuildResult?> BuildContentDocumentWithMetricsAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken,
		Func<string, string>? displayPathMapper,
		bool includeOmissionMarkers = false,
		ContentTransformationContext? transformationContext = null,
		bool includeSourceCoordinateMaps = false,
		string? displayRootPath = null,
		OutputPathRedactionDecision? outputPathRedaction = null,
		string? projectRoot = null)
    {
		var orderedFiles = ContentPathOrdering.BuildOrderedUnique(filePaths, cancellationToken);
		outputPathRedaction ??= OutputRootPathPresentation.CaptureRedactionDecision(transformationContext);
		await EnsurePersistentIdentityReadyAsync(transformationContext, cancellationToken).ConfigureAwait(false);
        if (orderedFiles.Length == 0)
        {
			using var emptyScope = transformationContext?.BeginOutputFromOwnedOrderedUnique(
				orderedFiles,
				cancellationToken);
			CompleteTransformation(emptyScope, transformationContext);
            return null;
		}

        using var builder = new PreviewTextStorageBuilder(InMemoryDocumentThresholdChars);
        var sections = new List<PreviewDocumentSection>(orderedFiles.Length);
		var redactions = new List<PreviewRedactionSpan>();
		using var transformationScope = transformationContext?.BeginOutputFromOwnedOrderedUnique(
			orderedFiles,
			cancellationToken);
		var redactionScope = transformationScope?.Redaction;
		var wroteRoot = false;
		if (!string.IsNullOrWhiteSpace(displayRootPath))
		{
			var rootPresentation = ResolveSingleLinePathPresentation(
				displayRootPath,
				outputPathRedaction);
			var rootLine = builder.LineCount + 1;
			builder.AppendLine(ContextRootPresentation.Prefix + rootPresentation.Text);
			AppendGeneratedPathRedaction(
				redactions,
				rootPresentation,
				rootLine,
				ContextRootPresentation.Prefix.Length);
			wroteRoot = true;
		}
		var anyWritten = await AppendContentEntriesAsync(
            builder,
            orderedFiles,
            sections,
            displayPathMapper,
			prependSectionSeparator: wroteRoot,
            includeOmissionMarkers,
			redactionScope,
			transformationScope,
			redactions,
			includeSourceCoordinateMaps,
			outputPathRedaction,
			projectRoot,
            cancellationToken).ConfigureAwait(false);

		CompleteTransformation(transformationScope, transformationContext);
		if (!anyWritten)
			return null;

		return builder.BuildResult(sections, redactions);
    }

    public async Task<IPreviewTextDocument> BuildTreeAndContentDocumentAsync(
        string treeText,
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken,
        Func<string, string>? displayPathMapper,
		bool includeOmissionMarkers = false,
		ContentTransformationContext? transformationContext = null,
		bool includeSourceCoordinateMaps = false,
		OutputPathRedactionDecision? outputPathRedaction = null,
		OutputPathPresentationResult? treeRootPresentation = null,
		string? projectRoot = null) =>
		(await BuildTreeAndContentDocumentWithMetricsAsync(
			treeText,
			filePaths,
			cancellationToken,
			displayPathMapper,
			includeOmissionMarkers,
			transformationContext,
			includeSourceCoordinateMaps,
			outputPathRedaction,
			treeRootPresentation,
			projectRoot).ConfigureAwait(false)).Document;

    public async Task<PreviewDocumentBuildResult> BuildTreeAndContentDocumentWithMetricsAsync(
        string treeText,
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken,
        Func<string, string>? displayPathMapper,
		bool includeOmissionMarkers = false,
		ContentTransformationContext? transformationContext = null,
		bool includeSourceCoordinateMaps = false,
		OutputPathRedactionDecision? outputPathRedaction = null,
		OutputPathPresentationResult? treeRootPresentation = null,
		string? projectRoot = null)
    {
		var orderedFiles = ContentPathOrdering.BuildOrderedUnique(filePaths, cancellationToken);
		outputPathRedaction ??= OutputRootPathPresentation.CaptureRedactionDecision(transformationContext);
		await EnsurePersistentIdentityReadyAsync(transformationContext, cancellationToken).ConfigureAwait(false);
		var normalizedTreeTextLength = TrailingLineEndingTrimming.GetTrimmedLength(treeText);

        if (orderedFiles.Length == 0)
        {
			using var emptyScope = transformationContext?.BeginOutputFromOwnedOrderedUnique(
				orderedFiles,
				cancellationToken);
			CompleteTransformation(emptyScope, transformationContext);
			return CreateDocumentWithMetrics(normalizedTreeTextLength == treeText.Length
				? treeText
				: treeText[..normalizedTreeTextLength]);
		}

        using var builder = new PreviewTextStorageBuilder(InMemoryDocumentThresholdChars);
        var sections = new List<PreviewDocumentSection>(orderedFiles.Length);
		var redactions = new List<PreviewRedactionSpan>();
		using var transformationScope = transformationContext?.BeginOutputFromOwnedOrderedUnique(
			orderedFiles,
			cancellationToken);
		var redactionScope = transformationScope?.Redaction;
		var wroteTree = AppendMultilineText(builder, treeText.AsSpan(0, normalizedTreeTextLength));
		if (treeRootPresentation is { } rootPresentation)
			AppendGeneratedPathRedactionFromText(redactions, treeText, rootPresentation);
        var wroteContent = await AppendContentEntriesAsync(
            builder,
            orderedFiles,
            sections,
            displayPathMapper,
            prependSectionSeparator: wroteTree,
            includeOmissionMarkers,
			redactionScope,
			transformationScope,
			redactions,
			includeSourceCoordinateMaps,
			outputPathRedaction,
			projectRoot,
            cancellationToken).ConfigureAwait(false);
		CompleteTransformation(transformationScope, transformationContext);

        if (!wroteTree && !wroteContent)
			return CreateDocumentWithMetrics(string.Empty);

		return builder.BuildResult(sections, redactions);
    }

	private static void CompleteTransformation(
		ContentTransformationScope? scope,
		ContentTransformationContext? context)
	{
		scope?.Redaction?.Complete();
		scope?.Compression?.Complete();
		if (scope?.Redaction is not null && context?.Redaction is { } redaction)
			redaction.Session.SchedulePendingPersistentMarkMigrationsAfterPreview(redaction.ProjectRoot);
	}

	private static async ValueTask EnsurePersistentIdentityReadyAsync(
		ContentTransformationContext? transformationContext,
		CancellationToken cancellationToken)
	{
		var redaction = transformationContext?.Redaction;
		if (redaction is not null &&
		    await redaction.Session
			    .EnsureCurrentPersistentIdentityReadyAsync(redaction.Features, cancellationToken)
			    .ConfigureAwait(false) !=
		    PersistentSecretIdentityAvailability.Ready)
		{
			throw new SecretDetectionException("The persistent secret identity key is unavailable.");
		}
	}

    private async Task<bool> AppendContentEntriesAsync(
        PreviewTextStorageBuilder builder,
        IReadOnlyList<string> orderedFiles,
        ICollection<PreviewDocumentSection> sections,
        Func<string, string>? displayPathMapper,
        bool prependSectionSeparator,
        bool includeOmissionMarkers,
		SecretRedactionScope? redactionScope,
		ContentTransformationScope? transformationScope,
		ICollection<PreviewRedactionSpan> redactions,
		bool includeSourceCoordinateMaps,
		OutputPathRedactionDecision? outputPathRedaction,
		string? projectRoot,
        CancellationToken cancellationToken)
    {
        var anyWritten = false;
        var trimTrailingEstimatedLine = false;

        await foreach (var prepared in PrepareContentEntriesAsync(
                           orderedFiles,
                           displayPathMapper,
                           redactionScope,
                           transformationScope,
						   projectRoot,
                           cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = prepared.FilePath;
            var readResult = prepared.ReadResult;
            var content = readResult.Content;
			if (redactionScope is not null &&
			    readResult.Classification is not (
				    FileContentClassification.Text or
				    FileContentClassification.TooLarge or
				    FileContentClassification.Unreadable or
				    FileContentClassification.UnsupportedEncoding or
				    FileContentClassification.Binary))
			{
				throw new SecretDetectionException(
					$"Hide Secrets could not inspect '{file}' ({readResult.Classification}).");
			}
            if ((!readResult.IsText || content is null) && !includeOmissionMarkers)
                continue;

            if (!anyWritten)
            {
                if (prependSectionSeparator)
                {
                    builder.AppendLine(ClipboardBlankLine);
                    builder.AppendLine(ClipboardBlankLine);
                }
            }
            else
            {
                builder.AppendLine(ClipboardBlankLine);
                builder.AppendLine(ClipboardBlankLine);
            }

            anyWritten = true;
            trimTrailingEstimatedLine = false;

			var displayPathPresentation = ResolveSingleLinePathPresentation(
				prepared.DisplayPath,
				outputPathRedaction);
			var displayPath = displayPathPresentation.Text;
            var sectionStartLine = builder.LineCount + 1;
            builder.AppendLine($"{displayPath}:");
			AppendGeneratedPathRedaction(redactions, displayPathPresentation, sectionStartLine);
            builder.AppendLine(ClipboardBlankLine);

            if (!readResult.IsText || content is null)
            {
                builder.AppendLine(GetOmissionMarker(readResult.Classification));
                sections.Add(new PreviewDocumentSection(
                    displayPath,
                    sectionStartLine,
                    builder.LineCount,
                    sectionStartLine,
                    sectionStartLine + 2,
                    SourcePath: file));
                continue;
            }

            if (content.IsEmpty)
            {
                builder.AppendLine(NoContentMarker);
                sections.Add(new PreviewDocumentSection(
                    displayPath,
                    sectionStartLine,
                    builder.LineCount,
                    sectionStartLine,
                    sectionStartLine + 2,
                    SourcePath: file));
                continue;
            }

            if (content.IsWhitespaceOnly)
            {
                builder.AppendLine($"{WhitespaceMarkerPrefix}{content.SizeBytes}{WhitespaceMarkerSuffix}");
                sections.Add(new PreviewDocumentSection(
                    displayPath,
                    sectionStartLine,
                    builder.LineCount,
                    sectionStartLine,
                    sectionStartLine + 2,
                    SourcePath: file));
                continue;
            }

            if (content.IsEstimated)
            {
				// Past the read limit the text is not in the document either way, so Hide Secrets
				// has nothing to hide here and no reason to abandon the rest of the selection. The
				// file is marked exactly as it would be with the setting off.
                if (includeOmissionMarkers)
                {
                    builder.AppendLine(GetOmissionMarker(FileContentClassification.TooLarge));
                }
                else
                {
                    builder.AppendLine(string.Empty);
                    trimTrailingEstimatedLine = true;
                }

                sections.Add(new PreviewDocumentSection(
                    displayPath,
                    sectionStartLine,
                    builder.LineCount,
                    sectionStartLine,
                    sectionStartLine + 2,
                    SourcePath: file));
                continue;
            }

            trimTrailingEstimatedLine = false;
			using var contentLease = redactionScope?.TrackFullContentBuffer();
			// Compression first: secrets must be detected in the text that actually ships, so a
			// value inside a removed body is neither redacted nor counted.
			var compression = prepared.Compression;
			var compressed = compression?.Text ?? content.Content;
			var transformed = redactionScope?.Redact(
				file,
				compressed,
				compression?.Map,
				cancellationToken);
			var text = transformed?.Text ?? compressed;
			var coordinateMap = includeSourceCoordinateMaps
				? PreviewContentCoordinateMap.Create(
					text.AsSpan(),
					compression?.Map ?? ContentTransformMap.Identity,
					transformed?.CoordinateMap)
				: null;
			if (transformed is { Spans.Count: > 0 })
			{
				AppendPreviewRedactionSpans(
					redactions,
					transformed,
					builder.LineCount + 1);
			}
			AppendTrimmedContent(builder, text.AsSpan());
            sections.Add(new PreviewDocumentSection(
                displayPath,
                sectionStartLine,
                builder.LineCount,
                sectionStartLine,
                sectionStartLine + 2,
				coordinateMap,
				file));
        }

        if (anyWritten && trimTrailingEstimatedLine)
            builder.TrimTrailingEmptyLine();

        return anyWritten;
    }

    private async IAsyncEnumerable<PreparedContentEntry> PrepareContentEntriesAsync(
        IReadOnlyList<string> orderedFiles,
        Func<string, string>? displayPathMapper,
        SecretRedactionScope? redactionScope,
        ContentTransformationScope? transformationScope,
		string? projectRoot,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
		if (orderedFiles.Count <= 1)
		{
			foreach (var file in orderedFiles)
			{
				yield return await PrepareContentEntryAsync(
						file,
						displayPathMapper,
						redactionScope,
						transformationScope,
						projectRoot,
						cancellationToken)
					.ConfigureAwait(false);
			}

			yield break;
		}

		using var preparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var preparationToken = preparationCancellation.Token;
		var pending = new Queue<Task<PreparedContentEntry>>(MaximumParallelPreparations);
		try
		{
			foreach (var file in orderedFiles)
			{
				preparationToken.ThrowIfCancellationRequested();
				if (!IsSmallFile(file))
				{
					while (pending.TryDequeue(out var prepared))
						yield return await prepared.ConfigureAwait(false);

					yield return await PrepareContentEntryAsync(
							file,
							displayPathMapper,
							redactionScope,
							transformationScope,
							projectRoot,
							preparationToken)
						.ConfigureAwait(false);
					continue;
				}

				pending.Enqueue(Task.Run(
					() => PrepareContentEntryAsync(
						file,
						displayPathMapper,
						redactionScope,
						transformationScope,
						projectRoot,
						preparationToken).AsTask(),
					preparationToken));
				if (pending.Count >= MaximumParallelPreparations)
					yield return await pending.Dequeue().ConfigureAwait(false);
			}

			while (pending.TryDequeue(out var prepared))
				yield return await prepared.ConfigureAwait(false);
		}
		finally
		{
			TryCancel(preparationCancellation);
			await ObservePendingPreparationsAsync(pending).ConfigureAwait(false);
		}
	}

	private static void TryCancel(CancellationTokenSource cancellation)
	{
		try
		{
			cancellation.Cancel();
		}
		catch (AggregateException)
		{
			// Pending preparation failures must not replace the consumer's exception.
		}
	}

	private static async Task ObservePendingPreparationsAsync(
		IReadOnlyCollection<Task<PreparedContentEntry>> pending)
	{
		if (pending.Count == 0)
			return;
		try
		{
			await Task.WhenAll(pending).ConfigureAwait(false);
		}
		catch
		{
			// The consumer's exception or cancellation remains authoritative.
		}
	}

    private async ValueTask<PreparedContentEntry> PrepareContentEntryAsync(
        string file,
        Func<string, string>? displayPathMapper,
        SecretRedactionScope? redactionScope,
        ContentTransformationScope? transformationScope,
		string? projectRoot,
        CancellationToken cancellationToken)
    {
        var maximumFileBytes = redactionScope is null
            ? MaximumInteractiveFileBytes
            : SecretRedactionOutputPreparer.MaximumScannableFileBytes;
		var unavailable = ClassifyUnavailableSource(projectRoot, file);
		ContentReadFact? readFact = null;
		FileContentReadResult readResult;
		if (unavailable is { } unavailableBeforeRead)
		{
			readResult = new FileContentReadResult(unavailableBeforeRead);
		}
		else
		{
			readFact = await contentAnalyzer
				.ReadFactAsync(file, maximumFileBytes, cancellationToken)
				.ConfigureAwait(false);
			var unavailableAfterRead = ClassifyUnavailableSource(projectRoot, file);
			readResult = unavailableAfterRead is { } classification
				? new FileContentReadResult(classification)
				: readFact.ToReadResult();
		}
        var displayPath = MapDisplayPath(file, displayPathMapper);
        var content = readResult.Content;
		var compression = readResult.IsText &&
		                  content is { IsEstimated: false, IsEmpty: false, IsWhitespaceOnly: false }
			? readFact?.Fingerprint is { } fingerprint
				? transformationScope?.Compress(
					file,
					displayPath,
					content.Content,
					fingerprint,
					cancellationToken)
				: transformationScope?.Compress(
					file,
					displayPath,
					content.Content,
					cancellationToken)
			: null;
        return new PreparedContentEntry(file, displayPath, readResult, compression);
    }

	private static FileContentClassification? ClassifyUnavailableSource(
		string? projectRoot,
		string path) =>
		string.IsNullOrWhiteSpace(projectRoot)
			? null
			: ProjectSourcePathPolicy.ClassifyUnavailable(projectRoot, path);

    private static bool IsSmallFile(string path)
    {
        try
        {
            return new FileInfo(path).Length <= MaximumParallelPreparationFileBytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private sealed record PreparedContentEntry(
        string FilePath,
        string DisplayPath,
        FileContentReadResult ReadResult,
        CodeCompressionResult? Compression);

	private static void AppendPreviewRedactionSpans(
		ICollection<PreviewRedactionSpan> destination,
		SecretTextRedactionResult result,
		int firstContentLine)
	{
		var sourcePosition = 0;
		var line = firstContentLine;
		var column = 0;
		foreach (var span in result.Spans)
		{
			AdvancePosition(result.Text.AsSpan(sourcePosition, span.Start - sourcePosition), ref line, ref column);
			var spanText = result.Text.AsSpan(span.Start, span.Length);
			var segmentStart = 0;
			var index = 0;
			while (index <= spanText.Length)
			{
				var lineBreakLength = index < spanText.Length
					? GetLineBreakLength(spanText, index)
					: 0;
				if (index < spanText.Length && lineBreakLength == 0)
				{
					index++;
					continue;
				}

				var segmentLength = index - segmentStart;
				if (segmentLength > 0)
				{
					destination.Add(new PreviewRedactionSpan(
						span.OccurrenceId,
						span.RuleId,
						line,
						column,
						segmentLength,
						span.State,
						span.State == SecretPreviewSpanState.KeptAsIs
							? segmentLength
							: span.SourceLength,
						span.Source,
						span.PersistentMarkHash,
						span.SessionMarkId,
						span.PersistentMarkId,
						span.RelativePath,
						span.CascadedOccurrenceIds));
				}

				if (lineBreakLength > 0)
				{
					line++;
					column = 0;
					index += lineBreakLength;
					segmentStart = index;
				}
				else
				{
					column += segmentLength;
					break;
				}
			}
			sourcePosition = span.Start + span.Length;
		}
	}

	private static void AppendGeneratedPathRedaction(
		ICollection<PreviewRedactionSpan> destination,
		OutputPathPresentationResult presentation,
		int lineNumber,
		int columnOffset = 0)
	{
		if (!presentation.HasRedaction)
			return;

		destination.Add(new PreviewRedactionSpan(
			presentation.OccurrenceId!,
			OutputRootPathPresentation.LocalUserRuleId,
			lineNumber,
			presentation.SegmentStart + columnOffset,
			presentation.SegmentLength,
			presentation.State,
			presentation.SourceLength,
			SecretFindingSource.GeneratedPath));
	}

	private static OutputPathPresentationResult ResolveSingleLinePathPresentation(
		string path,
		OutputPathRedactionDecision? redactionDecision) =>
		OutputRootPathPresentation.ResolvePath(
			SingleLineTextEscaping.Escape(path),
			redactionDecision);

	private static void AppendGeneratedPathRedactionFromText(
		ICollection<PreviewRedactionSpan> destination,
		string text,
		OutputPathPresentationResult presentation)
	{
		if (!presentation.HasRedaction)
			return;

		var segment = presentation.Text.AsSpan(
			presentation.SegmentStart,
			presentation.SegmentLength);
		var segmentStart = text.AsSpan().IndexOf(segment);
		if (segmentStart < 0)
			return;

		var line = 1;
		var column = 0;
		AdvancePosition(text.AsSpan(0, segmentStart), ref line, ref column);
		destination.Add(new PreviewRedactionSpan(
			presentation.OccurrenceId!,
			OutputRootPathPresentation.LocalUserRuleId,
			line,
			column,
			presentation.SegmentLength,
			presentation.State,
			presentation.SourceLength,
			SecretFindingSource.GeneratedPath));
	}

	private static void AdvancePosition(ReadOnlySpan<char> text, ref int line, ref int column)
	{
		for (var index = 0; index < text.Length;)
		{
			var lineBreakLength = GetLineBreakLength(text, index);
			if (lineBreakLength > 0)
			{
				line++;
				column = 0;
				index += lineBreakLength;
			}
			else
			{
				column++;
				index++;
			}
		}
	}

	private static int GetLineBreakLength(ReadOnlySpan<char> text, int index)
	{
		if (text[index] == '\n')
			return 1;
		if (text[index] != '\r')
			return 0;
		return index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
	}

    private string GetOmissionMarker(FileContentClassification classification)
    {
        if (omissionMessageProvider is not null)
            return $"[{omissionMessageProvider(classification)}]";

        return classification switch
        {
            FileContentClassification.Binary => BinaryMarker,
            FileContentClassification.TooLarge => LargeTextMarker,
            FileContentClassification.AccessDenied => AccessDeniedMarker,
            FileContentClassification.Missing => MissingMarker,
            FileContentClassification.UnsupportedEncoding => UnsupportedEncodingMarker,
            _ => UnreadableMarker
        };
    }

    private static bool AppendMultilineText(PreviewTextStorageBuilder builder, ReadOnlySpan<char> text)
    {
        if (text.Length == 0)
            return false;

        var wroteAnyLine = false;
        var lineStart = 0;

        for (var index = 0; index < text.Length;)
        {
            var lineBreakLength = GetLineBreakLength(text, index);
            if (lineBreakLength == 0)
            {
                index++;
                continue;
            }

            var line = text.Slice(lineStart, index - lineStart);
            builder.AppendLine(line);
            wroteAnyLine = true;
            index += lineBreakLength;
            lineStart = index;
        }

        if (lineStart < text.Length)
        {
            var line = text[lineStart..];
            builder.AppendLine(line);
            wroteAnyLine = true;
        }

        return wroteAnyLine;
    }

    private static void AppendTrimmedContent(PreviewTextStorageBuilder builder, ReadOnlySpan<char> content)
    {
        var end = content.Length;
        while (end > 0 && content[end - 1] is '\r' or '\n')
            end--;

        if (end <= 0)
            return;

        AppendMultilineText(builder, content[..end]);
    }

    private static string MapDisplayPath(string filePath, Func<string, string>? displayPathMapper)
    {
        if (displayPathMapper is null)
            return filePath;

        try
        {
            var mapped = displayPathMapper(filePath);
            return string.IsNullOrWhiteSpace(mapped) ? filePath : mapped;
        }
        catch
        {
            return filePath;
        }
    }

    private static async Task<PreviewDocumentBuildResult> BuildDocumentFromUtf8FileAsync(
        string storagePath,
        CancellationToken cancellationToken)
    {
        var metrics = await ReadStorageMetricsAsync(storagePath, cancellationToken)
            .ConfigureAwait(false);
        if (metrics.CharacterCount <= InMemoryDocumentThresholdChars)
        {
            var text = await File.ReadAllTextAsync(
                    storagePath,
                    PreviewTextStorageBuilder.Utf8WithoutBom,
                    cancellationToken)
                .ConfigureAwait(false);
            DisposeStorageFile(storagePath);
            return new PreviewDocumentBuildResult(
                new InMemoryPreviewTextDocument(text),
                ExportOutputMetricsCalculator.FromText(text));
        }

        return new PreviewDocumentBuildResult(
            new FileBackedPreviewTextDocument(
                storagePath,
                metrics.LineOffsets,
                metrics.FileLength,
                metrics.MaxLineLength,
                metrics.CharacterCount),
            metrics.OutputMetrics);
    }

    private static async Task<PreviewStorageMetrics> ReadStorageMetricsAsync(
        string storagePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            storagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 8192,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length == 0)
            return new PreviewStorageMetrics([], 0, 0, 0, ExportOutputMetrics.Empty);

        var offsets = new List<long> { 0 };
        var byteBuffer = ArrayPool<byte>.Shared.Rent(8192);
        var charBuffer = ArrayPool<char>.Shared.Rent(
            PreviewTextStorageBuilder.Utf8WithoutBom.GetMaxCharCount(byteBuffer.Length));
        var decoder = PreviewTextStorageBuilder.Utf8WithoutBom.GetDecoder();
        using var metricsWriter = ExportOutputMetricsCalculator.CreateTextWriter();
        try
        {
            long fileOffset = 0;
            long characterCount = 0;
            var currentLineLength = 0;
            var maxLineLength = 0;
            var firstRead = true;
            while (true)
            {
                var bytesRead = firstRead
                    ? await stream.ReadAtLeastAsync(
                            byteBuffer.AsMemory(),
                            minimumBytes: 3,
                            throwOnEndOfStream: false,
                            cancellationToken)
                        .ConfigureAwait(false)
                    : await stream.ReadAsync(byteBuffer.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                if (bytesRead == 0)
                    break;

                for (var index = 0; index < bytesRead; index++)
                {
                    if (byteBuffer[index] == (byte)'\n')
                        offsets.Add(fileOffset + index + 1);
                }

                var decodeOffset = firstRead &&
                                   bytesRead >= 3 &&
                                   byteBuffer[0] == 0xEF &&
                                   byteBuffer[1] == 0xBB &&
                                   byteBuffer[2] == 0xBF
                    ? 3
                    : 0;
                if (decodeOffset != 0)
                    metricsWriter.Write('\uFEFF');
                Decode(
                    byteBuffer.AsSpan(decodeOffset, bytesRead - decodeOffset),
                    flush: false);
                fileOffset += bytesRead;
                firstRead = false;
            }

            Decode(ReadOnlySpan<byte>.Empty, flush: true);
            return new PreviewStorageMetrics(
                offsets.ToArray(),
                stream.Length,
                characterCount,
                Math.Max(maxLineLength, currentLineLength),
                metricsWriter.Complete(cancellationToken));

            void Decode(ReadOnlySpan<byte> bytes, bool flush)
            {
                while (!bytes.IsEmpty || flush)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    decoder.Convert(
                        bytes,
                        charBuffer,
                        flush,
                        out var bytesUsed,
                        out var charactersUsed,
                        out var completed);
                    characterCount += charactersUsed;
                    var characters = charBuffer.AsSpan(0, charactersUsed);
                    metricsWriter.Write(characters);
                    foreach (var character in characters)
                    {
                        if (character == '\n')
                        {
                            maxLineLength = Math.Max(maxLineLength, currentLineLength);
                            currentLineLength = 0;
                        }
                        else if (character != '\r')
                        {
                            currentLineLength++;
                        }
                    }

                    bytes = bytes[bytesUsed..];
                    if (completed)
                        break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(byteBuffer, clearArray: true);
            ArrayPool<char>.Shared.Return(charBuffer, clearArray: true);
        }
    }

    private readonly record struct PreviewStorageMetrics(
        long[] LineOffsets,
        long FileLength,
        long CharacterCount,
        int MaxLineLength,
        ExportOutputMetrics OutputMetrics);

	private static string CreateStoragePath()
	{
		var previewDirectory = PrepareStorageDirectory(Path.GetTempPath());
		PreviewTextStorageScavenger.StartOnce(previewDirectory);
		return Path.Combine(previewDirectory, $"{Guid.NewGuid():N}.preview.txt");
	}

	internal static string PrepareStorageDirectory(string tempRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(tempRoot);
		var productDirectory = Path.Combine(tempRoot, "DevProjex");
		EnsurePrivateStorageDirectory(productDirectory);
		var previewDirectory = Path.Combine(productDirectory, "Preview");
		EnsurePrivateStorageDirectory(previewDirectory);
		return previewDirectory;
	}

	private static void EnsurePrivateStorageDirectory(string path)
	{
		Directory.CreateDirectory(path);
		RejectLinkedStorageDirectory(path);
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(
				path,
				UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
		RejectLinkedStorageDirectory(path);
	}

	private static void RejectLinkedStorageDirectory(string path)
	{
		if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
			throw new IOException("Preview storage cannot use a symbolic link or reparse point.");
	}

	private static FileStream OpenStorageFile(string storagePath, FileOptions options)
	{
		var streamOptions = new FileStreamOptions
		{
			Access = FileAccess.ReadWrite,
			Mode = FileMode.CreateNew,
			Share = FileShare.None,
			BufferSize = 8192,
			Options = options
		};
		if (!OperatingSystem.IsWindows())
			streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

		// Preview backing files can contain an explicitly kept credential. Keep them private
		// even though their randomized names live under the shared system temp directory.
		return new FileStream(storagePath, streamOptions);
	}

	/// <summary>
	/// Keeps one export-sized chunk below the LOH threshold in pooled memory, then spills
	/// to the existing private preview storage without narrowing the writable stream contract.
	/// </summary>
	internal sealed class SpillablePreviewWriteStream : Stream
	{
		internal const int MemoryLimitBytes = PreviewTextStreamWriter.BufferSizeBytes;

		private byte[]? _memoryBuffer;
		private MemoryStream? _memoryStream;
		private FileStream? _fileStream;
		private string? _storagePath;
		private bool _disposed;

		internal SpillablePreviewWriteStream()
		{
			_memoryBuffer = ArrayPool<byte>.Shared.Rent(MemoryLimitBytes);
			_memoryStream = new MemoryStream(
				_memoryBuffer,
				index: 0,
				count: _memoryBuffer.Length,
				writable: true,
				publiclyVisible: true);
			_memoryStream.SetLength(0);
		}

		internal bool HasSpilled => _fileStream is not null;

		internal string? StoragePath => _storagePath;

		public override bool CanRead => !_disposed;

		public override bool CanSeek => !_disposed;

		public override bool CanWrite => !_disposed;

		public override long Length
		{
			get
			{
				ThrowIfDisposed();
				return CurrentStream.Length;
			}
		}

		public override long Position
		{
			get
			{
				ThrowIfDisposed();
				return CurrentStream.Position;
			}
			set
			{
				ThrowIfDisposed();
				ArgumentOutOfRangeException.ThrowIfNegative(value);
				if (_fileStream is null && value > MemoryLimitBytes)
					SpillToFile();
				CurrentStream.Position = value;
			}
		}

		public override void Flush()
		{
			ThrowIfDisposed();
			CurrentStream.Flush();
		}

		public override Task FlushAsync(CancellationToken cancellationToken)
		{
			ThrowIfDisposed();
			cancellationToken.ThrowIfCancellationRequested();
			return CurrentStream.FlushAsync(cancellationToken);
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			ThrowIfDisposed();
			return CurrentStream.Read(buffer, offset, count);
		}

		public override int Read(Span<byte> buffer)
		{
			ThrowIfDisposed();
			return CurrentStream.Read(buffer);
		}

		public override Task<int> ReadAsync(
			byte[] buffer,
			int offset,
			int count,
			CancellationToken cancellationToken)
		{
			ThrowIfDisposed();
			return CurrentStream.ReadAsync(buffer, offset, count, cancellationToken);
		}

		public override ValueTask<int> ReadAsync(
			Memory<byte> buffer,
			CancellationToken cancellationToken = default)
		{
			ThrowIfDisposed();
			return CurrentStream.ReadAsync(buffer, cancellationToken);
		}

		public override int ReadByte()
		{
			ThrowIfDisposed();
			return CurrentStream.ReadByte();
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			ThrowIfDisposed();
			if (_fileStream is null && ResolveSeekPosition(offset, origin) > MemoryLimitBytes)
				SpillToFile();
			return CurrentStream.Seek(offset, origin);
		}

		public override void SetLength(long value)
		{
			ThrowIfDisposed();
			ArgumentOutOfRangeException.ThrowIfNegative(value);
			if (_fileStream is null && value > MemoryLimitBytes)
				SpillToFile();
			CurrentStream.SetLength(value);
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			ThrowIfDisposed();
			ArgumentNullException.ThrowIfNull(buffer);
			ArgumentOutOfRangeException.ThrowIfNegative(offset);
			ArgumentOutOfRangeException.ThrowIfNegative(count);
			if (offset > buffer.Length - count)
				throw new ArgumentException("The write range exceeds the source buffer.", nameof(count));

			EnsureStorageForWrite(count);
			CurrentStream.Write(buffer, offset, count);
		}

		public override void Write(ReadOnlySpan<byte> buffer)
		{
			ThrowIfDisposed();
			EnsureStorageForWrite(buffer.Length);
			CurrentStream.Write(buffer);
		}

		public override Task WriteAsync(
			byte[] buffer,
			int offset,
			int count,
			CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(buffer);
			ArgumentOutOfRangeException.ThrowIfNegative(offset);
			ArgumentOutOfRangeException.ThrowIfNegative(count);
			if (offset > buffer.Length - count)
				throw new ArgumentException("The write range exceeds the source buffer.", nameof(count));

			return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
		}

		public override ValueTask WriteAsync(
			ReadOnlyMemory<byte> buffer,
			CancellationToken cancellationToken = default)
		{
			ThrowIfDisposed();
			cancellationToken.ThrowIfCancellationRequested();
			if (RequiresSpill(buffer.Length))
				return WriteAfterSpillAsync(buffer, cancellationToken);
			return CurrentStream.WriteAsync(buffer, cancellationToken);
		}

		public override void WriteByte(byte value)
		{
			ThrowIfDisposed();
			EnsureStorageForWrite(1);
			CurrentStream.WriteByte(value);
		}

		internal async Task<string> ReadBufferedTextAsync(CancellationToken cancellationToken)
		{
			ThrowIfDisposed();
			if (_fileStream is not null)
				throw new InvalidOperationException("Spilled preview storage cannot be read as a memory buffer.");

			var stream = _memoryStream ??
			             throw new ObjectDisposedException(nameof(SpillablePreviewWriteStream));
			stream.Position = 0;
			using var reader = new StreamReader(
				stream,
				PreviewTextStorageBuilder.Utf8WithoutBom,
				detectEncodingFromByteOrderMarks: true,
				bufferSize: 8192,
				leaveOpen: true);
			return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
		}

		internal async ValueTask<string> DetachStoragePathAsync(CancellationToken cancellationToken)
		{
			ThrowIfDisposed();
			var stream = _fileStream ??
			             throw new InvalidOperationException("Preview storage has not spilled to a file.");
			await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
			await stream.DisposeAsync().ConfigureAwait(false);
			_fileStream = null;
			var storagePath = _storagePath ??
			                  throw new InvalidOperationException("Preview backing path is unavailable.");
			_storagePath = null;
			_disposed = true;
			return storagePath;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing && !_disposed)
			{
				_disposed = true;
				try
				{
					_fileStream?.Dispose();
				}
				finally
				{
					_fileStream = null;
					ReleaseMemoryBuffer();
					DeleteOwnedStorageFile();
				}
			}

			base.Dispose(disposing);
		}

		public override async ValueTask DisposeAsync()
		{
			if (_disposed)
				return;

			_disposed = true;
			try
			{
				if (_fileStream is not null)
					await _fileStream.DisposeAsync().ConfigureAwait(false);
			}
			finally
			{
				_fileStream = null;
				ReleaseMemoryBuffer();
				DeleteOwnedStorageFile();
				GC.SuppressFinalize(this);
			}
		}

		private Stream CurrentStream => (Stream?)_fileStream ?? _memoryStream ??
			throw new ObjectDisposedException(nameof(SpillablePreviewWriteStream));

		private void EnsureStorageForWrite(int count)
		{
			if (RequiresSpill(count))
				SpillToFile();
		}

		private bool RequiresSpill(int count)
		{
			if (_fileStream is not null)
				return false;
			var stream = _memoryStream ??
			             throw new ObjectDisposedException(nameof(SpillablePreviewWriteStream));
			var resultingLength = Math.Max(stream.Length, checked(stream.Position + count));
			return resultingLength > MemoryLimitBytes;
		}

		private async ValueTask WriteAfterSpillAsync(
			ReadOnlyMemory<byte> buffer,
			CancellationToken cancellationToken)
		{
			await SpillToFileAsync(cancellationToken).ConfigureAwait(false);
			await _fileStream!.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
		}

		private void SpillToFile()
		{
			if (_fileStream is not null)
				return;

			var memory = _memoryStream ??
			             throw new ObjectDisposedException(nameof(SpillablePreviewWriteStream));
			var storagePath = CreateStoragePath();
			FileStream? file = null;
			try
			{
				file = OpenStorageFile(
					storagePath,
					FileOptions.Asynchronous | FileOptions.SequentialScan);
				var position = memory.Position;
				if (memory.Length > 0)
					file.Write(_memoryBuffer!, 0, checked((int)memory.Length));
				file.Position = position;
				_fileStream = file;
				_storagePath = storagePath;
				ReleaseMemoryBuffer();
			}
			catch
			{
				file?.Dispose();
				DisposeStorageFile(storagePath);
				throw;
			}
		}

		private async ValueTask SpillToFileAsync(CancellationToken cancellationToken)
		{
			if (_fileStream is not null)
				return;

			var memory = _memoryStream ??
			             throw new ObjectDisposedException(nameof(SpillablePreviewWriteStream));
			var storagePath = CreateStoragePath();
			FileStream? file = null;
			try
			{
				file = OpenStorageFile(
					storagePath,
					FileOptions.Asynchronous | FileOptions.SequentialScan);
				var position = memory.Position;
				if (memory.Length > 0)
				{
					await file.WriteAsync(
							_memoryBuffer!.AsMemory(0, checked((int)memory.Length)),
							cancellationToken)
						.ConfigureAwait(false);
				}
				file.Position = position;
				_fileStream = file;
				_storagePath = storagePath;
				ReleaseMemoryBuffer();
			}
			catch
			{
				if (file is not null)
					await file.DisposeAsync().ConfigureAwait(false);
				DisposeStorageFile(storagePath);
				throw;
			}
		}

		private long ResolveSeekPosition(long offset, SeekOrigin origin)
		{
			var stream = _memoryStream ??
			             throw new ObjectDisposedException(nameof(SpillablePreviewWriteStream));
			return origin switch
			{
				SeekOrigin.Begin => offset,
				SeekOrigin.Current => checked(stream.Position + offset),
				SeekOrigin.End => checked(stream.Length + offset),
				_ => throw new ArgumentOutOfRangeException(nameof(origin), origin, null)
			};
		}

		private void ReleaseMemoryBuffer()
		{
			_memoryStream?.Dispose();
			_memoryStream = null;
			var buffer = Interlocked.Exchange(ref _memoryBuffer, null);
			if (buffer is not null)
				ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
		}

		private void DeleteOwnedStorageFile()
		{
			var storagePath = Interlocked.Exchange(ref _storagePath, null);
			if (storagePath is not null)
				DisposeStorageFile(storagePath);
		}

		private void ThrowIfDisposed()
		{
			if (_disposed)
				throw new ObjectDisposedException(nameof(SpillablePreviewWriteStream));
		}
	}

	private static void DisposeStorageFile(string storagePath)
    {
        try
        {
            if (File.Exists(storagePath))
                File.Delete(storagePath);
        }
        catch
        {
            // Best-effort cleanup only.
		}
	}

internal static class PreviewTextStorageScavenger
{
	private const string FileSuffix = ".preview.txt";
	private const int MaximumFilesRemoved = 64;
	internal static readonly TimeSpan MinimumAge = TimeSpan.FromHours(24);
	private static int _started;

	internal static void StartOnce(string previewDirectory)
	{
		if (Interlocked.Exchange(ref _started, 1) != 0)
			return;

		_ = Task.Run(() => Scavenge(previewDirectory, DateTime.UtcNow, MinimumAge));
	}

	internal static int Scavenge(string previewDirectory, DateTime utcNow, TimeSpan minimumAge)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(previewDirectory);
		ArgumentOutOfRangeException.ThrowIfLessThan(minimumAge, TimeSpan.Zero);
		var removed = 0;
		try
		{
			if (!Directory.Exists(previewDirectory) ||
			    (File.GetAttributes(previewDirectory) & FileAttributes.ReparsePoint) != 0)
			{
				return 0;
			}

			foreach (var path in Directory.EnumerateFiles(
				         previewDirectory,
				         $"*{FileSuffix}",
				         SearchOption.TopDirectoryOnly))
			{
				if (removed >= MaximumFilesRemoved)
					break;
				if (!IsOwnedStaleFile(path, utcNow, minimumAge))
					continue;

				try
				{
					using var lease = new FileStream(
						path,
						FileMode.Open,
						FileAccess.ReadWrite,
						FileShare.None,
						bufferSize: 1,
						FileOptions.DeleteOnClose);
					removed++;
				}
				catch (Exception exception) when (
					exception is IOException or UnauthorizedAccessException)
				{
					// An active preview or another process owns this file.
				}
			}
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			// Cleanup is best effort and must not invalidate preview creation.
		}

		return removed;
	}

	private static bool IsOwnedStaleFile(string path, DateTime utcNow, TimeSpan minimumAge)
	{
		try
		{
			var fileName = Path.GetFileName(path);
			var stem = fileName[..^FileSuffix.Length];
			if (!Guid.TryParseExact(stem, "N", out var identifier) ||
			    !string.Equals(fileName, $"{identifier:N}{FileSuffix}", StringComparison.Ordinal) ||
			    (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
			    utcNow - File.GetLastWriteTimeUtc(path) < minimumAge)
			{
				return false;
			}

			if (!OperatingSystem.IsWindows())
			{
				var sharedPermissions = UnixFileMode.GroupRead |
				                        UnixFileMode.GroupWrite |
				                        UnixFileMode.GroupExecute |
				                        UnixFileMode.OtherRead |
				                        UnixFileMode.OtherWrite |
				                        UnixFileMode.OtherExecute;
				if ((File.GetUnixFileMode(path) & sharedPermissions) != 0)
					return false;
			}

			return true;
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
		{
			return false;
		}
	}
}

    private sealed class PreviewTextStorageBuilder : IDisposable
    {
        private const int Utf8WriteBufferSize = 16 * 1024;

        internal static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

        private string? _storagePath;
        private FileStream? _stream;
        private readonly List<long> _lineOffsets = [];
        private readonly int _inMemoryThresholdChars;
        private char[]? _inMemoryBuffer;
        private int _inMemoryLength;
        private Encoder? _utf8Encoder;
        private byte[]? _utf8WriteBuffer;
        private readonly ExportOutputMetricsCalculator.TextMetricsWriter _metricsWriter =
            ExportOutputMetricsCalculator.CreateTextWriter();
        private bool _built;
        private bool _disposed;
        private bool _lastLineIsEmpty;
        private char _characterBeforePreviousCharacter;
        private char _previousCharacter;
        private char _lastCharacter;
        private int _trimmedNormalizedLineFeeds;
        private int _maxLineLength;
        private long _characterCount;

        public PreviewTextStorageBuilder(int inMemoryThresholdChars)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(inMemoryThresholdChars);
            _inMemoryThresholdChars = inMemoryThresholdChars;
        }

        public void AppendLine(string line) => AppendLine(line.AsSpan());

        public void AppendLine(ReadOnlySpan<char> line)
        {
            ThrowIfDisposed();

			var resultingCharacterCount = checked(_characterCount + line.Length + 1);
			EnsureStorage(resultingCharacterCount);
			RecordLineStart();
            _lastLineIsEmpty = line.Length == 0;
            _maxLineLength = Math.Max(_maxLineLength, line.Length);
			_characterCount = resultingCharacterCount;
			_metricsWriter.Write(line);
			_metricsWriter.Write('\n');
			UpdateTrailingCharacters(line);
			UpdateTrailingCharacter('\n');

			AppendText(line);
			AppendNewLine();
        }

        public int LineCount => _lineOffsets.Count;

        public void AppendExactText(ReadOnlySpan<char> text)
        {
            ThrowIfDisposed();
            if (text.Length == 0)
                return;

            var lineStart = 0;
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] != '\n')
                    continue;

                AppendExactLine(text[lineStart..(index + 1)]);
                lineStart = index + 1;
            }

            if (lineStart < text.Length)
            {
                AppendExactLine(text[lineStart..]);
            }
            else
            {
				RecordLineStart();
                _lastLineIsEmpty = true;
            }
        }

        public void TrimTrailingEmptyLine()
        {
            ThrowIfDisposed();

            if (_lineOffsets.Count == 0 || !_lastLineIsEmpty)
                return;

            var trailingLineStart = _lineOffsets[^1];
            if (_stream is null)
            {
                _inMemoryLength = checked((int)trailingLineStart);
            }
            else
            {
                _stream.SetLength(trailingLineStart);
                _stream.Position = trailingLineStart;
            }
            _lineOffsets.RemoveAt(_lineOffsets.Count - 1);
            _lastLineIsEmpty = false;
            _characterCount = Math.Max(0, _characterCount - 1);
            if (_previousCharacter != '\r')
                _trimmedNormalizedLineFeeds++;
            _lastCharacter = _previousCharacter;
            _previousCharacter = _characterBeforePreviousCharacter;
        }

        public PreviewDocumentBuildResult BuildResult(
			IReadOnlyList<PreviewDocumentSection>? sections = null,
			IReadOnlyList<PreviewRedactionSpan>? redactions = null)
        {
            ThrowIfDisposed();

            if (_built)
                throw new InvalidOperationException("Preview document was already built.");

            _built = true;
            if (_characterCount <= _inMemoryThresholdChars)
            {
                string text;
                if (_stream is null)
                {
                    var outputLength = _inMemoryLength > 0 && _inMemoryBuffer![_inMemoryLength - 1] == '\n'
                        ? _inMemoryLength - 1
                        : _inMemoryLength;
                    text = outputLength == 0
                        ? string.Empty
                        : new string(_inMemoryBuffer!, 0, outputLength);
                }
                else
                {
                    _stream.Flush();
                    _stream.Dispose();
                    _stream = null;
                    ReleaseUtf8WriteBuffer();
                    text = File.ReadAllText(_storagePath!, Utf8WithoutBom);
                    if (text.Length > 0 && text[^1] == '\n')
                        text = text[..^1];
                    DisposeStorageFile();
                }

                ReleaseInMemoryBuffer();
                var removedNormalizedLineFeeds = _trimmedNormalizedLineFeeds;
                if (_lastCharacter == '\n' && _previousCharacter != '\r')
                    removedNormalizedLineFeeds++;
                var inMemoryMetrics = ExportOutputMetricsCalculator.TrimTrailingLineFeeds(
                    _metricsWriter.Complete(CancellationToken.None),
                    removedNormalizedLineFeeds);
                _metricsWriter.Dispose();
                _disposed = true;
                return new PreviewDocumentBuildResult(
                    new InMemoryPreviewTextDocument(text, sections, redactions),
                    inMemoryMetrics);
            }

            var backingStream = _stream ??
                throw new InvalidOperationException("Preview backing storage was not initialized.");
            backingStream.Flush();
            var fileLength = backingStream.Length;
            backingStream.Dispose();
            _stream = null;
            ReleaseUtf8WriteBuffer();
            var storagePath = _storagePath ??
                throw new InvalidOperationException("Preview backing storage was not initialized.");
            var document = new FileBackedPreviewTextDocument(
                storagePath,
                _lineOffsets.ToArray(),
                fileLength,
                _maxLineLength,
                _characterCount,
                sections,
                redactions);
            _storagePath = null;
            _disposed = true;
            var fileBackedMetrics = ExportOutputMetricsCalculator.TrimTrailingLineFeeds(
                _metricsWriter.Complete(CancellationToken.None),
                _trimmedNormalizedLineFeeds);
            _metricsWriter.Dispose();
            return new PreviewDocumentBuildResult(document, fileBackedMetrics);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _stream?.Dispose();
            _stream = null;
            ReleaseInMemoryBuffer();
            ReleaseUtf8WriteBuffer();
            _metricsWriter.Dispose();
            DisposeStorageFile();
        }

		private void EnsureStorage(long resultingCharacterCount)
		{
			if (_stream is not null || resultingCharacterCount <= _inMemoryThresholdChars)
				return;

			_storagePath = CreateStoragePath();
			_stream = OpenStorageFile(_storagePath, FileOptions.SequentialScan);
			_utf8Encoder = Utf8WithoutBom.GetEncoder();
			_utf8WriteBuffer = ArrayPool<byte>.Shared.Rent(Utf8WriteBufferSize);
			FlushInMemoryTextToStorage();
		}

        private void FlushInMemoryTextToStorage()
        {
            var buffer = _inMemoryBuffer;
            var previousCharacterOffset = 0;
            for (var lineIndex = 0; lineIndex < _lineOffsets.Count; lineIndex++)
            {
                var lineCharacterOffset = checked((int)_lineOffsets[lineIndex]);
                WriteUtf8(
                    buffer.AsSpan(previousCharacterOffset, lineCharacterOffset - previousCharacterOffset),
                    flush: false);
                _lineOffsets[lineIndex] = _stream!.Position;
                previousCharacterOffset = lineCharacterOffset;
            }

            WriteUtf8(buffer.AsSpan(previousCharacterOffset, _inMemoryLength - previousCharacterOffset), flush: false);
            WriteUtf8(ReadOnlySpan<char>.Empty, flush: true);
            ReleaseInMemoryBuffer();
        }

        private void RecordLineStart()
        {
            _lineOffsets.Add(_stream is null ? _inMemoryLength : _stream.Position);
        }

        private void AppendText(ReadOnlySpan<char> text)
        {
            if (_stream is null)
            {
                EnsureInMemoryCapacity(text.Length);
                text.CopyTo(_inMemoryBuffer.AsSpan(_inMemoryLength));
                _inMemoryLength += text.Length;
            }
            else
                WriteUtf8(text, flush: true);
        }

        private void AppendNewLine()
        {
            if (_stream is null)
            {
                EnsureInMemoryCapacity(1);
                _inMemoryBuffer![_inMemoryLength++] = '\n';
            }
            else
                _stream.WriteByte((byte)'\n');
        }

        private void EnsureInMemoryCapacity(int additionalCharacterCount)
        {
            if (additionalCharacterCount == 0)
                return;

            var requiredLength = checked(_inMemoryLength + additionalCharacterCount);
            if (_inMemoryBuffer is { Length: var currentLength } && currentLength >= requiredLength)
                return;

			var targetLength = Math.Max(Utf8WriteBufferSize, requiredLength);
			if (_inMemoryBuffer is { } existingBuffer)
				targetLength = Math.Max(targetLength, checked(existingBuffer.Length * 2));
			System.Diagnostics.Debug.Assert(requiredLength <= _inMemoryThresholdChars);
			targetLength = Math.Min(_inMemoryThresholdChars, targetLength);

            var replacement = ArrayPool<char>.Shared.Rent(targetLength);
            if (_inMemoryLength > 0)
                _inMemoryBuffer.AsSpan(0, _inMemoryLength).CopyTo(replacement);

            ReleaseInMemoryBuffer();
            _inMemoryBuffer = replacement;
        }

        private void ReleaseInMemoryBuffer()
        {
            var buffer = Interlocked.Exchange(ref _inMemoryBuffer, null);
            if (buffer is not null)
                ArrayPool<char>.Shared.Return(buffer, clearArray: true);
        }

		private void WriteUtf8(ReadOnlySpan<char> text, bool flush)
        {
			if (text.Length == 0 && !flush)
                return;

			var stream = _stream ??
				throw new InvalidOperationException("Preview backing storage was not initialized.");
			var encoder = _utf8Encoder ??
				throw new InvalidOperationException("Preview UTF-8 encoder was not initialized.");
			var buffer = _utf8WriteBuffer ??
                         throw new ObjectDisposedException(nameof(PreviewTextStorageBuilder));
			var remaining = text;
            bool completed;
            do
            {
				encoder.Convert(
                    remaining,
                    buffer.AsSpan(0, Utf8WriteBufferSize),
					flush,
                    out var charsUsed,
                    out var bytesUsed,
                    out completed);
				if (charsUsed == 0 && bytesUsed == 0 && !completed)
					throw new IOException("The preview UTF-8 encoder did not make progress.");
				stream.Write(buffer, 0, bytesUsed);
                remaining = remaining[charsUsed..];
            }
            while (!completed);
        }

        private void ReleaseUtf8WriteBuffer()
        {
            var buffer = Interlocked.Exchange(ref _utf8WriteBuffer, null);
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        private void AppendExactLine(ReadOnlySpan<char> rawLine)
        {
			var resultingCharacterCount = checked(_characterCount + rawLine.Length);
			EnsureStorage(resultingCharacterCount);
			RecordLineStart();
            var displayLength = rawLine.Length;
            if (displayLength > 0 && rawLine[displayLength - 1] == '\n')
                displayLength--;
            if (displayLength > 0 && rawLine[displayLength - 1] == '\r')
                displayLength--;
            _lastLineIsEmpty = displayLength == 0;
            _maxLineLength = Math.Max(_maxLineLength, displayLength);
			_characterCount = resultingCharacterCount;
			_metricsWriter.Write(rawLine);
			UpdateTrailingCharacters(rawLine);
			AppendText(rawLine);
        }

		private void UpdateTrailingCharacters(ReadOnlySpan<char> text)
		{
			if (text.Length == 0)
				return;

			if (text.Length == 1)
			{
				_characterBeforePreviousCharacter = _previousCharacter;
				_previousCharacter = _lastCharacter;
				_lastCharacter = text[0];
				return;
			}

			_characterBeforePreviousCharacter = text.Length > 2 ? text[^3] : _lastCharacter;
			_previousCharacter = text[^2];
			_lastCharacter = text[^1];
		}

		private void UpdateTrailingCharacter(char character)
		{
			_characterBeforePreviousCharacter = _previousCharacter;
			_previousCharacter = _lastCharacter;
			_lastCharacter = character;
		}

        private void DisposeStorageFile()
        {
			if (_storagePath is not null)
			{
				PreviewDocumentBuilder.DisposeStorageFile(_storagePath);
				_storagePath = null;
			}
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PreviewTextStorageBuilder));
        }
    }
}
