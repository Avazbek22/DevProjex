using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DevProjex.Application.Dependencies;

namespace DevProjex.Infrastructure.Dependencies;

public sealed class FileDependencyConfigurationProvider : IDependencyConfigurationProvider
{
	internal const int MaximumConfigurationBytes = 4 * 1024 * 1024;
	internal const int MaximumTypeScriptExtendsDepth = 8;
	internal const string TypeScriptExtendsShapeReason = "tsconfig extends must be one relative path string";
	internal const string TypeScriptExtendsPackageReason = "tsconfig package extends is not supported";
	internal const string TypeScriptExtendsOutsideRootReason = "tsconfig extends must stay inside the project root";
	internal const string TypeScriptExtendsCycleReason = "tsconfig extends cycle is not supported";
	internal const string TypeScriptExtendsDepthReason = "tsconfig extends exceeds the maximum depth";
	internal const string TypeScriptExtendsUnavailableReason = "extended tsconfig is unavailable";
	private readonly IDependencyControlFileReader _reader;

	public FileDependencyConfigurationProvider()
		: this(new BoundedDependencyControlFileReader())
	{
	}

	internal FileDependencyConfigurationProvider(IDependencyControlFileReader reader)
	{
		_reader = reader ?? throw new ArgumentNullException(nameof(reader));
	}

	public async Task<DependencyResolverConfiguration> ReadAsync(
		string sourceRoot,
		IReadOnlyList<string> manifestFiles,
		CancellationToken cancellationToken)
	{
		var root = Path.GetFullPath(sourceRoot);
		var manifest = manifestFiles.Select(Path.GetFullPath).ToHashSet(PathComparer);
		var scopes = new List<DependencyScopeDescriptor>();
		var fingerprintParts = new List<string>();
		var csharpProjects = new Dictionary<string, (string Scope, string[] References)>(PathComparer);
		var snapshots = new Dictionary<string, Task<DependencyControlFileSnapshot>>(PathComparer);
		var packageProjections = new Dictionary<string, Task<ConfigurationParseResult<PackageMapDescriptor>>>(PathComparer);
		var diagnostics = new List<DependencyConfigurationDiagnostic>();
		var absentControlFiles = new HashSet<string>(PathComparer);
		var fingerprintedControlFiles = new HashSet<string>(PathComparer);
		var transientReadFailure = 0;

		Task<DependencyControlFileSnapshot> ReadSnapshotAsync(string path)
		{
			if (snapshots.TryGetValue(path, out var existing)) return existing;
			var created = ReadTrackedSnapshotAsync(path);
			snapshots[path] = created;
			return created;
		}

		async Task<DependencyControlFileSnapshot> ReadTrackedSnapshotAsync(string path)
		{
			var snapshot = await _reader.ReadAsync(path, MaximumConfigurationBytes, cancellationToken).ConfigureAwait(false);
			if (snapshot.State == DependencyConfigurationState.Missing)
				absentControlFiles.Add(Path.GetFullPath(path));
			if (!snapshot.CanCache)
				Interlocked.Exchange(ref transientReadFailure, 1);
			return snapshot;
		}

		Task<ConfigurationParseResult<PackageMapDescriptor>> ReadPackageAsync(string path)
		{
			if (packageProjections.TryGetValue(path, out var existing)) return existing;
			var created = ReadPackageCoreAsync(path);
			packageProjections[path] = created;
			return created;
		}

		async Task<ConfigurationParseResult<PackageMapDescriptor>> ReadPackageCoreAsync(string path)
		{
			var snapshot = await ReadSnapshotAsync(path).ConfigureAwait(false);
			AddFingerprint(path, snapshot);
			return snapshot.State == DependencyConfigurationState.Valid
				? ParsePackageMap(Path.GetDirectoryName(path)!, snapshot.Content)
				: ConfigurationParseResult<PackageMapDescriptor>.Failure(
					UnavailablePackageMap(Path.GetDirectoryName(path)!, snapshot.State, snapshot.Reason),
					snapshot.State,
					snapshot.Reason);
		}

		void AddFingerprint(string path, DependencyControlFileSnapshot snapshot)
		{
			path = Path.GetFullPath(path);
			if (fingerprintedControlFiles.Add(path))
				fingerprintParts.Add(Fingerprint(root, path, snapshot.FingerprintValue));
		}

		async Task<ConfigurationParseResult<TypeScriptConfiguration>> ReadTypeScriptConfigAsync(
			string configPath,
			string scopeDirectory)
		{
			var chain = new HashSet<string>(PathComparer);
			var parsed = await ReadTypeScriptLayerAsync(Path.GetFullPath(configPath), 0, chain).ConfigureAwait(false);
			return parsed.State == DependencyConfigurationState.Valid
				? ConfigurationParseResult<TypeScriptConfiguration>.Valid(
					MaterializeTypeScriptConfiguration(parsed.Value, scopeDirectory))
				: ConfigurationParseResult<TypeScriptConfiguration>.Failure(
					TypeScriptConfiguration.Default,
					parsed.State,
					parsed.Reason);
		}

		async Task<ConfigurationParseResult<TypeScriptConfigurationLayer>> ReadTypeScriptLayerAsync(
			string configPath,
			int depth,
			HashSet<string> chain)
		{
			if (!chain.Add(configPath))
				return TypeScriptLayerFailure(DependencyConfigurationState.UnsupportedSemantics, TypeScriptExtendsCycleReason);

			try
			{
				var snapshot = await ReadSnapshotAsync(configPath).ConfigureAwait(false);
				AddFingerprint(configPath, snapshot);
				if (snapshot.State != DependencyConfigurationState.Valid)
				{
					return TypeScriptLayerFailure(
						snapshot.State,
						snapshot.State == DependencyConfigurationState.Missing && depth > 0
							? TypeScriptExtendsUnavailableReason
							: snapshot.Reason);
				}

				var layer = ParseTypeScriptConfigLayer(configPath, snapshot.Content);
				if (layer.State != DependencyConfigurationState.Valid || layer.Value.Extends is null)
					return layer;

				if (depth >= MaximumTypeScriptExtendsDepth)
					return TypeScriptLayerFailure(DependencyConfigurationState.UnsupportedSemantics, TypeScriptExtendsDepthReason);

				var extendedPath = ResolveTypeScriptExtendsPath(configPath, layer.Value.Extends);
				if (extendedPath.State != DependencyConfigurationState.Valid)
					return TypeScriptLayerFailure(extendedPath.State, extendedPath.Reason);
				if (!IsWithin(root, extendedPath.Value))
					return TypeScriptLayerFailure(DependencyConfigurationState.UnsupportedSemantics, TypeScriptExtendsOutsideRootReason);
				// The engine's fast manifest snapshot can validate only files in its manifest.
				// If an extended control file is outside that set, bypass that snapshot so this
				// provider observes every later edit instead of reusing configuration by metadata
				// that never included the base file.
				if (!manifest.Contains(extendedPath.Value))
					Interlocked.Exchange(ref transientReadFailure, 1);

				var inherited = await ReadTypeScriptLayerAsync(extendedPath.Value, depth + 1, chain)
					.ConfigureAwait(false);
				return inherited.State == DependencyConfigurationState.Valid
					? ConfigurationParseResult<TypeScriptConfigurationLayer>.Valid(
						MergeTypeScriptLayers(inherited.Value, layer.Value))
					: inherited;
			}
			finally
			{
				chain.Remove(configPath);
			}
		}

		void AddDiagnostic(string path, DependencyConfigurationState state, string? reason, params string[] scopeIds)
		{
			if (state == DependencyConfigurationState.Valid ||
			    state == DependencyConfigurationState.Missing && !manifest.Contains(path))
				return;
			diagnostics.Add(new DependencyConfigurationDiagnostic(
				PortableRelative(root, path),
				state,
				reason ?? "configuration is unavailable",
				scopeIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
		}

		foreach (var project in manifest.Where(static path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
		{
			var snapshot = await ReadSnapshotAsync(project).ConfigureAwait(false);
			AddFingerprint(project, snapshot);
			var scope = "csharp:" + PortableRelative(root, project);
			var parsed = snapshot.State == DependencyConfigurationState.Valid
				? ParseProjectReferences(project, snapshot.Content)
				: ConfigurationParseResult<string[]>.Failure([], snapshot.State, snapshot.Reason);
			var references = parsed.Value;
			foreach (var reference in references.Where(reference => !manifest.Contains(reference)))
			{
				var exists = File.Exists(reference);
				fingerprintParts.Add(Fingerprint(root, reference, exists ? "present" : "missing"));
				if (!exists)
					absentControlFiles.Add(reference);
			}
			csharpProjects[project] = (scope, references);
			AddDiagnostic(project, parsed.State, parsed.Reason, scope);
		}
		foreach (var pair in csharpProjects.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
		{
			var references = pair.Value.References
				.Where(csharpProjects.ContainsKey)
				.Select(path => csharpProjects[path].Scope)
				.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
			var diagnostic = diagnostics.FirstOrDefault(item => item.ScopeIds.Contains(pair.Value.Scope, StringComparer.Ordinal));
			scopes.Add(new DependencyScopeDescriptor(
				pair.Value.Scope,
				Path.GetDirectoryName(pair.Key)!,
				LanguageId.CSharp,
				references,
				null,
				false,
				new Dictionary<string, IReadOnlyList<string>>(),
				null,
				new HashSet<string>(),
				[],
				true)
			{
				ConfigurationState = diagnostic?.State ?? DependencyConfigurationState.Valid,
				ConfigurationDiagnostic = diagnostic?.Reason
			});
		}

		foreach (var configPath in manifest.Where(IsTypeScriptConfig).Order(StringComparer.Ordinal))
		{
			var directory = Path.GetDirectoryName(configPath)!;
			var parsed = await ReadTypeScriptConfigAsync(configPath, directory).ConfigureAwait(false);
			var package = FindNearestManifestFile(directory, root, "package.json", manifest);
			var packageName = package is null ? null : (await ReadPackageAsync(package).ConfigureAwait(false)).Value.PackageName;
			var scopeId = "typescript:" + PortableRelative(root, configPath);
			AddDiagnostic(configPath, parsed.State, parsed.Reason, scopeId);
			scopes.Add(new DependencyScopeDescriptor(
				scopeId,
				directory,
				LanguageId.TypeScript,
				[],
				parsed.Value.ModuleResolution,
				parsed.Value.Legacy,
				parsed.Value.Paths,
				packageName,
				new HashSet<string>(),
				[],
				true,
				AllowJavaScript: parsed.Value.AllowJavaScript)
			{
				ConfigurationState = parsed.State,
				ConfigurationDiagnostic = parsed.Reason
			});
		}

		foreach (var configPath in manifest.Where(IsPythonConfig).Order(StringComparer.Ordinal))
		{
			var snapshot = await ReadSnapshotAsync(configPath).ConfigureAwait(false);
			AddFingerprint(configPath, snapshot);
			var directory = Path.GetDirectoryName(configPath)!;
			var scopeId = "python:" + PortableRelative(root, configPath);
			AddDiagnostic(configPath, snapshot.State, snapshot.Reason, scopeId);
			scopes.Add(new DependencyScopeDescriptor(
				scopeId,
				directory,
				LanguageId.Python,
				[], null, false,
				new Dictionary<string, IReadOnlyList<string>>(), null,
				snapshot.State == DependencyConfigurationState.Valid
					? ParsePythonDependencies(configPath, snapshot.Content)
					: new HashSet<string>(),
				new[] { directory, Path.Combine(directory, "src") },
				true,
				snapshot.State == DependencyConfigurationState.Valid ? ParsePythonVersion(snapshot.Content) : null)
			{
				ConfigurationState = snapshot.State,
				ConfigurationDiagnostic = snapshot.Reason
			});
		}

		AddFallbackScope(scopes, root, LanguageId.CSharp);
		AddFallbackScope(scopes, root, LanguageId.TypeScript);
		AddFallbackScope(scopes, root, LanguageId.Python);
		var packageMaps = new Dictionary<string, PackageMapDescriptor>(StringComparer.Ordinal);
		foreach (var packagePath in manifest.Where(static path => Path.GetFileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
		{
			var parsed = await ReadPackageAsync(packagePath).ConfigureAwait(false);
			packageMaps[PortableRelative(root, parsed.Value.Directory)] = parsed.Value;
			AddDiagnostic(packagePath, parsed.State, parsed.Reason,
				scopes.Where(scope => IsWithin(parsed.Value.Directory, scope.Root)).Select(static scope => scope.ScopeId).ToArray());
		}

		return new DependencyResolverConfiguration(
			Hash(fingerprintParts.Order(StringComparer.Ordinal)),
			scopes.OrderBy(static scope => scope.ScopeId, StringComparer.Ordinal).ToArray(),
			packageMaps,
			DotNetCatalog.Value,
			PythonCatalogs.Value,
			NodeCatalog.Value)
		{
			ConfigurationDiagnostics = diagnostics
				.GroupBy(static item => (item.Path, item.State, item.Reason))
				.Select(static group => group.First() with
				{
					ScopeIds = group.SelectMany(static item => item.ScopeIds)
						.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
				})
				.OrderBy(static item => item.Path, StringComparer.Ordinal)
				.ToArray(),
			CanCache = Volatile.Read(ref transientReadFailure) == 0,
			AbsentControlFiles = absentControlFiles.Order(StringComparer.Ordinal).ToArray()
		};
	}

	private static IReadOnlySet<string> ParseNodeDependencies(JsonElement root)
	{
		var result = new HashSet<string>(StringComparer.Ordinal);
		foreach (var property in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" })
		{
			if (!root.TryGetProperty(property, out var dependencies) ||
			    dependencies.ValueKind != JsonValueKind.Object)
				continue;
			foreach (var dependency in dependencies.EnumerateObject())
				result.Add(dependency.Name);
		}
		return result;
	}

	private static ConfigurationParseResult<string[]> ParseProjectReferences(string projectPath, string content)
	{
		try
		{
			var directory = Path.GetDirectoryName(projectPath)!;
			return ConfigurationParseResult<string[]>.Valid(XDocument.Parse(content).Descendants()
				.Where(static element => element.Name.LocalName == "ProjectReference")
				.Select(element => element.Attribute("Include")?.Value)
				.Where(static value => !string.IsNullOrWhiteSpace(value))
				.Select(value => Path.GetFullPath(Path.Combine(directory, NormalizeMsBuildInclude(value!))))
				.Distinct(PathComparer).Order(StringComparer.Ordinal).ToArray());
		}
		catch (System.Xml.XmlException)
		{
			return ConfigurationParseResult<string[]>.Failure(
				[], DependencyConfigurationState.Corrupt, "invalid project XML");
		}
	}

	private static string NormalizeMsBuildInclude(string value) =>
		value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

	private static ConfigurationParseResult<TypeScriptConfigurationLayer> ParseTypeScriptConfigLayer(
		string configPath,
		string content)
	{
		try
		{
			using var document = JsonDocument.Parse(content, new JsonDocumentOptions
			{
				AllowTrailingCommas = true,
				CommentHandling = JsonCommentHandling.Skip
			});
			if (document.RootElement.ValueKind != JsonValueKind.Object)
				return TypeScriptLayerFailure(
					DependencyConfigurationState.UnsupportedSemantics,
					"tsconfig root must be an object");

			var extends = ParseTypeScriptExtends(document.RootElement);
			if (extends.State != DependencyConfigurationState.Valid)
				return TypeScriptLayerFailure(extends.State, extends.Reason);

			if (!document.RootElement.TryGetProperty("compilerOptions", out var options))
			{
				return ConfigurationParseResult<TypeScriptConfigurationLayer>.Valid(
					TypeScriptConfigurationLayer.Empty with { Extends = extends.Value });
			}
			if (options.ValueKind != JsonValueKind.Object)
				return TypeScriptLayerFailure(
					DependencyConfigurationState.Corrupt,
					"tsconfig compilerOptions must be an object");
			if (options.TryGetProperty("moduleResolution", out var mode) && mode.ValueKind != JsonValueKind.String)
				return TypeScriptLayerFailure(
					DependencyConfigurationState.UnsupportedSemantics,
					"tsconfig compilerOptions.moduleResolution must be a string");

			var hasModuleResolution = options.TryGetProperty("moduleResolution", out mode);
			var moduleResolution = hasModuleResolution ? mode.GetString() ?? "bundler" : null;
			var hasBaseUrl = options.TryGetProperty("baseUrl", out var baseUrlElement);
			var baseUrl = hasBaseUrl && baseUrlElement.ValueKind == JsonValueKind.String
				? baseUrlElement.GetString()
				: null;
			var hasAllowJavaScript = options.TryGetProperty("allowJs", out var allowJs);
			var allowJavaScript = hasAllowJavaScript && allowJs.ValueKind is JsonValueKind.True;
			var paths = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
			var hasPaths = options.TryGetProperty("paths", out var mappings);
			if (hasPaths && mappings.ValueKind != JsonValueKind.Object)
				return TypeScriptLayerFailure(
					DependencyConfigurationState.UnsupportedSemantics,
					"tsconfig compilerOptions.paths must be an object");
			if (mappings.ValueKind == JsonValueKind.Object)
			{
				foreach (var mapping in mappings.EnumerateObject())
				{
					if (mapping.Value.ValueKind != JsonValueKind.Array ||
					    mapping.Value.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String))
						return TypeScriptLayerFailure(
							DependencyConfigurationState.UnsupportedSemantics,
							"tsconfig path mapping must be an array of strings");
					paths[mapping.Name] = mapping.Value.EnumerateArray()
						.Select(static item => item.GetString()!).ToArray();
				}
			}
			return ConfigurationParseResult<TypeScriptConfigurationLayer>.Valid(
				new TypeScriptConfigurationLayer(
					extends.Value,
					new OptionalConfigurationValue<string>(hasModuleResolution, moduleResolution),
					new OptionalConfigurationValue<TypeScriptBaseUrl>(
						hasBaseUrl,
						hasBaseUrl ? new TypeScriptBaseUrl(Path.GetDirectoryName(configPath)!, baseUrl) : null),
					new OptionalConfigurationValue<TypeScriptPathMappings>(
						hasPaths,
						hasPaths ? new TypeScriptPathMappings(Path.GetDirectoryName(configPath)!, paths) : null),
					new OptionalConfigurationValue<bool>(hasAllowJavaScript, allowJavaScript)));
		}
		catch (JsonException)
		{
			return TypeScriptLayerFailure(
				DependencyConfigurationState.Corrupt,
				"invalid tsconfig JSON");
		}
	}

	private static ConfigurationParseResult<string?> ParseTypeScriptExtends(JsonElement root)
	{
		if (!root.TryGetProperty("extends", out var extends))
			return ConfigurationParseResult<string?>.Valid(null);
		if (extends.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(extends.GetString()))
			return ConfigurationParseResult<string?>.Failure(
				null,
				DependencyConfigurationState.UnsupportedSemantics,
				TypeScriptExtendsShapeReason);

		var value = extends.GetString()!;
		if (!IsExplicitRelativeTypeScriptExtends(value))
			return ConfigurationParseResult<string?>.Failure(
				null,
				DependencyConfigurationState.UnsupportedSemantics,
				TypeScriptExtendsPackageReason);
		return ConfigurationParseResult<string?>.Valid(value);
	}

	private static bool IsExplicitRelativeTypeScriptExtends(string value) =>
		value.StartsWith("./", StringComparison.Ordinal) ||
		value.StartsWith("../", StringComparison.Ordinal) ||
		value.StartsWith(".\\", StringComparison.Ordinal) ||
		value.StartsWith("..\\", StringComparison.Ordinal);

	private static ConfigurationParseResult<string> ResolveTypeScriptExtendsPath(
		string configPath,
		string relativePath)
	{
		try
		{
			var normalized = relativePath
				.Replace('/', Path.DirectorySeparatorChar)
				.Replace('\\', Path.DirectorySeparatorChar);
			return ConfigurationParseResult<string>.Valid(
				Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configPath)!, normalized)));
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return ConfigurationParseResult<string>.Failure(
				string.Empty,
				DependencyConfigurationState.UnsupportedSemantics,
				TypeScriptExtendsShapeReason);
		}
	}

	private static TypeScriptConfigurationLayer MergeTypeScriptLayers(
		TypeScriptConfigurationLayer inherited,
		TypeScriptConfigurationLayer child) =>
		new(
			Extends: null,
			child.ModuleResolution.IsSpecified ? child.ModuleResolution : inherited.ModuleResolution,
			child.BaseUrl.IsSpecified ? child.BaseUrl : inherited.BaseUrl,
			child.Paths.IsSpecified ? child.Paths : inherited.Paths,
			child.AllowJavaScript.IsSpecified ? child.AllowJavaScript : inherited.AllowJavaScript);

	private static TypeScriptConfiguration MaterializeTypeScriptConfiguration(
		TypeScriptConfigurationLayer layer,
		string scopeDirectory)
	{
		var moduleResolution = layer.ModuleResolution.IsSpecified
			? layer.ModuleResolution.Value ?? "bundler"
			: "bundler";
		var mappings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
		if (layer.Paths.Value is { } paths)
		{
			var mappingRoot = layer.BaseUrl.Value is { } baseUrl
				? ResolveTypeScriptOptionDirectory(baseUrl.DeclaringDirectory, baseUrl.Value)
				: paths.DeclaringDirectory;
			foreach (var mapping in paths.Values)
			{
				mappings[mapping.Key] = mapping.Value
					.Select(target => RebaseTypeScriptPath(scopeDirectory, mappingRoot, target))
					.ToArray();
			}
		}
		var legacy = moduleResolution.Equals("node10", StringComparison.OrdinalIgnoreCase) ||
		             moduleResolution.Equals("node", StringComparison.OrdinalIgnoreCase) ||
		             layer.BaseUrl.IsSpecified;
		return new TypeScriptConfiguration(
			moduleResolution,
			legacy,
			mappings,
			layer.AllowJavaScript.IsSpecified && layer.AllowJavaScript.Value);
	}

	private static string ResolveTypeScriptOptionDirectory(string declaringDirectory, string? relative) =>
		string.IsNullOrEmpty(relative)
			? declaringDirectory
			: Path.GetFullPath(Path.Combine(declaringDirectory, relative));

	private static string RebaseTypeScriptPath(string scopeDirectory, string mappingRoot, string target) =>
		Path.GetRelativePath(scopeDirectory, Path.GetFullPath(Path.Combine(mappingRoot, target)));

	private static ConfigurationParseResult<TypeScriptConfigurationLayer> TypeScriptLayerFailure(
		DependencyConfigurationState state,
		string? reason) =>
		ConfigurationParseResult<TypeScriptConfigurationLayer>.Failure(
			TypeScriptConfigurationLayer.Empty,
			state,
			reason);

	private static ConfigurationParseResult<PackageMapDescriptor> ParsePackageMap(string directory, string content)
	{
		try
		{
			using var document = JsonDocument.Parse(content);
			if (document.RootElement.ValueKind != JsonValueKind.Object)
				return ConfigurationParseResult<PackageMapDescriptor>.Failure(
					UnavailablePackageMap(
						directory,
						DependencyConfigurationState.UnsupportedSemantics,
						"package.json root must be an object"),
					DependencyConfigurationState.UnsupportedSemantics,
					"package.json root must be an object");
			return ConfigurationParseResult<PackageMapDescriptor>.Valid(new PackageMapDescriptor(
				directory,
				document.RootElement.TryGetProperty("name", out var name) ? name.GetString() : null,
				FlattenMap(document.RootElement, "imports"),
				FlattenMap(document.RootElement, "exports"),
				document.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null,
				ParseNodeDependencies(document.RootElement)));
		}
		catch (Exception exception) when (exception is JsonException or InvalidOperationException)
		{
			return ConfigurationParseResult<PackageMapDescriptor>.Failure(
				UnavailablePackageMap(directory, DependencyConfigurationState.Corrupt, "invalid package.json JSON"),
				DependencyConfigurationState.Corrupt,
				"invalid package.json JSON");
		}
	}

	private static PackageMapDescriptor EmptyPackageMap(string directory) => new(
		directory,
		null,
		new Dictionary<string, PackageTargetDescriptor>(),
		new Dictionary<string, PackageTargetDescriptor>(),
		null,
		new HashSet<string>());

	private static PackageMapDescriptor UnavailablePackageMap(
		string directory,
		DependencyConfigurationState state,
		string? reason) => EmptyPackageMap(directory) with
		{
			ConfigurationState = state,
			ConfigurationDiagnostic = reason ?? "package.json configuration is unavailable"
		};

	private static IReadOnlyDictionary<string, PackageTargetDescriptor> FlattenMap(JsonElement root, string property)
	{
		var result = new Dictionary<string, PackageTargetDescriptor>(StringComparer.Ordinal);
		if (!root.TryGetProperty(property, out var map)) return result;
		if (map.ValueKind is JsonValueKind.String or JsonValueKind.Null)
		{
			result["."] = ParsePackageTarget(map);
			return result;
		}
		if (map.ValueKind != JsonValueKind.Object)
		{
			result["."] = UnsupportedPackageTarget(property == "exports"
				? "package exports must be a string, null, or object"
				: "package imports must be a string, null, or object");
			return result;
		}
		if (property == "exports" && !map.EnumerateObject().Any(static item => item.Name.StartsWith(".", StringComparison.Ordinal)))
		{
			result["."] = ParsePackageTarget(map);
			return result;
		}
		foreach (var item in map.EnumerateObject()) result[item.Name] = ParsePackageTarget(item.Value);
		return result;
	}

	private static PackageTargetDescriptor ParsePackageTarget(JsonElement value)
	{
		if (value.ValueKind == JsonValueKind.String)
			return new PackageTargetDescriptor(PackageTargetKind.Path, value.GetString(), [], null);
		if (value.ValueKind == JsonValueKind.Null)
			return new PackageTargetDescriptor(PackageTargetKind.Blocked, null, [], null);
		if (value.ValueKind != JsonValueKind.Object)
			return UnsupportedPackageTarget("package target kind is not supported");
		return new PackageTargetDescriptor(
			PackageTargetKind.Conditions,
			null,
			value.EnumerateObject()
				.Select(static condition => new PackageConditionDescriptor(
					condition.Name,
					ParsePackageTarget(condition.Value)))
				.ToArray(),
			null);
	}

	private static PackageTargetDescriptor UnsupportedPackageTarget(string reason) =>
		new(PackageTargetKind.Unsupported, null, [], reason);

	private static string? ParsePythonVersion(string content)
	{
		var match = PythonVersionRegex.Match(content);
		if (!match.Success) return null;
		var constraint = match.Groups["constraint"].Value.Replace(" ", string.Empty, StringComparison.Ordinal);
		if (constraint.Contains(">=3.13", StringComparison.Ordinal) ||
		    constraint.Contains("==3.13", StringComparison.Ordinal) ||
		    constraint.Contains("~=3.13", StringComparison.Ordinal))
			return "3.13";
		if (constraint.Contains("<3.13", StringComparison.Ordinal) ||
		    constraint.Contains("==3.12", StringComparison.Ordinal) ||
		    constraint.Contains("~=3.12", StringComparison.Ordinal))
			return "3.12";
		return null;
	}

	private static IReadOnlySet<string> ParsePythonDependencies(string path, string content)
	{
		var result = new HashSet<string>(StringComparer.Ordinal);
		var isToml = Path.GetFileName(path).Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase);
		var section = string.Empty;
		var dependencyList = false;
		var setupRequirementList = false;
		foreach (var line in content.Split('\n'))
		{
			var value = line.Trim();
			if (value.StartsWith('[') && value.EndsWith(']'))
			{
				section = value.Trim('[', ']').Trim();
				dependencyList = false;
				setupRequirementList = false;
				continue;
			}
			if (isToml)
			{
				var optional = section.Equals("project.optional-dependencies", StringComparison.OrdinalIgnoreCase);
				var poetry = section.Equals("tool.poetry.dependencies", StringComparison.OrdinalIgnoreCase) ||
				             section.Equals("tool.poetry.group.dev.dependencies", StringComparison.OrdinalIgnoreCase);
				if (section.Equals("project", StringComparison.OrdinalIgnoreCase) &&
				    value.StartsWith("dependencies", StringComparison.OrdinalIgnoreCase) &&
				    value.Contains('='))
					dependencyList = !value.Contains(']');
				if (dependencyList || optional ||
				    section.Equals("project", StringComparison.OrdinalIgnoreCase) && value.StartsWith("dependencies", StringComparison.OrdinalIgnoreCase))
					AddQuotedRequirements(value, result);
				if (poetry && value.Contains('='))
					AddRequirement(value[..value.IndexOf('=')], result);
				if (dependencyList && value.Contains(']')) dependencyList = false;
				continue;
			}

			var inOptions = section.Equals("options", StringComparison.OrdinalIgnoreCase);
			var inExtras = section.Equals("options.extras_require", StringComparison.OrdinalIgnoreCase);
			if (inOptions && value.StartsWith("install_requires", StringComparison.OrdinalIgnoreCase) && value.Contains('='))
			{
				setupRequirementList = true;
				AddRequirement(value[(value.IndexOf('=') + 1)..], result);
				continue;
			}
			if (inExtras && value.Contains('='))
			{
				setupRequirementList = true;
				AddRequirement(value[(value.IndexOf('=') + 1)..], result);
				continue;
			}
			if (setupRequirementList && (line.Length == 0 || char.IsWhiteSpace(line[0])))
				AddRequirement(value, result);
			else if (value.Length > 0)
				setupRequirementList = false;
		}
		return result;
	}

	private static void AddQuotedRequirements(string value, ISet<string> result)
	{
		foreach (Match match in QuotedRequirementRegex.Matches(value))
			AddRequirement(match.Groups["requirement"].Value, result);
	}

	private static void AddRequirement(string value, ISet<string> result)
	{
		var candidate = value.Trim().Trim('"', '\'', ',', '[', ']');
		if (candidate.Length == 0 || candidate.Contains("://", StringComparison.Ordinal) ||
		    candidate.Contains("::", StringComparison.Ordinal)) return;
		var match = RequirementNameRegex.Match(candidate);
		if (match.Success && !match.Groups["name"].Value.Equals("python", StringComparison.OrdinalIgnoreCase))
			result.Add(match.Groups["name"].Value.Replace('-', '_'));
	}

	private static readonly Regex QuotedRequirementRegex = new(
		"[\\\"'](?<requirement>[^\\\"']+)[\\\"']",
		RegexOptions.CultureInvariant);
	private static readonly Regex RequirementNameRegex = new(
		"^(?<name>[A-Za-z0-9][A-Za-z0-9_.-]*)",
		RegexOptions.CultureInvariant);
	private static readonly Regex PythonVersionRegex = new(
		"^(?:requires-python|requires_python|python_requires)\\s*=\\s*[\\\"']?(?<constraint>[^\\\"'\\r\\n]+)",
		RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline);

	private static IReadOnlySet<string> LoadCatalog(string fileName)
	{
		var name = $"DevProjex.Infrastructure.Dependencies.PlatformSymbols.{fileName}";
		using var stream = typeof(FileDependencyConfigurationProvider).Assembly.GetManifestResourceStream(name) ??
			throw new InvalidOperationException($"Dependency platform catalog '{name}' is missing.");
		return JsonSerializer.Deserialize<string[]>(stream)?.ToFrozenSet(StringComparer.Ordinal) ??
			throw new InvalidOperationException($"Dependency platform catalog '{name}' is empty.");
	}
	private static IReadOnlyDictionary<string, IReadOnlySet<string>> LoadCatalogMap(string fileName)
	{
		var name = $"DevProjex.Infrastructure.Dependencies.PlatformSymbols.{fileName}";
		using var stream = typeof(FileDependencyConfigurationProvider).Assembly.GetManifestResourceStream(name) ??
			throw new InvalidOperationException($"Dependency platform catalog '{name}' is missing.");
		var catalog = JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream) ??
			throw new InvalidOperationException($"Dependency platform catalog '{name}' is empty.");
		return catalog.ToFrozenDictionary(
			static pair => pair.Key,
			static pair => (IReadOnlySet<string>)pair.Value.ToFrozenSet(StringComparer.Ordinal),
			StringComparer.Ordinal);
	}
	private static readonly Lazy<IReadOnlySet<string>> DotNetCatalog = new(
		() => LoadCatalog("dotnet-net10.0.json"),
		LazyThreadSafetyMode.ExecutionAndPublication);
	private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlySet<string>>> PythonCatalogs = new(
		() => LoadCatalogMap("python-3.12-3.13.json"),
		LazyThreadSafetyMode.ExecutionAndPublication);
	private static readonly Lazy<IReadOnlySet<string>> NodeCatalog = new(
		() => LoadCatalog("node-24.json"),
		LazyThreadSafetyMode.ExecutionAndPublication);

	private static bool IsTypeScriptConfig(string path) => Path.GetFileName(path) is "tsconfig.json" or "jsconfig.json";
	private static bool IsPythonConfig(string path) => Path.GetFileName(path) is "pyproject.toml" or "setup.cfg";
	private static string? FindNearestManifestFile(string directory, string root, string name, IReadOnlySet<string> manifest)
	{
		while (IsWithin(root, directory))
		{
			var candidate = Path.Combine(directory, name);
			if (manifest.Contains(candidate)) return candidate;
			if (Path.GetFullPath(directory) == Path.GetFullPath(root)) break;
			directory = Path.GetDirectoryName(directory)!;
		}
		return null;
	}
	private static void AddFallbackScope(ICollection<DependencyScopeDescriptor> scopes, string root, LanguageId language)
	{
		if (scopes.Any(scope => scope.LanguageId == language)) return;
		scopes.Add(new DependencyScopeDescriptor(
			$"root:{language.ToString().ToLowerInvariant()}", root, language, [],
			language == LanguageId.TypeScript ? "bundler" : null, false,
			new Dictionary<string, IReadOnlyList<string>>(), null, new HashSet<string>(),
			language == LanguageId.Python ? new[] { root, Path.Combine(root, "src") } : [],
			false)
		{
			ConfigurationState = DependencyConfigurationState.Missing,
			ConfigurationDiagnostic = "no owning configuration file in the manifest"
		});
	}
	private static string Fingerprint(string root, string path, string content) => $"{PortableRelative(root, path)}\0{Hash([content])}";
	private static string Hash(IEnumerable<string> values) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values)))).ToLowerInvariant();
	private static string PortableRelative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
	private static bool IsWithin(string root, string path)
	{
		var relative = Path.GetRelativePath(root, path);
		return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
	}
	private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
	private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
	private sealed record ConfigurationParseResult<T>(
		T Value,
		DependencyConfigurationState State,
		string? Reason)
	{
		public static ConfigurationParseResult<T> Valid(T value) =>
			new(value, DependencyConfigurationState.Valid, null);

		public static ConfigurationParseResult<T> Failure(
			T value,
			DependencyConfigurationState state,
			string? reason) => new(value, state, reason ?? "configuration is unavailable");
	}
	private sealed record TypeScriptConfiguration(
		string ModuleResolution,
		bool Legacy,
		IReadOnlyDictionary<string, IReadOnlyList<string>> Paths,
		bool AllowJavaScript)
	{
		public static readonly TypeScriptConfiguration Default = new(
			"bundler",
			false,
			new Dictionary<string, IReadOnlyList<string>>(),
			false);
	}

	private readonly record struct OptionalConfigurationValue<T>(bool IsSpecified, T? Value);

	private sealed record TypeScriptBaseUrl(string DeclaringDirectory, string? Value);

	private sealed record TypeScriptPathMappings(
		string DeclaringDirectory,
		IReadOnlyDictionary<string, IReadOnlyList<string>> Values);

	private sealed record TypeScriptConfigurationLayer(
		string? Extends,
		OptionalConfigurationValue<string> ModuleResolution,
		OptionalConfigurationValue<TypeScriptBaseUrl> BaseUrl,
		OptionalConfigurationValue<TypeScriptPathMappings> Paths,
		OptionalConfigurationValue<bool> AllowJavaScript)
	{
		public static readonly TypeScriptConfigurationLayer Empty = new(
			null,
			default,
			default,
			default,
			default);
	}
}

internal interface IDependencyControlFileReader
{
	ValueTask<DependencyControlFileSnapshot> ReadAsync(
		string path,
		int maximumBytes,
		CancellationToken cancellationToken);
}

internal sealed record DependencyControlFileSnapshot(
	DependencyConfigurationState State,
	string Content,
	string? Reason,
	string FingerprintValue,
	bool CanCache = true);

internal sealed class BoundedDependencyControlFileReader : IDependencyControlFileReader
{
	private static ReadOnlySpan<byte> Utf8Preamble => [0xEF, 0xBB, 0xBF];
	private static readonly UTF8Encoding StrictUtf8 = new(false, true);

	public async ValueTask<DependencyControlFileSnapshot> ReadAsync(
		string path,
		int maximumBytes,
		CancellationToken cancellationToken)
	{
		try
		{
			await using var stream = new FileStream(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read | FileShare.Delete,
				64 * 1024,
				FileOptions.Asynchronous | FileOptions.SequentialScan);
			var length = stream.Length;
			var lastWrite = File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks;
			if (length > maximumBytes || length > int.MaxValue)
			{
				return Failure(
					DependencyConfigurationState.UnsupportedSemantics,
					$"configuration exceeds the {maximumBytes} byte limit",
					length,
					lastWrite);
			}

			var bytes = new byte[checked((int)length)];
			var written = 0;
			while (written < bytes.Length)
			{
				var read = await stream.ReadAsync(bytes.AsMemory(written), cancellationToken).ConfigureAwait(false);
				if (read == 0) break;
				written += read;
			}
			var currentLength = stream.Length;
			var currentLastWrite = File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks;
			if (written != bytes.Length || currentLength != length || currentLastWrite != lastWrite)
			{
				return Failure(
					DependencyConfigurationState.Corrupt,
					"configuration changed while it was being read",
					currentLength,
					currentLastWrite,
					canCache: false);
			}
			var offset = bytes.AsSpan().StartsWith(Utf8Preamble) ? Utf8Preamble.Length : 0;
			var content = StrictUtf8.GetString(bytes.AsSpan(offset));
			return new DependencyControlFileSnapshot(
				DependencyConfigurationState.Valid,
				content,
				null,
				$"valid:{length}:{lastWrite}:{Hash(bytes)}");
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (FileNotFoundException)
		{
			return Failure(DependencyConfigurationState.Missing, "configuration file is unavailable", 0, 0);
		}
		catch (DirectoryNotFoundException)
		{
			return Failure(DependencyConfigurationState.Missing, "configuration file is unavailable", 0, 0);
		}
		catch (DecoderFallbackException)
		{
			return Failure(DependencyConfigurationState.Corrupt, "configuration is not valid UTF-8", 0, 0);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return Failure(DependencyConfigurationState.Corrupt, "configuration file could not be read", 0, 0, canCache: false);
		}
	}

	private static DependencyControlFileSnapshot Failure(
		DependencyConfigurationState state,
		string reason,
		long length,
		long lastWrite,
		bool canCache = true) => new(state, string.Empty, reason, $"{state}:{length}:{lastWrite}:{reason}", canCache);

	private static string Hash(ReadOnlySpan<byte> bytes) =>
		Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

	private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
