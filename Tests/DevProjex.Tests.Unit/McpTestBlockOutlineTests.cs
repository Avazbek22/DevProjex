using System.Text.RegularExpressions;
using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Compression;
using DevProjex.Infrastructure.Dependencies;
using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpTestBlockOutlineTests
{
	[Theory]
	[InlineData("describe", "describe")]
	[InlineData("context", "describe")]
	[InlineData("suite.only", "describe")]
	[InlineData("suite", "describe")]
	[InlineData("specify", "test")]
	[InlineData("test.todo", "test")]
	[InlineData("it.concurrent", "test")]
	[InlineData("specify.each([1, 2])", "test")]
	[InlineData("(/* root */ describe).skip", "describe")]
	[InlineData("(describe /* trailing */).only", "describe")]
	[InlineData("it", "test")]
	[InlineData("test", "test")]
	[InlineData("specify.skip", "test")]
	[InlineData("describe.skipIf(condition).each(cases)", "describe")]
	[InlineData("test.each(cases).custom.modifier", "test")]
	[InlineData("test.describe.parallel", "describe")]
	[InlineData("test.only.describe", "describe")]
	[InlineData("test.extend(config).describe.serial", "describe")]
	[InlineData("test['describe'].only", "describe")]
	[InlineData("test[dynamic].only", "test")]
	[InlineData("test['des' + 'cribe']", "test")]
	[InlineData("Deno.test", "test")]
	[InlineData("Deno.test.ignore.each(cases)", "test")]
	[InlineData("Deno['test'].only", "test")]
	[InlineData("(describe).skip", "describe")]
	public void RecognizedStatementRootsKeepTheirKindsThroughCallChains(string callee, string kind)
	{
		var block = Assert.Single(Extract(callee + "('chosen title', () => {});"));
		Assert.Equal(kind, block.Kind);
		Assert.Equal("\"chosen title\"", block.Title);
	}

	[Theory]
	[InlineData("describe.each`column\n${value}`", "describe")]
	[InlineData("test.skipIf(flag).each`value ${1}`.custom", "test")]
	[InlineData("test.extend(config).describe.each`value ${1}`", "describe")]
	public void TaggedTablesDoNotReplaceTheOutermostCallTitle(string callee, string kind)
	{
		var block = Assert.Single(Extract(callee + "('outer title', () => {}).after('last title', () => {});"));
		Assert.Equal(kind, block.Kind);
		Assert.Equal("\"last title\"", block.Title);
	}

	[Theory]
	[InlineData("describe();")]
	[InlineData("test.each(cases)();")]
	[InlineData("test.each`table`;")]
	[InlineData("const registration = describe('assigned', () => {});")]
	[InlineData("registration = test('assigned', () => {});")]
	[InlineData("consume(describe('argument', () => {}));")]
	[InlineData("other.describe('other root', () => {});")]
	[InlineData("re.test('other root');")]
	[InlineData("obj.it('other root', () => {});")]
	[InlineData("this.test('other root', () => {});")]
	[InlineData("Deno.other.test('other member', () => {});")]
	[InlineData("Deno[dynamic].test('unknown member', () => {});")]
	[InlineData("globalThis.test('other root', () => {});")]
	[InlineData("'describe(\"string\", () => {})';")]
	[InlineData("`it(\"template\", () => {})`;")]
	[InlineData("// test('comment', () => {});")]
	[InlineData("/* describe('comment', () => {}); */")]
	[InlineData("new describe('constructor', () => {});")]
	public void NonStatementRegistrationsAndUnrelatedRootsAreIgnored(string source)
	{
		Assert.Empty(Extract(source));
	}

	[Theory]
	[InlineData(".js")]
	[InlineData(".jsx")]
	[InlineData(".mjs")]
	[InlineData(".cjs")]
	[InlineData(".ts")]
	[InlineData(".tsx")]
	[InlineData(".mts")]
	[InlineData(".cts")]
	public void EveryJavaScriptAndTypeScriptExtensionGetsTheSameBlocks(string extension)
	{
		var blocks = McpTestBlockOutlineExtractor.Extract("source" + extension, "describe('suite', () => { it('case', () => {}); });", TestContext.Current.CancellationToken);
		Assert.Equal(new[] { "describe", "test" }, blocks.Select(block => block.Kind));
		Assert.Equal(new[] { "\"suite\"", "\"case\"" }, blocks.Select(block => block.Title));
	}

	[Theory]
	[InlineData(".jsx")]
	[InlineData(".tsx")]
	public void JsxSyntaxDoesNotDisableStatementBlocks(string extension)
	{
		var source = "const element = <section>{value}</section>; test('jsx', () => {});";
		Assert.Equal("\"jsx\"", Assert.Single(McpTestBlockOutlineExtractor.Extract("source" + extension, source, TestContext.Current.CancellationToken)).Title);
	}

	[Theory]
	[InlineData(".cs")]
	[InlineData(".py")]
	[InlineData(".json")]
	[InlineData(".md")]
	[InlineData(".vue")]
	public void OtherLanguagesNeverLoadATestGrammar(string extension)
	{
		var locator = new MissingLocator();
		Assert.Empty(McpTestBlockOutlineExtractor.Extract("source" + extension, "describe('suite', () => {});", TestContext.Current.CancellationToken, locator));
		Assert.Equal(0, locator.Calls);
	}

	[Theory]
	[InlineData("'  literal   title  '", " literal title ")]
	[InlineData("`  template ${ value } \n title  `", " template ${ value } title ")]
	[InlineData("prefix +\n   caseName", "prefix + caseName")]
	[InlineData("{ name: 'object', fn: callback }", "{ name: 'object', fn: callback }")]
	[InlineData("17", "17")]
	[InlineData("null", "null")]
	[InlineData("''", "")]
	[InlineData("'   '", " ")]
	[InlineData("'has \"quote\" and \\\\slash'", "has \"quote\" and \\\\slash")]
	public void TitlesComeFromTheFirstArgumentWithoutEvaluation(string argument, string title)
	{
		var block = Assert.Single(Extract("describe(" + argument + ", () => {});"));
		Assert.Equal(JsonSerializer.Serialize(title, McpToolResults.CompactJsonOptions), block.Title);
	}

	[Theory]
	[InlineData(80)]
	[InlineData(81)]
	public void TitlesUseTheEightyCharacterLimitWithAnEllipsisOnlyWhenNeeded(int characters)
	{
		var title = new string('a', characters);
		var block = Assert.Single(Extract("test('" + title + "', () => {});"));
		Assert.Equal(JsonSerializer.Serialize(characters == 80 ? title : title[..79] + "…", McpToolResults.CompactJsonOptions), block.Title);
	}

	[Fact]
	public void TitlesNeverSplitASurrogatePair()
	{
		var title = new string('a', 79) + "🙂";
		Assert.Equal(JsonSerializer.Serialize(title, McpToolResults.CompactJsonOptions), Assert.Single(Extract("it('" + title + "', () => {}); ")).Title);
		var longer = title + "b";
		Assert.Equal(JsonSerializer.Serialize(new string('a', 79) + "…", McpToolResults.CompactJsonOptions), Assert.Single(Extract("it('" + longer + "', () => {}); ")).Title);
	}

	[Fact]
	public void WholeStatementsAndSameLineNestingKeepExactRanges()
	{
		const string source = "describe(\n 'outer',\n () => {\n  test.only(\n   'inner',\n   () => {}\n  );\n }\n);";
		var blocks = Extract(source);
		Assert.Equal((1, 9), (blocks[0].StartLine, blocks[0].EndLine));
		Assert.Equal((4, 7), (blocks[1].StartLine, blocks[1].EndLine));
		var inline = Build("describe('outer', () => { it('inner', () => {}); });");
		Assert.NotNull(inline);
		Assert.Equal("1-1 \"outer\" (describe)\n  1-1 \"inner\" (test)\n", inline.Map);
	}

	[Fact]
	public void TemplateSubstitutionsKeepStatementsButNeverLiteralText()
	{
		const string source = "const text = `test('literal', () => {}) ${(() => { it('actual', () => {}); return ''; })()}`;";
		var block = Assert.Single(Extract(source));
		Assert.Equal("\"actual\"", block.Title);
		Assert.Equal("test", block.Kind);
	}

	[Fact]
	public void TestBlocksShareExistingNestingWithOrdinaryDeclarations()
	{
		const string source = "function helper() {\n describe('suite', () => { it('case', () => {}); });\n}";
		var declaration = new NavigationDeclaration("helper", NavigationSymbolKind.Function, null, 1, 3, "protected") { StartIndex = 0, EndIndex = source.Length };
		var outline = Build(source, [declaration]);
		Assert.NotNull(outline);
		Assert.Contains("1-3 helper (function)\n  2-2 \"suite\" (describe)\n", outline.Map, StringComparison.Ordinal);
		Assert.DoesNotContain("\"case\"", outline.Map, StringComparison.Ordinal);
	}

	[Fact]
	public void TestBlocksNeverBecomeAddressableNavigationSymbols()
	{
		const string source = "describe('suite', () => { test('case', () => {}); });";
		using var extractor = new TreeSitterDependencyFactExtractor();
		var navigation = extractor.ExtractNavigation("sample.ts", source, "protected", TestContext.Current.CancellationToken);
		Assert.Empty(navigation);
		Assert.NotNull(Build(source, navigation));
	}

	[Fact]
	public void ParseDamageAndMissingGrammarLeaveNoBlocks()
	{
		Assert.Empty(Extract("describe('valid', () => {}); test('broken', () => {"));
		var locator = new MissingLocator();
		Assert.Empty(McpTestBlockOutlineExtractor.Extract("sample.ts", "test('case', () => {});", TestContext.Current.CancellationToken, locator));
		Assert.Equal(1, locator.Calls);
		using var canceled = new CancellationTokenSource();
		canceled.Cancel();
		Assert.Throws<OperationCanceledException>(() => McpTestBlockOutlineExtractor.Extract("sample.ts", "test('case', () => {});", canceled.Token, locator));
	}

	[Fact]
	public void OrdinaryNavigationKeepsItsExactMapWhenNoBlocksArePresent()
	{
		const string source = "function normal() {}\nfunction another() {}";
		var declarations = new[]
		{
			new NavigationDeclaration("normal", NavigationSymbolKind.Function, null, 1, 1, "protected"),
			new NavigationDeclaration("another", NavigationSymbolKind.Function, null, 2, 2, "protected")
		};
		Assert.Equal("1-1 normal (function)\n2-2 another (function)\n", Build(source, declarations)!.Map);
		Assert.Null(Build("test('single', () => {});"));
	}

	[Fact]
	public void EntryCapRemainsDistinctFromTheCharacterCapForEitherLineEndingWidth()
	{
		var source = "describe('r', () => {\n" + string.Join('\n', Enumerable.Repeat("test('', () => {});", 85)) + "\n});";
		var outline = Build(source)!;
		Assert.Equal(86, outline.Entries);
		Assert.Equal(80, Regex.Matches(outline.Map, @"(?m)^\s*\d+-\d+ ").Count);
		Assert.Contains("(+6 more)", outline.Map, StringComparison.Ordinal);
		Assert.True(outline.Map.Length <= 2_400);
		Assert.True(outline.Map.Replace("\n", "\r\n", StringComparison.Ordinal).Length <= 2_400);
		Assert.Contains("describe, 85 members", outline.Map, StringComparison.Ordinal);
	}

	[Fact]
	public void CharacterCapKeepsStableSlackAndDoesNotDependOnLineEndings()
	{
		var first = new string('a', 800);
		var last = new string('b', 800);
		var lines = new[] { "function " + first + "() {}" }
			.Concat(Enumerable.Repeat("test('" + new string('\"', 80) + "', () => {});", 6))
			.Append("function " + last + "() {}").ToArray();
		var source = string.Join('\n', lines);
		var declarations = new[]
		{
			new NavigationDeclaration(first, NavigationSymbolKind.Function, null, 1, 1, "protected") { StartIndex = 0, EndIndex = lines[0].Length },
			new NavigationDeclaration(last, NavigationSymbolKind.Function, null, 8, 8, "protected") { StartIndex = source.LastIndexOf("function", StringComparison.Ordinal), EndIndex = source.Length }
		};
		var outline = Build(source, declarations)!;
		Assert.Contains("(+1 more)", outline.Map, StringComparison.Ordinal);
		Assert.DoesNotContain(last, outline.Map, StringComparison.Ordinal);
		foreach (var newline in new[] { "\n", "\r\n" })
		{
			var measured = outline.Map.Replace("\n", newline, StringComparison.Ordinal);
			Assert.True(2_500 - measured.Length >= 100, measured.Length.ToString());
			Assert.True(measured.Length + ("8-8 " + last + " (function)" + newline).Length > 2_500);
		}
		Assert.Equal(outline.Map, Build(source.Replace("\n", "\r\n", StringComparison.Ordinal), declarations)!.Map);
	}

	private static IReadOnlyList<McpTestBlockOutlineEntry> Extract(string source) =>
		McpTestBlockOutlineExtractor.Extract("sample.ts", source, TestContext.Current.CancellationToken);

	private static McpLargeFileOutline? Build(string source, IReadOnlyList<NavigationDeclaration>? navigation = null) =>
		McpLargeFileOutlineBuilder.Build("sample.ts", source, () => navigation ?? [], TestContext.Current.CancellationToken, requireLargeFile: false);


	private sealed class MissingLocator : IGrammarLibraryLocator
	{
		public int Calls { get; private set; }
		public string StrategyName => "missing";
		public IReadOnlyList<string> EnumerateLibraries() => [];
		public string Resolve(string libraryBaseName)
		{
			Calls++;
			throw new DllNotFoundException(libraryBaseName);
		}
	}
}
