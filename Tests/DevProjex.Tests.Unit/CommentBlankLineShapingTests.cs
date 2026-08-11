using DevProjex.Application.Compression;

namespace DevProjex.Tests.Unit;

public sealed class CommentBlankLineShapingTests
{
	private const string FullyCommentedFileWithBlankSeparators = """
		// Task 1
		// Console.WriteLine("one");


		// Task 2
		// for (var index = 0; index < 10; index++)
		// {
		//     Console.WriteLine(index);
		// }


		// Task 3
		// Console.WriteLine("three");

		""";

	private const string MixedFileWithCommentSurroundedByBlankLines = """
		namespace CommentSpacing;

		internal static class Values
		{
		    internal static string First => "first";


		    // The removed explanation separates the two live members.


		    internal static string Second => "second";
		}

		""";

	public static IEnumerable<object[]> LanguageCases()
	{
		var cases = new (string Path, string Source, string Expected)[]
		{
			("sample.sh", "first=1\n\n \n# remove\n\t\n\nsecond=2\n", "first=1\n\nsecond=2\n"),
			("sample.c", "int first = 1;\n\n \n/* remove */\n\t\n\nint second = 2;\n", "int first = 1;\n\nint second = 2;\n"),
			("sample.cpp", "int first = 1;\n\n \n// remove\n\t\n\nint second = 2;\n", "int first = 1;\n\nint second = 2;\n"),
			(
				"Sample.cs",
				"class Sample\n{\n    int First;\n\n    \n    // remove\n\t\n\n    int Second;\n}\n",
				"class Sample\n{\n    int First;\n\n    int Second;\n}\n"),
			("sample.css", ".first { color: red; }\n\n \n/* remove */\n\t\n\n.second { color: blue; }\n", ".first { color: red; }\n\n.second { color: blue; }\n"),
			(
				"sample.go",
				"package sample\n\nconst First = 1\n\n \n// remove\n\t\n\nconst Second = 2\n",
				"package sample\n\nconst First = 1\n\nconst Second = 2\n"),
			(
				"sample.html",
				"<main>\n  <p>first</p>\n\n  \n  <!-- remove -->\n\t\n\n  <p>second</p>\n</main>\n",
				"<main>\n  <p>first</p>\n\n  <p>second</p>\n</main>\n"),
			(
				"Sample.java",
				"class Sample {\n    int first;\n\n    \n    // remove\n\t\n\n    int second;\n}\n",
				"class Sample {\n    int first;\n\n    int second;\n}\n"),
			("sample.js", "const first = 1;\n\n \n// remove\n\t\n\nconst second = 2;\n", "const first = 1;\n\nconst second = 2;\n"),
			("Sample.kt", "const val first = 1\n\n \n// remove\n\t\n\nconst val second = 2\n", "const val first = 1\n\nconst val second = 2\n"),
			(
				"sample.php",
				"<?php\n$first = 1;\n\n \n// remove\n\t\n\n$second = 2;\n?>\n",
				"<?php\n$first = 1;\n\n$second = 2;\n?>\n"),
			("sample.py", "first = 1\n\n \n# remove\n\t\n\nsecond = 2\n", "first = 1\n\nsecond = 2\n"),
			("sample.rb", "FIRST = 1\n\n \n# remove\n\t\n\nSECOND = 2\n", "FIRST = 1\n\nSECOND = 2\n"),
			("sample.rs", "const FIRST: i32 = 1;\n\n \n// remove\n\t\n\nconst SECOND: i32 = 2;\n", "const FIRST: i32 = 1;\n\nconst SECOND: i32 = 2;\n"),
			(
				"Sample.scala",
				"object Sample {\n  val first = 1\n\n  \n  // remove\n\t\n\n  val second = 2\n}\n",
				"object Sample {\n  val first = 1\n\n  val second = 2\n}\n"),
			("sample.toml", "first = 1\n\n \n# remove\n\t\n\nsecond = 2\n", "first = 1\n\nsecond = 2\n"),
			("sample.tsx", "const first = <span>one</span>;\n\n \n// remove\n\t\n\nconst second = <span>two</span>;\n", "const first = <span>one</span>;\n\nconst second = <span>two</span>;\n"),
			("sample.ts", "const first: number = 1;\n\n \n// remove\n\t\n\nconst second: number = 2;\n", "const first: number = 1;\n\nconst second: number = 2;\n"),
			(
				"sample.xml",
				"<root>\n  <first />\n\n  \n  <!-- remove -->\n\t\n\n  <second />\n</root>\n",
				"<root>\n  <first />\n\n  <second />\n</root>\n"),
			(
				"sample.yaml",
				"items:\n  - first\n\n  \n  # remove\n\t\n\n  - second\n",
				"items:\n  - first\n\n  - second\n")
		};

		foreach (var item in cases)
		{
			foreach (var newline in new[] { "\n", "\r\n" })
			{
				yield return
				[
					item.Path,
					item.Source.ReplaceLineEndings(newline),
					item.Expected.ReplaceLineEndings(newline)
				];
			}
		}
	}

	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	public void CommentBetweenMethodsLeavesExactlyOneBlankLine(string newline)
	{
		var source = string.Join(
			newline,
			"internal sealed class Strings",
			"{",
			"    void First() { }",
			"",
			"    ",
			"    // The removed explanation separated these methods.",
			"\t",
			"",
			"    void Second() { }",
			"}",
			"");
		var expected = string.Join(
			newline,
			"internal sealed class Strings",
			"{",
			"    void First() { }",
			"",
			"    void Second() { }",
			"}",
			"");

		var result = Transform("CommentSpacing.cs", source, CodeTransformKinds.Comments);

		Assert.Equal(expected, result.Text);
	}

	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	public void FullyCommentedFileWithBlankSeparatorsBecomesEmpty(string newline)
	{
		var source = string.Join(
			newline,
			"// Task 1",
			"// Console.WriteLine(\"one\");",
			"",
			"   ",
			"// Task 2",
			"// for (var index = 0; index < 10; index++) { }",
			"\t",
			"",
			"// Task 3");

		var result = Transform("FullyCommented.cs", source, CodeTransformKinds.Comments);

		Assert.Equal(string.Empty, result.Text);
	}

	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	public void LiveStatementBetweenRemovedBlocksHasNoBoundaryBlankRuns(string newline)
	{
		var source = string.Join(
			newline,
			"// Task 1",
			"// removed",
			"",
			" ",
			"Console.WriteLine(\"live\");",
			"\t",
			"",
			"// Task 2",
			"// removed",
			"");

		var result = Transform("Mixed.cs", source, CodeTransformKinds.Comments);

		Assert.Equal($"Console.WriteLine(\"live\");{newline}", result.Text);
	}

	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	public void RemovedHeaderAndFooterDoNotLeaveBoundaryBlankLines(string newline)
	{
		var source = string.Join(
			newline,
			"// Header",
			"",
			"   ",
			"var value = 42;",
			"",
			"\t",
			"// Footer");

		var result = Transform("Boundaries.cs", source, CodeTransformKinds.Comments);

		Assert.Equal($"var value = 42;{newline}", result.Text);
	}

	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	public void UnrelatedAuthorBlankLinesRemainByteForByte(string newline)
	{
		var source = $"var first = 1;{newline}{newline}{newline}var second = 2;{newline}";

		var result = Transform("NoComments.cs", source, CodeTransformKinds.Comments);

		Assert.Equal(CodeCompressionOutcome.UnchangedNoBenefit, result.Plan.Outcome);
		Assert.Equal(source, result.Text);
	}

	[Fact]
	public void MixedLineEndingsAndMissingFinalNewlineCollapseOnlyAtTheCommentSite()
	{
		const string source = "var first = 1;\r\n\r\n// remove\n \t\r\n\nvar second = 2;";

		var result = Transform("MixedEndings.cs", source, CodeTransformKinds.Comments);

		Assert.Equal("var first = 1;\r\n\r\nvar second = 2;", result.Text);
	}

	[Fact]
	public void TrailingCommentKeepsItsLineAndDoesNotCollapseFollowingAuthorSpacing()
	{
		const string source = "Call(); // remove\n\n\nNext();\n";

		var result = Transform("Trailing.cs", source, CodeTransformKinds.Comments);

		Assert.Equal("Call();\n\n\nNext();\n", result.Text);
	}

	[Theory]
	[MemberData(nameof(LanguageCases))]
	public void EveryCommentLanguageCollapsesOnlyBlankLinesAdjacentToRemovedComments(
		string path,
		string source,
		string expected)
	{
		var result = Transform(path, source, CodeTransformKinds.Comments);

		Assert.Equal(expected, result.Text);
		Assert.Equal(CodeTransformKinds.Comments, result.Plan.AffectedKinds);
		AssertNoBoundaryOrRepeatedBlankLines(result.Text);
	}

	[Fact]
	public void CommentsAndBodiesKeepTheBodyPlaceholderWhileBodyOnlyOutputIsUnchanged()
	{
		const string source =
			"internal sealed class Sample\n" +
			"{\n" +
			"    void Run()\n" +
			"    {\n" +
			"\n" +
			"        // remove\n" +
			"\n" +
			"        Work();\n" +
			"    }\n" +
			"}\n";

		var bodies = Transform("Sample.cs", source, CodeTransformKinds.Bodies);
		var comments = Transform("Sample.cs", source, CodeTransformKinds.Comments);
		var both = Transform(
			"Sample.cs",
			source,
			CodeTransformKinds.Bodies | CodeTransformKinds.Comments);

		Assert.Equal(
			"internal sealed class Sample\n{\n    void Run()\n    { }\n}\n",
			bodies.Text);
		Assert.Equal(
			"internal sealed class Sample\n{\n    void Run()\n    {\n\n        Work();\n    }\n}\n",
			comments.Text);
		Assert.Equal(bodies.Text, both.Text);
		Assert.Equal(CodeTransformKinds.Bodies | CodeTransformKinds.Comments, both.Plan.AffectedKinds);
	}

	[Fact]
	public void CommentOnlyFileWithFinalNewlineBecomesExactlyEmpty()
	{
		const string source = "// first\n\n \n// second\n";

		var result = Transform("Comments.cs", source, CodeTransformKinds.Comments);

		Assert.Equal(string.Empty, result.Text);
	}

	[Fact]
	public void ByteOrderMarkIsNotConsumedByBlankLineShaping()
	{
		const string source = "\uFEFF// header\r\n\r\nvar value = 1;\r\n";

		var result = Transform("Bom.cs", source, CodeTransformKinds.Comments);

		Assert.Equal("\uFEFFvar value = 1;\r\n", result.Text);
	}

	[Fact]
	public void InlineRegressionCasesProduceEmptyAndStructurallyCleanFiles()
	{
		var fullyCommentedResult = Transform(
			"FullyCommented.cs",
			FullyCommentedFileWithBlankSeparators,
			CodeTransformKinds.Comments);
		var mixedResult = Transform(
			"CommentSpacing.cs",
			MixedFileWithCommentSurroundedByBlankLines,
			CodeTransformKinds.Comments);

		Assert.Equal(string.Empty, fullyCommentedResult.Text);
		Assert.Equal(
			"namespace CommentSpacing;\n\n" +
			"internal static class Values\n" +
			"{\n" +
			"    internal static string First => \"first\";\n\n" +
			"    internal static string Second => \"second\";\n" +
			"}\n",
			mixedResult.Text.ReplaceLineEndings("\n"));
	}

	[Fact]
	public void ExpandedCommentEditsMapLaterContentBackToItsCanonicalSourceOffset()
	{
		const string source =
			"var first = 1;\n\n \n// remove\n\t\n\nvar secret = \"later-value\";\n";
		var result = Transform("Offsets.cs", source, CodeTransformKinds.Comments);
		var transformedOffset = result.Text.IndexOf("later-value", StringComparison.Ordinal);

		Assert.True(result.Map.TryToSource(transformedOffset, out var sourceOffset));
		Assert.Equal(source.IndexOf("later-value", StringComparison.Ordinal), sourceOffset);
	}

	[Fact]
	public void PythonDocstringsCollapseAdjacentBlankRunsWithoutInvalidatingSuites()
	{
		const string source =
			"\"\"\"module docs\"\"\"\n" +
			"\n" +
			" \t\n" +
			"value = 1\n" +
			"\n" +
			"def work():\n" +
			"    \"\"\"function docs\"\"\"\n" +
			"\n" +
			"    \t\n" +
			"    return value\n";
		var result = Transform("sample.py", source, CodeTransformKinds.Comments);

		Assert.Equal(
			"value = 1\n\n" +
			"def work():\n\n" +
			"    return value\n",
			result.Text);
		Assert.DoesNotContain("docs", result.Text, StringComparison.Ordinal);
	}

	private static (CodeCompressionPlan Plan, string Text, ContentTransformMap Map) Transform(
		string path,
		string source,
		CodeTransformKinds kinds)
	{
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var scope = compressor.CreateScope(Path.GetTempPath(), kinds);
		var analysis = scope.Analyze(path, path, source, TestContext.Current.CancellationToken);
		var result = analysis.GetResult(source);
		return (analysis.Plan, result.Text, result.Map);
	}

	private static void AssertNoBoundaryOrRepeatedBlankLines(string content)
	{
		using var reader = new StringReader(content);
		var sawContent = false;
		var previousWasBlank = false;
		string? line;
		while ((line = reader.ReadLine()) is not null)
		{
			var isBlank = line.Length == 0 || line.All(static value => value is ' ' or '\t');
			if (isBlank)
			{
				Assert.True(sawContent, "the transformed file starts with a blank line");
				Assert.False(previousWasBlank, "the transformed file contains a repeated blank line");
			}
			else
			{
				sawContent = true;
			}

			previousWasBlank = isBlank;
		}

		Assert.False(previousWasBlank, "the transformed file ends with a blank line");
	}
}
