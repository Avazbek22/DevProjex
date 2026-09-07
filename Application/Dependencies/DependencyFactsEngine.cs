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
	private readonly ConcurrentQueue<ManifestRequestKey> _manifestSnapshotOrder = [];
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

	public async Task<DependencyIndexSnapshot> IndexAsync(
		string sourceRoot,
		IReadOnlyList<string> manifestFiles,
		IProgress<DependencyIndexProgress>? progress = null,
		CancellationToken cancellationToken = default)
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
		var manifestRequestKey = new ManifestRequestKey(
			root,
			Hash(manifest.Select(path => PortableRelative(root, path))));
		var initialStamps = TryCaptureFileStamps(manifest);
		if (initialStamps is not null &&
		    _manifestSnapshots.TryGetValue(manifestRequestKey, out var cachedSnapshot) &&
		    cachedSnapshot.Stamps.SequenceEqual(initialStamps) &&
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
		var prepared = new PreparedDependencySource[manifest.Length];
		var parsedBefore = _extractor.ParseCount;
		var facts = new FileFacts[prepared.Length];
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
				var source = await _extractor
					.PrepareAsync(root, manifest[index], configuration, token)
					.ConfigureAwait(false);
				prepared[index] = source;
				if (source.PreparedStatus != DependencyFileStatus.Supported)
				{
					facts[index] = _extractor.Extract(source, _limits);
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
					if (ReferenceEquals(lazy, created))
						RegisterFileCacheWeight(key, lazy, EstimateFileFactsBytes(extracted));
					facts[index] = RebindScope(extracted, source.ScopeId);
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
		var createdIndex = new Lazy<Task<ResolvedIndex>>(
			() => Task.FromResult(GateResolvedIndex(
				DependencyResolver.Resolve(
					root,
					orderedFacts,
					declarations,
					configuration,
					_limits),
				allowed)),
			LazyThreadSafetyMode.ExecutionAndPublication);
		var cachedIndex = _indexCache.GetOrAdd(cacheKey, createdIndex);
		if (ReferenceEquals(cachedIndex, createdIndex))
			_indexCacheOrder.Enqueue(cacheKey);
		var resolved = await cachedIndex.Value.ConfigureAwait(false);
		if (ReferenceEquals(cachedIndex, createdIndex))
			RegisterIndexCacheWeight(cacheKey, cachedIndex, EstimateResolvedIndexBytes(resolved));
		var coverage = BuildCoverage(resolved.Files);
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
				ReferenceEquals(cachedIndex, createdIndex) ? orderedFacts.Length : 0,
				started.ElapsedMilliseconds,
				!ReferenceEquals(cachedIndex, createdIndex)));
		var finalStamps = TryCaptureFileStamps(manifest);
		if (initialStamps is not null && finalStamps is not null && initialStamps.SequenceEqual(finalStamps))
		{
			if (_indexCache.ContainsKey(cacheKey))
				StoreManifestSnapshot(manifestRequestKey, initialStamps, cacheKey, result);
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
		var fileByPath = index.Files.ToDictionary(static file => file.Path, StringComparer.Ordinal);
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
		IReadOnlyDictionary<string, FileFacts> files) =>
		sourceEdges.Where(edge => edge.Target != seed &&
		                    (edge.Target is not null || edge.Status == ResolutionStatus.Ambiguous))
			.GroupBy(edge => edge.Target ?? edge.Candidates.Order(StringComparer.Ordinal).FirstOrDefault() ??
				$"[{edge.Status.ToString().ToLowerInvariant()}] {edge.Reference}", StringComparer.Ordinal)
			.Select(group => ToRelated(group.Key, group, files))
			.OrderBy(static item => item.Path, StringComparer.Ordinal)
			.ToArray();

	private static IReadOnlyList<RelatedFile> ProjectDependents(
		string seed,
		IReadOnlyList<DependencyEdge> targetEdges,
		IReadOnlyDictionary<string, FileFacts> files) =>
		targetEdges.Where(edge => edge.Source != seed)
			.GroupBy(static edge => edge.Source, StringComparer.Ordinal)
			.Select(group => ToRelated(group.Key, group, files))
			.OrderBy(static item => item.Path, StringComparer.Ordinal)
			.ToArray();

	private static RelatedFile ToRelated(
		string path,
		IEnumerable<DependencyEdge> groupedEdges,
		IReadOnlyDictionary<string, FileFacts> files)
	{
		var edges = groupedEdges.ToArray();
		var status = edges.Select(static edge => edge.Status).OrderBy(static value => value).First();
		return new RelatedFile(
			path,
			status,
			edges.SelectMany(static edge => edge.Reasons.Concat(edge.Evidence.Select(site =>
				$"{EvidenceLabel(edge.Layer)} {edge.Reference} at line {site.Line}")))
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
			.SelectMany(static edge => (edge.Target is null ? edge.Candidates : edge.Candidates.Append(edge.Target))
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
					.ToArray()))
			.OrderBy(static declaration => declaration.Identity.ScopeId, StringComparer.Ordinal)
			.ThenBy(static declaration => declaration.Identity.QualifiedName, StringComparer.Ordinal)
			.ThenBy(static declaration => declaration.Identity.GenericArity)
			.ToArray();

	private static string DeclarationKey(DeclarationFact declaration) =>
		$"{declaration.Identity.ScopeId}\0{declaration.Identity.LanguageId}\0" +
		$"{declaration.Identity.SymbolKind}\0{declaration.Identity.QualifiedName}\0" +
		$"{declaration.Identity.GenericArity}\0{declaration.Identity.FileScope}\0" +
		string.Join('\0', declaration.DeclarationSites.Select(static site => $"{site.File}:{site.Line}"));

	private static DependencyFactsCoverage BuildCoverage(IReadOnlyList<FileFacts> files) =>
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
				.ToDictionary(static group => group.Key, static group => group.Sum(static pair => pair.Value), StringComparer.Ordinal));

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
				_manifestSnapshots.TryRemove(snapshot.Key, out _);
			if (_indexCacheWeights.TryRemove(oldest, out var weight))
				_indexCacheBytes -= weight;
		}
	}

	private void StoreManifestSnapshot(
		ManifestRequestKey key,
		IReadOnlyList<FileStamp> stamps,
		IndexCacheKey indexCacheKey,
		DependencyIndexSnapshot snapshot)
	{
		lock (_cacheTrimSync)
		{
			if (!_indexCache.ContainsKey(indexCacheKey)) return;
			var entry = new ManifestSnapshotCacheEntry(stamps, indexCacheKey, snapshot);
			if (_manifestSnapshots.TryAdd(key, entry))
				_manifestSnapshotOrder.Enqueue(key);
			else
				_manifestSnapshots[key] = entry;
			while (_manifestSnapshots.Count > _limits.MaximumCachedIndexes &&
			       _manifestSnapshotOrder.TryDequeue(out var oldest))
				_manifestSnapshots.TryRemove(oldest, out _);
		}
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

	private static long EstimateFileFactsBytes(FileFacts facts) =>
		256 + StringBytes(facts.Path) + StringBytes(facts.ScopeId) + StringBytes(facts.ContentFingerprint) +
		StringBytes(facts.StatusReason) +
		facts.ErrorNodeKinds.Sum(static pair => StringBytes(pair.Key) + 16) +
		facts.Declarations.Sum(static declaration => 160 + StringBytes(declaration.Identity.ScopeId) +
			StringBytes(declaration.Identity.QualifiedName) + StringBytes(declaration.Identity.FileScope) +
			declaration.DeclarationSites.Sum(SiteBytes)) +
		facts.Imports.Sum(static import => 128 + StringBytes(import.Specifier) + StringBytes(import.ImportedName) +
			StringBytes(import.Alias) + SiteBytes(import.Site)) +
		facts.References.Sum(static reference => 160 + StringBytes(reference.Name) + StringBytes(reference.SyntaxKind) +
			StringBytes(reference.Reason) + StringBytes(reference.Target) + (reference.Candidates?.Sum(StringBytes) ?? 0) +
			SiteBytes(reference.Site)) +
		facts.ContextNamespaces.Sum(StringBytes) + facts.Aliases.Sum(static pair => StringBytes(pair.Key) + StringBytes(pair.Value)) +
		facts.GlobalContextNamespaces.Sum(StringBytes) + facts.GlobalAliases.Sum(static pair => StringBytes(pair.Key) + StringBytes(pair.Value)) +
		facts.TypeParameters.Sum(StringBytes);

	private static long EstimateResolvedIndexBytes(ResolvedIndex index) =>
		256 + index.Edges.Sum(static edge => 192 + StringBytes(edge.Source) + StringBytes(edge.Target) +
			StringBytes(edge.Reference) + edge.Reasons.Sum(StringBytes) + edge.Evidence.Sum(SiteBytes) +
			edge.Candidates.Sum(StringBytes)) +
		index.Files.Sum(static file => StringBytes(file.Path) + file.Imports.Sum(static fact =>
			128 + StringBytes(fact.Specifier) + StringBytes(fact.ImportedName) + StringBytes(fact.Alias) +
			StringBytes(fact.Reason) + StringBytes(fact.Target) + (fact.Candidates?.Sum(StringBytes) ?? 0))) +
		index.Files.Sum(static file => file.References.Sum(static fact =>
			128 + StringBytes(fact.Name) + StringBytes(fact.Reason) + StringBytes(fact.Target) +
			(fact.Candidates?.Sum(StringBytes) ?? 0)));

	private static long SiteBytes(SourceSite site) =>
		64 + StringBytes(site.File) + StringBytes(site.Evidence);

	private static long StringBytes(string? value) => value is null ? 0 : 24 + value.Length * 2L;

	private static ResolvedIndex GateResolvedIndex(ResolvedIndex index, IReadOnlySet<string> allowed)
	{
		var files = index.Files.Select(file => file with
		{
			Imports = GateImports(file.Imports, allowed),
			References = GateReferences(file.References, allowed)
		}).ToArray();
		var edges = index.Edges.Where(edge => allowed.Contains(edge.Source) &&
			(edge.Target is null || allowed.Contains(edge.Target) || edge.Target.StartsWith("namespace:", StringComparison.Ordinal)) &&
			edge.Candidates.All(allowed.Contains)).ToArray();
		var (bySource, byTarget) = BuildEdgeIndexes(edges);
		return index with { Files = files, Edges = edges, EdgesBySource = bySource, EdgesByTarget = byTarget };
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

	private static string Hash(IEnumerable<string> values) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values))))
			.ToLowerInvariant();

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
			_manifestSnapshots.Clear();
			_extractor.Dispose();
		}
	}

	private readonly record struct FileCacheKey(
		string Path,
		string RelativePath,
		string Fingerprint,
		LanguageId LanguageId,
		string ExtractorIdentity);

	private readonly record struct IndexCacheKey(
		string ManifestGeneration,
		string DeclarationRevision,
		string ConfigurationFingerprint);

	private readonly record struct ManifestRequestKey(
		string SourceRoot,
		string ManifestPathsFingerprint);

	private readonly record struct FileStamp(
		long Length,
		long LastWriteTimeUtcTicks,
		long CreationTimeUtcTicks);

	private sealed record ManifestSnapshotCacheEntry(
		IReadOnlyList<FileStamp> Stamps,
		IndexCacheKey IndexCacheKey,
		DependencyIndexSnapshot Snapshot);

	private sealed record ResolvedIndex(
		IReadOnlyList<DependencyEdge> Edges,
		IReadOnlyList<FileFacts> Files,
		IReadOnlyDictionary<string, IReadOnlyList<DependencyEdge>> EdgesBySource,
		IReadOnlyDictionary<string, IReadOnlyList<DependencyEdge>> EdgesByTarget);

	private static class DependencyResolver
	{
		public static ResolvedIndex Resolve(
			string root,
			IReadOnlyList<FileFacts> files,
			IReadOnlyList<DeclarationFact> declarations,
			DependencyResolverConfiguration configuration,
			DependencyFactsLimits limits)
		{
			var context = new ResolverContext(root, files, declarations, configuration);
			var resolved = new List<DependencyEdge>();
			var importsByFile = new Dictionary<string, IReadOnlyList<ImportFact>>(StringComparer.Ordinal);
			var referencesByFile = new Dictionary<string, IReadOnlyList<ReferenceFact>>(StringComparer.Ordinal);
			var supportedFiles = files.Where(static file => file.Status == DependencyFileStatus.Supported).ToArray();
			var parallelism = Math.Clamp(Environment.ProcessorCount, 1, 8);
			var work = 0;
			for (var offset = 0; offset < supportedFiles.Length; offset += parallelism)
			{
				var count = Math.Min(parallelism, supportedFiles.Length - offset);
				var batch = new ResolvedFileWork[count];
				Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, index =>
				{
					var file = supportedFiles[offset + index];
					var importPairs = file.Imports
						.Select(import => (Fact: import, Edge: context.ResolveImport(file, import))).ToArray();
					var referencePairs = file.References
						.Select(reference => (Fact: reference, Edge: context.ResolveType(file, reference))).ToArray();
					batch[index] = new ResolvedFileWork(
						file,
						importPairs,
						referencePairs,
						importPairs.Select(static pair => pair.Edge)
							.Concat(referencePairs.Select(static pair => pair.Edge)).ToArray());
				});
				foreach (var fileWork in batch)
				{
					var file = fileWork.File;
					if (fileWork.Edges.Length > limits.MaximumEdgesPerFile)
					{
						resolved.Add(LimitEdge(file, "edge limit exceeded"));
						importsByFile[file.Path] = file.Imports.Select(static fact => Limit(fact, "edge limit exceeded")).ToArray();
						referencesByFile[file.Path] = file.References.Select(static fact => Limit(fact, "edge limit exceeded")).ToArray();
						continue;
					}
					work += fileWork.Edges.Length;
					if (work > limits.MaximumWorkPerIndex)
					{
						resolved.Add(LimitEdge(file, "index work limit exceeded"));
						importsByFile[file.Path] = file.Imports.Select(static fact => Limit(fact, "index work limit exceeded")).ToArray();
						referencesByFile[file.Path] = file.References.Select(static fact => Limit(fact, "index work limit exceeded")).ToArray();
						continue;
					}
					resolved.AddRange(fileWork.Edges);
					importsByFile[file.Path] = fileWork.Imports.Select(static pair => Resolve(pair.Fact, pair.Edge)).ToArray();
					referencesByFile[file.Path] = fileWork.References.Select(static pair => Resolve(pair.Fact, pair.Edge)).ToArray();
				}
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
				new Dictionary<string, IReadOnlyList<DependencyEdge>>(StringComparer.Ordinal),
				new Dictionary<string, IReadOnlyList<DependencyEdge>>(StringComparer.Ordinal));
		}

		private sealed record ResolvedFileWork(
			FileFacts File,
			IReadOnlyList<(ImportFact Fact, DependencyEdge Edge)> Imports,
			IReadOnlyList<(ReferenceFact Fact, DependencyEdge Edge)> References,
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
					group.Key.CrossScope))
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
			var scope = FindScope(source.ScopeId);
			if (scope is null || !scope.HasConfiguration)
				return Edge(source, import, ResolutionStatus.Unresolved, null,
					"no owning tsconfig.json or jsconfig.json in the manifest", []);
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
				var directory = Path.GetDirectoryName(Path.Combine(_root, source.Path))!;
				candidates = ProbeTypeScript(Path.GetFullPath(Path.Combine(directory, import.Specifier)), scope);
			}
			else if (import.Specifier.StartsWith("#", StringComparison.Ordinal))
			{
				if (IsPackageMapBlocked(source, import.Specifier, exports: false))
					return Edge(source, import, ResolutionStatus.Unresolved, null, "package imports target is null-blocked", []);
				candidates = ResolvePackageMap(source, import.Specifier, exports: false);
			}
			else if ((scope.PackageName ?? FindNearestPackageMap(source)?.PackageName) is { } package &&
			         (import.Specifier == package || import.Specifier.StartsWith(package + '/', StringComparison.Ordinal)))
			{
				var selfPath = import.Specifier[package.Length..].TrimStart('/');
				if (IsPackageMapBlocked(source, selfPath, exports: true))
					return Edge(source, import, ResolutionStatus.Unresolved, null, "package exports target is null-blocked", []);
				candidates = ResolvePackageMap(source, selfPath, exports: true);
			}
			else
			{
				var mapped = ResolvePaths(scope, import.Specifier).ToArray();
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
			var moduleType = FindNearestPackageMap(source)?.ModuleType;
			if (string.Equals(moduleType, "commonjs", StringComparison.OrdinalIgnoreCase)) return true;
			if (string.Equals(moduleType, "module", StringComparison.OrdinalIgnoreCase)) return false;
			return extension == ".js" || scope.LegacyTypeScriptConfiguration;
		}

		private static bool IsRequire(ImportFact import) =>
			import.Site.Evidence.TrimStart().StartsWith("require", StringComparison.Ordinal);

		private IEnumerable<string> ResolvePaths(DependencyScopeDescriptor? scope, string specifier)
		{
			if (scope is null)
				return [];
			foreach (var mapping in scope.TypeScriptPaths
				         .Select(pair => (pair.Key, pair.Value, Star: pair.Key.IndexOf('*')))
				         .Where(item => Matches(item.Key, item.Star, specifier))
				         .OrderBy(static item => item.Star >= 0)
				         .ThenByDescending(static item => item.Key.Length))
			{
				var wildcard = mapping.Star < 0 ? string.Empty :
					specifier[mapping.Star..(specifier.Length - (mapping.Key.Length - mapping.Star - 1))];
				var targets = mapping.Value.SelectMany(target => ProbeTypeScript(
					Path.GetFullPath(Path.Combine(scope.Root, target.Replace("*", wildcard, StringComparison.Ordinal))),
					scope)).ToArray();
				if (targets.Length > 0)
					return targets;
			}
			return [];
		}

		private static bool Matches(string pattern, int star, string value) => star < 0
			? pattern == value
			: value.StartsWith(pattern[..star], StringComparison.Ordinal) &&
			  value.EndsWith(pattern[(star + 1)..], StringComparison.Ordinal);

		private static string BarePackageName(string specifier)
		{
			var parts = specifier.Split('/', StringSplitOptions.RemoveEmptyEntries);
			return parts.Length > 1 && parts[0].StartsWith('@')
				? parts[0] + "/" + parts[1]
				: parts.FirstOrDefault() ?? specifier;
		}

		private IEnumerable<string> ResolvePackageMap(FileFacts source, string specifier, bool exports)
		{
			var directory = Path.GetDirectoryName(Path.Combine(_root, source.Path))!;
			while (IsWithin(_root, directory))
			{
				var relative = PortableRelative(_root, directory);
				if (_configuration.PackageMaps.TryGetValue(relative, out var map))
				{
					var values = exports ? map.Exports : map.Imports;
					var key = exports ? (specifier.Length == 0 ? "." : "./" + specifier) : specifier;
					if (TryMap(values, key, out var target) && target is not null)
						return ProbeTypeScript(Path.GetFullPath(Path.Combine(directory, target)), FindScope(source.ScopeId));
					return [];
				}
				if (Path.GetFullPath(directory) == Path.GetFullPath(_root))
					break;
				directory = Path.GetDirectoryName(directory)!;
			}
			return [];
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

		private bool IsPackageMapBlocked(FileFacts source, string specifier, bool exports)
		{
			var directory = Path.GetDirectoryName(Path.Combine(_root, source.Path))!;
			while (IsWithin(_root, directory))
			{
				if (_configuration.PackageMaps.TryGetValue(PortableRelative(_root, directory), out var map))
				{
					var values = exports ? map.Exports : map.Imports;
					var key = exports ? (specifier.Length == 0 ? "." : "./" + specifier) : specifier;
					return TryMap(values, key, out var target) && target is null;
				}
				if (Path.GetFullPath(directory) == Path.GetFullPath(_root)) break;
				directory = Path.GetDirectoryName(directory)!;
			}
			return false;
		}

		private static bool TryMap(IReadOnlyDictionary<string, string?> map, string key, out string? target)
		{
			if (map.TryGetValue(key, out target))
				return true;
			foreach (var pair in map.Where(static pair => pair.Key.Contains('*')).OrderByDescending(static pair => pair.Key.Length))
			{
				var star = pair.Key.IndexOf('*');
				var prefix = pair.Key[..star];
				var suffix = pair.Key[(star + 1)..];
				if (!key.StartsWith(prefix, StringComparison.Ordinal) || !key.EndsWith(suffix, StringComparison.Ordinal))
					continue;
				var wildcard = key[prefix.Length..(key.Length - suffix.Length)];
				target = pair.Value?.Replace("*", wildcard, StringComparison.Ordinal);
				return true;
			}
			target = null;
			return false;
		}

		private IEnumerable<string> ProbeTypeScript(string candidate, DependencyScopeDescriptor? scope)
		{
			var extension = Path.GetExtension(candidate);
			var probes = new List<string>();
			if (extension is ".js" or ".mjs" or ".cjs")
			{
				var stem = candidate[..^extension.Length];
				probes.AddRange(extension switch
				{
					".mjs" => [stem + ".mts", stem + ".d.mts"],
					".cjs" => [stem + ".cts", stem + ".d.cts"],
					_ => [stem + ".ts", stem + ".tsx", stem + ".d.ts"]
				});
			}
			else if (extension.Length > 0)
				probes.Add(candidate);
			else
				probes.AddRange([candidate + ".ts", candidate + ".tsx", candidate + ".d.ts", candidate + ".js"]);
			var mode = scope?.ModuleResolution ?? "bundler";
			if (mode.Equals("node", StringComparison.OrdinalIgnoreCase) || mode.Equals("node16", StringComparison.OrdinalIgnoreCase))
				probes.AddRange([Path.Combine(candidate, "index.ts"), Path.Combine(candidate, "index.tsx"), Path.Combine(candidate, "index.d.ts")]);
			return probes.Select(path => PortableRelative(_root, path)).Where(_files.ContainsKey).Distinct(StringComparer.Ordinal);
		}

		private DependencyEdge ResolvePythonImport(FileFacts source, ImportFact import)
		{
			var sourceModule = PythonModule(source);
			var sourcePackage = Path.GetFileNameWithoutExtension(source.Path) == "__init__"
				? sourceModule
				: sourceModule.Contains('.') ? sourceModule[..sourceModule.LastIndexOf('.')] : string.Empty;
			var parts = sourcePackage.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
			if (import.RelativeLevel > 0)
			{
				var remove = import.RelativeLevel - 1;
				if (remove > parts.Count)
					return Edge(source, import, ResolutionStatus.Unresolved, null, "relative import escapes package", []);
				parts.RemoveRange(parts.Count - remove, remove);
			}
			var module = import.RelativeLevel == 0
				? import.Specifier
				: string.Join('.', parts.Concat(import.Specifier.Split('.', StringSplitOptions.RemoveEmptyEntries)));
			var candidates = ProbePythonModule(source, module).ToList();
			if (import.ImportedName is { Length: > 0 } and not "*")
			{
				var child = module.Length == 0 ? import.ImportedName : module + "." + import.ImportedName;
				var children = ProbePythonModule(source, child).ToArray();
				if (children.Length > 0)
					candidates = children.ToList();
				else
					candidates = candidates.Where(candidate =>
						!Path.GetFileName(candidate).StartsWith("__init__.", StringComparison.Ordinal) ||
						PythonStaticallyProvides(candidate, import.ImportedName, 0, new HashSet<string>(StringComparer.Ordinal))).ToList();
			}
			if (candidates.Count == 0)
			{
				var portions = ProbePythonNamespace(source, module).ToArray();
				if (portions.Length > 0)
					return Edge(source, import, ResolutionStatus.Resolved, "namespace:" + module,
						$"one namespace-package entity with {portions.Length} portion(s)", portions);
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

		private bool PythonStaticallyProvides(string candidate, string name, int depth, ISet<string> visited)
		{
			if (depth >= 8 || !visited.Add(candidate) || !_files.TryGetValue(candidate, out var facts)) return false;
			if (facts.Declarations.Any(declaration => SimpleName(declaration.Identity.QualifiedName) == name)) return true;
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
					if (remove > parts.Count) continue;
					parts.RemoveRange(parts.Count - remove, remove);
				}
				var module = import.RelativeLevel == 0 ? import.Specifier :
					string.Join('.', parts.Concat(import.Specifier.Split('.', StringSplitOptions.RemoveEmptyEntries)));
				if (import.ImportedName is { Length: > 0 } imported)
				{
					var child = module.Length == 0 ? imported : module + "." + imported;
					if (ProbePythonModule(facts, child).Any()) return true;
					if (ProbePythonModule(facts, module).Any(next => PythonStaticallyProvides(next, imported, depth + 1, visited))) return true;
				}
				else if (ProbePythonModule(facts, module).Any()) return true;
			}
			return false;
		}

		private IEnumerable<string> ProbePythonNamespace(FileFacts source, string module)
		{
			if (module.Length == 0) return [];
			var relative = module.Replace('.', '/').Trim('/') + '/';
			var portions = new List<string>();
			foreach (var root in PythonRootPrefixes(source))
			{
				var prefix = string.Join('/', new[] { root, relative }.Where(static value => value.Length > 0));
				var init = prefix + "__init__.py";
				if (!_files.ContainsKey(init))
					portions.AddRange(_files.Keys.Where(path => path.StartsWith(prefix, StringComparison.Ordinal)).Take(1));
			}
			return portions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
		}

		private IEnumerable<string> ProbePythonModule(FileFacts source, string module)
		{
			var relative = module.Replace('.', '/');
			foreach (var root in PythonRootPrefixes(source))
			{
				var prefix = string.Join('/', new[] { root, relative }.Where(static value => value.Length > 0));
				var implementation = new[] { prefix + ".py", prefix + "/__init__.py" }
					.FirstOrDefault(_files.ContainsKey);
				if (implementation is not null)
				{
					yield return implementation;
					yield break;
				}
				var stub = new[] { prefix + ".pyi", prefix + "/__init__.pyi" }
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
			if (source.TypeParameters.Contains(simpleName, StringComparer.Ordinal))
				return Edge(source, reference, ResolutionStatus.Unresolved, null, "type parameter shadows declarations", []);
			var candidates = reference.Name.Contains('.')
				? LookupQualified(source, ExpandQualifiedAlias(source, reference.Name), reference.GenericArity)
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
				else
				{
					var namespaces = _contextNamespacesByFile.GetValueOrDefault(source.Path) ?? [];
					var contextual = candidates.Where(symbol => namespaces.Any(ns =>
						symbol.Identity.QualifiedName.StartsWith(ns + '.', StringComparison.Ordinal))).ToArray();
					if (contextual.Length > 0)
						candidates = contextual;
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
					$"one visible declaration identity in {source.ScopeId}", files)
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
	}
}
