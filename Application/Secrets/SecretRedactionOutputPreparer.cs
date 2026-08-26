using System.Runtime.CompilerServices;

namespace DevProjex.Application.Secrets;

/// <summary>
/// Reads each selected text file once, applies one deterministic redaction scope, and stores only
/// the transformed bytes in temporary files. Consumers may then render context, folders, or ZIPs
/// without reopening source text and without retaining a whole project in managed memory.
/// </summary>
public sealed class SecretRedactionOutputPreparer
{
	private readonly IFileContentAnalyzer contentAnalyzer;

	public SecretRedactionOutputPreparer(IFileContentAnalyzer contentAnalyzer)
	{
		this.contentAnalyzer = contentAnalyzer ?? throw new ArgumentNullException(nameof(contentAnalyzer));
		SecretRedactionTempDirectoryScavenger.StartOnce();
	}

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
	public Task<PreparedSecretRedactionOutput> PrepareAsync(
		ContentTransformationContext context,
		IReadOnlyList<string> orderedFilePaths,
		CancellationToken cancellationToken = default) =>
		PrepareAsync(
			context,
			orderedFilePaths,
			captureEffectiveFindings: false,
			cancellationToken);

	public Task<PreparedSecretRedactionOutput> PrepareAsync(
		ContentTransformationContext context,
		IReadOnlyList<string> orderedFilePaths,
		IProgress<ProjectCopyExportProgress> progress,
		CancellationToken cancellationToken = default) =>
		PrepareAsync(
			context,
			orderedFilePaths,
			captureEffectiveFindings: false,
			cancellationToken,
			progress);

	public async Task<PreparedSecretRedactionOutput> PrepareAsync(
		ContentTransformationContext context,
		IReadOnlyList<string> orderedFilePaths,
		bool captureEffectiveFindings,
		CancellationToken cancellationToken = default,
		IProgress<ProjectCopyExportProgress>? progress = null)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(orderedFilePaths);
		EnsureSourcePathsAvailable(context, orderedFilePaths);
		if (context is { Compression: not null, Redaction: null })
		{
			return await PrepareCompressionOnlyAsync(
					context,
					orderedFilePaths,
					progress,
					cancellationToken)
				.ConfigureAwait(false);
		}

		var redactionContext = context.Redaction;
		if (redactionContext is not null)
		{
			await redactionContext.Session
				.RefreshPersistentMarksAsync(redactionContext.ProjectRoot, cancellationToken)
				.ConfigureAwait(false);
			await redactionContext.EnsureWarmUpAsync(cancellationToken).ConfigureAwait(false);
		}

		SecretRedactionTempDirectory? workingDirectory = null;
		var preparedFiles = new Dictionary<string, PreparedSecretFile>(PathComparer.Default);
		var unscannableFiles = new List<UnscannableFile>();
		using var transformationScope = context.BeginOutput(orderedFilePaths);
		var scope = transformationScope.Redaction;
		var requiredInspectionScope = context.Compression is null ? scope : null;
		if (requiredInspectionScope is not null)
		{
			foreach (var sourcePath in orderedFilePaths)
			{
				if (requiredInspectionScope.GetContentInspectionMode(sourcePath) ==
				    SecretContentInspectionMode.None)
				{
					preparedFiles[sourcePath] = PreparedSecretFile.Unchanged(sourcePath);
				}
			}
		}
		var processedFiles = preparedFiles.Count;
		if (processedFiles > 0)
			ReportProgress(progress, processedFiles, orderedFilePaths.Count);
		try
		{
			await foreach (var prepared in PrepareOrderedTransformationEntriesAsync(
			                   context,
			                   transformationScope,
			                   orderedFilePaths,
			                   cancellationToken,
			                   requiredInspectionScope).ConfigureAwait(false))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var completed = false;
				try
				{
					var sourcePath = prepared.SourcePath;
					var result = prepared.ReadResult;
					var metadataAfterRead = prepared.Metadata;

					switch (result.Classification)
					{
						case FileContentClassification.Binary:
							scope?.AnalyzeBinary(sourcePath, metadataAfterRead);
							preparedFiles[sourcePath] = PreparedSecretFile.Binary(sourcePath);
							completed = true;
							continue;
						case FileContentClassification.TooLarge:
						case FileContentClassification.Unreadable:
						case FileContentClassification.UnsupportedEncoding:
							// A per-file inspection limitation degrades that file, not the whole run.
							// Redaction cannot promise anything about text it never decoded or fully read,
							// so every output withholds the content and reports the exact reason.
							if (scope is not null &&
							    scope.GetContentInspectionMode(sourcePath) != SecretContentInspectionMode.None)
							{
								scope.AnalyzeUnscannable(sourcePath, metadataAfterRead, result.Classification);
								preparedFiles[sourcePath] = PreparedSecretFile.Unscannable(
									sourcePath,
									result.Classification);
								unscannableFiles.Add(new UnscannableFile(sourcePath, result.Classification));
								completed = true;
								continue;
							}

							// Compression has no redaction promise to break, and detector policy may
							// exclude this path before content inspection. In both cases the original
							// remains the authoritative unchanged output.
							preparedFiles[sourcePath] = PreparedSecretFile.Unchanged(sourcePath);
							completed = true;
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
						prepared.Metadata,
						compressed.Map.IsIdentity
							? prepared.SourceFingerprint
							: null,
						cancellationToken);
					var redactions = plan?.Spans
						.Where(static span => span.State == SecretPreviewSpanState.Redacted)
						.Select(static span => new PreparedSecretSpan(span.Start, span.Length))
						.ToArray() ?? [];
					IReadOnlyList<EffectiveRedactionFinding> findings = plan is null || !captureEffectiveFindings
						? []
						: BuildEffectiveFindings(plan.Spans, content.Content, compressed.Map);
					// A completed redaction scan must remain authoritative if the source changes before output.
					if (scope is null && ReferenceEquals(transformedText, content.Content) && redactions.Length == 0)
					{
						preparedFiles[sourcePath] = findings.Count == 0
							? PreparedSecretFile.Unchanged(sourcePath)
							: new PreparedSecretFile(
								sourcePath,
								sourcePath,
								FileContentClassification.Text,
								result.Encoding,
								[],
								findings);
						completed = true;
						continue;
					}

					var encoding = result.Encoding ?? TextFileEncoding.Utf8;
					workingDirectory ??= CreateWorkingDirectory();
					var preparedPath = Path.Combine(workingDirectory.Path, $"{prepared.Index:D8}.redacted.txt");
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
						redactions,
						findings);
					completed = true;
				}
				finally
				{
					if (completed)
						ReportProgress(progress, ++processedFiles, orderedFilePaths.Count);
				}
			}

			var snapshot = scope?.Complete(
				unscannableFiles.Count,
				failedFileCount: 0,
				unscannableFiles);
			var compression = transformationScope.Compression?.Complete();
			if (redactionContext is not null)
			{
				await redactionContext.Session
					.FlushPendingPersistentMarkMigrationsAsync(
						redactionContext.ProjectRoot,
						cancellationToken)
					.ConfigureAwait(false);
			}
			return new PreparedSecretRedactionOutput(
				workingDirectory,
				preparedFiles,
				snapshot,
				compression,
				unscannableFiles);
		}
		catch
		{
			workingDirectory?.Dispose();
			throw;
		}
	}

	private async IAsyncEnumerable<PreparedTransformationEntry> PrepareOrderedTransformationEntriesAsync(
		ContentTransformationContext context,
		ContentTransformationScope transformationScope,
		IReadOnlyList<string> orderedFilePaths,
		[EnumeratorCancellation] CancellationToken cancellationToken,
		SecretRedactionScope? requiredInspectionScope = null)
	{
		var batch = new List<CompressionWorkItem>(MaximumParallelScans);
		for (var index = 0; index < orderedFilePaths.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var item = new CompressionWorkItem(index, orderedFilePaths[index]);
			if (requiredInspectionScope?.GetContentInspectionMode(item.SourcePath) ==
			    SecretContentInspectionMode.None)
			{
				continue;
			}
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
		try
		{
			return await PrepareTransformationEntryCoreAsync(
					context,
					transformationScope,
					item,
					cancellationToken)
				.ConfigureAwait(false);
		}
		catch (DecoderFallbackException)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return CreateUnsupportedEncodingEntry(item);
		}
	}

	private static IReadOnlyList<EffectiveRedactionFinding> BuildEffectiveFindings(
		IReadOnlyList<SecretPreviewSpan> spans,
		string sourceText,
		ContentTransformMap transformMap)
	{
		var candidates = spans
			.GroupBy(static span => span.OccurrenceId, StringComparer.Ordinal)
			.Select(static group => group.First())
			.Select(span => new SourceFinding(
				span,
				ResolveSourceStart(transformMap, span.SourceStart)))
			.OrderBy(static finding => finding.SourceStart)
			.ThenBy(static finding => finding.Span.RuleId, StringComparer.Ordinal)
			.ToArray();
		if (candidates.Length == 0)
			return [];

		var findings = new EffectiveRedactionFinding[candidates.Length];
		var sourceIndex = 0;
		var lineNumber = 1;
		var skipLineFeedAfterCarriageReturn = false;
		for (var index = 0; index < candidates.Length; index++)
		{
			var candidate = candidates[index];
			var target = Math.Clamp(candidate.SourceStart, sourceIndex, sourceText.Length);
			while (sourceIndex < target)
			{
				var character = sourceText[sourceIndex++];
				if (skipLineFeedAfterCarriageReturn && character == '\n')
				{
					skipLineFeedAfterCarriageReturn = false;
					continue;
				}
				skipLineFeedAfterCarriageReturn = false;
				if (character == '\r')
				{
					lineNumber++;
					skipLineFeedAfterCarriageReturn = true;
				}
				else if (character == '\n')
				{
					lineNumber++;
				}
			}
			findings[index] = new EffectiveRedactionFinding(
				candidate.Span.RuleId,
				candidate.Span.Category,
				candidate.Span.RelativePath,
				lineNumber);
		}
		return findings;
	}

	private static int ResolveSourceStart(ContentTransformMap transformMap, int transformedStart)
	{
		if (transformMap.TryToSource(transformedStart, out var sourceStart))
			return sourceStart;

		throw new SecretDetectionException(
			"An effective redaction finding could not be mapped to its source file.");
	}

	private readonly record struct SourceFinding(SecretPreviewSpan Span, int SourceStart);

	private static PreparedTransformationEntry CreateUnsupportedEncodingEntry(CompressionWorkItem item) =>
		new(
			item.Index,
			item.SourcePath,
			SecretFileMetadata.Capture(item.SourcePath),
			new FileContentReadResult(FileContentClassification.UnsupportedEncoding),
			new CodeCompressionResult(string.Empty, ContentTransformMap.Identity),
			sourceFingerprint: null,
			contentLease: null);

	private async Task<PreparedTransformationEntry> PrepareTransformationEntryCoreAsync(
		ContentTransformationContext context,
		ContentTransformationScope transformationScope,
		CompressionWorkItem item,
		CancellationToken cancellationToken)
	{
		EnsureSourcePathAvailable(context, item.SourcePath);
		var coherentRead = await ReadFactCoherentlyAsync(item.SourcePath, cancellationToken)
			.ConfigureAwait(false);
		EnsureSourcePathAvailable(context, item.SourcePath);
		var readFact = coherentRead.Fact;
		var result = readFact.ToReadResult();
		IDisposable? contentLease = null;
		try
		{
			CodeCompressionResult compression;
			if (result.Classification == FileContentClassification.Text && result.Content is not null)
			{
				contentLease = context.Redaction?.Session.TrackFullContentBuffer();
				compression = readFact.Fingerprint is { } fingerprint
					? transformationScope.Compress(
						item.SourcePath,
						NormalizeRelativePath(context, item.SourcePath),
						result.Content.Content,
						fingerprint,
						cancellationToken)
					: transformationScope.Compress(
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
				coherentRead.Metadata,
				result,
				compression,
				readFact.Fingerprint,
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
		IProgress<ProjectCopyExportProgress>? progress,
		CancellationToken cancellationToken)
	{
		var workingDirectory = new Lazy<SecretRedactionTempDirectory>(
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
		var processedFiles = 0;
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
						ReportProgress(
							progress,
							Interlocked.Increment(ref processedFiles),
							orderedFilePaths.Count);
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
				ReportProgress(
					progress,
					Interlocked.Increment(ref processedFiles),
					orderedFilePaths.Count);
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
				workingDirectory.Value.Dispose();
			throw;
		}
	}

	private static void ReportProgress(
		IProgress<ProjectCopyExportProgress>? progress,
		int processedFiles,
		int totalFiles)
	{
		if (progress is null)
			return;
		var percentage = totalFiles == 0
			? 100d
			: Math.Clamp(processedFiles * 100d / totalFiles, 0d, 100d);
		progress.Report(new ProjectCopyExportProgress(
			processedFiles,
			totalFiles,
			BytesWritten: 0,
			Percentage: percentage));
	}

	private async Task<PreparedSecretFile> PrepareCompressedFileAsync(
		ContentTransformationContext context,
		ContentTransformationScope transformationScope,
		Lazy<SecretRedactionTempDirectory> workingDirectory,
		CompressionWorkItem workItem,
		CancellationToken cancellationToken)
	{
		var sourcePath = workItem.SourcePath;
		EnsureSourcePathAvailable(context, sourcePath);
		var coherentRead = await ReadFactCoherentlyAsync(sourcePath, cancellationToken)
			.ConfigureAwait(false);
		EnsureSourcePathAvailable(context, sourcePath);
		var readFact = coherentRead.Fact;
		var result = readFact.ToReadResult();

		if (result.Classification != FileContentClassification.Text)
		{
			return result.Classification == FileContentClassification.TooLarge
				? PreparedSecretFile.Unchanged(sourcePath, coherentRead.Metadata)
				: new PreparedSecretFile(
					sourcePath,
					sourcePath,
					result.Classification,
					null,
					[])
				{
					SourceMetadata = coherentRead.Metadata
				};
		}

		if (result.Content is null)
		{
			throw new SecretDetectionException(
				$"Code compression could not inspect '{sourcePath}' ({result.Classification}).");
		}

		var compressed = readFact.Fingerprint is { } fingerprint
			? transformationScope.Compress(
				sourcePath,
				NormalizeRelativePath(context, sourcePath),
				result.Content.Content,
				fingerprint,
				cancellationToken)
			: transformationScope.Compress(
				sourcePath,
				NormalizeRelativePath(context, sourcePath),
				result.Content.Content,
				cancellationToken);
		if (ReferenceEquals(compressed.Text, result.Content.Content))
			return PreparedSecretFile.Unchanged(sourcePath, coherentRead.Metadata);

		var encoding = result.Encoding ?? TextFileEncoding.Utf8;
		var preparedPath = Path.Combine(workingDirectory.Value.Path, $"{workItem.Index:D8}.compressed.txt");
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

	private static void EnsureSourcePathsAvailable(
		ContentTransformationContext context,
		IReadOnlyList<string> orderedFilePaths)
	{
		foreach (var sourcePath in orderedFilePaths)
			EnsureSourcePathAvailable(context, sourcePath);
	}

	private static void EnsureSourcePathAvailable(
		ContentTransformationContext context,
		string sourcePath)
	{
		var projectRoot = context.Redaction?.ProjectRoot ?? context.Compression?.ProjectRoot;
		if (projectRoot is null)
			return;

		var classification = ProjectSourcePathPolicy.ClassifyUnavailable(projectRoot, sourcePath);
		if (classification is null)
			return;

		throw classification.Value switch
		{
			FileContentClassification.Missing => new FileNotFoundException(
				"A selected source file is no longer available.",
				sourcePath),
			FileContentClassification.AccessDenied => new UnauthorizedAccessException(
				$"Access was denied while inspecting the selected source file '{sourcePath}'."),
			_ => new SecretDetectionException(
				$"Content transformation could not safely inspect '{sourcePath}' ({classification.Value}).")
		};
	}

	private readonly record struct CompressionWorkItem(int Index, string SourcePath);

	private sealed class PreparedTransformationEntry(
		int index,
		string sourcePath,
		SecretFileMetadata metadata,
		FileContentReadResult readResult,
		CodeCompressionResult compression,
		ContentFingerprint? sourceFingerprint,
		IDisposable? contentLease) : IDisposable
	{
		private IDisposable? _contentLease = contentLease;

		public int Index { get; } = index;
		public string SourcePath { get; } = sourcePath;
		public SecretFileMetadata Metadata { get; } = metadata;
		public FileContentReadResult ReadResult { get; } = readResult;
		public CodeCompressionResult Compression { get; } = compression;
		public ContentFingerprint? SourceFingerprint { get; } = sourceFingerprint;

		public void Dispose() => Interlocked.Exchange(ref _contentLease, null)?.Dispose();
	}

	public async Task<SecretRedactionSnapshot> AnalyzeAsync(
		SecretRedactionContext context,
		IReadOnlyList<string> orderedFilePaths,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(orderedFilePaths);
		if (await context.Session
			    .EnsureCurrentPersistentIdentityReadyAsync(context.Features, cancellationToken)
			    .ConfigureAwait(false) != PersistentSecretIdentityAvailability.Ready)
		{
			throw new SecretDetectionException("The persistent secret identity key is unavailable.");
		}
		await context.EnsureWarmUpAsync(cancellationToken).ConfigureAwait(false);
		var scope = context.BeginOutput(orderedFilePaths);
		var entries = new SecretScanCacheEntry?[orderedFilePaths.Count];
		var unscannableFiles = new List<UnscannableFile>();
		var parallelWork = new List<SecretScanWorkItem>();
		var serialWork = new List<SecretScanWorkItem>();
		for (var index = 0; index < orderedFilePaths.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var sourcePath = orderedFilePaths[index];
			if (scope.GetContentInspectionMode(sourcePath) == SecretContentInspectionMode.None)
				continue;
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
			var entry = entries[index];
			if (entry is null &&
			    scope.GetContentInspectionMode(orderedFilePaths[index]) == SecretContentInspectionMode.None)
			{
				continue;
			}
			if (entry is null)
			{
				throw new SecretDetectionException(
					$"Content redaction produced no scan result for '{orderedFilePaths[index]}'.");
			}
			scope.ProcessEntry(orderedFilePaths[index], entry);
			if (entry.IsUnscannable)
			{
				skippedFileCount++;
				unscannableFiles.Add(new UnscannableFile(
					orderedFilePaths[index],
					entry.UnscannableClassification!.Value));
			}
		}

		var snapshot = scope.Complete(skippedFileCount, failedFileCount: 0, unscannableFiles);
		await context.Session
			.FlushPendingPersistentMarkMigrationsAsync(context.ProjectRoot, cancellationToken)
			.ConfigureAwait(false);
		return snapshot;
	}

	/// <summary>
	/// Discovers whether the current selection contains redaction findings without weakening output safety.
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
		await context.EnsureWarmUpAsync(cancellationToken).ConfigureAwait(false);
		var scope = context.BeginOutput(orderedFilePaths);
		var entries = new SecretScanCacheEntry?[orderedFilePaths.Count];
		var outcomes = new SecretDiscoveryFileOutcome[orderedFilePaths.Count];
		var unscannableFiles = new List<UnscannableFile>();
		var parallelWork = new List<SecretScanWorkItem>();
		var serialWork = new List<SecretScanWorkItem>();

		for (var index = 0; index < orderedFilePaths.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var sourcePath = orderedFilePaths[index];
			if (scope.GetContentInspectionMode(sourcePath) == SecretContentInspectionMode.None)
				continue;
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
				try
				{
					scope.ProcessEntry(orderedFilePaths[index], entry);
				}
				catch (SecretInspectionBudgetExceededException)
				{
					outcomes[index] = SecretDiscoveryFileOutcome.Failed;
					entry = null;
				}
				if (entry?.IsUnscannable == true)
				{
					outcomes[index] = SecretDiscoveryFileOutcome.SkippedByLimit;
					unscannableFiles.Add(new UnscannableFile(
						orderedFilePaths[index],
						entry.UnscannableClassification!.Value));
				}
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

		var snapshot = scope.Complete(skippedFileCount, failedFileCount, unscannableFiles);
		await context.Session
			.FlushPendingPersistentMarkMigrationsAsync(context.ProjectRoot, cancellationToken)
			.ConfigureAwait(false);
		return snapshot;
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

		if (await redactionContext.Session
			    .EnsureCurrentPersistentIdentityReadyAsync(redactionContext.Features, cancellationToken)
			    .ConfigureAwait(false) != PersistentSecretIdentityAvailability.Ready)
		{
			throw new SecretDetectionException("The persistent secret identity key is unavailable.");
		}
		await redactionContext.EnsureWarmUpAsync(cancellationToken).ConfigureAwait(false);
		using var transformationScope = context.BeginOutput(orderedFilePaths);
		var redactionScope = transformationScope.Redaction ??
		                     throw new InvalidOperationException(
			                     "The transformation scope did not create secret redaction state.");
		var skippedFileCount = 0;
		var unscannableFiles = new List<UnscannableFile>();

		await foreach (var prepared in PrepareOrderedTransformationEntriesAsync(
		                   context,
		                   transformationScope,
		                   orderedFilePaths,
		                   cancellationToken,
		                   redactionScope).ConfigureAwait(false))
		{
			cancellationToken.ThrowIfCancellationRequested();
			switch (prepared.ReadResult.Classification)
			{
				case FileContentClassification.Binary:
					redactionScope.AnalyzeBinary(prepared.SourcePath, prepared.Metadata);
					break;
				case FileContentClassification.TooLarge:
				case FileContentClassification.Unreadable:
				case FileContentClassification.UnsupportedEncoding:
					redactionScope.AnalyzeUnscannable(
						prepared.SourcePath,
						prepared.Metadata,
						prepared.ReadResult.Classification);
					skippedFileCount++;
					unscannableFiles.Add(new UnscannableFile(
						prepared.SourcePath,
						prepared.ReadResult.Classification));
					break;
				case FileContentClassification.Text:
					redactionScope.CreatePlan(
						prepared.SourcePath,
						prepared.Compression.Text,
						prepared.Compression.Map,
						prepared.Metadata,
						prepared.Compression.Map.IsIdentity
							? prepared.SourceFingerprint
							: null,
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
		var snapshot = redactionScope.Complete(
			skippedFileCount,
			failedFileCount: 0,
			unscannableFiles);
		await redactionContext.Session
			.FlushPendingPersistentMarkMigrationsAsync(redactionContext.ProjectRoot, cancellationToken)
			.ConfigureAwait(false);
		return snapshot;
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

		await redactionContext.EnsureWarmUpAsync(cancellationToken).ConfigureAwait(false);
		using var transformationScope = context.BeginOutput(orderedFilePaths);
		var redactionScope = transformationScope.Redaction ??
		                     throw new InvalidOperationException(
			                     "The transformation scope did not create secret redaction state.");
		var skippedFileCount = 0;
		var failedFileCount = 0;
		var unscannableFiles = new List<UnscannableFile>();

		await foreach (var attempt in PrepareOrderedDiscoveryEntriesAsync(
		                   context,
		                   transformationScope,
		                   redactionScope,
		                   orderedFilePaths,
		                   cacheMode,
		                   cancellationToken).ConfigureAwait(false))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (attempt.IsPolicyExcluded)
				continue;
			if (attempt.CachedEntry is not null)
			{
				try
				{
					redactionScope.ProcessEntry(attempt.SourcePath!, attempt.CachedEntry);
				}
				catch (SecretInspectionBudgetExceededException)
				{
					failedFileCount++;
					continue;
				}
				if (attempt.CachedEntry.IsUnscannable)
				{
					skippedFileCount++;
					unscannableFiles.Add(new UnscannableFile(
						attempt.SourcePath!,
						attempt.CachedEntry.UnscannableClassification!.Value));
				}
				continue;
			}

			if (attempt.Entry is null)
			{
				failedFileCount++;
				continue;
			}

			var prepared = attempt.Entry;
			try
			{
				switch (prepared.ReadResult.Classification)
				{
					case FileContentClassification.Binary:
						redactionScope.AnalyzeBinary(prepared.SourcePath, prepared.Metadata);
						break;
					case FileContentClassification.TooLarge:
					case FileContentClassification.Unreadable:
					case FileContentClassification.UnsupportedEncoding:
						redactionScope.AnalyzeUnscannable(
							prepared.SourcePath,
							prepared.Metadata,
							prepared.ReadResult.Classification);
						skippedFileCount++;
						unscannableFiles.Add(new UnscannableFile(
							prepared.SourcePath,
							prepared.ReadResult.Classification));
						break;
					case FileContentClassification.Text:
						redactionScope.CreatePlan(
							prepared.SourcePath,
							prepared.Compression.Text,
							prepared.Compression.Map,
							prepared.Metadata,
							prepared.Compression.Map.IsIdentity
								? prepared.SourceFingerprint
								: null,
							cancellationToken);
						break;
					default:
						failedFileCount++;
						break;
				}
			}
			catch (SecretInspectionBudgetExceededException)
			{
				failedFileCount++;
			}
		}

		var snapshot = redactionScope.Complete(skippedFileCount, failedFileCount, unscannableFiles);
		await redactionContext.Session
			.FlushPendingPersistentMarkMigrationsAsync(redactionContext.ProjectRoot, cancellationToken)
			.ConfigureAwait(false);
		return snapshot;
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
			if (redactionScope.GetContentInspectionMode(item.SourcePath) ==
			    SecretContentInspectionMode.None)
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
				yield return DiscoveryTransformationAttempt.PolicyExcluded();
				continue;
			}
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
		catch (DecoderFallbackException)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return DiscoveryTransformationAttempt.Prepared(CreateUnsupportedEncodingEntry(item));
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
		SecretScanCacheEntry? CachedEntry,
		bool IsPolicyExcluded) : IDisposable
	{
		public static DiscoveryTransformationAttempt Prepared(PreparedTransformationEntry entry) =>
			new(entry, null, null, false);

		public static DiscoveryTransformationAttempt Cached(
			string sourcePath,
			SecretScanCacheEntry entry) =>
			new(null, sourcePath, entry, false);

		public static DiscoveryTransformationAttempt Incomplete() => new(null, null, null, false);

		public static DiscoveryTransformationAttempt PolicyExcluded() =>
			new(null, null, null, true);

		public void Dispose() => Entry?.Dispose();
	}

	private async Task<SecretScanCacheEntry> AnalyzeFileAsync(
		SecretRedactionScope scope,
		SecretScanWorkItem workItem,
		CancellationToken cancellationToken)
	{
		var sourcePath = workItem.SourcePath;
		CoherentSecretTextBuffer coherentRead;
		try
		{
			coherentRead = await OpenCompleteTextBufferCoherentlyAsync(sourcePath, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (DecoderFallbackException)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return scope.StoreUnscannable(
				sourcePath,
				SecretFileMetadata.Capture(sourcePath),
				FileContentClassification.UnsupportedEncoding);
		}
		await using var contentBuffer = coherentRead.Buffer;
		var metadata = coherentRead.Metadata;

		switch (contentBuffer.Classification)
		{
			case FileContentClassification.Binary:
				return scope.StoreBinary(sourcePath, metadata);
			case FileContentClassification.TooLarge:
			case FileContentClassification.Unreadable:
			case FileContentClassification.UnsupportedEncoding:
				// This scan only feeds the count on the checkbox. One file it may not read is a
				// reason to leave that file out of the count, never to refuse the whole project -
				// the user asked how many secrets are here, not for a guarantee about output.
				return scope.StoreUnscannable(sourcePath, metadata, contentBuffer.Classification);
			case FileContentClassification.Text:
				using (scope.TrackFullContentBuffer())
				{
					return scope.Detect(
						sourcePath,
						contentBuffer.Content.Span,
						metadata,
						cancellationToken);
				}
			default:
				throw new SecretDetectionException(
					$"Hide Secrets could not inspect '{sourcePath}' ({contentBuffer.Classification}).");
		}
	}

	private async ValueTask<CoherentSecretContentRead> ReadFactCoherentlyAsync(
		string path,
		CancellationToken cancellationToken)
	{
		if (contentAnalyzer is ICoherentFileContentAnalyzer coherentAnalyzer)
		{
			var identified = await coherentAnalyzer
				.ReadFactWithIdentityAsync(path, MaximumScannableFileBytes, cancellationToken)
				.ConfigureAwait(false);
			var metadata = ResolveReadMetadata(
				path,
				identified.Identity,
				identified.Fact.ToReadResult().Content);
			return new CoherentSecretContentRead(identified.Fact, metadata);
		}

		var before = SecretFileMetadata.Capture(path);
		var fact = await contentAnalyzer
			.ReadFactAsync(path, MaximumScannableFileBytes, cancellationToken)
			.ConfigureAwait(false);
		var after = SecretFileMetadata.Capture(path);
		EnsureStableRead(path, before, after, fact.ToReadResult().Content);
		return new CoherentSecretContentRead(fact, after);
	}

	private async ValueTask<CoherentSecretTextBuffer> OpenCompleteTextBufferCoherentlyAsync(
		string path,
		CancellationToken cancellationToken)
	{
		if (contentAnalyzer is ICoherentFileContentAnalyzer coherentAnalyzer)
		{
			var identified = await coherentAnalyzer
				.OpenCompleteTextBufferWithIdentityAsync(
					path,
					MaximumScannableFileBytes,
					cancellationToken)
				.ConfigureAwait(false);
			try
			{
				var metadata = ResolveBufferMetadata(path, identified.Identity, identified.Buffer);
				return new CoherentSecretTextBuffer(identified.Buffer, metadata);
			}
			catch
			{
				await identified.Buffer.DisposeAsync().ConfigureAwait(false);
				throw;
			}
		}

		var before = SecretFileMetadata.Capture(path);
		var buffer = await contentAnalyzer
			.OpenCompleteTextBufferAsync(path, MaximumScannableFileBytes, cancellationToken)
			.ConfigureAwait(false);
		try
		{
			var after = SecretFileMetadata.Capture(path);
			if (before != after || buffer.SizeBytes > 0 && buffer.SizeBytes != after.Length)
			{
				throw new SecretDetectionException(
					$"Hide Secrets could not inspect a changing file: '{path}'.");
			}
			return new CoherentSecretTextBuffer(buffer, after);
		}
		catch
		{
			await buffer.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	private static SecretFileMetadata ResolveReadMetadata(
		string path,
		FileContentIdentity? identity,
		TextFileContent? content)
	{
		var metadata = identity is { } value
			? SecretFileMetadata.FromIdentity(value)
			: SecretFileMetadata.Capture(path);
		if (content is not null && content.SizeBytes != metadata.Length)
			throw new SecretDetectionException($"Hide Secrets could not inspect a changing file: '{path}'.");
		return metadata;
	}

	private static SecretFileMetadata ResolveBufferMetadata(
		string path,
		FileContentIdentity? identity,
		ICompleteTextFileBuffer buffer)
	{
		var metadata = identity is { } value
			? SecretFileMetadata.FromIdentity(value)
			: SecretFileMetadata.Capture(path);
		if (buffer.SizeBytes > 0 && buffer.SizeBytes != metadata.Length)
			throw new SecretDetectionException($"Hide Secrets could not inspect a changing file: '{path}'.");
		return metadata;
	}

	private readonly record struct CoherentSecretContentRead(
		ContentReadFact Fact,
		SecretFileMetadata Metadata);

	private readonly record struct CoherentSecretTextBuffer(
		ICompleteTextFileBuffer Buffer,
		SecretFileMetadata Metadata);

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

	private static SecretRedactionTempDirectory CreateWorkingDirectory() =>
		SecretRedactionTempDirectory.Create();

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

}

public sealed record PreparedSecretFile(
	string SourcePath,
	string ContentPath,
	FileContentClassification Classification,
	TextFileEncoding? Encoding,
	IReadOnlyList<PreparedSecretSpan> Redactions,
	IReadOnlyList<EffectiveRedactionFinding>? EffectiveFindings = null)
{
	public bool IsText => Classification == FileContentClassification.Text;

	/// <summary>
	/// The file could not be decoded or fully read under the scanner's bounded contract, so no
	/// redaction was ever planned for it. Its source content must not be served by prepared outputs.
	/// </summary>
	public bool IsUnscannable => Classification is FileContentClassification.TooLarge or
		FileContentClassification.Unreadable or
		FileContentClassification.UnsupportedEncoding;

	public int RedactedCount => Redactions.Count;
	public IReadOnlyList<EffectiveRedactionFinding> Findings => EffectiveFindings ?? [];
	internal SecretFileMetadata? SourceMetadata { get; init; }

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

	internal static PreparedSecretFile Unchanged(
		string sourcePath,
		SecretFileMetadata sourceMetadata) =>
		new PreparedSecretFile(sourcePath, sourcePath, FileContentClassification.Text, null, [])
		{
			SourceMetadata = sourceMetadata
		};

	internal void EnsureSourceVersion(FileStream? source = null)
	{
		if (SourceMetadata is not { } expected)
			return;

		SecretFileMetadata current;
		try
		{
			current = source is not null && FileContentIdentity.TryCapture(source) is { } identity
				? SecretFileMetadata.FromIdentity(identity)
				: SecretFileMetadata.Capture(SourcePath);
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException)
		{
			throw new SecretDetectionException(
				$"Code compression could not verify the prepared source file '{SourcePath}'.",
				exception);
		}

		if (current != expected)
		{
			throw new SecretDetectionException(
				$"Code compression source changed after preparation: '{SourcePath}'.");
		}
	}

	/// <summary>Uninspected text: recorded, reported, and withheld from every prepared output.</summary>
	public static PreparedSecretFile Unscannable(
		string sourcePath,
		FileContentClassification classification)
	{
		if (classification is not (FileContentClassification.TooLarge or
		    FileContentClassification.Unreadable or
		    FileContentClassification.UnsupportedEncoding))
		{
			throw new ArgumentOutOfRangeException(nameof(classification), classification, null);
		}
		return new PreparedSecretFile(sourcePath, sourcePath, classification, null, []);
	}
}

public sealed record PreparedSecretSpan(int Start, int Length)
{
	public int End => checked(Start + Length);
}

public sealed class PreparedSecretRedactionOutput : IAsyncDisposable
{
	private readonly SecretRedactionTempDirectory? _workingDirectory;
	private readonly IReadOnlyDictionary<string, PreparedSecretFile> _files;
	private bool _disposed;

	internal PreparedSecretRedactionOutput(
		SecretRedactionTempDirectory? workingDirectory,
		IReadOnlyDictionary<string, PreparedSecretFile> files,
		SecretRedactionSnapshot? snapshot,
		Compression.CodeCompressionSnapshot? compressionSnapshot = null,
		IReadOnlyList<UnscannableFile>? unscannableFiles = null)
	{
		_workingDirectory = workingDirectory;
		_files = files;
		Snapshot = snapshot;
		CompressionSnapshot = compressionSnapshot;
		UnscannableFiles = unscannableFiles is { Count: > 0 }
			? unscannableFiles.ToArray()
			: [];
		UnscannablePaths = UnscannableFiles.Count == 0
			? []
			: UnscannableFiles.Select(static file => file.Path).ToArray();
	}

	/// <summary>
	/// Selected files Hide Secrets was not allowed to read, in selection order.
	///
	/// Document surfaces omit their text and carry on. A project copy reproduces bytes, so it
	/// leaves them out entirely and names them in the notice rather than copying text the scanner
	/// never saw - or refusing the whole copy over one file.
	/// </summary>
	public IReadOnlyList<string> UnscannablePaths { get; }

	public IReadOnlyList<UnscannableFile> UnscannableFiles { get; }

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

	public IReadOnlyList<EffectiveRedactionFinding> GetEffectiveFindings() =>
		_files.Values
			.SelectMany(static file => file.Findings)
			.OrderBy(static finding => finding.RelativePath, PathComparer.Default)
			.ThenBy(static finding => finding.LineNumber)
			.ThenBy(static finding => finding.RuleId, StringComparer.Ordinal)
			.ToArray();

	public ValueTask DisposeAsync()
	{
		if (_disposed)
			return ValueTask.CompletedTask;
		_disposed = true;
		if (_workingDirectory is not null)
			_workingDirectory.Dispose();
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
	/// An unscannable file retains its original classification but is never given a larger read
	/// budget than the scanner. Too-large text stays estimated and unsupported text stays undecoded,
	/// so no uninspected character can escape through a more permissive downstream caller.
	/// </summary>
	private long ClampReadLimit(PreparedSecretFile file, long requested) =>
		file.IsUnscannable
			? Math.Min(requested, SecretRedactionOutputPreparer.MaximumScannableFileBytes)
			: requested;

	public FileContentClassification? ClassifyWithoutReading(string path)
	{
		var file = prepared.GetFile(path);
		if (file.IsUnscannable)
			return file.Classification;
		return file.IsText ? null : FileContentClassification.Binary;
	}

	public async ValueTask<FileContentReadResult> ReadClassifiedAsync(
		string path,
		long maxSizeForFullRead,
		CancellationToken cancellationToken = default)
	{
		var file = prepared.GetFile(path);
		if (!file.IsText && !file.IsUnscannable)
			return new FileContentReadResult(FileContentClassification.Binary);

		file.EnsureSourceVersion();
		var result = await inner.ReadClassifiedAsync(
				file.ContentPath,
				ClampReadLimit(file, maxSizeForFullRead),
				cancellationToken)
			.ConfigureAwait(false);
		file.EnsureSourceVersion();
		return result;
	}

	public async ValueTask<bool> IsTextFileAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		var file = prepared.GetFile(path);
		if (!file.IsText && !file.IsUnscannable)
			return false;

		file.EnsureSourceVersion();
		var result = await inner.IsTextFileAsync(file.ContentPath, cancellationToken)
			.ConfigureAwait(false);
		file.EnsureSourceVersion();
		return result;
	}

	public async ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		var file = prepared.GetFile(path);
		if (!file.IsText && !file.IsUnscannable)
			return null;

		file.EnsureSourceVersion();
		var result = await inner.GetTextFileMetricsAsync(file.ContentPath, cancellationToken)
			.ConfigureAwait(false);
		file.EnsureSourceVersion();
		return result;
	}

	public async ValueTask<FileContentMetricsResult> GetClassifiedMetricsAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		var file = prepared.GetFile(path);
		if (!file.IsText && !file.IsUnscannable)
			return new FileContentMetricsResult(FileContentClassification.Binary);

		file.EnsureSourceVersion();
		var result = await inner.GetClassifiedMetricsAsync(file.ContentPath, cancellationToken)
			.ConfigureAwait(false);
		file.EnsureSourceVersion();
		return result;
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
			file.EnsureSourceVersion();
			var metrics = await inner.GetClassifiedMetricsAsync(file.ContentPath, cancellationToken)
				.ConfigureAwait(false);
			file.EnsureSourceVersion();
			return new UnscannableSnapshot(
				file.Classification,
				metrics);
		}

		file.EnsureSourceVersion();
		var snapshot = await inner.OpenCompleteSnapshotAsync(file.ContentPath, cancellationToken)
			.ConfigureAwait(false);
		try
		{
			file.EnsureSourceVersion();
			return snapshot;
		}
		catch
		{
			await snapshot.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	public async ValueTask<TextFileContent?> TryReadAsTextAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		var file = prepared.GetFile(path);
		if (file.IsUnscannable)
		{
			file.EnsureSourceVersion();
			var unscannable = await inner.TryReadAsTextAsync(
				file.ContentPath,
				SecretRedactionOutputPreparer.MaximumScannableFileBytes,
				cancellationToken).ConfigureAwait(false);
			file.EnsureSourceVersion();
			return unscannable;
		}

		if (!file.IsText)
			return null;

		file.EnsureSourceVersion();
		var result = await inner.TryReadAsTextAsync(file.ContentPath, cancellationToken)
			.ConfigureAwait(false);
		file.EnsureSourceVersion();
		return result;
	}

	public async ValueTask<TextFileContent?> TryReadAsTextAsync(
		string path,
		long maxSizeForFullRead,
		CancellationToken cancellationToken = default)
	{
		var file = prepared.GetFile(path);
		if (!file.IsText && !file.IsUnscannable)
			return null;

		file.EnsureSourceVersion();
		var result = await inner.TryReadAsTextAsync(
				file.ContentPath,
				ClampReadLimit(file, maxSizeForFullRead),
				cancellationToken)
			.ConfigureAwait(false);
		file.EnsureSourceVersion();
		return result;
	}

	private sealed class UnscannableSnapshot(
		FileContentClassification classification,
		FileContentMetricsResult result) : IFileContentSnapshot
	{
		public FileContentMetricsResult Result { get; } =
			new(classification, result.Metrics);

		public ValueTask CopyTextToAsync(
			int maximumCharacters,
			Func<ReadOnlyMemory<char>, CancellationToken, ValueTask> writeChunk,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException(
				new IOException("The file could not be inspected for content redaction and was withheld."));

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
