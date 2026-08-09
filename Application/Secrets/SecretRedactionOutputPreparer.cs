using System.Runtime.CompilerServices;

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
	// Parallel scanning applies only to files at or below the per-file parallel limit, so the
	// worst-case buffered content is MaximumParallelScans * 1 MiB. Scaling with the machine keeps
	// wide desktops from idling behind a fixed cap while small machines are not oversubscribed.
	public static readonly int MaximumParallelScans = Math.Clamp(Environment.ProcessorCount, 4, 16);

	public IFileContentAnalyzer CreatePreparedAnalyzer(PreparedSecretRedactionOutput prepared)
	{
		ArgumentNullException.ThrowIfNull(prepared);
		return new PreparedSecretFileContentAnalyzer(contentAnalyzer, prepared);
	}

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
		if (context is { Compression: not null, Redaction: null })
		{
			return await PrepareCompressionOnlyAsync(context, orderedFilePaths, cancellationToken)
				.ConfigureAwait(false);
		}

		if (context.Redaction is { } redactionContext)
			await redactionContext.Session.EnsureWarmUpAsync(cancellationToken).ConfigureAwait(false);

		string? workingDirectory = null;
		var preparedFiles = new Dictionary<string, PreparedSecretFile>(PathComparer.Default);
		var unscannablePaths = new List<string>();
		using var transformationScope = context.BeginOutput(orderedFilePaths);
		var scope = transformationScope.Redaction;
		try
		{
			await foreach (var prepared in PrepareOrderedTransformationEntriesAsync(
			                   context,
			                   transformationScope,
			                   orderedFilePaths,
			                   cancellationToken).ConfigureAwait(false))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var sourcePath = prepared.SourcePath;
				var result = prepared.ReadResult;
				var metadataAfterRead = prepared.Metadata;

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
						// this large anyway, so nothing that used to ship stops shipping. A project
						// copy leaves it out and names it in the notice, because copying the bytes
						// would hand over text the scanner never saw.
						if (scope is not null)
						{
							scope.AnalyzeUnscannable(sourcePath, metadataAfterRead);
							preparedFiles[sourcePath] = PreparedSecretFile.Unscannable(sourcePath);
							unscannablePaths.Add(sourcePath);
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
				// Compression first: the redaction plan, its spans and its counts all describe the
				// text that actually leaves, not the text on disk.
				var compressed = prepared.Compression;
				var transformedText = compressed.Text;
				var plan = scope?.CreatePlan(
					sourcePath,
					transformedText,
					compressed.Map,
					cancellationToken);
				var redactions = plan?.Spans
					.Where(static span => span.State == SecretPreviewSpanState.Redacted)
					.Select(static span => new PreparedSecretSpan(span.Start, span.Length))
					.ToArray() ?? [];
				if (ReferenceEquals(transformedText, content.Content) && redactions.Length == 0)
				{
					preparedFiles[sourcePath] = PreparedSecretFile.Unchanged(sourcePath);
					continue;
				}

				var encoding = result.Encoding ?? TextFileEncoding.Utf8;
				workingDirectory ??= CreateWorkingDirectory();
				var preparedPath = Path.Combine(workingDirectory, $"{prepared.Index:D8}.redacted.txt");
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
					redactions);
			}

			var snapshot = scope?.Complete(unscannablePaths.Count, failedFileCount: 0);
			var compression = transformationScope.Compression?.Complete();
			return new PreparedSecretRedactionOutput(
				workingDirectory,
				preparedFiles,
				snapshot,
				compression,
				unscannablePaths);
		}
		catch
		{
			if (workingDirectory is not null)
				DeleteWorkingDirectory(workingDirectory);
			throw;
		}
	}

	private async IAsyncEnumerable<PreparedTransformationEntry> PrepareOrderedTransformationEntriesAsync(
		ContentTransformationContext context,
		ContentTransformationScope transformationScope,
		IReadOnlyList<string> orderedFilePaths,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		var batch = new List<CompressionWorkItem>(MaximumParallelScans);
		for (var index = 0; index < orderedFilePaths.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var item = new CompressionWorkItem(index, orderedFilePaths[index]);
			if (SecretFileMetadata.Capture(item.SourcePath).Length > MaximumParallelScanFileBytes)
			{
				await foreach (var prepared in PrepareTransformationBatchAsync(
				                   context,
				                   transformationScope,
				                   batch,
				                   cancellationToken).ConfigureAwait(false))
				{
					yield return prepared;
				}
				batch.Clear();
				yield return await PrepareTransformationEntryAsync(
					context,
					transformationScope,
					item,
					cancellationToken).ConfigureAwait(false);
				continue;
			}

			batch.Add(item);
			if (batch.Count < MaximumParallelScans)
				continue;
			await foreach (var prepared in PrepareTransformationBatchAsync(
			                   context,
			                   transformationScope,
			                   batch,
			                   cancellationToken).ConfigureAwait(false))
			{
				yield return prepared;
			}
			batch.Clear();
		}

		await foreach (var prepared in PrepareTransformationBatchAsync(
		                   context,
		                   transformationScope,
		                   batch,
		                   cancellationToken).ConfigureAwait(false))
		{
			yield return prepared;
		}
	}

	private async IAsyncEnumerable<PreparedTransformationEntry> PrepareTransformationBatchAsync(
		ContentTransformationContext context,
		ContentTransformationScope transformationScope,
		IReadOnlyList<CompressionWorkItem> items,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		if (items.Count == 0)
			yield break;

		var tasks = items
			.Select(item => Task.Run(
				() => PrepareTransformationEntryAsync(
					context,
					transformationScope,
					item,
					cancellationToken),
				cancellationToken))
			.ToArray();
		PreparedTransformationEntry[] entries;
		try
		{
			entries = await Task.WhenAll(tasks).ConfigureAwait(false);
		}
		catch
		{
			foreach (var task in tasks)
			{
				if (task.IsCompletedSuccessfully)
					task.Result.Dispose();
			}
			throw;
		}

		var next = 0;
		try
		{
			for (; next < entries.Length; next++)
			{
				yield return entries[next];
				entries[next].Dispose();
			}
		}
		finally
		{
			for (; next < entries.Length; next++)
				entries[next].Dispose();
		}
	}

	private async Task<PreparedTransformationEntry> PrepareTransformationEntryAsync(
		ContentTransformationContext context,
		ContentTransformationScope transformationScope,
		CompressionWorkItem item,
		CancellationToken cancellationToken)
	{
		var metadataBeforeRead = SecretFileMetadata.Capture(item.SourcePath);
		var result = await contentAnalyzer
			.ReadClassifiedAsync(item.SourcePath, MaximumScannableFileBytes, cancellationToken)
			.ConfigureAwait(false);
		var metadataAfterRead = SecretFileMetadata.Capture(item.SourcePath);
		EnsureStableRead(item.SourcePath, metadataBeforeRead, metadataAfterRead, result.Content);
		IDisposable? contentLease = null;
		try
		{
			CodeCompressionResult compression;
			if (result.Classification == FileContentClassification.Text && result.Content is not null)
			{
				contentLease = context.Redaction?.Session.TrackFullContentBuffer();
				compression = transformationScope.Compress(
					item.SourcePath,
					NormalizeRelativePath(context, item.SourcePath),
					result.Content.Content,
					cancellationToken);
			}
			else
			{
				compression = new CodeCompressionResult(
					result.Content?.Content ?? string.Empty,
					ContentTransformMap.Identity);
			}

			return new PreparedTransformationEntry(
				item.Index,
				item.SourcePath,
				metadataAfterRead,
				result,
				compression,
				contentLease);
		}
		catch
		{
			contentLease?.Dispose();
			throw;
		}
	}

	private async Task<PreparedSecretRedactionOutput> PrepareCompressionOnlyAsync(
		ContentTransformationContext context,
		IReadOnlyList<string> orderedFilePaths,
		CancellationToken cancellationToken)
	{
		var workingDirectory = new Lazy<string>(
			CreateWorkingDirectory,
			LazyThreadSafetyMode.ExecutionAndPublication);
		var prepared = new PreparedSecretFile?[orderedFilePaths.Count];
		var parallelWork = new List<CompressionWorkItem>();
		var serialWork = new List<CompressionWorkItem>();
		for (var index = 0; index < orderedFilePaths.Count; index++)
		{
			var workItem = new CompressionWorkItem(index, orderedFilePaths[index]);
			if (SecretFileMetadata.Capture(workItem.SourcePath).Length <= MaximumParallelScanFileBytes)
				parallelWork.Add(workItem);
			else
				serialWork.Add(workItem);
		}

		using var transformationScope = context.BeginOutput(orderedFilePaths);
		try
		{
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
						prepared[workItem.Index] = await PrepareCompressedFileAsync(
							context,
							transformationScope,
							workingDirectory,
							workItem,
							token).ConfigureAwait(false);
					}).ConfigureAwait(false);
			}

			foreach (var workItem in serialWork)
			{
				prepared[workItem.Index] = await PrepareCompressedFileAsync(
					context,
					transformationScope,
					workingDirectory,
					workItem,
					cancellationToken).ConfigureAwait(false);
			}

			var preparedFiles = new Dictionary<string, PreparedSecretFile>(
				orderedFilePaths.Count,
				PathComparer.Default);
			for (var index = 0; index < orderedFilePaths.Count; index++)
			{
				preparedFiles.Add(
					orderedFilePaths[index],
					prepared[index] ?? throw new InvalidOperationException(
						$"Code compression produced no result for '{orderedFilePaths[index]}'."));
			}

			var snapshot = transformationScope.Compression?.Complete();
			return new PreparedSecretRedactionOutput(
				workingDirectory.IsValueCreated ? workingDirectory.Value : null,
				preparedFiles,
				snapshot: null,
				compressionSnapshot: snapshot);
		}
		catch
		{
			if (workingDirectory.IsValueCreated)
				DeleteWorkingDirectory(workingDirectory.Value);
			throw;
		}
	}

	private async Task<PreparedSecretFile> PrepareCompressedFileAsync(
		ContentTransformationContext context,
		ContentTransformationScope transformationScope,
		Lazy<string> workingDirectory,
		CompressionWorkItem workItem,
		CancellationToken cancellationToken)
	{
		var sourcePath = workItem.SourcePath;
		var metadataBeforeRead = SecretFileMetadata.Capture(sourcePath);
		var result = await contentAnalyzer
			.ReadClassifiedAsync(sourcePath, MaximumScannableFileBytes, cancellationToken)
			.ConfigureAwait(false);
		var metadataAfterRead = SecretFileMetadata.Capture(sourcePath);
		EnsureStableRead(sourcePath, metadataBeforeRead, metadataAfterRead, result.Content);

		if (result.Classification != FileContentClassification.Text)
		{
			return result.Classification == FileContentClassification.TooLarge
				? PreparedSecretFile.Unchanged(sourcePath)
				: new PreparedSecretFile(
					sourcePath,
					sourcePath,
					result.Classification,
					null,
					[]);
		}

		if (result.Content is null)
		{
			throw new SecretDetectionException(
				$"Code compression could not inspect '{sourcePath}' ({result.Classification}).");
		}

		var compressed = transformationScope.Compress(
			sourcePath,
			NormalizeRelativePath(context, sourcePath),
			result.Content.Content,
			cancellationToken);
		if (ReferenceEquals(compressed.Text, result.Content.Content))
			return PreparedSecretFile.Unchanged(sourcePath);

		var encoding = result.Encoding ?? TextFileEncoding.Utf8;
		var preparedPath = Path.Combine(workingDirectory.Value, $"{workItem.Index:D8}.compressed.txt");
		await WritePreparedTextAsync(
				preparedPath,
				compressed.Text,
				plan: null,
				ResolveEncoding(encoding),
				cancellationToken)
			.ConfigureAwait(false);
		return new PreparedSecretFile(
			sourcePath,
			preparedPath,
			FileContentClassification.Text,
			encoding,
			[]);
	}

	private readonly record struct CompressionWorkItem(int Index, string SourcePath);

	private sealed class PreparedTransformationEntry(
		int index,
		string sourcePath,
		SecretFileMetadata metadata,
		FileContentReadResult readResult,
		CodeCompressionResult compression,
		IDisposable? contentLease) : IDisposable
	{
		private IDisposable? _contentLease = contentLease;

		public int Index { get; } = index;
		public string SourcePath { get; } = sourcePath;
		public SecretFileMetadata Metadata { get; } = metadata;
		public FileContentReadResult ReadResult { get; } = readResult;
		public CodeCompressionResult Compression { get; } = compression;

		public void Dispose() => Interlocked.Exchange(ref _contentLease, null)?.Dispose();
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
				// Findings and binary classification both depend on content, so equal filesystem
				// metadata is only a candidate. Re-reading once lets Detect compare the cached
				// fingerprint without rerunning detector rules or reading a changed file twice.
				// A file above the hard scan limit remains unscannable while its length is unchanged.
				if (cached.IsUnscannable)
				{
					entries[index] = cached;
					continue;
				}
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

		var skippedFileCount = 0;
		for (var index = 0; index < entries.Length; index++)
		{
			var entry = entries[index] ??
			            throw new SecretDetectionException(
				            $"Hide Secrets produced no scan result for '{orderedFilePaths[index]}'.");
			scope.ProcessEntry(orderedFilePaths[index], entry);
			if (entry.IsUnscannable)
				skippedFileCount++;
		}

		return scope.Complete(skippedFileCount, failedFileCount: 0);
	}

	/// <summary>
	/// Discovers whether the current selection contains secrets without weakening output safety.
	/// Files that disappear, change, or cannot be read are counted as failures while the remaining
	/// files are still inspected. Text beyond the interactive scan limit is reported separately as
	/// skipped, because a deliberate resource bound is not an engine failure. Preview and export
	/// callers intentionally continue to use the strict
	/// <see cref="AnalyzeAsync(SecretRedactionContext,IReadOnlyList{string},CancellationToken)"/> path.
	/// </summary>
	public Task<SecretRedactionSnapshot> DiscoverAsync(
		SecretRedactionContext context,
		IReadOnlyList<string> orderedFilePaths,
		CancellationToken cancellationToken = default) =>
		DiscoverAsync(
			context,
			orderedFilePaths,
			SecretDiscoveryCacheMode.RevalidateContent,
			cancellationToken);

	public async Task<SecretRedactionSnapshot> DiscoverAsync(
		SecretRedactionContext context,
		IReadOnlyList<string> orderedFilePaths,
		SecretDiscoveryCacheMode cacheMode,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(orderedFilePaths);
		await context.Session.EnsureWarmUpAsync(cancellationToken).ConfigureAwait(false);
		var scope = context.BeginOutput(orderedFilePaths);
		var entries = new SecretScanCacheEntry?[orderedFilePaths.Count];
		var outcomes = new SecretDiscoveryFileOutcome[orderedFilePaths.Count];
		var parallelWork = new List<SecretScanWorkItem>();
		var serialWork = new List<SecretScanWorkItem>();

		for (var index = 0; index < orderedFilePaths.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var sourcePath = orderedFilePaths[index];
			SecretFileMetadata metadata;
			try
			{
				metadata = SecretFileMetadata.Capture(sourcePath);
			}
			catch (Exception exception) when (IsRecoverableDiscoveryFailure(exception))
			{
				outcomes[index] = SecretDiscoveryFileOutcome.Failed;
				continue;
			}

			if (scope.TryGetCachedEntry(sourcePath, metadata, out var cached) &&
			    (cacheMode == SecretDiscoveryCacheMode.ReuseValidatedContent || cached.IsUnscannable))
			{
				entries[index] = cached;
				if (cached.IsUnscannable)
					outcomes[index] = SecretDiscoveryFileOutcome.SkippedByLimit;
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
					try
					{
						entries[workItem.Index] = await AnalyzeFileAsync(scope, workItem, token)
							.ConfigureAwait(false);
					}
					catch (Exception exception) when (IsRecoverableDiscoveryFailure(exception))
					{
						outcomes[workItem.Index] = SecretDiscoveryFileOutcome.Failed;
					}
				}).ConfigureAwait(false);
		}

		foreach (var workItem in serialWork)
		{
			try
			{
				entries[workItem.Index] = await AnalyzeFileAsync(scope, workItem, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (Exception exception) when (IsRecoverableDiscoveryFailure(exception))
			{
				outcomes[workItem.Index] = SecretDiscoveryFileOutcome.Failed;
			}
		}

		var skippedFileCount = 0;
		var failedFileCount = 0;
		for (var index = 0; index < entries.Length; index++)
		{
			var entry = entries[index];
			if (entry is not null)
			{
				scope.ProcessEntry(orderedFilePaths[index], entry);
				if (entry.IsUnscannable)
					outcomes[index] = SecretDiscoveryFileOutcome.SkippedByLimit;
			}

			switch (outcomes[index])
			{
				case SecretDiscoveryFileOutcome.SkippedByLimit:
					skippedFileCount++;
					break;
				case SecretDiscoveryFileOutcome.Failed:
					failedFileCount++;
					break;
			}
		}

		return scope.Complete(skippedFileCount, failedFileCount);
	}

	public async Task<SecretRedactionSnapshot> AnalyzeAsync(
		ContentTransformationContext context,
		IReadOnlyList<string> orderedFilePaths,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(orderedFilePaths);
		var redactionContext = context.Redaction ??
		                       throw new ArgumentException(
			                       "The transformation context must include secret redaction.",
			                       nameof(context));
		if (context.Compression is null)
		{
			return await AnalyzeAsync(redactionContext, orderedFilePaths, cancellationToken)
				.ConfigureAwait(false);
		}

		await redactionContext.Session.EnsureWarmUpAsync(cancellationToken).ConfigureAwait(false);
		using var transformationScope = context.BeginOutput(orderedFilePaths);
		var redactionScope = transformationScope.Redaction ??
		                     throw new InvalidOperationException(
			                     "The transformation scope did not create secret redaction state.");
		var skippedFileCount = 0;

		await foreach (var prepared in PrepareOrderedTransformationEntriesAsync(
		                   context,
		                   transformationScope,
		                   orderedFilePaths,
		                   cancellationToken).ConfigureAwait(false))
		{
			cancellationToken.ThrowIfCancellationRequested();
			switch (prepared.ReadResult.Classification)
			{
				case FileContentClassification.Binary:
					redactionScope.AnalyzeBinary(prepared.SourcePath, prepared.Metadata);
					break;
				case FileContentClassification.TooLarge:
					redactionScope.AnalyzeUnscannable(prepared.SourcePath, prepared.Metadata);
					skippedFileCount++;
					break;
				case FileContentClassification.Text:
					redactionScope.CreatePlan(
						prepared.SourcePath,
						prepared.Compression.Text,
						prepared.Compression.Map,
						cancellationToken);
					break;
				default:
					throw new SecretDetectionException(
						$"Hide Secrets could not inspect '{prepared.SourcePath}' " +
						$"({prepared.ReadResult.Classification}).");
			}
		}

		// Analysis borrows the compression pipeline but is not itself a transformed output. Publishing
		// its auxiliary snapshot would make compression statistics appear before Preview or export ran.
		return redactionScope.Complete(skippedFileCount, failedFileCount: 0);
	}

	public Task<SecretRedactionSnapshot> DiscoverAsync(
		ContentTransformationContext context,
		IReadOnlyList<string> orderedFilePaths,
		CancellationToken cancellationToken = default) =>
		DiscoverAsync(
			context,
			orderedFilePaths,
			SecretDiscoveryCacheMode.RevalidateContent,
			cancellationToken);

	public async Task<SecretRedactionSnapshot> DiscoverAsync(
		ContentTransformationContext context,
		IReadOnlyList<string> orderedFilePaths,
		SecretDiscoveryCacheMode cacheMode,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(orderedFilePaths);
		var redactionContext = context.Redaction ??
		                       throw new ArgumentException(
			                       "The transformation context must include secret redaction.",
			                       nameof(context));
		if (context.Compression is null)
		{
			return await DiscoverAsync(redactionContext, orderedFilePaths, cacheMode, cancellationToken)
				.ConfigureAwait(false);
		}

		await redactionContext.Session.EnsureWarmUpAsync(cancellationToken).ConfigureAwait(false);
		using var transformationScope = context.BeginOutput(orderedFilePaths);
		var redactionScope = transformationScope.Redaction ??
		                     throw new InvalidOperationException(
			                     "The transformation scope did not create secret redaction state.");
		var skippedFileCount = 0;
		var failedFileCount = 0;

		await foreach (var attempt in PrepareOrderedDiscoveryEntriesAsync(
		                   context,
		                   transformationScope,
		                   redactionScope,
		                   orderedFilePaths,
		                   cacheMode,
		                   cancellationToken).ConfigureAwait(false))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (attempt.CachedEntry is not null)
			{
				redactionScope.ProcessEntry(attempt.SourcePath!, attempt.CachedEntry);
				if (attempt.CachedEntry.IsUnscannable)
					skippedFileCount++;
				continue;
			}

			if (attempt.Entry is null)
			{
				failedFileCount++;
				continue;
			}

			var prepared = attempt.Entry;
			switch (prepared.ReadResult.Classification)
			{
				case FileContentClassification.Binary:
					redactionScope.AnalyzeBinary(prepared.SourcePath, prepared.Metadata);
					break;
				case FileContentClassification.TooLarge:
					redactionScope.AnalyzeUnscannable(prepared.SourcePath, prepared.Metadata);
					skippedFileCount++;
					break;
				case FileContentClassification.Text:
					redactionScope.CreatePlan(
						prepared.SourcePath,
						prepared.Compression.Text,
						prepared.Compression.Map,
						cancellationToken);
					break;
				default:
					failedFileCount++;
					break;
			}
		}

		return redactionScope.Complete(skippedFileCount, failedFileCount);
	}

	private async IAsyncEnumerable<DiscoveryTransformationAttempt> PrepareOrderedDiscoveryEntriesAsync(
		ContentTransformationContext context,
		ContentTransformationScope transformationScope,
		SecretRedactionScope redactionScope,
		IReadOnlyList<string> orderedFilePaths,
		SecretDiscoveryCacheMode cacheMode,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		var batch = new List<CompressionWorkItem>(MaximumParallelScans);
		for (var index = 0; index < orderedFilePaths.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var item = new CompressionWorkItem(index, orderedFilePaths[index]);
			SecretFileMetadata? metadata = null;
			try
			{
				metadata = SecretFileMetadata.Capture(item.SourcePath);
			}
			catch (Exception exception) when (IsRecoverableDiscoveryFailure(exception))
			{
				// Iterator blocks cannot yield from catch clauses. The null value is handled below.
			}
			if (metadata is null)
			{
				await foreach (var attempt in PrepareDiscoveryBatchAsync(
				                   context,
				                   transformationScope,
				                   batch,
				                   cancellationToken).ConfigureAwait(false))
				{
					yield return attempt;
				}
				batch.Clear();
				yield return DiscoveryTransformationAttempt.Incomplete();
				continue;
			}

			if (redactionScope.TryGetCachedEntry(item.SourcePath, metadata.Value, out var cached) &&
			    (cacheMode == SecretDiscoveryCacheMode.ReuseValidatedContent || cached.IsUnscannable))
			{
				await foreach (var attempt in PrepareDiscoveryBatchAsync(
				                   context,
				                   transformationScope,
				                   batch,
				                   cancellationToken).ConfigureAwait(false))
				{
					yield return attempt;
				}
				batch.Clear();
				yield return DiscoveryTransformationAttempt.Cached(item.SourcePath, cached);
				continue;
			}

			if (metadata.Value.Length > MaximumParallelScanFileBytes)
			{
				await foreach (var attempt in PrepareDiscoveryBatchAsync(
				                   context,
				                   transformationScope,
				                   batch,
				                   cancellationToken).ConfigureAwait(false))
				{
					yield return attempt;
				}
				batch.Clear();
				yield return await TryPrepareDiscoveryEntryAsync(
					context,
					transformationScope,
					item,
					cancellationToken).ConfigureAwait(false);
				continue;
			}

			batch.Add(item);
			if (batch.Count < MaximumParallelScans)
				continue;

			await foreach (var attempt in PrepareDiscoveryBatchAsync(
			                   context,
			                   transformationScope,
			                   batch,
			                   cancellationToken).ConfigureAwait(false))
			{
				yield return attempt;
			}
			batch.Clear();
		}

		await foreach (var attempt in PrepareDiscoveryBatchAsync(
		                   context,
		                   transformationScope,
		                   batch,
		                   cancellationToken).ConfigureAwait(false))
		{
			yield return attempt;
		}
	}

	private async IAsyncEnumerable<DiscoveryTransformationAttempt> PrepareDiscoveryBatchAsync(
		ContentTransformationContext context,
		ContentTransformationScope transformationScope,
		IReadOnlyList<CompressionWorkItem> items,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		if (items.Count == 0)
			yield break;

		var tasks = items
			.Select(item => TryPrepareDiscoveryEntryAsync(
					context,
					transformationScope,
					item,
					cancellationToken))
			.ToArray();
		DiscoveryTransformationAttempt[] attempts;
		try
		{
			attempts = await Task.WhenAll(tasks).ConfigureAwait(false);
		}
		catch
		{
			foreach (var task in tasks)
			{
				if (task.IsCompletedSuccessfully)
					task.Result.Dispose();
			}
			throw;
		}
		var next = 0;
		try
		{
			for (; next < attempts.Length; next++)
			{
				yield return attempts[next];
				attempts[next].Dispose();
			}
		}
		finally
		{
			for (; next < attempts.Length; next++)
				attempts[next].Dispose();
		}
	}

	private async Task<DiscoveryTransformationAttempt> TryPrepareDiscoveryEntryAsync(
		ContentTransformationContext context,
		ContentTransformationScope transformationScope,
		CompressionWorkItem item,
		CancellationToken cancellationToken)
	{
		try
		{
			var entry = await PrepareTransformationEntryAsync(
				context,
				transformationScope,
				item,
				cancellationToken).ConfigureAwait(false);
			return DiscoveryTransformationAttempt.Prepared(entry);
		}
		catch (Exception exception) when (IsRecoverableDiscoveryFailure(exception))
		{
			return DiscoveryTransformationAttempt.Incomplete();
		}
	}

	private static bool IsRecoverableDiscoveryFailure(Exception exception) =>
		exception is IOException or UnauthorizedAccessException or SecretDetectionException or DecoderFallbackException;

	private readonly record struct DiscoveryTransformationAttempt(
		PreparedTransformationEntry? Entry,
		string? SourcePath,
		SecretScanCacheEntry? CachedEntry) : IDisposable
	{
		public static DiscoveryTransformationAttempt Prepared(PreparedTransformationEntry entry) =>
			new(entry, null, null);

		public static DiscoveryTransformationAttempt Cached(
			string sourcePath,
			SecretScanCacheEntry entry) =>
			new(null, sourcePath, entry);

		public static DiscoveryTransformationAttempt Incomplete() => new(null, null, null);

		public void Dispose() => Entry?.Dispose();
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

	private enum SecretDiscoveryFileOutcome : byte
	{
		None = 0,
		SkippedByLimit = 1,
		Failed = 2
	}

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
	private readonly string? _workingDirectory;
	private readonly IReadOnlyDictionary<string, PreparedSecretFile> _files;
	private bool _disposed;

	internal PreparedSecretRedactionOutput(
		string? workingDirectory,
		IReadOnlyDictionary<string, PreparedSecretFile> files,
		SecretRedactionSnapshot? snapshot,
		Compression.CodeCompressionSnapshot? compressionSnapshot = null,
		IReadOnlyList<string>? unscannablePaths = null)
	{
		_workingDirectory = workingDirectory;
		_files = files;
		Snapshot = snapshot;
		CompressionSnapshot = compressionSnapshot;
		UnscannablePaths = unscannablePaths ?? [];
	}

	/// <summary>
	/// Selected files Hide Secrets was not allowed to read, in selection order.
	///
	/// Document surfaces omit their text and carry on. A project copy reproduces bytes, so it
	/// leaves them out entirely and names them in the notice rather than copying text the scanner
	/// never saw - or refusing the whole copy over one file.
	/// </summary>
	public IReadOnlyList<string> UnscannablePaths { get; }

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
		if (_workingDirectory is not null)
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
