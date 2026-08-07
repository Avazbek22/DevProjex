using DevProjex.Application.Compression;
using DevProjex.Infrastructure.Compression;
using TreeSitter;

namespace DevProjex.Tests.Unit;

/// <summary>
/// Loads a shipped language pack and its grammar the same way the product does — embedded resource,
/// materialized to app data, loaded by absolute path — so the tests exercise the real delivery path
/// rather than a convenient shortcut.
/// </summary>
internal sealed class CodeCompressionTestHarness : IDisposable
{
	private static readonly IReadOnlyList<CompressionLanguagePack> Packs = CompressionLanguagePack.LoadAll();

	public static IReadOnlyList<string> LanguageIds { get; } =
		Packs.Select(static pack => pack.Id).ToArray();

	public static IReadOnlyList<CompressionLanguagePack> LanguagePacks => Packs;

	private CodeCompressionTestHarness(CompressionLanguagePack pack, Language language, string fixture)
	{
		Pack = pack;
		Language = language;
		Fixture = fixture;
		Parser = new Parser(language);
		Bodies = new Query(language, pack.BodiesQuery);
		Declarations = new Query(language, pack.DeclarationsQuery);
		Docstrings = pack.DocstringsQuery is null ? null : new Query(language, pack.DocstringsQuery);
	}

	public CompressionLanguagePack Pack { get; }

	public Language Language { get; }

	public Parser Parser { get; }

	public Query Bodies { get; }

	public Query Declarations { get; }

	public Query? Docstrings { get; }

	public string Fixture { get; }

	public static TreeSitterCodeCompressor CreateCompressor() => new(CreateLocator());

	public static TreeSitterCodeCompressor CreateCompressor(CompressionLanguagePack pack) =>
		new TreeSitterCodeCompressor(CreateLocator(), [pack]);

	/// <summary>
	/// A pack whose body query captures a container as well as the leaf bodies inside it, which is
	/// what a careless "(_ body: (_) @body)" produces. Used to prove a pack defect costs one file.
	/// </summary>
	public static CompressionLanguagePack PackWithOverlappingBodyQuery(string languageId)
	{
		var pack = Packs.Single(candidate => candidate.Id.Equals(languageId, StringComparison.Ordinal));
		return pack with
		{
			BodiesQuery = pack.BodiesQuery + "\n(class_declaration body: (declaration_list) @body)\n",
			ContainerNodeTypes = new HashSet<string>(StringComparer.Ordinal)
		};
	}

	public static IGrammarLibraryLocator CreateLocator() =>
		new EmbeddedGrammarLibraryLocator(
			typeof(TreeSitterCodeCompressor).Assembly,
			"DevProjex.Grammars/",
			EmbeddedGrammarLibraryLocator.DefaultRootDirectory("tests"));

	public static CodeCompressionTestHarness For(string languageId)
	{
		var pack = Packs.Single(candidate => candidate.Id.Equals(languageId, StringComparison.Ordinal));
		var language = new Language(CreateLocator().Resolve(pack.Library), pack.Export);
		return new CodeCompressionTestHarness(pack, language, FixtureFor(languageId));
	}

	public int CountCaptures(Query query)
	{
		using var tree = Parser.Parse(Fixture)!;
		return query.Execute(tree.RootNode).Captures.Count();
	}

	public IReadOnlyCollection<string> CapturedNodeTypes(Query query)
	{
		using var tree = Parser.Parse(Fixture)!;
		return query.Execute(tree.RootNode).Captures
			.Select(static capture => capture.Node.Type)
			.ToHashSet(StringComparer.Ordinal);
	}

	public static string FixtureFor(string languageId) => languageId switch
	{
		"c" => CodeCompressionFixtures.C,
		"csharp" => CodeCompressionFixtures.CSharp,
		"cpp" => CodeCompressionFixtures.Cpp,
		"go" => CodeCompressionFixtures.Go,
		"java" => CodeCompressionFixtures.Java,
		"javascript" => CodeCompressionFixtures.JavaScript,
		"python" => CodeCompressionFixtures.PythonSource,
		"rust" => CodeCompressionFixtures.Rust,
		"tsx" => CodeCompressionFixtures.Tsx,
		"typescript" => CodeCompressionFixtures.TypeScript,
		_ => throw new ArgumentOutOfRangeException(nameof(languageId), languageId, "No fixture for this language.")
	};

	public void Dispose()
	{
		Docstrings?.Dispose();
		Declarations.Dispose();
		Bodies.Dispose();
		Parser.Dispose();
		Language.Dispose();
	}
}
