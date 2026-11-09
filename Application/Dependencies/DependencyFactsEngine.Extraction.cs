using System.Runtime.ExceptionServices;

namespace DevProjex.Application.Dependencies;

public sealed partial class DependencyFactsEngine
{
	private sealed record ExtractedDependencyFiles(
		PreparedDependencyIdentity[] Prepared,
		DependencySourceObservation[] Observations,
		FileFacts[] Facts,
		bool[] Cacheable,
		AccumulatedFactBudget Budget,
		int ParsedFiles,
		int ReusedFiles);

	private async Task<ExtractedDependencyFiles> ExtractFilesAsync(
		string root,
		string[] manifest,
		CanonicalManifestFile[] canonicalManifest,
		DependencyResolverConfiguration configuration,
		IReadOnlyList<string>? alignedContentIdentities,
		IProgress<DependencyIndexProgress>? progress,
		CancellationToken cancellationToken)
	{
		var prepared = new PreparedDependencyIdentity[manifest.Length];
		var observations = new DependencySourceObservation[manifest.Length];
		var parsedBefore = _extractor.ParseCount;
		var facts = new FileFacts[prepared.Length];
		var scopeIds = new string[prepared.Length];
		var cacheable = new bool[prepared.Length];
		var factBudget = new AccumulatedFactBudget(_limits.MaximumAccumulatedFactBytes);
		var completed = 0;
		var reusedFiles = 0;
		var pipelined = prepared.Length >= _minimumPipelinedFiles;
		var windowSize = pipelined ? Math.Min(_extractionParallelism * 2, prepared.Length) : 0;
		using var extractionCancellation = pipelined
			? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
			: null;
		using var extractionBudget = pipelined ? new SemaphoreSlim(_extractionParallelism, _extractionParallelism) : null;
		ExceptionDispatchInfo? producerFailure = null;
		var pending = pipelined ? new Task[windowSize] : Array.Empty<Task>();
		try
		{
			if (pipelined)
			{
				for (var index = 0; index < windowSize; index++)
					pending[index] = ExtractFileAsync(index, extractionCancellation!.Token).AsTask();
				for (var index = 0; index < prepared.Length; index++)
				{
					var slot = index % windowSize;
					await pending[slot].ConfigureAwait(false);
					cancellationToken.ThrowIfCancellationRequested();
					Admit(index, facts, scopeIds, factBudget);
					progress?.Report(new DependencyIndexProgress(++completed, prepared.Length));
					if (index + windowSize < prepared.Length)
						pending[slot] = ExtractFileAsync(index + windowSize, extractionCancellation!.Token).AsTask();
				}
			}
			else
			{
				for (var start = 0; start < prepared.Length; start += _extractionParallelism)
				{
					var count = Math.Min(_extractionParallelism, prepared.Length - start);
					await Parallel.ForEachAsync(Enumerable.Range(start, count), new ParallelOptions
					{
						CancellationToken = cancellationToken,
						MaxDegreeOfParallelism = _extractionParallelism
					}, ExtractFileAsync).ConfigureAwait(false);
					for (var index = start; index < start + count; index++)
					{
						Admit(index, facts, scopeIds, factBudget);
						progress?.Report(new DependencyIndexProgress(++completed, prepared.Length));
					}
				}
			}
		}
		catch (Exception exception)
		{
			var primaryFailure = producerFailure ?? ExceptionDispatchInfo.Capture(exception);
			CancelProducers();
			// Observe every producer before rethrowing the primary failure.
			try { await Task.WhenAll(pending).ConfigureAwait(false); }
			catch { }
			primaryFailure.Throw();
			throw;
		}

		return new ExtractedDependencyFiles(prepared, observations, facts, cacheable, factBudget,
			_extractor.ParseCount - parsedBefore, reusedFiles);

		void CancelProducers()
		{
			// A secondary cancellation callback failure must not skip draining the producers.
			try { extractionCancellation?.Cancel(); }
			catch (AggregateException) { }
		}

		static void Admit(int index, FileFacts[] facts, string[] scopeIds, AccumulatedFactBudget factBudget)
		{
			var extracted = facts[index];
			facts[index] = factBudget.TryAdmit(EstimateFileFactsBytes(extracted))
				? RebindScope(extracted, scopeIds[index])
				: CreateFactBudgetLimited(extracted, scopeIds[index]);
		}
		async ValueTask ExtractFileAsync(int index, CancellationToken token)
		{
			var acquired = false;
			try
			{
				if (extractionBudget is not null)
				{
					await extractionBudget.WaitAsync(token).ConfigureAwait(false);
					acquired = true;
				}
				var contentIdentity = alignedContentIdentities?[index];
				var source = await _extractor
					.PrepareAsync(root, manifest[index], configuration, _limits, token, contentIdentity)
					.ConfigureAwait(false);
				prepared[index] = new PreparedDependencyIdentity(
					canonicalManifest[index].RelativePath,
					source.ContentFingerprint,
					source.LanguageId);
				observations[index] = source.ContentObservation;
				scopeIds[index] = source.ScopeId;
				FileFacts extracted;
				if (source.PreparedStatus != DependencyFileStatus.Supported)
				{
					extracted = _extractor.Extract(source, _limits, token);
				}
				else
				{
					var key = CreateFileCacheKey(source);
					while (true)
					{
						token.ThrowIfCancellationRequested();
						FileCacheEntry? created = null;
						if (!_fileCache.TryGetValue(key, out var lazy))
						{
							created = new FileCacheEntry(key, new Lazy<Task<FileFacts>>(
								() => Task.Run(() => _extractor.Extract(source, _limits, token), token),
								LazyThreadSafetyMode.ExecutionAndPublication));
							lazy = GetOrAddFileCacheEntry(created);
						}
						var shared = !ReferenceEquals(lazy, created);
						if (shared)
							DependencyEngineDiagnostics.RecordFileCacheHit();
						try
						{
							var extraction = lazy.Value.Value;
							extracted = await (shared ? extraction.WaitAsync(token) : extraction)
								.ConfigureAwait(false);
							if (shared)
								Interlocked.Increment(ref reusedFiles);
							if (!extracted.CanCache)
								RemoveFileCacheEntry(key, lazy);
							else if (!shared)
								RegisterFileCacheWeight(key, lazy, EstimateFileFactsBytes(extracted));
							break;
						}
						catch (OperationCanceledException) when (shared && token.IsCancellationRequested)
						{
							throw;
						}
						catch (OperationCanceledException) when (shared && !token.IsCancellationRequested)
						{
							// Retry with this caller's token if the shared producer was canceled.
							RemoveFileCacheEntry(key, lazy);
						}
						catch
						{
							RemoveFileCacheEntry(key, lazy);
							throw;
						}
					}
				}

				cacheable[index] = source.CanCache && extracted.CanCache;
				facts[index] = extracted;
			}
			catch (Exception exception)
			{
				Interlocked.CompareExchange(ref producerFailure, ExceptionDispatchInfo.Capture(exception), null);
				CancelProducers();
				throw;
			}
			finally
			{
				if (acquired) extractionBudget!.Release();
			}
		}

	}
}
