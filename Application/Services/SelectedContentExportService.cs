using System.Text;
using DevProjex.Kernel;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Application.Services;

/// <summary>
/// Builds clipboard-friendly text export from selected file contents.
/// Uses IFileContentAnalyzer as the single source of truth for text detection.
/// </summary>
public sealed class SelectedContentExportService
{
	private const string ClipboardBlankLine = "\u00A0"; // NBSP: looks empty but won't collapse on paste
	private const string NoContentMarker = "[No Content, 0 bytes]";
	private const string WhitespaceMarkerPrefix = "[Whitespace, ";
	private const string WhitespaceMarkerSuffix = " bytes]";

	private readonly IFileContentAnalyzer _contentAnalyzer;

	public SelectedContentExportService(IFileContentAnalyzer contentAnalyzer)
	{
		_contentAnalyzer = contentAnalyzer;
	}

	public string Build(IEnumerable<string> filePaths) =>
		BuildAsync(filePaths, CancellationToken.None).GetAwaiter().GetResult();

	public async Task<string> BuildAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken)
	{
		// Use HashSet for O(1) deduplication
		var uniqueFiles = new HashSet<string>(PathComparer.Default);
		foreach (var path in filePaths)
		{
			if (!string.IsNullOrWhiteSpace(path))
				uniqueFiles.Add(path);
		}

		if (uniqueFiles.Count == 0)
			return string.Empty;

		// Convert to list and sort in-place
		var files = new List<string>(uniqueFiles);
		files.Sort(PathComparer.Default);

		var sb = new StringBuilder();
		bool anyWritten = false;

		foreach (var file in files)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var content = await _contentAnalyzer.TryReadAsTextAsync(file, cancellationToken).ConfigureAwait(false);

			// Skip binary files (null result)
			if (content is null)
				continue;

			if (anyWritten)
			{
				AppendClipboardBlankLine(sb);
				AppendClipboardBlankLine(sb);
			}

			anyWritten = true;

			sb.AppendLine($"{file}:");
			AppendClipboardBlankLine(sb);

			if (content.IsEmpty)
			{
				sb.AppendLine(NoContentMarker);
			}
			else if (content.IsWhitespaceOnly)
			{
				sb.AppendLine($"{WhitespaceMarkerPrefix}{content.SizeBytes}{WhitespaceMarkerSuffix}");
			}
			else
			{
				// Trim trailing newlines for clipboard-friendly output
				var text = content.Content.TrimEnd('\r', '\n');
				sb.AppendLine(text);
			}
		}

		return anyWritten ? sb.ToString().TrimEnd('\r', '\n') : string.Empty;
	}

	private static void AppendClipboardBlankLine(StringBuilder sb) => sb.AppendLine(ClipboardBlankLine);
}
