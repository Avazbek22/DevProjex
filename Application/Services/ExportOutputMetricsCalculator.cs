using System.Text;
using DevProjex.Application.Preview;

namespace DevProjex.Application.Services;

public static class ExportOutputMetricsCalculator
{
	private const string ClipboardBlankLine = "\u00A0";
	private const string NoContentMarker = "[No Content, 0 bytes]";
	private const string WhitespaceMarkerPrefix = "[Whitespace, ";
	private const string WhitespaceMarkerSuffix = " bytes]";
	private static readonly System.Buffers.SearchValues<char> LineBreakCharacters =
		System.Buffers.SearchValues.Create("\r\n");

	public static ExportOutputMetrics FromText(string text)
	{
		if (string.IsNullOrEmpty(text))
			return ExportOutputMetrics.Empty;

		var stats = GetNormalizedTextStats(text.AsSpan());
		long chars = stats.NormalizedChars;
		long lines = stats.LineBreaks + 1L;
		long tokens = EstimateTokens(chars);

		return new ExportOutputMetrics(lines, chars, tokens);
	}

	public static async Task<ExportOutputMetrics> FromDocumentAsync(
		IPreviewTextDocument document,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		return await FromUtf8WriterAsync(
				(stream, token) => document.WriteToAsync(stream, token).AsTask(),
				cancellationToken)
			.ConfigureAwait(false);
	}

	public static async Task<ExportOutputMetrics> FromUtf8WriterAsync(
		Func<Stream, CancellationToken, Task> writeAsync,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(writeAsync);
		using var metricsStream = new Utf8MetricsStream();
		await writeAsync(metricsStream, cancellationToken).ConfigureAwait(false);
		return metricsStream.Complete(cancellationToken);
	}

	internal static TextMetricsWriter CreateTextWriter() => new();

	internal static ExportOutputMetrics TrimTrailingLineFeeds(
		ExportOutputMetrics metrics,
		int count)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(count);
		if (count == 0 || metrics == ExportOutputMetrics.Empty)
			return metrics;

		var chars = Math.Max(0, metrics.Chars - count);
		if (chars == 0)
			return ExportOutputMetrics.Empty;
		return new ExportOutputMetrics(
			Math.Max(1, metrics.Lines - count),
			chars,
			EstimateTokens(chars));
	}

	public static ExportOutputMetrics FromContentFiles(IEnumerable<ContentFileMetrics> files)
	{
		var uniquePaths = new HashSet<string>(PathComparer.Default);
		var ordered = new List<ContentFileMetrics>();
		foreach (var file in files)
		{
			if (string.IsNullOrEmpty(file.Path))
				continue;

			if (!uniquePaths.Add(file.Path))
				continue;

			ordered.Add(file);
		}

		if (ordered.Count == 0)
			return ExportOutputMetrics.Empty;

		ordered.Sort(static (left, right) => PathComparer.Default.Compare(left.Path, right.Path));
		return FromOrderedContentFiles(ordered);
	}

	/// <summary>
	/// Calculates content metrics for an already de-duplicated, path-ordered sequence.
	/// This avoids extra HashSet/List allocations on hot status-bar recalculations.
	/// </summary>
	public static ExportOutputMetrics FromOrderedContentFiles(IReadOnlyList<ContentFileMetrics> ordered)
	{
		if (ordered.Count == 0)
			return ExportOutputMetrics.Empty;

		var accumulator = new OrderedContentMetricsAccumulator();
		foreach (var file in ordered)
			accumulator.AppendFile(file);

		return accumulator.ToMetrics();
	}

	/// <summary>
	/// Accumulates clipboard-style content metrics without forcing callers to build
	/// a temporary <see cref="List{T}"/> for large status-bar recalculations.
	/// </summary>
	public struct OrderedContentMetricsAccumulator
	{
		private const int NormalizedNewLineChars = 1;

		// Aggregate values can legitimately exceed Int32 for workspace-sized previews.
		// Keep the per-file snapshot compact, but never narrow the combined output metrics.
		private long _chars;
		private long _lineBreaks;
		private long _trailingLineBreakChars;
		private long _trailingLineBreaks;
		private bool _hasRootHeader;
		private bool _anyFileWritten;

		public void AppendRootHeader(string displayRootPath)
		{
			if (_hasRootHeader || _anyFileWritten || string.IsNullOrWhiteSpace(displayRootPath))
				return;

			AppendRenderedLine(
				renderedChars: ContextRootPresentation.FormatLine(displayRootPath).Length,
				internalLineBreaks: 0,
				newLineChars: NormalizedNewLineChars,
				chars: ref _chars,
				lineBreaks: ref _lineBreaks,
				trailingLineBreakChars: ref _trailingLineBreakChars,
				trailingLineBreaks: ref _trailingLineBreaks);
			_hasRootHeader = true;
		}

		public void AppendFile(ContentFileMetrics file)
		{
			if (string.IsNullOrEmpty(file.Path))
				return;

			if (_hasRootHeader || _anyFileWritten)
			{
				AppendLiteralLine(
					ClipboardBlankLine,
					NormalizedNewLineChars,
					ref _chars,
					ref _lineBreaks,
					ref _trailingLineBreakChars,
					ref _trailingLineBreaks);
				AppendLiteralLine(
					ClipboardBlankLine,
					NormalizedNewLineChars,
					ref _chars,
					ref _lineBreaks,
					ref _trailingLineBreakChars,
					ref _trailingLineBreaks);
			}

			_anyFileWritten = true;

			// Metrics need the rendered header length, not a materialized "<path>:"
			// string. Avoiding that allocation matters when thousands of files are
			// recalculated after a selection or format change.
			AppendRenderedLine(
				renderedChars: SingleLineTextEscaping.GetEscapedLength(file.Path.AsSpan()) + 1L,
				internalLineBreaks: 0,
				newLineChars: NormalizedNewLineChars,
				chars: ref _chars,
				lineBreaks: ref _lineBreaks,
				trailingLineBreakChars: ref _trailingLineBreakChars,
				trailingLineBreaks: ref _trailingLineBreaks);
			AppendLiteralLine(
				ClipboardBlankLine,
				NormalizedNewLineChars,
				ref _chars,
				ref _lineBreaks,
				ref _trailingLineBreakChars,
				ref _trailingLineBreaks);

			if (file.IsEmpty)
			{
				AppendLiteralLine(
					NoContentMarker,
					NormalizedNewLineChars,
					ref _chars,
					ref _lineBreaks,
					ref _trailingLineBreakChars,
					ref _trailingLineBreaks);
				return;
			}

			if (file.IsWhitespaceOnly)
			{
				AppendLiteralLine(
					$"{WhitespaceMarkerPrefix}{file.SizeBytes}{WhitespaceMarkerSuffix}",
					NormalizedNewLineChars,
					ref _chars,
					ref _lineBreaks,
					ref _trailingLineBreakChars,
					ref _trailingLineBreaks);
				return;
			}

			if (file.IsEstimated)
			{
				// Estimated files intentionally keep an empty content line in the rendered export.
				AppendRenderedLine(
					renderedChars: 0,
					internalLineBreaks: 0,
					newLineChars: NormalizedNewLineChars,
					chars: ref _chars,
					lineBreaks: ref _lineBreaks,
					trailingLineBreakChars: ref _trailingLineBreakChars,
					trailingLineBreaks: ref _trailingLineBreaks);
				return;
			}

			var internalLineBreaks = Math.Max(0, file.LineCount - 1);
			var trimmedLineBreaks = Math.Max(0, internalLineBreaks - file.TrailingNewlineLineBreaks);
			var normalizedChars = Math.Max(0, file.CharCount - file.CrLfPairCount);
			var trimmedChars = Math.Max(0, normalizedChars - file.TrailingNewlineLineBreaks);

			AppendRenderedLine(
				renderedChars: trimmedChars,
				internalLineBreaks: trimmedLineBreaks,
				newLineChars: NormalizedNewLineChars,
				chars: ref _chars,
				lineBreaks: ref _lineBreaks,
				trailingLineBreakChars: ref _trailingLineBreakChars,
				trailingLineBreaks: ref _trailingLineBreaks);
		}

		public ExportOutputMetrics ToMetrics()
		{
			var chars = Math.Max(0, _chars - _trailingLineBreakChars);
			var lineBreaks = Math.Max(0, _lineBreaks - _trailingLineBreaks);

			if (chars == 0)
				return ExportOutputMetrics.Empty;

			var lines = lineBreaks + 1;
			var tokens = EstimateTokens(chars);
			return new ExportOutputMetrics(lines, chars, tokens);
		}
	}

	private static void AppendLiteralLine(
		string text,
		int newLineChars,
		ref long chars,
		ref long lineBreaks,
		ref long trailingLineBreakChars,
		ref long trailingLineBreaks)
	{
		AppendRenderedLine(
			renderedChars: text.Length,
			internalLineBreaks: 0,
			newLineChars: newLineChars,
			chars: ref chars,
			lineBreaks: ref lineBreaks,
			trailingLineBreakChars: ref trailingLineBreakChars,
			trailingLineBreaks: ref trailingLineBreaks);
	}

	private static void AppendRenderedLine(
		long renderedChars,
		long internalLineBreaks,
		int newLineChars,
		ref long chars,
		ref long lineBreaks,
		ref long trailingLineBreakChars,
		ref long trailingLineBreaks)
	{
		chars += renderedChars + newLineChars;
		lineBreaks += internalLineBreaks + 1;

		if (renderedChars == 0 && internalLineBreaks == 0)
		{
			trailingLineBreakChars += newLineChars;
			trailingLineBreaks++;
			return;
		}

		trailingLineBreakChars = newLineChars;
		trailingLineBreaks = 1;
	}

	private static long EstimateTokens(long chars) =>
		chars <= 0 ? 0 : (chars / 4) + (chars % 4 == 0 ? 0 : 1);

	private static NormalizedTextStats GetNormalizedTextStats(ReadOnlySpan<char> text)
	{
		var normalizedChars = 0;
		var lineBreaks = 0;
		var index = 0;

		// Scan non-line-break segments in bulk to reduce per-char branching on hot paths.
		while (index < text.Length)
		{
			var remaining = text[index..];
			var breakOffset = remaining.IndexOfAny(LineBreakCharacters);
			if (breakOffset < 0)
			{
				normalizedChars += remaining.Length;
				break;
			}

			normalizedChars += breakOffset + 1;
			index += breakOffset;

			if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
			{
				// CRLF contributes a single normalized line-break character.
				index++;
			}

			lineBreaks++;
			index++;
		}

		return new NormalizedTextStats(normalizedChars, lineBreaks);
	}

	private readonly record struct NormalizedTextStats(int NormalizedChars, int LineBreaks);

	internal sealed class TextMetricsWriter : TextWriter
	{
		private NormalizedTextMetricsAccumulator _metrics;
		private bool _completed;

		public override Encoding Encoding => Encoding.Unicode;

		public ExportOutputMetrics Complete(CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!_completed)
			{
				_completed = true;
				return _metrics.Complete();
			}

			return _metrics.ToMetrics();
		}

		public override void Write(char value)
		{
			ThrowIfCompleted();
			_metrics.Append(value);
		}

		public override void Write(string? value)
		{
			if (value is null)
				return;

			Write(value.AsSpan());
		}

		public override void Write(char[] buffer, int index, int count)
		{
			ArgumentNullException.ThrowIfNull(buffer);
			ArgumentOutOfRangeException.ThrowIfNegative(index);
			ArgumentOutOfRangeException.ThrowIfNegative(count);
			if (index > buffer.Length - count)
				throw new ArgumentException("The write range exceeds the source buffer.", nameof(count));

			Write(buffer.AsSpan(index, count));
		}

		public override void Write(ReadOnlySpan<char> buffer)
		{
			ThrowIfCompleted();
			_metrics.Append(buffer);
		}

		public override Task WriteAsync(char value)
		{
			Write(value);
			return Task.CompletedTask;
		}

		public override Task WriteAsync(string? value)
		{
			Write(value);
			return Task.CompletedTask;
		}

		public override Task WriteAsync(char[] buffer, int index, int count)
		{
			Write(buffer, index, count);
			return Task.CompletedTask;
		}

		public override Task WriteAsync(
			ReadOnlyMemory<char> buffer,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Write(buffer.Span);
			return Task.CompletedTask;
		}

		public override Task FlushAsync()
		{
			ThrowIfCompleted();
			return Task.CompletedTask;
		}

		private void ThrowIfCompleted()
		{
			if (_completed)
				throw new InvalidOperationException("Output metrics were already finalized.");
		}
	}

	private sealed class Utf8MetricsStream : Stream
	{
		private const int CharacterBufferSize = 4 * 1024;
		private static readonly UTF8Encoding StrictUtf8 = new(
			encoderShouldEmitUTF8Identifier: false,
			throwOnInvalidBytes: true);
		private readonly Decoder _decoder = StrictUtf8.GetDecoder();
		private readonly char[] _characterBuffer = new char[CharacterBufferSize];
		private NormalizedTextMetricsAccumulator _metrics;
		private bool _completed;

		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => true;
		public override long Length => throw new NotSupportedException();
		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public ExportOutputMetrics Complete(CancellationToken cancellationToken)
		{
			if (!_completed)
			{
				Decode(ReadOnlySpan<byte>.Empty, flush: true, cancellationToken);
				_completed = true;
				return _metrics.Complete();
			}

			return _metrics.ToMetrics();
		}

		public override void Flush()
		{
		}

		public override Task FlushAsync(CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.CompletedTask;
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			ArgumentNullException.ThrowIfNull(buffer);
			ArgumentOutOfRangeException.ThrowIfNegative(offset);
			ArgumentOutOfRangeException.ThrowIfNegative(count);
			if (offset > buffer.Length - count)
				throw new ArgumentException("The write range exceeds the source buffer.", nameof(count));
			Write(buffer.AsSpan(offset, count));
		}

		public override void Write(ReadOnlySpan<byte> buffer)
		{
			ThrowIfCompleted();
			Decode(buffer, flush: false, CancellationToken.None);
		}

		public override ValueTask WriteAsync(
			ReadOnlyMemory<byte> buffer,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ThrowIfCompleted();
			Decode(buffer.Span, flush: false, cancellationToken);
			return ValueTask.CompletedTask;
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
			cancellationToken.ThrowIfCancellationRequested();
			ThrowIfCompleted();
			Decode(buffer.AsSpan(offset, count), flush: false, cancellationToken);
			return Task.CompletedTask;
		}

		public override int Read(byte[] buffer, int offset, int count) =>
			throw new NotSupportedException();

		public override long Seek(long offset, SeekOrigin origin) =>
			throw new NotSupportedException();

		public override void SetLength(long value) =>
			throw new NotSupportedException();

		private void Decode(
			ReadOnlySpan<byte> bytes,
			bool flush,
			CancellationToken cancellationToken)
		{
			do
			{
				cancellationToken.ThrowIfCancellationRequested();
				_decoder.Convert(
					bytes,
					_characterBuffer,
					flush,
					out var bytesUsed,
					out var charactersUsed,
					out var completed);
				_metrics.Append(_characterBuffer.AsSpan(0, charactersUsed));
				bytes = bytes[bytesUsed..];
				if (completed)
					break;
			}
			while (!bytes.IsEmpty || flush);
		}

		private void ThrowIfCompleted()
		{
			if (_completed)
				throw new InvalidOperationException("Output metrics were already finalized.");
		}
	}

	private struct NormalizedTextMetricsAccumulator
	{
		private long _normalizedCharacters;
		private long _lineBreaks;
		private bool _pendingCarriageReturn;

		public void Append(ReadOnlySpan<char> characters)
		{
			var index = 0;
			if (_pendingCarriageReturn && !characters.IsEmpty)
			{
				FlushPendingCarriageReturn();
				if (characters[0] == '\n')
					index = 1;
			}

			while (index < characters.Length)
			{
				var remaining = characters[index..];
				var lineBreakOffset = remaining.IndexOfAny(LineBreakCharacters);
				if (lineBreakOffset < 0)
				{
					_normalizedCharacters += remaining.Length;
					return;
				}

				_normalizedCharacters += lineBreakOffset;
				index += lineBreakOffset;
				var lineBreak = characters[index++];
				if (lineBreak == '\r' && index == characters.Length)
				{
					_pendingCarriageReturn = true;
					return;
				}

				_normalizedCharacters++;
				_lineBreaks++;
				if (lineBreak == '\r' && characters[index] == '\n')
					index++;
			}
		}

		public void Append(char character)
		{
			if (_pendingCarriageReturn)
			{
				FlushPendingCarriageReturn();
				if (character == '\n')
					return;
			}

			if (character == '\r')
			{
				_pendingCarriageReturn = true;
				return;
			}

			_normalizedCharacters++;
			if (character == '\n')
				_lineBreaks++;
		}

		public ExportOutputMetrics Complete()
		{
			FlushPendingCarriageReturn();
			return ToMetrics();
		}

		public ExportOutputMetrics ToMetrics()
		{
			if (_normalizedCharacters == 0)
				return ExportOutputMetrics.Empty;

			return new ExportOutputMetrics(
				_lineBreaks + 1,
				_normalizedCharacters,
				EstimateTokens(_normalizedCharacters));
		}

		private void FlushPendingCarriageReturn()
		{
			if (!_pendingCarriageReturn)
				return;

			_pendingCarriageReturn = false;
			_normalizedCharacters++;
			_lineBreaks++;
		}
	}
}

public readonly record struct ContentFileMetrics(
	string Path,
	long SizeBytes,
	int LineCount,
	int CharCount,
	bool IsEmpty,
	bool IsWhitespaceOnly,
	bool IsEstimated = false,
	int CrLfPairCount = 0,
	int TrailingNewlineChars = 0,
	int TrailingNewlineLineBreaks = 0);

public readonly record struct ExportOutputMetrics(long Lines, long Chars, long Tokens)
{
	public static ExportOutputMetrics Empty { get; } = new(0, 0, 0);
}
