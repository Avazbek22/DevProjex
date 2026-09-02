using DevProjex.Application.Compression;
using TreeSitter;

namespace DevProjex.Tests.Unit;

public sealed class CodeCompressionScalaTests
{
	[Fact]
	public void ScalaPreservesStateConstructorCodeAndShortExpressionsWhileCompressingNamedBodies()
	{
		const string source = """
			final class Service(val root: String):
			  val normalize: String => String = value => value.trim
			  var count: Int = 0
			  println("constructor-state")

			  def short(value: String): String = value.trim

			  def braced(value: String): String = {
			    val scala_braced_marker = normalize(value)
			    scala_braced_marker
			  }

			  def indented(value: String): String =
			    val scala_indented_marker = normalize(value)
			    scala_indented_marker
			""";

		var (plan, text) = Compress("Service.scala", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Equal("scala", plan.LanguageId);
		Assert.Contains("val normalize: String => String = value => value.trim", text, StringComparison.Ordinal);
		Assert.Contains("var count: Int = 0", text, StringComparison.Ordinal);
		Assert.Contains("println(\"constructor-state\")", text, StringComparison.Ordinal);
		Assert.Contains("def short(value: String): String = value.trim", text, StringComparison.Ordinal);
		Assert.Contains("def braced(value: String): String = { }", text, StringComparison.Ordinal);
		Assert.Contains("scala_indented_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("scala_braced_marker", text, StringComparison.Ordinal);
		Assert.True(text.Length < source.Length);
		AssertStructurePreserved(source, text);
	}

	[Fact]
	public void ScalaCompressesMethodsAcrossContainersWithoutCollapsingTheContainers()
	{
		const string source = """
			trait Compute:
			  def required(value: Int): Int
			  def implemented(value: Int): Int = {
			    val scala_trait_marker = value + 1
			    scala_trait_marker
			  }

			case class Entry(name: String, size: Int)

			object Entry:
			  val Empty: Entry = Entry("", 0)
			  def create(name: String): Entry = {
			    val scala_object_marker = name.trim
			    Entry(scala_object_marker, scala_object_marker.length)
			  }

			extension (value: Entry)
			  def label: String = {
			    val scala_extension_marker = value.name.toUpperCase
			    scala_extension_marker
			  }
			""";

		var (plan, text) = Compress("Entry.scala", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("trait Compute:", text, StringComparison.Ordinal);
		Assert.Contains("def required(value: Int): Int", text, StringComparison.Ordinal);
		Assert.Contains("case class Entry(name: String, size: Int)", text, StringComparison.Ordinal);
		Assert.Contains("object Entry:", text, StringComparison.Ordinal);
		Assert.Contains("val Empty: Entry = Entry(\"\", 0)", text, StringComparison.Ordinal);
		Assert.Contains("extension (value: Entry)", text, StringComparison.Ordinal);
		Assert.DoesNotContain("scala_trait_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("scala_object_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("scala_extension_marker", text, StringComparison.Ordinal);
		AssertStructurePreserved(source, text);
	}

	[Fact]
	public void ScalaPreservesGivenValuesIncludingTheirImplementationMembers()
	{
		const string source = """
			final case class Entry(rank: Int)

			given entryOrdering: Ordering[Entry] with
			  def compare(left: Entry, right: Entry): Int =
			    val scala_given_marker = left.rank.compare(right.rank)
			    scala_given_marker

			def outside(value: Int): Int = {
			  val scala_outside_marker = value + 1
			  scala_outside_marker
			}
			""";

		var (plan, text) = Compress("Ordering.scala", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("given entryOrdering: Ordering[Entry] with", text, StringComparison.Ordinal);
		Assert.Contains("scala_given_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("scala_outside_marker", text, StringComparison.Ordinal);
		AssertStructurePreserved(source, text);
	}

	[Fact]
	public void ScalaSignificantIndentationBodiesRemainCompleteWhenSpliceBoundariesAreUnstable()
	{
		const string source = """
			def sign(value: Int): String =
			  if value > 0 then
			    "positive"
			  else
			    "zero"

			def oneLine(value: Int): Int = value * 2
			""";

		var (plan, text) = Compress("math.sc", source);

		Assert.Equal(CodeCompressionOutcome.UnchangedNoBenefit, plan.Outcome);
		Assert.Equal(source, text);
		Assert.Contains("def oneLine(value: Int): Int = value * 2", text, StringComparison.Ordinal);
		AssertStructurePreserved(source, text);
	}

	[Fact]
	public void ScalaMultilineParenthesizedExpressionCompressesThroughTheExpressionContract()
	{
		const string source = """
			def calculate(value: Int): Int = (
			  value
			    + 1
			    + 2
			)

			def oneLine(value: Int): Int = value + 1
			""";

		var (plan, text) = Compress("calculate.scala", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("def calculate(value: Int): Int = { }", text, StringComparison.Ordinal);
		Assert.Contains("def oneLine(value: Int): Int = value + 1", text, StringComparison.Ordinal);
		Assert.DoesNotContain("+ 2", text, StringComparison.Ordinal);
		AssertStructurePreserved(source, text);
	}

	[Fact]
	public void ScalaCrLfAndUnicodeRemainParseValidAndPreserveStateBytes()
	{
		var source = string.Join(
			"\r\n",
			"object Пример:",
			"  val greeting = \"Привет 👋\"",
			"  def compute(value: Int): Int = {",
			"    val scala_unicode_marker = value + 1",
			"    scala_unicode_marker",
			"  }",
			string.Empty);

		var (plan, text) = Compress("Пример.scala", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("val greeting = \"Привет 👋\"", text, StringComparison.Ordinal);
		Assert.Contains("\r\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain("scala_unicode_marker", text, StringComparison.Ordinal);
		AssertStructurePreserved(source, text);
	}

	[Fact]
	public void SbtBuildFilesRemainOutsideTheCompressionLanguageSet()
	{
		const string source = """
			lazy val root = project.in(file("."))
			def helper(value: Int): Int = {
			  val sbt_marker = value + 1
			  sbt_marker
			}
			""";

		var (plan, text) = Compress("build.sbt", source);

		Assert.Equal(CodeCompressionOutcome.UnchangedUnsupportedLanguage, plan.Outcome);
		Assert.Equal(source, text);
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
		using var harness = CodeCompressionTestHarness.For("scala");
		Assert.True(
			CountParseDefects(harness.Parser, transformed) <= CountParseDefects(harness.Parser, source),
			"Scala compression introduced a parse defect.");
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
