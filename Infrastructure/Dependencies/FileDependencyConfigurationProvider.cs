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

		Task<DependencyControlFileSnapshot> ReadSnapshotAsync(string path)
		{
			if (snapshots.TryGetValue(path, out var existing)) return existing;
			var created = _reader.ReadAsync(path, MaximumConfigurationBytes, cancellationToken).AsTask();
			snapshots[path] = created;
			return created;
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
			fingerprintParts.Add(Fingerprint(root, path, snapshot.FingerprintValue));
			return snapshot.State == DependencyConfigurationState.Valid
				? ParsePackageMap(Path.GetDirectoryName(path)!, snapshot.Content)
				: ConfigurationParseResult<PackageMapDescriptor>.Failure(
					UnavailablePackageMap(Path.GetDirectoryName(path)!, snapshot.State, snapshot.Reason),
					snapshot.State,
					snapshot.Reason);
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
			fingerprintParts.Add(Fingerprint(root, project, snapshot.FingerprintValue));
			var scope = "csharp:" + PortableRelative(root, project);
			var parsed = snapshot.State == DependencyConfigurationState.Valid
				? ParseProjectReferences(project, snapshot.Content)
				: ConfigurationParseResult<string[]>.Failure([], snapshot.State, snapshot.Reason);
			var references = parsed.Value;
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
			var snapshot = await ReadSnapshotAsync(configPath).ConfigureAwait(false);
			fingerprintParts.Add(Fingerprint(root, configPath, snapshot.FingerprintValue));
			var parsed = snapshot.State == DependencyConfigurationState.Valid
				? ParseTypeScriptConfig(snapshot.Content)
				: ConfigurationParseResult<TypeScriptConfiguration>.Failure(
					TypeScriptConfiguration.Default, snapshot.State, snapshot.Reason);
			var directory = Path.GetDirectoryName(configPath)!;
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
			fingerprintParts.Add(Fingerprint(root, configPath, snapshot.FingerprintValue));
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
				.ToArray()
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
				.Select(value => Path.GetFullPath(Path.Combine(directory, value!)))
				.Distinct(PathComparer).Order(StringComparer.Ordinal).ToArray());
		}
		catch (System.Xml.XmlException exception)
		{
			return ConfigurationParseResult<string[]>.Failure(
				[], DependencyConfigurationState.Corrupt, OneLine(exception.Message));
		}
	}

	private static ConfigurationParseResult<TypeScriptConfiguration> ParseTypeScriptConfig(string content)
	{
		try
		{
			using var document = JsonDocument.Parse(content, new JsonDocumentOptions
			{
				AllowTrailingCommas = true,
				CommentHandling = JsonCommentHandling.Skip
			});
			if (document.RootElement.ValueKind != JsonValueKind.Object)
				return ConfigurationParseResult<TypeScriptConfiguration>.Failure(
					TypeScriptConfiguration.Default,
					DependencyConfigurationState.UnsupportedSemantics,
					"tsconfig root must be an object");
			if (!document.RootElement.TryGetProperty("compilerOptions", out var options))
				return ConfigurationParseResult<TypeScriptConfiguration>.Valid(TypeScriptConfiguration.Default);
			if (options.ValueKind != JsonValueKind.Object)
				return ConfigurationParseResult<TypeScriptConfiguration>.Failure(
					TypeScriptConfiguration.Default,
					DependencyConfigurationState.Corrupt,
					"tsconfig compilerOptions must be an object");
			if (options.TryGetProperty("moduleResolution", out var mode) && mode.ValueKind != JsonValueKind.String)
				return ConfigurationParseResult<TypeScriptConfiguration>.Failure(
					TypeScriptConfiguration.Default,
					DependencyConfigurationState.UnsupportedSemantics,
					"tsconfig compilerOptions.moduleResolution must be a string");
			var moduleResolution = options.TryGetProperty("moduleResolution", out mode)
				? mode.GetString() ?? "bundler"
				: "bundler";
			var legacy = moduleResolution.Equals("node10", StringComparison.OrdinalIgnoreCase) ||
			             moduleResolution.Equals("node", StringComparison.OrdinalIgnoreCase) ||
			             options.TryGetProperty("baseUrl", out _);
			var allowJavaScript = options.TryGetProperty("allowJs", out var allowJs) &&
			                      allowJs.ValueKind is JsonValueKind.True;
			var paths = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
			if (options.TryGetProperty("paths", out var mappings) && mappings.ValueKind != JsonValueKind.Object)
				return ConfigurationParseResult<TypeScriptConfiguration>.Failure(
					TypeScriptConfiguration.Default,
					DependencyConfigurationState.UnsupportedSemantics,
					"tsconfig compilerOptions.paths must be an object");
			if (mappings.ValueKind == JsonValueKind.Object)
			{
				foreach (var mapping in mappings.EnumerateObject())
				{
					if (mapping.Value.ValueKind != JsonValueKind.Array ||
					    mapping.Value.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String))
						return ConfigurationParseResult<TypeScriptConfiguration>.Failure(
							TypeScriptConfiguration.Default,
							DependencyConfigurationState.UnsupportedSemantics,
							$"tsconfig path mapping '{mapping.Name}' must be an array of strings");
					paths[mapping.Name] = mapping.Value.EnumerateArray()
						.Select(static item => item.GetString()!).ToArray();
				}
			}
			return ConfigurationParseResult<TypeScriptConfiguration>.Valid(
				new TypeScriptConfiguration(moduleResolution, legacy, paths, allowJavaScript));
		}
		catch (JsonException exception)
		{
			return ConfigurationParseResult<TypeScriptConfiguration>.Failure(
				TypeScriptConfiguration.Default,
				DependencyConfigurationState.Corrupt,
				"invalid tsconfig JSON: " + OneLine(exception.Message));
		}
	}

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
				UnavailablePackageMap(directory, DependencyConfigurationState.Corrupt, OneLine(exception.Message)),
				DependencyConfigurationState.Corrupt,
				OneLine(exception.Message));
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
			result["."] = UnsupportedPackageTarget($"{property} must be a string, null, or object");
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
			return UnsupportedPackageTarget($"package target kind {value.ValueKind} is not supported");
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
	string FingerprintValue);

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
					currentLastWrite);
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
		catch (FileNotFoundException exception)
		{
			return Failure(DependencyConfigurationState.Missing, OneLine(exception.Message), 0, 0);
		}
		catch (DirectoryNotFoundException exception)
		{
			return Failure(DependencyConfigurationState.Missing, OneLine(exception.Message), 0, 0);
		}
		catch (DecoderFallbackException exception)
		{
			return Failure(DependencyConfigurationState.Corrupt, OneLine(exception.Message), 0, 0);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return Failure(DependencyConfigurationState.Corrupt, OneLine(exception.Message), 0, 0);
		}
	}

	private static DependencyControlFileSnapshot Failure(
		DependencyConfigurationState state,
		string reason,
		long length,
		long lastWrite) => new(state, string.Empty, reason, $"{state}:{length}:{lastWrite}:{reason}");

	private static string Hash(ReadOnlySpan<byte> bytes) =>
		Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

	private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
