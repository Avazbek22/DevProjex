namespace DevProjex.Application.Dependencies;

/// <summary>One import statement of a file and the project file it resolves to.</summary>
/// <param name="Line">The 1-based line the import statement starts on.</param>
/// <param name="Reference">The module reference as the statement writes it, such as <c>../../helper/conninfo</c> or <c>._config</c>.</param>
/// <param name="Targets">The project files the import resolves to; one for a resolved import, several for an ambiguous one.</param>
public sealed record DependencyFileImport(
	int Line,
	string Reference,
	ResolutionStatus Status,
	IReadOnlyList<string> Targets);

public sealed partial class DependencyFactsEngine
{
	private const int MaximumFileImportRounds = 4;
	private const int MaximumFileImportFactFiles = 64;

	/// <summary>
	/// The imports of <paramref name="sourceFile"/> and where each resolves, without indexing the
	/// project. Module targets are found from the manifest paths and the project configuration;
	/// facts are extracted only for the source and for the modules its imports reach, following
	/// Python re-exports a few levels, so the answer matches the full index for the same manifest.
	/// A cached index for the manifest is used as is. Returns null for a language whose imports
	/// resolve through project-wide declarations rather than module paths, and for a source that
	/// is not in the manifest.
	/// </summary>
	public async Task<IReadOnlyList<DependencyFileImport>?> ResolveFileImportsAsync(
		string sourceRoot,
		IReadOnlyList<string> manifestFiles,
		string sourceFile,
		IReadOnlyCollection<string>? readableProjectFiles = null,
		CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
		ArgumentNullException.ThrowIfNull(manifestFiles);
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
		var root = Path.GetFullPath(sourceRoot);
		var canonicalManifest = CreateCanonicalManifest(root, manifestFiles, cancellationToken);
		var sourceFullPath = Path.GetFullPath(sourceFile, root);
		var sourceEntry = canonicalManifest.FirstOrDefault(file => PathComparer.Equals(file.FullPath, sourceFullPath));
		if (sourceEntry.FullPath is null)
			return null;

		var cached = TryGetCachedIndex(root, manifestFiles, readableProjectFiles, cancellationToken);
		if (cached is not null)
		{
			if (!cached.FileByPath.TryGetValue(sourceEntry.RelativePath, out var cachedSource) ||
				!SupportsFileImports(cachedSource.LanguageId))
				return null;
			var cachedEdges = cached.EdgesBySource.GetValueOrDefault(sourceEntry.RelativePath) ?? [];
			return ToFileImports(cachedSource, cachedEdges.Where(static edge => edge.Layer == EvidenceLayer.ExplicitImport));
		}

		var manifest = canonicalManifest.Select(static file => file.FullPath).ToArray();
		var configuration = await _configurationProvider
			.ReadAsync(
				root,
				manifest,
				readableProjectFiles?.Select(path => Path.GetFullPath(path, root)).ToHashSet(PathComparer),
				cancellationToken)
			.ConfigureAwait(false);
		var stubs = new FileFacts[canonicalManifest.Length];
		var indexByPath = new Dictionary<string, int>(StringComparer.Ordinal);
		try
		{
			for (var index = 0; index < canonicalManifest.Length; index++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var (language, scopeId) = _extractor.DescribePath(canonicalManifest[index].FullPath, configuration);
				stubs[index] = PathOnlyFacts(canonicalManifest[index].RelativePath, scopeId, language);
				indexByPath[canonicalManifest[index].RelativePath] = index;
			}
		}
		catch (NotSupportedException)
		{
			return null;
		}
		if (!SupportsFileImports(stubs[indexByPath[sourceEntry.RelativePath]].LanguageId))
			return null;

		var facts = (FileFacts[])stubs.Clone();
		var extracted = new HashSet<string>(StringComparer.Ordinal);
		var pending = new HashSet<string>(StringComparer.Ordinal) { sourceEntry.RelativePath };
		// TypeScript and JavaScript resolve modules from paths and configuration alone; Python needs
		// the facts of the modules it imports to follow declarations and re-exports.
		var rounds = stubs[indexByPath[sourceEntry.RelativePath]].LanguageId == LanguageId.Python
			? MaximumFileImportRounds
			: 1;
		for (var round = 0; round < rounds && pending.Count > 0; round++)
		{
			foreach (var path in pending.Order(StringComparer.Ordinal))
			{
				if (extracted.Count >= MaximumFileImportFactFiles)
					break;
				var index = indexByPath[path];
				facts[index] = await ExtractForFileImportsAsync(
					root, canonicalManifest[index].FullPath, configuration, cancellationToken).ConfigureAwait(false);
				extracted.Add(path);
			}
			// Each round makes the modules the extracted files import real, so the next resolution
			// can follow their declarations and re-exports.
			var context = new ResolverContext(root, facts, MergeDeclarations(facts), configuration);
			pending = extracted
				.Select(path => facts[indexByPath[path]])
				.Where(static file => file.Status == DependencyFileStatus.Supported)
				.SelectMany(file => file.Imports.SelectMany(import => context.ImportModuleFiles(file, import)))
				.Where(path => indexByPath.TryGetValue(path, out var target) &&
							   facts[target].LanguageId == LanguageId.Python && !extracted.Contains(path))
				.ToHashSet(StringComparer.Ordinal);
		}

		var source = facts[indexByPath[sourceEntry.RelativePath]];
		if (source.Status != DependencyFileStatus.Supported)
			return [];
		var resolver = new ResolverContext(root, facts, MergeDeclarations(facts), configuration);
		var allowed = facts.Select(static file => file.Path).ToHashSet(StringComparer.Ordinal);
		var edges = source.Imports
			.Select(import => resolver.ResolveImport(source, import))
			.Where(edge => edge.Target is null || allowed.Contains(edge.Target) ||
						   edge.Target.StartsWith("namespace:", StringComparison.Ordinal))
			.ToArray();
		return ToFileImports(source, edges);
	}

	private static bool SupportsFileImports(LanguageId language) =>
		language is LanguageId.Python or LanguageId.TypeScript or LanguageId.Tsx or LanguageId.JavaScript;

	private static FileFacts PathOnlyFacts(string relativePath, string scopeId, LanguageId language) => new(
		relativePath,
		scopeId,
		language,
		"path-only",
		0,
		language == LanguageId.Unsupported ? DependencyFileStatus.Unsupported : DependencyFileStatus.Supported,
		null,
		false,
		new Dictionary<string, int>(),
		[],
		[],
		[],
		[],
		new Dictionary<string, string>(),
		[],
		new Dictionary<string, string>(),
		[])
	{
		CanCache = false
	};

	private async Task<FileFacts> ExtractForFileImportsAsync(
		string root,
		string fullPath,
		DependencyResolverConfiguration configuration,
		CancellationToken cancellationToken)
	{
		var source = await _extractor
			.PrepareAsync(root, fullPath, configuration, _limits, cancellationToken)
			.ConfigureAwait(false);
		if (source.PreparedStatus != DependencyFileStatus.Supported)
			return RebindScope(_extractor.Extract(source, _limits, cancellationToken), source.ScopeId);
		// The facts enter the same content-keyed cache the index uses, so repeated reads and a later
		// index reuse them instead of parsing again.
		var key = CreateFileCacheKey(source);
		if (!_fileCache.TryGetValue(key, out var entry))
		{
			var created = new FileCacheEntry(key, new Lazy<Task<FileFacts>>(
				() => Task.Run(() => _extractor.Extract(source, _limits, cancellationToken), cancellationToken),
				LazyThreadSafetyMode.ExecutionAndPublication));
			entry = GetOrAddFileCacheEntry(created);
		}
		FileFacts extracted;
		try
		{
			extracted = await entry.Value.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			RemoveFileCacheEntry(key, entry);
			throw;
		}
		if (!extracted.CanCache)
			RemoveFileCacheEntry(key, entry);
		else
			RegisterFileCacheWeight(key, entry, EstimateFileFactsBytes(extracted));
		return RebindScope(extracted, source.ScopeId);
	}

	private static IReadOnlyList<DependencyFileImport> ToFileImports(FileFacts source, IEnumerable<DependencyEdge> edges)
	{
		var lineByImport = source.Imports
			.GroupBy(static import => import.Site.Line)
			.ToDictionary(static group => group.Key, static group => group.First());
		return edges
			.Where(static edge => edge.Layer == EvidenceLayer.ExplicitImport && edge.Evidence.Count > 0)
			.Select(edge =>
			{
				var targets = edge.Status switch
				{
					ResolutionStatus.Resolved when edge.Target is not null => [edge.Target],
					ResolutionStatus.Ambiguous => edge.Candidates,
					_ => (IReadOnlyList<string>)[]
				};
				return new DependencyFileImport(edge.Evidence[0].Line, edge.Reference, edge.Status, targets);
			})
			.Where(item => lineByImport.ContainsKey(item.Line))
			.GroupBy(static item => (item.Line, item.Reference))
			.Select(static group =>
			{
				var first = group.First();
				var targets = group.SelectMany(static item => item.Targets).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
				var status = group.Any(static item => item.Status == ResolutionStatus.Resolved)
					? ResolutionStatus.Resolved
					: first.Status;
				return first with { Status = status, Targets = targets };
			})
			.OrderBy(static item => item.Line)
			.ThenBy(static item => item.Reference, StringComparer.Ordinal)
			.ToArray();
	}

	private sealed partial class ResolverContext
	{
		/// <summary>
		/// The project files an import names as modules, from paths and configuration alone: the
		/// module of a Python import and, for "from package import name", the submodule of that
		/// name; for TypeScript and JavaScript the files the import resolves to. Declaration-level
		/// binding is left to <see cref="ResolveImport"/>.
		/// </summary>
		public IReadOnlyList<string> ImportModuleFiles(FileFacts source, ImportFact import)
		{
			if (source.LanguageId != LanguageId.Python)
			{
				var edge = ResolveImport(source, import);
				return edge.Target is { } target ? [target, .. edge.Candidates] : edge.Candidates;
			}
			// Mirrors the module computation at the start of ResolvePythonImport.
			var sourceModule = PythonModule(source);
			var sourcePackage = Path.GetFileNameWithoutExtension(source.Path) == "__init__"
				? sourceModule
				: sourceModule.Contains('.') ? sourceModule[..sourceModule.LastIndexOf('.')] : string.Empty;
			var parts = sourcePackage.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
			if (import.RelativeLevel > 0)
			{
				var remove = import.RelativeLevel - 1;
				if (remove >= parts.Count)
					return [];
				parts.RemoveRange(parts.Count - remove, remove);
			}
			var module = import.RelativeLevel == 0
				? import.Specifier
				: string.Join('.', parts.Concat(import.Specifier.Split('.', StringSplitOptions.RemoveEmptyEntries)));
			var searchRoots = PythonRootPrefixes(source);
			if (import.RelativeLevel == 0)
			{
				var lookup = FindPythonImportRoots(source, module);
				if (lookup.Conflicting)
					return lookup.Conflict;
				searchRoots = lookup.Roots;
			}
			var files = ProbePythonModule(searchRoots, module).ToList();
			if (import.ImportedName is { Length: > 0 } name and not "*")
			{
				var child = module.Length == 0 ? name : module + "." + name;
				files.AddRange(ProbePythonModule(searchRoots, child));
			}
			return files.Distinct(StringComparer.Ordinal).ToArray();
		}
	}
}
