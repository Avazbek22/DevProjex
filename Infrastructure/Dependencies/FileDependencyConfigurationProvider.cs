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

		foreach (var project in manifest.Where(static path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
		{
			var content = await ReadTextAsync(project, cancellationToken).ConfigureAwait(false);
			fingerprintParts.Add(Fingerprint(root, project, content));
			var scope = "csharp:" + PortableRelative(root, project);
			var references = ParseProjectReferences(project, content);
			csharpProjects[project] = (scope, references);
		}
		foreach (var pair in csharpProjects.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
		{
			var references = pair.Value.References
				.Where(csharpProjects.ContainsKey)
				.Select(path => csharpProjects[path].Scope)
				.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
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
				true));
		}

		foreach (var configPath in manifest.Where(IsTypeScriptConfig).Order(StringComparer.Ordinal))
		{
			var content = await ReadTextAsync(configPath, cancellationToken).ConfigureAwait(false);
			fingerprintParts.Add(Fingerprint(root, configPath, content));
			var parsed = ParseTypeScriptConfig(content);
			var directory = Path.GetDirectoryName(configPath)!;
			var package = FindNearestManifestFile(directory, root, "package.json", manifest);
			var packageName = package is null ? null : ReadPackageName(await ReadTextAsync(package, cancellationToken).ConfigureAwait(false));
			scopes.Add(new DependencyScopeDescriptor(
				"typescript:" + PortableRelative(root, configPath),
				directory,
				LanguageId.TypeScript,
				[],
				parsed.ModuleResolution,
				parsed.Legacy,
				parsed.Paths,
				packageName,
				new HashSet<string>(),
				[],
				true));
		}

		foreach (var configPath in manifest.Where(IsPythonConfig).Order(StringComparer.Ordinal))
		{
			var content = await ReadTextAsync(configPath, cancellationToken).ConfigureAwait(false);
			fingerprintParts.Add(Fingerprint(root, configPath, content));
			var directory = Path.GetDirectoryName(configPath)!;
			scopes.Add(new DependencyScopeDescriptor(
				"python:" + PortableRelative(root, configPath),
				directory,
				LanguageId.Python,
				[], null, false,
				new Dictionary<string, IReadOnlyList<string>>(), null,
				ParsePythonDependencies(configPath, content),
				new[] { directory, Path.Combine(directory, "src") },
				true,
				ParsePythonVersion(content)));
		}

		AddFallbackScope(scopes, root, LanguageId.CSharp);
		AddFallbackScope(scopes, root, LanguageId.TypeScript);
		AddFallbackScope(scopes, root, LanguageId.Python);
		var packageMaps = new Dictionary<string, PackageMapDescriptor>(StringComparer.Ordinal);
		foreach (var packagePath in manifest.Where(static path => Path.GetFileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
		{
			var content = await ReadTextAsync(packagePath, cancellationToken).ConfigureAwait(false);
			fingerprintParts.Add(Fingerprint(root, packagePath, content));
			var directory = Path.GetDirectoryName(packagePath)!;
			var descriptor = ParsePackageMap(directory, content);
			packageMaps[PortableRelative(root, directory)] = descriptor;
		}

		return new DependencyResolverConfiguration(
			Hash(fingerprintParts.Order(StringComparer.Ordinal)),
			scopes.OrderBy(static scope => scope.ScopeId, StringComparer.Ordinal).ToArray(),
			packageMaps,
			DotNetCatalog.Value,
			PythonCatalogs.Value,
			NodeCatalog.Value);
	}

	private static IReadOnlySet<string> ParseNodeDependencies(string content)
	{
		var result = new HashSet<string>(StringComparer.Ordinal);
		try
		{
			using var document = JsonDocument.Parse(content, new JsonDocumentOptions
			{
				AllowTrailingCommas = true,
				CommentHandling = JsonCommentHandling.Skip
			});
			foreach (var property in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" })
			{
				if (!document.RootElement.TryGetProperty(property, out var dependencies) ||
				    dependencies.ValueKind != JsonValueKind.Object)
					continue;
				foreach (var dependency in dependencies.EnumerateObject())
					result.Add(dependency.Name);
			}
		}
		catch (JsonException)
		{
		}
		return result;
	}

	private static string[] ParseProjectReferences(string projectPath, string content)
	{
		try
		{
			var directory = Path.GetDirectoryName(projectPath)!;
			return XDocument.Parse(content).Descendants()
				.Where(static element => element.Name.LocalName == "ProjectReference")
				.Select(element => element.Attribute("Include")?.Value)
				.Where(static value => !string.IsNullOrWhiteSpace(value))
				.Select(value => Path.GetFullPath(Path.Combine(directory, value!)))
				.Distinct(PathComparer).Order(StringComparer.Ordinal).ToArray();
		}
		catch (System.Xml.XmlException)
		{
			return [];
		}
	}

	private static TypeScriptConfiguration ParseTypeScriptConfig(string content)
	{
		try
		{
			using var document = JsonDocument.Parse(content, new JsonDocumentOptions
			{
				AllowTrailingCommas = true,
				CommentHandling = JsonCommentHandling.Skip
			});
			if (!document.RootElement.TryGetProperty("compilerOptions", out var options))
				return TypeScriptConfiguration.Default;
			var moduleResolution = options.TryGetProperty("moduleResolution", out var mode)
				? mode.GetString() ?? "bundler"
				: "bundler";
			var legacy = moduleResolution.Equals("node10", StringComparison.OrdinalIgnoreCase) ||
			             moduleResolution.Equals("node", StringComparison.OrdinalIgnoreCase) ||
			             options.TryGetProperty("baseUrl", out _);
			var paths = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
			if (options.TryGetProperty("paths", out var mappings) && mappings.ValueKind == JsonValueKind.Object)
			{
				foreach (var mapping in mappings.EnumerateObject())
					paths[mapping.Name] = mapping.Value.ValueKind == JsonValueKind.Array
						? mapping.Value.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String)
							.Select(static item => item.GetString()!).ToArray()
						: [];
			}
			return new TypeScriptConfiguration(moduleResolution, legacy, paths);
		}
		catch (JsonException)
		{
			return TypeScriptConfiguration.Default;
		}
	}

	private static PackageMapDescriptor ParsePackageMap(string directory, string content)
	{
		try
		{
			using var document = JsonDocument.Parse(content);
			return new PackageMapDescriptor(
				directory,
				document.RootElement.TryGetProperty("name", out var name) ? name.GetString() : null,
				FlattenMap(document.RootElement, "imports"),
				FlattenMap(document.RootElement, "exports"),
				document.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null,
				ParseNodeDependencies(content));
		}
		catch (JsonException)
		{
			return new PackageMapDescriptor(
				directory,
				null,
				new Dictionary<string, string?>(),
				new Dictionary<string, string?>(),
				null,
				new HashSet<string>());
		}
	}

	private static IReadOnlyDictionary<string, string?> FlattenMap(JsonElement root, string property)
	{
		var result = new Dictionary<string, string?>(StringComparer.Ordinal);
		if (!root.TryGetProperty(property, out var map)) return result;
		if (map.ValueKind == JsonValueKind.String || map.ValueKind == JsonValueKind.Null)
		{
			result["."] = SelectConditional(map);
			return result;
		}
		if (map.ValueKind != JsonValueKind.Object) return result;
		if (property == "exports" && !map.EnumerateObject().Any(static item => item.Name.StartsWith(".", StringComparison.Ordinal)))
		{
			result["."] = SelectConditional(map);
			return result;
		}
		foreach (var item in map.EnumerateObject()) result[item.Name] = SelectConditional(item.Value);
		return result;
	}

	private static string? SelectConditional(JsonElement value)
	{
		if (value.ValueKind == JsonValueKind.String) return value.GetString();
		if (value.ValueKind is JsonValueKind.Null or not JsonValueKind.Object) return null;
		foreach (var condition in new[] { "types", "import", "require", "node", "default" })
			if (value.TryGetProperty(condition, out var candidate)) return SelectConditional(candidate);
		return null;
	}

	private static string? ReadPackageName(string content)
	{
		try
		{
			using var document = JsonDocument.Parse(content);
			return document.RootElement.TryGetProperty("name", out var name) ? name.GetString() : null;
		}
		catch (JsonException) { return null; }
	}

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

	private static async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken) =>
		await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
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
			false));
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
	private sealed record TypeScriptConfiguration(string ModuleResolution, bool Legacy, IReadOnlyDictionary<string, IReadOnlyList<string>> Paths)
	{
		public static readonly TypeScriptConfiguration Default = new("bundler", false, new Dictionary<string, IReadOnlyList<string>>());
	}
}
