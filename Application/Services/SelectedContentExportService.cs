using System.Text;
using DevProjex.Kernel;

namespace DevProjex.Application.Services;

public sealed class SelectedContentExportService
{
	private const string ClipboardBlankLine = "\u00A0"; // NBSP: looks empty but won't collapse on paste

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

			var (success, text) = await TryReadFileTextForClipboardAsync(file, cancellationToken).ConfigureAwait(false);
			if (!success)
				continue;

			if (anyWritten)
			{
				AppendClipboardBlankLine(sb);
				AppendClipboardBlankLine(sb);
			}

			anyWritten = true;

			sb.AppendLine($"{file}:");
			AppendClipboardBlankLine(sb);
			sb.AppendLine(text);
		}

		return anyWritten ? sb.ToString().TrimEnd('\r', '\n') : string.Empty;
	}

	private static async Task<(bool Success, string Text)> TryReadFileTextForClipboardAsync(string path, CancellationToken cancellationToken)
	{
		try
		{
			if (!File.Exists(path))
				return (false, string.Empty);

			var fi = new FileInfo(path);
			if (fi.Length == 0)
				return (false, string.Empty);

			// Check for binary content (first 8KB)
			await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, useAsync: true))
			{
				int toRead = (int)Math.Min(8192, fs.Length);
				var buffer = new byte[toRead];
				int read = await fs.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);

				for (int i = 0; i < read; i++)
				{
					if (buffer[i] == 0)
						return (false, string.Empty);
				}
			}

			string raw;
			using (var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
				raw = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

			if (string.IsNullOrWhiteSpace(raw))
				return (false, string.Empty);

			if (raw.IndexOf('\0') >= 0)
				return (false, string.Empty);

			var text = raw.TrimEnd('\r', '\n');
			return string.IsNullOrWhiteSpace(text) ? (false, string.Empty) : (true, text);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			return (false, string.Empty);
		}
	}

	private static void AppendClipboardBlankLine(StringBuilder sb) => sb.AppendLine(ClipboardBlankLine);
}
