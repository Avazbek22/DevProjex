using DevProjex.Application.Compression;

namespace DevProjex.Tests.Unit;

public sealed class CodeCommentStrippingTests
{
	public static TheoryData<string, string, string> LanguageCases => new()
	{
		{
			"sample.c",
			"/** doc_marker */\nint value = 1; // line_marker\n/* block_marker */\nconst char *text = \"// string_marker\";\n",
			"int value = 1;\nconst char *text = \"// string_marker\";\n"
		},
		{
			"sample.cpp",
			"/** doc_marker */\nconstexpr int value = 1; // line_marker\n/* block_marker */\nconst char *text = \"// string_marker\";\n",
			"constexpr int value = 1;\nconst char *text = \"// string_marker\";\n"
		},
		{
			"Sample.cs",
			"/// doc_marker\ninternal static class Sample\n{\n    /* block_marker */\n    private const string Text = \"// string_marker\"; // line_marker\n}\n",
			"internal static class Sample\n{\n    private const string Text = \"// string_marker\";\n}\n"
		},
		{
			"sample.go",
			"package sample\n\n// doc_marker\nconst Value = 1 // line_marker\n/* block_marker */\nconst Text = \"// string_marker\"\n",
			"package sample\n\nconst Value = 1\nconst Text = \"// string_marker\"\n"
		},
		{
			"Sample.java",
			"/** doc_marker */\nfinal class Sample {\n    /* block_marker */\n    static final String TEXT = \"// string_marker\"; // line_marker\n}\n",
			"final class Sample {\n    static final String TEXT = \"// string_marker\";\n}\n"
		},
		{
			"sample.js",
			"/** doc_marker */\nconst value = 1; // line_marker\n/* block_marker */\nconst text = \"// string_marker\";\n",
			"const value = 1;\nconst text = \"// string_marker\";\n"
		},
		{
			"Sample.kt",
			"/** doc_marker */\nconst val value = 1 // line_marker\n/* block_marker */\nconst val text = \"// string_marker\"\n",
			"const val value = 1\nconst val text = \"// string_marker\"\n"
		},
		{
			"sample.php",
			"<p><!-- html_marker --></p>\n<?php\n/** doc_marker */\n$value = 1; # line_marker\n/* block_marker */\n$text = '// string_marker';\n?>\n",
			"<p><!-- html_marker --></p>\n<?php\n$value = 1;\n$text = '// string_marker';\n?>\n"
		},
		{
			"sample.py",
			"\"\"\"doc_marker\"\"\"\nvalue = 1  # line_marker\n# pragma_marker\ntext = \"# string_marker\"\n",
			"value = 1\ntext = \"# string_marker\"\n"
		},
		{
			"sample.rb",
			"# doc_marker\nVALUE = 1 # line_marker\n=begin\nblock_marker\n=end\nTEXT = '# string_marker'\n",
			"VALUE = 1\nTEXT = '# string_marker'\n"
		},
		{
			"sample.rs",
			"//! doc_marker\nconst VALUE: i32 = 1; // line_marker\n/* block_marker */\nconst TEXT: &str = \"// string_marker\";\n",
			"const VALUE: i32 = 1;\nconst TEXT: &str = \"// string_marker\";\n"
		},
		{
			"Sample.scala",
			"/** doc_marker */\nval value = 1 // line_marker\n/* block_marker */\nval text = \"// string_marker\"\n",
			"val value = 1\nval text = \"// string_marker\"\n"
		},
		{
			"sample.tsx",
			"/** doc_marker */\nconst value = 1; // line_marker\n/* block_marker */\nconst text = \"// string_marker\";\nconst element = <span>{text}</span>;\n",
			"const value = 1;\nconst text = \"// string_marker\";\nconst element = <span>{text}</span>;\n"
		},
		{
			"sample.ts",
			"/** doc_marker */\nconst value: number = 1; // line_marker\n/* block_marker */\nconst text: string = \"// string_marker\";\n",
			"const value: number = 1;\nconst text: string = \"// string_marker\";\n"
		}
	};

	[Fact]
	public void ModeMatrix_ProducesDocumentationImplementationAndSkeletonContracts()
	{
		const string source =
			"/// Calculates.\r\n" +
			"internal static class Sample\r\n" +
			"{\r\n" +
			"    internal static int Add(int left, int right)\r\n" +
			"    {\r\n" +
			"        // Sum values.\r\n" +
			"        return left + right; // result\r\n" +
			"    }\r\n" +
			"}\r\n";

		var bodies = Transform("Sample.cs", source, CodeTransformKinds.Bodies);
		var comments = Transform("Sample.cs", source, CodeTransformKinds.Comments);
		var both = Transform(
			"Sample.cs",
			source,
			CodeTransformKinds.Bodies | CodeTransformKinds.Comments);

		Assert.Equal(
			"/// Calculates.\r\ninternal static class Sample\r\n{\r\n    internal static int Add(int left, int right)\r\n    { }\r\n}\r\n",
			bodies.Text);
		Assert.Equal(
			"internal static class Sample\r\n{\r\n    internal static int Add(int left, int right)\r\n    {\r\n        return left + right;\r\n    }\r\n}\r\n",
			comments.Text);
		Assert.Equal(
			"internal static class Sample\r\n{\r\n    internal static int Add(int left, int right)\r\n    { }\r\n}\r\n",
			both.Text);
		Assert.Equal(CodeTransformKinds.Bodies, bodies.Plan.AffectedKinds);
		Assert.Equal(CodeTransformKinds.Comments, comments.Plan.AffectedKinds);
		Assert.Equal(CodeTransformKinds.Bodies | CodeTransformKinds.Comments, both.Plan.AffectedKinds);
	}

	[Fact]
	public void CommentOnly_CleansCompleteTrailingAndInlineCommentsWithoutChangingLineEndings()
	{
		const string source =
			"internal static class Sample\r\n" +
			"{\r\n" +
			"    internal static void Run()\r\n" +
			"    {\r\n" +
			"        // first\r\n" +
			"        // second\r\n" +
			"        int left /* inline */ = 1; // tail\r\n" +
			"        Use(left); /* no-final-newline */";

		var result = Transform("Sample.cs", source, CodeTransformKinds.Comments);

		Assert.Equal(
			"internal static class Sample\r\n" +
			"{\r\n" +
			"    internal static void Run()\r\n" +
			"    {\r\n" +
			"        int left  = 1;\r\n" +
			"        Use(left);",
			result.Text);
		Assert.DoesNotContain(" \r\n", result.Text, StringComparison.Ordinal);
	}

	[Fact]
	public void TrailingMultilineBlockCommentDoesNotLeaveWhitespaceBehind()
	{
		const string source =
			"internal static class Sample\n" +
			"{\n" +
			"    internal static void Run()\n" +
			"    {\n" +
			"        Call(); /* first\n" +
			"                  second */\n" +
			"        Next();\n" +
			"    }\n" +
			"}\n";

		var result = Transform("Sample.cs", source, CodeTransformKinds.Comments);

		Assert.Equal(
			"internal static class Sample\n" +
			"{\n" +
			"    internal static void Run()\n" +
			"    {\n" +
			"        Call();\n" +
			"        Next();\n" +
			"    }\n" +
			"}\n",
			result.Text);
	}

	[Fact]
	public void Python_StripsLeadingDocstringsAndKeepsSuitesValid()
	{
		const string source = """"
			#!/usr/bin/env python3
			"""Module docs."""

			def only_docs():
			    """Function docs."""

			def work():
			    """Work docs."""
			    value = "# not a comment"
			    return value

			"not a docstring"
			"""";

		var result = Transform("sample.py", source, CodeTransformKinds.Comments);

		Assert.StartsWith("#!/usr/bin/env python3\n", result.Text, StringComparison.Ordinal);
		Assert.DoesNotContain("Module docs", result.Text, StringComparison.Ordinal);
		Assert.DoesNotContain("Function docs", result.Text, StringComparison.Ordinal);
		Assert.Contains("def only_docs():\n    ...", result.Text, StringComparison.Ordinal);
		Assert.Contains("value = \"# not a comment\"", result.Text, StringComparison.Ordinal);
		Assert.Contains("\"not a docstring\"", result.Text, StringComparison.Ordinal);
	}

	[Fact]
	public void PythonModeMatrixGivesDocumentationImplementationAndBareSkeleton()
	{
		const string source =
			"def work():\n" +
			"    \"\"\"Work docs.\"\"\"\n" +
			"    value = 42\n" +
			"    return value\n";

		var bodies = Transform("sample.py", source, CodeTransformKinds.Bodies);
		var comments = Transform("sample.py", source, CodeTransformKinds.Comments);
		var both = Transform(
			"sample.py",
			source,
			CodeTransformKinds.Bodies | CodeTransformKinds.Comments);

		Assert.Equal("def work():\n    \"\"\"Work docs.\"\"\"\n    ...\n", bodies.Text);
		Assert.Equal("def work():\n    value = 42\n    return value\n", comments.Text);
		Assert.Equal("def work():\n    ...\n", both.Text);
		Assert.Equal(CodeTransformKinds.Bodies | CodeTransformKinds.Comments, both.Plan.AffectedKinds);
	}

	[Fact]
	public void Python_FStringIsCodeAndACommentOnlyFileCanBecomeEmpty()
	{
		const string source = """
			def value(name):
			    f"hello {name}"
			    return name  # remove
			""";

		var code = Transform("sample.py", source, CodeTransformKinds.Comments);
		var comments = Transform("comments.py", "# first\r\n# second", CodeTransformKinds.Comments);

		Assert.Contains("f\"hello {name}\"", code.Text, StringComparison.Ordinal);
		Assert.DoesNotContain("# remove", code.Text, StringComparison.Ordinal);
		Assert.Equal(string.Empty, comments.Text);
	}

	[Theory]
	[InlineData("tool.py", "#!/usr/bin/env python3\n# remove\nvalue = 1\n")]
	[InlineData("tool.rb", "#!/usr/bin/env ruby\n# remove\nVALUE = 1\n")]
	[InlineData("tool.js", "#!/usr/bin/env node\n// remove\nconst value = 1;\n")]
	public void ShebangAtOffsetZero_IsNeverRemoved(string path, string source)
	{
		var result = Transform(path, source, CodeTransformKinds.Comments);

		Assert.StartsWith("#!", result.Text, StringComparison.Ordinal);
		Assert.DoesNotContain("remove", result.Text, StringComparison.Ordinal);
	}

	[Fact]
	public void DirectivesAttributesAndAnnotationsAreNotComments()
	{
		var csharp = Transform(
			"Sample.cs",
			"#region Keep\n#if DEBUG\ninternal class Sample { }\n#endif\n#endregion\n// remove\n",
			CodeTransformKinds.Comments);
		var rust = Transform(
			"sample.rs",
			"#![allow(dead_code)]\n#[derive(Debug)]\nstruct Sample;\n// remove\n",
			CodeTransformKinds.Comments);
		var php = Transform(
			"sample.php",
			"<?php #[Attribute] class Sample {} // remove\n",
			CodeTransformKinds.Comments);
		var kotlin = Transform(
			"Sample.kt",
			"// remove\n@Deprecated(\"old\")\nclass Sample { }\n",
			CodeTransformKinds.Comments);

		Assert.Contains("#region Keep", csharp.Text, StringComparison.Ordinal);
		Assert.Contains("#if DEBUG", csharp.Text, StringComparison.Ordinal);
		Assert.Contains("#![allow(dead_code)]", rust.Text, StringComparison.Ordinal);
		Assert.Contains("#[derive(Debug)]", rust.Text, StringComparison.Ordinal);
		Assert.Contains("#[Attribute]", php.Text, StringComparison.Ordinal);
		Assert.Contains("@Deprecated", kotlin.Text, StringComparison.Ordinal);
		Assert.Equal(CodeCompressionOutcome.Compressed, kotlin.Plan.Outcome);
		Assert.DoesNotContain("remove", csharp.Text, StringComparison.Ordinal);
		Assert.DoesNotContain("remove", rust.Text, StringComparison.Ordinal);
		Assert.DoesNotContain("remove", php.Text, StringComparison.Ordinal);
		Assert.DoesNotContain("remove", kotlin.Text, StringComparison.Ordinal);
	}

	[Fact]
	public void ToolingPragmaCommentsAreRemovedWithoutTouchingTheFollowingCode()
	{
		const string typeScriptSource =
			"// eslint-disable-next-line no-console\n" +
			"console.log('kept');\n" +
			"// @ts-ignore\n" +
			"const value: number = 'kept';\n";
		const string pythonSource =
			"value = unknown()  # type: ignore[name-defined]\n";

		var typeScript = Transform("sample.ts", typeScriptSource, CodeTransformKinds.Comments);
		var python = Transform("sample.py", pythonSource, CodeTransformKinds.Comments);

		Assert.Equal("console.log('kept');\nconst value: number = 'kept';\n", typeScript.Text);
		Assert.Equal("value = unknown()\n", python.Text);
	}

	[Theory]
	[InlineData(
		"sample.rb",
		"TEXT = <<~DOC\n// string_marker\n#{1 + 1} # interpolation_marker\nDOC\n# remove_marker\n",
		"TEXT = <<~DOC\n// string_marker\n#{1 + 1} # interpolation_marker\nDOC\n")]
	[InlineData(
		"sample.php",
		"<?php\n$text = <<<TXT\n// string_marker\n# string_marker\nTXT;\n// remove_marker\n",
		"<?php\n$text = <<<TXT\n// string_marker\n# string_marker\nTXT;\n")]
	[InlineData(
		"sample.php",
		"<?php\n$text = <<<'TXT'\n# string_marker\nTXT;\n// remove_marker\n",
		"<?php\n$text = <<<'TXT'\n# string_marker\nTXT;\n")]
	[InlineData(
		"sample.js",
		"const text = `/* string_marker ${value} */`; // remove_marker\n",
		"const text = `/* string_marker ${value} */`;\n")]
	public void HeredocTemplateAndInterpolationContentIsNotCommentSyntax(
		string path,
		string source,
		string expected)
	{
		var result = Transform(path, source, CodeTransformKinds.Comments);

		Assert.Equal(expected, result.Text);
	}

	[Fact]
	public void PreservedMember_RemainsByteForByteIncludingItsComment()
	{
		const string source = """
			// remove this
			internal sealed class Sample
			{
			    private readonly string _value = /* retained with field */ "value";

			    public string Value
			    {
			        get { /* retained with property */ return _value; }
			    }
			}
			""";

		var result = Transform("Sample.cs", source, CodeTransformKinds.Comments);

		Assert.DoesNotContain("remove this", result.Text, StringComparison.Ordinal);
		Assert.Contains("/* retained with field */", result.Text, StringComparison.Ordinal);
		Assert.Contains("/* retained with property */", result.Text, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(
		"sample.py",
		"# remove_marker\nclass Sample:\n    def __init__(self):\n        # retained_marker\n        self.value = 1\n")]
	[InlineData(
		"sample.rb",
		"# remove_marker\nclass Sample\n  def initialize\n    # retained_marker\n    @value = 1\n  end\nend\n")]
	[InlineData(
		"sample.php",
		"<?php\n// remove_marker\nclass Sample {\n    public function __construct() {\n        // retained_marker\n        $this->value = 1;\n    }\n}\n")]
	[InlineData(
		"sample.js",
		"// remove_marker\nclass Sample {\n    handler = () => {\n        // retained_marker\n        return 1;\n    };\n}\n")]
	[InlineData(
		"Sample.kt",
		"// remove_marker\nclass Sample {\n    val handler = {\n        // retained_marker\n        1\n    }\n}\n")]
	[InlineData(
		"Sample.scala",
		"// remove_marker\nobject Sample {\n  val handler = () => {\n    // retained_marker\n    1\n  }\n}\n")]
	public void PreservedStateKeepsNestedCommentsAcrossLanguageModels(
		string path,
		string source)
	{
		var result = Transform(path, source, CodeTransformKinds.Comments);

		Assert.DoesNotContain("remove_marker", result.Text, StringComparison.Ordinal);
		Assert.Contains("retained_marker", result.Text, StringComparison.Ordinal);
	}

	[Theory]
	[MemberData(nameof(LanguageCases))]
	public void EveryLanguage_ProducesTheExactCommentFreeText(
		string path,
		string source,
		string expected)
	{
		var result = Transform(path, source, CodeTransformKinds.Comments);

		Assert.Equal(CodeCompressionOutcome.Compressed, result.Plan.Outcome);
		Assert.Equal(expected, result.Text);
	}

	[Fact]
	public void SessionCache_IsolatedByModeAndReusesThePreviousCombination()
	{
		const string source = """
			// docs
			internal static class Sample
			{
			    internal static int Run()
			    {
			        // implementation
			        return 42;
			    }
			}
			""";
		var fullPath = Path.Combine(Path.GetTempPath(), "mode-cache.cs");
		using var session = new CodeCompressionSession(CodeCompressionTestHarness.CreateCompressor());

		var comments = Transform(session, fullPath, source, CodeTransformKinds.Comments);
		var afterComments = session.Diagnostics;
		var bodies = Transform(session, fullPath, source, CodeTransformKinds.Bodies);
		var afterBodies = session.Diagnostics;
		var both = Transform(
			session,
			fullPath,
			source,
			CodeTransformKinds.Bodies | CodeTransformKinds.Comments);
		var afterBoth = session.Diagnostics;
		var commentsAgain = Transform(session, fullPath, source, CodeTransformKinds.Comments);
		var afterReuse = session.Diagnostics;

		Assert.NotEqual(comments, bodies);
		Assert.NotEqual(comments, both);
		Assert.NotEqual(bodies, both);
		Assert.Equal(comments, commentsAgain);
		Assert.Equal(afterComments.AnalysisExecutions + 1, afterBodies.AnalysisExecutions);
		Assert.Equal(afterBodies.AnalysisExecutions + 1, afterBoth.AnalysisExecutions);
		Assert.Equal(afterBoth.AnalysisExecutions, afterReuse.AnalysisExecutions);
		Assert.True(afterReuse.CacheHits > afterBoth.CacheHits);
	}

	[Fact]
	public void UnsupportedLanguageRemainsByteForByteWithItsNormalOutcome()
	{
		const string source = "<!-- documentation -->\n# Markdown heading\n";

		var result = Transform("README.md", source, CodeTransformKinds.Comments);

		Assert.Equal(CodeCompressionOutcome.UnchangedUnsupportedLanguage, result.Plan.Outcome);
		Assert.Equal(source, result.Text);
		Assert.Empty(result.Plan.Edits);
	}

	private static (CodeCompressionPlan Plan, string Text) Transform(
		string path,
		string source,
		CodeTransformKinds kinds)
	{
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var scope = compressor.CreateScope(Path.GetTempPath(), kinds);
		var analysis = scope.Analyze(path, path, source, TestContext.Current.CancellationToken);
		return (analysis.Plan, analysis.GetResult(source).Text);
	}

	private static string Transform(
		CodeCompressionSession session,
		string fullPath,
		string source,
		CodeTransformKinds kinds)
	{
		using var scope = new CodeCompressionContext(Path.GetTempPath(), session, kinds)
			.BeginOutput([fullPath]);
		return scope.Transform(fullPath, Path.GetFileName(fullPath), source, TestContext.Current.CancellationToken).Text;
	}
}
