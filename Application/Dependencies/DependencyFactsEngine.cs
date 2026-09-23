using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace DevProjex.Application.Dependencies;

public sealed partial class DependencyFactsEngine : IDisposable
{
	public const int MaximumRelatedTraversalSeeds = 256;
	internal const string AccumulatedFactBudgetReason = "index fact memory limit exceeded";
	private const int MaximumCachedNavigationFiles = 2_048;
	private const long MaximumNavigationCacheBytes = 8L * 1024 * 1024;
	private const int MaximumCachedPhysicalFileProbes = 4_096;
	private const int MaximumSharedIndexCancellationRetries = 1;

	private readonly IDependencyFactExtractor _extractor;
	private readonly IDependencyConfigurationProvider _configurationProvider;
	private readonly DependencyFactsLimits _limits;
	private readonly ConcurrentDictionary<FileCacheKey, FileCacheEntry> _fileCache = [];
	private readonly LinkedList<FileCacheEntry> _fileCacheOrder = [];
	private readonly ConcurrentDictionary<IndexCacheKey, IndexCacheEntry> _indexCache = [];
	private readonly LinkedList<IndexCacheEntry> _indexCacheOrder = [];
	private readonly ConcurrentDictionary<ManifestRequestKey, ManifestSnapshotCacheEntry> _manifestSnapshots = [];
	private readonly LinkedList<ManifestSnapshotCacheEntry> _manifestSnapshotOrder = [];
	private readonly Dictionary<NavigationCacheKey, LinkedListNode<NavigationCacheEntry>> _navigationCache = [];
	private readonly LinkedList<NavigationCacheEntry> _navigationCacheOrder = [];
	private readonly object _navigationCacheSync = new();
	private readonly object _cacheTrimSync = new();
	private long _fileCacheBytes;
	private long _indexCacheBytes;
	private long _navigationCacheBytes;
	private int _disposed;

	public DependencyFactsEngine(
		IDependencyFactExtractor extractor,
		IDependencyConfigurationProvider configurationProvider,
		DependencyFactsLimits? limits = null)
	{
		_extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
		_configurationProvider = configurationProvider ??
			throw new ArgumentNullException(nameof(configurationProvider));
		_limits = limits ?? new DependencyFactsLimits();
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaximumAccumulatedFactBytes);
	}

	public int ParseCount => _extractor.ParseCount;
	public int CompiledQuerySetCount => _extractor.CompiledQuerySetCount;

	public IReadOnlyList<NavigationDeclaration> ExtractNavigation(
		string relativePath,
		string source,
		string contentFingerprint,
		CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
		ArgumentNullException.ThrowIfNull(source);
		ArgumentException.ThrowIfNullOrWhiteSpace(contentFingerprint);
		cancellationToken.ThrowIfCancellationRequested();
		if (source.Length > _limits.MaximumCharactersPerFile ||
			_extractor is not IDependencyNavigationExtractor navigationExtractor)
			return [];
		return navigationExtractor.ExtractNavigation(
			relativePath,
			source,
			contentFingerprint,
			cancellationToken);
	}

	/// <summary>Caches navigation extracted from a caller-provided immutable text snapshot.</summary>
	internal IReadOnlyList<NavigationDeclaration> ExtractNavigationFromProtectedText(
		string relativePath,
		string source,
		string contentFingerprint,
		CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
		ArgumentNullException.ThrowIfNull(source);
		ArgumentException.ThrowIfNullOrWhiteSpace(contentFingerprint);
		cancellationToken.ThrowIfCancellationRequested();
		if (source.Length > _limits.MaximumCharactersPerFile ||
			_extractor is not IDependencyNavigationExtractor)
			return [];
		var key = new NavigationCacheKey(relativePath, contentFingerprint);
		lock (_navigationCacheSync)
		{
			if (_navigationCache.TryGetValue(key, out var cached))
			{
				cancellationToken.ThrowIfCancellationRequested();
				_navigationCacheOrder.Remove(cached);
				_navigationCacheOrder.AddLast(cached);
				return cached.Value.Declarations;
			}
		}
		var declarations = ExtractNavigation(
			relativePath,
			source,
			contentFingerprint,
			cancellationToken);
		cancellationToken.ThrowIfCancellationRequested();
		var retained = Array.AsReadOnly(declarations.ToArray());
		var weight = EstimateNavigationCacheBytes(key, retained);
		if (weight > MaximumNavigationCacheBytes)
			return retained;
		lock (_navigationCacheSync)
		{
			if (_navigationCache.TryGetValue(key, out var cached))
			{
				cancellationToken.ThrowIfCancellationRequested();
				_navigationCacheOrder.Remove(cached);
				_navigationCacheOrder.AddLast(cached);
				return cached.Value.Declarations;
			}
			if (Volatile.Read(ref _disposed) != 0)
				return retained;
			var entry = new NavigationCacheEntry(key, retained, weight);
			_navigationCache[key] = _navigationCacheOrder.AddLast(entry);
			_navigationCacheBytes += weight;
			while ((_navigationCache.Count > MaximumCachedNavigationFiles ||
					_navigationCacheBytes > MaximumNavigationCacheBytes) &&
				   _navigationCacheOrder.First is { Value: var oldest })
			{
				_navigationCache.Remove(oldest.Key);
				_navigationCacheOrder.RemoveFirst();
				_navigationCacheBytes -= oldest.Weight;
			}
		}
		return retained;
	}

	private static long EstimateNavigationCacheBytes(
		NavigationCacheKey key,
		IReadOnlyList<NavigationDeclaration> declarations)
	{
		var strings = new RetainedStringEstimator();
		return 160 + strings.Add(key.RelativePath) + strings.Add(key.ContentFingerprint) +
			   declarations.Count * 8L + declarations.Sum(declaration =>
				   96 + strings.Add(declaration.Name) + strings.Add(declaration.Owner) +
				   strings.Add(declaration.ContentFingerprint));
	}
	internal DependencyFactsCacheState CacheState
	{
		get
		{
			lock (_cacheTrimSync)
				return new(
					_manifestSnapshots.Count,
					_manifestSnapshotOrder.Count,
					_indexCache.Count,
					_indexCacheBytes,
					_indexCacheOrder.Count,
					_fileCache.Count,
					_fileCacheOrder.Count,
					_fileCacheBytes);
		}
	}

	public async Task<DependencyIndexSnapshot> IndexAsync(
		string sourceRoot,
		IReadOnlyList<string> manifestFiles,
		IProgress<DependencyIndexProgress>? progress = null,
		CancellationToken cancellationToken = default,
		DependencyManifestContentIdentities? contentIdentities = null)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
		ArgumentNullException.ThrowIfNull(manifestFiles);
		cancellationToken.ThrowIfCancellationRequested();
		var started = Stopwatch.StartNew();
		var root = Path.GetFullPath(sourceRoot);
		var canonicalManifest = CreateCanonicalManifest(root, manifestFiles, cancellationToken);
		var manifest = new string[canonicalManifest.Length];
		var manifestRelativePaths = new string[canonicalManifest.Length];
		for (var index = 0; index < canonicalManifest.Length; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			manifest[index] = canonicalManifest[index].FullPath;
			manifestRelativePaths[index] = canonicalManifest[index].RelativePath;
		}
		var manifestRequestKey = new ManifestRequestKey(
			root,
			HashWithCancellation(manifestRelativePaths, cancellationToken));
		var initialStamps = TryCaptureFileStamps(manifest, cancellationToken);
		var alignedContentIdentities = AlignContentIdentities(manifest, contentIdentities, cancellationToken);
		if (initialStamps is not null &&
			_manifestSnapshots.TryGetValue(manifestRequestKey, out var cachedSnapshot) &&
			cachedSnapshot.ManifestPaths.SequenceEqual(manifestRelativePaths, StringComparer.Ordinal) &&
			cachedSnapshot.Stamps.SequenceEqual(initialStamps) &&
			AreControlFilesStillAbsent(cachedSnapshot.AbsentControlFiles, cancellationToken) &&
			ArePhysicalFileProbesCurrent(cachedSnapshot.PhysicalFileProbes, cancellationToken) &&
			ContentIdentitiesMatch(cachedSnapshot.ContentIdentities, alignedContentIdentities) &&
			_indexCache.ContainsKey(cachedSnapshot.IndexCacheKey))
		{
			DependencyEngineDiagnostics.RecordResolutionCacheHit();
			var snapshot = cachedSnapshot.Snapshot;
			progress?.Report(new DependencyIndexProgress(manifest.Length, manifest.Length));
			cancellationToken.ThrowIfCancellationRequested();
			return snapshot with
			{
				Metrics = new DependencyIndexMetrics(
					0,
					snapshot.Files.Count,
					0,
					started.ElapsedMilliseconds,
					true)
				{
					AccumulatedFactBytes = snapshot.Metrics.AccumulatedFactBytes,
					MaximumAccumulatedFactBytes = snapshot.Metrics.MaximumAccumulatedFactBytes,
					FactBudgetLimitedFiles = snapshot.Metrics.FactBudgetLimitedFiles,
					ResolverContextEstimatedBytes = snapshot.Metrics.ResolverContextEstimatedBytes,
					ResolvedIndexEstimatedBytes = snapshot.Metrics.ResolvedIndexEstimatedBytes
				},
				// Nothing was prepared or read here, so the observations an earlier pass made are
				// not repeated as if this pass had just made them.
				ContentObservations = NoContentObservations
			};
		}
		var configuration = await _configurationProvider
			.ReadAsync(root, manifest, cancellationToken)
			.ConfigureAwait(false);
		var prepared = new PreparedDependencyIdentity[manifest.Length];
		var observations = new DependencySourceObservation[manifest.Length];
		var parsedBefore = _extractor.ParseCount;
		var facts = new FileFacts[prepared.Length];
		var scopeIds = new string[prepared.Length];
		var cacheable = new bool[prepared.Length];
		var factBudget = new AccumulatedFactBudget(_limits.MaximumAccumulatedFactBytes);
		var completed = 0;
		var reusedFiles = 0;
		var extractionParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8);
		for (var batchStart = 0; batchStart < prepared.Length; batchStart += extractionParallelism)
		{
			var batchCount = Math.Min(extractionParallelism, prepared.Length - batchStart);
			await Parallel.ForEachAsync(
				Enumerable.Range(batchStart, batchCount),
				new ParallelOptions
				{
					CancellationToken = cancellationToken,
					MaxDegreeOfParallelism = extractionParallelism
				},
				async (index, token) =>
				{
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
				}).ConfigureAwait(false);

			for (var index = batchStart; index < batchStart + batchCount; index++)
			{
				var extracted = facts[index];
				facts[index] = factBudget.TryAdmit(EstimateFileFactsBytes(extracted))
					? RebindScope(extracted, scopeIds[index])
					: CreateFactBudgetLimited(extracted, scopeIds[index]);
				progress?.Report(new DependencyIndexProgress(++completed, prepared.Length));
			}
		}

		var manifestGeneration = HashWithCancellation(prepared.Select(source =>
			$"{source.RelativePath}\0{source.ContentFingerprint}\0{source.LanguageId}"), cancellationToken);
		var parsedFiles = _extractor.ParseCount - parsedBefore;
		// Parallel extraction writes by canonical manifest index, so this array is already ordered.
		var orderedFacts = facts;
		var declarations = MergeDeclarations(orderedFacts);
		var declarationRevision = HashWithCancellation(declarations.Select(DeclarationKey), cancellationToken);
		var cacheKey = new IndexCacheKey(
			root,
			manifestGeneration,
			declarationRevision,
			configuration.Fingerprint);
		var allowed = orderedFacts.Select(static fact => fact.Path).ToHashSet(StringComparer.Ordinal);
		var canCacheIndex = configuration.CanCache && cacheable.All(static value => value);
		Lazy<Task<ResolvedIndex>> CreateIndex() => new(
			() => Task.FromResult(GateResolvedIndex(
				DependencyResolver.Resolve(root, orderedFacts, declarations, configuration, _limits, cancellationToken),
				allowed)),
			LazyThreadSafetyMode.ExecutionAndPublication);
		ResolvedIndex resolved;
		IndexCacheEntry? resolvedIndexEntry = null;
		var resolutionCacheHit = false;
		if (!canCacheIndex)
		{
			var createdIndex = CreateIndex();
			resolved = await createdIndex.Value.ConfigureAwait(false);
		}
		else
		{
			var sharedIndex = false;
			var canceledSharedAttempts = 0;
			while (true)
			{
				IndexCacheEntry? createdIndex = null;
				if (!_indexCache.TryGetValue(cacheKey, out var cachedIndex))
				{
					createdIndex = new IndexCacheEntry(cacheKey, CreateIndex());
					cachedIndex = GetOrAddIndexCacheEntry(createdIndex);
				}
				sharedIndex = createdIndex is null || !ReferenceEquals(cachedIndex, createdIndex);
				if (sharedIndex)
					DependencyEngineDiagnostics.RecordIndexCacheJoin();
				try
				{
					resolved = await cachedIndex.Value.Value.ConfigureAwait(false);
					resolvedIndexEntry = cachedIndex;
					break;
				}
				catch (OperationCanceledException) when (sharedIndex && !cancellationToken.IsCancellationRequested)
				{
					RemoveIndexCacheEntry(cacheKey, cachedIndex);
					if (++canceledSharedAttempts <= MaximumSharedIndexCancellationRetries)
						continue;

					// A live caller must not inherit repeated producer cancellations.
					resolved = await CreateIndex().Value.ConfigureAwait(false);
					sharedIndex = false;
					break;
				}
				catch
				{
					RemoveIndexCacheEntry(cacheKey, cachedIndex);
					throw;
				}
			}
			bool probesCurrent;
			try
			{
				probesCurrent = resolved.CanCachePhysicalFileProbes &&
								ArePhysicalFileProbesCurrent(resolved.PhysicalFileProbes, cancellationToken);
			}
			catch
			{
				if (resolvedIndexEntry is not null)
					RemoveIndexCacheEntry(cacheKey, resolvedIndexEntry);
				throw;
			}
			if (!probesCurrent)
			{
				if (resolvedIndexEntry is not null)
					RemoveIndexCacheEntry(cacheKey, resolvedIndexEntry);
				resolvedIndexEntry = null;
				if (sharedIndex || resolved.CanCachePhysicalFileProbes)
					resolved = await CreateIndex().Value.ConfigureAwait(false);
			}
			resolutionCacheHit = resolvedIndexEntry is not null && sharedIndex;
			if (resolutionCacheHit)
				DependencyEngineDiagnostics.RecordResolutionCacheHit();
			if (resolvedIndexEntry is not null && !resolutionCacheHit)
				RegisterIndexCacheWeight(cacheKey, resolvedIndexEntry, EstimateResolvedIndexBytes(resolved));
		}
		var contentObservations = new Dictionary<string, DependencySourceObservation>(
			manifest.Length,
			PathComparer);
		for (var index = 0; index < manifest.Length; index++)
			contentObservations[manifest[index]] = observations[index];
		var coverage = BuildCoverage(resolved.Files, configuration.ConfigurationDiagnostics);
		var result = new DependencyIndexSnapshot(
			root,
			manifestGeneration,
			declarationRevision,
			resolved.Files,
			declarations,
			resolved.Edges,
			resolved.EdgesBySource,
			resolved.EdgesByTarget,
			coverage,
			new DependencyIndexMetrics(
				parsedFiles,
				reusedFiles,
				resolutionCacheHit ? 0 : orderedFacts.Length,
				started.ElapsedMilliseconds,
				resolutionCacheHit)
			{
				AccumulatedFactBytes = factBudget.UsedBytes,
				MaximumAccumulatedFactBytes = _limits.MaximumAccumulatedFactBytes,
				FactBudgetLimitedFiles = factBudget.LimitedFiles,
				ResolverContextEstimatedBytes = resolved.ResolverContextEstimatedBytes,
				ResolvedIndexEstimatedBytes = EstimateResolvedIndexBytes(resolved)
			})
		{
			FileByPath = resolved.FileByPath,
			ContentObservations = contentObservations
		};
		var finalStamps = TryCaptureFileStamps(manifest, cancellationToken);
		if (canCacheIndex && resolvedIndexEntry is not null &&
			initialStamps is not null && finalStamps is not null && initialStamps.SequenceEqual(finalStamps) &&
			AreControlFilesStillAbsent(configuration.AbsentControlFiles, cancellationToken))
		{
			StoreManifestSnapshot(
				manifestRequestKey,
				manifestRelativePaths,
				initialStamps,
				alignedContentIdentities,
				configuration.AbsentControlFiles,
				resolved.PhysicalFileProbes,
				cacheKey,
				resolvedIndexEntry,
				result);
		}
		cancellationToken.ThrowIfCancellationRequested();
		return result;
	}

	public async Task<DependencyRelatedResult> FindRelatedAsync(
		string sourceRoot,
		IReadOnlyList<string> manifestFiles,
		IReadOnlyList<string> seedRelativePaths,
		DependencyDirection direction = DependencyDirection.Both,
		IProgress<DependencyIndexProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		var index = await IndexAsync(sourceRoot, manifestFiles, progress, cancellationToken)
			.ConfigureAwait(false);
		return FindRelated(index, seedRelativePaths, direction);
	}

	public async Task<DependencyRelatedResult> FindRelatedAsync(
		string sourceRoot,
		IReadOnlyList<string> manifestFiles,
		IReadOnlyList<string> seedRelativePaths,
		DependencyDirection direction,
		int depth,
		IProgress<DependencyIndexProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		if (depth < 1)
			throw new ArgumentOutOfRangeException(nameof(depth));

		var index = await IndexAsync(sourceRoot, manifestFiles, progress, cancellationToken)
			.ConfigureAwait(false);
		var normalizedSeeds = seedRelativePaths.Select(Normalize).ToArray();
		var visited = new HashSet<string>(normalizedSeeds, StringComparer.Ordinal);
		if (visited.Count > MaximumRelatedTraversalSeeds)
			throw new DependencyTraversalLimitException(MaximumRelatedTraversalSeeds);
		IReadOnlyList<string> frontier = normalizedSeeds;
		var seeds = new List<SeedRelatedFiles>();
		for (var level = 0; level < depth && frontier.Count > 0; level++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var result = FindRelated(index, frontier, direction);
			seeds.AddRange(result.Seeds);
			if (level + 1 >= depth)
				break;

			var next = new HashSet<string>(StringComparer.Ordinal);
			foreach (var path in result.Seeds
						 .SelectMany(seed => RelatedFiles(seed, direction))
						 .Where(file => file.Status == ResolutionStatus.Resolved &&
							 index.FileByPath.ContainsKey(file.Path))
						 .Select(static file => file.Path))
			{
				if (visited.Contains(path) || !next.Add(path))
					continue;
				if (visited.Count + next.Count > MaximumRelatedTraversalSeeds)
					throw new DependencyTraversalLimitException(MaximumRelatedTraversalSeeds);
			}

			frontier = next.Order(StringComparer.Ordinal).ToArray();
			visited.UnionWith(frontier);
		}

		return new DependencyRelatedResult(index, seeds);
	}

	private static DependencyRelatedResult FindRelated(
		DependencyIndexSnapshot index,
		IReadOnlyList<string> seedRelativePaths,
		DependencyDirection direction)
	{
		var fileByPath = index.FileByPath;
		var seeds = new List<SeedRelatedFiles>(seedRelativePaths.Count);
		foreach (var rawSeed in seedRelativePaths)
		{
			var seed = Normalize(rawSeed);
			if (!fileByPath.TryGetValue(seed, out var facts))
				throw new ArgumentException($"Seed '{seed}' is outside the dependency manifest.", nameof(seedRelativePaths));
			if (facts.Status != DependencyFileStatus.Supported)
			{
				seeds.Add(new SeedRelatedFiles(seed, facts.LanguageId, [], [], facts.StatusReason));
				continue;
			}
			var dependencies = direction == DependencyDirection.Dependents
				? []
				: ProjectDependencies(seed, index.EdgesBySource.GetValueOrDefault(seed) ?? [], fileByPath);
			var dependents = direction == DependencyDirection.Dependencies
				? []
				: ProjectDependents(seed, index.EdgesByTarget.GetValueOrDefault(seed) ?? [], fileByPath);
			seeds.Add(new SeedRelatedFiles(seed, facts.LanguageId, dependencies, dependents, null));
		}
		return new DependencyRelatedResult(index, seeds);
	}

	private static IEnumerable<RelatedFile> RelatedFiles(
		SeedRelatedFiles seed,
		DependencyDirection direction)
	{
		if (direction is DependencyDirection.Dependencies or DependencyDirection.Both)
		{
			foreach (var dependency in seed.Dependencies)
				yield return dependency;
		}
		if (direction is DependencyDirection.Dependents or DependencyDirection.Both)
		{
			foreach (var dependent in seed.Dependents)
				yield return dependent;
		}
	}

	private static FileFacts RebindScope(FileFacts facts, string scopeId)
	{
		if (facts.ScopeId == scopeId)
			return facts;
		return facts with
		{
			ScopeId = scopeId,
			Declarations = facts.Declarations.Select(declaration => declaration with
			{
				Identity = declaration.Identity with { ScopeId = scopeId }
			}).ToArray()
		};
	}

	private static FileFacts CreateFactBudgetLimited(FileFacts facts, string scopeId) => new(
		facts.Path,
		scopeId,
		facts.LanguageId,
		facts.ContentFingerprint,
		facts.CharacterCount,
		DependencyFileStatus.ExtractionFailed,
		AccumulatedFactBudgetReason,
		false,
		EmptyErrorNodeKinds,
		[],
		[],
		[],
		[],
		EmptyAliases,
		[],
		EmptyAliases,
		[])
	{
		CanCache = facts.CanCache,
		FactBudgetLimited = true
	};

	private static IReadOnlyList<RelatedFile> ProjectDependencies(
		string seed,
		IReadOnlyList<DependencyEdge> sourceEdges,
		IReadOnlyDictionary<string, FileFacts> files)
	{
		var resolved = sourceEdges
			.Where(static edge => edge.Status == ResolutionStatus.Resolved)
			.SelectMany(edge => ResolvedTargetFiles(edge)
				.Where(path => path != seed)
				.Select(path => (Path: path, Edge: edge)))
			.GroupBy(static item => item.Path, StringComparer.Ordinal)
			.Select(group => ToRelated(group.Key, ResolutionStatus.Resolved,
				group.Select(static item => item.Edge), files));
		var ambiguous = sourceEdges
			.Where(static edge => edge.Status == ResolutionStatus.Ambiguous)
			.Select(edge => (Edge: edge, Path: edge.Candidates.Order(StringComparer.Ordinal)
				.FirstOrDefault(candidate => candidate != seed)))
			.Where(static item => item.Path is not null)
			.GroupBy(item => new RelatedGroupKey(
				item.Path!,
				item.Edge.Layer,
				item.Edge.Reference,
				Hash(item.Edge.Candidates.Order(StringComparer.Ordinal))))
			.Select(group => ToRelated(group.Key.Path, ResolutionStatus.Ambiguous,
				group.Select(static item => item.Edge), files));
		return resolved.Concat(ambiguous)
			.OrderBy(static item => item.Path, StringComparer.Ordinal)
			.ThenBy(static item => item.Status)
			.ThenBy(static item => string.Join('\0', item.Candidates), StringComparer.Ordinal)
			.ToArray();
	}

	private static IReadOnlyList<RelatedFile> ProjectDependents(
		string seed,
		IReadOnlyList<DependencyEdge> targetEdges,
		IReadOnlyDictionary<string, FileFacts> files)
	{
		var eligible = targetEdges.Where(edge => edge.Source != seed).ToArray();
		var resolved = eligible
			.Where(static edge => edge.Status == ResolutionStatus.Resolved)
			.GroupBy(static edge => edge.Source, StringComparer.Ordinal)
			.Select(group => ToRelated(group.Key, ResolutionStatus.Resolved, group, files));
		var ambiguous = eligible
			.Where(static edge => edge.Status == ResolutionStatus.Ambiguous)
			.GroupBy(edge => new RelatedGroupKey(
				edge.Source,
				edge.Layer,
				edge.Reference,
				Hash(edge.Candidates.Order(StringComparer.Ordinal))))
			.Select(group => ToRelated(group.Key.Path, ResolutionStatus.Ambiguous, group, files));
		return resolved.Concat(ambiguous)
			.OrderBy(static item => item.Path, StringComparer.Ordinal)
			.ThenBy(static item => item.Status)
			.ThenBy(static item => string.Join('\0', item.Candidates), StringComparer.Ordinal)
			.ToArray();
	}

	private static RelatedFile ToRelated(
		string path,
		ResolutionStatus status,
		IEnumerable<DependencyEdge> groupedEdges,
		IReadOnlyDictionary<string, FileFacts> files)
	{
		var edges = groupedEdges.ToArray();
		var declarationPartReasons = edges
			.Where(static edge => edge.Status == ResolutionStatus.Resolved && edge.DeclarationFiles.Count > 1)
			.Select(static edge => $"declaration part of one resolved symbol with {edge.DeclarationFiles.Count} files");
		return new RelatedFile(
			path,
			status,
			edges.SelectMany(static edge => edge.Reasons.Concat(edge.Evidence.Select(site =>
				$"{EvidenceLabel(edge.Layer)} {edge.Reference} at line {site.Line}")))
				.Concat(declarationPartReasons)
				.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
			edges.SelectMany(static edge => edge.Candidates).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
			edges.Any(static edge => edge.CrossScope),
			files.TryGetValue(path, out var facts) ? EstimateTokens(facts.CharacterCount) : 0);
	}

	private static string EvidenceLabel(EvidenceLayer layer) => layer switch
	{
		EvidenceLayer.ExplicitImport => "import",
		EvidenceLayer.TypeReference => "type reference",
		_ => "reference"
	};

	private static long EstimateTokens(int characters) => (characters + 3L) / 4L;

	private static IReadOnlyList<string> ResolvedTargetFiles(DependencyEdge edge) =>
		edge.DeclarationFiles.Count > 0
			? edge.DeclarationFiles
			: edge.Target is null ? [] : [edge.Target];

	private static (
		IReadOnlyDictionary<string, IReadOnlyList<DependencyEdge>> BySource,
		IReadOnlyDictionary<string, IReadOnlyList<DependencyEdge>> ByTarget) BuildEdgeIndexes(
		IReadOnlyList<DependencyEdge> edges)
	{
		var bySource = edges
			.GroupBy(static edge => edge.Source, StringComparer.Ordinal)
			.OrderBy(static group => group.Key, StringComparer.Ordinal)
			.ToDictionary(
				static group => group.Key,
				static group => (IReadOnlyList<DependencyEdge>)group.ToArray(),
				StringComparer.Ordinal);
		var byTarget = edges
			.SelectMany(static edge => (edge.Status == ResolutionStatus.Resolved
					? ResolvedTargetFiles(edge)
					: edge.Candidates)
				.Distinct(StringComparer.Ordinal)
				.Select(target => (Target: target, Edge: edge)))
			.GroupBy(static item => item.Target, StringComparer.Ordinal)
			.OrderBy(static group => group.Key, StringComparer.Ordinal)
			.ToDictionary(
				static group => group.Key,
				static group => (IReadOnlyList<DependencyEdge>)group.Select(static item => item.Edge)
					.Distinct().OrderBy(static edge => edge.Source, StringComparer.Ordinal).ToArray(),
				StringComparer.Ordinal);
		return (bySource, byTarget);
	}

	private static IReadOnlyList<DeclarationFact> MergeDeclarations(IEnumerable<FileFacts> facts) =>
		facts.SelectMany(static file => file.Declarations)
			.GroupBy(static declaration => declaration.Identity)
			.Select(static group => new DeclarationFact(
				group.Key,
				group.SelectMany(static item => item.DeclarationSites)
					.Distinct()
					.OrderBy(static site => site.File, StringComparer.Ordinal)
					.ThenBy(static site => site.Line)
					.ToArray())
			{
				ContainingNamespace = group.Select(static item => item.ContainingNamespace)
					.Order(StringComparer.Ordinal).FirstOrDefault() ?? string.Empty,
				ContainingType = group.Select(static item => item.ContainingType)
					.Order(StringComparer.Ordinal).FirstOrDefault()
			})
			.OrderBy(static declaration => declaration.Identity.ScopeId, StringComparer.Ordinal)
			.ThenBy(static declaration => declaration.Identity.QualifiedName, StringComparer.Ordinal)
			.ThenBy(static declaration => declaration.Identity.GenericArity)
			.ToArray();

	private static string DeclarationKey(DeclarationFact declaration) =>
		$"{declaration.Identity.ScopeId}\0{declaration.Identity.LanguageId}\0" +
		$"{declaration.Identity.SymbolKind}\0{declaration.Identity.QualifiedName}\0" +
		$"{declaration.Identity.GenericArity}\0{declaration.Identity.FileScope}\0" +
		string.Join('\0', declaration.DeclarationSites.Select(static site => $"{site.File}:{site.Line}"));

	private static DependencyFactsCoverage BuildCoverage(
		IReadOnlyList<FileFacts> files,
		IReadOnlyList<DependencyConfigurationDiagnostic> configurationDiagnostics) =>
		new(
			files.Count,
			files.Count(static file => file.Status == DependencyFileStatus.Supported),
			files.Count(static file => file.Status == DependencyFileStatus.Unsupported),
			files.Count(static file => file.Status == DependencyFileStatus.ExtractionFailed),
			files.Where(static file => file.Status == DependencyFileStatus.Unsupported)
				.GroupBy(static file => file.LanguageId.ToString())
				.ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal),
			files.Where(static file => file.LanguageId == LanguageId.CSharp)
				.SelectMany(static file => file.ErrorNodeKinds)
				.GroupBy(static pair => pair.Key)
				.ToDictionary(static group => group.Key, static group => group.Sum(static pair => pair.Value), StringComparer.Ordinal))
		{
			ConfigurationDiagnostics = configurationDiagnostics,
			ExtractionFailedFiles = files
				.Where(static file => file.Status == DependencyFileStatus.ExtractionFailed)
				.Select(static file => file.Path)
				.Order(StringComparer.Ordinal)
				.ToArray(),
			PartialParseDiagnostics = files
				.Where(static file => file.PartialParse is not null)
				.Select(static file => file.PartialParse!)
				.OrderBy(static diagnostic => diagnostic.Path, StringComparer.Ordinal)
				.ToArray(),
			FactBudgetLimitedFiles = files
				.Where(static file => file.FactBudgetLimited)
				.Select(static file => file.Path)
				.Order(StringComparer.Ordinal)
				.ToArray()
		};

	private FileCacheEntry GetOrAddFileCacheEntry(FileCacheEntry created)
	{
		lock (_cacheTrimSync)
		{
			if (_fileCache.TryGetValue(created.Key, out var existing))
				return existing;
			_fileCache[created.Key] = created;
			created.OrderNode = _fileCacheOrder.AddLast(created);
			return created;
		}
	}

	private IndexCacheEntry GetOrAddIndexCacheEntry(IndexCacheEntry created)
	{
		lock (_cacheTrimSync)
		{
			if (_indexCache.TryGetValue(created.Key, out var existing))
				return existing;
			_indexCache[created.Key] = created;
			created.OrderNode = _indexCacheOrder.AddLast(created);
			return created;
		}
	}

	private void RemoveFileCacheEntry(FileCacheKey key, FileCacheEntry entry)
	{
		lock (_cacheTrimSync)
			RemoveFileCacheEntryUnderLock(key, entry);
	}

	private void RemoveIndexCacheEntry(IndexCacheKey key, IndexCacheEntry entry)
	{
		lock (_cacheTrimSync)
			RemoveIndexCacheEntryUnderLock(key, entry);
	}

	// A failed or evicted generation must not remove a replacement with the same key.
	private void RemoveFileCacheEntryUnderLock(FileCacheKey key, FileCacheEntry entry)
	{
		if (!_fileCache.TryRemove(new KeyValuePair<FileCacheKey, FileCacheEntry>(key, entry)))
			return;
		if (entry.OrderNode is not null)
		{
			_fileCacheOrder.Remove(entry.OrderNode);
			entry.OrderNode = null;
		}
		_fileCacheBytes -= entry.RegisteredWeight;
		entry.RegisteredWeight = 0;
	}

	private void RemoveIndexCacheEntryUnderLock(IndexCacheKey key, IndexCacheEntry entry)
	{
		if (!_indexCache.TryRemove(new KeyValuePair<IndexCacheKey, IndexCacheEntry>(key, entry)))
			return;
		if (entry.OrderNode is not null)
		{
			_indexCacheOrder.Remove(entry.OrderNode);
			entry.OrderNode = null;
		}
		_indexCacheBytes -= entry.RegisteredWeight;
		entry.RegisteredWeight = 0;
		foreach (var snapshot in _manifestSnapshots.Where(pair => pair.Value.IndexCacheKey == key).ToArray())
			RemoveManifestSnapshotUnderLock(snapshot.Key, snapshot.Value);
	}

	private void RegisterFileCacheWeight(
		FileCacheKey key,
		FileCacheEntry entry,
		long weight)
	{
		lock (_cacheTrimSync)
		{
			if (_fileCache.TryGetValue(key, out var current) && ReferenceEquals(current, entry) &&
				entry.RegisteredWeight == 0)
			{
				entry.RegisteredWeight = weight;
				_fileCacheBytes += weight;
			}
			TrimFileCache();
		}
	}

	private void RegisterIndexCacheWeight(
		IndexCacheKey key,
		IndexCacheEntry entry,
		long weight)
	{
		lock (_cacheTrimSync)
		{
			if (_indexCache.TryGetValue(key, out var current) && ReferenceEquals(current, entry) &&
				entry.RegisteredWeight == 0)
			{
				entry.RegisteredWeight = weight;
				_indexCacheBytes += weight;
			}
			TrimIndexCache();
		}
	}

	private void TrimFileCache()
	{
		while ((_fileCache.Count > _limits.MaximumCachedFiles || _fileCacheBytes > _limits.MaximumFileCacheBytes) &&
			   _fileCacheOrder.First is { Value: var oldest })
			RemoveFileCacheEntryUnderLock(oldest.Key, oldest);
	}

	private void TrimIndexCache()
	{
		while ((_indexCache.Count > _limits.MaximumCachedIndexes || _indexCacheBytes > _limits.MaximumIndexCacheBytes) &&
			   _indexCacheOrder.First is { Value: var oldest })
			RemoveIndexCacheEntryUnderLock(oldest.Key, oldest);
	}

	private void StoreManifestSnapshot(
		ManifestRequestKey key,
		IReadOnlyList<string> manifestPaths,
		IReadOnlyList<FileStamp> stamps,
		IReadOnlyList<string>? contentIdentities,
		IReadOnlyList<string> absentControlFiles,
		IReadOnlyList<PhysicalFileProbe> physicalFileProbes,
		IndexCacheKey indexCacheKey,
		IndexCacheEntry expectedIndexEntry,
		DependencyIndexSnapshot snapshot)
	{
		lock (_cacheTrimSync)
		{
			// A retired generation must not retain another graph behind its replacement's budget.
			if (!_indexCache.TryGetValue(indexCacheKey, out var current) ||
				!ReferenceEquals(current, expectedIndexEntry))
				return;
			if (_manifestSnapshots.TryGetValue(key, out var previous))
				RemoveManifestSnapshotUnderLock(key, previous);
			var entry = new ManifestSnapshotCacheEntry(
				key, manifestPaths, stamps, contentIdentities, absentControlFiles,
				physicalFileProbes, indexCacheKey, snapshot);
			_manifestSnapshots[key] = entry;
			entry.OrderNode = _manifestSnapshotOrder.AddLast(entry);
			while (_manifestSnapshots.Count > _limits.MaximumCachedIndexes &&
				   _manifestSnapshotOrder.First is { Value: var oldest })
				RemoveManifestSnapshotUnderLock(oldest.Key, oldest);
		}
	}

	private void RemoveManifestSnapshotUnderLock(
		ManifestRequestKey key,
		ManifestSnapshotCacheEntry entry)
	{
		if (!_manifestSnapshots.TryRemove(
				new KeyValuePair<ManifestRequestKey, ManifestSnapshotCacheEntry>(key, entry)))
			return;
		if (entry.OrderNode is null)
			return;
		_manifestSnapshotOrder.Remove(entry.OrderNode);
		entry.OrderNode = null;
	}

	private static FileCacheKey CreateFileCacheKey(PreparedDependencySource source) => new(
		Path.GetFullPath(source.FullPath),
		source.RelativePath,
		source.ContentFingerprint,
		source.LanguageId,
		source.ExtractorIdentity);

	private static IReadOnlyList<FileStamp>? TryCaptureFileStamps(
		IReadOnlyList<string> manifest,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		try
		{
			var stamps = new FileStamp[manifest.Count];
			for (var index = 0; index < manifest.Count; index++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var info = new FileInfo(manifest[index]);
				if (!info.Exists) return null;
				stamps[index] = new FileStamp(
					info.Length,
					info.LastWriteTimeUtc.Ticks,
					info.CreationTimeUtc.Ticks);
			}
			return stamps;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return null;
		}
	}

	private static IReadOnlyList<string>? AlignContentIdentities(
		IReadOnlyList<string> manifest,
		DependencyManifestContentIdentities? contentIdentities,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (contentIdentities is null)
			return null;
		var aligned = new string[manifest.Count];
		for (var index = 0; index < manifest.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!contentIdentities.ByFullPath.TryGetValue(manifest[index], out var identity))
			{
				throw new ArgumentException(
					$"A content identity is required for manifest file '{manifest[index]}'.",
					nameof(contentIdentities));
			}
			aligned[index] = identity;
		}
		return aligned;
	}

	private static bool ContentIdentitiesMatch(
		IReadOnlyList<string>? cached,
		IReadOnlyList<string>? requested)
	{
		if (requested is null)
			return true;
		return cached is not null && cached.SequenceEqual(requested, StringComparer.Ordinal);
	}

	internal static long EstimateFileFactsBytes(FileFacts facts) =>
		EstimateFileFactsBytes(facts, new RetainedStringEstimator());

	private static long EstimateFileFactsBytes(FileFacts facts, RetainedStringEstimator strings) =>
		256 + strings.Add(facts.Path) + strings.Add(facts.ScopeId) + strings.Add(facts.ContentFingerprint) +
		strings.Add(facts.StatusReason) +
		facts.ErrorNodeKinds.Sum(pair => strings.Add(pair.Key) + 16) +
		(facts.PartialParse is null
			? 0
			: 64 + strings.Add(facts.PartialParse.Path) + facts.PartialParse.Ranges.Count * 16L) +
		facts.Declarations.Sum(declaration => 160 + strings.Add(declaration.Identity.ScopeId) +
			strings.Add(declaration.Identity.QualifiedName) + strings.Add(declaration.Identity.FileScope) +
			strings.Add(declaration.ContainingNamespace) +
			declaration.DeclarationSites.Sum(site => SiteBytes(site, strings))) +
		facts.NavigationDeclarations.Sum(declaration => 96 + strings.Add(declaration.Name) +
			strings.Add(declaration.Owner) + strings.Add(declaration.ContentFingerprint)) +
		facts.Imports.Sum(import => 128 + strings.Add(import.Specifier) + strings.Add(import.ImportedName) +
			strings.Add(import.Alias) + strings.Add(import.ContainingDeclaration) + SiteBytes(import.Site, strings)) +
		facts.References.Sum(reference => 160 + strings.Add(reference.Name) + strings.Add(reference.SyntaxKind) +
			strings.Add(reference.Reason) + strings.Add(reference.Target) +
			(reference.Candidates?.Sum(strings.Add) ?? 0) +
			strings.Add(reference.ContainingNamespace) + strings.Add(reference.ContainingType) +
			SiteBytes(reference.Site, strings)) +
		facts.ContextNamespaces.Sum(strings.Add) +
		facts.Aliases.Sum(pair => strings.Add(pair.Key) + strings.Add(pair.Value)) +
		facts.GlobalContextNamespaces.Sum(strings.Add) +
		facts.GlobalAliases.Sum(pair => strings.Add(pair.Key) + strings.Add(pair.Value)) +
		facts.TypeParameters.Sum(strings.Add) +
		facts.TypeParameterScopes.Sum(scope => 40 + strings.Add(scope.Name)) +
		facts.CSharpUsingDirectives.Sum(directive => 64 + strings.Add(directive.Target) + strings.Add(directive.Alias)) +
		facts.ScalaImportDirectives.Sum(directive => 64 + strings.Add(directive.Specifier) + strings.Add(directive.Alias) + strings.Add(directive.ContainingNamespace)) +
		facts.ScalaValueScopes.Sum(scope => 48 + strings.Add(scope.Name));

	private static long EstimateResolvedIndexBytes(ResolvedIndex index)
	{
		var strings = new RetainedStringEstimator();
		return 256 + index.Edges.Sum(edge => 192 + strings.Add(edge.Source) + strings.Add(edge.Target) +
			strings.Add(edge.Reference) + edge.Reasons.Sum(strings.Add) +
			edge.Evidence.Sum(site => SiteBytes(site, strings)) +
			edge.Candidates.Sum(strings.Add) + edge.DeclarationFiles.Sum(strings.Add)) +
			index.Files.Sum(file => EstimateFileFactsBytes(file, strings)) +
			index.PhysicalFileProbes.Sum(probe => 32 + strings.Add(probe.Path));
	}

	private static bool AreControlFilesStillAbsent(IEnumerable<string> paths, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		foreach (var path in paths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (File.Exists(path))
				return false;
		}
		return true;
	}

	private static bool ArePhysicalFileProbesCurrent(
		IReadOnlyList<PhysicalFileProbe> probes,
		CancellationToken cancellationToken)
	{
		for (var index = 0; index < probes.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var probe = probes[index];
			if (File.Exists(probe.Path) != probe.Exists)
				return false;
		}
		return true;
	}

	private static long SiteBytes(SourceSite site, RetainedStringEstimator strings) =>
		64 + strings.Add(site.File) + strings.Add(site.Evidence);

	private sealed class RetainedStringEstimator
	{
		private readonly HashSet<string> _seen = new(ReferenceEqualityComparer.Instance);

		public long Add(string? value) =>
			value is not null && _seen.Add(value) ? 24 + value.Length * 2L : 0;
	}

	private static ResolvedIndex GateResolvedIndex(ResolvedIndex index, IReadOnlySet<string> allowed)
	{
		var files = new FileFacts[index.Files.Count];
		var filesChanged = false;
		for (var indexValue = 0; indexValue < files.Length; indexValue++)
		{
			var file = index.Files[indexValue];
			var imports = GateImports(file.Imports, allowed);
			var references = GateReferences(file.References, allowed);
			files[indexValue] = ReferenceEquals(imports, file.Imports) && ReferenceEquals(references, file.References)
				? file
				: file with { Imports = imports, References = references };
			if (!ReferenceEquals(files[indexValue], file))
			{
				filesChanged = true;
				DependencyEngineDiagnostics.RecordFileFactsClone();
			}
		}
		var edges = index.Edges.Where(edge => allowed.Contains(edge.Source) &&
			(edge.Target is null || allowed.Contains(edge.Target) || edge.Target.StartsWith("namespace:", StringComparison.Ordinal)) &&
			edge.Candidates.All(allowed.Contains) &&
			edge.DeclarationFiles.All(allowed.Contains)).ToArray();
		var (bySource, byTarget) = BuildEdgeIndexes(edges);
		var gatedFiles = filesChanged ? files : index.Files;
		var fileByPath = index.FileByPath;
		if (filesChanged)
		{
			fileByPath = files.ToDictionary(static file => file.Path, StringComparer.Ordinal);
			DependencyEngineDiagnostics.RecordDictionaryBuild();
		}
		return index with
		{
			Files = gatedFiles,
			FileByPath = fileByPath,
			Edges = edges,
			EdgesBySource = bySource,
			EdgesByTarget = byTarget
		};
	}

	private static IReadOnlyList<ImportFact> GateImports(
		IReadOnlyList<ImportFact> facts,
		IReadOnlySet<string> allowed)
	{
		if (facts.All(fact => (fact.Target is null || allowed.Contains(fact.Target)) &&
							(fact.Candidates ?? []).All(allowed.Contains)))
			return facts;
		return facts.Select(fact =>
		{
			var candidates = (fact.Candidates ?? []).Where(allowed.Contains).Order(StringComparer.Ordinal).ToArray();
			if (fact.Target is null || allowed.Contains(fact.Target))
				return fact with { Candidates = candidates };
			return fact with
			{
				Status = ResolutionStatus.Unresolved,
				Reason = "cached target is outside the current manifest",
				Candidates = candidates,
				Target = null
			};
		}).ToArray();
	}

	private static IReadOnlyList<ReferenceFact> GateReferences(
		IReadOnlyList<ReferenceFact> facts,
		IReadOnlySet<string> allowed)
	{
		if (facts.All(fact => (fact.Target is null || allowed.Contains(fact.Target)) &&
							(fact.Candidates ?? []).All(allowed.Contains)))
			return facts;
		return facts.Select(fact =>
		{
			var candidates = (fact.Candidates ?? []).Where(allowed.Contains).Order(StringComparer.Ordinal).ToArray();
			if (fact.Target is null || allowed.Contains(fact.Target))
				return fact with { Candidates = candidates };
			return fact with
			{
				Status = ResolutionStatus.Unresolved,
				Reason = "cached target is outside the current manifest",
				Candidates = candidates,
				Target = null
			};
		}).ToArray();
	}

	private static string Hash(IEnumerable<string> values) => HashWithCancellation(values, CancellationToken.None);

	private static string HashWithCancellation(IEnumerable<string> values, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		Span<byte> lengthPrefix = stackalloc byte[sizeof(int)];
		var buffer = ArrayPool<byte>.Shared.Rent(4096);
		Encoder? encoder = null;
		try
		{
			foreach (var value in values)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var byteCount = Encoding.UTF8.GetByteCount(value);
				BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, byteCount);
				hash.AppendData(lengthPrefix);
				if (byteCount <= buffer.Length)
				{
					var written = Encoding.UTF8.GetBytes(value.AsSpan(), buffer);
					hash.AppendData(buffer.AsSpan(0, written));
					continue;
				}
				encoder ??= Encoding.UTF8.GetEncoder();
				encoder.Reset();
				var remaining = value.AsSpan();
				do
				{
					cancellationToken.ThrowIfCancellationRequested();
					encoder.Convert(
						remaining,
						buffer,
						flush: true,
						out var charactersUsed,
						out var bytesUsed,
						out _);
					if (bytesUsed > 0)
						hash.AppendData(buffer.AsSpan(0, bytesUsed));
					remaining = remaining[charactersUsed..];
				} while (!remaining.IsEmpty);
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(buffer);
			ArrayPool<byte>.Shared.Return(buffer);
		}

		cancellationToken.ThrowIfCancellationRequested();
		return Convert.ToHexStringLower(hash.GetHashAndReset());
	}

	private static CanonicalManifestFile[] CreateCanonicalManifest(
		string root,
		IReadOnlyList<string> manifestFiles,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var unique = new Dictionary<string, CanonicalManifestFile>(manifestFiles.Count, PathComparer);
		foreach (var path in manifestFiles)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var fullPath = Path.GetFullPath(path);
			if (!IsWithin(root, fullPath) || unique.ContainsKey(fullPath))
				continue;
			DependencyEngineDiagnostics.RecordPathNormalization();
			unique.Add(fullPath, new CanonicalManifestFile(fullPath, PortableRelative(root, fullPath)));
		}
		cancellationToken.ThrowIfCancellationRequested();
		DependencyEngineDiagnostics.RecordManifestSort();
		var ordered = unique.Values.OrderBy(static file => file.RelativePath, StringComparer.Ordinal).ToArray();
		cancellationToken.ThrowIfCancellationRequested();
		return ordered;
	}

	private static bool IsWithin(string root, string path)
	{
		var relative = Path.GetRelativePath(root, path);
		return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
			   !Path.IsPathRooted(relative);
	}

	private static string PortableRelative(string root, string path) => Normalize(Path.GetRelativePath(root, path));
	private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
	private const string MissingTypeScriptConfigurationReason =
		"no owning tsconfig.json or jsconfig.json in the manifest";

	private static StringComparer PathComparer => OperatingSystem.IsWindows()
		? StringComparer.OrdinalIgnoreCase
		: StringComparer.Ordinal;

	private static readonly IReadOnlyDictionary<string, DependencySourceObservation> NoContentObservations =
		new Dictionary<string, DependencySourceObservation>(PathComparer);
	private static readonly IReadOnlyDictionary<string, int> EmptyErrorNodeKinds =
		new Dictionary<string, int>(StringComparer.Ordinal);
	private static readonly IReadOnlyDictionary<string, string> EmptyAliases =
		new Dictionary<string, string>(StringComparer.Ordinal);

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
		{
			lock (_navigationCacheSync)
			{
				_navigationCache.Clear();
				_navigationCacheOrder.Clear();
				_navigationCacheBytes = 0;
			}
			lock (_cacheTrimSync)
			{
				_manifestSnapshots.Clear();
				_manifestSnapshotOrder.Clear();
			}
			_extractor.Dispose();
		}
	}

	private readonly record struct FileCacheKey(
		string Path,
		string RelativePath,
		string Fingerprint,
		LanguageId LanguageId,
		string ExtractorIdentity);

	private readonly record struct NavigationCacheKey(string RelativePath, string ContentFingerprint);

	private sealed record NavigationCacheEntry(
		NavigationCacheKey Key,
		IReadOnlyList<NavigationDeclaration> Declarations,
		long Weight);

	private readonly record struct PreparedDependencyIdentity(
		string RelativePath,
		string ContentFingerprint,
		LanguageId LanguageId);

	private sealed class AccumulatedFactBudget(long maximumBytes)
	{
		private long _usedBytes;
		private int _limitedFiles;
		public long UsedBytes => _usedBytes;

		public int LimitedFiles => _limitedFiles;

		public bool TryAdmit(long estimatedBytes)
		{
			if (estimatedBytes <= maximumBytes - _usedBytes)
			{
				_usedBytes += estimatedBytes;
				return true;
			}

			_limitedFiles++;
			return false;
		}
	}

	private readonly record struct CanonicalManifestFile(string FullPath, string RelativePath);

	private readonly record struct IndexCacheKey(
		string SourceRoot,
		string ManifestGeneration,
		string DeclarationRevision,
		string ConfigurationFingerprint);

	private readonly record struct ManifestRequestKey(
		string SourceRoot,
		string ManifestPathsFingerprint);

	private readonly record struct RelatedGroupKey(
		string Path,
		EvidenceLayer Layer,
		string Reference,
		string CandidatesFingerprint);

	private readonly record struct FileStamp(
		long Length,
		long LastWriteTimeUtcTicks,
		long CreationTimeUtcTicks);

	private readonly record struct PhysicalFileProbe(string Path, bool Exists);

	private sealed class FileCacheEntry(FileCacheKey key, Lazy<Task<FileFacts>> value)
	{
		public FileCacheKey Key { get; } = key;
		public Lazy<Task<FileFacts>> Value { get; } = value;
		public long RegisteredWeight { get; set; }
		public LinkedListNode<FileCacheEntry>? OrderNode { get; set; }
	}

	private sealed class IndexCacheEntry(IndexCacheKey key, Lazy<Task<ResolvedIndex>> value)
	{
		public IndexCacheKey Key { get; } = key;
		public Lazy<Task<ResolvedIndex>> Value { get; } = value;
		public long RegisteredWeight { get; set; }
		public LinkedListNode<IndexCacheEntry>? OrderNode { get; set; }
	}

	private sealed record ManifestSnapshotCacheEntry(
		ManifestRequestKey Key,
		IReadOnlyList<string> ManifestPaths,
		IReadOnlyList<FileStamp> Stamps,
		IReadOnlyList<string>? ContentIdentities,
		IReadOnlyList<string> AbsentControlFiles,
		IReadOnlyList<PhysicalFileProbe> PhysicalFileProbes,
		IndexCacheKey IndexCacheKey,
		DependencyIndexSnapshot Snapshot)
	{
		public LinkedListNode<ManifestSnapshotCacheEntry>? OrderNode { get; set; }
	}

	internal readonly record struct DependencyFactsCacheState(
		int ManifestSnapshots,
		int ManifestEvictionEntries,
		int ResolvedIndexes,
		long ResolvedIndexBytes,
		int IndexEvictionEntries,
		int Files,
		int FileEvictionEntries,
		long FileBytes);

	private sealed record ResolvedIndex(
		IReadOnlyList<DependencyEdge> Edges,
		IReadOnlyList<FileFacts> Files,
		IReadOnlyDictionary<string, FileFacts> FileByPath,
		IReadOnlyDictionary<string, IReadOnlyList<DependencyEdge>> EdgesBySource,
		IReadOnlyDictionary<string, IReadOnlyList<DependencyEdge>> EdgesByTarget)
	{
		public long ResolverContextEstimatedBytes { get; init; }
		public IReadOnlyList<PhysicalFileProbe> PhysicalFileProbes { get; init; } = [];
		public bool CanCachePhysicalFileProbes { get; init; } = true;
	}

	private static class DependencyResolver
	{
		public static ResolvedIndex Resolve(
			string root,
			IReadOnlyList<FileFacts> files,
			IReadOnlyList<DeclarationFact> declarations,
			DependencyResolverConfiguration configuration,
			DependencyFactsLimits limits,
			CancellationToken cancellationToken)
		{
			var context = new ResolverContext(root, files, declarations, configuration);
			var resolved = new List<DependencyEdge>();
			var supportedFiles = files.Where(static file => file.Status == DependencyFileStatus.Supported).ToArray();
			var parallelism = Math.Clamp(Environment.ProcessorCount, 1, 8);
			var plans = CreateWorkPlans(supportedFiles, context, limits, cancellationToken);
			var completed = new ResolvedFileWork[plans.Length];
			Parallel.For(0, plans.Length, new ParallelOptions
			{
				MaxDegreeOfParallelism = parallelism,
				CancellationToken = cancellationToken
			}, index =>
			{
				var plan = plans[index];
				if (plan.LimitReason is { } limitReason)
				{
					completed[index] = new ResolvedFileWork(
						plan.File,
						null,
						null,
						[LimitEdge(plan.File, limitReason)]);
					return;
				}

				var imports = new ImportFact[plan.File.Imports.Count];
				var references = new ReferenceFact[plan.File.References.Count];
				var edges = new DependencyEdge[imports.Length + references.Length];
				for (var factIndex = 0; factIndex < imports.Length; factIndex++)
				{
					cancellationToken.ThrowIfCancellationRequested();
					var fact = plan.File.Imports[factIndex];
					var edge = context.ResolveImport(plan.File, fact);
					imports[factIndex] = Resolve(fact, edge);
					edges[factIndex] = edge;
				}
				for (var factIndex = 0; factIndex < references.Length; factIndex++)
				{
					cancellationToken.ThrowIfCancellationRequested();
					var fact = plan.File.References[factIndex];
					var edge = context.ResolveType(plan.File, fact);
					references[factIndex] = Resolve(fact, edge);
					edges[imports.Length + factIndex] = edge;
				}
				completed[index] = new ResolvedFileWork(plan.File, imports, references, edges);
			});
			foreach (var fileWork in completed)
			{
				cancellationToken.ThrowIfCancellationRequested();
				resolved.AddRange(fileWork.Edges);
			}
			var resolvedFiles = new FileFacts[files.Count];
			var supportedIndex = 0;
			for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
			{
				var file = files[fileIndex];
				if (file.Status != DependencyFileStatus.Supported)
				{
					resolvedFiles[fileIndex] = file;
					continue;
				}
				var work = completed[supportedIndex++];
				resolvedFiles[fileIndex] = work.Imports is null || work.References is null
					? file
					: file with
					{
						Imports = work.Imports,
						References = work.References
					};
			}
			var fileByPath = resolvedFiles.ToDictionary(static file => file.Path, StringComparer.Ordinal);
			DependencyEngineDiagnostics.RecordDictionaryBuild();
			return new ResolvedIndex(
				Aggregate(resolved),
				resolvedFiles,
				fileByPath,
				new Dictionary<string, IReadOnlyList<DependencyEdge>>(StringComparer.Ordinal),
				new Dictionary<string, IReadOnlyList<DependencyEdge>>(StringComparer.Ordinal))
			{
				ResolverContextEstimatedBytes = context.EstimatedRetainedBytes,
				PhysicalFileProbes = context.CapturePhysicalFileProbes(),
				CanCachePhysicalFileProbes = context.CanCachePhysicalFileProbes
			};
		}

		private static ResolutionWorkPlan[] CreateWorkPlans(
			IReadOnlyList<FileFacts> files,
			ResolverContext context,
			DependencyFactsLimits limits,
			CancellationToken cancellationToken)
		{
			var plans = new ResolutionWorkPlan[files.Count];
			long acceptedWork = 0;
			for (var index = 0; index < files.Count; index++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var file = files[index];
				var requestedEdges = (long)file.Imports.Count + file.References.Count;
				var requestedWork = context.EstimateResolutionWork(file, limits.MaximumWorkPerIndex);
				string? limitReason = null;
				if (requestedEdges > limits.MaximumEdgesPerFile)
					limitReason = "edge limit exceeded";
				else if (requestedWork > limits.MaximumWorkPerIndex - acceptedWork)
					limitReason = "index work limit exceeded";
				else
					acceptedWork += requestedWork;
				plans[index] = new ResolutionWorkPlan(file, limitReason);
			}
			return plans;
		}

		private sealed record ResolutionWorkPlan(FileFacts File, string? LimitReason);

		private sealed record ResolvedFileWork(
			FileFacts File,
			IReadOnlyList<ImportFact>? Imports,
			IReadOnlyList<ReferenceFact>? References,
			DependencyEdge[] Edges);

		private static ImportFact Resolve(ImportFact fact, DependencyEdge edge) => fact with
		{
			Status = edge.Status,
			Reason = string.Join("; ", edge.Reasons),
			Candidates = edge.Candidates,
			Target = edge.Target
		};

		private static ReferenceFact Resolve(ReferenceFact fact, DependencyEdge edge) => fact with
		{
			Status = edge.Status,
			Reason = string.Join("; ", edge.Reasons),
			Candidates = edge.Candidates,
			Target = edge.Target
		};

		private static DependencyEdge LimitEdge(FileFacts file, string reason) => new(
			file.Path, null, EvidenceLayer.TypeReference, ResolutionStatus.Unresolved,
			"<limit>", [reason], [new SourceSite(file.Path, 1, reason)], [], false);

		private static IReadOnlyList<DependencyEdge> Aggregate(IEnumerable<DependencyEdge> raw)
		{
			var groups = new Dictionary<EdgeAggregationKey, EdgeAccumulator>();
			foreach (var edge in raw)
			{
				var key = new EdgeAggregationKey(
					edge.Source, edge.Target, edge.Layer, edge.Status, edge.Reference, edge.CrossScope);
				if (!groups.TryGetValue(key, out var accumulator))
					groups.Add(key, accumulator = new EdgeAccumulator());
				accumulator.Add(edge);
			}
			return groups.Select(static group => group.Value.Create(group.Key))
				.OrderBy(static edge => edge.Source, StringComparer.Ordinal)
				.ThenBy(static edge => edge.Target, StringComparer.Ordinal)
				.ThenBy(static edge => edge.Reference, StringComparer.Ordinal)
				.ToArray();
		}

		private readonly record struct EdgeAggregationKey(
			string Source,
			string? Target,
			EvidenceLayer Layer,
			ResolutionStatus Status,
			string Reference,
			bool CrossScope);

		private sealed class EdgeAccumulator
		{
			private DependencyEdge? _single;
			private HashSet<string>? _reasons;
			private HashSet<SourceSite>? _evidenceSeen;
			private List<SourceSite>? _evidence;
			private HashSet<string>? _candidates;
			private HashSet<string>? _declarationFiles;

			public void Add(DependencyEdge edge)
			{
				if (_single is null)
				{
					_single = edge;
					return;
				}
				if (_reasons is null)
					InitializeCollections(_single);
				_reasons!.UnionWith(edge.Reasons);
				foreach (var site in edge.Evidence)
					if (_evidenceSeen!.Add(site))
						_evidence!.Add(site);
				_candidates!.UnionWith(edge.Candidates);
				_declarationFiles!.UnionWith(edge.DeclarationFiles);
			}

			public DependencyEdge Create(EdgeAggregationKey key)
			{
				if (_reasons is null)
					return _single!;
				return new DependencyEdge(
					key.Source,
					key.Target,
					key.Layer,
					key.Status,
					key.Reference,
					_reasons.Order(StringComparer.Ordinal).ToArray(),
					_evidence!.OrderBy(static site => site.Line).ToArray(),
					_candidates!.Order(StringComparer.Ordinal).ToArray(),
					key.CrossScope)
				{
					DeclarationFiles = _declarationFiles!.Order(StringComparer.Ordinal).ToArray()
				};
			}

			private void InitializeCollections(DependencyEdge edge)
			{
				_reasons = new HashSet<string>(edge.Reasons, StringComparer.Ordinal);
				_evidenceSeen = new HashSet<SourceSite>(edge.Evidence);
				_evidence = new List<SourceSite>(edge.Evidence);
				_candidates = new HashSet<string>(edge.Candidates, StringComparer.Ordinal);
				_declarationFiles = new HashSet<string>(edge.DeclarationFiles, StringComparer.Ordinal);
			}
		}
	}

	private sealed partial class ResolverContext
	{
		private const int MaximumBashSuffixCandidates = 32;
		private const string BashWorkingDirectoryReason = "the Bash execution working directory is unknown";
		private const string BashSearchPathReason =
			"the Bash execution working directory is unknown; PATH and sourcepath settings are also unknown";
		private const string TypeScriptCustomConditionsReason = "tsconfig customConditions are not supported";
		private const string StaticUsingPrefix = "static::";
		private const string RubyExternalConstantReason = "Ruby constant is provided outside the project";
		private static readonly IReadOnlySet<string> RubyRuntimeConstants = new HashSet<string>(StringComparer.Ordinal)
		{
			"Array", "BasicObject", "Class", "Dir", "Encoding", "Enumerator", "Exception", "FalseClass",
			"File", "Float", "Hash", "Integer", "IO", "Kernel", "MatchData", "Method", "Module", "NilClass",
			"Numeric", "Object", "Proc", "Range", "Regexp", "String", "Struct", "Symbol", "Thread", "Time",
			"TrueClass"
		};
		private static readonly ConditionalWeakTable<IReadOnlySet<string>, IReadOnlySet<string>> DotNetSimpleNames = new();
		private readonly string _root;
		private readonly IReadOnlyDictionary<string, FileFacts> _files;
		private readonly IReadOnlyDictionary<string, FileFacts> _manifestFiles;
		private readonly IReadOnlyDictionary<string, string[]> _manifestPathsByFileName;
		private readonly IReadOnlyList<DeclarationFact> _declarations;
		private readonly IReadOnlyDictionary<SymbolLookupKey, DeclarationFact[]> _symbolsBySimpleName;
		private readonly IReadOnlyDictionary<QualifiedSymbolLookupKey, DeclarationFact[]> _symbolsByQualifiedName;
		private readonly DependencyResolverConfiguration _configuration;
		private readonly IReadOnlyDictionary<string, DependencyScopeDescriptor> _scopesById;
		private readonly IReadOnlyDictionary<string, string[]> _visibleScopesById;
		private readonly IReadOnlyDictionary<string, string[]> _globalNamespaces;
		private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _globalAliases;
		private readonly IReadOnlyDictionary<string, CSharpNamespaceRegions> _csharpNamespaceRegionsByFile;
		private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, CSharpUsingDirective[]>> _aliasesByFileAndName;
		private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, TypeParameterScope[]>> _typeParametersByFileAndName;
		private readonly IReadOnlySet<string> _dotNetExternalSimpleNames;
		private readonly IReadOnlyDictionary<string, string[]> _pythonRootPrefixesByScope;
		private readonly IReadOnlyDictionary<string, string> _pythonModuleByFile;
		private readonly IReadOnlySet<string> _manifestDirectoryPrefixes;
		private readonly IReadOnlyDictionary<string, TypeScriptPathMapping[]> _typeScriptMappingsByScope;
		private readonly bool _diagnosticsEnabled;
		private readonly ConcurrentDictionary<string, int> _physicalFileProbes = new(PathComparer);
		private int _physicalFileProbeCount;
		private int _physicalFileProbeOverflow;
		private int _physicalFileProbeConflict;
		public long EstimatedRetainedBytes { get; }
		public bool CanCachePhysicalFileProbes =>
			Volatile.Read(ref _physicalFileProbeOverflow) == 0 &&
			Volatile.Read(ref _physicalFileProbeConflict) == 0;

		public IReadOnlyList<PhysicalFileProbe> CapturePhysicalFileProbes() =>
			CanCachePhysicalFileProbes
				? _physicalFileProbes
					.OrderBy(static pair => pair.Key, PathComparer)
					.Select(static pair => new PhysicalFileProbe(pair.Key, pair.Value == 2))
					.ToArray()
				: [];

		public ResolverContext(
			string root,
			IReadOnlyList<FileFacts> files,
			IReadOnlyList<DeclarationFact> declarations,
			DependencyResolverConfiguration configuration)
		{
			_root = root;
			_diagnosticsEnabled = DependencyEngineDiagnostics.IsEnabled;
			_files = files.ToDictionary(static file => file.Path, StringComparer.Ordinal);
			_manifestFiles = files.ToDictionary(static file => file.Path, PathComparer);
			_manifestPathsByFileName = files
				.GroupBy(static file => PortableFileName(file.Path), StringComparer.Ordinal)
				.ToDictionary(
					static group => group.Key,
					static group => group.Select(static file => file.Path).Order(StringComparer.Ordinal).ToArray(),
					StringComparer.Ordinal);
			_declarations = declarations;
			DependencyEngineDiagnostics.RecordDictionaryBuild();
			_symbolsBySimpleName = declarations
				.GroupBy(static declaration => new SymbolLookupKey(
					declaration.Identity.ScopeId,
					declaration.Identity.LanguageId,
					SimpleName(declaration.Identity.QualifiedName)))
				.ToDictionary(static group => group.Key, static group => group.ToArray());
			_symbolsByQualifiedName = declarations
				.GroupBy(static declaration => new QualifiedSymbolLookupKey(
					declaration.Identity.ScopeId,
					declaration.Identity.LanguageId,
					QualifiedLookupName(declaration.Identity.QualifiedName),
					declaration.Identity.GenericArity))
				.ToDictionary(static group => group.Key, static group => group.ToArray());
			_configuration = configuration;
			_scopesById = configuration.Scopes.ToDictionary(static scope => scope.ScopeId, StringComparer.Ordinal);
			_visibleScopesById = BuildVisibleScopes(_scopesById);
			_globalNamespaces = files.GroupBy(static file => file.ScopeId)
				.ToDictionary(static group => group.Key,
					static group => group.SelectMany(static file => file.GlobalContextNamespaces)
						.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
			_globalAliases = files.GroupBy(static file => file.ScopeId)
				.ToDictionary(static group => group.Key,
					static group => (IReadOnlyDictionary<string, string>)group.SelectMany(static file => file.GlobalAliases)
						.GroupBy(static pair => pair.Key, StringComparer.Ordinal)
						.ToDictionary(static aliases => aliases.Key, static aliases => aliases.OrderBy(static pair => pair.Value, StringComparer.Ordinal).First().Value, StringComparer.Ordinal),
					StringComparer.Ordinal);
			_csharpNamespaceRegionsByFile = files.ToDictionary(
				static file => file.Path,
				file => CSharpNamespaceRegions.Create(
					file.ContextNamespaces.Concat(_globalNamespaces.GetValueOrDefault(file.ScopeId) ?? []),
					file.CSharpUsingDirectives),
				StringComparer.Ordinal);
			_aliasesByFileAndName = files.ToDictionary(
				static file => file.Path,
				static file => (IReadOnlyDictionary<string, CSharpUsingDirective[]>)file.CSharpUsingDirectives
					.Where(static directive => directive.Alias is not null)
					.GroupBy(static directive => directive.Alias!, StringComparer.Ordinal)
					.ToDictionary(
						static group => group.Key,
						static group => group
							.OrderBy(static directive => directive.ScopeEndIndex - directive.ScopeStartIndex)
							.ThenBy(static directive => directive.Target, StringComparer.Ordinal)
							.ToArray(),
						StringComparer.Ordinal),
				StringComparer.Ordinal);
			_typeParametersByFileAndName = files.ToDictionary(
				static file => file.Path,
				static file => (IReadOnlyDictionary<string, TypeParameterScope[]>)file.TypeParameterScopes
					.GroupBy(static parameter => parameter.Name, StringComparer.Ordinal)
					.ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal),
				StringComparer.Ordinal);
			_dotNetExternalSimpleNames = DotNetSimpleNames.GetValue(
				configuration.DotNetExternalSymbols,
				static symbols => symbols.Select(SimpleName).ToHashSet(StringComparer.Ordinal));
			_pythonRootPrefixesByScope = configuration.Scopes
				.Where(static scope => scope.LanguageId == LanguageId.Python)
				.ToDictionary(
					static scope => scope.ScopeId,
					scope => scope.PythonRoots.Where(candidate => IsWithin(_root, candidate))
						.Select(candidate => PortableRelative(_root, candidate) is "." ? string.Empty : PortableRelative(_root, candidate).Trim('/'))
						.Distinct(StringComparer.Ordinal).ToArray(),
					StringComparer.Ordinal);
			_pythonModuleByFile = files.Where(static file => file.LanguageId == LanguageId.Python)
				.ToDictionary(static file => file.Path, ComputePythonModule, StringComparer.Ordinal);
			_manifestDirectoryPrefixes = BuildDirectoryPrefixes(files);
			_typeScriptMappingsByScope = configuration.Scopes
				.Where(static scope => IsTypeScript(scope.LanguageId))
				.ToDictionary(
					static scope => scope.ScopeId,
					static scope => scope.TypeScriptPaths
						.Select(static pair => new TypeScriptPathMapping(pair.Key, pair.Value, pair.Key.IndexOf('*')))
						.OrderBy(static mapping => mapping.Star >= 0)
						.ThenByDescending(static mapping => mapping.Star)
						.ThenBy(static mapping => mapping.Pattern, StringComparer.Ordinal)
						.ToArray(),
					StringComparer.Ordinal);
			EstimatedRetainedBytes = EstimateRetainedBytes();
		}

		private long EstimateRetainedBytes() =>
			256L +
			_files.Count * 64L +
			_manifestFiles.Count * 64L +
			_manifestPathsByFileName.Sum(static pair => 64L + pair.Value.Length * 8L) +
			_declarations.Count * 8L +
			_symbolsBySimpleName.Sum(static pair => 64L + pair.Value.Length * 8L) +
			_symbolsByQualifiedName.Sum(static pair => 64L + pair.Value.Length * 8L) +
			_scopesById.Count * 64L +
			_visibleScopesById.Sum(static pair => 64L + pair.Value.Length * 8L) +
			_globalNamespaces.Sum(static pair => 64L + pair.Value.Length * 8L) +
			_globalAliases.Sum(static pair => 64L + pair.Value.Count * 64L) +
			_csharpNamespaceRegionsByFile.Count * 64L +
			_aliasesByFileAndName.Sum(static pair => 64L + pair.Value.Sum(static aliases => 64L + aliases.Value.Length * 8L)) +
			_typeParametersByFileAndName.Sum(static pair => 64L + pair.Value.Sum(static parameters => 64L + parameters.Value.Length * 8L)) +
			_pythonRootPrefixesByScope.Sum(static pair => 64L + pair.Value.Length * 8L) +
			_pythonModuleByFile.Count * 64L +
			_manifestDirectoryPrefixes.Count * 32L +
			_typeScriptMappingsByScope.Sum(static pair => 64L + pair.Value.Length * 24L);

		public DependencyEdge ResolveImport(FileFacts source, ImportFact import)
		{
			if (!string.Equals(import.Reason, "not resolved yet", StringComparison.Ordinal))
				return Edge(source, import, ResolutionStatus.Unresolved, null, import.Reason, []);
			return source.LanguageId switch
			{
				LanguageId.TypeScript or LanguageId.JavaScript or LanguageId.Tsx => ResolveTypeScriptImport(source, import),
				LanguageId.Python => ResolvePythonImport(source, import),
				LanguageId.Java or LanguageId.Kotlin or LanguageId.Php => ResolveJavaImport(source, import),
				LanguageId.C or LanguageId.Cpp => ResolveCImport(source, import),
				LanguageId.Bash => ResolveBashImport(source, import),
				LanguageId.Scala => ResolveScalaImport(source, import),
				LanguageId.Rust => ResolveRustImport(source, import),
				LanguageId.Ruby => ResolveRubyImport(source, import),
				_ => Edge(source, import, ResolutionStatus.Unresolved, null,
					"explicit imports are context, not dependency edges, for this language", [])
			};
		}

		private DependencyEdge ResolveBashImport(FileFacts source, ImportFact import)
		{
			if (Path.IsPathRooted(import.Specifier))
				return Edge(source, import, ResolutionStatus.Unresolved, null, "absolute Bash paths are not project-relative evidence", []);
			var reason = import.ImportedName is "source" or "." && !import.Specifier.Contains('/')
				? BashSearchPathReason
				: BashWorkingDirectoryReason;
			var probe = ProbeBashSuffixCandidates(import.Specifier, MaximumBashSuffixCandidates);
			if (probe.Total == 0)
				return Edge(source, import, ResolutionStatus.Unresolved, null, reason, []);
			if (probe.Total > probe.Candidates.Count)
				reason += $"; showing {probe.Candidates.Count.ToString(CultureInfo.InvariantCulture)} of " +
						  $"{probe.Total.ToString(CultureInfo.InvariantCulture)} suffix-matching manifest candidates";
			return Edge(source, import, ResolutionStatus.Ambiguous, null, reason, probe.Candidates);
		}

		private BashCandidateProbe ProbeBashSuffixCandidates(string specifier, int maximumCandidates)
		{
			var suffix = NormalizeBashCandidateSuffix(specifier);
			if (suffix is null || !_manifestPathsByFileName.TryGetValue(PortableFileName(suffix), out var sameName))
				return new BashCandidateProbe([], 0);
			var candidates = maximumCandidates > 0 ? new List<string>(Math.Min(maximumCandidates, sameName.Length)) : null;
			var total = 0;
			foreach (var path in sameName)
			{
				if (!string.Equals(path, suffix, StringComparison.Ordinal) &&
					!path.EndsWith('/' + suffix, StringComparison.Ordinal))
					continue;
				total++;
				if (candidates is not null && candidates.Count < maximumCandidates)
					candidates.Add(path);
			}
			return new BashCandidateProbe(candidates ?? [], total);
		}

		private static string? NormalizeBashCandidateSuffix(string specifier)
		{
			var segments = new List<string>();
			foreach (var segment in specifier.Split('/'))
			{
				if (segment.Length == 0 || segment == ".") continue;
				if (segment == "..")
				{
					if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
					continue;
				}
				segments.Add(segment);
			}
			return segments.Count == 0 ? null : string.Join('/', segments);
		}

		private static string PortableFileName(string path)
		{
			var separator = path.LastIndexOf('/');
			return separator < 0 ? path : path[(separator + 1)..];
		}

		private DependencyEdge ResolveCImport(FileFacts source, ImportFact import)
		{
			var sourceDirectory = Path.GetDirectoryName(Path.Combine(_root, source.Path))!;
			var roots = new List<string>();
			if (string.Equals(import.ImportedName, "$quoted", StringComparison.Ordinal))
				roots.Add(sourceDirectory);
			if (FindScope(source.ScopeId) is { } scope)
				roots.AddRange(scope.CIncludeDirectories);
			roots.Add(_root);

			var paths = roots
				.Select(root => Path.GetFullPath(Path.Combine(root, import.Specifier)))
				.Where(path => IsWithin(_root, path))
				.Select(path => PortableRelative(_root, path))
				.Where(_files.ContainsKey);
			return FinishImport(source, import, paths, "one repository C header");
		}

		private DependencyEdge ResolveJavaImport(FileFacts source, ImportFact import)
		{
			if (source.LanguageId == LanguageId.Php &&
				FindScope(source.ScopeId) is { } scope && ConfigurationFailure(scope) is { } configurationFailure)
				return Edge(source, import, ResolutionStatus.Unresolved, null, configurationFailure, []);
			if (import.IsWildcard)
				return Edge(source, import, ResolutionStatus.Unresolved, null,
					"wildcard import is resolution context, not a dependency target", []);
			var qualifiedName = import.Specifier;
			while (qualifiedName.Length > 0)
			{
				var declarations = source.LanguageId == LanguageId.Kotlin
					? LookupQualifiedAcrossRepository(source, qualifiedName, 0)
					: LookupQualified(source, qualifiedName, 0);
				var files = declarations.SelectMany(static declaration => declaration.DeclarationSites)
					.Select(static site => site.File).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
				if (files.Length > 0)
					return declarations.Length == 1
						? Edge(source, import, ResolutionStatus.Resolved, files[0], "one imported declaration", files) with
						{
							DeclarationFiles = files
						}
						: Edge(source, import, ResolutionStatus.Ambiguous, null, "multiple imported declarations", files);
				var separator = qualifiedName.LastIndexOf('.');
				if (separator < 0) break;
				qualifiedName = qualifiedName[..separator];
			}
			return Edge(source, import, ResolutionStatus.Unresolved, null,
				"no imported declaration in the manifest", []);
		}

		private DependencyEdge ResolveRustImport(FileFacts source, ImportFact import)
		{
			var scope = FindScope(source.ScopeId);
			if (scope is not null && ConfigurationFailure(scope) is { } configurationFailure)
				return Edge(source, import, ResolutionStatus.Unresolved, null, configurationFailure, []);
			if (import.ImportedName == "$module")
			{
				var sourcePath = Path.Combine(_root, source.Path);
				var sourceDirectory = Path.GetDirectoryName(sourcePath)!;
				var sourceStem = Path.GetFileNameWithoutExtension(sourcePath);
				var directory = sourceStem is "lib" or "main" or "mod" ||
								IsConventionalRustTargetRoot(scope, sourcePath)
					? sourceDirectory
					: Path.Combine(sourceDirectory, sourceStem);
				if (!string.IsNullOrEmpty(import.ContainingDeclaration))
				{
					foreach (var module in import.ContainingDeclaration.Split("::", StringSplitOptions.RemoveEmptyEntries))
						directory = Path.Combine(directory, module);
				}
				var stem = import.Specifier["./".Length..];
				var candidates = new[]
				{
					Path.Combine(directory, stem + ".rs"),
					Path.Combine(directory, stem, "mod.rs")
				}.Where(path => IsWithin(_root, path))
					.Select(path => PortableRelative(_root, path))
					.Where(_files.ContainsKey);
				return FinishImport(source, import, candidates, "one declared Rust module");
			}
			if (import.IsWildcard)
				return Edge(source, import, ResolutionStatus.Unresolved, null,
					"wildcard import is resolution context, not a dependency target", []);
			if (import.IsCrateQualified)
			{
				var localDeclarations = LookupRustCrateQualifiedInScope(source, import.Specifier);
				var localFiles = localDeclarations.SelectMany(static declaration => declaration.DeclarationSites)
					.Select(static site => site.File)
					.Distinct(StringComparer.Ordinal)
					.Order(StringComparer.Ordinal)
					.ToArray();
				return localDeclarations.Length switch
				{
					0 => Edge(source, import, ResolutionStatus.Unresolved, null,
						"no imported declaration in the owning Rust crate", []),
					1 => Edge(source, import, ResolutionStatus.Resolved, localFiles[0],
						"one imported declaration in the owning Rust crate", localFiles) with
					{
						DeclarationFiles = localFiles
					},
					_ => Edge(source, import, ResolutionStatus.Ambiguous, null,
						"multiple imported declarations in the owning Rust crate", localFiles)
				};
			}

			var names = new List<string> { import.Specifier };
			var firstSeparator = import.Specifier.IndexOf("::", StringComparison.Ordinal);
			var first = firstSeparator < 0 ? import.Specifier : import.Specifier[..firstSeparator];
			foreach (var scopeId in VisibleScopeIds(source.ScopeId))
			{
				var visibleScope = FindScope(scopeId);
				if (visibleScope?.PackageName == first && firstSeparator >= 0)
					names.Add(import.Specifier[(firstSeparator + 2)..]);
			}
			var files = names.Distinct(StringComparer.Ordinal)
				.SelectMany(name => LookupQualified(source, name, 0))
				.SelectMany(static declaration => declaration.DeclarationSites)
				.Select(static site => site.File).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
			return files.Length switch
			{
				0 => Edge(source, import, ResolutionStatus.Unresolved, null,
					"no imported declaration in the manifest", []),
				1 => Edge(source, import, ResolutionStatus.Resolved, files[0], "one imported declaration", files),
				_ => Edge(source, import, ResolutionStatus.Ambiguous, null, "multiple imported declarations", files)
			};
		}

		private DeclarationFact[] LookupRustCrateQualifiedInScope(FileFacts source, string name)
		{
			var exact = LookupQualifiedInScope(source, name, 0);
			if (exact.Length > 0)
				return exact;

			var normalized = QualifiedLookupName(name);
			var suffix = "::" + normalized;
			var scope = FindScope(source.ScopeId);
			var sourceTarget = FindUniqueRustTarget(scope, source.Path);
			if (sourceTarget is null)
				return [];
			return _declarations.Where(declaration =>
				declaration.Identity.ScopeId == source.ScopeId &&
				declaration.Identity.LanguageId == LanguageId.Rust &&
				declaration.Identity.GenericArity == 0 &&
				QualifiedLookupName(declaration.Identity.QualifiedName)
					.EndsWith(suffix, StringComparison.Ordinal) &&
				declaration.DeclarationSites.All(site =>
					PathComparer.Equals(FindUniqueRustTarget(scope, site.File), sourceTarget)) &&
				IsVisible(source, declaration)).ToArray();
		}

		private string? FindUniqueRustTarget(DependencyScopeDescriptor? scope, string relativePath)
		{
			if (scope is null || scope.RustTargetRoots.Count == 0)
				return null;
			var fullPath = Path.GetFullPath(Path.Combine(_root, relativePath));
			var matches = scope.RustTargetRoots
				.Where(target => PathComparer.Equals(target, fullPath) ||
					IsWithin(Path.GetDirectoryName(target)!, fullPath))
				.Take(2)
				.ToArray();
			return matches.Length == 1 ? matches[0] : null;
		}

		private static bool IsConventionalRustTargetRoot(DependencyScopeDescriptor? scope, string sourcePath)
		{
			if (scope is null)
				return false;
			var relative = Path.GetRelativePath(scope.Root, sourcePath).Replace('\\', '/');
			var separator = relative.IndexOf('/');
			return scope.RustTargetRoots.Any(target => PathComparer.Equals(target, sourcePath)) ||
				   separator > 0 && relative.IndexOf('/', separator + 1) < 0 &&
				   relative[..separator] is "tests" or "examples" or "benches";
		}

		private DependencyEdge ResolveRubyImport(FileFacts source, ImportFact import)
		{
			return FinishImport(
				source,
				import,
				RubyImportCandidatePaths(source, import).Where(_files.ContainsKey),
				"one repository Ruby source");
		}

		private IEnumerable<string> RubyImportCandidatePaths(FileFacts source, ImportFact import)
		{
			var sourceDirectory = Path.GetDirectoryName(Path.Combine(_root, source.Path))!;
			var roots = new List<string>();
			if (import.ImportedName == "$relative")
				roots.Add(sourceDirectory);
			else
			{
				roots.Add(_root);
				roots.Add(Path.Combine(_root, "lib"));
				foreach (var scopeId in VisibleScopeIds(source.ScopeId))
				{
					if (FindScope(scopeId) is not { } scope) continue;
					roots.Add(scope.Root);
					roots.Add(Path.Combine(scope.Root, "lib"));
				}
			}
			var paths = new List<string>();
			foreach (var root in roots)
			{
				var requested = import.Specifier.EndsWith(".rb", StringComparison.OrdinalIgnoreCase)
					? import.Specifier
					: import.Specifier + ".rb";
				var fullPath = Path.GetFullPath(Path.Combine(root, requested.Replace('/', Path.DirectorySeparatorChar)));
				if (IsWithin(_root, fullPath)) paths.Add(PortableRelative(_root, fullPath));
			}
			return paths;
		}

		public long EstimateResolutionWork(FileFacts source, int maximumWork)
		{
			long work = source.Imports.Count;
			if (source.LanguageId == LanguageId.Bash)
			{
				foreach (var import in source.Imports)
				{
					if (!string.Equals(import.Reason, "not resolved yet", StringComparison.Ordinal) ||
						Path.IsPathRooted(import.Specifier))
						continue;
					var candidates = ProbeBashSuffixCandidates(import.Specifier, maximumCandidates: 0).Total;
					work += candidates;
					if (_diagnosticsEnabled) DependencyEngineDiagnostics.RecordResolverCandidateProbes(candidates);
					if (work > maximumWork) return work;
				}
			}
			foreach (var reference in source.References)
			{
				var name = SimpleName(reference.Name);
				long candidates = 0;
				foreach (var scope in VisibleScopeIds(source.ScopeId))
					foreach (var language in CompatibleLanguages(source.LanguageId))
					{
						if (_symbolsBySimpleName.TryGetValue(new SymbolLookupKey(scope, language, name), out var matches))
						{
							candidates += matches.Length;
							if (_diagnosticsEnabled)
								DependencyEngineDiagnostics.RecordResolverCandidateProbes(matches.Length);
						}
						if (candidates > maximumWork)
							return candidates;
					}
				work += Math.Max(1, candidates);
				if (work > maximumWork)
					return work;
			}
			return work;
		}

		private DependencyEdge ResolveTypeScriptImport(FileFacts source, ImportFact import)
		{
			if (!string.Equals(import.Reason, "not resolved yet", StringComparison.Ordinal))
				return Edge(source, import, ResolutionStatus.Unresolved, null,
					import.Reason, []);
			var scope = FindScope(source.ScopeId);
			if (scope is null || !scope.HasConfiguration)
				return ResolveTypeScriptImportWithoutConfiguration(source, import);
			if (ConfigurationFailure(scope) is { } configurationFailure)
				return Edge(source, import, ResolutionStatus.Unresolved, null, configurationFailure, []);
			if (FindNearestPackageMap(source) is { ConfigurationState: not DependencyConfigurationState.Valid } packageMap)
				return Edge(source, import, ResolutionStatus.Unresolved, null,
					packageMap.ConfigurationDiagnostic ?? "package.json configuration is unavailable", []);
			if (IsRequire(import) && !SupportsCommonJs(source, scope))
				return Edge(source, import, ResolutionStatus.Unresolved, null,
					"require call is outside a supported CommonJS context", []);
			if (scope.LegacyTypeScriptConfiguration)
				return Edge(source, import, ResolutionStatus.Unresolved, null,
					"legacy tsconfig node10/baseUrl semantics are not emulated", []);
			if (DependencyPlatformCatalog.IsNodeExternal(_configuration, import.Specifier))
				return Edge(source, import, ResolutionStatus.External, null, "known Node built-in module", []);
			IEnumerable<string> candidates;
			if (import.Specifier.StartsWith(".", StringComparison.Ordinal))
			{
				var physicalSpecifier = PhysicalModuleSpecifier(import.Specifier);
				if (Path.GetExtension(physicalSpecifier).Length == 0 &&
					RequiresExplicitRelativeExtension(source, scope, import))
				{
					return Edge(source, import, ResolutionStatus.Unresolved, null,
						"extension required for a relative ESM import under node16/nodenext", []);
				}
				var directory = Path.GetDirectoryName(Path.Combine(_root, source.Path))!;
				var rootDirectoryCandidates = ProbeTypeScriptRootDirectories(
					directory,
					physicalSpecifier,
					scope,
					source).ToArray();
				candidates = rootDirectoryCandidates.Length > 0
					? rootDirectoryCandidates
					: ProbeTypeScript(Path.GetFullPath(Path.Combine(directory, physicalSpecifier)), scope, source);
			}
			else if (import.Specifier.StartsWith("#", StringComparison.Ordinal))
			{
				var packageTarget = ResolvePackageMap(source, import, import.Specifier, exports: false);
				if (packageTarget.FailureReason is { } reason)
					return Edge(source, import, ResolutionStatus.Unresolved, null, reason, []);
				if (packageTarget.IsExternal)
					return Edge(source, import, ResolutionStatus.External, null,
						"declared Node package outside the manifest", []);
				candidates = packageTarget.Candidates;
			}
			else if ((scope.PackageName ?? FindNearestPackageMap(source)?.PackageName) is { } package &&
					 (import.Specifier == package || import.Specifier.StartsWith(package + '/', StringComparison.Ordinal)))
			{
				var selfPath = import.Specifier[package.Length..].TrimStart('/');
				var packageTarget = ResolvePackageMap(source, import, selfPath, exports: true);
				if (packageTarget.FailureReason is { } reason)
					return Edge(source, import, ResolutionStatus.Unresolved, null, reason, []);
				candidates = packageTarget.Candidates;
			}
			else
			{
				var mapped = ResolvePaths(scope, source, import.Specifier).ToArray();
				if (mapped.Length > 0)
					return FinishImport(source, import, mapped,
						"one module target under configured module resolution");
				var packageName = BarePackageName(import.Specifier);
				return FindNearestPackageMap(source)?.ExternalPackages.Contains(packageName) == true
					? Edge(source, import, ResolutionStatus.External, null,
						"declared Node package outside the manifest", [])
					: Edge(source, import, ResolutionStatus.Unresolved, null,
						"bare package has no target or external-package evidence", []);
			}
			return FinishImport(source, import, candidates,
				"one module target under configured module resolution");
		}

		private bool SupportsCommonJs(FileFacts source, DependencyScopeDescriptor? scope)
		{
			var extension = Path.GetExtension(source.Path).ToLowerInvariant();
			if (extension is ".cjs" or ".cts") return true;
			if (extension is ".mjs" or ".mts") return false;
			if (extension is not (".ts" or ".tsx" or ".js" or ".jsx"))
				return scope?.LegacyTypeScriptConfiguration == true;
			var moduleType = FindNearestPackageMap(source)?.ModuleType;
			if (string.Equals(moduleType, "commonjs", StringComparison.OrdinalIgnoreCase)) return true;
			if (string.Equals(moduleType, "module", StringComparison.OrdinalIgnoreCase)) return false;
			return true;
		}

		private static bool IsRequire(ImportFact import) => import.ImportKind == ModuleImportKind.Require;

		private bool RequiresExplicitRelativeExtension(
			FileFacts source,
			DependencyScopeDescriptor scope,
			ImportFact import)
		{
			var mode = scope.ModuleResolution ?? "bundler";
			return (mode.Equals("node16", StringComparison.OrdinalIgnoreCase) ||
					mode.Equals("nodenext", StringComparison.OrdinalIgnoreCase)) &&
				   (import.ImportKind == ModuleImportKind.DynamicImport || !SupportsCommonJs(source, scope));
		}

		private IEnumerable<string> ResolvePaths(
			DependencyScopeDescriptor? scope,
			FileFacts source,
			string specifier)
		{
			if (scope is null)
				return [];
			var mappings = (_typeScriptMappingsByScope.GetValueOrDefault(scope.ScopeId) ?? [])
				.Where(item => Matches(item.Pattern, item.Star, specifier))
				.ToArray();
			if (mappings.Length == 0)
				return [];
			var mapping = mappings[0];
			var wildcard = mapping.Star < 0 ? string.Empty :
					specifier[mapping.Star..(specifier.Length - (mapping.Pattern.Length - mapping.Star - 1))];
			foreach (var target in mapping.Targets)
			{
				var candidate = Path.GetFullPath(Path.Combine(
					scope.Root,
					target.Replace("*", wildcard, StringComparison.Ordinal)));
				foreach (var probe in EnumerateTypeScriptProbes(candidate, scope, source, allowDirectoryIndex: true))
				{
					var relative = PortableRelative(_root, probe);
					if (TryGetManifestPath(relative, out var manifestPath))
						return [manifestPath];
					if (ProbeUnselectedFile(probe))
						return [];
				}
			}
			return [];
		}

		private bool ProbeUnselectedFile(string path)
		{
			var exists = File.Exists(path);
			if (Volatile.Read(ref _physicalFileProbeOverflow) != 0)
				return exists;
			var state = exists ? 2 : 1;
			if (_physicalFileProbes.TryAdd(path, state))
			{
				if (Interlocked.Increment(ref _physicalFileProbeCount) > MaximumCachedPhysicalFileProbes)
					Volatile.Write(ref _physicalFileProbeOverflow, 1);
			}
			else if (_physicalFileProbes.TryGetValue(path, out var previous) && previous != state)
			{
				Volatile.Write(ref _physicalFileProbeConflict, 1);
			}
			return exists;
		}

		private static bool Matches(string pattern, int star, string value) => star < 0
			? pattern == value
			: star + (pattern.Length - star - 1) <= value.Length &&
			  value.StartsWith(pattern[..star], StringComparison.Ordinal) &&
			  value.EndsWith(pattern[(star + 1)..], StringComparison.Ordinal);

		private static string BarePackageName(string specifier)
		{
			var parts = specifier.Split('/', StringSplitOptions.RemoveEmptyEntries);
			return parts.Length > 1 && parts[0].StartsWith('@')
				? parts[0] + "/" + parts[1]
				: parts.FirstOrDefault() ?? specifier;
		}

		private PackageMapProbe ResolvePackageMap(
			FileFacts source,
			ImportFact import,
			string specifier,
			bool exports)
		{
			var directory = Path.GetDirectoryName(Path.Combine(_root, source.Path))!;
			while (IsWithin(_root, directory))
			{
				var relative = PortableRelative(_root, directory);
				if (_configuration.PackageMaps.TryGetValue(relative, out var map))
				{
					var values = exports ? map.Exports : map.Imports;
					var key = exports ? (specifier.Length == 0 ? "." : "./" + specifier) : specifier;
					if (!TryMap(values, key, out var target, out var wildcard))
						return new PackageMapProbe([], null);
					var selected = SelectPackageTarget(target!, PackageCondition(source, import));
					if (selected.Kind == PackageTargetSelectionKind.Blocked)
						return new PackageMapProbe([], exports
							? "package exports target is null-blocked"
							: "package imports target is null-blocked");
					if (selected.Kind == PackageTargetSelectionKind.Unsupported)
						return new PackageMapProbe([], selected.Reason);
					if (selected.Kind != PackageTargetSelectionKind.Path || selected.Path is null)
						return new PackageMapProbe([], "no applicable package condition");
					var mappedPath = wildcard.Length == 0
						? selected.Path
						: selected.Path.Replace("*", wildcard, StringComparison.Ordinal);
					if (!exports && !mappedPath.StartsWith("./", StringComparison.Ordinal))
					{
						return map.ExternalPackages.Contains(BarePackageName(mappedPath))
							? new PackageMapProbe([], null, IsExternal: true)
							: new PackageMapProbe([], "package imports target has no external-package evidence");
					}
					if (exports && !IsValidPackageExportTarget(mappedPath))
						return new PackageMapProbe([], "package exports target is invalid");
					return new PackageMapProbe(
						ProbeTypeScript(
							Path.GetFullPath(Path.Combine(directory, mappedPath)),
							FindScope(source.ScopeId),
							source,
							allowDirectoryIndex: !exports).ToArray(),
						null);
				}
				if (Path.GetFullPath(directory) == Path.GetFullPath(_root))
					break;
				directory = Path.GetDirectoryName(directory)!;
			}
			return new PackageMapProbe([], null);
		}

		private static string PhysicalModuleSpecifier(string specifier)
		{
			var query = specifier.IndexOf('?');
			var fragment = specifier.IndexOf('#');
			var suffix = query < 0 ? fragment : fragment < 0 ? query : Math.Min(query, fragment);
			return suffix < 0 ? specifier : specifier[..suffix];
		}

		private static bool IsValidPackageExportTarget(string target)
		{
			if (!target.StartsWith("./", StringComparison.Ordinal) || target.Length == 2 || target.Contains('\\'))
				return false;
			return !target[2..].Split('/').Any(segment =>
				segment.Length == 0 ||
				segment is "." or ".." ||
				segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase));
		}

		private PackageMapDescriptor? FindNearestPackageMap(FileFacts source)
		{
			var directory = Path.GetDirectoryName(Path.Combine(_root, source.Path))!;
			while (IsWithin(_root, directory))
			{
				if (_configuration.PackageMaps.TryGetValue(PortableRelative(_root, directory), out var map)) return map;
				if (Path.GetFullPath(directory) == Path.GetFullPath(_root)) break;
				directory = Path.GetDirectoryName(directory)!;
			}
			return null;
		}

		private PackageResolutionConditions PackageCondition(FileFacts source, ImportFact import)
		{
			var scope = FindScope(source.ScopeId);
			var mode = scope?.ModuleResolution ?? "bundler";
			var nodeActive = mode.Equals("node16", StringComparison.OrdinalIgnoreCase) ||
							 mode.Equals("nodenext", StringComparison.OrdinalIgnoreCase) ||
							 mode.Equals("node", StringComparison.OrdinalIgnoreCase) ||
							 mode.Equals("node10", StringComparison.OrdinalIgnoreCase);
			if (IsRequire(import))
				return new PackageResolutionConditions(
					"require", nodeActive, scope?.HasTypeScriptCustomConditions == true);
			if (import.ImportKind == ModuleImportKind.DynamicImport)
				return new PackageResolutionConditions(
					"import", nodeActive, scope?.HasTypeScriptCustomConditions == true);
			var moduleCondition = scope is not null &&
				   (mode.Equals("node16", StringComparison.OrdinalIgnoreCase) ||
					mode.Equals("nodenext", StringComparison.OrdinalIgnoreCase)) &&
				   SupportsCommonJs(source, scope)
				? "require"
				: "import";
			return new PackageResolutionConditions(
				moduleCondition, nodeActive, scope?.HasTypeScriptCustomConditions == true);
		}

		private static bool TryMap(
			IReadOnlyDictionary<string, PackageTargetDescriptor> map,
			string key,
			out PackageTargetDescriptor? target,
			out string wildcard)
		{
			if (map.TryGetValue(key, out target))
			{
				wildcard = string.Empty;
				return true;
			}
			foreach (var pair in map.Where(static pair => pair.Key.Contains('*'))
						 .OrderByDescending(static pair => pair.Key.IndexOf('*'))
						 .ThenByDescending(static pair => pair.Key.Length))
			{
				var star = pair.Key.IndexOf('*');
				var prefix = pair.Key[..star];
				var suffix = pair.Key[(star + 1)..];
				if (prefix.Length + suffix.Length > key.Length ||
					!key.StartsWith(prefix, StringComparison.Ordinal) ||
					!key.EndsWith(suffix, StringComparison.Ordinal))
					continue;
				wildcard = key[prefix.Length..(key.Length - suffix.Length)];
				target = pair.Value;
				return true;
			}
			target = null;
			wildcard = string.Empty;
			return false;
		}

		private static PackageTargetSelection SelectPackageTarget(
			PackageTargetDescriptor target,
			PackageResolutionConditions conditions)
		{
			if (target.Kind == PackageTargetKind.Path)
				return new PackageTargetSelection(PackageTargetSelectionKind.Path, target.Path, null);
			if (target.Kind == PackageTargetKind.Blocked)
				return new PackageTargetSelection(PackageTargetSelectionKind.Blocked, null, null);
			if (target.Kind == PackageTargetKind.Unsupported)
				return new PackageTargetSelection(
					PackageTargetSelectionKind.Unsupported,
					null,
					target.UnsupportedReason ?? "unsupported package target");
			if (conditions.HasTypeScriptCustomConditions)
				return new PackageTargetSelection(
					PackageTargetSelectionKind.Unsupported,
					null,
					TypeScriptCustomConditionsReason);
			foreach (var branch in target.Conditions)
			{
				if (!IsActivePackageCondition(branch.Name, conditions))
					continue;
				if (!IsSupportedPackageCondition(branch.Name))
					return new PackageTargetSelection(
						PackageTargetSelectionKind.Unsupported,
						null,
						"package condition is not supported");
				var selected = SelectPackageTarget(branch.Target, conditions);
				if (selected.Kind != PackageTargetSelectionKind.NoMatch)
					return selected;
			}
			return new PackageTargetSelection(PackageTargetSelectionKind.NoMatch, null, null);
		}

		private static bool IsSupportedPackageCondition(string condition) =>
			condition is "types" or "import" or "require" or "node" or "default";

		private static bool IsActivePackageCondition(
			string condition,
			PackageResolutionConditions conditions) =>
			condition is "types" or "default" ||
			condition == "node" && conditions.NodeActive ||
			condition == conditions.ModuleCondition;

		private IEnumerable<string> ProbeTypeScript(
			string candidate,
			DependencyScopeDescriptor? scope,
			FileFacts source,
			bool allowDirectoryIndex = true)
		{
			foreach (var probe in EnumerateTypeScriptProbes(candidate, scope, source, allowDirectoryIndex))
			{
				var relative = PortableRelative(_root, probe);
				if (TryGetManifestPath(relative, out var manifestPath))
					return [manifestPath];
			}
			return [];
		}

		private IEnumerable<string> ProbeTypeScriptRootDirectories(
			string sourceDirectory,
			string physicalSpecifier,
			DependencyScopeDescriptor scope,
			FileFacts source)
		{
			if (scope.TypeScriptRootDirectories is not { Count: > 0 } rootDirectories)
				return [];
			var candidates = new List<string>();
			foreach (var sourceRoot in rootDirectories)
			{
				if (!IsWithin(sourceRoot, sourceDirectory))
					continue;
				var virtualCandidate = Path.GetFullPath(Path.Combine(sourceDirectory, physicalSpecifier));
				if (!IsWithin(sourceRoot, virtualCandidate))
					continue;
				var virtualPath = Path.GetRelativePath(sourceRoot, virtualCandidate);
				foreach (var destinationRoot in rootDirectories)
				{
					var destination = Path.GetFullPath(Path.Combine(destinationRoot, virtualPath));
					if (!IsWithin(destinationRoot, destination))
						continue;
					candidates.AddRange(ProbeTypeScript(destination, scope, source));
				}
			}
			return candidates;
		}

		/// <summary>
		/// Index files a relative directory specifier may name. This is the same list, in the same
		/// order, that a configured project probes, so a directory resolved without configuration is
		/// always a directory a configured project would have resolved too.
		/// </summary>
		private static readonly string[] TypeScriptIndexSuffixes =
			[".ts", ".tsx", ".d.ts", ".js", ".jsx"];

		/// <summary>
		/// Suffixes that make a sibling file compete with a directory of the same stem. Choosing
		/// between <c>util.js</c> and <c>util/index.js</c> is what module resolution settings decide,
		/// so the presence of any of these leaves the specifier unresolved.
		/// </summary>
		private static readonly string[] TypeScriptSiblingSuffixes =
			[".ts", ".tsx", ".d.ts", ".js", ".jsx", ".mts", ".cts", ".mjs", ".cjs"];

		/// <summary>
		/// Resolution for a file that no <c>tsconfig.json</c> or <c>jsconfig.json</c> owns. Only what
		/// the specifier literally names can be proven without configuration: a relative path that is
		/// itself a manifest file, or a relative directory holding exactly one index file and nothing
		/// that competes with it. Extension substitution, root and path mapping, package resolution,
		/// and <c>main</c> or <c>types</c> entry points all need configuration and keep the existing
		/// unresolved reason.
		/// </summary>
		private DependencyEdge ResolveTypeScriptImportWithoutConfiguration(FileFacts source, ImportFact import)
		{
			if (!IsExplicitRelativeSpecifier(import.Specifier))
				return MissingTypeScriptConfiguration(source, import);
			if (FindNearestPackageMap(source) is { ConfigurationState: not DependencyConfigurationState.Valid })
				return MissingTypeScriptConfiguration(source, import);
			if (IsRequire(import) && !SupportsCommonJs(source, scope: null))
				return MissingTypeScriptConfiguration(source, import);
			var physical = PhysicalModuleSpecifier(import.Specifier);
			if (physical.Length == 0)
				return MissingTypeScriptConfiguration(source, import);
			var directory = Path.GetDirectoryName(Path.Combine(_root, source.Path));
			if (directory is null)
				return MissingTypeScriptConfiguration(source, import);
			var target = Path.GetFullPath(Path.Combine(directory, physical));
			if (!IsWithin(_root, target))
				return MissingTypeScriptConfiguration(source, import);
			var relative = PortableRelative(_root, target);
			if (TryGetManifestPath(relative, out var named))
				return Edge(source, import, ResolutionStatus.Resolved, named,
					"relative specifier names a file in the manifest", [named]);
			if (SingleTypeScriptDirectoryIndex(relative) is not { } index)
				return MissingTypeScriptConfiguration(source, import);
			return Edge(source, import, ResolutionStatus.Resolved, index,
				"relative specifier names a directory with one index file", [index]);
		}

		/// <summary>
		/// A specifier that names a path relative to the importing file. A bare specifier that merely
		/// starts with a dot, such as <c>.config/app.js</c>, is a package name and is not relative.
		/// </summary>
		private static bool IsExplicitRelativeSpecifier(string specifier) =>
			specifier is "." or ".." ||
			specifier.StartsWith("./", StringComparison.Ordinal) ||
			specifier.StartsWith("../", StringComparison.Ordinal);

		private DependencyEdge MissingTypeScriptConfiguration(FileFacts source, ImportFact import) =>
			Edge(source, import, ResolutionStatus.Unresolved, null,
				MissingTypeScriptConfigurationReason, []);

		/// <summary>
		/// The single <c>index.*</c> file directly inside a manifest directory, or
		/// <see langword="null"/> when the directory owns a <c>package.json</c>, when a sibling file
		/// of the same stem competes with it, or when it holds none or more than one index file.
		/// Each of those is a choice only the configuration could make.
		/// </summary>
		private string? SingleTypeScriptDirectoryIndex(string relativeDirectory)
		{
			if (_configuration.PackageMaps.ContainsKey(relativeDirectory))
				return null;
			foreach (var suffix in TypeScriptSiblingSuffixes)
				if (TryGetManifestPath(relativeDirectory + suffix, out _))
					return null;
			var prefix = relativeDirectory is "." or "" ? string.Empty : relativeDirectory + "/";
			string? single = null;
			foreach (var suffix in TypeScriptIndexSuffixes)
			{
				if (!TryGetManifestPath(prefix + "index" + suffix, out var candidate))
					continue;
				if (single is not null)
					return null;
				single = candidate;
			}
			return single;
		}

		private bool TryGetManifestPath(string relative, out string manifestPath)
		{
			if (_manifestFiles.TryGetValue(relative, out var file))
			{
				manifestPath = file.Path;
				return true;
			}
			manifestPath = string.Empty;
			return false;
		}

		private IEnumerable<string> EnumerateTypeScriptProbes(
			string candidate,
			DependencyScopeDescriptor? scope,
			FileFacts source,
			bool allowDirectoryIndex)
		{
			var extension = Path.GetExtension(candidate).ToLowerInvariant();
			var probes = new List<string>();
			if (extension is ".js" or ".jsx" or ".mjs" or ".cjs")
			{
				var stem = candidate[..^extension.Length];
				probes.AddRange(extension switch
				{
					".mjs" => [stem + ".mts", stem + ".d.mts", candidate],
					".cjs" => [stem + ".cts", stem + ".d.cts", candidate],
					".jsx" => [stem + ".tsx", stem + ".d.ts", candidate],
					_ => [stem + ".ts", stem + ".tsx", stem + ".d.ts", candidate]
				});
			}
			else if (extension.Length > 0)
				probes.Add(candidate);
			else
			{
				probes.AddRange([
					candidate + ".ts",
					candidate + ".tsx",
					candidate + ".d.ts",
					candidate + ".js",
					candidate + ".jsx"]);
			}
			var mode = scope?.ModuleResolution ?? "bundler";
			var candidateDirectory = PortableRelative(_root, candidate);
			if (allowDirectoryIndex &&
				!_configuration.PackageMaps.ContainsKey(candidateDirectory) &&
				SupportsDirectoryIndex(mode, source, scope))
			{
				probes.AddRange([
					Path.Combine(candidate, "index.ts"),
					Path.Combine(candidate, "index.tsx"),
					Path.Combine(candidate, "index.d.ts"),
					Path.Combine(candidate, "index.js"),
					Path.Combine(candidate, "index.jsx")]);
			}
			var suffixes = scope?.TypeScriptModuleSuffixes is { Count: > 0 } configuredSuffixes
				? configuredSuffixes
				: [""];
			foreach (var baseProbe in probes)
				foreach (var suffix in suffixes)
					yield return ApplyTypeScriptModuleSuffix(baseProbe, suffix);
		}

		private static string ApplyTypeScriptModuleSuffix(string path, string suffix)
		{
			if (suffix.Length == 0)
				return path;
			var extension = path.EndsWith(".d.mts", StringComparison.OrdinalIgnoreCase) ||
							path.EndsWith(".d.cts", StringComparison.OrdinalIgnoreCase)
				? path[^6..]
				: path.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)
					? path[^5..]
					: Path.GetExtension(path);
			return extension.Length == 0
				? path + suffix
				: path[..^extension.Length] + suffix + extension;
		}

		private bool SupportsDirectoryIndex(
			string mode,
			FileFacts source,
			DependencyScopeDescriptor? scope) =>
			mode.Equals("bundler", StringComparison.OrdinalIgnoreCase) ||
			mode.Equals("node", StringComparison.OrdinalIgnoreCase) ||
			mode.Equals("node10", StringComparison.OrdinalIgnoreCase) ||
			(mode.Equals("node16", StringComparison.OrdinalIgnoreCase) ||
			 mode.Equals("nodenext", StringComparison.OrdinalIgnoreCase)) &&
			SupportsCommonJs(source, scope!);

		private DependencyEdge ResolvePythonImport(FileFacts source, ImportFact import)
		{
			if (FindScope(source.ScopeId) is { } scope && ConfigurationFailure(scope) is { } configurationFailure)
				return Edge(source, import, ResolutionStatus.Unresolved, null, configurationFailure, []);
			var sourceModule = PythonModule(source);
			var sourcePackage = Path.GetFileNameWithoutExtension(source.Path) == "__init__"
				? sourceModule
				: sourceModule.Contains('.') ? sourceModule[..sourceModule.LastIndexOf('.')] : string.Empty;
			var parts = sourcePackage.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
			if (import.RelativeLevel > 0)
			{
				var remove = import.RelativeLevel - 1;
				if (remove >= parts.Count)
					return Edge(source, import, ResolutionStatus.Unresolved, null, "relative import goes beyond the top-level package", []);
				parts.RemoveRange(parts.Count - remove, remove);
			}
			var module = import.RelativeLevel == 0
				? import.Specifier
				: string.Join('.', parts.Concat(import.Specifier.Split('.', StringSplitOptions.RemoveEmptyEntries)));
			var candidates = ProbePythonModule(source, module).ToList();
			var moduleCandidates = candidates.ToArray();
			var moduleEntityExists = moduleCandidates.Length > 0;
			if (import.ImportedName is { Length: > 0 } and not "*")
			{
				if (moduleCandidates.Any(candidate => HasConditionalPythonBinding(candidate, import.ImportedName)))
					return Edge(source, import, ResolutionStatus.Unresolved, null,
						"conditional Python re-export bindings are not indexed", []);
				var provided = moduleCandidates
					.SelectMany(candidate => ResolvePythonStaticBinding(
						candidate,
						import.ImportedName,
						0,
						new HashSet<string>(StringComparer.Ordinal)))
					.Distinct(StringComparer.Ordinal)
					.Order(StringComparer.Ordinal)
					.ToArray();
				if (provided.Length > 0)
					candidates = provided.ToList();
				else if (candidates.Any(IsPythonPackageInitializer))
				{
					var child = module.Length == 0 ? import.ImportedName : module + "." + import.ImportedName;
					candidates = ProbePythonModule(source, child).ToList();
				}
				else
					candidates.Clear();
				if (candidates.Count == 0 && !moduleEntityExists)
				{
					var child = module.Length == 0 ? import.ImportedName : module + "." + import.ImportedName;
					candidates = ProbePythonModule(source, child).ToList();
					if (candidates.Count > 0)
						return FinishImport(source, import, candidates);
				}
				if (candidates.Count == 0 && moduleEntityExists)
				{
					if (moduleCandidates.Any(candidate => HasPythonModuleAssignment(candidate, import.ImportedName)))
						return Edge(source, import, ResolutionStatus.Unresolved, null,
							"Python module assignment bindings are not indexed", []);
					if (moduleCandidates.Any(HasPythonWildcardReExport))
						return Edge(source, import, ResolutionStatus.Unresolved, null,
							"Python wildcard re-export bindings are not indexed", []);
					return Edge(source, import, ResolutionStatus.Unresolved, null,
						"name not found in module", []);
				}
			}
			if (candidates.Count == 0 && !moduleEntityExists)
			{
				var portionCount = CountPythonNamespacePortions(source, module);
				if (portionCount > 0)
					return Edge(source, import, ResolutionStatus.Resolved, "namespace:" + module,
						"one namespace-package entity", []);
			}
			if (candidates.Count == 0 && import.RelativeLevel == 0 && DependencyPlatformCatalog.IsPythonExternal(_configuration, source.ScopeId, import.Specifier))
				return Edge(source, import, ResolutionStatus.External, null, "known Python standard-library module", []);
			if (candidates.Count == 0 && import.RelativeLevel == 0 &&
				FindScope(source.ScopeId)?.PythonExternalPackages.Contains(import.Specifier.Split('.')[0]) == true)
				return Edge(source, import, ResolutionStatus.External, null, "declared Python package outside the manifest", []);
			if (import.IsWildcard && candidates.Any(candidate =>
				_files.TryGetValue(candidate, out var facts) && facts.Aliases.ContainsKey("$dynamic-all")))
				return Edge(source, import, ResolutionStatus.Unresolved, null,
					"dynamic __all__ is an unsupported mechanism", candidates);
			return FinishImport(source, import, candidates);
		}

		private IReadOnlyList<string> ResolvePythonStaticBinding(
			string candidate,
			string name,
			int depth,
			ISet<string> visited)
		{
			if (depth >= 8 || !visited.Add(candidate) || !_files.TryGetValue(candidate, out var facts)) return [];
			if (facts.Declarations.Any(declaration => declaration.ContainingType is null &&
				SimpleName(declaration.Identity.QualifiedName) == name))
				return [candidate];
			foreach (var import in facts.Imports.Where(import => import.ContainingDeclaration is null &&
				string.Equals(PythonBoundName(import), name, StringComparison.Ordinal)).Reverse())
			{
				var sourceModule = PythonModule(facts);
				var sourcePackage = Path.GetFileNameWithoutExtension(candidate) == "__init__"
					? sourceModule
					: sourceModule.Contains('.') ? sourceModule[..sourceModule.LastIndexOf('.')] : string.Empty;
				var parts = sourcePackage.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
				if (import.RelativeLevel > 0)
				{
					var remove = import.RelativeLevel - 1;
					if (remove >= parts.Count) continue;
					parts.RemoveRange(parts.Count - remove, remove);
				}
				var module = import.RelativeLevel == 0 ? import.Specifier :
					string.Join('.', parts.Concat(import.Specifier.Split('.', StringSplitOptions.RemoveEmptyEntries)));
				if (import.ImportedName is { Length: > 0 } imported)
				{
					var child = module.Length == 0 ? imported : module + "." + imported;
					if (import.Specifier.Length == 0)
					{
						var directChildTargets = ProbePythonModule(facts, child).ToArray();
						if (directChildTargets.Length > 0) return directChildTargets;
					}
					var moduleTargets = ProbePythonModule(facts, module).ToArray();
					var nested = moduleTargets
						.SelectMany(next => ResolvePythonStaticBinding(next, imported, depth + 1, visited))
						.Distinct(StringComparer.Ordinal)
						.Order(StringComparer.Ordinal)
						.ToArray();
					if (nested.Length > 0) return nested;
					if (moduleTargets.Any(IsPythonPackageInitializer))
					{
						var childTargets = ProbePythonModule(facts, child).ToArray();
						if (childTargets.Length > 0) return childTargets;
					}
				}
				else
				{
					var moduleTargets = ProbePythonModule(facts, module).ToArray();
					if (moduleTargets.Length > 0) return moduleTargets;
				}
			}
			return [];
		}

		private int CountPythonNamespacePortions(FileFacts source, string module)
		{
			var segments = module.Split('.', StringSplitOptions.RemoveEmptyEntries);
			if (segments.Length == 0) return 0;
			var searchDirectories = PythonRootPrefixes(source).ToList();
			for (var segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
			{
				var namespaceDirectories = new List<string>();
				string? concrete = null;
				foreach (var directory in searchDirectories)
				{
					var prefix = string.Join('/', new[] { directory, segments[segmentIndex] }
						.Where(static value => value.Length > 0));
					concrete = new[]
					{
						prefix + "/__init__.py",
						prefix + ".py",
						prefix + "/__init__.pyi",
						prefix + ".pyi"
					}.FirstOrDefault(_files.ContainsKey);
					if (concrete is not null) break;
					if (_manifestDirectoryPrefixes.Contains(prefix + '/'))
						namespaceDirectories.Add(prefix);
				}
				var isLast = segmentIndex == segments.Length - 1;
				if (concrete is not null)
				{
					if (isLast || !IsPythonPackageInitializer(concrete)) return 0;
					searchDirectories = [concrete[..concrete.LastIndexOf('/')]];
					continue;
				}
				if (namespaceDirectories.Count == 0) return 0;
				if (isLast) return namespaceDirectories.Count;
				searchDirectories = namespaceDirectories;
			}
			return 0;
		}

		private IEnumerable<string> ProbePythonModule(FileFacts source, string module)
		{
			var segments = module.Split('.', StringSplitOptions.RemoveEmptyEntries);
			if (segments.Length == 0) yield break;
			var searchDirectories = PythonRootPrefixes(source).ToList();
			for (var segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
			{
				var isLast = segmentIndex == segments.Length - 1;
				var namespaceDirectories = new List<string>();
				string? concrete = null;
				var concreteIsPackage = false;
				foreach (var directory in searchDirectories)
				{
					var prefix = string.Join('/', new[] { directory, segments[segmentIndex] }
						.Where(static value => value.Length > 0));
					concrete = new[]
					{
						prefix + "/__init__.py",
						prefix + ".py",
						prefix + "/__init__.pyi",
						prefix + ".pyi"
					}.FirstOrDefault(_files.ContainsKey);
					if (concrete is not null)
					{
						concreteIsPackage = IsPythonPackageInitializer(concrete);
						break;
					}
					if (_manifestDirectoryPrefixes.Contains(prefix + '/'))
						namespaceDirectories.Add(prefix);
				}
				if (concrete is not null)
				{
					if (isLast)
					{
						yield return concrete;
						yield break;
					}
					else if (!concreteIsPackage)
						yield break;
					searchDirectories = [concrete[..concrete.LastIndexOf('/')]];
					continue;
				}
				if (namespaceDirectories.Count == 0)
					yield break;
				searchDirectories = namespaceDirectories;
			}
		}

		private bool HasPythonModuleAssignment(string candidate, string name) =>
			_files.TryGetValue(candidate, out var facts) &&
			facts.Aliases.ContainsKey("$python-assignment:" + name);

		private bool HasPythonWildcardReExport(string candidate) =>
			_files.TryGetValue(candidate, out var facts) && facts.Imports.Any(static import =>
				import.ContainingDeclaration is null && import.IsWildcard);

		private bool HasConditionalPythonBinding(string candidate, string name) =>
			_files.TryGetValue(candidate, out var facts) && facts.Imports.Any(import =>
				import.ContainingDeclaration == "$conditional-import" &&
				string.Equals(PythonBoundName(import), name, StringComparison.Ordinal));

		private static string PythonBoundName(ImportFact import) =>
			import.Alias ?? import.ImportedName ?? import.Specifier.Split('.')[0];

		private IReadOnlyList<string> PythonRootPrefixes(FileFacts source)
		{
			if (_pythonRootPrefixesByScope.TryGetValue(source.ScopeId, out var roots))
				return roots;
			return [string.Empty, "src"];
		}

		public DependencyEdge ResolveType(FileFacts source, ReferenceFact reference)
		{
			if (!string.Equals(reference.Reason, "not resolved yet", StringComparison.Ordinal))
				return Edge(source, reference, ResolutionStatus.Unresolved, null, reference.Reason, []);
			if (source.LanguageId == LanguageId.Scala) return ResolveScalaType(source, reference);
			if (reference.Name == "<target-typed-new>")
				return Edge(source, reference, ResolutionStatus.Unresolved, null, "target-typed new has no explicit type", []);
			var simpleName = SimpleName(reference.Name);
			var scope = FindScope(source.ScopeId);
			if ((source.LanguageId is LanguageId.CSharp or LanguageId.TypeScript or LanguageId.Tsx or LanguageId.JavaScript) &&
				scope?.HasConfiguration != true)
			{
				return Edge(source, reference, ResolutionStatus.Unresolved, null,
					source.LanguageId == LanguageId.CSharp
						? "no owning .csproj in the manifest"
						: MissingTypeScriptConfigurationReason, []);
			}
			if (source.LanguageId is not (LanguageId.Java or LanguageId.Kotlin) &&
				scope is not null && ConfigurationFailure(scope) is { } configurationFailure)
				return Edge(source, reference, ResolutionStatus.Unresolved, null, configurationFailure, []);
			var isSyntacticallyQualified = reference.IsGlobalQualified || reference.Name.Contains('.') ||
				reference.Name.Contains("::", StringComparison.Ordinal) || reference.Name.Contains('\\');
			var typeParameterShadowsReference = !isSyntacticallyQualified && (source.TypeParameterScopes.Count > 0
				? _typeParametersByFileAndName.GetValueOrDefault(source.Path)?
					.GetValueOrDefault(simpleName)?.Any(parameter =>
					parameter.Name == simpleName &&
					parameter.StartIndex <= reference.SourceStartIndex &&
					parameter.EndIndex >= reference.SourceStartIndex) == true
				: source.TypeParameters.Contains(simpleName, StringComparer.Ordinal));
			if (typeParameterShadowsReference)
				return Edge(source, reference, ResolutionStatus.Unresolved, null, "type parameter shadows declarations", []);
			if (source.LanguageId == LanguageId.Ruby && IsRubyExternalConstant(source, reference.Name))
				return Edge(source, reference, ResolutionStatus.Unresolved, null, RubyExternalConstantReason, []);
			string? expandedAlias = null;
			var rustCrateAliasExpanded = false;
			var aliasExpanded = source.LanguageId == LanguageId.CSharp &&
								!reference.IsGlobalQualified &&
								TryExpandCSharpAlias(source, reference, out expandedAlias);
			if (!aliasExpanded && source.LanguageId is LanguageId.Java or LanguageId.Kotlin && !isSyntacticallyQualified &&
				source.Aliases.TryGetValue(simpleName, out var javaImport))
			{
				expandedAlias = javaImport;
				aliasExpanded = true;
			}
			if (!aliasExpanded && source.LanguageId == LanguageId.Php && !isSyntacticallyQualified &&
				source.Aliases.TryGetValue(simpleName, out var phpImport))
			{
				expandedAlias = phpImport;
				aliasExpanded = true;
			}
			if (!aliasExpanded && source.LanguageId == LanguageId.Rust && !isSyntacticallyQualified &&
				source.Aliases.TryGetValue(simpleName, out var rustImport))
			{
				expandedAlias = rustImport;
				aliasExpanded = true;
				rustCrateAliasExpanded = source.Imports.Any(import => import.IsCrateQualified &&
					string.Equals(import.Specifier, rustImport, StringComparison.Ordinal) &&
					string.Equals(import.Alias ?? SimpleName(import.Specifier), simpleName, StringComparison.Ordinal));
			}
			var expandedName = aliasExpanded ? expandedAlias! : reference.Name;
			var requiresQualifiedLookup = isSyntacticallyQualified || aliasExpanded;
			var expandedArity = aliasExpanded ? GenericArityFromQualifiedName(expandedName) : 0;
			var lookupArity = expandedArity > 0 ? expandedArity : reference.GenericArity;
			var candidates = requiresQualifiedLookup
				? rustCrateAliasExpanded
					? LookupQualifiedInScope(source, expandedName, lookupArity)
					: source.LanguageId == LanguageId.Kotlin
					? LookupQualifiedAcrossRepository(source, expandedName, lookupArity)
					: LookupQualified(source, expandedName, lookupArity)
				: LookupSimple(source, simpleName, reference.GenericArity);
			var attributeName = reference.SyntaxKind == "attribute"
				? expandedName + "Attribute"
				: null;
			if (source.LanguageId == LanguageId.CSharp)
			{
				if (aliasExpanded && candidates.Length == 0)
				{
					var contextual = LookupContextualCSharpQualified(source, reference, expandedName);
					if (contextual.Length > 0)
						candidates = contextual;
				}
				else if (!reference.IsGlobalQualified && reference.Name.Contains('.') && !aliasExpanded)
				{
					var contextual = LookupContextualCSharpQualified(source, reference, expandedName);
					if (contextual.Length > 0)
						candidates = contextual;
				}
				else if (!requiresQualifiedLookup)
					candidates = SelectVisibleCSharpCandidates(source, reference, candidates);
			}
			else if (source.LanguageId == LanguageId.Go)
				candidates = SelectSamePackageGoCandidates(source, candidates);
			else if (source.LanguageId is LanguageId.Java or LanguageId.Kotlin)
			{
				if (reference.Name.Contains('.') && candidates.Length == 0)
					candidates = LookupQualified(source,
						reference.ContainingNamespace.Length == 0
							? reference.Name
							: reference.ContainingNamespace + "." + reference.Name,
							reference.GenericArity);
				if (!requiresQualifiedLookup)
					candidates = SelectVisibleJavaCandidates(source, reference, candidates);
				if (source.LanguageId == LanguageId.Kotlin)
				{
					candidates = FilterKotlinSourceSetCandidates(source, candidates);
					if (reference.SyntaxKind == "identifier")
						candidates = candidates.Where(static candidate =>
							candidate.Identity.SymbolKind == SymbolKind.Module).ToArray();
				}
			}
			else if (source.LanguageId == LanguageId.Rust && !requiresQualifiedLookup)
				candidates = SelectVisibleRustCandidates(source, reference, candidates);
			else if (source.LanguageId == LanguageId.Ruby)
				candidates = SelectVisibleRubyCandidates(reference, candidates)
					.Where(static candidate => candidate.DeclarationSites
						.Select(static site => site.File)
						.Distinct(StringComparer.Ordinal)
						.Take(2)
						.Count() == 1)
					.ToArray();
			else if (source.LanguageId == LanguageId.Php && !requiresQualifiedLookup)
				candidates = candidates.Where(candidate =>
					string.Equals(candidate.ContainingNamespace, reference.ContainingNamespace, StringComparison.Ordinal)).ToArray();
			else if (source.LanguageId is LanguageId.C or LanguageId.Cpp)
				candidates = SelectVisibleCCandidates(source, candidates);
			if (candidates.Length == 0 && attributeName is not null)
			{
				candidates = attributeName.Contains('.')
					? LookupQualified(source, attributeName, reference.GenericArity)
					: LookupSimple(source, attributeName, reference.GenericArity);
				if (source.LanguageId == LanguageId.CSharp)
				{
					if (!reference.IsGlobalQualified && attributeName.Contains('.'))
					{
						var contextual = LookupContextualCSharpQualified(source, reference, attributeName);
						if (contextual.Length > 0)
							candidates = contextual;
					}
					else if (!attributeName.Contains('.'))
						candidates = SelectVisibleCSharpCandidates(source, reference, candidates);
				}
			}
			if (candidates.Length == 0)
			{
				if (source.LanguageId == LanguageId.CSharp &&
					(IsDotNetExternal(reference.Name) || attributeName is not null && IsDotNetExternal(attributeName)))
					return Edge(source, reference, ResolutionStatus.External, null, "known net10.0 reference symbol", []);
				return Edge(source, reference, ResolutionStatus.Unresolved, null,
					"no declaration in the manifest; absence is not evidence of externality", []);
			}
			var files = candidates.SelectMany(static item => item.DeclarationSites.Select(static site => site.File))
				.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
			return candidates.Length == 1
				? Edge(source, reference, ResolutionStatus.Resolved, files[0],
						"one visible declaration identity", files) with
				{
					DeclarationFiles = files
				}
				: Edge(source, reference, ResolutionStatus.Ambiguous, null, "multiple visible declaration identities", files);
		}

		private DeclarationFact[] LookupSimple(FileFacts source, string name, int arity)
		{
			List<DeclarationFact>? matches = null;
			foreach (var scope in VisibleScopeIds(source.ScopeId))
				foreach (var language in CompatibleLanguages(source.LanguageId))
				{
					if (!_symbolsBySimpleName.TryGetValue(new SymbolLookupKey(scope, language, name), out var candidates))
						continue;
					foreach (var candidate in candidates)
						if (candidate.Identity.GenericArity == arity && IsVisible(source, candidate))
							(matches ??= []).Add(candidate);
				}
			return matches?.ToArray() ?? [];
		}

		private DeclarationFact[] LookupQualified(FileFacts source, string name, int arity)
		{
			List<DeclarationFact>? matches = null;
			var lookupName = QualifiedLookupName(name);
			foreach (var scope in VisibleScopeIds(source.ScopeId))
				foreach (var language in CompatibleLanguages(source.LanguageId))
				{
					if (!_symbolsByQualifiedName.TryGetValue(
							new QualifiedSymbolLookupKey(scope, language, lookupName, arity), out var candidates))
						continue;
					foreach (var candidate in candidates)
						if (IsVisible(source, candidate))
							(matches ??= []).Add(candidate);
				}
			return matches?.ToArray() ?? [];
		}

		private DeclarationFact[] LookupQualifiedInScope(FileFacts source, string name, int arity)
		{
			if (!_symbolsByQualifiedName.TryGetValue(
					new QualifiedSymbolLookupKey(source.ScopeId, source.LanguageId, QualifiedLookupName(name), arity),
					out var candidates))
				return [];
			return candidates.Where(candidate => IsVisible(source, candidate)).ToArray();
		}

		private DeclarationFact[] LookupQualifiedAcrossRepository(FileFacts source, string name, int arity) =>
			FilterKotlinSourceSetCandidates(
				source,
				_declarations.Where(declaration => declaration.Identity.LanguageId == source.LanguageId &&
					declaration.Identity.GenericArity == arity &&
					string.Equals(
						QualifiedLookupName(declaration.Identity.QualifiedName),
						QualifiedLookupName(name),
						StringComparison.Ordinal)).ToArray());

		private static DeclarationFact[] FilterKotlinSourceSetCandidates(
			FileFacts source,
			IEnumerable<DeclarationFact> candidates)
		{
			var sourceSet = KotlinSourceSet(source.Path);
			return candidates.Select(candidate => candidate with
			{
				DeclarationSites = candidate.DeclarationSites
						.Where(site => AreKotlinSourceSetsCompatible(sourceSet, KotlinSourceSet(site.File)))
						.ToArray()
			})
				.Where(static candidate => candidate.DeclarationSites.Count > 0)
				.ToArray();
		}

		private static string? KotlinSourceSet(string path)
		{
			var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
			for (var index = 0; index + 1 < parts.Length; index++)
				if (parts[index] == "src")
					return parts[index + 1];
			return null;
		}

		private static bool AreKotlinSourceSetsCompatible(string? source, string? target)
		{
			if (source is null || target is null || string.Equals(source, target, StringComparison.Ordinal))
				return true;
			if (target.StartsWith("common", StringComparison.OrdinalIgnoreCase))
				return true;
			if (source.StartsWith("common", StringComparison.OrdinalIgnoreCase))
				return false;
			var sourceIsJvm = source.StartsWith("jvm", StringComparison.OrdinalIgnoreCase) || source is "main" or "test";
			var targetIsNonJvm = target.StartsWith("nonJvm", StringComparison.OrdinalIgnoreCase) ||
				target.StartsWith("native", StringComparison.OrdinalIgnoreCase) ||
				target.StartsWith("js", StringComparison.OrdinalIgnoreCase) ||
				target.StartsWith("wasm", StringComparison.OrdinalIgnoreCase);
			return !(sourceIsJvm && targetIsNonJvm);
		}

		private static bool IsPythonPackageInitializer(string path) =>
			Path.GetFileName(path).StartsWith("__init__.", StringComparison.Ordinal);

		private DeclarationFact[] SelectVisibleCSharpCandidates(
			FileFacts source,
			ReferenceFact reference,
			IReadOnlyList<DeclarationFact> candidates)
		{
			if (reference.ContainingType is { Length: > 0 } containingType)
			{
				foreach (var enclosingType in EnumerateContainingTypes(containingType, reference.ContainingNamespace))
				{
					var nested = candidates.Where(candidate =>
						string.Equals(candidate.ContainingType, enclosingType, StringComparison.Ordinal)).ToArray();
					if (nested.Length > 0)
						return nested;
				}
			}

			var namespaceName = reference.ContainingNamespace;
			if (namespaceName.Length == 0)
			{
				var global = candidates.Where(candidate =>
					candidate.ContainingType is null &&
					candidate.ContainingNamespace.Length == 0).ToArray();
				if (global.Length > 0)
					return global;
			}
			while (namespaceName.Length > 0)
			{
				var lexical = candidates.Where(candidate =>
					candidate.ContainingType is null &&
					candidate.ContainingNamespace.Equals(namespaceName, StringComparison.Ordinal)).ToArray();
				if (lexical.Length > 0)
					return lexical;
				var separator = namespaceName.LastIndexOf('.');
				namespaceName = separator < 0 ? string.Empty : namespaceName[..separator];
			}

			var importedNamespaces = ActiveCSharpNamespaces(source, reference);
			var importedStaticTypes = ActiveCSharpStaticTypes(source, reference);
			var importedNested = candidates.Where(candidate =>
				candidate.ContainingType is not null &&
				importedStaticTypes.Contains(
					QualifiedLookupName(candidate.ContainingType),
					StringComparer.Ordinal)).ToArray();
			if (importedNested.Length > 0)
				return importedNested;
			var imported = candidates.Where(candidate =>
				candidate.ContainingType is null &&
				importedNamespaces.Contains(candidate.ContainingNamespace, StringComparer.Ordinal)).ToArray();
			if (imported.Length > 0)
				return imported;

			return candidates.Where(static candidate =>
				candidate.ContainingType is null && candidate.ContainingNamespace.Length == 0).ToArray();
		}

		private static DeclarationFact[] SelectVisibleJavaCandidates(
			FileFacts source,
			ReferenceFact reference,
			DeclarationFact[] candidates)
		{
			if (reference.ContainingType is not null)
			{
				var containingType = reference.ContainingType;
				while (containingType.Length > 0)
				{
					var nested = candidates.Where(candidate =>
						string.Equals(candidate.ContainingType, containingType, StringComparison.Ordinal)).ToArray();
					if (nested.Length > 0) return nested;
					var separator = containingType.LastIndexOf('.');
					if (separator < 0) break;
					containingType = containingType[..separator];
				}
			}
			var samePackage = candidates.Where(candidate => candidate.ContainingType is null &&
				string.Equals(candidate.ContainingNamespace, reference.ContainingNamespace, StringComparison.Ordinal)).ToArray();
			if (samePackage.Length > 0) return samePackage;
			return candidates.Where(candidate => candidate.ContainingType is null &&
				source.GlobalContextNamespaces.Contains(candidate.ContainingNamespace, StringComparer.Ordinal)).ToArray();
		}

		private DeclarationFact[] SelectVisibleCCandidates(FileFacts source, DeclarationFact[] candidates)
		{
			var visibleFiles = new HashSet<string>(StringComparer.Ordinal) { source.Path };
			foreach (var import in source.Imports)
			{
				var edge = ResolveCImport(source, import);
				if (edge.Target is not null) visibleFiles.Add(edge.Target);
				foreach (var candidate in edge.Candidates) visibleFiles.Add(candidate);
			}

			return candidates.Where(candidate =>
				candidate.Identity.SymbolKind != SymbolKind.Function &&
				candidate.DeclarationSites.Any(site => visibleFiles.Contains(site.File))).ToArray();
		}

		private static DeclarationFact[] SelectVisibleRustCandidates(
			FileFacts source,
			ReferenceFact reference,
			DeclarationFact[] candidates)
		{
			var sameModule = candidates.Where(candidate =>
				string.Equals(candidate.Identity.ScopeId, source.ScopeId, StringComparison.Ordinal) &&
				string.Equals(candidate.ContainingNamespace, reference.ContainingNamespace, StringComparison.Ordinal)).ToArray();
			if (sameModule.Length > 0) return sameModule;
			return candidates.Where(candidate => source.GlobalContextNamespaces.Contains(
				candidate.ContainingNamespace, StringComparer.Ordinal)).ToArray();
		}

		private static DeclarationFact[] SelectVisibleRubyCandidates(
			ReferenceFact reference,
			DeclarationFact[] candidates)
		{
			if (reference.Name.Contains("::", StringComparison.Ordinal)) return candidates;
			if (reference.ContainingType is null)
				return candidates.Where(static candidate => candidate.ContainingType is null).ToArray();
			var owner = reference.ContainingType;
			while (owner.Length > 0)
			{
				var nested = candidates.Where(candidate =>
					string.Equals(candidate.ContainingType, owner, StringComparison.Ordinal)).ToArray();
				if (nested.Length > 0) return nested;
				var separator = owner.LastIndexOf("::", StringComparison.Ordinal);
				if (separator < 0) break;
				owner = owner[..separator];
			}
			return candidates.Where(static candidate => candidate.ContainingType is null).ToArray();
		}

		private bool IsRubyExternalConstant(FileFacts source, string reference)
		{
			var rootSeparator = reference.IndexOf("::", StringComparison.Ordinal);
			var root = rootSeparator < 0 ? reference : reference[..rootSeparator];
			if (RubyRuntimeConstants.Contains(root)) return true;
			var normalizedRoot = NormalizeRubyPackageName(root);
			var scope = FindScope(source.ScopeId);
			if (scope is not null && scope.RubyExternalPackages.Any(package =>
					string.Equals(NormalizeRubyPackageName(package), normalizedRoot, StringComparison.Ordinal)))
				return true;
			return source.Imports.Any(import =>
				import.ImportedName != "$relative" &&
				string.Equals(
					NormalizeRubyPackageName(import.Specifier.Split('/')[0]),
					normalizedRoot,
					StringComparison.Ordinal) &&
				!RubyImportCandidatePaths(source, import).Any(_files.ContainsKey));
		}

		private static string NormalizeRubyPackageName(string value)
		{
			var buffer = new char[value.Length];
			var length = 0;
			foreach (var character in value)
				if (char.IsAsciiLetterOrDigit(character))
					buffer[length++] = char.ToLowerInvariant(character);
			return new string(buffer, 0, length);
		}

		private DeclarationFact[] LookupContextualCSharpQualified(
			FileFacts source,
			ReferenceFact reference,
			string qualifiedName)
		{
			var namespaceName = reference.ContainingNamespace;
			while (namespaceName.Length > 0)
			{
				var lexical = LookupQualified(source, namespaceName + "." + qualifiedName, reference.GenericArity);
				if (lexical.Length > 0)
					return lexical;
				var separator = namespaceName.LastIndexOf('.');
				namespaceName = separator < 0 ? string.Empty : namespaceName[..separator];
			}

			return ActiveCSharpNamespaces(source, reference)
				.SelectMany(namespaceValue => LookupQualified(
					source,
					namespaceValue + "." + qualifiedName,
					reference.GenericArity))
				.Distinct()
				.ToArray();
		}

		private static IEnumerable<string> EnumerateContainingTypes(
			string containingType,
			string containingNamespace)
		{
			var current = containingType;
			while (current.Length > containingNamespace.Length)
			{
				yield return current;
				var separator = current.LastIndexOf('.');
				if (separator < 0)
					yield break;
				current = current[..separator];
			}
		}

		private IReadOnlyList<string> VisibleScopeIds(string scopeId) =>
			_visibleScopesById.GetValueOrDefault(scopeId) ?? [scopeId];

		private static IReadOnlyList<LanguageId> CompatibleLanguages(LanguageId languageId) => languageId switch
		{
			LanguageId.TypeScript or LanguageId.Tsx or LanguageId.JavaScript => TypeScriptLanguages,
			_ => [languageId]
		};

		private bool IsVisible(FileFacts source, DeclarationFact declaration)
		{
			var targetLanguage = declaration.Identity.LanguageId;
			if (targetLanguage != source.LanguageId &&
				!(IsTypeScript(source.LanguageId) && IsTypeScript(targetLanguage)))
				return false;
			if (declaration.Identity.FileScope is not null && declaration.Identity.FileScope != source.Path)
				return false;
			if (declaration.Identity.ScopeId == source.ScopeId)
				return true;
			return source.LanguageId is LanguageId.CSharp or LanguageId.Java or LanguageId.Kotlin or LanguageId.Rust or LanguageId.Ruby or LanguageId.Php or LanguageId.C or LanguageId.Cpp &&
				   VisibleScopeIds(source.ScopeId).Contains(
				   declaration.Identity.ScopeId, StringComparer.Ordinal);
		}

		private DependencyEdge FinishImport(
			FileFacts source,
			ImportFact import,
			IEnumerable<string> raw,
			string? resolvedReason = null)
		{
			var candidates = raw.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
			return candidates.Length switch
			{
				0 => Edge(source, import, ResolutionStatus.Unresolved, null, "no target in the manifest for this module", []),
				1 => Edge(source, import, ResolutionStatus.Resolved, candidates[0],
					resolvedReason ?? "one module target", candidates),
				_ => Edge(source, import, ResolutionStatus.Ambiguous, null, "multiple module targets", candidates)
			};
		}

		private DependencyEdge Edge(FileFacts source, ImportFact import, ResolutionStatus status, string? target, string reason, IReadOnlyList<string> candidates) =>
			CreateEdge(source, target, EvidenceLayer.ExplicitImport, status,
				import.IsWildcard ? import.Specifier + ".*" : import.Specifier,
				reason, import.Site, candidates);

		private DependencyEdge Edge(FileFacts source, ReferenceFact reference, ResolutionStatus status, string? target, string reason, IReadOnlyList<string> candidates)
		{
			var edge = CreateEdge(source, target, reference.Layer, status, reference.Name, reason, reference.Site, candidates);
			return reference.SyntaxKind == "call_type_argument"
				? edge with { Reasons = [.. edge.Reasons, "type argument of a call"] }
				: edge;
		}

		private DependencyEdge CreateEdge(FileFacts source, string? target, EvidenceLayer layer, ResolutionStatus status, string reference, string reason, SourceSite site, IReadOnlyList<string> candidates)
		{
			var targetScope = target is not null && _files.TryGetValue(target, out var targetFacts) ? targetFacts.ScopeId : null;
			var crossScope = targetScope is not null && targetScope != source.ScopeId || candidates.Any(candidate =>
				_files.TryGetValue(candidate, out var candidateFacts) && candidateFacts.ScopeId != source.ScopeId);
			return new DependencyEdge(source.Path, target, layer, status, reference, [reason], [site], candidates,
				crossScope);
		}

		private static bool IsTypeScript(LanguageId id) => id is LanguageId.TypeScript or LanguageId.JavaScript or LanguageId.Tsx;
		private bool IsDotNetExternal(string reference) =>
			DependencyPlatformCatalog.IsDotNetAlias(reference) ||
			_configuration.DotNetExternalSymbols.Contains(reference) ||
			!reference.Contains('.') && !reference.Contains("::", StringComparison.Ordinal) &&
			_dotNetExternalSimpleNames.Contains(reference);
		private bool TryExpandCSharpAlias(
			FileFacts source,
			ReferenceFact reference,
			out string? expanded)
		{
			var separator = reference.Name.IndexOf('.');
			var prefix = separator < 0 ? reference.Name : reference.Name[..separator];
			var suffix = separator < 0 ? string.Empty : reference.Name[separator..];
			var local = _aliasesByFileAndName.GetValueOrDefault(source.Path)?
				.GetValueOrDefault(prefix)?
				.FirstOrDefault(directive => IsActive(directive, reference.SourceStartIndex));
			if (local is not null)
			{
				expanded = local.Target + suffix;
				return true;
			}
			var globalAliases = _globalAliases.GetValueOrDefault(source.ScopeId);
			if (globalAliases?.TryGetValue(prefix, out var target) == true)
			{
				expanded = target + suffix;
				return true;
			}
			expanded = null;
			return false;
		}

		private IReadOnlyList<string> ActiveCSharpNamespaces(FileFacts source, ReferenceFact reference) =>
			_csharpNamespaceRegionsByFile.GetValueOrDefault(source.Path)?.At(reference.SourceStartIndex) ?? [];

		private IReadOnlyList<string> ActiveCSharpStaticTypes(
			FileFacts source,
			ReferenceFact reference) =>
			(_globalNamespaces.GetValueOrDefault(source.ScopeId) ?? [])
			.Concat(source.CSharpUsingDirectives
				.Where(directive => directive.Alias is null &&
									directive.Target.StartsWith(StaticUsingPrefix, StringComparison.Ordinal) &&
									IsActive(directive, reference.SourceStartIndex))
				.Select(static directive => directive.Target))
			.Where(static target => target.StartsWith(StaticUsingPrefix, StringComparison.Ordinal))
			.Select(static target => QualifiedLookupName(target[StaticUsingPrefix.Length..]))
			.Distinct(StringComparer.Ordinal)
			.ToArray();

		private static bool IsActive(CSharpUsingDirective directive, int sourceStartIndex) =>
			directive.ScopeStartIndex <= sourceStartIndex && directive.ScopeEndIndex >= sourceStartIndex;

		private sealed class CSharpNamespaceRegions(int[] starts, string[][] namespaces)
		{
			public IReadOnlyList<string> At(int sourceStartIndex)
			{
				var index = Array.BinarySearch(starts, sourceStartIndex);
				if (index < 0)
					index = ~index - 1;
				return namespaces[Math.Max(0, index)];
			}

			public static CSharpNamespaceRegions Create(
				IEnumerable<string> baseNamespaces,
				IReadOnlyList<CSharpUsingDirective> directives)
			{
				var baseValues = baseNamespaces
					.Where(static value => !value.StartsWith(StaticUsingPrefix, StringComparison.Ordinal));
				var local = directives.Where(static directive =>
					directive.Alias is null &&
					!directive.Target.StartsWith(StaticUsingPrefix, StringComparison.Ordinal)).ToArray();
				var boundaries = new SortedSet<int> { int.MinValue };
				foreach (var directive in local)
				{
					boundaries.Add(directive.ScopeStartIndex);
					if (directive.ScopeEndIndex < int.MaxValue)
						boundaries.Add(directive.ScopeEndIndex + 1);
				}
				var starts = boundaries.ToArray();
				var values = new string[starts.Length][];
				for (var index = 0; index < starts.Length; index++)
				{
					var position = starts[index];
					values[index] = baseValues.Concat(local
							.Where(directive => IsActive(directive, position))
							.Select(static directive => directive.Target))
						.Distinct(StringComparer.Ordinal)
						.Order(StringComparer.Ordinal)
						.ToArray();
				}
				return new CSharpNamespaceRegions(starts, values);
			}
		}

		private static string QualifiedLookupName(string qualified)
		{
			var marker = qualified.IndexOf('`');
			if (marker < 0) return qualified;
			var result = new StringBuilder(qualified.Length);
			for (var index = 0; index < qualified.Length; index++)
			{
				if (qualified[index] != '`')
				{
					result.Append(qualified[index]);
					continue;
				}
				while (index + 1 < qualified.Length && char.IsAsciiDigit(qualified[index + 1])) index++;
			}
			return result.ToString();
		}

		private static int GenericArityFromQualifiedName(string qualified)
		{
			var marker = qualified.LastIndexOf('`');
			if (marker < 0 || marker + 1 >= qualified.Length)
				return 0;
			var end = marker + 1;
			while (end < qualified.Length && char.IsAsciiDigit(qualified[end])) end++;
			return int.TryParse(qualified.AsSpan(marker + 1, end - marker - 1), out var arity)
				? arity
				: 0;
		}

		/// <summary>
		/// A Go package is a directory, so a name is visible to a file only when it is declared in
		/// the same directory. Without this the one Go scope spans the whole root and a name
		/// declared in two packages would read as ambiguous.
		/// </summary>
		private static DeclarationFact[] SelectSamePackageGoCandidates(
			FileFacts source,
			DeclarationFact[] candidates)
		{
			var package = GoPackageDirectory(source.Path);
			return candidates
				.Where(candidate => candidate.Identity.SymbolKind == SymbolKind.Class &&
					candidate.DeclarationSites.Any(site =>
					string.Equals(GoPackageDirectory(site.File), package, StringComparison.Ordinal)))
				.ToArray();
		}

		private static string GoPackageDirectory(string portablePath)
		{
			var separator = portablePath.LastIndexOf('/');
			return separator < 0 ? string.Empty : portablePath[..separator];
		}
		private static string SimpleName(string qualified)
		{
			var separator = Math.Max(qualified.LastIndexOf('.'), qualified.LastIndexOf('#'));
			var rustSeparator = qualified.LastIndexOf("::", StringComparison.Ordinal);
			if (rustSeparator >= 0) separator = Math.Max(separator, rustSeparator + 1);
			separator = Math.Max(separator, qualified.LastIndexOf('\\'));
			var value = qualified[(separator + 1)..];
			var arity = value.IndexOf('`');
			return arity < 0 ? value : value[..arity];
		}
		private string PythonModule(FileFacts source)
		{
			if (_pythonModuleByFile.TryGetValue(source.Path, out var module))
				return module;
			return ComputePythonModule(source);
		}

		private string ComputePythonModule(FileFacts source)
		{
			var root = PythonRootPrefixes(source)
				.Where(prefix => prefix.Length == 0 || source.Path.StartsWith(prefix + '/', StringComparison.Ordinal))
				.OrderByDescending(static prefix => prefix.Length)
				.FirstOrDefault();
			var relative = root is { Length: > 0 } ? source.Path[(root.Length + 1)..] : source.Path;
			var computed = Path.ChangeExtension(relative, null)!.Replace('/', '.').Replace('\\', '.');
			return computed.EndsWith(".__init__", StringComparison.Ordinal) ? computed[..^".__init__".Length] : computed;
		}

		private static IReadOnlySet<string> BuildDirectoryPrefixes(IEnumerable<FileFacts> files)
		{
			var result = new HashSet<string>(StringComparer.Ordinal);
			foreach (var file in files)
			{
				var separator = file.Path.IndexOf('/');
				while (separator >= 0)
				{
					result.Add(file.Path[..(separator + 1)]);
					separator = file.Path.IndexOf('/', separator + 1);
				}
			}
			return result;
		}

		private DependencyScopeDescriptor? FindScope(string scopeId) =>
			_scopesById.GetValueOrDefault(scopeId);

		private static string? ConfigurationFailure(DependencyScopeDescriptor scope) =>
			scope.ConfigurationState == DependencyConfigurationState.Valid
				? null
				: scope.ConfigurationDiagnostic ??
				  $"{scope.ConfigurationState.ToString().ToLowerInvariant()} dependency configuration";

		private static IReadOnlyDictionary<string, string[]> BuildVisibleScopes(
			IReadOnlyDictionary<string, DependencyScopeDescriptor> scopes)
		{
			var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
			foreach (var scope in scopes.Values)
			{
				if (scope.LanguageId == LanguageId.CSharp && scope.DisableTransitiveProjectReferences)
				{
					result[scope.ScopeId] = new[] { scope.ScopeId }
						.Concat(scope.ProjectReferences.Where(scopes.ContainsKey))
						.Distinct(StringComparer.Ordinal)
						.Order(StringComparer.Ordinal)
						.ToArray();
					continue;
				}
				var pending = new Queue<string>();
				var visited = new HashSet<string>(StringComparer.Ordinal);
				pending.Enqueue(scope.ScopeId);
				while (pending.TryDequeue(out var scopeId))
				{
					if (!visited.Add(scopeId)) continue;
					if (scope.LanguageId is not (LanguageId.CSharp or LanguageId.Java or LanguageId.Kotlin or LanguageId.Rust or LanguageId.Ruby or LanguageId.Php or LanguageId.C or LanguageId.Cpp) ||
						!scopes.TryGetValue(scopeId, out var current)) continue;
					foreach (var projectReference in current.ProjectReferences) pending.Enqueue(projectReference);
				}
				result[scope.ScopeId] = visited.Order(StringComparer.Ordinal).ToArray();
			}
			return result;
		}

		private static readonly LanguageId[] TypeScriptLanguages =
			[LanguageId.TypeScript, LanguageId.Tsx, LanguageId.JavaScript];

		private readonly record struct SymbolLookupKey(string ScopeId, LanguageId LanguageId, string SimpleName);
		private readonly record struct QualifiedSymbolLookupKey(
			string ScopeId,
			LanguageId LanguageId,
			string QualifiedName,
			int GenericArity);
		private readonly record struct TypeScriptPathMapping(
			string Pattern,
			IReadOnlyList<string> Targets,
			int Star);
		private readonly record struct PackageMapProbe(
			IReadOnlyList<string> Candidates,
			string? FailureReason,
			bool IsExternal = false);
		private readonly record struct PackageTargetSelection(
			PackageTargetSelectionKind Kind,
			string? Path,
			string? Reason);
		private readonly record struct PackageResolutionConditions(
			string ModuleCondition,
			bool NodeActive,
			bool HasTypeScriptCustomConditions);
		private readonly record struct BashCandidateProbe(
			IReadOnlyList<string> Candidates,
			int Total);
		private enum PackageTargetSelectionKind
		{
			NoMatch,
			Path,
			Blocked,
			Unsupported
		}
	}
}
