using System.Buffers;

namespace DevProjex.Application.Services;

/// <summary>
/// Builds preview documents with bounded memory usage.
/// Preview keeps the same visible output as export, but stores large payloads in a temporary
/// UTF-8 backing file instead of a single giant managed string.
/// </summary>
public sealed class PreviewDocumentBuilder(IFileContentAnalyzer contentAnalyzer)
{
    private const string ClipboardBlankLine = "\u00A0";
    private const string NoContentMarker = "[No Content, 0 bytes]";
    private const string WhitespaceMarkerPrefix = "[Whitespace, ";
    private const string WhitespaceMarkerSuffix = " bytes]";
    private const int InMemoryDocumentThresholdChars = 500_000;

    public IPreviewTextDocument CreateInMemory(string? text, IReadOnlyList<PreviewDocumentSection>? sections = null)
        => new InMemoryPreviewTextDocument(text, sections);

    public async Task<IPreviewTextDocument?> BuildContentDocumentAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken,
        Func<string, string>? displayPathMapper)
    {
        var orderedFiles = BuildOrderedUniqueFiles(filePaths);
        if (orderedFiles.Count == 0)
            return null;

        using var builder = new PreviewTextStorageBuilder(InMemoryDocumentThresholdChars);
        var sections = new List<PreviewDocumentSection>(orderedFiles.Count);
        var anyWritten = await AppendContentEntriesAsync(
            builder,
            orderedFiles,
            sections,
            displayPathMapper,
            prependSectionSeparator: false,
            cancellationToken).ConfigureAwait(false);

        return anyWritten ? builder.BuildDocument(sections) : null;
    }

    public async Task<IPreviewTextDocument> BuildTreeAndContentDocumentAsync(
        string treeText,
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken,
        Func<string, string>? displayPathMapper)
    {
        var orderedFiles = BuildOrderedUniqueFiles(filePaths);
        var normalizedTreeText = treeText.TrimEnd('\r', '\n');

        if (orderedFiles.Count == 0)
            return CreateInMemory(normalizedTreeText);

        using var builder = new PreviewTextStorageBuilder(InMemoryDocumentThresholdChars);
        var sections = new List<PreviewDocumentSection>(orderedFiles.Count);
        var wroteTree = AppendMultilineText(builder, normalizedTreeText.AsSpan());
        var wroteContent = await AppendContentEntriesAsync(
            builder,
            orderedFiles,
            sections,
            displayPathMapper,
            prependSectionSeparator: wroteTree,
            cancellationToken).ConfigureAwait(false);

        if (!wroteTree && !wroteContent)
            return CreateInMemory(string.Empty);

        return builder.BuildDocument(sections);
    }

    private async Task<bool> AppendContentEntriesAsync(
        PreviewTextStorageBuilder builder,
        IReadOnlyList<string> orderedFiles,
        ICollection<PreviewDocumentSection> sections,
        Func<string, string>? displayPathMapper,
        bool prependSectionSeparator,
        CancellationToken cancellationToken)
    {
        var anyWritten = false;
        var trimTrailingEstimatedLine = false;

        foreach (var file in orderedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var content = await contentAnalyzer.TryReadAsTextAsync(file, cancellationToken).ConfigureAwait(false);
            if (content is null)
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

            var displayPath = MapDisplayPath(file, displayPathMapper);
            var sectionStartLine = builder.LineCount + 1;
            builder.AppendLine($"{displayPath}:");
            builder.AppendLine(ClipboardBlankLine);

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
                // Export writes an empty content line for estimated files. We mirror that behavior
                // and trim the terminal empty line only for the final entry to keep visible output stable.
                builder.AppendLine(string.Empty);
                trimTrailingEstimatedLine = true;
                sections.Add(new PreviewDocumentSection(
                    displayPath,
                    sectionStartLine,
                    builder.LineCount,
                    sectionStartLine,
                    sectionStartLine + 2));
                continue;
            }

            trimTrailingEstimatedLine = false;
            AppendTrimmedContent(builder, content.Content.AsSpan());
            sections.Add(new PreviewDocumentSection(
                displayPath,
                sectionStartLine,
                builder.LineCount,
                sectionStartLine,
                sectionStartLine + 2));
        }

        if (anyWritten && trimTrailingEstimatedLine)
            builder.TrimTrailingEmptyLine();

        return anyWritten;
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

    private sealed class PreviewTextStorageBuilder : IDisposable
    {
        private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

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
            var previewDirectory = Path.Combine(Path.GetTempPath(), "DevProjex", "Preview");
            Directory.CreateDirectory(previewDirectory);

            _storagePath = Path.Combine(previewDirectory, $"{Guid.NewGuid():N}.preview.txt");
            _stream = new FileStream(
                _storagePath,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 8192,
                options: FileOptions.SequentialScan);
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

        public IPreviewTextDocument BuildDocument(IReadOnlyList<PreviewDocumentSection>? sections = null)
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
                return new InMemoryPreviewTextDocument(text, sections);
            }

            _disposed = true;
            return new FileBackedPreviewTextDocument(
                _storagePath,
                _lineOffsets.ToArray(),
                fileLength,
                _maxLineLength,
                _characterCount,
                sections);
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

        private void DisposeStorageFile()
        {
            try
            {
                if (File.Exists(_storagePath))
                    File.Delete(_storagePath);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PreviewTextStorageBuilder));
        }
    }
}
