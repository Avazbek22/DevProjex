using System.Security;
using System.Text;
using System.Buffers;
using DevProjex.Kernel.IO;

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
						new MaximumLengthReadStream(rawInput, MaximumBytes, InvalidSource),
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
				ownedReader = new StreamReader(
					new MaximumLengthReadStream(stream, MaximumBytes, InvalidSource),
					new UTF8Encoding(false, true),
					detectEncodingFromByteOrderMarks: false,
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
			ArrayPool<char>.Shared.Return(buffer, clearArray: true);
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

	private static ProjectContextValidationException InvalidSource() =>
		new("DPX-CLI-SELECT-FROM-INVALID", "Selection source is invalid.");
}
