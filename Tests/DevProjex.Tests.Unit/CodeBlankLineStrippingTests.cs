using DevProjex.Application.Compression;

namespace DevProjex.Tests.Unit;

public sealed class CodeBlankLineStrippingTests
{
	[Theory]
	[MemberData(nameof(ModeMatrixCases))]
	public void AllEightModeCombinations_ProduceTheExpectedBytes(
		CodeTransformKinds kinds,
		string expected)
	{
		var result = kinds == CodeTransformKinds.None
			? ModeMatrixSource
			: Transform("Sample.cs", ModeMatrixSource, kinds).Text;

		Assert.Equal(expected, result);
	}

	[Theory]
	[InlineData("\n\nvalue();\n\n", "value();\n")]
	[InlineData("\r\n\t\r\nvalue();\r\n \t\r\n", "value();\r\n")]
	[InlineData("\n\n", "")]
	[InlineData("\r\n \t\r\n", "")]
	[InlineData(" \t", "")]
	[InlineData("\r\n\n \t\r\nvalue();\r\n\n", "value();\r\n")]
	[InlineData("value();\n\n \t", "value();\n")]
	[InlineData("value();", "value();")]
	[InlineData("class Sample\n{\n\n}\n", "class Sample\n{\n}\n")]
	public void Boundaries_RemoveEveryBlankLineAndPreserveTheLastContentNewline(
		string source,
		string expected)
	{
		var result = Transform("sample.js", source, CodeTransformKinds.BlankLines);

		Assert.Equal(expected, result.Text);
	}

	[Theory]
	[MemberData(nameof(LineContinuationCases))]
	public void BlankLineAfterTrailingBackslash_IsPreservedConservatively(
		string caseName,
		string path,
		string source,
		string expected,
		bool expectNoBenefit)
	{
		var result = Transform(path, source, CodeTransformKinds.BlankLines);

		Assert.Equal(expected, result.Text);
		if (expectNoBenefit)
		{
			// Python and Ruby inputs in this matrix are intentionally malformed. The shared guard
			// preserves their bytes before the structural gate has any reason to intervene.
			Assert.Equal(CodeCompressionOutcome.UnchangedNoBenefit, result.Plan.Outcome);
			Assert.Empty(result.Plan.Edits);
		}
		Assert.False(string.IsNullOrWhiteSpace(caseName));
	}

	[Fact]
	public void AlreadyCanceledLargeBlankOnlyFile_ThrowsOperationCanceledException()
	{
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var scope = compressor.CreateScope(Path.GetTempPath(), CodeTransformKinds.BlankLines);
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		var source = new string('\n', 128 * 1024);

		Assert.ThrowsAny<OperationCanceledException>(() =>
			scope.Analyze("blank.js", "blank.js", source, cancellation.Token));
	}

	[Theory]
	[MemberData(nameof(ProtectedLeafCases))]
	public void MultilineLeafTokens_KeepTheirBlankLinesByteForByte(
		string path,
		string source,
		string expected)
	{
		var result = Transform(path, source, CodeTransformKinds.BlankLines);

		Assert.Equal(expected, result.Text);
	}

	[Fact]
	public void MultilineComment_IsProtectedWhenCommentsAreNotRemoved()
	{
		const string source = "/* first\n\nsecond */\n\nint value = 1;\n";
		const string expected = "/* first\n\nsecond */\nint value = 1;\n";

		var result = Transform("sample.c", source, CodeTransformKinds.BlankLines);

		Assert.Equal(expected, result.Text);
	}

	[Fact]
	public void PreserveQueries_DoNotProtectBlankLinesOutsideMultilineLeafTokens()
	{
		const string source =
			"internal sealed class Sample\n" +
			"{\n" +
			"    internal int Value\n" +
			"    {\n" +
			"\n" +
			"        get;\n" +
			"    }\n" +
			"}\n";
		const string expected =
			"internal sealed class Sample\n" +
			"{\n" +
			"    internal int Value\n" +
			"    {\n" +
			"        get;\n" +
			"    }\n" +
			"}\n";

		var blankLines = Transform("Sample.cs", source, CodeTransformKinds.BlankLines);
		var bodiesAndBlankLines = Transform(
			"Sample.cs",
			source,
			CodeTransformKinds.Bodies | CodeTransformKinds.BlankLines);

		Assert.Equal(expected, blankLines.Text);
		Assert.Equal(expected, bodiesAndBlankLines.Text);
	}

	[Fact]
	public void CommentsAndBlankLines_KeepPreservedCommentsButRemoveAdjacentBlankLines()
	{
		const string source =
			"internal sealed class Sample\n" +
			"{\n" +
			"    internal int Value\n" +
			"    {\n" +
			"        // Declarative state documentation stays with the property.\n" +
			"\n" +
			"        get;\n" +
			"    }\n" +
			"}\n";
		const string expected =
			"internal sealed class Sample\n" +
			"{\n" +
			"    internal int Value\n" +
			"    {\n" +
			"        // Declarative state documentation stays with the property.\n" +
			"        get;\n" +
			"    }\n" +
			"}\n";

		var result = Transform(
			"Sample.cs",
			source,
			CodeTransformKinds.Comments | CodeTransformKinds.BlankLines);

		Assert.Equal(expected, result.Text);
	}

	[Fact]
	public void CommentsAndBlankLines_MergeAtSharedLineBoundariesWithoutDuplicateEdits()
	{
		const string source =
			"internal static class Sample\n" +
			"{\n" +
			"\n" +
			"    // remove\n" +
			"\n" +
			"    internal static int Value => 42;\n" +
			"\n" +
			"}\n";
		const string expected =
			"internal static class Sample\n" +
			"{\n" +
			"    internal static int Value => 42;\n" +
			"}\n";

		var result = Transform(
			"Sample.cs",
			source,
			CodeTransformKinds.Comments | CodeTransformKinds.BlankLines);

		Assert.Equal(expected, result.Text);
		for (var index = 1; index < result.Plan.Edits.Count; index++)
			Assert.True(result.Plan.Edits[index - 1].SourceEnd <= result.Plan.Edits[index].SourceStart);
	}

	[Fact]
	public void BodiesAndBlankLines_UseTheOutermostBodyEdit()
	{
		const string source =
			"internal static class Sample\n" +
			"{\n" +
			"    internal static void Run()\n" +
			"    {\n" +
			"\n" +
			"        Call();\n" +
			"\n" +
			"    }\n" +
			"}\n";

		var result = Transform(
			"Sample.cs",
			source,
			CodeTransformKinds.Bodies | CodeTransformKinds.BlankLines);

		Assert.Equal(
			"internal static class Sample\n{\n    internal static void Run()\n    { }\n}\n",
			result.Text);
		Assert.Contains(
			result.Plan.Edits,
			static edit => edit.Kinds == (CodeTransformKinds.Bodies | CodeTransformKinds.BlankLines));
	}

	[Theory]
	[MemberData(nameof(LanguageCases))]
	public void EveryLanguagePack_UsesTheSameLeafProtectedBlankLineRule(
		string path,
		string source,
		string expected)
	{
		var result = Transform(path, source, CodeTransformKinds.BlankLines);

		Assert.Equal(expected, result.Text);
	}

	[Fact]
	public void FullLanguageCache_IsolatedAcrossAllSevenActiveModes()
	{
		var fullPath = Path.Combine(Path.GetTempPath(), "blank-line-mode-cache.cs");
		var kinds = ActiveModes;
		using var session = new CodeCompressionSession(CodeCompressionTestHarness.CreateCompressor());
		var results = new Dictionary<CodeTransformKinds, string>();

		foreach (var mode in kinds)
			results[mode] = Resolve(session, fullPath, ModeMatrixSource, mode);
		var beforeReuse = session.Diagnostics;

		foreach (var mode in kinds)
			Assert.Equal(results[mode], Resolve(session, fullPath, ModeMatrixSource, mode));
		var afterReuse = session.Diagnostics;

		Assert.Equal(kinds.Length, beforeReuse.AnalysisExecutions);
		Assert.Equal(kinds.Length, beforeReuse.CacheEntries);
		Assert.Equal(beforeReuse.AnalysisExecutions, afterReuse.AnalysisExecutions);
		Assert.Equal(beforeReuse.CacheHits + kinds.Length, afterReuse.CacheHits);
		Assert.Equal(1, session.Snapshot.BlankLineTransformedFiles);
		Assert.Equal(
			kinds.Length,
			kinds.Select(session.GetTransformIdentity).Distinct(StringComparer.Ordinal).Count());
	}

	[Fact]
	public void CommentsOnlyPack_CanonicalizesOnlyModesWithTheSameEffectiveKinds()
	{
		const string source = "/* remove */\n\n.card { color: red; }\n";
		var fullPath = Path.Combine(Path.GetTempPath(), "blank-line-mode-cache.css");
		using var session = new CodeCompressionSession(CodeCompressionTestHarness.CreateCompressor());

		var comments = Resolve(session, fullPath, source, CodeTransformKinds.Comments);
		var commentsWithBodies = Resolve(
			session,
			fullPath,
			source,
			CodeTransformKinds.Bodies | CodeTransformKinds.Comments);
		var blankLines = Resolve(session, fullPath, source, CodeTransformKinds.BlankLines);
		var blankLinesWithBodies = Resolve(
			session,
			fullPath,
			source,
			CodeTransformKinds.Bodies | CodeTransformKinds.BlankLines);
		var both = Resolve(
			session,
			fullPath,
			source,
			CodeTransformKinds.Comments | CodeTransformKinds.BlankLines);
		var all = Resolve(
			session,
			fullPath,
			source,
			CodeTransformKinds.Bodies | CodeTransformKinds.Comments | CodeTransformKinds.BlankLines);
		var diagnostics = session.Diagnostics;

		Assert.Equal(comments, commentsWithBodies);
		Assert.Equal(blankLines, blankLinesWithBodies);
		Assert.Equal(both, all);
		Assert.NotEqual(comments, blankLines);
		Assert.Equal(3, diagnostics.AnalysisExecutions);
		Assert.Equal(3, diagnostics.CacheEntries);
		Assert.Equal(3, diagnostics.CacheHits);
		Assert.Equal(1, session.Snapshot.BlankLineTransformedFiles);
	}

	private const string ModeMatrixSource =
		"// note\n" +
		"class Sample\n" +
		"{\n" +
		"\n" +
		"    void Run()\n" +
		"    {\n" +
		"        Call();\n" +
		"    }\n" +
		"}\n";

	private static readonly CodeTransformKinds[] ActiveModes =
	[
		CodeTransformKinds.Bodies,
		CodeTransformKinds.Comments,
		CodeTransformKinds.BlankLines,
		CodeTransformKinds.Bodies | CodeTransformKinds.Comments,
		CodeTransformKinds.Bodies | CodeTransformKinds.BlankLines,
		CodeTransformKinds.Comments | CodeTransformKinds.BlankLines,
		CodeTransformKinds.Bodies | CodeTransformKinds.Comments | CodeTransformKinds.BlankLines
	];

	public static TheoryData<CodeTransformKinds, string> ModeMatrixCases => new()
	{
		{ CodeTransformKinds.None, ModeMatrixSource },
		{
			CodeTransformKinds.Bodies,
			"// note\nclass Sample\n{\n\n    void Run()\n    { }\n}\n"
		},
		{
			CodeTransformKinds.Comments,
			"class Sample\n{\n\n    void Run()\n    {\n        Call();\n    }\n}\n"
		},
		{
			CodeTransformKinds.BlankLines,
			"// note\nclass Sample\n{\n    void Run()\n    {\n        Call();\n    }\n}\n"
		},
		{
			CodeTransformKinds.Bodies | CodeTransformKinds.Comments,
			"class Sample\n{\n\n    void Run()\n    { }\n}\n"
		},
		{
			CodeTransformKinds.Bodies | CodeTransformKinds.BlankLines,
			"// note\nclass Sample\n{\n    void Run()\n    { }\n}\n"
		},
		{
			CodeTransformKinds.Comments | CodeTransformKinds.BlankLines,
			"class Sample\n{\n    void Run()\n    {\n        Call();\n    }\n}\n"
		},
		{
			CodeTransformKinds.Bodies | CodeTransformKinds.Comments | CodeTransformKinds.BlankLines,
			"class Sample\n{\n    void Run()\n    { }\n}\n"
		}
	};

	public static TheoryData<string, string, string> LanguageCases => new()
	{
		{ "sample.sh", "echo one\n\necho two\n", "echo one\necho two\n" },
		{ "sample.c", "int one;\n\nint two;\n", "int one;\nint two;\n" },
		{ "sample.cpp", "int one;\n\nint two;\n", "int one;\nint two;\n" },
		{ "Sample.cs", "class One {}\n\nclass Two {}\n", "class One {}\nclass Two {}\n" },
		{ "sample.css", ".one {}\n\n.two {}\n", ".one {}\n.two {}\n" },
		{ "sample.go", "package sample\n\nvar Value = 1\n", "package sample\nvar Value = 1\n" },
		{ "sample.html", "<p>one</p>\n\n<p>two</p>\n", "<p>one</p>\n<p>two</p>\n" },
		{ "Sample.java", "class One {}\n\nclass Two {}\n", "class One {}\nclass Two {}\n" },
		{ "sample.js", "const one = 1;\n\nconst two = 2;\n", "const one = 1;\nconst two = 2;\n" },
		{ "Sample.kt", "val one = 1\n\nval two = 2\n", "val one = 1\nval two = 2\n" },
		{ "sample.php", "<?php\n$one = 1;\n\n$two = 2;\n", "<?php\n$one = 1;\n$two = 2;\n" },
		{ "sample.py", "one = 1\n\ntwo = 2\n", "one = 1\ntwo = 2\n" },
		{ "sample.rb", "one = 1\n\ntwo = 2\n", "one = 1\ntwo = 2\n" },
		{ "sample.rs", "static ONE: i32 = 1;\n\nstatic TWO: i32 = 2;\n", "static ONE: i32 = 1;\nstatic TWO: i32 = 2;\n" },
		{ "sample.scala", "val one = 1\n\nval two = 2\n", "val one = 1\nval two = 2\n" },
		{ "sample.toml", "one = 1\n\ntwo = 2\n", "one = 1\ntwo = 2\n" },
		{ "sample.tsx", "const one = 1;\n\nconst two = 2;\n", "const one = 1;\nconst two = 2;\n" },
		{ "sample.ts", "const one = 1;\n\nconst two = 2;\n", "const one = 1;\nconst two = 2;\n" },
		{ "sample.xml", "<one />\n\n<two />\n", "<one />\n<two />\n" },
		{ "sample.yaml", "one: 1\n\ntwo: 2\n", "one: 1\ntwo: 2\n" }
	};

	public static TheoryData<string, string, string> ProtectedLeafCases => new()
	{
		{
			"sample.py",
			"text = \"\"\"first\n\nsecond\"\"\"\n\nvalue = 1\n",
			"text = \"\"\"first\n\nsecond\"\"\"\nvalue = 1\n"
		},
		{
			"sample.js",
			"const text = `first\n\nsecond`;\n\nconst value = 1;\n",
			"const text = `first\n\nsecond`;\nconst value = 1;\n"
		},
		{
			"Sample.cs",
			"internal static class Sample\n{\n    private const string Text = \"\"\"\nfirst\n\nsecond\n\"\"\";\n\n    private const int Value = 1;\n}\n",
			"internal static class Sample\n{\n    private const string Text = \"\"\"\nfirst\n\nsecond\n\"\"\";\n    private const int Value = 1;\n}\n"
		},
		{
			"sample.sh",
			"cat <<'EOF'\nfirst\n\nsecond\nEOF\n\nprintf 'done\\n'\n",
			"cat <<'EOF'\nfirst\n\nsecond\nEOF\nprintf 'done\\n'\n"
		},
		{
			"sample.yaml",
			"script: |\n  first\n\n  second\n\nvalue: 1\n",
			"script: |\n  first\n\n  second\nvalue: 1\n"
		},
		{
			"sample.xml",
			"<root>\n  first\n\n  second\n</root>\n",
			"<root>\n  first\n\n  second\n</root>\n"
		},
		{
			"sample.html",
			"<main>\n  first\n\n  second\n</main>\n",
			"<main>\n  first\n\n  second\n</main>\n"
		}
	};

	public static TheoryData<string, string, string, string, bool> LineContinuationCases()
	{
		const char backslash = '\\';
		var cases = new (string Name, string Path, string Source, string Expected, bool ExpectNoBenefit)[]
		{
			(
				"bash-heredoc",
				"sample.sh",
				$"printf '%s\\n' one {backslash}\n\ntwo\ncat <<'EOF'\nfirst\n\nsecond\nEOF\n\nprintf 'done\\n'\n",
				$"printf '%s\\n' one {backslash}\n\ntwo\ncat <<'EOF'\nfirst\n\nsecond\nEOF\nprintf 'done\\n'\n",
				false),
			(
				"c-preprocessor",
				"sample.c",
				$"#define FIRST value {backslash}\n\n#define SECOND 42\n",
				$"#define FIRST value {backslash}\n\n#define SECOND 42\n",
				false),
			(
				"cpp-preprocessor",
				"sample.cpp",
				$"#define FIRST value {backslash}\n\n#define SECOND 42\n",
				$"#define FIRST value {backslash}\n\n#define SECOND 42\n",
				false),
			(
				"python-malformed",
				"sample.py",
				$"x = 1 + {backslash}\n\n2\n",
				$"x = 1 + {backslash}\n\n2\n",
				true),
			(
				"ruby-malformed",
				"sample.rb",
				$"x = 1 + {backslash}\n\n2\n",
				$"x = 1 + {backslash}\n\n2\n",
				true),
			(
				"csharp-comment",
				"Sample.cs",
				$"// note {backslash}\n\nclass Sample {{ }}\n",
				$"// note {backslash}\n\nclass Sample {{ }}\n",
				false),
			(
				"bash-escaped-backslash",
				"sample.sh",
				$"echo one {backslash}{backslash}\n\necho two\n",
				$"echo one {backslash}{backslash}\n\necho two\n",
				false),
			(
				"leading-blank-line",
				"sample.sh",
				"\necho value\n",
				"echo value\n",
				false),
			(
				"three-blank-lines",
				"sample.sh",
				$"echo one {backslash}\n\n\n\necho two\n",
				$"echo one {backslash}\n\necho two\n",
				false)
		};
		var data = new TheoryData<string, string, string, string, bool>();
		foreach (var item in cases)
		{
			data.Add($"{item.Name}-lf", item.Path, item.Source, item.Expected, item.ExpectNoBenefit);
			data.Add(
				$"{item.Name}-crlf",
				item.Path,
				item.Source.Replace("\n", "\r\n", StringComparison.Ordinal),
				item.Expected.Replace("\n", "\r\n", StringComparison.Ordinal),
				item.ExpectNoBenefit);
		}
		return data;
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

	private static string Resolve(
		CodeCompressionSession session,
		string fullPath,
		string source,
		CodeTransformKinds kinds)
	{
		using var scope = new CodeCompressionContext(Path.GetTempPath(), session, kinds)
			.BeginOutput([fullPath]);
		var result = scope.Transform(
			fullPath,
			Path.GetFileName(fullPath),
			source,
			TestContext.Current.CancellationToken);
		_ = scope.Complete();
		return result.Text;
	}
}
