using System.Security;
using System.Text;
using System.Buffers;

namespace DevProjex.Terminal.CommandLine;

internal static class SelectionPathListReader
{
	internal const int MaximumEntries = 100_000;
	internal const long MaximumBytes = 16L * 1024 * 1024;

	public static async Task<IReadOnlyList<string>> ReadAsync(
		string source,
		ITerminalEnvironment environment,
		CancellationToken cancellationToken)
	{
		if (source == "-" && environment.IsInputInteractive)
			throw InvalidSource();

		StreamReader? ownedReader = null;
		try
		{
			TextReader reader;
			var enforceTextReaderByteLimit = false;
			if (source == "-")
			{
				if (environment.RawInput is { } rawInput)
				{
					ownedReader = new StreamReader(
						new MaximumLengthReadStream(rawInput, MaximumBytes),
						new UTF8Encoding(false, true),
						detectEncodingFromByteOrderMarks: false,
						bufferSize: 16 * 1024,
						leaveOpen: false);
					reader = ownedReader;
				}
				else
				{
					reader = environment.Input;
					enforceTextReaderByteLimit = true;
				}
			}
			else
			{
				var stream = new FileStream(
					Path.GetFullPath(source),
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read,
					bufferSize: 16 * 1024,
					FileOptions.Asynchronous | FileOptions.SequentialScan);
				if (stream.Length > MaximumBytes)
				{
					await stream.DisposeAsync().ConfigureAwait(false);
					throw InvalidSource();
				}
				ownedReader = new StreamReader(
					stream,
					new UTF8Encoding(false, true),
					detectEncodingFromByteOrderMarks: true,
					bufferSize: 16 * 1024,
					leaveOpen: false);
				reader = ownedReader;
			}

			return await ReadLinesAsync(
				reader,
				enforceTextReaderByteLimit,
				cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (ProjectContextValidationException)
		{
			throw;
		}
		catch (Exception exception) when (exception is
			       IOException or
			       UnauthorizedAccessException or
			       SecurityException or
			       DecoderFallbackException or
			       ArgumentException or
			       NotSupportedException)
		{
			throw InvalidSource();
		}
		finally
		{
			ownedReader?.Dispose();
		}
	}

	private static async Task<IReadOnlyList<string>> ReadLinesAsync(
		TextReader reader,
		bool enforceByteLimit,
		CancellationToken cancellationToken)
	{
		var paths = new List<string>();
		var line = new StringBuilder();
		var buffer = ArrayPool<char>.Shared.Rent(16 * 1024);
		var encoder = enforceByteLimit
			? new UTF8Encoding(false, true).GetEncoder()
			: null;
		long bytesRead = 0;
		var skipLeadingBom = true;
		var previousWasCarriageReturn = false;
		try
		{
			while (true)
			{
				var count = await reader.ReadAsync(
					buffer.AsMemory(),
					cancellationToken).ConfigureAwait(false);
				if (count == 0)
					break;

				var characters = buffer.AsSpan(0, count);
				if (encoder is not null)
				{
					bytesRead = checked(bytesRead + encoder.GetByteCount(characters, flush: false));
					if (bytesRead > MaximumBytes)
						throw InvalidSource();
				}

				foreach (var character in characters)
				{
					if (skipLeadingBom)
					{
						skipLeadingBom = false;
						if (character == '\uFEFF')
							continue;
					}
					if (character == '\n' && previousWasCarriageReturn)
					{
						previousWasCarriageReturn = false;
						continue;
					}
					previousWasCarriageReturn = false;
					if (character == '\r')
					{
						AddLine(paths, line);
						previousWasCarriageReturn = true;
					}
					else if (character == '\n')
					{
						AddLine(paths, line);
					}
					else
					{
						line.Append(character);
					}
				}
			}

			if (encoder is not null)
			{
				bytesRead = checked(bytesRead + encoder.GetByteCount([], flush: true));
				if (bytesRead > MaximumBytes)
					throw InvalidSource();
			}
			AddLine(paths, line);
			return paths;
		}
		finally
		{
			ArrayPool<char>.Shared.Return(buffer);
		}
	}

	private static void AddLine(ICollection<string> paths, StringBuilder line)
	{
		if (line.Length == 0)
			return;
		if (paths.Count == MaximumEntries)
			throw InvalidSource();
		paths.Add(line.ToString());
		line.Clear();
	}

	private sealed class MaximumLengthReadStream(Stream inner, long maximumBytes) : Stream
	{
		private long _bytesRead;

		public override bool CanRead => inner.CanRead;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position
		{
			get => _bytesRead;
			set => throw new NotSupportedException();
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			var allowed = ResolveReadCount(count);
			var read = inner.Read(buffer, offset, allowed);
			RegisterRead(read);
			return read;
		}

		public override int Read(Span<byte> buffer)
		{
			var read = inner.Read(buffer[..ResolveReadCount(buffer.Length)]);
			RegisterRead(read);
			return read;
		}

		public override async ValueTask<int> ReadAsync(
			Memory<byte> buffer,
			CancellationToken cancellationToken = default)
		{
			var read = await inner.ReadAsync(
				buffer[..ResolveReadCount(buffer.Length)],
				cancellationToken).ConfigureAwait(false);
			RegisterRead(read);
			return read;
		}

		private int ResolveReadCount(int requested)
		{
			var remainingWithSentinel = maximumBytes - _bytesRead + 1;
			return (int)Math.Min(requested, Math.Max(1, remainingWithSentinel));
		}

		private void RegisterRead(int count)
		{
			_bytesRead = checked(_bytesRead + count);
			if (_bytesRead > maximumBytes)
				throw InvalidSource();
		}

		public override void Flush() => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}

	private static ProjectContextValidationException InvalidSource() =>
		new("DPX-CLI-SELECT-FROM-INVALID", "Selection source is invalid.");
}
