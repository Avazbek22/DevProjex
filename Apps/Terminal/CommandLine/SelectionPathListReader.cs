using System.Security;
using System.Text;

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
			if (source == "-")
			{
				reader = environment.Input;
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

			var paths = new List<string>();
			long bytesRead = 0;
			while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
			{
				bytesRead += Encoding.UTF8.GetByteCount(line) + 1L;
				if (bytesRead > MaximumBytes)
					throw InvalidSource();
				if (line.Length == 0)
					continue;
				if (paths.Count == MaximumEntries)
					throw InvalidSource();
				paths.Add(line);
			}
			return paths;
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

	private static ProjectContextValidationException InvalidSource() =>
		new("DPX-CLI-SELECT-FROM-INVALID", "Selection source is invalid.");
}
