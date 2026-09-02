using DevProjex.Application.Compression;
using TreeSitter;

namespace DevProjex.Tests.Unit;

public sealed class CodeCompressionKotlinTests
{
	[Fact]
	public void KotlinPreservesDeclarativeStateWhileCompressingEveryNamedBlockOwner()
	{
		const string source = """
			package sample

			data class Payload(val id: String, val count: Int)

			class Service(
			    val root: String,
			    private var count: Int = 0,
			) {
			    val onClick: (String) -> String = { value ->
			        val kotlin_property_lambda_marker = value.trim()
			        kotlin_property_lambda_marker
			    }

			    var status: String = "ready"
			        get() {
			            val kotlin_getter_marker = field.trim()
			            return kotlin_getter_marker
			        }
			        set(value) {
			            val kotlin_setter_marker = value.trim()
			            field = kotlin_setter_marker
			        }

			    init {
			        val kotlin_init_marker = root.length
			        require(kotlin_init_marker >= 0)
			    }

			    constructor(root: String, enabled: Boolean) : this(root) {
			        val kotlin_secondary_marker = enabled
			        require(kotlin_secondary_marker || root.isNotEmpty())
			    }

			    fun short(): Int = root.length

			    fun calculate(value: Int): Int {
			        val kotlin_function_marker = value + count
			        return kotlin_function_marker
			    }
			}
			""";

		var (plan, text) = Compress("Service.kt", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Equal("kotlin", plan.LanguageId);
		Assert.Contains("data class Payload(val id: String, val count: Int)", text, StringComparison.Ordinal);
		Assert.Contains("val root: String", text, StringComparison.Ordinal);
		Assert.Contains("val kotlin_property_lambda_marker", text, StringComparison.Ordinal);
		Assert.Contains("val kotlin_getter_marker", text, StringComparison.Ordinal);
		Assert.Contains("val kotlin_setter_marker", text, StringComparison.Ordinal);
		Assert.Contains("fun short(): Int = root.length", text, StringComparison.Ordinal);
		Assert.Contains("init { }", text, StringComparison.Ordinal);
		Assert.Contains("constructor(root: String, enabled: Boolean) : this(root) { }", text, StringComparison.Ordinal);
		Assert.Contains("fun calculate(value: Int): Int { }", text, StringComparison.Ordinal);
		Assert.DoesNotContain("kotlin_init_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("kotlin_secondary_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("kotlin_function_marker", text, StringComparison.Ordinal);
		Assert.True(text.Length < source.Length);
		AssertStructurePreserved(source, text);
	}

	[Fact]
	public void KotlinKeepsSignaturesContainersAndFreeLambdasWhileCompressingAdvancedFunctions()
	{
		const string source = """
			annotation class Composable

			enum class Mode {
			    FAST,
			    SLOW;

			    fun label(): String {
			        val kotlin_enum_marker = name.lowercase()
			        return kotlin_enum_marker
			    }
			}

			class Repository {
			    companion object {
			        val Empty = Repository()
			    }

			    @Composable
			    fun Render(value: String) {
			        val kotlin_composable_marker = value.trim()
			        println(kotlin_composable_marker)
			    }

			    suspend inline fun <reified T> load(value: T): String {
			        val kotlin_suspend_marker = value.toString()
			        return kotlin_suspend_marker
			    }

			    fun String.normalized(): String {
			        val kotlin_extension_marker = trim()
			        return kotlin_extension_marker
			    }
			}

			val topLevelTask = launch {
			    val kotlin_trailing_lambda_marker = "retained"
			    println(kotlin_trailing_lambda_marker)
			}

			tasks.register("verify") {
			    val kotlin_bare_dsl_marker = "retained"
			    println(kotlin_bare_dsl_marker)
			}
			""";

		var (plan, text) = Compress("Repository.kts", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("enum class Mode", text, StringComparison.Ordinal);
		Assert.Contains("FAST", text, StringComparison.Ordinal);
		Assert.Contains("companion object", text, StringComparison.Ordinal);
		Assert.Contains("val Empty = Repository()", text, StringComparison.Ordinal);
		Assert.Contains("@Composable", text, StringComparison.Ordinal);
		Assert.Contains("suspend inline fun <reified T> load(value: T): String { }", text, StringComparison.Ordinal);
		Assert.Contains("fun String.normalized(): String { }", text, StringComparison.Ordinal);
		Assert.Contains("kotlin_trailing_lambda_marker", text, StringComparison.Ordinal);
		Assert.Contains("kotlin_bare_dsl_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("kotlin_enum_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("kotlin_composable_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("kotlin_suspend_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("kotlin_extension_marker", text, StringComparison.Ordinal);
		AssertStructurePreserved(source, text);
	}

	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	public void KotlinCompressesQaExpressionBodiesAsFunctionDeclarationsWithoutLambdaPlaceholders(
		string newLine)
	{
		var source = """
			import java.time.format.DateTimeFormatter

			fun oneLine(value: String): String = value.trim()

			fun lineBreakAfterEquals(value: String): String =
			    value.trim()

			fun dateFormatted(value: String): String =
			    DateTimeFormatter
			        .ofPattern(value)
			        .toString()

			fun providesOnFrameListener(): OnFrameListener =
			    OnFrameListener {
			        println("frame")
			    }

			class IndexHintQuery {
			    override fun <T> copy(value: T): IndexHintQuery
			        where T : CharSequence,
			              T : Comparable<T> =
			        IndexHintQuery(
			            value,
			        )
			}

			operator fun String.invoke(value: Int): String =
			    buildString {
			        append(this@invoke)
			        append(value)
			    }
			""".ReplaceLineEndings(newLine);

		var (plan, text) = Compress("Expressions.kt", source);
		var normalizedText = text.ReplaceLineEndings("\n");

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("fun oneLine(value: String): String = value.trim()", text, StringComparison.Ordinal);
		Assert.Contains("fun lineBreakAfterEquals(value: String): String =\n    value.trim()", normalizedText, StringComparison.Ordinal);
		Assert.Contains("fun dateFormatted(value: String): String { }", text, StringComparison.Ordinal);
		Assert.Contains("fun providesOnFrameListener(): OnFrameListener { }", text, StringComparison.Ordinal);
		Assert.Contains("override fun <T> copy(value: T): IndexHintQuery\n        where T : CharSequence,\n              T : Comparable<T> { }", normalizedText, StringComparison.Ordinal);
		Assert.Contains("operator fun String.invoke(value: Int): String { }", text, StringComparison.Ordinal);
		Assert.DoesNotContain(".ofPattern(value)", text, StringComparison.Ordinal);
		Assert.DoesNotContain("println(\"frame\")", text, StringComparison.Ordinal);
		Assert.DoesNotContain("IndexHintQuery(\n", normalizedText, StringComparison.Ordinal);
		Assert.DoesNotContain("buildString {", text, StringComparison.Ordinal);
		Assert.DoesNotContain("= { }", text, StringComparison.Ordinal);
		AssertStructurePreserved(source, text);
	}

	[Fact]
	public void KotlinExpressionCaptureEndsWithItsOwningDeclaration()
	{
		const string source = """
			class Query {
			    override fun <T> copy(value: T): String
			        where T : CharSequence,
			              T : Comparable<T> =
			        value
			            .trim()
			            .uppercase()
			}
			""";
		using var harness = CodeCompressionTestHarness.For("kotlin");
		using var tree = harness.Parser.Parse(source)!;
		using var cursor = harness.Bodies!.Execute(tree.RootNode);
		var expression = Assert.Single(
			cursor.Captures,
			static capture => capture.Name.Equals("expression", StringComparison.Ordinal)).Node;
		var functionBody = Assert.IsType<Node>(expression.Parent);
		var declaration = Assert.IsType<Node>(functionBody.Parent);

		Assert.Equal("function_body", functionBody.Type);
		Assert.Equal("function_declaration", declaration.Type);
		Assert.Equal(functionBody.EndIndex, declaration.EndIndex);

		var (_, text) = Compress("Query.kt", source);
		Assert.Contains("override fun <T> copy(value: T): String", text, StringComparison.Ordinal);
		Assert.Contains("where T : CharSequence", text, StringComparison.Ordinal);
		Assert.Contains("T : Comparable<T> { }", text, StringComparison.Ordinal);
	}

	[Fact]
	public void KotlinCrLfAndUnicodeRemainParseValidWithoutCollapsingTheClassBody()
	{
		var source = string.Join(
			"\r\n",
			"class Пример(val приветствие: String = \"Привет 👋\") {",
			"    val текст = приветствие",
			"    fun вычислить(value: Int): Int {",
			"        val kotlin_unicode_marker = value + 1",
			"        return kotlin_unicode_marker",
			"    }",
			"}",
			string.Empty);

		var (plan, text) = Compress("Пример.kt", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("class Пример", text, StringComparison.Ordinal);
		Assert.Contains("val приветствие: String = \"Привет 👋\"", text, StringComparison.Ordinal);
		Assert.Contains("val текст = приветствие", text, StringComparison.Ordinal);
		Assert.Contains("fun вычислить(value: Int): Int { }", text, StringComparison.Ordinal);
		Assert.Contains("\r\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain("kotlin_unicode_marker", text, StringComparison.Ordinal);
		AssertStructurePreserved(source, text);
	}

	private static (CodeCompressionPlan Plan, string Text) Compress(string path, string source)
	{
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var scope = compressor.CreateScope(Path.GetTempPath());
		var analysis = scope.Analyze(path, path, source, TestContext.Current.CancellationToken);
		return (analysis.Plan, analysis.GetResult(source).Text);
	}

	private static void AssertStructurePreserved(string source, string transformed)
	{
		using var harness = CodeCompressionTestHarness.For("kotlin");
		Assert.True(
			CountParseDefects(harness.Parser, transformed) <= CountParseDefects(harness.Parser, source),
			"Kotlin compression introduced a parse defect.");
		Assert.Equal(ReadDeclarations(harness, source), ReadDeclarations(harness, transformed));
	}

	private static string[] ReadDeclarations(CodeCompressionTestHarness harness, string source)
	{
		using var tree = harness.Parser.Parse(source)!;
		using var cursor = harness.Declarations.Execute(tree.RootNode);
		return cursor.Matches
			.Select(static match =>
			{
				var declaration = match.Captures.First(static capture => capture.Name == "declaration");
				var name = match.Captures.FirstOrDefault(static capture => capture.Name == "name");
				return $"{declaration.Node.Type}:{name?.Node.Text ?? string.Empty}";
			})
			.Order(StringComparer.Ordinal)
			.ToArray();
	}

	private static int CountParseDefects(Parser parser, string source)
	{
		using var tree = parser.Parse(source)!;
		using var cursor = new TreeCursor(tree.RootNode);
		var defects = 0;
		while (true)
		{
			var node = cursor.CurrentNode;
			if (node.IsError || node.IsMissing || node.IsNamed && node.StartIndex == node.EndIndex)
				defects++;
			if (cursor.GotoFirstChild())
				continue;
			while (!cursor.GotoNextSibling())
			{
				if (!cursor.GotoParent())
					return defects;
			}
		}
	}
}
