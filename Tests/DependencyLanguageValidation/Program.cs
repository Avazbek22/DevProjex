using System.Text.Json;
using System.Text.Json.Serialization;
using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

if (args.Length < 3) throw new ArgumentException("Expected a repository root, language name and output path.");
var root = Path.GetFullPath(args[0]);
var language = Enum.Parse<LanguageId>(args[1], true);
var extensions = language == LanguageId.Bash ? new[] { ".sh", ".bash" } : new[] { ".scala", ".sc" };
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
			(extensions.Contains(entry.Extension, StringComparer.OrdinalIgnoreCase) || entry.Name is "build.sbt" or "build.sc"))
			files.Add(entry.FullName);
	}
}
using var engine = new DependencyFactsEngine(new TreeSitterDependencyFactExtractor(), new FileDependencyConfigurationProvider());
var index = await engine.IndexAsync(root, files);
var sources = index.Files.SelectMany(file => file.Imports.Where(import => import.ImportedName is "source" or ".")
	.Select(import => new { file.Path, import.Specifier, import.Status, import.Reason, import.Site })).ToArray();
var samples = args.Skip(3).ToHashSet(StringComparer.Ordinal);
var report = JsonSerializer.Serialize(new
{
	files = index.Files.Count,
	withFacts = index.Files.Count(file => file.Declarations.Count + file.Imports.Count + file.References.Count > 0),
	failed = index.Files.Where(file => file.Status == DependencyFileStatus.ExtractionFailed).Select(file => new { file.Path, file.StatusReason }),
	declarations = index.Declarations.Count,
	edges = index.Edges.GroupBy(edge => edge.Status).ToDictionary(group => group.Key.ToString(), group => group.Count()),
	resolvedEdges = index.Edges.Where(edge => edge.Status == ResolutionStatus.Resolved),
	sources,
	samples = index.Files.Where(file => samples.Contains(file.Path)).Select(file => new
	{
		file.Path, file.NavigationDeclarations, file.Imports, file.References,
		edges = index.Edges.Where(edge => edge.Source == file.Path)
	})
}, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
await File.WriteAllTextAsync(args[2], report);
Console.WriteLine($"Files={index.Files.Count}; edges={index.Edges.Count}; output={args[2]}");
