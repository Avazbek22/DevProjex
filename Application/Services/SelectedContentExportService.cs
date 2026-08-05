namespace DevProjex.Application.Services;

using DevProjex.Application.Secrets;

public sealed record SelectedContentExportResult(
	string Text,
	SecretRedactionSnapshot? Redaction,
	string? PlaceholderExample,
	SecretRedactionLegendText? LegendText);

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
		SecretRedactionContext? redactionContext = null)
		=> (await BuildCoreAsync(
			filePaths,
			cancellationToken,
			displayPathMapper,
			maxFileCount: null,
			maxFileSizeForFullRead: null,
			maxOutputCharacters: null,
			redactionContext,
			includeLegend: true).ConfigureAwait(false)).Text;

	public Task<SelectedContentExportResult> BuildResultAsync(
		IEnumerable<string> filePaths,
		CancellationToken cancellationToken,
		Func<string, string>? displayPathMapper,
		SecretRedactionContext? redactionContext,
		bool includeLegend) =>
		BuildCoreAsync(
			filePaths,
			cancellationToken,
			displayPathMapper,
			maxFileCount: null,
			maxFileSizeForFullRead: null,
			maxOutputCharacters: null,
			redactionContext,
			includeLegend);

	public async Task<string> BuildBoundedPreviewAsync(
		IEnumerable<string> filePaths,
		int maxFileCount,
		long maxFileSizeForFullRead,
		int maxOutputCharacters,
		CancellationToken cancellationToken,
		Func<string, string>? displayPathMapper)
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
			redactionContext: null,
			includeLegend: false).ConfigureAwait(false)).Text;
	}

	private async Task<SelectedContentExportResult> BuildCoreAsync(
		IEnumerable<string> filePaths,
		CancellationToken cancellationToken,
		Func<string, string>? displayPathMapper,
		int? maxFileCount,
		long? maxFileSizeForFullRead,
		int? maxOutputCharacters,
		SecretRedactionContext? redactionContext,
		bool includeLegend)
	{
		// Use HashSet for O(1) deduplication
		var uniqueFiles = new HashSet<string>(PathComparer.Default);
		foreach (var path in filePaths)
		{
			if (!string.IsNullOrWhiteSpace(path))
				uniqueFiles.Add(path);
		}

		if (uniqueFiles.Count == 0)
			return new SelectedContentExportResult(string.Empty, null, null, null);

		// Convert to list and sort in-place
		var files = new List<string>(uniqueFiles);
		files.Sort(PathComparer.Default);
		var redactionScope = redactionContext?.BeginOutput(files);

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
						throw new SecretScanLimitExceededException(
							file,
							content?.SizeBytes ?? new FileInfo(file).Length,
							scanLimit);
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
			if (redactionScope is not null && content.IsEstimated)
			{
				throw new SecretScanLimitExceededException(
					file,
					content.SizeBytes,
					maxFileSizeForFullRead ?? SecretRedactionOutputPreparer.MaximumScannableFileBytes);
			}

			var transformedContent = redactionScope?.Redact(
				file,
				content.Content,
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
				// Trim trailing newlines for clipboard-friendly output
				var text = (transformedContent?.Text ?? content.Content).TrimEnd('\r', '\n');
				sb.AppendLine(text);
			}

			if (maxOutputCharacters is { } characterLimit && sb.Length >= characterLimit)
			{
				sb.Length = characterLimit;
				break;
			}
		}

		var snapshot = redactionScope?.Complete();
		var textOutput = anyWritten ? sb.ToString().TrimEnd('\r', '\n') : string.Empty;
		if (includeLegend && snapshot is { RedactedCount: > 0 })
		{
			var legend = SecretRedactionLegend.CreatePlainText(
				snapshot.RedactedCount,
				redactionScope!.PlaceholderExample!,
				redactionScope.LegendText);
			textOutput = string.IsNullOrEmpty(textOutput)
				? legend
				: $"{legend}{Environment.NewLine}{ClipboardBlankLine}{Environment.NewLine}{ClipboardBlankLine}{Environment.NewLine}{textOutput}";
		}

		return new SelectedContentExportResult(
			textOutput,
			snapshot,
			redactionScope?.PlaceholderExample,
			redactionScope?.LegendText);
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
