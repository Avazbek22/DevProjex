using System.Text.Json;
using System.Text.Json.Serialization;
using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;
using DevProjex.Kernel;

var options = Options.Parse(args);
var root = Path.GetFullPath(options.Root);
var languages = options.Languages.Select(ParseLanguage).ToHashSet();
var manifest = EnumerateManifest(root, languages, options.ExcludedDirectories);

using var engine = new DependencyFactsEngine(
	new TreeSitterDependencyFactExtractor(),
	new FileDependencyConfigurationProvider());
var snapshot = await engine.IndexAsync(root, manifest);
var related = await engine.FindRelatedAsync(
	root,
	manifest,
	options.Samples,
	DependencyDirection.Both);

var languageFiles = snapshot.Files
	.Where(file => languages.Contains(file.LanguageId))
	.OrderBy(static file => file.Path, StringComparer.Ordinal)
	.ToArray();
var languagePaths = languageFiles.Select(static file => file.Path).ToHashSet(StringComparer.Ordinal);
var languageEdges = snapshot.Edges.Where(edge => languagePaths.Contains(edge.Source)).ToArray();
var probes = options.Probes is null
	? []
	: JsonSerializer.Deserialize<RelationProbe[]>(
		await File.ReadAllTextAsync(options.Probes),
		new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
var relationProbes = probes.Select(probe =>
{
	var matching = languageEdges.Where(edge =>
		string.Equals(edge.Source, probe.Source, StringComparison.Ordinal) &&
		string.Equals(edge.Reference, probe.Reference, StringComparison.Ordinal)).ToArray();
	var confirmed = matching.Any(edge =>
		edge.Status == ResolutionStatus.Resolved &&
		string.Equals(edge.Target, probe.Target, StringComparison.Ordinal));
	var sourceStatus = snapshot.FileByPath.GetValueOrDefault(probe.Source)?.Status;
	var sourcePartial = snapshot.FileByPath.GetValueOrDefault(probe.Source)?.PartialParse;
	var lineWasDropped = probe.Line > 0 && sourcePartial?.Ranges.Any(range =>
		range.StartLine <= probe.Line && range.EndLine >= probe.Line) == true;
	var state = confirmed
		? "confirmed"
		: sourceStatus is not DependencyFileStatus.Supported || matching.Length > 0 || lineWasDropped
			? "missed-honestly"
			: "missed-silently";
	return new
	{
		probe.Seed,
		probe.Direction,
		path = probe.Direction == "dependencies" ? probe.Target : probe.Source,
		probe.Source,
		probe.Target,
		probe.Reference,
		probe.Line,
		probe.Evidence,
		engineState = state
	};
}).ToArray();
var report = new
{
	root,
	manifestFiles = manifest.Count,
	languages = languages.OrderBy(static language => language.ToString(), StringComparer.Ordinal),
	files = new
	{
		total = languageFiles.Length,
		supported = languageFiles.Count(static file => file.Status == DependencyFileStatus.Supported),
		failed = languageFiles.Count(static file => file.Status == DependencyFileStatus.ExtractionFailed),
		partial = languageFiles.Count(static file => file.PartialParse is not null),
		declarations = languageFiles.Sum(static file => file.Declarations.Count),
		imports = languageFiles.Sum(static file => file.Imports.Count),
		references = languageFiles.Sum(static file => file.References.Count),
		navigation = languageFiles.Sum(static file => file.NavigationDeclarations.Count)
	},
	edges = new
	{
		total = languageEdges.Length,
		resolved = languageEdges.Count(static edge => edge.Status == ResolutionStatus.Resolved),
		ambiguous = languageEdges.Count(static edge => edge.Status == ResolutionStatus.Ambiguous),
		unresolved = languageEdges.Count(static edge => edge.Status == ResolutionStatus.Unresolved),
		external = languageEdges.Count(static edge => edge.Status == ResolutionStatus.External)
	},
	relationProbes,
	partialParse = languageFiles
		.Where(static file => file.PartialParse is not null)
		.Select(static file => file.PartialParse)
		.ToArray(),
	failures = languageFiles
		.Where(static file => file.Status == DependencyFileStatus.ExtractionFailed)
		.Select(static file => new { file.Path, file.StatusReason, file.ErrorNodeKinds })
		.ToArray(),
	samples = related.Seeds.Select(seed => new
	{
		seed.Seed,
		seed.LanguageId,
		seed.NoFactsReason,
		facts = snapshot.FileByPath.TryGetValue(seed.Seed, out var facts)
			? new
			{
				facts.Status,
				facts.StatusReason,
				facts.HasSyntaxErrors,
				facts.PartialParse,
				declarations = facts.Declarations.Count,
				imports = facts.Imports.Count,
				references = facts.References.Count,
				navigation = facts.NavigationDeclarations.Count
			}
			: null,
		seed.Dependencies,
		seed.Dependents,
		evidence = languageEdges
			.Where(edge => string.Equals(edge.Source, seed.Seed, StringComparison.Ordinal) ||
			               string.Equals(edge.Target, seed.Seed, StringComparison.Ordinal) ||
			               edge.DeclarationFiles.Contains(seed.Seed, StringComparer.Ordinal))
			.OrderBy(static edge => edge.Source, StringComparer.Ordinal)
			.ThenBy(static edge => edge.Target, StringComparer.Ordinal)
			.ThenBy(static edge => edge.Reference, StringComparer.Ordinal)
			.Select(static edge => new
			{
				edge.Source,
				edge.Target,
				edge.Status,
				edge.Reference,
				edge.Reasons,
				edge.Candidates,
				edge.DeclarationFiles,
				edge.Evidence
			})
			.ToArray()
	}).ToArray()
};

var jsonOptions = new JsonSerializerOptions
{
	WriteIndented = true,
	PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
};
var json = JsonSerializer.Serialize(report, jsonOptions) + Environment.NewLine;
if (options.Output is null)
	Console.Write(json);
else
{
	var output = Path.GetFullPath(options.Output);
	Directory.CreateDirectory(Path.GetDirectoryName(output)!);
	await File.WriteAllTextAsync(output, json);
}

static IReadOnlyList<string> EnumerateManifest(
	string root,
	IReadOnlySet<LanguageId> languages,
	IReadOnlySet<string> excludedDirectories)
{
	var files = new List<string>();
	var pending = new Stack<string>();
	pending.Push(root);
	while (pending.TryPop(out var directory))
	{
		foreach (var child in Directory.EnumerateDirectories(directory).OrderByDescending(static path => path, StringComparer.Ordinal))
		{
			var name = Path.GetFileName(child);
			if (name == ".git" || excludedDirectories.Contains(name)) continue;
			pending.Push(child);
		}
		foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
			if (IsSource(file, languages) || IsControlFile(file))
				files.Add(Path.GetFullPath(file));
	}
	files.Sort(PathComparer.Default);
	return files;
}

static bool IsSource(string path, IReadOnlySet<LanguageId> languages)
{
	var extension = Path.GetExtension(path).ToLowerInvariant();
	return languages.Any(language => language switch
	{
		LanguageId.CSharp => extension == ".cs",
		LanguageId.Go => extension == ".go",
		LanguageId.JavaScript => extension is ".js" or ".jsx" or ".mjs" or ".cjs",
		LanguageId.TypeScript => extension == ".ts",
		LanguageId.Tsx => extension == ".tsx",
		LanguageId.Python => extension == ".py",
		LanguageId.Java => extension == ".java",
		LanguageId.Kotlin => extension is ".kt" or ".kts",
		LanguageId.Rust => extension == ".rs",
		LanguageId.Ruby => extension is ".rb" or ".rake" or ".gemspec" or ".ru",
		LanguageId.Php => extension == ".php",
		LanguageId.C => extension is ".c" or ".h",
		LanguageId.Cpp => extension is ".cc" or ".cpp" or ".cxx" or ".hh" or ".hpp" or ".hxx" or ".h",
		_ => false
	});
}

static bool IsControlFile(string path)
{
	var name = Path.GetFileName(path);
	return name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) ||
	       name.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
	       name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) ||
	       name.Equals("setup.cfg", StringComparison.OrdinalIgnoreCase) ||
	       name.Equals("pom.xml", StringComparison.OrdinalIgnoreCase) ||
	       name.Equals("build.gradle", StringComparison.OrdinalIgnoreCase) ||
	       name.Equals("build.gradle.kts", StringComparison.OrdinalIgnoreCase) ||
	       name.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase) ||
	       name.Equals("Gemfile", StringComparison.OrdinalIgnoreCase) ||
	       name.Equals("composer.json", StringComparison.OrdinalIgnoreCase) ||
	       name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
	       name.EndsWith(".gemspec", StringComparison.OrdinalIgnoreCase) ||
	       name.StartsWith("tsconfig", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
	       name.StartsWith("jsconfig", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
}

static LanguageId ParseLanguage(string value) => value.ToLowerInvariant() switch
{
	"csharp" => LanguageId.CSharp,
	"go" => LanguageId.Go,
	"javascript" => LanguageId.JavaScript,
	"typescript" => LanguageId.TypeScript,
	"tsx" => LanguageId.Tsx,
	"python" => LanguageId.Python,
	"java" => LanguageId.Java,
	"kotlin" => LanguageId.Kotlin,
	"rust" => LanguageId.Rust,
	"ruby" => LanguageId.Ruby,
	"php" => LanguageId.Php,
	"c" => LanguageId.C,
	"cpp" => LanguageId.Cpp,
	_ => throw new ArgumentException($"Unsupported language '{value}'.")
};

internal sealed record Options(
	string Root,
	string[] Languages,
	string[] Samples,
	IReadOnlySet<string> ExcludedDirectories,
	string? Probes,
	string? Output)
{
	public static Options Parse(string[] values)
	{
		var root = Required(values, "--root");
		var languages = Required(values, "--languages")
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		var samples = Required(values, "--samples")
			.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(static path => path.Replace('\\', '/'))
			.ToArray();
		var excluded = (Optional(values, "--exclude-directories") ?? "node_modules;bin;obj;artifacts;build;.venv;venv")
			.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		return new Options(root, languages, samples, excluded, Optional(values, "--probes"), Optional(values, "--output"));
	}

	private static string Required(string[] values, string name) =>
		Optional(values, name) ?? throw new ArgumentException($"Missing {name}.");

	private static string? Optional(string[] values, string name)
	{
		var index = Array.IndexOf(values, name);
		return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
	}
}

internal sealed record RelationProbe(
	string Seed,
	string Direction,
	string Source,
	string Target,
	string Reference,
	int Line,
	string Evidence);
