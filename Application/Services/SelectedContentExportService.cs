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
	private const long DefaultMaximumMaterializedFileBytes = 10L * 1024 * 1024;

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
		ContentTransformationContext? transformationContext = null,
		string? displayRootPath = null,
		OutputPathRedactionDecision? outputPathRedaction = null)
		=> (await BuildCoreAsync(
			filePaths,
			cancellationToken,
			displayPathMapper,
			maxFileCount: null,
			maxFileSizeForFullRead: null,
			maxOutputCharacters: null,
			transformationContext,
			publishCompressionSnapshot: true,
			displayRootPath,
			outputPathRedaction).ConfigureAwait(false)).Text;

	public Task<SelectedContentExportResult> BuildResultAsync(
		IEnumerable<string> filePaths,
		CancellationToken cancellationToken,
		Func<string, string>? displayPathMapper,
		ContentTransformationContext? transformationContext,
		string? displayRootPath = null,
		OutputPathRedactionDecision? outputPathRedaction = null) =>
		BuildCoreAsync(
			filePaths,
			cancellationToken,
			displayPathMapper,
			maxFileCount: null,
			maxFileSizeForFullRead: null,
			maxOutputCharacters: null,
			transformationContext,
			publishCompressionSnapshot: true,
			displayRootPath,
			outputPathRedaction);

	public async Task WriteAsync(
		Stream destination,
		IEnumerable<string> filePaths,
		CancellationToken cancellationToken,
		Func<string, string>? displayPathMapper,
		ContentTransformationContext? transformationContext = null,
		string? displayRootPath = null,
		OutputPathRedactionDecision? outputPathRedaction = null)
	{
		ArgumentNullException.ThrowIfNull(destination);
		if (!destination.CanWrite)
			throw new InvalidOperationException("Target stream must be writable.");

		await using var writer = new StreamWriter(
			destination,
			new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
			bufferSize: 20 * 1024,
			leaveOpen: true);
		var output = new StreamingSelectedContentOutput(writer);
		_ = await BuildCoreAsync(
			filePaths,
			cancellationToken,
			displayPathMapper,
			maxFileCount: null,
			maxFileSizeForFullRead: null,
			maxOutputCharacters: null,
			transformationContext,
			publishCompressionSnapshot: true,
			displayRootPath,
			outputPathRedaction,
			output).ConfigureAwait(false);
	}

	public async Task<string> BuildBoundedPreviewAsync(
		IEnumerable<string> filePaths,
		int maxFileCount,
		long maxFileSizeForFullRead,
		int maxOutputCharacters,
		CancellationToken cancellationToken,
		Func<string, string>? displayPathMapper,
		CodeCompressionContext? compressionContext = null,
		string? displayRootPath = null)
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
			publishCompressionSnapshot: false,
			displayRootPath,
			outputPathRedaction: null).ConfigureAwait(false)).Text;
	}

	private async Task<SelectedContentExportResult> BuildCoreAsync(
		IEnumerable<string> filePaths,
		CancellationToken cancellationToken,
		Func<string, string>? displayPathMapper,
		int? maxFileCount,
		long? maxFileSizeForFullRead,
		int? maxOutputCharacters,
		ContentTransformationContext? transformationContext,
		bool publishCompressionSnapshot,
		string? displayRootPath = null,
		OutputPathRedactionDecision? outputPathRedaction = null,
		SelectedContentOutput? destination = null)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var files = ContentPathOrdering.BuildOrderedUnique(filePaths, cancellationToken);
		if (files.Count == 0)
			return new SelectedContentExportResult(string.Empty, null);
		var redactionContext = transformationContext?.Redaction;
		outputPathRedaction ??= OutputRootPathPresentation.CaptureRedactionDecision(transformationContext);
		if (redactionContext is not null)
		{
			await redactionContext.Session
				.RefreshPersistentMarksAsync(redactionContext.ProjectRoot, cancellationToken)
				.ConfigureAwait(false);
		}
		using var transformationScope = transformationContext?.BeginOutput(files, cancellationToken);
		var redactionScope = transformationScope?.Redaction;

		var output = destination ?? new MaterializedSelectedContentOutput();
		bool anyWritten = false;

		var processedFileCount = 0;
		foreach (var file in files)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (maxFileCount is { } fileLimit && processedFileCount >= fileLimit)
				break;

			TextFileContent? content;
			ContentFingerprint? contentFingerprint = null;
			SecretFileMetadata? redactionMetadata = null;
			if (redactionScope is null)
			{
				if (transformationScope?.Compression is not null)
				{
					var readFact = await contentAnalyzer.ReadFactAsync(
						file,
						maxFileSizeForFullRead ?? DefaultMaximumMaterializedFileBytes,
						cancellationToken).ConfigureAwait(false);
					content = readFact.ToReadResult().Content;
					contentFingerprint = readFact.Fingerprint;
				}
				else
				{
					content = maxFileSizeForFullRead is { } sizeLimit
						? await contentAnalyzer.TryReadAsTextAsync(file, sizeLimit, cancellationToken).ConfigureAwait(false)
						: await contentAnalyzer.TryReadAsTextAsync(file, cancellationToken).ConfigureAwait(false);
				}
			}
			else
			{
				var scanLimit = maxFileSizeForFullRead ??
				                SecretRedactionOutputPreparer.MaximumScannableFileBytes;
				var coherentRead = await ReadRedactionFactAsync(file, scanLimit, cancellationToken)
					.ConfigureAwait(false);
				var readFact = coherentRead.Fact;
				redactionMetadata = coherentRead.Metadata;
				var readResult = readFact.ToReadResult();
				content = readResult.Content;
				contentFingerprint = readFact.Fingerprint;
				switch (readResult.Classification)
				{
					case FileContentClassification.Binary:
						continue;
					case FileContentClassification.TooLarge:
					case FileContentClassification.Unreadable:
					case FileContentClassification.UnsupportedEncoding:
						// Unavailable content contributes no text, matching the unredacted path.
						// One unreadable file must not discard the rest of the selection.
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
			var rawDisplayPath = MapDisplayPath(file, displayPathMapper);
			var compression = content.IsEstimated
				? null
				: contentFingerprint is { } fingerprint
					? transformationScope?.Compress(
						file,
						rawDisplayPath,
						content.Content,
						fingerprint,
						cancellationToken)
					: transformationScope?.Compress(
						file,
						rawDisplayPath,
						content.Content,
						cancellationToken);
			var transformedText = compression?.Text ?? content.Content;
			var redactionPlan = content.IsEstimated
				? null
				: redactionScope?.CreatePlan(
					file,
					transformedText,
					compression?.Map,
					redactionMetadata ?? SecretFileMetadata.Capture(file),
					contentFingerprint,
					cancellationToken);

			processedFileCount++;
			if (!anyWritten && !string.IsNullOrWhiteSpace(displayRootPath))
			{
				await output.AppendLineAsync(
					$"{SingleLineTextEscaping.Escape(displayRootPath)}:",
					cancellationToken).ConfigureAwait(false);
				await AppendClipboardBlankLineAsync(output, cancellationToken).ConfigureAwait(false);
				await AppendClipboardBlankLineAsync(output, cancellationToken).ConfigureAwait(false);
			}
			if (anyWritten)
			{
				await AppendClipboardBlankLineAsync(output, cancellationToken).ConfigureAwait(false);
				await AppendClipboardBlankLineAsync(output, cancellationToken).ConfigureAwait(false);
			}

			anyWritten = true;

			var displayPath = OutputRootPathPresentation
				.ResolvePath(rawDisplayPath, outputPathRedaction)
				.Text;
			displayPath = SingleLineTextEscaping.Escape(displayPath);
			await output.AppendLineAsync($"{displayPath}:", cancellationToken).ConfigureAwait(false);
			await AppendClipboardBlankLineAsync(output, cancellationToken).ConfigureAwait(false);

			if (content.IsEmpty)
			{
				await output.AppendLineAsync(NoContentMarker, cancellationToken).ConfigureAwait(false);
			}
			else if (content.IsWhitespaceOnly)
			{
				await output.AppendLineAsync(
					$"{WhitespaceMarkerPrefix}{content.SizeBytes}{WhitespaceMarkerSuffix}",
					cancellationToken).ConfigureAwait(false);
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
					await output.AppendAsync(
						transformedText.AsMemory(0, sourceLength),
						cancellationToken).ConfigureAwait(false);
					await output.AppendLineAsync(string.Empty, cancellationToken).ConfigureAwait(false);
				}
				else
				{
					await output.AppendRedactedAsync(
						redactionPlan,
						transformedText,
						sourceLength,
						cancellationToken).ConfigureAwait(false);
					await output.AppendLineAsync(string.Empty, cancellationToken).ConfigureAwait(false);
				}
			}

			if (maxOutputCharacters is { } characterLimit && output.Length >= characterLimit)
			{
				output.Truncate(characterLimit);
				break;
			}
		}

		var snapshot = redactionScope?.Complete();
		if (redactionContext is not null)
		{
			await redactionContext.Session
				.FlushPendingPersistentMarkMigrationsAsync(
					redactionContext.ProjectRoot,
					cancellationToken)
				.ConfigureAwait(false);
		}
		if (publishCompressionSnapshot)
			transformationScope?.Compression?.Complete();
		await output.CompleteAsync(cancellationToken).ConfigureAwait(false);
		var textOutput = anyWritten ? output.GetMaterializedText() : string.Empty;

		return new SelectedContentExportResult(textOutput, snapshot);
	}

	private async ValueTask<CoherentRedactionRead> ReadRedactionFactAsync(
		string path,
		long maximumBytes,
		CancellationToken cancellationToken)
	{
		if (contentAnalyzer is ICoherentFileContentAnalyzer coherentAnalyzer)
		{
			var identified = await coherentAnalyzer
				.ReadFactWithIdentityAsync(path, maximumBytes, cancellationToken)
				.ConfigureAwait(false);
			var metadata = identified.Identity is { } identity
				? SecretFileMetadata.FromIdentity(identity)
				: SecretFileMetadata.Capture(path);
			EnsureContentMatchesMetadata(path, identified.Fact, metadata);
			return new CoherentRedactionRead(identified.Fact, metadata);
		}

		var before = SecretFileMetadata.Capture(path);
		var fact = await contentAnalyzer
			.ReadFactAsync(path, maximumBytes, cancellationToken)
			.ConfigureAwait(false);
		var after = SecretFileMetadata.Capture(path);
		if (before != after)
			throw new SecretDetectionException($"Hide Secrets could not inspect a changing file: '{path}'.");
		EnsureContentMatchesMetadata(path, fact, after);
		return new CoherentRedactionRead(fact, after);
	}

	private static void EnsureContentMatchesMetadata(
		string path,
		ContentReadFact fact,
		SecretFileMetadata metadata)
	{
		if (fact.ToReadResult().Content is { } content && content.SizeBytes != metadata.Length)
			throw new SecretDetectionException($"Hide Secrets could not inspect a changing file: '{path}'.");
	}

	private readonly record struct CoherentRedactionRead(
		ContentReadFact Fact,
		SecretFileMetadata Metadata);

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

	private static ValueTask AppendClipboardBlankLineAsync(
		SelectedContentOutput output,
		CancellationToken cancellationToken) =>
		output.AppendLineAsync(ClipboardBlankLine, cancellationToken);

	private abstract class SelectedContentOutput
	{
		public abstract int Length { get; }
		public abstract ValueTask AppendAsync(
			ReadOnlyMemory<char> value,
			CancellationToken cancellationToken);

		public async ValueTask AppendLineAsync(
			string value,
			CancellationToken cancellationToken)
		{
			if (value.Length > 0)
				await AppendAsync(value.AsMemory(), cancellationToken).ConfigureAwait(false);
			await AppendAsync(Environment.NewLine.AsMemory(), cancellationToken).ConfigureAwait(false);
		}

		public abstract ValueTask AppendRedactedAsync(
			SecretFileRedactionPlan plan,
			string content,
			int sourceLength,
			CancellationToken cancellationToken);
		public abstract void Truncate(int length);
		public abstract ValueTask CompleteAsync(CancellationToken cancellationToken);
		public abstract string GetMaterializedText();
	}

	private sealed class MaterializedSelectedContentOutput : SelectedContentOutput
	{
		private readonly StringBuilder _builder = new();
		public override int Length => _builder.Length;

		public override ValueTask AppendAsync(
			ReadOnlyMemory<char> value,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			_builder.Append(value.Span);
			return ValueTask.CompletedTask;
		}

		public override ValueTask AppendRedactedAsync(
			SecretFileRedactionPlan plan,
			string content,
			int sourceLength,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			plan.AppendTo(_builder, content, sourceLength);
			return ValueTask.CompletedTask;
		}

		public override void Truncate(int length) => _builder.Length = length;
		public override ValueTask CompleteAsync(CancellationToken cancellationToken) =>
			ValueTask.CompletedTask;
		public override string GetMaterializedText()
		{
			while (_builder.Length > 0 && _builder[^1] is '\r' or '\n')
				_builder.Length--;
			return _builder.ToString();
		}
	}

	private sealed class StreamingSelectedContentOutput(TextWriter writer) : SelectedContentOutput
	{
		private readonly TrailingLineEndingTextWriter _output = new(writer);
		public override int Length => _output.Length;

		public override ValueTask AppendAsync(
			ReadOnlyMemory<char> value,
			CancellationToken cancellationToken) =>
			new(_output.WriteAsync(value, cancellationToken));

		public override ValueTask AppendRedactedAsync(
			SecretFileRedactionPlan plan,
			string content,
			int sourceLength,
			CancellationToken cancellationToken) =>
			plan.WriteToAsync(_output, content, sourceLength, cancellationToken);

		public override void Truncate(int length) =>
			throw new NotSupportedException("Streaming output cannot be truncated.");
		public override ValueTask CompleteAsync(CancellationToken cancellationToken) =>
			_output.CompleteAsync(cancellationToken);
		public override string GetMaterializedText() => string.Empty;
	}

	private sealed class TrailingLineEndingTextWriter(TextWriter inner) : TextWriter
	{
		private readonly StringBuilder _trailing = new(2);
		public override Encoding Encoding => inner.Encoding;
		public int Length { get; private set; }

		public override async Task WriteAsync(
			ReadOnlyMemory<char> buffer,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var contentLength = buffer.Length;
			while (contentLength > 0 && buffer.Span[contentLength - 1] is '\r' or '\n')
				contentLength--;

			if (contentLength > 0)
			{
				if (_trailing.Length > 0)
				{
					await inner.WriteAsync(
						_trailing.ToString().AsMemory(),
						cancellationToken).ConfigureAwait(false);
					_trailing.Clear();
				}
				await inner.WriteAsync(buffer[..contentLength], cancellationToken).ConfigureAwait(false);
			}

			if (contentLength < buffer.Length)
				_trailing.Append(buffer.Span[contentLength..]);
			Length = checked(Length + buffer.Length);
		}

		public async ValueTask CompleteAsync(CancellationToken cancellationToken)
		{
			_trailing.Clear();
			await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
		}
	}
}
