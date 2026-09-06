using System.Security.Cryptography;
using System.Text;
using DevProjex.Infrastructure.Compression;
using TreeSitter;

namespace DependencyFactsSpike;

internal sealed class TreeSitterFactExtractor : IDisposable
{
	private readonly EmbeddedGrammarLibraryLocator _locator;
	private readonly Dictionary<LanguageId, LanguageRuntime> _runtimes = [];

	public TreeSitterFactExtractor(string grammarCache)
	{
		_locator = new EmbeddedGrammarLibraryLocator(
			typeof(TreeSitterCodeCompressor).Assembly,
			CodeCompressionFactory.EmbeddedResourcePrefix,
			grammarCache);
	}

	public FileFacts Extract(string root, string fullPath, ProjectMap projects)
	{
		var relativePath = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
		var languageId = LanguageCatalog.ForPath(fullPath);
		var source = File.ReadAllText(fullPath, Encoding.UTF8);
		var runtime = GetRuntime(languageId);
		using var tree = runtime.Parser.Parse(source)
		                 ?? throw new InvalidOperationException($"Tree-sitter returned no tree for '{relativePath}'.");

		var declarationCaptures = Capture(runtime.Declarations, tree.RootNode);
		var referenceCaptures = Capture(runtime.References, tree.RootNode);
		var context = new ExtractionContext(
			root,
			relativePath,
			projects.ScopeFor(fullPath, languageId),
			languageId,
			source,
			tree.RootNode.HasError,
			declarationCaptures,
			referenceCaptures);
		return LanguageCatalog.Adapter(languageId).Extract(context);
	}

	private LanguageRuntime GetRuntime(LanguageId languageId)
	{
		if (_runtimes.TryGetValue(languageId, out var runtime))
			return runtime;
		var definition = LanguageCatalog.Definition(languageId);
		var language = new Language(_locator.Resolve(definition.Library), definition.Export);
		var parser = new Parser(language);
		var queryDirectory = Path.Combine(AppContext.BaseDirectory, "queries", definition.QueryDirectory);
		runtime = new LanguageRuntime(
			language,
			parser,
			new Query(language, File.ReadAllText(Path.Combine(queryDirectory, "declarations.scm"))),
			new Query(language, File.ReadAllText(Path.Combine(queryDirectory, "references.scm"))));
		_runtimes.Add(languageId, runtime);
		return runtime;
	}

	private static IReadOnlyList<SyntaxCapture> Capture(Query query, Node root)
	{
		using var cursor = query.Execute(root);
		return cursor.Captures
			.Select(static capture => new SyntaxCapture(
				capture.Name,
				capture.Node.Type,
				capture.Node.Text,
				checked((int)capture.Node.StartPosition.Row + 1),
				checked((int)capture.Node.StartIndex),
				checked((int)capture.Node.EndIndex)))
			.OrderBy(static capture => capture.StartIndex)
			.ThenBy(static capture => capture.Name, StringComparer.Ordinal)
			.ToArray();
	}

	public void Dispose()
	{
		foreach (var runtime in _runtimes.Values)
			runtime.Dispose();
		_locator.Dispose();
	}

	private sealed class LanguageRuntime(
		Language language,
		Parser parser,
		Query declarations,
		Query references) : IDisposable
	{
		public Parser Parser { get; } = parser;
		public Query Declarations { get; } = declarations;
		public Query References { get; } = references;

		public void Dispose()
		{
			References.Dispose();
			Declarations.Dispose();
			Parser.Dispose();
			language.Dispose();
		}
	}
}

internal sealed record SyntaxCapture(
	string Name,
	string NodeType,
	string Text,
	int Line,
	int StartIndex,
	int EndIndex);

internal sealed record ExtractionContext(
	string Root,
	string RelativePath,
	string ScopeId,
	LanguageId Language,
	string Source,
	bool HasSyntaxErrors,
	IReadOnlyList<SyntaxCapture> Declarations,
	IReadOnlyList<SyntaxCapture> References)
{
	public string ContentHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Source))).ToLowerInvariant();
}

internal sealed record LanguageDefinition(
	string Library,
	string Export,
	string QueryDirectory,
	IReadOnlySet<string> Extensions);

internal interface ILanguageFactAdapter
{
	FileFacts Extract(ExtractionContext context);
}

internal static class LanguageCatalog
{
	private static readonly LanguageDefinition CSharp = new(
		"tree-sitter-c-sharp", "tree_sitter_c_sharp", "csharp", new HashSet<string>([".cs"], StringComparer.OrdinalIgnoreCase));
	private static readonly LanguageDefinition TypeScript = new(
		"tree-sitter-typescript", "tree_sitter_typescript", "typescript", new HashSet<string>([".ts", ".mts", ".cts"], StringComparer.OrdinalIgnoreCase));
	private static readonly LanguageDefinition JavaScript = new(
		"tree-sitter-javascript", "tree_sitter_javascript", "javascript", new HashSet<string>([".js", ".mjs", ".cjs"], StringComparer.OrdinalIgnoreCase));
	private static readonly LanguageDefinition Tsx = new(
		"tree-sitter-tsx", "tree_sitter_tsx", "typescript", new HashSet<string>([".tsx", ".jsx"], StringComparer.OrdinalIgnoreCase));
	private static readonly LanguageDefinition Python = new(
		"tree-sitter-python", "tree_sitter_python", "python", new HashSet<string>([".py", ".pyi"], StringComparer.OrdinalIgnoreCase));
	private static readonly IReadOnlyDictionary<LanguageId, LanguageDefinition> Definitions =
		new Dictionary<LanguageId, LanguageDefinition>
		{
			[LanguageId.CSharp] = CSharp,
			[LanguageId.TypeScript] = TypeScript,
			[LanguageId.JavaScript] = JavaScript,
			[LanguageId.Tsx] = Tsx,
			[LanguageId.Python] = Python
		};
	private static readonly CSharpFactAdapter CSharpAdapter = new();
	private static readonly TypeScriptFactAdapter TypeScriptAdapter = new();
	private static readonly PythonFactAdapter PythonAdapter = new();

	public static LanguageDefinition Definition(LanguageId id) => Definitions[id];

	public static ILanguageFactAdapter Adapter(LanguageId id) => id switch
	{
		LanguageId.CSharp => CSharpAdapter,
		LanguageId.TypeScript or LanguageId.JavaScript or LanguageId.Tsx => TypeScriptAdapter,
		LanguageId.Python => PythonAdapter,
		_ => throw new ArgumentOutOfRangeException(nameof(id), id, null)
	};

	public static LanguageId ForPath(string path)
	{
		var extension = Path.GetExtension(path);
		foreach (var pair in Definitions)
		{
			if (pair.Value.Extensions.Contains(extension))
				return pair.Key;
		}
		throw new ArgumentException($"Unsupported source file '{path}'.", nameof(path));
	}

	public static bool IsSupported(string path)
	{
		var extension = Path.GetExtension(path);
		return Definitions.Values.Any(definition => definition.Extensions.Contains(extension));
	}
}
