using System.Text;
using DevProjex.Kernel;

namespace DevProjex.Application.Services;

public sealed class SelectedContentExportService
{
	private const string ClipboardBlankLine = "\u00A0"; // NBSP: looks empty but won't collapse on paste
	private const string NoContentMarker = "[No Content, 0 bytes]";
	private const string WhitespaceMarkerPrefix = "[Whitespace, ";
	private const string WhitespaceMarkerSuffix = " bytes]";

	public string Build(IEnumerable<string> filePaths) => BuildAsync(filePaths, CancellationToken.None).GetAwaiter().GetResult();

	public async Task<string> BuildAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken)
	{
		var files = filePaths
			.Where(p => !string.IsNullOrWhiteSpace(p))
			.Distinct(PathComparer.Default)
			.OrderBy(p => p, PathComparer.Default)
			.ToList();

		if (files.Count == 0)
			return string.Empty;

		var sb = new StringBuilder();
		bool anyWritten = false;

		foreach (var file in files)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var (result, text, sizeBytes) = await TryReadFileTextForClipboardAsync(file, cancellationToken).ConfigureAwait(false);
			if (result == ContentReadResult.None)
				continue;

			if (anyWritten)
			{
				AppendClipboardBlankLine(sb);
				AppendClipboardBlankLine(sb);
			}

			anyWritten = true;

			sb.AppendLine($"{file}:");
			AppendClipboardBlankLine(sb);

			switch (result)
			{
				case ContentReadResult.Empty:
					sb.AppendLine(NoContentMarker);
					break;
				case ContentReadResult.Whitespace:
					sb.AppendLine($"{WhitespaceMarkerPrefix}{sizeBytes}{WhitespaceMarkerSuffix}");
					break;
				default:
					sb.AppendLine(text);
					break;
			}
		}

		return anyWritten ? sb.ToString().TrimEnd('\r', '\n') : string.Empty;
	}

	private static async Task<(ContentReadResult Result, string Text, long SizeBytes)> TryReadFileTextForClipboardAsync(string path, CancellationToken cancellationToken)
	{
		try
		{
			if (!File.Exists(path))
				return (ContentReadResult.None, string.Empty, 0);

			var fi = new FileInfo(path);
			var sizeBytes = fi.Length;
			if (sizeBytes == 0)
				return (ContentReadResult.Empty, string.Empty, 0);

			// Check for binary content (first 8KB)
			await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, useAsync: true))
			{
				int toRead = (int)Math.Min(8192, fs.Length);
				var buffer = new byte[toRead];
				int read = await fs.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);

				for (int i = 0; i < read; i++)
				{
					if (buffer[i] == 0)
						return (ContentReadResult.None, string.Empty, sizeBytes);
				}
			}

			string raw;
			using (var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
				raw = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

			if (string.IsNullOrWhiteSpace(raw))
				return (ContentReadResult.Whitespace, string.Empty, sizeBytes);

			if (raw.IndexOf('\0') >= 0)
				return (ContentReadResult.None, string.Empty, sizeBytes);

			var text = raw.TrimEnd('\r', '\n');
			return string.IsNullOrWhiteSpace(text)
				? (ContentReadResult.Whitespace, string.Empty, sizeBytes)
				: (ContentReadResult.Text, text, sizeBytes);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			return (ContentReadResult.None, string.Empty, 0);
		}
	}

	private static void AppendClipboardBlankLine(StringBuilder sb) => sb.AppendLine(ClipboardBlankLine);

	private enum ContentReadResult
	{
		None,
		Text,
		Empty,
		Whitespace
	}
}
