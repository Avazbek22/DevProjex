namespace DevProjex.Application.Services;

using DevProjex.Application.Secrets;

public sealed record SelectedContentExportResult(
	string Text,
	SecretRedactionSnapshot? Redaction);

/// <summary>
/// Builds clipboard-friendly text export from selected file contents.
/// Uses IFileContentAnalyzer as the single source of truth for text detection.
/// </summary>
public sealed class SelectedContentExportService(IFileContentAnalyzer contentAnalyzer)
{
	private const string ClipboardBlankLine = "\u00A0"; // NBSP: looks empty but won't collapse on paste
	private const string NoContentMarker = "[No Content, 0 bytes]";
	private const string WhitespaceMarkerPrefix = "[Whitespace, ";
	private const string WhitespaceMarkerSuffix = " bytes]";

	public string Build(IEnumerable<string> filePaths) =>
		Build(filePaths, displayPathMapper: null);

	public string Build(IEnumerable<string> filePaths, Func<string, string>? displayPathMapper) =>
		BuildAsync(filePaths, CancellationToken.None, displayPathMapper).GetAwaiter().GetResult();

	public async Task<string> BuildAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken)
		=> await BuildAsync(filePaths, cancellationToken, displayPathMapper: null).ConfigureAwait(false);

	public async Task<string> BuildAsync(
		IEnumerable<string> filePaths,
		CancellationToken cancellationToken,
		Func<string, string>? displayPathMapper,
		ContentTransformationContext? transformationContext = null)
		=> (await BuildCoreAsync(
			filePaths,
			cancellationToken,
			displayPathMapper,
			maxFileCount: null,
			maxFileSizeForFullRead: null,
			maxOutputCharacters: null,
			transformationContext,
			publishCompressionSnapshot: true).ConfigureAwait(false)).Text;

	public Task<SelectedContentExportResult> BuildResultAsync(
		IEnumerable<string> filePaths,
		CancellationToken cancellationToken,
		Func<string, string>? displayPathMapper,
		ContentTransformationContext? transformationContext) =>
		BuildCoreAsync(
			filePaths,
			cancellationToken,
			displayPathMapper,
			maxFileCount: null,
			maxFileSizeForFullRead: null,
			maxOutputCharacters: null,
			transformationContext,
			publishCompressionSnapshot: true);

	public async Task<string> BuildBoundedPreviewAsync(
		IEnumerable<string> filePaths,
		int maxFileCount,
		long maxFileSizeForFullRead,
		int maxOutputCharacters,
		CancellationToken cancellationToken,
		Func<string, string>? displayPathMapper,
		CodeCompressionContext? compressionContext = null)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileCount);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileSizeForFullRead);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputCharacters);

		return (await BuildCoreAsync(
			filePaths,
			cancellationToken,
			displayPathMapper,
			maxFileCount,
			maxFileSizeForFullRead,
			maxOutputCharacters,
			ContentTransformationContext.For(compressionContext, redaction: null),
			publishCompressionSnapshot: false).ConfigureAwait(false)).Text;
	}

	private async Task<SelectedContentExportResult> BuildCoreAsync(
		IEnumerable<string> filePaths,
		CancellationToken cancellationToken,
		Func<string, string>? displayPathMapper,
		int? maxFileCount,
		long? maxFileSizeForFullRead,
		int? maxOutputCharacters,
		ContentTransformationContext? transformationContext,
		bool publishCompressionSnapshot)
	{
		// Use HashSet for O(1) deduplication
		var uniqueFiles = new HashSet<string>(PathComparer.Default);
		foreach (var path in filePaths)
		{
			if (!string.IsNullOrWhiteSpace(path))
				uniqueFiles.Add(path);
		}

		if (uniqueFiles.Count == 0)
			return new SelectedContentExportResult(string.Empty, null);

		// Convert to list and sort in-place
		var files = new List<string>(uniqueFiles);
		files.Sort(PathComparer.Default);
		using var transformationScope = transformationContext?.BeginOutput(files);
		var redactionScope = transformationScope?.Redaction;

		var sb = new StringBuilder();
		bool anyWritten = false;

		var processedFileCount = 0;
		foreach (var file in files)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (maxFileCount is { } fileLimit && processedFileCount >= fileLimit)
				break;

			TextFileContent? content;
			if (redactionScope is null)
			{
				content = maxFileSizeForFullRead is { } sizeLimit
					? await contentAnalyzer.TryReadAsTextAsync(file, sizeLimit, cancellationToken).ConfigureAwait(false)
					: await contentAnalyzer.TryReadAsTextAsync(file, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				var scanLimit = maxFileSizeForFullRead ??
				                SecretRedactionOutputPreparer.MaximumScannableFileBytes;
				var readResult = await contentAnalyzer
					.ReadClassifiedAsync(file, scanLimit, cancellationToken)
					.ConfigureAwait(false);
				content = readResult.Content;
				switch (readResult.Classification)
				{
					case FileContentClassification.Binary:
						continue;
					case FileContentClassification.TooLarge:
						// Estimated content carries no text, so this file contributes the same empty
						// section it would with Hide Secrets off. One file the scanner may not read
						// does not cost the user the rest of the selection.
						break;
					case FileContentClassification.Text when content is not null:
						break;
					default:
						throw new SecretDetectionException(
							$"Hide Secrets could not inspect '{file}' ({readResult.Classification}).");
				}
			}

			// Skip binary files (null result)
			if (content is null)
				continue;

			using var contentLease = redactionScope?.TrackFullContentBuffer();
			// Compression first, then secrets: the clipboard must carry exactly what the preview
			// showed, and the secret counter must describe the text that actually leaves.
			// Estimated content is an empty string standing in for text nobody read - transforming
			// it would record a clean scan of a file that was never opened.
			var compression = content.IsEstimated
				? null
				: transformationScope?.Compress(
					file,
					MapDisplayPath(file, displayPathMapper),
					content.Content,
					cancellationToken);
			var transformedText = compression?.Text ?? content.Content;
			var redactionPlan = content.IsEstimated
				? null
				: redactionScope?.CreatePlan(
					file,
					transformedText,
					compression?.Map,
					cancellationToken);

			processedFileCount++;
			if (anyWritten)
			{
				AppendClipboardBlankLine(sb);
				AppendClipboardBlankLine(sb);
			}

			anyWritten = true;

			var displayPath = MapDisplayPath(file, displayPathMapper);
			sb.AppendLine($"{displayPath}:");
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
				// Written from the transformed text, never from the file on disk: the plan's offsets
				// describe that text, and this surface has to carry the same bytes the preview showed.
				// Trim trailing newlines for clipboard-friendly output without allocating a
				// second whole-file string when redaction is enabled.
				var sourceLength = transformedText.Length;
				while (sourceLength > 0 && transformedText[sourceLength - 1] is '\r' or '\n')
					sourceLength--;
				if (redactionPlan is null)
				{
					sb.Append(transformedText.AsSpan(0, sourceLength));
					sb.AppendLine();
				}
				else
				{
					redactionPlan.AppendTo(sb, transformedText, sourceLength);
					sb.AppendLine();
				}
			}

			if (maxOutputCharacters is { } characterLimit && sb.Length >= characterLimit)
			{
				sb.Length = characterLimit;
				break;
			}
		}

		var snapshot = redactionScope?.Complete();
		if (publishCompressionSnapshot)
			transformationScope?.Compression?.Complete();
		var textOutput = anyWritten ? sb.ToString().TrimEnd('\r', '\n') : string.Empty;

		return new SelectedContentExportResult(textOutput, snapshot);
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

	private static void AppendClipboardBlankLine(StringBuilder sb) => sb.AppendLine(ClipboardBlankLine);
}
