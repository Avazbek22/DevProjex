using DevProjex.Application.Compression;
using DevProjex.Infrastructure.Compression;
using TreeSitter;

namespace DevProjex.Tests.Unit;

public sealed class CodeCompressionExpressionBodyTests
{
	[Fact]
	public void CSharpPreservesOneLineExpressionsWithoutInspectingTheirText()
	{
		const string source = """
			internal sealed class Service
			{
			    public bool IsSupported(string path) => compressor.IsSupported(path);
			    private static StreamWriter CreateStreamWriter(Stream destination) =>
			        new(destination, Utf8WithoutBom, bufferSize: 8192, leaveOpen: true);
			    public int CountValid() => items.Where(x => { Log(); return x.IsValid; }).Count();
			    public string Separator() => Join(";", parts) + "=>;";
			    public string Status() => $"{items.Count(x => x.Ok)};done";
			    public void Execute() { var block_marker = 1; Consume(block_marker); }
			}
			""";

		var (plan, text) = Compress("Service.cs", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("public bool IsSupported(string path) => compressor.IsSupported(path);", text, StringComparison.Ordinal);
		Assert.Contains(
			"private static StreamWriter CreateStreamWriter(Stream destination) =>\n        new(destination, Utf8WithoutBom, bufferSize: 8192, leaveOpen: true);",
			text,
			StringComparison.Ordinal);
		Assert.Contains("items.Where(x => { Log(); return x.IsValid; }).Count();", text, StringComparison.Ordinal);
		Assert.Contains("Join(\";\", parts) + \"=>;\";", text, StringComparison.Ordinal);
		Assert.Contains("$\"{items.Count(x => x.Ok)};done\";", text, StringComparison.Ordinal);
		Assert.DoesNotContain("block_marker", text, StringComparison.Ordinal);
		AssertNoNewParseDefects("csharp", source, text);
	}

	[Fact]
	public void CSharpCompressesMultilineExpressionsAndConsumesTheDeclarationSemicolon()
	{
		const string source = """
			internal static class Tokens
			{
			    private static string ToToken(FileKind kind) =>
			        kind switch
			        {
			            FileKind.Text => "text",
			            FileKind.Binary => "binary",
			            _ => "unknown"
			        };

			    private static int Pick(bool enabled) =>
			        enabled
			            ? 1
			            : 2;

			    private static Task WriteAsync(Writer writer) =>
			        writer.WriteAsync(
			            value: "payload",
			            flush: true);
			}
			""";

		var (plan, text) = Compress("Tokens.cs", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("private static string ToToken(FileKind kind) { }", text, StringComparison.Ordinal);
		Assert.Contains("private static int Pick(bool enabled) { }", text, StringComparison.Ordinal);
		Assert.Contains("private static Task WriteAsync(Writer writer) { }", text, StringComparison.Ordinal);
		Assert.DoesNotContain("{ };", text, StringComparison.Ordinal);
		Assert.DoesNotContain("FileKind.Text", text, StringComparison.Ordinal);
		Assert.DoesNotContain("value: \"payload\"", text, StringComparison.Ordinal);
		Assert.Equal(
			CountDeclarations("csharp", source),
			CountDeclarations("csharp", text));
		AssertNoNewParseDefects("csharp", source, text);
	}

	[Fact]
	public void CSharpMeasuresExpressionRowsCorrectlyWithCrLfLineEndings()
	{
		var source = string.Join(
			"\r\n",
			"internal sealed class Service",
			"{",
			"    private int Keep() =>",
			"        Compute();",
			"    private int Remove() =>",
			"        enabled",
			"            ? 1",
			"            : 2;",
			"}");

		var (plan, text) = Compress("Service.cs", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("private int Keep() =>\r\n        Compute();", text, StringComparison.Ordinal);
		Assert.Contains("private int Remove() { }", text, StringComparison.Ordinal);
		Assert.DoesNotContain("? 1", text, StringComparison.Ordinal);
		AssertNoNewParseDefects("csharp", source, text);
	}

	[Fact]
	public void CSharpAppliesTheRuleToConstructorsAndOperatorsButPreservesPropertiesAndIndexers()
	{
		const string source = """
			internal sealed class Number
			{
			    public Number(int value) =>
			        Value =
			            Normalize(value);

			    public static Number operator +(Number left, Number right) =>
			        new(
			            left.Value + right.Value);

			    public int Value { get; }
			    public string Description =>
			        Value switch
			        {
			            0 => "zero",
			            _ => $"value:{Value}"
			        };

			    public string this[int index] =>
			        Values[index]
			            .Trim()
			            .ToUpperInvariant();
			}
			""";

		var (plan, text) = Compress("Number.cs", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("public Number(int value) { }", text, StringComparison.Ordinal);
		Assert.Contains("public static Number operator +(Number left, Number right) { }", text, StringComparison.Ordinal);
		Assert.Contains("0 => \"zero\"", text, StringComparison.Ordinal);
		Assert.Contains("Values[index]", text, StringComparison.Ordinal);
		Assert.Contains(".ToUpperInvariant();", text, StringComparison.Ordinal);
		AssertNoNewParseDefects("csharp", source, text);
	}

	public static TheoryData<string, string> JavaScriptFamilyFiles() => new()
	{
		{ "javascript", "expressions.js" },
		{ "typescript", "expressions.ts" },
		{ "tsx", "expressions.tsx" }
	};

	[Theory]
	[MemberData(nameof(JavaScriptFamilyFiles))]
	public void JavaScriptFamilyCompressesOnlyMultilineExpressionsUnderStableBindings(
		string languageId,
		string path)
	{
		const string source = """
			const normalize = (value) => value.trim()
			const computed = (value) => (
			  value
			    .trim()
			    .toUpperCase()
			)
			const chained = (items) =>
			  items
			    .map((item) => item.value)
			    .filter(Boolean)
			let assigned
			assigned = (value) => (
			  value
			    .trim()
			)
			export default (value) => (
			  value
			    .trim()
			    .toUpperCase()
			)
			const options = {
			  short: (value) => value.trim(),
			  long: (value) => (
			    value
			      .trim()
			  ),
			  block: () => { const block_marker = 1; return block_marker }
			}
			describe("suite", () => (
			  run()
			    .then((value) => value)
			))
			class Widget {
			  handler = () => (
			    this.items
			      .map((item) => item.value)
			  )
			}
			""";

		var (plan, text) = Compress(path, source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("const normalize = (value) => value.trim()", text, StringComparison.Ordinal);
		Assert.Contains("const computed = (value) => { }", text, StringComparison.Ordinal);
		Assert.Contains("const chained = (items) =>\n  { }", text, StringComparison.Ordinal);
		Assert.Contains("assigned = (value) => { }", text, StringComparison.Ordinal);
		Assert.Contains("export default (value) => { }", text, StringComparison.Ordinal);
		Assert.Contains("short: (value) => value.trim()", text, StringComparison.Ordinal);
		Assert.Contains("long: (value) => { }", text, StringComparison.Ordinal);
		Assert.Contains("block: () => { }", text, StringComparison.Ordinal);
		Assert.Contains("describe(\"suite\", () => (", text, StringComparison.Ordinal);
		Assert.Contains("handler = () => (\n    this.items\n      .map((item) => item.value)\n  )", text, StringComparison.Ordinal);
		Assert.DoesNotContain("block_marker", text, StringComparison.Ordinal);
		AssertNoNewParseDefects(languageId, source, text);
	}

	[Theory]
	[MemberData(nameof(JavaScriptFamilyFiles))]
	public void JavaScriptFamilyDeduplicatesBlockAndExpressionCaptures(
		string languageId,
		string path)
	{
		const string source = "const run = () => { const implementation_marker = 1; return implementation_marker; };";

		var (plan, text) = Compress(path, source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Single(plan.Edits);
		Assert.Equal("const run = () => { };", text);
		AssertNoNewParseDefects(languageId, source, text);
	}

	[Fact]
	public void TsxCompressesMultilineJsxInDirectAndCallWrappedBindings()
	{
		const string source = """
			const renderItem = (item: Item) => <ItemView item={item} />;
			const Card = (props: CardProps) => (
			  <article>
			    <h2>{props.title}</h2>
			    <p>{props.description}</p>
			  </article>
			);
			const Wrapped = memo((props: PanelProps) => (
			  <aside>
			    <Card {...props} />
			  </aside>
			));
			export default memo(forwardRef((props: PanelProps, ref) => (
			  <main ref={ref}>
			    <Card {...props} />
			  </main>
			)));
			""";

		var (plan, text) = Compress("Components.tsx", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("const renderItem = (item: Item) => <ItemView item={item} />;", text, StringComparison.Ordinal);
		Assert.Contains("const Card = (props: CardProps) => { };", text, StringComparison.Ordinal);
		Assert.Contains("const Wrapped = memo((props: PanelProps) => { });", text, StringComparison.Ordinal);
		Assert.Contains("export default memo(forwardRef((props: PanelProps, ref) => { }));", text, StringComparison.Ordinal);
		Assert.DoesNotContain("props.description", text, StringComparison.Ordinal);
		Assert.DoesNotContain("<Card {...props} />", text, StringComparison.Ordinal);
		AssertNoNewParseDefects("tsx", source, text);
	}

	private static (CodeCompressionPlan Plan, string Text) Compress(string path, string source)
	{
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var scope = compressor.CreateScope(Path.GetTempPath());
		var analysis = scope.Analyze(path, path, source, TestContext.Current.CancellationToken);
		return (analysis.Plan, analysis.GetResult(source).Text);
	}

	private static int CountDeclarations(string languageId, string source)
	{
		using var harness = CodeCompressionTestHarness.For(languageId);
		using var tree = harness.Parser.Parse(source)!;
		using var cursor = harness.Declarations.Execute(tree.RootNode);
		return cursor.Matches.Count();
	}

	private static void AssertNoNewParseDefects(string languageId, string source, string transformed)
	{
		using var harness = CodeCompressionTestHarness.For(languageId);
		Assert.True(
			CountParseDefects(harness.Parser, transformed) <= CountParseDefects(harness.Parser, source),
			$"{languageId}: compression introduced a parse defect");
	}

	private static int CountParseDefects(Parser parser, string source)
	{
		using var tree = parser.Parse(source)!;
		var defects = 0;
		var nodes = new Stack<Node>();
		nodes.Push(tree.RootNode);
		while (nodes.TryPop(out var node))
		{
			if (node.IsError || node.IsMissing || node.IsNamed && node.StartIndex == node.EndIndex)
				defects++;
			foreach (var child in node.Children)
				nodes.Push(child);
		}

		return defects;
	}
}
