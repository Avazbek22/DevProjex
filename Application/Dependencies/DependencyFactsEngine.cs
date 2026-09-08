using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace DevProjex.Application.Dependencies;

public sealed class DependencyFactsEngine : IDisposable
{
	private readonly IDependencyFactExtractor _extractor;
	private readonly IDependencyConfigurationProvider _configurationProvider;
	private readonly DependencyFactsLimits _limits;
	private readonly ConcurrentDictionary<FileCacheKey, Lazy<Task<FileFacts>>> _fileCache = [];
	private readonly ConcurrentQueue<FileCacheKey> _fileCacheOrder = [];
	private readonly ConcurrentDictionary<FileCacheKey, long> _fileCacheWeights = [];
	private readonly ConcurrentDictionary<IndexCacheKey, Lazy<Task<ResolvedIndex>>> _indexCache = [];
	private readonly ConcurrentQueue<IndexCacheKey> _indexCacheOrder = [];
	private readonly ConcurrentDictionary<IndexCacheKey, long> _indexCacheWeights = [];
	private readonly ConcurrentDictionary<ManifestRequestKey, ManifestSnapshotCacheEntry> _manifestSnapshots = [];
	private readonly LinkedList<ManifestSnapshotCacheEntry> _manifestSnapshotOrder = [];
	private readonly object _cacheTrimSync = new();
	private long _fileCacheBytes;
	private long _indexCacheBytes;
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
	}

	public int ParseCount => _extractor.ParseCount;
	public int CompiledQuerySetCount => _extractor.CompiledQuerySetCount;
	internal DependencyFactsCacheState CacheState
	{
		get
		{
			lock (_cacheTrimSync)
				return new(
					_manifestSnapshots.Count,
					_manifestSnapshotOrder.Count,
					_indexCache.Count,
					_indexCacheBytes);
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
		var started = Stopwatch.StartNew();
		var root = Path.GetFullPath(sourceRoot);
		var manifest = manifestFiles
			.Select(Path.GetFullPath)
			.Where(path => IsWithin(root, path))
			.Distinct(PathComparer)
			.OrderBy(path => PortableRelative(root, path), StringComparer.Ordinal)
			.ToArray();
		var manifestRelativePaths = manifest.Select(path => PortableRelative(root, path)).ToArray();
		var manifestRequestKey = new ManifestRequestKey(
			root,
			Hash(manifestRelativePaths));
		var initialStamps = TryCaptureFileStamps(manifest);
		var alignedContentIdentities = AlignContentIdentities(manifest, contentIdentities);
		if (initialStamps is not null &&
		    _manifestSnapshots.TryGetValue(manifestRequestKey, out var cachedSnapshot) &&
		    cachedSnapshot.ManifestPaths.SequenceEqual(manifestRelativePaths, StringComparer.Ordinal) &&
		    cachedSnapshot.Stamps.SequenceEqual(initialStamps) &&
		    ContentIdentitiesMatch(cachedSnapshot.ContentIdentities, alignedContentIdentities) &&
		    _indexCache.ContainsKey(cachedSnapshot.IndexCacheKey))
		{
			var snapshot = cachedSnapshot.Snapshot;
			progress?.Report(new DependencyIndexProgress(manifest.Length, manifest.Length));
			return snapshot with
			{
				Metrics = new DependencyIndexMetrics(
					0,
					snapshot.Files.Count,
					0,
					started.ElapsedMilliseconds,
					true)
			};
		}
		var configuration = await _configurationProvider
			.ReadAsync(root, manifest, cancellationToken)
			.ConfigureAwait(false);
		var prepared = new PreparedDependencyIdentity[manifest.Length];
		var parsedBefore = _extractor.ParseCount;
		var facts = new FileFacts[prepared.Length];
		var cacheable = new bool[prepared.Length];
		var completed = 0;
		var reusedFiles = 0;
		await Parallel.ForEachAsync(
			Enumerable.Range(0, prepared.Length),
			new ParallelOptions
			{
				CancellationToken = cancellationToken,
				MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8)
			},
			async (index, token) =>
			{
				var contentIdentity = alignedContentIdentities?[index];
				var source = await _extractor
					.PrepareAsync(root, manifest[index], configuration, _limits, token, contentIdentity)
					.ConfigureAwait(false);
				prepared[index] = new PreparedDependencyIdentity(
					source.RelativePath,
					source.ContentFingerprint,
					source.LanguageId);
				if (source.PreparedStatus != DependencyFileStatus.Supported)
				{
					var extracted = _extractor.Extract(source, _limits);
					facts[index] = extracted;
					cacheable[index] = source.CanCache && extracted.CanCache;
					progress?.Report(new DependencyIndexProgress(
						Interlocked.Increment(ref completed),
						prepared.Length));
					return;
				}
				var key = CreateFileCacheKey(source);
				var created = new Lazy<Task<FileFacts>>(
					() => Task.Run(() => _extractor.Extract(source, _limits), token),
					LazyThreadSafetyMode.ExecutionAndPublication);
				var lazy = _fileCache.GetOrAdd(key, created);
				if (ReferenceEquals(lazy, created))
					_fileCacheOrder.Enqueue(key);
				else
					Interlocked.Increment(ref reusedFiles);
				try
				{
					var extracted = await lazy.Value.ConfigureAwait(false);
					if (!extracted.CanCache)
						_fileCache.TryRemove(new KeyValuePair<FileCacheKey, Lazy<Task<FileFacts>>>(key, lazy));
					else if (ReferenceEquals(lazy, created))
						RegisterFileCacheWeight(key, lazy, EstimateFileFactsBytes(extracted));
					facts[index] = RebindScope(extracted, source.ScopeId);
					cacheable[index] = source.CanCache && extracted.CanCache;
				}
				catch
				{
					_fileCache.TryRemove(new KeyValuePair<FileCacheKey, Lazy<Task<FileFacts>>>(key, lazy));
					throw;
				}
				progress?.Report(new DependencyIndexProgress(
					Interlocked.Increment(ref completed),
					prepared.Length));
			}).ConfigureAwait(false);

		var manifestGeneration = Hash(prepared.Select(source =>
			$"{source.RelativePath}\0{source.ContentFingerprint}\0{source.LanguageId}"));
		var parsedFiles = _extractor.ParseCount - parsedBefore;
		var orderedFacts = facts.OrderBy(static fact => fact.Path, StringComparer.Ordinal).ToArray();
		var declarations = MergeDeclarations(orderedFacts);
		var declarationRevision = Hash(declarations.Select(DeclarationKey));
		var cacheKey = new IndexCacheKey(
			manifestGeneration,
			declarationRevision,
			configuration.Fingerprint);
		var allowed = orderedFacts.Select(static fact => fact.Path).ToHashSet(StringComparer.Ordinal);
		var canCacheIndex = configuration.CanCache && cacheable.All(static value => value);
		var createdIndex = new Lazy<Task<ResolvedIndex>>(
			() => Task.FromResult(GateResolvedIndex(
				DependencyResolver.Resolve(
					root,
					orderedFacts,
					declarations,
					configuration,
					_limits,
					cancellationToken),
				allowed)),
			LazyThreadSafetyMode.ExecutionAndPublication);
		ResolvedIndex resolved;
		var resolutionCacheHit = false;
		if (!canCacheIndex)
		{
			resolved = await createdIndex.Value.ConfigureAwait(false);
		}
		else
		{
			var cachedIndex = _indexCache.GetOrAdd(cacheKey, createdIndex);
			if (ReferenceEquals(cachedIndex, createdIndex))
				_indexCacheOrder.Enqueue(cacheKey);
			try
			{
				resolved = await cachedIndex.Value.ConfigureAwait(false);
			}
			catch
			{
				_indexCache.TryRemove(new KeyValuePair<IndexCacheKey, Lazy<Task<ResolvedIndex>>>(cacheKey, cachedIndex));
				throw;
			}
			resolutionCacheHit = !ReferenceEquals(cachedIndex, createdIndex);
			if (!resolutionCacheHit)
				RegisterIndexCacheWeight(cacheKey, cachedIndex, EstimateResolvedIndexBytes(resolved));
		}
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
				resolutionCacheHit))
		{
			FileByPath = resolved.FileByPath
		};
		var finalStamps = TryCaptureFileStamps(manifest);
		if (canCacheIndex && initialStamps is not null && finalStamps is not null && initialStamps.SequenceEqual(finalStamps))
		{
			if (_indexCache.ContainsKey(cacheKey))
				StoreManifestSnapshot(
					manifestRequestKey,
					manifestRelativePaths,
					initialStamps,
					alignedContentIdentities,
					cacheKey,
					result);
		}
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
			ConfigurationDiagnostics = configurationDiagnostics
		};

	private void RegisterFileCacheWeight(
		FileCacheKey key,
		Lazy<Task<FileFacts>> entry,
		long weight)
	{
		lock (_cacheTrimSync)
		{
			if (_fileCache.TryGetValue(key, out var current) && ReferenceEquals(current, entry) &&
			    _fileCacheWeights.TryAdd(key, weight))
				_fileCacheBytes += weight;
			TrimFileCache();
		}
	}

	private void RegisterIndexCacheWeight(
		IndexCacheKey key,
		Lazy<Task<ResolvedIndex>> entry,
		long weight)
	{
		lock (_cacheTrimSync)
		{
			if (_indexCache.TryGetValue(key, out var current) && ReferenceEquals(current, entry) &&
			    _indexCacheWeights.TryAdd(key, weight))
				_indexCacheBytes += weight;
			TrimIndexCache();
		}
	}

	private void TrimFileCache()
	{
		while ((_fileCache.Count > _limits.MaximumCachedFiles || _fileCacheBytes > _limits.MaximumFileCacheBytes) &&
		       _fileCacheOrder.TryDequeue(out var oldest))
		{
			_fileCache.TryRemove(oldest, out _);
			if (_fileCacheWeights.TryRemove(oldest, out var weight))
				_fileCacheBytes -= weight;
		}
	}

	private void TrimIndexCache()
	{
		while ((_indexCache.Count > _limits.MaximumCachedIndexes || _indexCacheBytes > _limits.MaximumIndexCacheBytes) &&
		       _indexCacheOrder.TryDequeue(out var oldest))
		{
			_indexCache.TryRemove(oldest, out _);
			foreach (var snapshot in _manifestSnapshots.Where(pair => pair.Value.IndexCacheKey == oldest).ToArray())
				RemoveManifestSnapshotUnderLock(snapshot.Key, snapshot.Value);
			if (_indexCacheWeights.TryRemove(oldest, out var weight))
				_indexCacheBytes -= weight;
		}
	}

	private void StoreManifestSnapshot(
		ManifestRequestKey key,
		IReadOnlyList<string> manifestPaths,
		IReadOnlyList<FileStamp> stamps,
		IReadOnlyList<string>? contentIdentities,
		IndexCacheKey indexCacheKey,
		DependencyIndexSnapshot snapshot)
	{
		lock (_cacheTrimSync)
		{
			if (!_indexCache.ContainsKey(indexCacheKey)) return;
			if (_manifestSnapshots.TryGetValue(key, out var previous))
				RemoveManifestSnapshotUnderLock(key, previous);
			var entry = new ManifestSnapshotCacheEntry(key, manifestPaths, stamps, contentIdentities, indexCacheKey, snapshot);
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

	private static IReadOnlyList<FileStamp>? TryCaptureFileStamps(IReadOnlyList<string> manifest)
	{
		try
		{
			var stamps = new FileStamp[manifest.Count];
			for (var index = 0; index < manifest.Count; index++)
			{
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
		DependencyManifestContentIdentities? contentIdentities)
	{
		if (contentIdentities is null)
			return null;
		var aligned = new string[manifest.Count];
		for (var index = 0; index < manifest.Count; index++)
		{
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

	private static long EstimateFileFactsBytes(FileFacts facts) =>
		EstimateFileFactsBytes(facts, new RetainedStringEstimator());

	private static long EstimateFileFactsBytes(FileFacts facts, RetainedStringEstimator strings) =>
		256 + strings.Add(facts.Path) + strings.Add(facts.ScopeId) + strings.Add(facts.ContentFingerprint) +
		strings.Add(facts.StatusReason) +
		facts.ErrorNodeKinds.Sum(pair => strings.Add(pair.Key) + 16) +
		facts.Declarations.Sum(declaration => 160 + strings.Add(declaration.Identity.ScopeId) +
			strings.Add(declaration.Identity.QualifiedName) + strings.Add(declaration.Identity.FileScope) +
			strings.Add(declaration.ContainingNamespace) +
			declaration.DeclarationSites.Sum(site => SiteBytes(site, strings))) +
		facts.Imports.Sum(import => 128 + strings.Add(import.Specifier) + strings.Add(import.ImportedName) +
			strings.Add(import.Alias) + SiteBytes(import.Site, strings)) +
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
		facts.TypeParameterScopes.Sum(scope => 40 + strings.Add(scope.Name));

	private static long EstimateResolvedIndexBytes(ResolvedIndex index)
	{
		var strings = new RetainedStringEstimator();
		return 256 + index.Edges.Sum(edge => 192 + strings.Add(edge.Source) + strings.Add(edge.Target) +
			strings.Add(edge.Reference) + edge.Reasons.Sum(strings.Add) +
			edge.Evidence.Sum(site => SiteBytes(site, strings)) +
			edge.Candidates.Sum(strings.Add) + edge.DeclarationFiles.Sum(strings.Add)) +
			index.Files.Sum(file => EstimateFileFactsBytes(file, strings));
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
		var files = index.Files.Select(file => file with
		{
			Imports = GateImports(file.Imports, allowed),
			References = GateReferences(file.References, allowed)
		}).ToArray();
		var edges = index.Edges.Where(edge => allowed.Contains(edge.Source) &&
			(edge.Target is null || allowed.Contains(edge.Target) || edge.Target.StartsWith("namespace:", StringComparison.Ordinal)) &&
			edge.Candidates.All(allowed.Contains) &&
			edge.DeclarationFiles.All(allowed.Contains)).ToArray();
		var (bySource, byTarget) = BuildEdgeIndexes(edges);
		return index with
		{
			Files = files,
			FileByPath = files.ToDictionary(static file => file.Path, StringComparer.Ordinal),
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

	private static string Hash(IEnumerable<string> values)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		Span<byte> lengthPrefix = stackalloc byte[sizeof(int)];
		foreach (var value in values)
		{
			var bytes = Encoding.UTF8.GetBytes(value);
			BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, bytes.Length);
			hash.AppendData(lengthPrefix);
			hash.AppendData(bytes);
		}

		return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
	}

	private static bool IsWithin(string root, string path)
	{
		var relative = Path.GetRelativePath(root, path);
		return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
		       !Path.IsPathRooted(relative);
	}

	private static string PortableRelative(string root, string path) => Normalize(Path.GetRelativePath(root, path));
	private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
	private static StringComparer PathComparer => OperatingSystem.IsWindows()
		? StringComparer.OrdinalIgnoreCase
		: StringComparer.Ordinal;

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
		{
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

	private readonly record struct PreparedDependencyIdentity(
		string RelativePath,
		string ContentFingerprint,
		LanguageId LanguageId);

	private readonly record struct IndexCacheKey(
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

	private sealed record ManifestSnapshotCacheEntry(
		ManifestRequestKey Key,
		IReadOnlyList<string> ManifestPaths,
		IReadOnlyList<FileStamp> Stamps,
		IReadOnlyList<string>? ContentIdentities,
		IndexCacheKey IndexCacheKey,
		DependencyIndexSnapshot Snapshot)
	{
		public LinkedListNode<ManifestSnapshotCacheEntry>? OrderNode { get; set; }
	}

	internal readonly record struct DependencyFactsCacheState(
		int ManifestSnapshots,
		int ManifestEvictionEntries,
		int ResolvedIndexes,
		long ResolvedIndexBytes);

	private sealed record ResolvedIndex(
		IReadOnlyList<DependencyEdge> Edges,
		IReadOnlyList<FileFacts> Files,
		IReadOnlyDictionary<string, FileFacts> FileByPath,
		IReadOnlyDictionary<string, IReadOnlyList<DependencyEdge>> EdgesBySource,
		IReadOnlyDictionary<string, IReadOnlyList<DependencyEdge>> EdgesByTarget);

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
			var importsByFile = new Dictionary<string, IReadOnlyList<ImportFact>>(StringComparer.Ordinal);
			var referencesByFile = new Dictionary<string, IReadOnlyList<ReferenceFact>>(StringComparer.Ordinal);
			var supportedFiles = files.Where(static file => file.Status == DependencyFileStatus.Supported).ToArray();
			var parallelism = Math.Clamp(Environment.ProcessorCount, 1, 8);
			var plans = CreateWorkPlans(supportedFiles, limits, cancellationToken);
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
						plan.File.Imports.Select(fact => Limit(fact, limitReason)).ToArray(),
						plan.File.References.Select(fact => Limit(fact, limitReason)).ToArray(),
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
				importsByFile[fileWork.File.Path] = fileWork.Imports;
				referencesByFile[fileWork.File.Path] = fileWork.References;
			}
			var resolvedFiles = files.Select(file => file.Status != DependencyFileStatus.Supported
				? file
				: file with
				{
					Imports = importsByFile.GetValueOrDefault(file.Path) ?? file.Imports,
					References = referencesByFile.GetValueOrDefault(file.Path) ?? file.References
				}).ToArray();
			return new ResolvedIndex(
				Aggregate(resolved),
				resolvedFiles,
				resolvedFiles.ToDictionary(static file => file.Path, StringComparer.Ordinal),
				new Dictionary<string, IReadOnlyList<DependencyEdge>>(StringComparer.Ordinal),
				new Dictionary<string, IReadOnlyList<DependencyEdge>>(StringComparer.Ordinal));
		}

		private static ResolutionWorkPlan[] CreateWorkPlans(
			IReadOnlyList<FileFacts> files,
			DependencyFactsLimits limits,
			CancellationToken cancellationToken)
		{
			var plans = new ResolutionWorkPlan[files.Count];
			long acceptedWork = 0;
			for (var index = 0; index < files.Count; index++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var file = files[index];
				var requestedWork = (long)file.Imports.Count + file.References.Count;
				string? limitReason = null;
				if (requestedWork > limits.MaximumEdgesPerFile)
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
			IReadOnlyList<ImportFact> Imports,
			IReadOnlyList<ReferenceFact> References,
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

		private static ImportFact Limit(ImportFact fact, string reason) => fact with
		{
			Status = ResolutionStatus.Unresolved,
			Reason = reason,
			Candidates = [],
			Target = null
		};

		private static ReferenceFact Limit(ReferenceFact fact, string reason) => fact with
		{
			Status = ResolutionStatus.Unresolved,
			Reason = reason,
			Candidates = [],
			Target = null
		};

		private static DependencyEdge LimitEdge(FileFacts file, string reason) => new(
			file.Path, null, EvidenceLayer.TypeReference, ResolutionStatus.Unresolved,
			"<limit>", [reason], [new SourceSite(file.Path, 1, reason)], [], false);

		private static IReadOnlyList<DependencyEdge> Aggregate(IEnumerable<DependencyEdge> raw) =>
			raw.GroupBy(static edge => new
				{
					edge.Source,
					edge.Target,
					edge.Layer,
					edge.Status,
					edge.Reference,
					edge.CrossScope
				})
				.Select(static group => new DependencyEdge(
					group.Key.Source,
					group.Key.Target,
					group.Key.Layer,
					group.Key.Status,
					group.Key.Reference,
					group.SelectMany(static edge => edge.Reasons).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
					group.SelectMany(static edge => edge.Evidence).Distinct().OrderBy(static site => site.Line).ToArray(),
					group.SelectMany(static edge => edge.Candidates).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
					group.Key.CrossScope)
				{
					DeclarationFiles = group.SelectMany(static edge => edge.DeclarationFiles)
						.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
				})
				.OrderBy(static edge => edge.Source, StringComparer.Ordinal)
				.ThenBy(static edge => edge.Target, StringComparer.Ordinal)
				.ThenBy(static edge => edge.Reference, StringComparer.Ordinal)
				.ToArray();
	}

	private sealed class ResolverContext
	{
		private readonly string _root;
		private readonly IReadOnlyDictionary<string, FileFacts> _files;
		private readonly IReadOnlyDictionary<SymbolLookupKey, DeclarationFact[]> _symbolsBySimpleName;
		private readonly IReadOnlyDictionary<QualifiedSymbolLookupKey, DeclarationFact[]> _symbolsByQualifiedName;
		private readonly DependencyResolverConfiguration _configuration;
		private readonly IReadOnlyDictionary<string, DependencyScopeDescriptor> _scopesById;
		private readonly IReadOnlyDictionary<string, string[]> _visibleScopesById;
		private readonly IReadOnlyDictionary<string, string[]> _globalNamespaces;
		private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _globalAliases;
		private readonly IReadOnlyDictionary<string, string[]> _contextNamespacesByFile;
		private readonly IReadOnlySet<string> _dotNetExternalSimpleNames;

		public ResolverContext(
			string root,
			IReadOnlyList<FileFacts> files,
			IReadOnlyList<DeclarationFact> declarations,
			DependencyResolverConfiguration configuration)
		{
			_root = root;
			_files = files.ToDictionary(static file => file.Path, StringComparer.Ordinal);
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
			_contextNamespacesByFile = files.ToDictionary(
				static file => file.Path,
				file => file.ContextNamespaces
					.Concat(_globalNamespaces.GetValueOrDefault(file.ScopeId) ?? [])
					.Distinct(StringComparer.Ordinal)
					.Order(StringComparer.Ordinal)
					.ToArray(),
				StringComparer.Ordinal);
			_dotNetExternalSimpleNames = configuration.DotNetExternalSymbols
				.Select(SimpleName)
				.ToHashSet(StringComparer.Ordinal);
		}

		public DependencyEdge ResolveImport(FileFacts source, ImportFact import) => source.LanguageId switch
		{
			LanguageId.TypeScript or LanguageId.JavaScript or LanguageId.Tsx => ResolveTypeScriptImport(source, import),
			LanguageId.Python => ResolvePythonImport(source, import),
			_ => Edge(source, import, ResolutionStatus.Unresolved, null,
				"explicit imports are context, not dependency edges, for this language", [])
		};

		private DependencyEdge ResolveTypeScriptImport(FileFacts source, ImportFact import)
		{
			if (!import.HasLiteralSpecifier)
				return Edge(source, import, ResolutionStatus.Unresolved, null,
					"module specifier is not a string literal", []);
			var scope = FindScope(source.ScopeId);
			if (scope is null || !scope.HasConfiguration)
				return Edge(source, import, ResolutionStatus.Unresolved, null,
					"no owning tsconfig.json or jsconfig.json in the manifest", []);
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
				if (Path.GetExtension(import.Specifier).Length == 0 && RequiresExplicitRelativeExtension(source, scope))
				{
					return Edge(source, import, ResolutionStatus.Unresolved, null,
						"extension required for a relative ESM import under node16/nodenext", []);
				}
				var directory = Path.GetDirectoryName(Path.Combine(_root, source.Path))!;
				candidates = ProbeTypeScript(Path.GetFullPath(Path.Combine(directory, import.Specifier)), scope, source);
			}
			else if (import.Specifier.StartsWith("#", StringComparison.Ordinal))
			{
				var packageTarget = ResolvePackageMap(source, import, import.Specifier, exports: false);
				if (packageTarget.FailureReason is { } reason)
					return Edge(source, import, ResolutionStatus.Unresolved, null, reason, []);
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
						$"one module target under {scope.ModuleResolution} in {scope.ScopeId}");
				var packageName = BarePackageName(import.Specifier);
				return FindNearestPackageMap(source)?.ExternalPackages.Contains(packageName) == true
					? Edge(source, import, ResolutionStatus.External, null,
						"declared Node package outside the manifest", [])
					: Edge(source, import, ResolutionStatus.Unresolved, null,
						"bare package has no target or external-package evidence", []);
			}
			return FinishImport(source, import, candidates,
				$"one module target under {scope.ModuleResolution} in {scope.ScopeId}");
		}

		private bool SupportsCommonJs(FileFacts source, DependencyScopeDescriptor scope)
		{
			var extension = Path.GetExtension(source.Path).ToLowerInvariant();
			if (extension is ".cjs" or ".cts") return true;
			if (extension is ".mjs" or ".mts") return false;
			if (extension is not (".ts" or ".tsx" or ".js" or ".jsx"))
				return scope.LegacyTypeScriptConfiguration;
			var moduleType = FindNearestPackageMap(source)?.ModuleType;
			if (string.Equals(moduleType, "commonjs", StringComparison.OrdinalIgnoreCase)) return true;
			if (string.Equals(moduleType, "module", StringComparison.OrdinalIgnoreCase)) return false;
			return true;
		}

		private static bool IsRequire(ImportFact import) =>
			import.Site.Evidence.TrimStart().StartsWith("require", StringComparison.Ordinal);

		private bool RequiresExplicitRelativeExtension(FileFacts source, DependencyScopeDescriptor scope)
		{
			var mode = scope.ModuleResolution ?? "bundler";
			return (mode.Equals("node16", StringComparison.OrdinalIgnoreCase) ||
			        mode.Equals("nodenext", StringComparison.OrdinalIgnoreCase)) &&
			       !SupportsCommonJs(source, scope);
		}

		private IEnumerable<string> ResolvePaths(
			DependencyScopeDescriptor? scope,
			FileFacts source,
			string specifier)
		{
			if (scope is null)
				return [];
			var mappings = scope.TypeScriptPaths
				.Select(pair => (pair.Key, pair.Value, Star: pair.Key.IndexOf('*')))
				.Where(item => Matches(item.Key, item.Star, specifier))
				.OrderBy(static item => item.Star >= 0)
				.ThenByDescending(static item => item.Star)
				.ThenBy(static item => item.Key, StringComparer.Ordinal)
				.ToArray();
			if (mappings.Length == 0)
				return [];
			var mapping = mappings[0];
			var wildcard = mapping.Star < 0 ? string.Empty :
				specifier[mapping.Star..(specifier.Length - (mapping.Key.Length - mapping.Star - 1))];
			foreach (var target in mapping.Value)
			{
				var resolved = ProbeTypeScript(
					Path.GetFullPath(Path.Combine(scope.Root, target.Replace("*", wildcard, StringComparison.Ordinal))),
					scope,
					source).FirstOrDefault();
				if (resolved is not null)
					return [resolved];
			}
			return [];
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
						return new PackageMapProbe([], $"package {(exports ? "exports" : "imports")} target is null-blocked");
					if (selected.Kind == PackageTargetSelectionKind.Unsupported)
						return new PackageMapProbe([], selected.Reason);
					if (selected.Kind != PackageTargetSelectionKind.Path || selected.Path is null)
						return new PackageMapProbe([], "no applicable package condition");
					var mappedPath = wildcard.Length == 0
						? selected.Path
						: selected.Path.Replace("*", wildcard, StringComparison.Ordinal);
					return new PackageMapProbe(
						ProbeTypeScript(
							Path.GetFullPath(Path.Combine(directory, mappedPath)),
							FindScope(source.ScopeId),
							source).ToArray(),
						null);
				}
				if (Path.GetFullPath(directory) == Path.GetFullPath(_root))
					break;
				directory = Path.GetDirectoryName(directory)!;
			}
			return new PackageMapProbe([], null);
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

		private string PackageCondition(FileFacts source, ImportFact import)
		{
			if (IsRequire(import))
				return "require";
			var scope = FindScope(source.ScopeId);
			var mode = scope?.ModuleResolution ?? "bundler";
			return scope is not null &&
			       (mode.Equals("node16", StringComparison.OrdinalIgnoreCase) ||
			        mode.Equals("nodenext", StringComparison.OrdinalIgnoreCase)) &&
			       SupportsCommonJs(source, scope)
				? "require"
				: "import";
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
			foreach (var pair in map.Where(static pair => pair.Key.Contains('*')).OrderByDescending(static pair => pair.Key.Length))
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
			string moduleCondition)
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
			foreach (var branch in target.Conditions)
			{
				if (!IsSupportedPackageCondition(branch.Name))
					return new PackageTargetSelection(
						PackageTargetSelectionKind.Unsupported,
						null,
						$"package condition '{branch.Name}' is not supported");
				if (!IsActivePackageCondition(branch.Name, moduleCondition))
					continue;
				var selected = SelectPackageTarget(branch.Target, moduleCondition);
				if (selected.Kind != PackageTargetSelectionKind.NoMatch)
					return selected;
			}
			return new PackageTargetSelection(PackageTargetSelectionKind.NoMatch, null, null);
		}

		private static bool IsSupportedPackageCondition(string condition) =>
			condition is "types" or "import" or "require" or "node" or "default";

		private static bool IsActivePackageCondition(string condition, string moduleCondition) =>
			condition is "types" or "node" or "default" || condition == moduleCondition;

		private IEnumerable<string> ProbeTypeScript(
			string candidate,
			DependencyScopeDescriptor? scope,
			FileFacts source)
		{
			var extension = Path.GetExtension(candidate).ToLowerInvariant();
			var probes = new List<string>();
			if (extension is ".js" or ".mjs" or ".cjs")
			{
				var stem = candidate[..^extension.Length];
				probes.AddRange(extension switch
				{
					".mjs" => [stem + ".mts", stem + ".d.mts", candidate],
					".cjs" => [stem + ".cts", stem + ".d.cts", candidate],
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
			if (SupportsDirectoryIndex(mode, source, scope))
			{
				probes.AddRange([
					Path.Combine(candidate, "index.ts"),
					Path.Combine(candidate, "index.tsx"),
					Path.Combine(candidate, "index.d.ts"),
					Path.Combine(candidate, "index.js"),
					Path.Combine(candidate, "index.jsx")]);
			}
			foreach (var probe in probes)
			{
				var relative = PortableRelative(_root, probe);
				if (_files.ContainsKey(relative))
					return [relative];
			}
			return [];
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
			var moduleEntityExists = candidates.Count > 0;
			if (import.ImportedName is { Length: > 0 } and not "*")
			{
				var provided = candidates
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
					return Edge(source, import, ResolutionStatus.Unresolved, null,
						"name not found in module", []);
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
			foreach (var import in facts.Imports.Where(import =>
				string.Equals(import.Alias ?? import.ImportedName ?? import.Specifier.Split('.').Last(), name, StringComparison.Ordinal)))
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
					var childTargets = ProbePythonModule(facts, child).ToArray();
					if (childTargets.Length > 0) return childTargets;
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
			if (module.Length == 0) return 0;
			var relative = module.Replace('.', '/').Trim('/') + '/';
			var portions = 0;
			foreach (var root in PythonRootPrefixes(source))
			{
				var prefix = string.Join('/', new[] { root, relative }.Where(static value => value.Length > 0));
				var init = prefix + "__init__.py";
				if (!_files.ContainsKey(init) && _files.Keys.Any(path => path.StartsWith(prefix, StringComparison.Ordinal)))
					portions++;
			}
			return portions;
		}

		private IEnumerable<string> ProbePythonModule(FileFacts source, string module)
		{
			var relative = module.Replace('.', '/');
			foreach (var root in PythonRootPrefixes(source))
			{
				var prefix = string.Join('/', new[] { root, relative }.Where(static value => value.Length > 0));
				var implementation = new[] { prefix + "/__init__.py", prefix + ".py" }
					.FirstOrDefault(_files.ContainsKey);
				if (implementation is not null)
				{
					yield return implementation;
					yield break;
				}
				var stub = new[] { prefix + "/__init__.pyi", prefix + ".pyi" }
					.FirstOrDefault(_files.ContainsKey);
				if (stub is not null)
				{
					yield return stub;
					yield break;
				}
			}
		}

		private IEnumerable<string> PythonRootPrefixes(FileFacts source)
		{
			var roots = FindScope(source.ScopeId)?.PythonRoots ?? [_root, Path.Combine(_root, "src")];
			return roots.Where(root => IsWithin(_root, root))
				.Select(root => PortableRelative(_root, root) is "." ? string.Empty : PortableRelative(_root, root).Trim('/'))
				.Distinct(StringComparer.Ordinal);
		}

		public DependencyEdge ResolveType(FileFacts source, ReferenceFact reference)
		{
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
						: "no owning tsconfig.json or jsconfig.json in the manifest", []);
			}
			if (scope is not null && ConfigurationFailure(scope) is { } configurationFailure)
				return Edge(source, reference, ResolutionStatus.Unresolved, null, configurationFailure, []);
			var isQualified = reference.IsGlobalQualified || reference.Name.Contains('.');
			var typeParameterShadowsReference = !isQualified && (source.TypeParameterScopes.Count > 0
				? source.TypeParameterScopes.Any(parameter =>
					parameter.Name == simpleName &&
					parameter.StartIndex <= reference.SourceStartIndex &&
					parameter.EndIndex >= reference.SourceStartIndex)
				: source.TypeParameters.Contains(simpleName, StringComparer.Ordinal));
			if (typeParameterShadowsReference)
				return Edge(source, reference, ResolutionStatus.Unresolved, null, "type parameter shadows declarations", []);
			var expandedName = reference.IsGlobalQualified
				? reference.Name
				: ExpandQualifiedAlias(source, reference.Name);
			var candidates = isQualified
				? LookupQualified(source, expandedName, reference.GenericArity)
				: LookupSimple(source, simpleName, reference.GenericArity);
			var attributeName = reference.SyntaxKind == "attribute"
				? reference.Name + "Attribute"
				: null;
			if (candidates.Length == 0 && attributeName is not null)
			{
				candidates = attributeName.Contains('.')
					? LookupQualified(source, ExpandQualifiedAlias(source, attributeName), reference.GenericArity)
					: LookupSimple(source, attributeName, reference.GenericArity);
			}
			if (source.LanguageId == LanguageId.CSharp)
			{
				var globalAliases = _globalAliases.GetValueOrDefault(source.ScopeId);
				if (source.Aliases.TryGetValue(simpleName, out var alias) ||
				    globalAliases?.TryGetValue(simpleName, out alias) == true)
					candidates = LookupQualified(source, alias, reference.GenericArity);
				else if (!reference.IsGlobalQualified && reference.Name.Contains('.') && candidates.Length == 0)
					candidates = LookupContextualCSharpQualified(source, reference, expandedName);
				else if (!isQualified)
					candidates = SelectVisibleCSharpCandidates(source, reference, candidates);
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
					$"one visible declaration identity in {source.ScopeId}", files) with
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

			var importedNamespaces = _contextNamespacesByFile.GetValueOrDefault(source.Path) ?? [];
			var imported = candidates.Where(candidate =>
				candidate.ContainingType is null &&
				importedNamespaces.Contains(candidate.ContainingNamespace, StringComparer.Ordinal)).ToArray();
			if (imported.Length > 0)
				return imported;

			return candidates.Where(static candidate =>
				candidate.ContainingType is null && candidate.ContainingNamespace.Length == 0).ToArray();
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

			return (_contextNamespacesByFile.GetValueOrDefault(source.Path) ?? [])
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
			return source.LanguageId == LanguageId.CSharp &&
			       FindScope(source.ScopeId)?.ProjectReferences.Contains(
			       declaration.Identity.ScopeId, StringComparer.Ordinal) == true;
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
					resolvedReason ?? $"one module target in {source.ScopeId}", candidates),
				_ => Edge(source, import, ResolutionStatus.Ambiguous, null, "multiple module targets", candidates)
			};
		}

		private DependencyEdge Edge(FileFacts source, ImportFact import, ResolutionStatus status, string? target, string reason, IReadOnlyList<string> candidates) =>
			CreateEdge(source, target, EvidenceLayer.ExplicitImport, status,
				import.IsWildcard ? import.Specifier + ".*" : import.Specifier,
				reason, import.Site, candidates);

		private DependencyEdge Edge(FileFacts source, ReferenceFact reference, ResolutionStatus status, string? target, string reason, IReadOnlyList<string> candidates) =>
			CreateEdge(source, target, reference.Layer, status, reference.Name, reason, reference.Site, candidates);

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
			_dotNetExternalSimpleNames.Contains(SimpleName(reference));
		private string ExpandQualifiedAlias(FileFacts source, string name)
		{
			var separator = name.IndexOf('.');
			if (separator <= 0) return name;
			var prefix = name[..separator];
			var globalAliases = _globalAliases.GetValueOrDefault(source.ScopeId);
			return source.Aliases.TryGetValue(prefix, out var target) ||
			       globalAliases?.TryGetValue(prefix, out target) == true
				? target + name[separator..]
				: name;
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

		private static string SimpleName(string qualified)
		{
			var value = qualified[(Math.Max(qualified.LastIndexOf('.'), qualified.LastIndexOf('#')) + 1)..];
			var arity = value.IndexOf('`');
			return arity < 0 ? value : value[..arity];
		}
		private string PythonModule(FileFacts source)
		{
			var root = PythonRootPrefixes(source)
				.Where(prefix => prefix.Length == 0 || source.Path.StartsWith(prefix + '/', StringComparison.Ordinal))
				.OrderByDescending(static prefix => prefix.Length)
				.FirstOrDefault();
			var relative = root is { Length: > 0 } ? source.Path[(root.Length + 1)..] : source.Path;
			var module = Path.ChangeExtension(relative, null)!.Replace('/', '.').Replace('\\', '.');
			return module.EndsWith(".__init__", StringComparison.Ordinal) ? module[..^".__init__".Length] : module;
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
				var pending = new Queue<string>();
				var visited = new HashSet<string>(StringComparer.Ordinal);
				pending.Enqueue(scope.ScopeId);
				while (pending.TryDequeue(out var scopeId))
				{
					if (!visited.Add(scopeId)) continue;
					if (scope.LanguageId != LanguageId.CSharp || !scopes.TryGetValue(scopeId, out var current)) continue;
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
		private readonly record struct PackageMapProbe(
			IReadOnlyList<string> Candidates,
			string? FailureReason);
		private readonly record struct PackageTargetSelection(
			PackageTargetSelectionKind Kind,
			string? Path,
			string? Reason);
		private enum PackageTargetSelectionKind
		{
			NoMatch,
			Path,
			Blocked,
			Unsupported
		}
	}
}
