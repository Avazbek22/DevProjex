using DevProjex.Application.Dependencies;
using DevProjex.Application.Diff;

namespace DevProjex.Tests.Unit;

public sealed class DependencyChangeFocusTests
{
	private static readonly Func<int, int, bool> Unprotected = static (_, _) => false;

	[Fact]
	public void ChangedLinesPlaceAdditionsAndRemovalsOnNewSideLines()
	{
		var hunk = new UnifiedDiffHunk(10, 3, 10, 3,
		[
			new UnifiedDiffLine(UnifiedDiffLineKind.Context, "a"),
			new UnifiedDiffLine(UnifiedDiffLineKind.Removed, "OLD = 1"),
			new UnifiedDiffLine(UnifiedDiffLineKind.Added, "OLD = 2"),
			new UnifiedDiffLine(UnifiedDiffLineKind.Context, "b")
		]);

		var line = Assert.Single(DependencyChangeFocus.ChangedLines([hunk]));

		Assert.Equal(new DependencyChangedLine(11, "OLD = 2", "OLD = 1"), line);
	}

	[Fact]
	public void PureRemovalAtTheTopIsPlacedOnTheFirstLine()
	{
		var hunk = new UnifiedDiffHunk(1, 1, 0, 0, [new UnifiedDiffLine(UnifiedDiffLineKind.Removed, "LIMIT = 3")]);

		Assert.Equal(1, Assert.Single(DependencyChangeFocus.ChangedLines([hunk])).Line);
	}

	[Fact]
	public void ChangeInsideAMethodNamesTheTopLevelTypeAndTheMember()
	{
		var facts = Facts(LanguageId.Python, declarations:
		[
			Declaration("Client", null, 10, 40, NavigationSymbolKind.Type),
			Declaration("Client.send", "Client", 20, 30, NavigationSymbolKind.Method)
		]);

		var (declarations, unmatched) = DependencyChangeFocus.FindChangedDeclarations(facts, [new DependencyChangedLine(25, "x", null)]);

		var declaration = Assert.Single(declarations);
		Assert.Equal("Client", declaration.Name);
		Assert.Equal(["Client.send"], declaration.Members);
		Assert.Equal(10, declaration.DeclarationLine);
		Assert.Empty(unmatched);
	}

	[Fact]
	public void PythonModuleConstantCountsOnlyWhenTheExtractorRecordedTheAssignment()
	{
		var recorded = Facts(LanguageId.Python, aliases: new Dictionary<string, string> { ["$python-assignment:LIMIT"] = "true" });
		var unrecorded = Facts(LanguageId.Python);
		var changed = new DependencyChangedLine(5, "LIMIT = 10", "LIMIT = 20");

		Assert.Equal("LIMIT", Assert.Single(DependencyChangeFocus.FindChangedDeclarations(recorded, [changed]).Declarations).Name);
		Assert.Equal([5], DependencyChangeFocus.FindChangedDeclarations(unrecorded, [new DependencyChangedLine(5, "LIMIT = 10", null)]).Unmatched);
	}

	[Fact]
	public void RemovedPythonConstantIsReportedWithoutADeclarationLine()
	{
		var declaration = Assert.Single(DependencyChangeFocus.FindChangedDeclarations(
			Facts(LanguageId.Python),
			[new DependencyChangedLine(7, null, "RETRIES = 3")]).Declarations);

		Assert.Equal("RETRIES", declaration.Name);
		Assert.Equal(0, declaration.DeclarationLine);
	}

	[Theory]
	[InlineData("export const LIMIT = 10", "LIMIT")]
	[InlineData("const timeout = 5", "timeout")]
	[InlineData("export let $state = {}", "$state")]
	public void ScriptTopLevelBindingIsADeclaration(string text, string expected)
	{
		var declaration = Assert.Single(DependencyChangeFocus.FindChangedDeclarations(
			Facts(LanguageId.TypeScript),
			[new DependencyChangedLine(3, text, null)]).Declarations);

		Assert.Equal(expected, declaration.Name);
	}

	[Theory]
	[InlineData("    value = 3")]
	[InlineData("# LIMIT = 3")]
	[InlineData("LIMIT == 3")]
	public void IndentedOrNonAssignmentPythonLinesAreUnmatched(string text)
	{
		var facts = Facts(LanguageId.Python, aliases: new Dictionary<string, string> { ["$python-assignment:LIMIT"] = "true" });

		Assert.Empty(DependencyChangeFocus.FindChangedDeclarations(facts, [new DependencyChangedLine(3, text, null)]).Declarations);
	}

	[Fact]
	public void UsesSkipImportStatementsCommentsAttributesAndQuotedNames()
	{
		const string content =
			"from ._config import (\n" +
			"    LIMIT,\n" +
			")\n" +
			"# LIMIT in a comment\n" +
			"value = LIMIT\n" +
			"other = obj.LIMIT\n" +
			"__all__ = [\"LIMIT\"]\n" +
			"LIMITED = 1\n";
		var bindings = new[] { new DependencyNameBinding(DependencyBindingKind.Name, "LIMIT", "LIMIT", 1) };

		var uses = DependencyChangeFocus.FindUses(content, LanguageId.Python, bindings, ["LIMIT"], new HashSet<string>(), Unprotected, CancellationToken.None);

		Assert.Equal([5], uses.Uses["LIMIT"]);
		Assert.Contains("LIMIT", uses.Bound);
	}

	[Fact]
	public void QualifiedUsesNeedTheQualifier()
	{
		const string content = "import httpx\nhttpx.request('GET')\nrequest()\nother.httpx.request()\n";
		var bindings = new[] { new DependencyNameBinding(DependencyBindingKind.Qualifier, "httpx", null, 1) };

		var uses = DependencyChangeFocus.FindUses(content, LanguageId.Python, bindings, ["request"], new HashSet<string>(), Unprotected, CancellationToken.None);

		Assert.Equal([2], uses.Uses["request"]);
		Assert.Empty(uses.Bound);
	}

	[Fact]
	public void ProtectedOccurrencesAreNotUses()
	{
		const string content = "from a import TOKEN\nkey = TOKEN\n";
		var bindings = new[] { new DependencyNameBinding(DependencyBindingKind.Name, "TOKEN", "TOKEN", 1) };
		var protectedStart = content.LastIndexOf("TOKEN", StringComparison.Ordinal);

		var uses = DependencyChangeFocus.FindUses(
			content,
			LanguageId.Python,
			bindings,
			["TOKEN"],
			new HashSet<string>(),
			(start, end) => start < protectedStart + 5 && end > protectedStart,
			CancellationToken.None);

		Assert.False(uses.UsesAny);
		Assert.True(uses.OnlyReExports);
	}

	[Fact]
	public void WildcardBindsOnlyTheExportedNames()
	{
		const string content = "from ._api import *\nrequest()\n_private()\n";
		var bindings = new[] { new DependencyNameBinding(DependencyBindingKind.Wildcard, string.Empty, null, 1) };

		var uses = DependencyChangeFocus.FindUses(
			content,
			LanguageId.Python,
			bindings,
			["request", "_private"],
			new HashSet<string>(["request"]),
			Unprotected,
			CancellationToken.None);

		Assert.Equal(["request"], uses.Uses.Keys);
	}

	[Fact]
	public void ScriptStatementBindingsAreReadFromTheImportText()
	{
		const string content =
			"import {\n" +
			"  HTTPException as Failure,\n" +
			"  type Other,\n" +
			"} from './http-exception'\n" +
			"throw new Failure(401)\n" +
			"const x = HTTPException\n";
		var bindings = new[] { new DependencyNameBinding(DependencyBindingKind.Statement, string.Empty, null, 1) };

		var uses = DependencyChangeFocus.FindUses(content, LanguageId.TypeScript, bindings, ["HTTPException"], new HashSet<string>(), Unprotected, CancellationToken.None);

		Assert.Equal([5], uses.Uses["HTTPException"]);
	}

	[Theory]
	[InlineData("import * as errors from './errors'\nerrors.HTTPException()\n")]
	[InlineData("const errors = require('./errors')\nerrors.HTTPException()\n")]
	[InlineData("const { HTTPException } = require('./errors')\nHTTPException()\n")]
	public void ScriptNamespaceAndRequireBindingsAreRecognized(string content)
	{
		var bindings = new[] { new DependencyNameBinding(DependencyBindingKind.Statement, string.Empty, null, 1) };

		var uses = DependencyChangeFocus.FindUses(content, LanguageId.JavaScript, bindings, ["HTTPException"], new HashSet<string>(), Unprotected, CancellationToken.None);

		Assert.Equal([2], uses.Uses["HTTPException"]);
	}

	[Fact]
	public void ScriptReExportBindsWithoutUsing()
	{
		const string content = "export { HTTPException } from './http-exception'\n";
		var bindings = new[] { new DependencyNameBinding(DependencyBindingKind.Statement, string.Empty, null, 1) };

		var uses = DependencyChangeFocus.FindUses(content, LanguageId.TypeScript, bindings, ["HTTPException"], new HashSet<string>(), Unprotected, CancellationToken.None);

		Assert.True(uses.OnlyReExports);
	}

	[Fact]
	public void PythonWildcardNamesFollowALiteralAll()
	{
		const string content = "__all__ = [\n    \"request\",\n    'get',\n]\nLIMIT = 1\n";

		var names = DependencyChangeFocus.PythonWildcardNames(content, ["request", "LIMIT"], dynamicAll: false);

		Assert.NotNull(names);
		Assert.Equal(["get", "request"], names.Order(StringComparer.Ordinal));
	}

	[Fact]
	public void PythonWildcardNamesWithoutAllAreThePublicNames()
	{
		var names = DependencyChangeFocus.PythonWildcardNames("LIMIT = 1\n", ["LIMIT", "_hidden"], dynamicAll: false);

		Assert.Equal(["LIMIT"], names);
	}

	[Theory]
	[InlineData("__all__ = build()\n", false)]
	[InlineData("__all__ = [\"a\"]\n", true)]
	public void PythonWildcardNamesAreUnknownForADynamicAll(string content, bool dynamicAll)
	{
		Assert.Null(DependencyChangeFocus.PythonWildcardNames(content, ["a"], dynamicAll));
	}

	[Fact]
	public void PythonBindingsDistinguishModulesNamesAndWildcards()
	{
		var facts = Facts(LanguageId.Python, imports:
		[
			Import("httpx", null, null, false, 0, 1),
			Import("", "_config", "config", false, 1, 2),
			Import("_api", "request", "send", false, 1, 3),
			Import("_types", null, null, true, 1, 4),
			Import("other", "unrelated", null, false, 1, 9)
		]);

		var bindings = DependencyChangeFocus.Bindings(facts, "httpx/_config.py", new HashSet<int>([1, 2, 3, 4]));

		Assert.Equal(
		[
			new DependencyNameBinding(DependencyBindingKind.Qualifier, "httpx", null, 1),
			new DependencyNameBinding(DependencyBindingKind.Qualifier, "config", null, 2),
			new DependencyNameBinding(DependencyBindingKind.Name, "send", "request", 3),
			new DependencyNameBinding(DependencyBindingKind.Wildcard, string.Empty, null, 4)
		], bindings);
	}

	[Fact]
	public void MemberAccessLinesFindDottedAccessOnly()
	{
		const string content = "err.getResponse()\ngetResponse()\nerr . getResponse\n// err.getResponse()\n";

		Assert.Equal([1, 3], DependencyChangeFocus.MemberAccessLines(content, LanguageId.TypeScript, "getResponse", Unprotected));
	}

	[Fact]
	public void MemberNamesSkipConstructors()
	{
		var declaration = new DependencyChangedDeclaration("Client", ["Client.__init__", "Client.send", "Client.send"], 3, 1);

		Assert.Equal(["send"], DependencyChangeFocus.MemberNames(declaration));
	}

	private static NavigationDeclaration Declaration(string name, string? owner, int start, int end, NavigationSymbolKind kind) =>
		new(name, kind, owner, start, end, "fingerprint");

	private static ImportFact Import(string specifier, string? name, string? alias, bool wildcard, int level, int line) =>
		new(specifier, name, alias, wildcard, level, new SourceSite("dependent.py", line, "import"));

	private static FileFacts Facts(
		LanguageId language,
		IReadOnlyList<NavigationDeclaration>? declarations = null,
		IReadOnlyDictionary<string, string>? aliases = null,
		IReadOnlyList<ImportFact>? imports = null) =>
		new(
			"seed",
			"scope",
			language,
			"fingerprint",
			0,
			DependencyFileStatus.Supported,
			null,
			false,
			new Dictionary<string, int>(),
			[],
			imports ?? [],
			[],
			[],
			aliases ?? new Dictionary<string, string>(),
			[],
			new Dictionary<string, string>(),
			[])
		{
			NavigationDeclarations = declarations ?? []
		};
}
