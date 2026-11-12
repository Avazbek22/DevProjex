using DevProjex.Application.Dependencies;
using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpDeclarationCommentsTests
{
	public static IEnumerable<object[]> Boundaries()
	{
		(string Path, string Comment, bool Attached)[] cases =
		[
			("sample.ts", "// needle\n// end\n", true),
			("sample.ts", "/**\n * needle\n */\n", true),
			("sample.ts", "// before\n/* needle */\n// after\n", true),
			("sample.ts", "// needle\n\n", false),
			("sample.ts", "/*\n * needle\n\n */\n", false),
			("sample.py", "# needle\n", true),
			("sample.pyi", "# needle\n", true),
			("sample.rb", "# needle\n", true),
			("sample.rake", "# needle\n", true),
			("sample.cs", "#region needle\n", false),
			("sample.rs", "#[derive(needle)]\n", false),
			("sample.cs", "[Attr(needle)]\n", false),
			("sample.py", "@needle\n", false),
			("sample.ts", "/* needle\n" + string.Concat(Enumerable.Repeat(" * padding\n", 200)) + " */\n", false)
		];
		foreach (var item in cases)
			foreach (var newline in new[] { "\n", "\r\n" })
				yield return [item.Path, item.Comment.Replace("\n", newline, StringComparison.Ordinal), item.Attached];
	}

	[Theory]
	[MemberData(nameof(Boundaries))]
	public void IndexAndResolveUseTheSameBoundedCommentOwner(string path, string comment, bool attached)
	{
		var content = "class Outer {\n" + comment + "member() {\n return 1;\n}\n}";
		var lines = McpLargeFileOutlineBuilder.NormalizeLineEndings(content).Split('\n');
		var start = Array.FindIndex(lines, static line => line.StartsWith("member", StringComparison.Ordinal)) + 1;
		var outer = Declaration("Outer", 1, lines.Length);
		var member = Declaration("Outer.member", start, start + 2);
		NavigationDeclaration[] declarations = [outer, member];
		var hit = Array.FindIndex(lines, static line => line.Contains("needle", StringComparison.Ordinal)) + 1;
		var index = Index(path, content, declarations);
		var expected = attached ? member : outer;
		Assert.Same(expected, index.Find(hit));
		var resolved = Resolve(path, declarations, index, hit);
		Assert.Equal(expected.Name, resolved.Names[new(path, hit)]);
		Assert.Equal(expected.StartLine, Assert.Single(resolved.Declarations).StartLine);
		Assert.Equal(expected.EndLine, Assert.Single(resolved.Declarations).EndLine);
		foreach (var bodyLine in Enumerable.Range(start, 3)) Assert.Same(member, index.Find(bodyLine));
		Assert.Same(expected, index.Find(hit));
	}

	[Fact]
	public void ACommentStartingTheFileCanNameATopLevelFunction()
	{
		const string content = "// needle\nfunction run() {\n return 1;\n}";
		NavigationDeclaration[] declarations = [Declaration("run", 2, 4)];
		var index = Index("sample.ts", content, declarations);
		Assert.Same(declarations[0], index.Find(1));
		Assert.Equal("run", Resolve("sample.ts", declarations, index, 1).Names[new("sample.ts", 1)]);
	}

	[Theory]
	[InlineData(200, true)]
	[InlineData(201, false)]
	public void UpwardInspectionStopsAfterTwoHundredLines(int count, bool attached)
	{
		var content = string.Concat(Enumerable.Repeat("// needle\n", count)) + "function run() {}";
		NavigationDeclaration[] declarations = [Declaration("run", count + 1, count + 1)];
		Assert.Equal(attached, Index("sample.ts", content, declarations).Find(1) is not null);
	}

	[Fact]
	public void APreviousUnrelatedDeclarationKeepsItsOwnLine()
	{
		NavigationDeclaration[] declarations = [Declaration("previous", 1, 3), Declaration("next", 4, 6)];
		var index = Index("sample.ts", "function previous() {\n}\n// needle\nfunction next() {\n}\n", declarations);
		Assert.Same(declarations[0], index.Find(3));
		Assert.Equal("previous", Resolve("sample.ts", declarations, index, 3).Names[new("sample.ts", 3)]);
	}

	[Fact]
	public void CommentHitsKeepEveryOverloadRangeWithoutIncludingTheComment()
	{
		NavigationDeclaration[] declarations = [Declaration("run", 2, 2), Declaration("run", 4, 5)];
		var index = Index("sample.ts", "// needle\nfunction run(): void;\n// needle\nfunction run() {\n}\n", declarations);
		var result = Resolve("sample.ts", declarations, index, 1);
		var declaration = Assert.Single(result.Declarations);
		Assert.Equal("2-2, 4-5", result.DeclarationRanges.Format(declaration));
	}

	[Fact]
	public void NameSearchStillShowsTheDeclarationInsteadOfItsLeadingComment()
	{
		const string content = "// run\nfunction run() {\n}\n";
		NavigationDeclaration[] declarations = [Declaration("run", 2, 3)];
		var matches = McpSearchSymbols.FindNamedDeclarationMatches(content,
			new McpSearchRegex("run", ignoreCase: false), 0, [], declarations, ["run"], TestContext.Current.CancellationToken);
		Assert.Equal(2, Assert.Single(matches).MatchLineNumber);
	}

	private static NavigationDeclaration Declaration(string name, int start, int end) =>
		new(name, NavigationSymbolKind.Function, null, start, end, "fixture");

	private static McpNavigationDeclarationIndex Index(string path, string content, IReadOnlyList<NavigationDeclaration> declarations) =>
		new(declarations, comments: McpDeclarationComments.Capture(path, content, declarations, TestContext.Current.CancellationToken));

	private static McpSearchSymbolResult Resolve(string path, IReadOnlyList<NavigationDeclaration> declarations,
		McpNavigationDeclarationIndex index, int line) =>
		McpSearchSymbols.Resolve([new(path, path, line)], new Dictionary<string, IReadOnlyList<NavigationDeclaration>> { [path] = declarations },
			TestContext.Current.CancellationToken, new Dictionary<string, McpNavigationDeclarationIndex> { [path] = index });
}
