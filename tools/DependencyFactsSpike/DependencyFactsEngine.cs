using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DependencyFactsSpike;

internal sealed class DependencyFactsEngine(string grammarCache)
{
	public RepositoryResult Index(IndexOptions options)
	{
		var stopwatch = Stopwatch.StartNew();
		var projects = ProjectMap.Build(options.Root);
		var previous = LoadPrevious(options.PreviousFacts);
		var files = Directory.EnumerateFiles(options.Root, "*", SearchOption.AllDirectories)
			.Where(LanguageCatalog.IsSupported)
			.Where(static path => !PathPolicy.IsExcluded(path))
			.OrderBy(path => Path.GetRelativePath(options.Root, path), StringComparer.Ordinal)
			.ToArray();
		if (options.ReverseFileOrder)
			Array.Reverse(files);

		var parsed = 0;
		var reused = 0;
		var errors = 0;
		var facts = new List<FileFacts>(files.Length);
		using (var extractor = new TreeSitterFactExtractor(grammarCache))
		{
			foreach (var path in files)
			{
				var relative = Path.GetRelativePath(options.Root, path).Replace('\\', '/');
				var hash = HashFile(path);
				if (previous.TryGetValue(relative, out var cached) && cached.ContentHash == hash)
				{
					facts.Add(cached);
					reused++;
					continue;
				}
				try
				{
					facts.Add(extractor.Extract(options.Root, path, projects));
					parsed++;
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
				{
					Console.Error.WriteLine($"extract-error: {relative}: {exception.Message}");
					errors++;
				}
			}
		}

		facts = facts.OrderBy(static fact => fact.Path, StringComparer.Ordinal).ToList();
		var symbols = MergeDeclarations(facts);
		var resolver = new DependencyResolver(options.Root, facts, symbols, projects);
		var edges = resolver.ResolveAll();
		stopwatch.Stop();
		Process.GetCurrentProcess().Refresh();
		var statusCounts = Enum.GetValues<ResolutionStatus>()
			.ToDictionary(status => status, status => edges.Count(edge => edge.Status == status));
		var metrics = new RepositoryMetrics(
			facts.Count,
			errors,
			facts.Count(static fact => fact.HasSyntaxErrors),
			edges.Count(static edge => edge.Layer == EvidenceLayer.ExplicitImport),
			edges.Count(static edge => edge.Layer == EvidenceLayer.TypeReference),
			statusCounts,
			stopwatch.ElapsedMilliseconds,
			Process.GetCurrentProcess().PeakWorkingSet64,
			parsed,
			reused,
			facts.Count);
		var result = new RepositoryResult(options.Root.Replace('\\', '/'), options.CorpusSha, facts, symbols, edges, metrics, string.Empty);
		var persisted = result with
		{
			Metrics = metrics with
			{
				ElapsedMilliseconds = 0,
				PeakWorkingSetBytes = 0,
				ParsedFiles = 0,
				ReusedFiles = 0,
				ReresolvedFiles = 0
			}
		};
		var firstBytes = JsonSerializer.SerializeToUtf8Bytes(persisted, SpikeJsonContext.Default.RepositoryResult);
		var resultHash = Convert.ToHexString(SHA256.HashData(firstBytes)).ToLowerInvariant();
		persisted = persisted with { ResultSha256 = resultHash };
		Directory.CreateDirectory(Path.GetDirectoryName(options.Output) ?? ".");
		File.WriteAllBytes(options.Output, JsonSerializer.SerializeToUtf8Bytes(persisted, SpikeJsonContext.Default.RepositoryResult));
		return result with { ResultSha256 = resultHash };
	}

	private static Dictionary<string, FileFacts> LoadPrevious(string? path)
	{
		if (path is null || !File.Exists(path))
			return new Dictionary<string, FileFacts>(StringComparer.Ordinal);
		var result = JsonSerializer.Deserialize(File.ReadAllBytes(path), SpikeJsonContext.Default.RepositoryResult);
		return result?.Files.ToDictionary(static fact => fact.Path, StringComparer.Ordinal)
		       ?? new Dictionary<string, FileFacts>(StringComparer.Ordinal);
	}

	private static string HashFile(string path)
	{
		using var stream = File.OpenRead(path);
		return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
	}

	private static IReadOnlyList<DeclarationFact> MergeDeclarations(IReadOnlyList<FileFacts> files) =>
		files.SelectMany(static file => file.Declarations)
			.GroupBy(static declaration => declaration.Identity)
			.Select(static group => new DeclarationFact(
				group.Key,
				group.SelectMany(static declaration => declaration.Sites)
					.Distinct()
					.OrderBy(static site => site.File, StringComparer.Ordinal)
					.ThenBy(static site => site.Line)
					.ToArray()))
			.OrderBy(static declaration => declaration.Identity.ScopeId, StringComparer.Ordinal)
			.ThenBy(static declaration => declaration.Identity.QualifiedName, StringComparer.Ordinal)
			.ThenBy(static declaration => declaration.Identity.Kind)
			.ToArray();
}

internal sealed class DependencyResolver(
	string root,
	IReadOnlyList<FileFacts> files,
	IReadOnlyList<DeclarationFact> symbols,
	ProjectMap projects)
{
	private readonly IReadOnlyDictionary<string, FileFacts> filesByPath =
		files.ToDictionary(static file => file.Path, StringComparer.Ordinal);
	private static readonly HashSet<string> CSharpExternalTypes = new(
		["String", "Int32", "Int64", "Boolean", "Object", "DateTime", "DateTimeOffset", "Guid", "Task", "ValueTask", "CancellationToken", "IEnumerable", "IReadOnlyList", "IReadOnlyCollection", "IReadOnlyDictionary", "Dictionary", "List", "HashSet", "Exception", "Stream", "FileInfo", "DirectoryInfo", "Uri", "Regex", "JsonElement", "JsonDocument", "string", "int", "long", "bool", "byte", "char", "double", "decimal"],
		StringComparer.Ordinal);
	private static readonly HashSet<string> PythonExternalModules = new(
		["abc", "argparse", "asyncio", "collections", "contextlib", "dataclasses", "datetime", "enum", "functools", "hashlib", "inspect", "io", "itertools", "json", "logging", "os", "pathlib", "re", "secrets", "sys", "tempfile", "threading", "time", "typing", "urllib", "uuid", "warnings", "weakref"],
		StringComparer.Ordinal);

	public IReadOnlyList<DependencyEdge> ResolveAll()
	{
		var edges = new List<DependencyEdge>();
		foreach (var file in files)
		{
			foreach (var import in file.Imports)
				edges.Add(ResolveImport(file, import));
			foreach (var reference in file.References)
				edges.Add(ResolveType(file, reference));
		}
		return edges.OrderBy(static edge => edge.Source, StringComparer.Ordinal)
			.ThenBy(static edge => edge.Line)
			.ThenBy(static edge => edge.Layer)
			.ThenBy(static edge => edge.Reference, StringComparer.Ordinal)
			.ThenBy(static edge => edge.Target, StringComparer.Ordinal)
			.ToArray();
	}

	private DependencyEdge ResolveImport(FileFacts source, ImportContext import) => source.Language switch
	{
		LanguageId.TypeScript or LanguageId.JavaScript or LanguageId.Tsx => ResolveTypeScriptImport(source, import),
		LanguageId.Python => ResolvePythonImport(source, import),
		_ => UnresolvedImport(source, import, "explicit imports are not edges for this language")
	};

	private DependencyEdge ResolveTypeScriptImport(FileFacts source, ImportContext import)
	{
		var descriptor = projects.Descriptor(source.ScopeId);
		if (descriptor?.LegacyTypeScriptConfiguration == true)
			return UnresolvedImport(source, import, "legacy tsconfig: node10/baseUrl semantics are intentionally not emulated");
		var candidates = new List<string>();
		if (import.Specifier.StartsWith(".", StringComparison.Ordinal))
		{
			var directory = Path.GetDirectoryName(Path.Combine(root, source.Path))!;
			candidates.AddRange(ProbeTypeScript(Path.GetFullPath(Path.Combine(directory, import.Specifier)), descriptor));
		}
		else if (import.Specifier.StartsWith('#'))
		{
			candidates.AddRange(ResolvePackageMap(source, import.Specifier, "imports"));
			if (candidates.Count == 0)
				return UnresolvedImport(source, import, "package imports map has no enabled target or explicitly blocks it");
		}
		else if (descriptor?.PackageName is not null &&
		         (import.Specifier == descriptor.PackageName || import.Specifier.StartsWith(descriptor.PackageName + '/', StringComparison.Ordinal)))
		{
			candidates.AddRange(ResolvePackageMap(source, import.Specifier[descriptor.PackageName.Length..].TrimStart('/'), "exports"));
			if (candidates.Count == 0)
				return UnresolvedImport(source, import, "package self-reference is absent from exports or explicitly null-blocked");
		}
		else
		{
			var pathCandidates = ResolveTsPaths(descriptor, import.Specifier);
			candidates.AddRange(pathCandidates);
			if (candidates.Count == 0)
				return Edge(source, import, ResolutionStatus.External, null, "bare package import; node_modules is outside the manifest", []);
		}

		return FinishImport(source, import, candidates);
	}

	private IEnumerable<string> ResolveTsPaths(ScopeDescriptor? descriptor, string specifier)
	{
		if (descriptor is null)
			return [];
		var mappings = descriptor.TypeScriptPaths
			.Select(pair => (Pattern: pair.Key, Targets: pair.Value, Wildcard: pair.Key.IndexOf('*')))
			.Where(item => item.Wildcard < 0 ? item.Pattern == specifier :
				specifier.StartsWith(item.Pattern[..item.Wildcard], StringComparison.Ordinal) &&
				specifier.EndsWith(item.Pattern[(item.Wildcard + 1)..], StringComparison.Ordinal))
			.OrderBy(static item => item.Wildcard >= 0)
			.ThenByDescending(static item => item.Pattern.Length)
			.ToArray();
		foreach (var mapping in mappings)
		{
			var wildcard = mapping.Wildcard < 0 ? string.Empty :
				specifier[mapping.Wildcard..(specifier.Length - (mapping.Pattern.Length - mapping.Wildcard - 1))];
			var resolved = mapping.Targets.SelectMany(target => ProbeTypeScript(
				Path.GetFullPath(Path.Combine(descriptor.Root, target.Replace("*", wildcard, StringComparison.Ordinal))), descriptor)).ToArray();
			if (resolved.Length > 0)
				return resolved;
		}
		return [];
	}

	private IEnumerable<string> ResolvePackageMap(FileFacts source, string specifier, string propertyName)
	{
		var current = Path.GetDirectoryName(Path.Combine(root, source.Path))!;
		while (PathPolicy.IsWithin(root, current))
		{
			var packagePath = Path.Combine(current, "package.json");
			if (File.Exists(packagePath))
			{
				try
				{
					using var package = JsonDocument.Parse(File.ReadAllText(packagePath));
					if (!package.RootElement.TryGetProperty(propertyName, out var map) || map.ValueKind != JsonValueKind.Object)
						return [];
					var key = propertyName == "exports" ? (specifier.Length == 0 ? "." : "./" + specifier) : specifier;
					if (!TrySelectPackageMapTarget(map, key, out var target, out var wildcard) || target.ValueKind == JsonValueKind.Null)
						return [];
					var value = SelectConditionalTarget(target);
					if (value is not null && wildcard is not null)
						value = value.Replace("*", wildcard, StringComparison.Ordinal);
					return value is null ? [] : ProbeTypeScript(Path.GetFullPath(Path.Combine(current, value)), projects.Descriptor(source.ScopeId));
				}
				catch (JsonException)
				{
					return [];
				}
			}
			if (string.Equals(Path.GetFullPath(current), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
				break;
			current = Path.GetDirectoryName(current)!;
		}
		return [];
	}

	private static bool TrySelectPackageMapTarget(
		JsonElement map,
		string key,
		out JsonElement target,
		out string? wildcard)
	{
		if (map.TryGetProperty(key, out target))
		{
			wildcard = null;
			return true;
		}
		foreach (var property in map.EnumerateObject()
			         .Where(static property => property.Name.Contains('*'))
			         .OrderByDescending(static property => property.Name.Length))
		{
			var star = property.Name.IndexOf('*');
			var prefix = property.Name[..star];
			var suffix = property.Name[(star + 1)..];
			if (!key.StartsWith(prefix, StringComparison.Ordinal) || !key.EndsWith(suffix, StringComparison.Ordinal))
				continue;
			target = property.Value;
			wildcard = key[prefix.Length..(key.Length - suffix.Length)];
			return true;
		}
		target = default;
		wildcard = null;
		return false;
	}

	private static string? SelectConditionalTarget(JsonElement element)
	{
		if (element.ValueKind == JsonValueKind.String)
			return element.GetString();
		if (element.ValueKind != JsonValueKind.Object)
			return null;
		foreach (var condition in new[] { "types", "import", "require", "node", "default" })
		{
			if (element.TryGetProperty(condition, out var target))
				return SelectConditionalTarget(target);
		}
		return null;
	}

	private IEnumerable<string> ProbeTypeScript(string candidate, ScopeDescriptor? descriptor)
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
		var mode = descriptor?.ModuleResolution ?? "bundler";
		if (mode.Equals("node", StringComparison.OrdinalIgnoreCase) || mode.Equals("node16", StringComparison.OrdinalIgnoreCase))
			probes.AddRange([Path.Combine(candidate, "index.ts"), Path.Combine(candidate, "index.tsx"), Path.Combine(candidate, "index.d.ts")]);
		return probes.Where(File.Exists)
			.Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
			.Distinct(StringComparer.Ordinal);
	}

	private DependencyEdge ResolvePythonImport(FileFacts source, ImportContext import)
	{
		var sourceModule = PythonModule(source.Path);
		var sourcePackage = Path.GetFileNameWithoutExtension(source.Path) == "__init__"
			? sourceModule
			: sourceModule.Contains('.') ? sourceModule[..sourceModule.LastIndexOf('.')] : string.Empty;
		var baseParts = sourcePackage.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
		if (import.RelativeLevel > 0)
		{
			var remove = import.RelativeLevel - 1;
			if (remove > baseParts.Count)
				return UnresolvedImport(source, import, "relative import escapes its package context");
			baseParts.RemoveRange(baseParts.Count - remove, remove);
		}
		var module = string.Join('.', baseParts.Concat(import.Specifier.Split('.', StringSplitOptions.RemoveEmptyEntries)));
		if (import.RelativeLevel == 0)
			module = import.Specifier;
		var candidates = ProbePythonModule(module).ToList();
		if (import.ImportedName is { Length: > 0 } and not "*")
		{
			var child = module.Length == 0 ? import.ImportedName : module + "." + import.ImportedName;
			var childCandidates = ProbePythonModule(child).ToArray();
			if (childCandidates.Length > 0)
				candidates = childCandidates.ToList();
			else
				candidates = candidates
					.Where(candidate => !Path.GetFileName(candidate).StartsWith("__init__.", StringComparison.Ordinal) ||
					                    PythonModuleStaticallyProvides(candidate, import.ImportedName, 0, new HashSet<string>(StringComparer.Ordinal)))
					.ToList();
		}
		if (candidates.Count == 0)
		{
			var portions = ProbePythonNamespace(module).ToArray();
			if (portions.Length > 0)
				return Edge(source, import, ResolutionStatus.Resolved, "namespace:" + module,
					$"one namespace-package entity with {portions.Length} portion(s)", portions);
		}
		if (candidates.Count == 0 && import.RelativeLevel == 0 && PythonExternalModules.Contains(import.Specifier.Split('.')[0]))
			return Edge(source, import, ResolutionStatus.External, null, "Python standard-library module is outside the manifest", []);
		if (candidates.Count == 0 && import.RelativeLevel == 0 &&
		    projects.Descriptor(source.ScopeId)?.PythonExternalPackages.Contains(import.Specifier.Split('.')[0]) == true)
			return Edge(source, import, ResolutionStatus.External, null, "declared Python package is outside the source manifest", []);
		if (candidates.Count == 0 && import.RelativeLevel == 0)
			return Edge(source, import, ResolutionStatus.Unresolved, null, "no module target or declared external package; absence is not evidence of externality", []);
		if (import.IsWildcard && candidates.Any(IsDynamicPythonAll))
			return Edge(source, import, ResolutionStatus.Unresolved, null, "dynamic __all__ is an unsupported mechanism", candidates);
		return FinishImport(source, import, candidates);
	}

	private bool PythonModuleStaticallyProvides(string candidate, string name, int depth, ISet<string> visited)
	{
		if (depth >= 8 || !visited.Add(candidate) || !filesByPath.TryGetValue(candidate, out var facts))
			return false;
		if (facts.Declarations.Any(declaration => SimpleName(declaration.Identity.QualifiedName) == name))
			return true;

		foreach (var reexport in facts.Imports.Where(import =>
				string.Equals(import.Alias ?? import.ImportedName ?? import.Specifier.Split('.').Last(), name, StringComparison.Ordinal)))
		{
			var sourceModule = PythonModule(candidate);
			var sourcePackage = Path.GetFileNameWithoutExtension(candidate) == "__init__"
				? sourceModule
				: sourceModule.Contains('.') ? sourceModule[..sourceModule.LastIndexOf('.')] : string.Empty;
			var baseParts = sourcePackage.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
			if (reexport.RelativeLevel > 0)
			{
				var remove = reexport.RelativeLevel - 1;
				if (remove > baseParts.Count)
					continue;
				baseParts.RemoveRange(baseParts.Count - remove, remove);
			}
			var module = reexport.RelativeLevel == 0
				? reexport.Specifier
				: string.Join('.', baseParts.Concat(reexport.Specifier.Split('.', StringSplitOptions.RemoveEmptyEntries)));
			if (reexport.ImportedName is { Length: > 0 } importedName)
			{
				var child = module.Length == 0 ? importedName : module + "." + importedName;
				var childCandidates = ProbePythonModule(child).ToArray();
				if (childCandidates.Length > 0)
					return true;
				if (ProbePythonModule(module).Any(next => PythonModuleStaticallyProvides(next, importedName, depth + 1, visited)))
					return true;
			}
			else if (ProbePythonModule(module).Any())
				return true;
		}
		return false;
	}

	private IEnumerable<string> ProbePythonNamespace(string module)
	{
		if (module.Length == 0)
			yield break;
		var relative = module.Replace('.', Path.DirectorySeparatorChar);
		foreach (var pythonRoot in new[] { root, Path.Combine(root, "src") })
		{
			var directory = Path.Combine(pythonRoot, relative);
			if (Directory.Exists(directory) &&
			    !File.Exists(Path.Combine(directory, "__init__.py")) &&
			    !File.Exists(Path.Combine(directory, "__init__.pyi")))
				yield return Path.GetRelativePath(root, directory).Replace('\\', '/') + "/";
		}
	}

	private bool IsDynamicPythonAll(string candidate)
	{
		if (!Path.GetFileName(candidate).StartsWith("__init__.", StringComparison.Ordinal))
			return false;
		var text = File.ReadAllText(Path.Combine(root, candidate));
		var assignment = text.Split('\n').FirstOrDefault(static line => line.TrimStart().StartsWith("__all__", StringComparison.Ordinal));
		return assignment is not null && !(assignment.Contains('[', StringComparison.Ordinal) || assignment.Contains('(', StringComparison.Ordinal) && assignment.Contains(')', StringComparison.Ordinal) && assignment.Contains('"', StringComparison.Ordinal));
	}

	private IEnumerable<string> ProbePythonModule(string module)
	{
		var relative = module.Replace('.', Path.DirectorySeparatorChar);
		foreach (var pythonRoot in new[] { root, Path.Combine(root, "src") })
		{
			var implementation = Path.Combine(pythonRoot, relative + ".py");
			var stub = Path.Combine(pythonRoot, relative + ".pyi");
			var packageImplementation = Path.Combine(pythonRoot, relative, "__init__.py");
			var packageStub = Path.Combine(pythonRoot, relative, "__init__.pyi");
			// The spike fixes the policy to implementation-first, then stub-only.
			foreach (var candidate in new[] { implementation, packageImplementation, stub, packageStub })
			{
				if (File.Exists(candidate))
					yield return Path.GetRelativePath(root, candidate).Replace('\\', '/');
			}
		}
	}

	private DependencyEdge ResolveType(FileFacts source, ReferenceFact reference)
	{
		if (source.TypeParameters.Contains(reference.Name, StringComparer.Ordinal))
			return Edge(source, reference, ResolutionStatus.Unresolved, null, "type parameter shadows global declarations", []);
		var candidates = symbols.Where(symbol => IsVisible(source, symbol) && SimpleName(symbol.Identity.QualifiedName) == reference.Name)
			.ToArray();
		if (source.Language == LanguageId.CSharp)
		{
			var qualified = source.Aliases.TryGetValue(reference.Name, out var aliasTarget) ? aliasTarget : null;
			if (qualified is not null)
				candidates = candidates.Where(symbol => symbol.Identity.QualifiedName.StartsWith(qualified, StringComparison.Ordinal)).ToArray();
			else if (source.ContextNamespaces.Count > 0)
			{
				var contextual = candidates.Where(symbol => source.ContextNamespaces.Any(ns => symbol.Identity.QualifiedName.StartsWith(ns + '.', StringComparison.Ordinal))).ToArray();
				if (contextual.Length > 0)
					candidates = contextual;
			}
		}

		if (candidates.Length == 0)
		{
			if (source.Language == LanguageId.CSharp && CSharpExternalTypes.Contains(reference.Name))
				return Edge(source, reference, ResolutionStatus.External, null, "known BCL type outside the source manifest", []);
			return Edge(source, reference, ResolutionStatus.Unresolved, null, "no declaration in the manifest; absence is not evidence of externality", []);
		}
		var candidateFiles = candidates.SelectMany(static candidate => candidate.Sites.Select(static site => site.File))
			.Distinct(StringComparer.Ordinal).OrderBy(static path => path, StringComparer.Ordinal).ToArray();
		return candidates.Length == 1
			? Edge(source, reference, ResolutionStatus.Resolved, candidateFiles[0], "one visible declaration identity", candidateFiles)
			: Edge(source, reference, ResolutionStatus.Ambiguous, null, "multiple visible declaration identities", candidateFiles);
	}

	private bool IsVisible(FileFacts source, DeclarationFact symbol)
	{
		if (symbol.Identity.Language != source.Language &&
		    !(source.Language is LanguageId.TypeScript or LanguageId.JavaScript or LanguageId.Tsx &&
		      symbol.Identity.Language is LanguageId.TypeScript or LanguageId.JavaScript or LanguageId.Tsx))
			return false;
		if (symbol.Identity.ScopeId == source.ScopeId || symbol.Identity.ScopeId == source.ScopeId + "#file:" + source.Path)
			return true;
		if (source.Language != LanguageId.CSharp)
			return false;
		var descriptor = projects.Descriptor(source.ScopeId);
		return descriptor?.ProjectReferences.Contains(symbol.Identity.ScopeId, StringComparer.Ordinal) == true;
	}

	private static string SimpleName(string qualified)
	{
		var name = qualified[(Math.Max(qualified.LastIndexOf('.'), qualified.LastIndexOf('#')) + 1)..];
		var arity = name.IndexOf('`');
		return arity < 0 ? name : name[..arity];
	}

	private static string PythonModule(string path)
	{
		var module = Path.ChangeExtension(path, null)!.Replace('/', '.').Replace('\\', '.');
		if (module.StartsWith("src.", StringComparison.Ordinal))
			module = module[4..];
		return module.EndsWith(".__init__", StringComparison.Ordinal) ? module[..^".__init__".Length] : module;
	}

	private DependencyEdge FinishImport(FileFacts source, ImportContext import, IEnumerable<string> rawCandidates)
	{
		var candidates = rawCandidates.Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray();
		return candidates.Length switch
		{
			0 => UnresolvedImport(source, import, "no target in the manifest"),
			1 => Edge(source, import, ResolutionStatus.Resolved, candidates[0], "one module target under configured resolution rules", candidates),
			_ => Edge(source, import, ResolutionStatus.Ambiguous, null, "multiple module targets under configured resolution rules", candidates)
		};
	}

	private static DependencyEdge UnresolvedImport(FileFacts source, ImportContext import, string reason) =>
		Edge(source, import, ResolutionStatus.Unresolved, null, reason, []);

	private static DependencyEdge Edge(
		FileFacts source,
		ImportContext import,
		ResolutionStatus status,
		string? target,
		string reason,
		IReadOnlyList<string> candidates) =>
		new(source.Path, target, EvidenceLayer.ExplicitImport, status,
			import.IsWildcard ? import.Specifier + ".*" : import.Specifier,
			import.Line, import.Evidence, reason, candidates);

	private static DependencyEdge Edge(
		FileFacts source,
		ReferenceFact reference,
		ResolutionStatus status,
		string? target,
		string reason,
		IReadOnlyList<string> candidates) =>
		new(source.Path, target, reference.Layer, status, reference.Name, reference.Line, reference.Evidence, reason, candidates);
}
