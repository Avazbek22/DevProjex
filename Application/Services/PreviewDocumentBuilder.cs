using System.Buffers;
using System.Runtime.CompilerServices;
using DevProjex.Application.Secrets;

namespace DevProjex.Application.Services;

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

    public IPreviewTextDocument CreateInMemory(string? text, IReadOnlyList<PreviewDocumentSection>? sections = null)
        => new InMemoryPreviewTextDocument(text, sections);

    public IPreviewTextDocument CreateDocument(
        string? text,
        IReadOnlyList<PreviewDocumentSection>? sections = null)
    {
        var value = text ?? string.Empty;
        if (value.Length <= InMemoryDocumentThresholdChars)
            return CreateInMemory(value, sections);

        using var builder = new PreviewTextStorageBuilder(InMemoryDocumentThresholdChars);
        builder.AppendExactText(value.AsSpan());
        return builder.BuildDocument(sections);
    }

    public async Task<IPreviewTextDocument> CreateDocumentAsync(
        Func<Stream, CancellationToken, Task> writeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeAsync);

		var storagePath = CreateStoragePath();
		try
		{
			await using (var stream = OpenStorageFile(
			                 storagePath,
			                 FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await writeAsync(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            return BuildDocumentFromUtf8File(storagePath);
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
		bool includeSourceCoordinateMaps = false)
    {
        var orderedFiles = BuildOrderedUniqueFiles(filePaths);
        if (orderedFiles.Count == 0)
        {
			transformationContext?.Redaction?.BeginOutput(orderedFiles).Complete();
            return null;
		}

        using var builder = new PreviewTextStorageBuilder(InMemoryDocumentThresholdChars);
        var sections = new List<PreviewDocumentSection>(orderedFiles.Count);
		var redactions = new List<PreviewRedactionSpan>();
		using var transformationScope = transformationContext?.BeginOutput(orderedFiles);
		var redactionScope = transformationScope?.Redaction;
        var anyWritten = await AppendContentEntriesAsync(
            builder,
            orderedFiles,
            sections,
            displayPathMapper,
            prependSectionSeparator: false,
            includeOmissionMarkers,
			redactionScope,
			transformationScope,
			redactions,
			includeSourceCoordinateMaps,
            cancellationToken).ConfigureAwait(false);

		redactionScope?.Complete();
		transformationScope?.Compression?.Complete();
		if (!anyWritten)
			return null;

		return builder.BuildDocument(sections, redactions);
    }

    public async Task<IPreviewTextDocument> BuildTreeAndContentDocumentAsync(
        string treeText,
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken,
        Func<string, string>? displayPathMapper,
        bool includeOmissionMarkers = false,
		ContentTransformationContext? transformationContext = null,
		bool includeSourceCoordinateMaps = false)
    {
        var orderedFiles = BuildOrderedUniqueFiles(filePaths);
        var normalizedTreeText = treeText.TrimEnd('\r', '\n');

        if (orderedFiles.Count == 0)
        {
			transformationContext?.Redaction?.BeginOutput(orderedFiles).Complete();
            return CreateInMemory(normalizedTreeText);
		}

        using var builder = new PreviewTextStorageBuilder(InMemoryDocumentThresholdChars);
        var sections = new List<PreviewDocumentSection>(orderedFiles.Count);
		var redactions = new List<PreviewRedactionSpan>();
		using var transformationScope = transformationContext?.BeginOutput(orderedFiles);
		var redactionScope = transformationScope?.Redaction;
        var wroteTree = AppendMultilineText(builder, normalizedTreeText.AsSpan());
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
            cancellationToken).ConfigureAwait(false);
		redactionScope?.Complete();
		transformationScope?.Compression?.Complete();

        if (!wroteTree && !wroteContent)
            return CreateInMemory(string.Empty);

        return builder.BuildDocument(sections, redactions);
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
        CancellationToken cancellationToken)
    {
        var anyWritten = false;
        var trimTrailingEstimatedLine = false;

        await foreach (var prepared in PrepareContentEntriesAsync(
                           orderedFiles,
                           displayPathMapper,
                           redactionScope,
                           transformationScope,
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

            var displayPath = prepared.DisplayPath;
            var sectionStartLine = builder.LineCount + 1;
            builder.AppendLine($"{displayPath}:");
            builder.AppendLine(ClipboardBlankLine);

            if (!readResult.IsText || content is null)
            {
                builder.AppendLine(GetOmissionMarker(readResult.Classification));
                sections.Add(new PreviewDocumentSection(
                    displayPath,
                    sectionStartLine,
                    builder.LineCount,
                    sectionStartLine,
                    sectionStartLine + 2));
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
                    sectionStartLine + 2));
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
                    sectionStartLine + 2));
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
                    sectionStartLine + 2));
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
				coordinateMap));
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
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (transformationScope?.Compression is null)
        {
            foreach (var file in orderedFiles)
            {
                yield return await PrepareContentEntryAsync(
                        file,
                        displayPathMapper,
                        redactionScope,
                        transformationScope,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            yield break;
        }

        var batch = new List<string>(MaximumParallelPreparations);
        foreach (var file in orderedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSmallFile(file))
            {
                await foreach (var entry in PrepareParallelBatchAsync(
                                   batch,
                                   displayPathMapper,
                                   redactionScope,
                                   transformationScope,
                                   cancellationToken).ConfigureAwait(false))
                {
                    yield return entry;
                }

                batch.Clear();
                yield return await PrepareContentEntryAsync(
                        file,
                        displayPathMapper,
                        redactionScope,
                        transformationScope,
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            batch.Add(file);
            if (batch.Count < MaximumParallelPreparations)
                continue;

            await foreach (var entry in PrepareParallelBatchAsync(
                               batch,
                               displayPathMapper,
                               redactionScope,
                               transformationScope,
                               cancellationToken).ConfigureAwait(false))
            {
                yield return entry;
            }

            batch.Clear();
        }

        await foreach (var entry in PrepareParallelBatchAsync(
                           batch,
                           displayPathMapper,
                           redactionScope,
                           transformationScope,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return entry;
        }
    }

    private async IAsyncEnumerable<PreparedContentEntry> PrepareParallelBatchAsync(
        IReadOnlyList<string> files,
        Func<string, string>? displayPathMapper,
        SecretRedactionScope? redactionScope,
        ContentTransformationScope transformationScope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (files.Count == 0)
            yield break;

        var tasks = new Task<PreparedContentEntry>[files.Count];
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            tasks[index] = Task.Run(
                () => PrepareContentEntryAsync(
                    file,
                    displayPathMapper,
                    redactionScope,
                    transformationScope,
                    cancellationToken).AsTask(),
                cancellationToken);
        }

        var entries = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var entry in entries)
            yield return entry;
    }

    private async ValueTask<PreparedContentEntry> PrepareContentEntryAsync(
        string file,
        Func<string, string>? displayPathMapper,
        SecretRedactionScope? redactionScope,
        ContentTransformationScope? transformationScope,
        CancellationToken cancellationToken)
    {
        var maximumFileBytes = redactionScope is null
            ? MaximumInteractiveFileBytes
            : SecretRedactionOutputPreparer.MaximumScannableFileBytes;
		var readFact = await contentAnalyzer
			.ReadFactAsync(file, maximumFileBytes, cancellationToken)
			.ConfigureAwait(false);
		var readResult = readFact.ToReadResult();
        var displayPath = MapDisplayPath(file, displayPathMapper);
        var content = readResult.Content;
		var compression = readResult.IsText &&
		                  content is { IsEstimated: false, IsEmpty: false, IsWhitespaceOnly: false }
			? readFact.Fingerprint is { } fingerprint
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
		foreach (var span in result.Spans.OrderBy(static span => span.Start))
		{
			AdvancePosition(result.Text.AsSpan(sourcePosition, span.Start - sourcePosition), ref line, ref column);
			var spanText = result.Text.AsSpan(span.Start, span.Length);
			var segmentStart = 0;
			for (var index = 0; index <= spanText.Length; index++)
			{
				if (index < spanText.Length && spanText[index] != '\n')
					continue;

				var segmentLength = index - segmentStart;
				if (segmentLength > 0 && spanText[index - 1] == '\r')
					segmentLength--;
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
						span.SessionMarkId));
				}

				if (index < spanText.Length)
				{
					line++;
					column = 0;
					segmentStart = index + 1;
				}
				else
				{
					column += segmentLength;
				}
			}
			sourcePosition = span.Start + span.Length;
		}
	}

	private static void AdvancePosition(ReadOnlySpan<char> text, ref int line, ref int column)
	{
		foreach (var character in text)
		{
			if (character == '\n')
			{
				line++;
				column = 0;
			}
			else if (character != '\r')
			{
				column++;
			}
		}
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

    private static List<string> BuildOrderedUniqueFiles(IEnumerable<string> filePaths)
    {
        var uniqueFiles = new HashSet<string>(PathComparer.Default);
        foreach (var path in filePaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
                uniqueFiles.Add(path);
        }

        var files = new List<string>(uniqueFiles.Count);
        files.AddRange(uniqueFiles);
        files.Sort(PathComparer.Default);
        return files;
    }

    private static bool AppendMultilineText(PreviewTextStorageBuilder builder, ReadOnlySpan<char> text)
    {
        if (text.Length == 0)
            return false;

        var wroteAnyLine = false;
        var lineStart = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
                continue;

            var line = text.Slice(lineStart, i - lineStart);
            if (line.Length > 0 && line[^1] == '\r')
                line = line[..^1];

            builder.AppendLine(line);
            wroteAnyLine = true;
            lineStart = i + 1;
        }

        if (lineStart < text.Length)
        {
            var line = text[lineStart..];
            if (line.Length > 0 && line[^1] == '\r')
                line = line[..^1];

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

    private static IPreviewTextDocument BuildDocumentFromUtf8File(string storagePath)
    {
        var lineOffsets = BuildLineOffsets(storagePath);
        var (characterCount, maxLineLength) = ReadTextMetrics(storagePath);
        var fileLength = new FileInfo(storagePath).Length;
        if (characterCount <= InMemoryDocumentThresholdChars)
        {
            var text = File.ReadAllText(storagePath, PreviewTextStorageBuilder.Utf8WithoutBom);
            DisposeStorageFile(storagePath);
            return new InMemoryPreviewTextDocument(text);
        }

        return new FileBackedPreviewTextDocument(
            storagePath,
            lineOffsets,
            fileLength,
            maxLineLength,
            characterCount);
    }

    private static long[] BuildLineOffsets(string storagePath)
    {
        using var stream = new FileStream(
            storagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 8192,
            options: FileOptions.SequentialScan);
        if (stream.Length == 0)
            return [];

        var offsets = new List<long> { 0 };
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            long absoluteOffset = 0;
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var index = 0; index < bytesRead; index++)
                {
                    if (buffer[index] == (byte)'\n')
                        offsets.Add(absoluteOffset + index + 1);
                }

                absoluteOffset += bytesRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return offsets.ToArray();
    }

    private static (long CharacterCount, int MaxLineLength) ReadTextMetrics(string storagePath)
    {
        using var stream = new FileStream(
            storagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 8192,
            options: FileOptions.SequentialScan);
        using var reader = new StreamReader(
            stream,
            PreviewTextStorageBuilder.Utf8WithoutBom,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 8192);

        var buffer = ArrayPool<char>.Shared.Rent(8192);
        try
        {
            long characterCount = 0;
            var currentLineLength = 0;
            var maxLineLength = 0;
            int charactersRead;
            while ((charactersRead = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                characterCount += charactersRead;
                foreach (var character in buffer.AsSpan(0, charactersRead))
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
            }

            return (characterCount, Math.Max(maxLineLength, currentLineLength));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

	private static string CreateStoragePath()
	{
		var previewDirectory = Path.Combine(Path.GetTempPath(), "DevProjex", "Preview");
		Directory.CreateDirectory(previewDirectory);
		return Path.Combine(previewDirectory, $"{Guid.NewGuid():N}.preview.txt");
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

    private sealed class PreviewTextStorageBuilder : IDisposable
    {
        internal static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

        private readonly string _storagePath;
        private readonly FileStream _stream;
        private readonly List<long> _lineOffsets = [];
        private readonly List<int> _lineLengths = [];
        private readonly int _inMemoryThresholdChars;
        private bool _built;
        private bool _disposed;
        private int _maxLineLength;
        private long _characterCount;

		public PreviewTextStorageBuilder(int inMemoryThresholdChars)
		{
			_inMemoryThresholdChars = inMemoryThresholdChars;
			_storagePath = CreateStoragePath();
			_stream = OpenStorageFile(_storagePath, FileOptions.SequentialScan);
		}

        public void AppendLine(string line) => AppendLine(line.AsSpan());

        public void AppendLine(ReadOnlySpan<char> line)
        {
            ThrowIfDisposed();

            _lineOffsets.Add(_stream.Position);
            _lineLengths.Add(line.Length);
            _maxLineLength = Math.Max(_maxLineLength, line.Length);
            _characterCount += line.Length + 1;

            WriteUtf8(line);
            _stream.WriteByte((byte)'\n');
        }

        public int LineCount => _lineLengths.Count;

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
                _lineOffsets.Add(_stream.Position);
                _lineLengths.Add(0);
            }
        }

        public void TrimTrailingEmptyLine()
        {
            ThrowIfDisposed();

            if (_lineLengths.Count == 0 || _lineLengths[^1] != 0)
                return;

            var trailingLineStart = _lineOffsets[^1];
            _stream.SetLength(trailingLineStart);
            _stream.Position = trailingLineStart;
            _lineOffsets.RemoveAt(_lineOffsets.Count - 1);
            _lineLengths.RemoveAt(_lineLengths.Count - 1);
            _characterCount = Math.Max(0, _characterCount - 1);
        }

        public void AppendFrom(PreviewTextStorageBuilder source)
		{
			ThrowIfDisposed();
			ArgumentNullException.ThrowIfNull(source);
			source.ThrowIfDisposed();
			if (ReferenceEquals(this, source))
				throw new ArgumentException("A preview builder cannot append itself.", nameof(source));

			source._stream.Flush();
			var destinationStart = _stream.Position;
			source._stream.Position = 0;
			source._stream.CopyTo(_stream);
			foreach (var offset in source._lineOffsets)
				_lineOffsets.Add(destinationStart + offset);
			_lineLengths.AddRange(source._lineLengths);
			_maxLineLength = Math.Max(_maxLineLength, source._maxLineLength);
			_characterCount += source._characterCount;
		}

        public IPreviewTextDocument BuildDocument(
			IReadOnlyList<PreviewDocumentSection>? sections = null,
			IReadOnlyList<PreviewRedactionSpan>? redactions = null)
        {
            ThrowIfDisposed();

            if (_built)
                throw new InvalidOperationException("Preview document was already built.");

            _built = true;
            _stream.Flush();

            var fileLength = _stream.Length;
            _stream.Dispose();

            if (_characterCount <= _inMemoryThresholdChars)
            {
                var text = File.Exists(_storagePath)
                    ? File.ReadAllText(_storagePath, Utf8WithoutBom)
                    : string.Empty;

                if (text.Length > 0 && text[^1] == '\n')
                    text = text[..^1];

                DisposeStorageFile();
                _disposed = true;
				return new InMemoryPreviewTextDocument(text, sections, redactions);
            }

            _disposed = true;
            return new FileBackedPreviewTextDocument(
                _storagePath,
                _lineOffsets.ToArray(),
                fileLength,
                _maxLineLength,
                _characterCount,
                sections,
				redactions);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _stream.Dispose();
            DisposeStorageFile();
        }

        private void WriteUtf8(ReadOnlySpan<char> line)
        {
            if (line.Length == 0)
                return;

            var maxByteCount = Utf8WithoutBom.GetMaxByteCount(line.Length);
            var rentedBuffer = ArrayPool<byte>.Shared.Rent(maxByteCount);
            try
            {
                var bytesWritten = Utf8WithoutBom.GetBytes(line, rentedBuffer);
                _stream.Write(rentedBuffer, 0, bytesWritten);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }

        private void AppendExactLine(ReadOnlySpan<char> rawLine)
        {
            _lineOffsets.Add(_stream.Position);
            var displayLength = rawLine.Length;
            if (displayLength > 0 && rawLine[displayLength - 1] == '\n')
                displayLength--;
            if (displayLength > 0 && rawLine[displayLength - 1] == '\r')
                displayLength--;
            _lineLengths.Add(displayLength);
            _maxLineLength = Math.Max(_maxLineLength, displayLength);
            _characterCount += rawLine.Length;
            WriteUtf8(rawLine);
        }

        private void DisposeStorageFile()
        {
            PreviewDocumentBuilder.DisposeStorageFile(_storagePath);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PreviewTextStorageBuilder));
        }
    }
}
