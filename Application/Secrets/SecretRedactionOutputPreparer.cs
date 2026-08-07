namespace DevProjex.Application.Secrets;

/// <summary>
/// Reads each selected text file once, applies one deterministic redaction scope, and stores only
/// the transformed bytes in temporary files. Consumers may then render context, folders, or ZIPs
/// without reopening source text and without retaining a whole project in managed memory.
/// </summary>
public sealed class SecretRedactionOutputPreparer(IFileContentAnalyzer contentAnalyzer)
{
	public const long MaximumScannableFileBytes = 16 * 1024 * 1024;
	private const long MaximumParallelScanFileBytes = 1024 * 1024;
	private const int MaximumParallelScans = 8;

	/// <summary>
	/// Materializes the transformed text every non-preview output reads: context documents in every
	/// format, and the folder and ZIP project copies.
	///
	/// There is deliberately no per-caller opt-out. What the preview shows is what every copy and
	/// export contains - one resolved state, no surface that quietly disagrees with another. The
	/// project copy carries a notice saying so, exactly as it already does for Hide Secrets.
	/// </summary>
	public async Task<PreparedSecretRedactionOutput> PrepareAsync(
		ContentTransformationContext context,
		IReadOnlyList<string> orderedFilePaths,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(orderedFilePaths);
		if (context.Redaction is { } redactionContext)
			await redactionContext.Session.EnsureWarmUpAsync(cancellationToken).ConfigureAwait(false);

		var workingDirectory = CreateWorkingDirectory();
		var preparedFiles = new Dictionary<string, PreparedSecretFile>(PathComparer.Default);
		string? firstUnscannablePath = null;
		using var transformationScope = context.BeginOutput(orderedFilePaths);
		var scope = transformationScope.Redaction;
		try
		{
			for (var index = 0; index < orderedFilePaths.Count; index++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var sourcePath = orderedFilePaths[index];
				var metadataBeforeRead = SecretFileMetadata.Capture(sourcePath);
				var result = await contentAnalyzer
					.ReadClassifiedAsync(sourcePath, MaximumScannableFileBytes, cancellationToken)
					.ConfigureAwait(false);
				var metadataAfterRead = SecretFileMetadata.Capture(sourcePath);
				EnsureStableRead(sourcePath, metadataBeforeRead, metadataAfterRead, result.Content);

				switch (result.Classification)
				{
					case FileContentClassification.Binary:
						scope?.AnalyzeBinary(sourcePath, metadataAfterRead);
						preparedFiles[sourcePath] = PreparedSecretFile.Binary(sourcePath);
						continue;
					case FileContentClassification.TooLarge:
						// A per-file limit degrades that file, not the whole run - the same rule the
						// compressor already follows for text past its parse limit. Redaction cannot
						// promise anything about text it never read, so the file is recorded as
						// unscanned and its content is withheld: every document surface omits text
						// this large anyway, so nothing that used to ship stops shipping. The one
						// surface that would copy the bytes verbatim is the project copy, and that
						// is where the refusal now lives.
						if (scope is not null)
						{
							scope.AnalyzeUnscannable(sourcePath, metadataAfterRead);
							preparedFiles[sourcePath] = PreparedSecretFile.Unscannable(sourcePath);
							firstUnscannablePath ??= sourcePath;
							continue;
						}

						// Compression alone has no promise to break, and the file is far past the
						// parse limit anyway, so it ships unchanged from its original.
						preparedFiles[sourcePath] = PreparedSecretFile.Unchanged(sourcePath);
						continue;
					case FileContentClassification.Text:
						break;
					default:
						throw new SecretDetectionException(
							$"Hide Secrets could not inspect '{sourcePath}' ({result.Classification}).");
				}

				var content = result.Content ??
				              throw new SecretDetectionException(
					              $"Hide Secrets received no text for '{sourcePath}'.");
				using var contentLease = context.Redaction?.Session.TrackFullContentBuffer();
				// Compression first: the redaction plan, its spans and its counts all describe the
				// text that actually leaves, not the text on disk.
				var compressed = transformationScope.Compress(
					sourcePath,
					NormalizeRelativePath(context, sourcePath),
					content.Content,
					cancellationToken);
				var transformedText = compressed.Text;
				var plan = scope?.CreatePlan(
					sourcePath,
					transformedText,
					content.Content,
					compressed.Map,
					cancellationToken);
				var encoding = result.Encoding ?? TextFileEncoding.Utf8;
				var preparedPath = Path.Combine(workingDirectory, $"{index:D8}.redacted.txt");
				await WritePreparedTextAsync(
						preparedPath,
						transformedText,
						plan,
						ResolveEncoding(encoding),
						cancellationToken)
					.ConfigureAwait(false);
				preparedFiles[sourcePath] = new PreparedSecretFile(
					sourcePath,
					preparedPath,
					FileContentClassification.Text,
					encoding,
					plan?.Spans
						.Where(static span => span.State == SecretPreviewSpanState.Redacted)
						.Select(static span => new PreparedSecretSpan(span.Start, span.Length))
						.ToArray() ?? []);
			}

			var snapshot = scope?.Complete();
			var compression = transformationScope.Compression?.Complete();
			return new PreparedSecretRedactionOutput(
				workingDirectory,
				preparedFiles,
				snapshot,
				compression,
				firstUnscannablePath);
		}
		catch
		{
			DeleteWorkingDirectory(workingDirectory);
			throw;
		}
	}

	public async Task<SecretRedactionSnapshot> AnalyzeAsync(
		SecretRedactionContext context,
		IReadOnlyList<string> orderedFilePaths,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(orderedFilePaths);
		await context.Session.EnsureWarmUpAsync(cancellationToken).ConfigureAwait(false);
		var scope = context.BeginOutput(orderedFilePaths);
		var entries = new SecretScanCacheEntry?[orderedFilePaths.Count];
		var parallelWork = new List<SecretScanWorkItem>();
		var serialWork = new List<SecretScanWorkItem>();
		for (var index = 0; index < orderedFilePaths.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var sourcePath = orderedFilePaths[index];
			var metadata = SecretFileMetadata.Capture(sourcePath);
			if (scope.TryGetCachedEntry(sourcePath, metadata, out var cached))
			{
				entries[index] = cached;
				continue;
			}

			var workItem = new SecretScanWorkItem(index, sourcePath, metadata);
			if (metadata.Length <= MaximumParallelScanFileBytes)
				parallelWork.Add(workItem);
			else
				serialWork.Add(workItem);
		}

		if (parallelWork.Count > 0)
		{
			await Parallel.ForEachAsync(
				parallelWork,
				new ParallelOptions
				{
					CancellationToken = cancellationToken,
					MaxDegreeOfParallelism = Math.Min(
						MaximumParallelScans,
						Math.Max(1, Environment.ProcessorCount))
				},
				async (workItem, token) =>
				{
					entries[workItem.Index] = await AnalyzeFileAsync(scope, workItem, token)
						.ConfigureAwait(false);
				}).ConfigureAwait(false);
		}

		// Large files run alone after the small-file batch. This keeps peak raw content
		// bounded by either eight 1 MiB files or one file at the documented scan limit.
		foreach (var workItem in serialWork)
		{
			entries[workItem.Index] = await AnalyzeFileAsync(scope, workItem, cancellationToken)
				.ConfigureAwait(false);
		}

		for (var index = 0; index < entries.Length; index++)
		{
			var entry = entries[index] ??
			            throw new SecretDetectionException(
				            $"Hide Secrets produced no scan result for '{orderedFilePaths[index]}'.");
			scope.ProcessEntry(orderedFilePaths[index], entry);
		}

		return scope.Complete();
	}

	private async Task<SecretScanCacheEntry> AnalyzeFileAsync(
		SecretRedactionScope scope,
		SecretScanWorkItem workItem,
		CancellationToken cancellationToken)
	{
		var sourcePath = workItem.SourcePath;
		var metadataBeforeRead = workItem.Metadata;

		await using var contentBuffer = await contentAnalyzer
			.OpenCompleteTextBufferAsync(
				sourcePath,
				MaximumScannableFileBytes,
				cancellationToken)
			.ConfigureAwait(false);
		var metadataAfterRead = SecretFileMetadata.Capture(sourcePath);
		if (metadataBeforeRead != metadataAfterRead ||
		    contentBuffer.SizeBytes > 0 && contentBuffer.SizeBytes != metadataAfterRead.Length)
		{
			throw new SecretDetectionException(
				$"Hide Secrets could not inspect a changing file: '{sourcePath}'.");
		}

		switch (contentBuffer.Classification)
		{
			case FileContentClassification.Binary:
				return scope.StoreBinary(sourcePath, metadataAfterRead);
			case FileContentClassification.TooLarge:
				// This scan only feeds the count on the checkbox. One file it may not read is a
				// reason to leave that file out of the count, never to refuse the whole project -
				// the user asked how many secrets are here, not for a guarantee about output.
				return scope.StoreUnscannable(sourcePath, metadataAfterRead);
			case FileContentClassification.Text:
				using (scope.TrackFullContentBuffer())
				{
					return scope.Detect(
						sourcePath,
						contentBuffer.Content.Span,
						metadataAfterRead,
						cancellationToken);
				}
			default:
				throw new SecretDetectionException(
					$"Hide Secrets could not inspect '{sourcePath}' ({contentBuffer.Classification}).");
		}
	}

	private readonly record struct SecretScanWorkItem(
		int Index,
		string SourcePath,
		SecretFileMetadata Metadata);

	private static Encoding ResolveEncoding(TextFileEncoding encoding) => encoding switch
	{
		TextFileEncoding.Utf8 => new UTF8Encoding(false, true),
		TextFileEncoding.Utf8Bom => new UTF8Encoding(true, true),
		TextFileEncoding.Utf16LittleEndian => new UnicodeEncoding(false, true, true),
		TextFileEncoding.Utf16BigEndian => new UnicodeEncoding(true, true, true),
		TextFileEncoding.Utf32LittleEndian => new UTF32Encoding(false, true, true),
		TextFileEncoding.Utf32BigEndian => new UTF32Encoding(true, true, true),
		_ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, null)
	};

	private static string CreateWorkingDirectory() =>
		// CreateTempSubdirectory is atomic and creates owner-only (0700) directories on Unix.
		// A predictable shared temp parent would expose prepared output to symlink and permission races.
		Directory.CreateTempSubdirectory("DevProjex-SecretRedaction-").FullName;

	/// <summary>
	/// Relative path for the compressor's language lookup. It only needs the extension and a stable
	/// identity, so a failure to relativize falls back to the full path rather than throwing.
	/// </summary>
	private static string NormalizeRelativePath(ContentTransformationContext context, string fullPath)
	{
		var root = context.Compression?.ProjectRoot ?? context.Redaction?.ProjectRoot;
		if (string.IsNullOrEmpty(root))
			return fullPath;
		try
		{
			return Path.GetRelativePath(root, fullPath);
		}
		catch (ArgumentException)
		{
			return fullPath;
		}
	}

	private static async Task WritePreparedTextAsync(
		string path,
		string content,
		SecretFileRedactionPlan? plan,
		Encoding encoding,
		CancellationToken cancellationToken)
	{
		var options = new FileStreamOptions
		{
			Access = FileAccess.Write,
			Mode = FileMode.CreateNew,
			Share = FileShare.None,
			Options = FileOptions.Asynchronous | FileOptions.SequentialScan
		};
		if (!OperatingSystem.IsWindows())
			options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

		await using var stream = new FileStream(path, options);
		await using var writer = new StreamWriter(stream, encoding);
		if (plan is null)
		{
			await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
			return;
		}

		await plan.WriteToAsync(writer, content, cancellationToken).ConfigureAwait(false);
	}

	private static void EnsureStableRead(
		string path,
		SecretFileMetadata before,
		SecretFileMetadata after,
		TextFileContent? content)
	{
		if (before != after || (content is not null && content.SizeBytes != after.Length))
			throw new SecretDetectionException($"Hide Secrets could not inspect a changing file: '{path}'.");
	}

	private static void EnsureStableSnapshot(
		string path,
		SecretFileMetadata before,
		SecretFileMetadata after,
		FileContentMetricsResult result)
	{
		if (before != after || (result.Metrics is { } metrics && metrics.SizeBytes != after.Length))
			throw new SecretDetectionException($"Hide Secrets could not inspect a changing file: '{path}'.");
	}

	internal static void DeleteWorkingDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
				Directory.Delete(path, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// A per-match override can intentionally keep a value in the decided output, so these
			// files are private working data rather than a sanitized cache. Cleanup remains
			// best effort because an antivirus-held handle must not invalidate a completed export.
		}
	}
}

public sealed record PreparedSecretFile(
	string SourcePath,
	string ContentPath,
	FileContentClassification Classification,
	TextFileEncoding? Encoding,
	IReadOnlyList<PreparedSecretSpan> Redactions)
{
	public bool IsText => Classification == FileContentClassification.Text;

	/// <summary>
	/// The file is text, but larger than the scanner may read, so no redaction was ever planned for
	/// it. Its content must not be served from the original at full size - see the analyzer below.
	/// </summary>
	public bool IsUnscannable => Classification == FileContentClassification.TooLarge;

	public int RedactedCount => Redactions.Count;

	public int ClampLengthToCompleteRedactions(int requestedLength)
	{
		foreach (var span in Redactions)
		{
			if (span.Start < requestedLength && span.End > requestedLength)
				return span.Start;
		}

		return requestedLength;
	}

	public IReadOnlyList<PreparedSecretSpan> GetRedactionsWithin(int characterCount) =>
		Redactions.Where(span => span.End <= characterCount).ToArray();

	public static PreparedSecretFile Binary(string sourcePath) =>
		new(sourcePath, sourcePath, FileContentClassification.Binary, null, []);

	/// <summary>Text served straight from the original file, with no transformation applied.</summary>
	public static PreparedSecretFile Unchanged(string sourcePath) =>
		new(sourcePath, sourcePath, FileContentClassification.Text, null, []);

	/// <summary>Text past the scan limit: recorded, reported, and never served in full.</summary>
	public static PreparedSecretFile Unscannable(string sourcePath) =>
		new(sourcePath, sourcePath, FileContentClassification.TooLarge, null, []);
}

public sealed record PreparedSecretSpan(int Start, int Length)
{
	public int End => checked(Start + Length);
}

public sealed class PreparedSecretRedactionOutput : IAsyncDisposable
{
	private readonly string _workingDirectory;
	private readonly IReadOnlyDictionary<string, PreparedSecretFile> _files;
	private bool _disposed;

	internal PreparedSecretRedactionOutput(
		string workingDirectory,
		IReadOnlyDictionary<string, PreparedSecretFile> files,
		SecretRedactionSnapshot? snapshot,
		Compression.CodeCompressionSnapshot? compressionSnapshot = null,
		string? firstUnscannablePath = null)
	{
		_workingDirectory = workingDirectory;
		_files = files;
		Snapshot = snapshot;
		CompressionSnapshot = compressionSnapshot;
		FirstUnscannablePath = firstUnscannablePath;
	}

	/// <summary>
	/// The first selected file Hide Secrets could not read, in selection order, or null.
	///
	/// Document surfaces omit such a file and carry on. A project copy cannot: it reproduces bytes,
	/// so shipping one would hand over text the scanner never saw. That surface refuses instead.
	/// </summary>
	public string? FirstUnscannablePath { get; }

	/// <summary>Null when redaction was not part of this run - compression can be enabled alone.</summary>
	public SecretRedactionSnapshot? Snapshot { get; }

	public Compression.CodeCompressionSnapshot? CompressionSnapshot { get; }

	public PreparedSecretFile GetFile(string sourcePath)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		return _files.TryGetValue(sourcePath, out var file)
			? file
			: throw new KeyNotFoundException($"No prepared redaction entry exists for '{sourcePath}'.");
	}

	public ValueTask DisposeAsync()
	{
		if (_disposed)
			return ValueTask.CompletedTask;
		_disposed = true;
		SecretRedactionOutputPreparer.DeleteWorkingDirectory(_workingDirectory);
		return ValueTask.CompletedTask;
	}
}

/// <summary>
/// Presents prepared text under its original path identity. Document serializers continue to use
/// source-relative headers while all content reads are redirected to the redacted snapshot.
/// </summary>
public sealed class PreparedSecretFileContentAnalyzer(
	IFileContentAnalyzer inner,
	PreparedSecretRedactionOutput prepared) : IFileContentAnalyzer
{
	/// <summary>
	/// An unscannable file is read from its original path, exactly as it would be with Hide Secrets
	/// off, but never with a larger budget than the scanner itself was given. The file is bigger
	/// than that budget by definition, so every read comes back estimated with empty text: the
	/// surfaces omit it for the same reason they already do, and no unscanned character can escape
	/// through a caller that happened to ask for a generous limit.
	/// </summary>
	private long ClampReadLimit(PreparedSecretFile file, long requested) =>
		file.IsUnscannable
			? Math.Min(requested, SecretRedactionOutputPreparer.MaximumScannableFileBytes)
			: requested;

	public FileContentClassification? ClassifyWithoutReading(string path)
	{
		var file = prepared.GetFile(path);
		if (file.IsUnscannable)
			return FileContentClassification.TooLarge;
		return file.IsText ? null : FileContentClassification.Binary;
	}

	public ValueTask<FileContentReadResult> ReadClassifiedAsync(
		string path,
		long maxSizeForFullRead,
		CancellationToken cancellationToken = default)
	{
		var file = prepared.GetFile(path);
		return file.IsText || file.IsUnscannable
			? inner.ReadClassifiedAsync(
				file.ContentPath,
				ClampReadLimit(file, maxSizeForFullRead),
				cancellationToken)
			: ValueTask.FromResult(new FileContentReadResult(FileContentClassification.Binary));
	}

	public ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default)
	{
		var file = prepared.GetFile(path);
		return file.IsText || file.IsUnscannable
			? inner.IsTextFileAsync(file.ContentPath, cancellationToken)
			: ValueTask.FromResult(false);
	}

	public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		var file = prepared.GetFile(path);
		return file.IsText || file.IsUnscannable
			? inner.GetTextFileMetricsAsync(file.ContentPath, cancellationToken)
			: ValueTask.FromResult<TextFileMetrics?>(null);
	}

	public ValueTask<FileContentMetricsResult> GetClassifiedMetricsAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		var file = prepared.GetFile(path);
		return file.IsText || file.IsUnscannable
			? inner.GetClassifiedMetricsAsync(file.ContentPath, cancellationToken)
			: ValueTask.FromResult(new FileContentMetricsResult(FileContentClassification.Binary));
	}

	public async ValueTask<IFileContentSnapshot> OpenCompleteSnapshotAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		var file = prepared.GetFile(path);
		if (!file.IsText && !file.IsUnscannable)
			return new BinarySnapshot();
		// The default snapshot reads with no limit at all, which is exactly the budget an
		// unscannable file may not be read with. Streamed metrics stay available; text does not.
		if (file.IsUnscannable)
		{
			return new UnscannableSnapshot(
				await inner.GetClassifiedMetricsAsync(file.ContentPath, cancellationToken)
					.ConfigureAwait(false));
		}

		return await inner.OpenCompleteSnapshotAsync(file.ContentPath, cancellationToken)
			.ConfigureAwait(false);
	}

	public ValueTask<TextFileContent?> TryReadAsTextAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		var file = prepared.GetFile(path);
		if (file.IsUnscannable)
		{
			return inner.TryReadAsTextAsync(
				file.ContentPath,
				SecretRedactionOutputPreparer.MaximumScannableFileBytes,
				cancellationToken);
		}

		return file.IsText
			? inner.TryReadAsTextAsync(file.ContentPath, cancellationToken)
			: ValueTask.FromResult<TextFileContent?>(null);
	}

	public ValueTask<TextFileContent?> TryReadAsTextAsync(
		string path,
		long maxSizeForFullRead,
		CancellationToken cancellationToken = default)
	{
		var file = prepared.GetFile(path);
		return file.IsText || file.IsUnscannable
			? inner.TryReadAsTextAsync(
				file.ContentPath,
				ClampReadLimit(file, maxSizeForFullRead),
				cancellationToken)
			: ValueTask.FromResult<TextFileContent?>(null);
	}

	private sealed class UnscannableSnapshot(FileContentMetricsResult result) : IFileContentSnapshot
	{
		public FileContentMetricsResult Result { get; } =
			new(FileContentClassification.TooLarge, result.Metrics);

		public ValueTask CopyTextToAsync(
			int maximumCharacters,
			Func<ReadOnlyMemory<char>, CancellationToken, ValueTask> writeChunk,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException(
				new IOException("The file is past the Hide Secrets scan limit and was not read."));

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class BinarySnapshot : IFileContentSnapshot
	{
		public FileContentMetricsResult Result { get; } =
			new(FileContentClassification.Binary);

		public ValueTask CopyTextToAsync(
			int maximumCharacters,
			Func<ReadOnlyMemory<char>, CancellationToken, ValueTask> writeChunk,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException(new IOException("A binary snapshot has no text content."));

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
