using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DependencyFactsSpike;

internal sealed record ScopeDescriptor(
	string Id,
	string Root,
	LanguageId Language,
	IReadOnlyList<string> ProjectReferences,
	string? ModuleResolution,
	bool LegacyTypeScriptConfiguration,
	string? PackageName,
	IReadOnlyDictionary<string, IReadOnlyList<string>> TypeScriptPaths,
	string? RootDir,
	string? OutDir,
	IReadOnlySet<string> PythonExternalPackages);

internal sealed partial class ProjectMap
{
	private readonly string _repositoryRoot;
	private readonly IReadOnlyList<ScopeDescriptor> _scopes;

	private ProjectMap(string repositoryRoot, IReadOnlyList<ScopeDescriptor> scopes)
	{
		_repositoryRoot = repositoryRoot;
		_scopes = scopes;
	}

	public IReadOnlyList<ScopeDescriptor> Scopes => _scopes;

	public static ProjectMap Build(string repositoryRoot)
	{
		var scopes = new List<ScopeDescriptor>();
		foreach (var project in Directory.EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
			.Where(static path => !PathPolicy.IsExcluded(path)))
		{
			var document = XDocument.Load(project, LoadOptions.None);
			var references = document.Descendants()
				.Where(static element => element.Name.LocalName == "ProjectReference")
				.Select(element => element.Attribute("Include")?.Value)
				.Where(static value => !string.IsNullOrWhiteSpace(value))
				.Select(value => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, value!)))
				.Select(path => Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'))
				.OrderBy(static value => value, StringComparer.Ordinal)
				.ToArray();
			scopes.Add(new ScopeDescriptor(
				Path.GetRelativePath(repositoryRoot, project).Replace('\\', '/'),
				Path.GetDirectoryName(project)!,
				LanguageId.CSharp,
				references,
				null,
				false,
				null,
				new Dictionary<string, IReadOnlyList<string>>(),
				null,
				null,
				new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
		}

		foreach (var config in Directory.EnumerateFiles(repositoryRoot, "tsconfig*.json", SearchOption.AllDirectories)
			.Where(static path => !PathPolicy.IsExcluded(path)))
		{
			try
			{
				using var document = JsonDocument.Parse(File.ReadAllText(config), new JsonDocumentOptions
				{
					AllowTrailingCommas = true,
					CommentHandling = JsonCommentHandling.Skip
				});
				var compiler = document.RootElement.TryGetProperty("compilerOptions", out var options) ? options : default;
				var moduleResolution = TryString(compiler, "moduleResolution") ?? "bundler";
				var hasBaseUrl = TryString(compiler, "baseUrl") is not null;
				var paths = ParsePaths(compiler);
				var directory = Path.GetDirectoryName(config)!;
				var package = FindNearestFile(directory, repositoryRoot, "package.json");
				scopes.Add(new ScopeDescriptor(
					Path.GetRelativePath(repositoryRoot, config).Replace('\\', '/'),
					directory,
					LanguageId.TypeScript,
					[],
					moduleResolution,
					moduleResolution.Equals("node10", StringComparison.OrdinalIgnoreCase) || hasBaseUrl,
					package is null ? null : ReadPackageName(package),
					paths,
					TryString(compiler, "rootDir"),
					TryString(compiler, "outDir"),
					new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
			}
			catch (JsonException)
			{
				// Invalid configuration remains observable as a missing owner during resolution.
			}
		}

		var pythonMarkers = new[] { "pyproject.toml", "setup.cfg" }
			.SelectMany(name => Directory.EnumerateFiles(repositoryRoot, name, SearchOption.AllDirectories))
			.Where(static path => !PathPolicy.IsExcluded(path))
			.OrderBy(static path => path, StringComparer.Ordinal)
			.ToArray();
		foreach (var marker in pythonMarkers)
		{
			var directory = Path.GetDirectoryName(marker)!;
			scopes.Add(new ScopeDescriptor(
				Path.GetRelativePath(repositoryRoot, marker).Replace('\\', '/'),
				directory,
				LanguageId.Python,
				[], null, false, null, new Dictionary<string, IReadOnlyList<string>>(), null, null,
				ReadPythonExternalPackages(marker)));
		}

		return new ProjectMap(repositoryRoot, scopes
			.OrderByDescending(static scope => scope.Root.Length)
			.ThenBy(static scope => scope.Id, StringComparer.Ordinal)
			.ToArray());
	}

	public string ScopeFor(string file, LanguageId language)
	{
		var normalizedFile = Path.GetFullPath(file);
		var family = language is LanguageId.TypeScript or LanguageId.JavaScript or LanguageId.Tsx
			? LanguageId.TypeScript
			: language;
		var scope = _scopes.FirstOrDefault(candidate =>
			candidate.Language == family && PathPolicy.IsWithin(candidate.Root, normalizedFile));
		return scope?.Id ?? $"root:{family.ToString().ToLowerInvariant()}";
	}

	public ScopeDescriptor? Descriptor(string id) => _scopes.FirstOrDefault(scope => scope.Id == id);

	private static IReadOnlyDictionary<string, IReadOnlyList<string>> ParsePaths(JsonElement compiler)
	{
		var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
		if (compiler.ValueKind != JsonValueKind.Object ||
		    !compiler.TryGetProperty("paths", out var paths) ||
		    paths.ValueKind != JsonValueKind.Object)
			return result;
		foreach (var property in paths.EnumerateObject())
		{
			if (property.Value.ValueKind != JsonValueKind.Array)
				continue;
			result[property.Name] = property.Value.EnumerateArray()
				.Where(static item => item.ValueKind == JsonValueKind.String)
				.Select(static item => item.GetString()!)
				.ToArray();
		}
		return result;
	}

	private static string? TryString(JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
			? property.GetString()
			: null;

	private static string? FindNearestFile(string start, string stop, string name)
	{
		for (var directory = start; PathPolicy.IsWithin(stop, directory); directory = Path.GetDirectoryName(directory)!)
		{
			var candidate = Path.Combine(directory, name);
			if (File.Exists(candidate))
				return candidate;
			if (string.Equals(Path.GetFullPath(directory), Path.GetFullPath(stop), StringComparison.OrdinalIgnoreCase))
				break;
		}
		return null;
	}

	private static string? ReadPackageName(string packageJson)
	{
		try
		{
			using var document = JsonDocument.Parse(File.ReadAllText(packageJson));
			return TryString(document.RootElement, "name");
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static IReadOnlySet<string> ReadPythonExternalPackages(string marker)
	{
		var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var line in File.ReadLines(marker))
		{
			if (line.Contains("::", StringComparison.Ordinal) || line.Contains("://", StringComparison.Ordinal))
				continue;
			foreach (Match match in PythonRequirementRegex().Matches(line))
			{
				var name = match.Groups["name"].Value.Replace('-', '_');
				if (name.Length > 0)
					packages.Add(name);
			}
		}
		return packages;
	}

	[GeneratedRegex("[\\\"'](?<name>[A-Za-z0-9_.-]+)(?:\\[[^\\]]+\\])?(?=[<>=!~\\\"'])", RegexOptions.CultureInvariant)]
	private static partial Regex PythonRequirementRegex();
}

internal static class PathPolicy
{
	private static readonly HashSet<string> ExcludedSegments = new(
		[".git", "node_modules", ".venv", "venv", "site-packages", "bin", "obj", "dist", "build", "coverage"],
		StringComparer.OrdinalIgnoreCase);

	public static bool IsExcluded(string path) =>
		path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(ExcludedSegments.Contains);

	public static bool IsWithin(string root, string candidate)
	{
		var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
		return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
	}
}
