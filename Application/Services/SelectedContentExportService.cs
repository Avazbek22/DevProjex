namespace DevProjex.Application.Services;

using System.Runtime.CompilerServices;
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
	private const long MaximumParallelPreparationFileBytes = 1024 * 1024;
	internal const int MaximumParallelPreparations = 8;

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
		outputPathRedaction ??= OutputRootPathPresentation.CaptureRedactionDecision(transformationContext);
		var output = destination ?? new MaterializedSelectedContentOutput();
		var hasRootHeader = !string.IsNullOrWhiteSpace(displayRootPath);
		if (hasRootHeader)
		{
			var displayRoot = OutputRootPathPresentation
				.ResolvePath(displayRootPath!, outputPathRedaction)
				.Text;
			await output.AppendLineAsync(
				ContextRootPresentation.FormatLine(displayRoot),
				cancellationToken).ConfigureAwait(false);
		}
		if (maxOutputCharacters is { } rootCharacterLimit && output.Length >= rootCharacterLimit)
		{
			output.Truncate(rootCharacterLimit);
			await output.CompleteAsync(cancellationToken).ConfigureAwait(false);
			return new SelectedContentExportResult(output.GetMaterializedText(), null);
		}

		if (files.Length == 0)
		{
			if (maxOutputCharacters is { } characterLimit && output.Length > characterLimit)
				output.Truncate(characterLimit);
			await output.CompleteAsync(cancellationToken).ConfigureAwait(false);
			return new SelectedContentExportResult(
				hasRootHeader ? output.GetMaterializedText() : string.Empty,
				null);
		}

		var redactionContext = transformationContext?.Redaction;
		if (redactionContext is not null)
		{
			await redactionContext.Session
				.RefreshPersistentMarksAsync(redactionContext.ProjectRoot, cancellationToken)
				.ConfigureAwait(false);
		}
		using var transformationScope = transformationContext?.BeginOutputFromOwnedOrderedUnique(
			files,
			cancellationToken);
		var redactionScope = transformationScope?.Redaction;

		bool anyWritten = false;

		var processedFileCount = 0;
		var allowParallelPreparation = maxFileCount is null && maxOutputCharacters is null;
		await foreach (var prepared in PrepareContentEntriesAsync(
						   files,
						   displayPathMapper,
						   maxFileSizeForFullRead,
						   transformationScope,
						   redactionScope,
						   allowParallelPreparation,
						   cancellationToken).ConfigureAwait(false))
		{
			using (prepared)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var file = prepared.FilePath;
				var content = prepared.Content;
				if (content is null)
					continue;

				// Compression first, then secrets: the clipboard must carry exactly what the preview
				// showed, and the secret counter must describe the text that actually leaves.
				// Estimated content is an empty string standing in for text nobody read - transforming
				// it would record a clean scan of a file that was never opened.
				var rawDisplayPath = prepared.DisplayPath;
				var compression = prepared.Compression;
				var transformedText = compression?.Text ?? content.Content;
				var redactionPlan = content.IsEstimated
					? null
					: redactionScope?.CreatePlan(
						file,
						transformedText,
						compression?.Map,
						prepared.RedactionMetadata ?? SecretFileMetadata.Capture(file),
						prepared.ContentFingerprint,
						cancellationToken);

				processedFileCount++;
				if (!anyWritten && hasRootHeader)
				{
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
				if (maxFileCount is { } fileLimit && processedFileCount >= fileLimit)
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
		if (maxOutputCharacters is { } finalCharacterLimit && output.Length > finalCharacterLimit)
			output.Truncate(finalCharacterLimit);
		await output.CompleteAsync(cancellationToken).ConfigureAwait(false);
		var textOutput = hasRootHeader || anyWritten
			? output.GetMaterializedText()
			: string.Empty;

		return new SelectedContentExportResult(textOutput, snapshot);
	}

	private async IAsyncEnumerable<PreparedContentEntry> PrepareContentEntriesAsync(
		IReadOnlyList<string> files,
		Func<string, string>? displayPathMapper,
		long? maxFileSizeForFullRead,
		ContentTransformationScope? transformationScope,
		SecretRedactionScope? redactionScope,
		bool allowParallelPreparation,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		if (!allowParallelPreparation || files.Count <= 1)
		{
			foreach (var file in files)
			{
				cancellationToken.ThrowIfCancellationRequested();
				yield return await PrepareContentEntryAsync(
						file,
						displayPathMapper,
						maxFileSizeForFullRead,
						transformationScope,
						redactionScope,
						cancellationToken)
					.ConfigureAwait(false);
			}
			yield break;
		}

		using var preparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var preparationToken = preparationCancellation.Token;
		var pending = new Queue<Task<PreparedContentEntry>>(MaximumParallelPreparations);
		try
		{
			foreach (var file in files)
			{
				preparationToken.ThrowIfCancellationRequested();
				if (!IsSmallFile(file))
				{
					while (pending.TryDequeue(out var queued))
						yield return await queued.ConfigureAwait(false);

					yield return await PrepareContentEntryAsync(
							file,
							displayPathMapper,
							maxFileSizeForFullRead,
							transformationScope,
							redactionScope,
							preparationToken)
						.ConfigureAwait(false);
					continue;
				}

				pending.Enqueue(Task.Run(
					() => PrepareContentEntryAsync(
						file,
						displayPathMapper,
						maxFileSizeForFullRead,
						transformationScope,
						redactionScope,
						preparationToken).AsTask(),
					preparationToken));
				if (pending.Count >= MaximumParallelPreparations)
					yield return await pending.Dequeue().ConfigureAwait(false);
			}

			while (pending.TryDequeue(out var queued))
				yield return await queued.ConfigureAwait(false);
		}
		finally
		{
			TryCancel(preparationCancellation);
			await DrainAndDisposePendingAsync(pending).ConfigureAwait(false);
		}
	}

	private async ValueTask<PreparedContentEntry> PrepareContentEntryAsync(
		string file,
		Func<string, string>? displayPathMapper,
		long? maxFileSizeForFullRead,
		ContentTransformationScope? transformationScope,
		SecretRedactionScope? redactionScope,
		CancellationToken cancellationToken)
	{
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
			var scanLimit = maxFileSizeForFullRead ?? SecretRedactionOutputPreparer.MaximumScannableFileBytes;
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
					content = null;
					break;
				case var classification when PreparedSecretFile.IsUnscannableClassification(classification):
					break;
				case FileContentClassification.Text when content is not null:
					break;
				default:
					throw new SecretDetectionException(
						$"Hide Secrets could not inspect '{file}' ({readResult.Classification}).");
			}
		}

		if (content is null)
			return new PreparedContentEntry(file, file, null, null, contentFingerprint, redactionMetadata, null);

		var contentLease = redactionScope?.TrackFullContentBuffer();
		try
		{
			var displayPath = MapDisplayPath(file, displayPathMapper);
			var compression = content.IsEstimated
				? null
				: contentFingerprint is { } fingerprint
					? transformationScope?.Compress(
						file,
						displayPath,
						content.Content,
						fingerprint,
						cancellationToken)
					: transformationScope?.Compress(
						file,
						displayPath,
						content.Content,
						cancellationToken);
			return new PreparedContentEntry(
				file,
				displayPath,
				content,
				compression,
				contentFingerprint,
				redactionMetadata,
				contentLease);
		}
		catch
		{
			contentLease?.Dispose();
			throw;
		}
	}

	private static bool IsSmallFile(string path)
	{
		try
		{
			return new FileInfo(path).Length <= MaximumParallelPreparationFileBytes;
		}
		catch (Exception exception) when (exception is
			   IOException or
			   UnauthorizedAccessException or
			   NotSupportedException or
			   System.Security.SecurityException)
		{
			return false;
		}
	}

	private static async Task DrainAndDisposePendingAsync(
		IEnumerable<Task<PreparedContentEntry>> pending)
	{
		foreach (var task in pending)
		{
			try
			{
				(await task.ConfigureAwait(false)).Dispose();
			}
			catch
			{
				// The consumer's failure or cancellation remains authoritative.
			}
		}
	}

	private static void TryCancel(CancellationTokenSource cancellation)
	{
		try
		{
			cancellation.Cancel();
		}
		catch (AggregateException)
		{
			// Preparation callbacks cannot replace the consumer's failure during cleanup.
		}
	}

	private sealed class PreparedContentEntry(
		string filePath,
		string displayPath,
		TextFileContent? content,
		CodeCompressionResult? compression,
		ContentFingerprint? contentFingerprint,
		SecretFileMetadata? redactionMetadata,
		IDisposable? contentLease) : IDisposable
	{
		private IDisposable? _contentLease = contentLease;

		public string FilePath { get; } = filePath;
		public string DisplayPath { get; } = displayPath;
		public TextFileContent? Content { get; } = content;
		public CodeCompressionResult? Compression { get; } = compression;
		public ContentFingerprint? ContentFingerprint { get; } = contentFingerprint;
		public SecretFileMetadata? RedactionMetadata { get; } = redactionMetadata;

		public void Dispose() => Interlocked.Exchange(ref _contentLease, null)?.Dispose();
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

		public override void Truncate(int length)
		{
			ArgumentOutOfRangeException.ThrowIfNegative(length);
			if (length > _builder.Length)
				throw new ArgumentOutOfRangeException(nameof(length));
			if (length > 0 && length < _builder.Length &&
			    char.IsHighSurrogate(_builder[length - 1]) &&
			    char.IsLowSurrogate(_builder[length]))
			{
				length--;
			}

			_builder.Length = length;
		}
		public override ValueTask CompleteAsync(CancellationToken cancellationToken) =>
			ValueTask.CompletedTask;
		public override string GetMaterializedText()
			=> MaterializeWithoutTrailingLineEndings(_builder);
	}

	internal static string MaterializeWithoutTrailingLineEndings(StringBuilder builder)
	{
		ArgumentNullException.ThrowIfNull(builder);
		var length = builder.Length;
		while (length > 0 && builder[length - 1] is '\r' or '\n')
			length--;
		return builder.ToString(0, length);
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

}
