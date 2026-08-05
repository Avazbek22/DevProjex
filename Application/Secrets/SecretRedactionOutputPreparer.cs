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

	public async Task<PreparedSecretRedactionOutput> PrepareAsync(
		SecretRedactionContext context,
		IReadOnlyList<string> orderedFilePaths,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(orderedFilePaths);

		var workingDirectory = CreateWorkingDirectory();
		var preparedFiles = new Dictionary<string, PreparedSecretFile>(PathComparer.Default);
		var scope = context.BeginOutput(orderedFilePaths);
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
						scope.AnalyzeBinary(sourcePath, metadataAfterRead);
						preparedFiles[sourcePath] = PreparedSecretFile.Binary(sourcePath);
						continue;
					case FileContentClassification.TooLarge:
						throw new SecretScanLimitExceededException(
							sourcePath,
							result.Content?.SizeBytes ?? new FileInfo(sourcePath).Length,
							MaximumScannableFileBytes);
					case FileContentClassification.Text:
						break;
					default:
						throw new SecretDetectionException(
							$"Hide Secrets could not inspect '{sourcePath}' ({result.Classification}).");
				}

				var content = result.Content ??
				              throw new SecretDetectionException(
					              $"Hide Secrets received no text for '{sourcePath}'.");
				using var contentLease = context.Session.TrackFullContentBuffer();
				var plan = scope.CreatePlan(sourcePath, content.Content, cancellationToken);
				var encoding = result.Encoding ?? TextFileEncoding.Utf8;
				var preparedPath = Path.Combine(workingDirectory, $"{index:D8}.redacted.txt");
				await WritePreparedTextAsync(
						preparedPath,
						content.Content,
						plan,
						ResolveEncoding(encoding),
						cancellationToken)
					.ConfigureAwait(false);
				preparedFiles[sourcePath] = new PreparedSecretFile(
					sourcePath,
					preparedPath,
					FileContentClassification.Text,
					encoding,
					plan.Spans
						.Where(static span => span.State == SecretPreviewSpanState.Redacted)
						.Select(static span => new PreparedSecretSpan(span.Start, span.Length))
						.ToArray());
			}

			var snapshot = scope.Complete();
			return new PreparedSecretRedactionOutput(
				workingDirectory,
				preparedFiles,
				snapshot,
				scope.PlaceholderExample,
				scope.LegendText);
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
				throw new SecretScanLimitExceededException(
					sourcePath,
					contentBuffer.SizeBytes > 0 ? contentBuffer.SizeBytes : metadataAfterRead.Length,
					MaximumScannableFileBytes);
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

	private static async Task WritePreparedTextAsync(
		string path,
		string content,
		SecretFileRedactionPlan plan,
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
		SecretRedactionSnapshot snapshot,
		string? placeholderExample,
		SecretRedactionLegendText legendText)
	{
		_workingDirectory = workingDirectory;
		_files = files;
		Snapshot = snapshot;
		PlaceholderExample = placeholderExample;
		LegendText = legendText;
	}

	public SecretRedactionSnapshot Snapshot { get; }
	public string? PlaceholderExample { get; }
	public SecretRedactionLegendText LegendText { get; }

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
	public FileContentClassification? ClassifyWithoutReading(string path)
	{
		var file = prepared.GetFile(path);
		return file.IsText ? null : FileContentClassification.Binary;
	}

	public ValueTask<FileContentReadResult> ReadClassifiedAsync(
		string path,
		long maxSizeForFullRead,
		CancellationToken cancellationToken = default)
	{
		var file = prepared.GetFile(path);
		return file.IsText
			? inner.ReadClassifiedAsync(file.ContentPath, maxSizeForFullRead, cancellationToken)
			: ValueTask.FromResult(new FileContentReadResult(FileContentClassification.Binary));
	}

	public ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default)
	{
		var file = prepared.GetFile(path);
		return file.IsText
			? inner.IsTextFileAsync(file.ContentPath, cancellationToken)
			: ValueTask.FromResult(false);
	}

	public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		var file = prepared.GetFile(path);
		return file.IsText
			? inner.GetTextFileMetricsAsync(file.ContentPath, cancellationToken)
			: ValueTask.FromResult<TextFileMetrics?>(null);
	}

	public ValueTask<FileContentMetricsResult> GetClassifiedMetricsAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		var file = prepared.GetFile(path);
		return file.IsText
			? inner.GetClassifiedMetricsAsync(file.ContentPath, cancellationToken)
			: ValueTask.FromResult(new FileContentMetricsResult(FileContentClassification.Binary));
	}

	public ValueTask<IFileContentSnapshot> OpenCompleteSnapshotAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		var file = prepared.GetFile(path);
		return file.IsText
			? inner.OpenCompleteSnapshotAsync(file.ContentPath, cancellationToken)
			: ValueTask.FromResult<IFileContentSnapshot>(new BinarySnapshot());
	}

	public ValueTask<TextFileContent?> TryReadAsTextAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		var file = prepared.GetFile(path);
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
		return file.IsText
			? inner.TryReadAsTextAsync(file.ContentPath, maxSizeForFullRead, cancellationToken)
			: ValueTask.FromResult<TextFileContent?>(null);
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
