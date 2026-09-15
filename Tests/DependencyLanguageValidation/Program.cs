using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

if (args.Length < 3) throw new ArgumentException("Expected a repository root, language name and output path.");
var root = Path.GetFullPath(args[0]);
var languages = args[1].Split(',').Select(value => Enum.Parse<LanguageId>(value, true)).ToHashSet();
var extensions = languages.SelectMany(language => language.ToString() switch
{
	"Bash" => new[] { ".sh", ".bash" },
	"Scala" => new[] { ".scala", ".sc" },
	"CSharp" => new[] { ".cs" },
	"TypeScript" => new[] { ".ts", ".mts", ".cts" },
	"JavaScript" => new[] { ".js", ".mjs", ".cjs", ".jsx" },
	"Tsx" => new[] { ".tsx", ".jsx" },
	"Python" => new[] { ".py", ".pyi" },
	"Go" => new[] { ".go" },
	"Java" => new[] { ".java" },
	"Kotlin" => new[] { ".kt", ".kts" },
	"Rust" => new[] { ".rs" },
	"Ruby" => new[] { ".rb", ".rake", ".gemspec", ".ru" },
	"Php" => new[] { ".php", ".phtml" },
	"C" => new[] { ".c", ".h" },
	"Cpp" => new[] { ".cc", ".cpp", ".cxx", ".hh", ".hpp", ".hxx", ".h" },
	_ => throw new ArgumentException("The requested language has no source bindings.")
}).ToHashSet(StringComparer.OrdinalIgnoreCase);
var files = new List<string>();
var excluded = args.Skip(3).Where(value => value.StartsWith("--exclude=", StringComparison.Ordinal))
	.Select(value => value["--exclude=".Length..]).ToHashSet(StringComparer.Ordinal);
var pending = new Stack<string>();
pending.Push(root);
while (pending.TryPop(out var directory))
{
	foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
	{
		if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
		if (entry is DirectoryInfo)
		{
			if (entry.Name is not (".git" or "target" or "node_modules" or "bin" or "obj")) pending.Push(entry.FullName);
		}
		else if (!excluded.Contains(Path.GetRelativePath(root, entry.FullName).Replace('\\', '/')) &&
			(extensions.Contains(entry.Extension) || IsConfiguration(entry.Name)))
			files.Add(entry.FullName);
	}
}
using var engine = new DependencyFactsEngine(new TreeSitterDependencyFactExtractor(), new FileDependencyConfigurationProvider());
var index = await engine.IndexAsync(root, files);
var languageFiles = index.Files.Where(file => languages.Contains(file.LanguageId)).ToArray();
var paths = languageFiles.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
var edges = index.Edges.Where(edge => paths.Contains(edge.Source)).ToArray();
var sources = index.Files.SelectMany(file => file.Imports.Where(import => import.ImportedName is "source" or ".")
	.Select(import => new { file.Path, import.Specifier, import.Status, import.Reason, import.Site })).ToArray();
var samples = args.Skip(3).ToHashSet(StringComparer.Ordinal);
var report = JsonSerializer.Serialize(new
{
	files = languageFiles.Length,
	withFacts = languageFiles.Count(file => file.Declarations.Count + file.Imports.Count + file.References.Count > 0),
	failed = index.Files.Where(file => file.Status == DependencyFileStatus.ExtractionFailed).Select(file => new { file.Path, file.StatusReason }),
	declarations = index.Declarations.Count(declaration => languages.Contains(declaration.Identity.LanguageId)),
	edges = edges.GroupBy(edge => edge.Status).ToDictionary(group => group.Key.ToString(), group => group.Count()),
	edgeHash = Digest(edges),
	factsHash = Digest(languageFiles.Select(file => new
	{
		file.Path,
		file.ScopeId,
		file.ContentFingerprint,
		file.Status,
		file.Declarations,
		file.Imports,
		file.References,
		file.NavigationDeclarations,
		file.ContextNamespaces,
		file.Aliases,
		file.GlobalContextNamespaces,
		file.GlobalAliases,
		file.TypeParameters,
		file.TypeParameterScopes,
		file.CSharpUsingDirectives
	})),
	resolvedEdges = edges.Where(edge => edge.Status == ResolutionStatus.Resolved),
	sources,
	samples = index.Files.Where(file => samples.Contains(file.Path)).Select(file => new
	{
		file.Path,
		file.NavigationDeclarations,
		file.Imports,
		file.References,
		edges = index.Edges.Where(edge => edge.Source == file.Path)
	})
}, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
await File.WriteAllTextAsync(args[2], report);
Console.WriteLine($"Files={index.Files.Count}; edges={index.Edges.Count}; output={args[2]}");

bool IsConfiguration(string name) => name is "CMakeLists.txt" or "package.json" or "pyproject.toml" or "setup.cfg" or
	"pom.xml" or "build.gradle" or "build.gradle.kts" or "Cargo.toml" or "Gemfile" or "composer.json" ||
	name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".gemspec", StringComparison.OrdinalIgnoreCase) ||
	(name.StartsWith("tsconfig", StringComparison.OrdinalIgnoreCase) || name.StartsWith("jsconfig", StringComparison.OrdinalIgnoreCase)) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
	languages.Any(language => language.ToString() == "Scala") && name is "build.sbt" or "build.sc";

static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
