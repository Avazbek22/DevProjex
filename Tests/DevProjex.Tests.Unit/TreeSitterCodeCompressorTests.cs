using System.Diagnostics;
using DevProjex.Application.Compression;
using DevProjex.Infrastructure.Compression;
using TreeSitter;

namespace DevProjex.Tests.Unit;

public sealed class TreeSitterCodeCompressorTests
{
	public static TheoryData<string> ShippedLanguages()
	{
		var data = new TheoryData<string>();
		foreach (var pack in CodeCompressionTestHarness.LanguagePacks)
		{
			if ((pack.TransformCapabilities & CodeTransformKinds.Bodies) != 0)
				data.Add(pack.Id);
		}
		return data;
	}

	private static (CodeCompressionPlan Plan, string Text) Compress(string relativePath, string source)
	{
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var scope = compressor.CreateScope(Path.GetTempPath());
		var plan = scope.Analyze(relativePath, relativePath, source, TestContext.Current.CancellationToken).Plan;
		return (plan, plan.Apply(source).Text);
	}

	[Fact]
	public void ShippedLanguageCatalog_MatchesTheCapabilityAwareProductContract()
	{
		Assert.Equal(
			["bash", "c", "cpp", "csharp", "css", "go", "html", "java", "javascript", "kotlin", "php", "python", "ruby", "rust", "scala", "toml", "tsx", "typescript", "xml", "yaml"],
			CodeCompressionTestHarness.LanguageIds.Order(StringComparer.Ordinal));
		Assert.Equal(
			["c", "cpp", "csharp", "go", "java", "javascript", "kotlin", "php", "python", "ruby", "rust", "scala", "tsx", "typescript"],
			CodeCompressionTestHarness.LanguagePacks
				.Where(static pack => (pack.TransformCapabilities & CodeTransformKinds.Bodies) != 0)
				.Select(static pack => pack.Id)
				.Order(StringComparer.Ordinal));
	}

	[Fact]
	public void CSharp_RemovesBodiesAndKeepsEveryDeclaration()
	{
		var (plan, text) = Compress("Widget.cs", CodeCompressionFixtures.CSharp);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.True(plan.SavedCharacters > 0);

		foreach (var declaration in new[]
		         {
			         "namespace Sample.Services", "class Widget", "IStore _store", "Key",
			         "Widget(IStore store)", "Count", "Names", "SumAsync", "Describe",
			         "enum Mode", "Fast", "Slow", "class Nested", "Work"
		         })
		{
			Assert.Contains(declaration, text, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void CSharp_DropsImplementationButNotStructure()
	{
		var (_, text) = Compress("Widget.cs", CodeCompressionFixtures.CSharp);

		Assert.DoesNotContain("foreach (var value in values)", text, StringComparison.Ordinal);
		Assert.DoesNotContain("implementation comment", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Console.WriteLine", text, StringComparison.Ordinal);
		// The nested type's own body is a container: its members must survive even though the
		// method inside it is emptied.
		Assert.Contains("public void Work()", text, StringComparison.Ordinal);
	}

	[Fact]
	public void CSharp_LocalFunctionInsideARemovedBody_DoesNotRejectTheFile()
	{
		// The local function legitimately disappears with the body. If the gate compared raw
		// declaration sets this file would be refused, and the refusal would be silent.
		var (plan, text) = Compress("Widget.cs", CodeCompressionFixtures.CSharp);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.DoesNotContain("static int Double(int value)", text, StringComparison.Ordinal);
	}

	[Fact]
	public void CSharp_ConversionOperatorsAndCustomEventAccessors_DoNotRejectTheFile()
	{
		const string source = """
			public sealed class Widget
			{
			    private int _value;

			    public static implicit operator int(Widget value)
			    {
			        var converted = value._value + 10;
			        return converted;
			    }

			    public event System.EventHandler Changed
			    {
			        add
			        {
			            System.Console.WriteLine("adding handler");
			        }
			        remove
			        {
			            System.Console.WriteLine("removing handler");
			        }
			    }

			    public void Run()
			    {
			        System.Console.WriteLine("running");
			    }
			}
			""";

		var (plan, text) = Compress("Widget.cs", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.DoesNotContain("converted =", text, StringComparison.Ordinal);
		Assert.DoesNotContain("adding handler", text, StringComparison.Ordinal);
		Assert.DoesNotContain("removing handler", text, StringComparison.Ordinal);
		Assert.DoesNotContain("running", text, StringComparison.Ordinal);
	}

	[Fact]
	public void CSharp_ConditionalAttributesDoNotTurnConstructorSignaturesIntoBodies()
	{
		const string source = """
			using System;
			using System.Runtime.Serialization;

			public sealed class ValidationException : Exception
			{
			    public ValidationException(string message) : base(message)
			    {
			        Console.WriteLine("ordinary implementation");
			    }

			#if NET8_0_OR_GREATER
			    [Obsolete(DiagnosticId = "SYSLIB0051")]
			#endif
			    public ValidationException(SerializationInfo info, StreamingContext context) : base(info, context)
			    {
			        Console.WriteLine("conditional implementation");
			    }
			}
			""";

		var (plan, text) = Compress("ValidationException.cs", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains(
			"public ValidationException(SerializationInfo info, StreamingContext context)",
			text,
			StringComparison.Ordinal);
		Assert.DoesNotContain("ordinary implementation", text, StringComparison.Ordinal);
		Assert.DoesNotContain("conditional implementation", text, StringComparison.Ordinal);
	}

	[Fact]
	public void CSharp_MethodGroupFieldInitializerDoesNotDestabilizeTheDeclarationFingerprint()
	{
		const string source = """
			internal sealed class MemoryCoordinator(
			    System.Func<bool> uiReady,
			    System.TimeSpan animationDuration) : System.IDisposable
			{
			    private readonly System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task>
			        _waitForRenderPasses = WaitForRenderPassesAsync;

			    public void Schedule()
			    {
			        _ = System.Threading.Tasks.Task.Run(async () =>
			        {
			            await _waitForRenderPasses(System.Threading.CancellationToken.None);
			            if (uiReady())
			                System.Console.WriteLine(animationDuration);
			        });
			    }

			    private static async System.Threading.Tasks.Task WaitForRenderPassesAsync(
			        System.Threading.CancellationToken cancellationToken)
			    {
			        await System.Threading.Tasks.Task.Yield();
			        cancellationToken.ThrowIfCancellationRequested();
			    }

			    public void Dispose()
			    {
			        System.Console.WriteLine("disposed");
			    }
			}
			""";

		var (plan, text) = Compress("MemoryCoordinator.cs", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("_waitForRenderPasses = WaitForRenderPassesAsync;", text, StringComparison.Ordinal);
		Assert.Contains("public void Schedule()", text, StringComparison.Ordinal);
		Assert.Contains("WaitForRenderPassesAsync(", text, StringComparison.Ordinal);
		Assert.DoesNotContain("System.Console.WriteLine(animationDuration)", text, StringComparison.Ordinal);
		Assert.DoesNotContain("cancellationToken.ThrowIfCancellationRequested", text, StringComparison.Ordinal);
	}

	[Theory]
	[MemberData(nameof(ShippedLanguages))]
	public void EveryShippedLanguage_CompressesItsProductionFixture(string languageId)
	{
		using var harness = CodeCompressionTestHarness.For(languageId);
		using var compressor = CodeCompressionTestHarness.CreateCompressor(harness.Pack);
		using var scope = compressor.CreateScope(Path.GetTempPath());
		var path = $"sample{harness.Pack.Extensions[0]}";

		var analysis = scope.Analyze(
			path,
			path,
			harness.Fixture,
			TestContext.Current.CancellationToken);

		Assert.Equal(CodeCompressionOutcome.Compressed, analysis.Plan.Outcome);
		Assert.True(analysis.GetResult(harness.Fixture).Text.Length < harness.Fixture.Length);
	}

	[Fact]
	public async Task ColdStart_AllShippedLanguagesMaterializeAndCompressConcurrently()
	{
		using var temp = new TemporaryDirectory();
		var locator = new EmbeddedGrammarLibraryLocator(
			typeof(TreeSitterCodeCompressor).Assembly,
			CodeCompressionFactory.EmbeddedResourcePrefix,
			Path.Combine(temp.Path, "grammars"));
		using var compressor = new TreeSitterCodeCompressor(
			locator,
			CodeCompressionTestHarness.LanguagePacks);
		using var scope = compressor.CreateScope(temp.Path);
		using var start = new ManualResetEventSlim();

		var bodyPacks = CodeCompressionTestHarness.LanguagePacks
			.Where(static pack => (pack.TransformCapabilities & CodeTransformKinds.Bodies) != 0)
			.ToArray();
		var tasks = bodyPacks.Select(pack => Task.Run(() =>
		{
			start.Wait(TestContext.Current.CancellationToken);
			var source = CodeCompressionTestHarness.FixtureFor(pack.Id);
			var path = $"sample{pack.Extensions[0]}";
			return scope.Analyze(
				path,
				path,
				source,
				TestContext.Current.CancellationToken).Plan;
		}, TestContext.Current.CancellationToken)).ToArray();

		start.Set();
		var plans = await Task.WhenAll(tasks);

		Assert.All(plans, plan => Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome));
		Assert.Equal(bodyPacks.Length, Directory.GetFiles(locator.RootDirectory).Length);
	}

	[Fact]
	public void FreeCppLambdaWithoutNamedExecutableRemainsUnchanged()
	{
		const string source =
			"auto transform = [](int value) { int doubled = value * 2; int adjusted = doubled + 10; return adjusted; };";

		var (plan, text) = Compress("sample.cpp", source);

		Assert.Equal(CodeCompressionOutcome.UnchangedNoBenefit, plan.Outcome);
		Assert.Equal(source, text);
	}

	[Fact]
	public void Cpp_PreprocessorBeforeClassDoesNotTurnTheClassBodyIntoAFunctionBody()
	{
		const string source = """
			#ifdef _WIN32
			int system_category();
			#else
			inline auto system_category() -> int
			{
			    return 1;
			}
			#endif

			class buffered_file
			{
			private:
			    int descriptor_;

			public:
			    int descriptor() const
			    {
			        return descriptor_;
			    }

			    void close()
			    {
			        descriptor_ = -1;
			    }
			};
			""";

		var (plan, text) = Compress("os.h", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Equal("cpp", plan.LanguageId);
		Assert.Contains("class buffered_file", text, StringComparison.Ordinal);
		Assert.Contains("int descriptor_;", text, StringComparison.Ordinal);
		Assert.Contains("int descriptor() const", text, StringComparison.Ordinal);
		Assert.Contains("void close()", text, StringComparison.Ordinal);
		Assert.DoesNotContain("return descriptor_;", text, StringComparison.Ordinal);
		Assert.DoesNotContain("descriptor_ = -1", text, StringComparison.Ordinal);
	}

	[Fact]
	public void CHeader_WithoutCppConstructsUsesTheCGrammar()
	{
		const string source = """
			#ifndef COUNTER_H
			#define COUNTER_H

			struct counter { int value; };

			static inline int counter_read(const struct counter* counter)
			{
			    return counter->value;
			}

			#endif
			""";

		var (plan, text) = Compress("counter.h", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Equal("c", plan.LanguageId);
		Assert.Contains("struct counter { int value; };", text, StringComparison.Ordinal);
		Assert.Contains("counter_read", text, StringComparison.Ordinal);
		Assert.DoesNotContain("return counter->value", text, StringComparison.Ordinal);
	}

	[Fact]
	public void CHeader_ClassWordOnlyInCommentStillUsesTheCGrammar()
	{
		const string source = """
			/* This class of counters is shared with the C runtime. */
			struct counter { int value; };
			static inline int counter_read(const struct counter* counter)
			{
			    return counter->value;
			}
			""";

		var (plan, text) = Compress("counter.h", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Equal("c", plan.LanguageId);
		Assert.DoesNotContain("return counter->value", text, StringComparison.Ordinal);
	}

	[Fact]
	public void CHeader_CppWordInContinuedPreprocessorDirectiveStillUsesTheCGrammar()
	{
		const string source = """
			#define COUNTER_DESCRIPTION \
			    "This class of counters belongs to the C API"
			struct counter { int value; };
			static inline int counter_read(const struct counter* counter)
			{
			    return counter->value;
			}
			""";

		var (plan, text) = Compress("counter.h", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Equal("c", plan.LanguageId);
		Assert.DoesNotContain("return counter->value", text, StringComparison.Ordinal);
	}

	[Fact]
	public void CppStructOnlyHeader_WithInlineMethodUsesTheCppGrammar()
	{
		const string source = """
			struct counter
			{
			    int value;
			    int read()
			    {
			        return value;
			    }
			};
			""";

		var (plan, text) = Compress("counter.h", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Equal("cpp", plan.LanguageId);
		Assert.Contains("int read()", text, StringComparison.Ordinal);
		Assert.DoesNotContain("return value", text, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("int read() const { return value; }")]
	[InlineData("int read() const noexcept { return value; }")]
	[InlineData("int read() const & { return value; }")]
	[InlineData("auto read() -> int { return value; }")]
	public void CppStructOnlyHeader_WithQualifiedInlineMethodUsesTheCppGrammar(string member)
	{
		var source = $$"""
			struct counter
			{
			    int value;
			    {{member}}
			};
			""";

		var (plan, text) = Compress("counter.h", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Equal("cpp", plan.LanguageId);
		Assert.DoesNotContain("return value", text, StringComparison.Ordinal);
	}

	[Fact]
	public void CSharp_FieldInitializerWithACollectionExpression_Survives()
	{
		// The shipped grammar does not understand C# 12 collection expressions and parses this with
		// a defect. Refusing every such file would cost a quarter of a modern codebase, so the gate
		// tolerates pre-existing defects and only refuses NEW ones.
		var (plan, text) = Compress("Widget.cs", CodeCompressionFixtures.CSharp);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("Names { get; } = [];", text, StringComparison.Ordinal);
	}

	[Fact]
	public void Python_KeepsTheDocstringAndRemovesTheRest()
	{
		var (plan, text) = Compress("model.py", CodeCompressionFixtures.PythonSource);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("Multi-line docstring.", text, StringComparison.Ordinal);
		Assert.Contains("\"\"\"Doc.\"\"\"", text, StringComparison.Ordinal);
		Assert.Contains("self._cache = {}", text, StringComparison.Ordinal);
		Assert.DoesNotContain("return a + b", text, StringComparison.Ordinal);
	}

	[Fact]
	public void Python_LeadingCommentIsNotADocstringAndIsRemoved()
	{
		// The difference between a comment on the first line of a body and a docstring is not
		// obvious, and someone will eventually read this behaviour as a bug. It is not: only a
		// string literal is documentation in Python.
		var (_, text) = Compress("model.py", CodeCompressionFixtures.PythonSource);

		Assert.DoesNotContain("implementation comment, not a docstring", text, StringComparison.Ordinal);
		Assert.Contains("def run(self, data):", text, StringComparison.Ordinal);
	}

	[Fact]
	public void PythonCompression_PreservesCrlfWithoutIntroducingBareLineFeeds()
	{
		const string lfSource = "def run(value):\n    first = value + 1\n    second = first * 2\n    return second\n";
		var source = lfSource.Replace("\n", "\r\n", StringComparison.Ordinal);

		var (plan, text) = Compress("model.py", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
		Assert.Contains("\r\n    ...\r\n", text, StringComparison.Ordinal);
	}

	[Fact]
	public void Python_BodyThatIsOnlyADocstring_StaysValidAndKeepsIt()
	{
		var (plan, text) = Compress("model.py", CodeCompressionFixtures.PythonSource);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("def only_doc(self):", text, StringComparison.Ordinal);
		Assert.Contains("Nothing but documentation.", text, StringComparison.Ordinal);
	}

	[Fact]
	public void Python_NestedClassSuiteIsNeverCollapsed()
	{
		// class_definition and function_definition both use "block" as their body node, so a bare
		// (block) query would delete class suites wholesale.
		var (_, text) = Compress("model.py", CodeCompressionFixtures.PythonSource);

		Assert.Contains("class Inner:", text, StringComparison.Ordinal);
		Assert.Contains("def work(self):", text, StringComparison.Ordinal);
	}

	[Fact]
	public void UnsupportedExtension_IsLeftFullWithAReason()
	{
		var (plan, text) = Compress("notes.md", "# hello\n");

		Assert.Equal(CodeCompressionOutcome.UnchangedUnsupportedLanguage, plan.Outcome);
		Assert.Equal("# hello\n", text);
	}

	[Fact]
	public void FileOverTheParseLimit_IsLeftFullWithAReason()
	{
		// Parsing cannot be aborted once started, so the size cap is the only defence and its
		// refusal must be explainable rather than mysterious.
		var huge = new string('a', TreeSitterCodeCompressor.MaximumParsableCharacters + 1);

		var (plan, text) = Compress("huge.cs", huge);

		Assert.Equal(CodeCompressionOutcome.UnchangedTooLarge, plan.Outcome);
		Assert.Equal(huge.Length, text.Length);
	}

	[Fact]
	public void OversizedAmbiguousHeader_IsRejectedBeforeCppEvidenceScan()
	{
		var huge = "namespace sample { class Widget {}; }" +
		           new string(' ', TreeSitterCodeCompressor.MaximumParsableCharacters);

		var (plan, text) = Compress("huge.h", huge);

		Assert.Equal(CodeCompressionOutcome.UnchangedTooLarge, plan.Outcome);
		Assert.Equal("c", plan.LanguageId);
		Assert.Same(huge, text);
	}

	[Fact]
	public void AMalformedLanguagePackRefusesOneFileRatherThanThrowing()
	{
		// A query capturing overlapping spans is a pack defect. Plan is contracted never to throw
		// for a refusal, so it must cost one uncompressed file, not the whole export.
		var pack = CodeCompressionTestHarness.PackWithOverlappingBodyQuery("csharp");
		using var compressor = CodeCompressionTestHarness.CreateCompressor(pack);
		using var scope = compressor.CreateScope(Path.GetTempPath());

		var plan = scope.Analyze("Widget.cs", "Widget.cs", CodeCompressionFixtures.CSharp, TestContext.Current.CancellationToken).Plan;

		Assert.NotEqual(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Equal(CodeCompressionFixtures.CSharp, plan.Apply(CodeCompressionFixtures.CSharp).Text);
	}

	[Fact]
	public void GeneratedFileBeyondEditBudget_IsReturnedUnchangedWithinBoundedTime()
	{
		var source = new StringBuilder("class Generated {\n");
		for (var index = 0; index <= TreeSitterCodeCompressor.MaximumEditsPerFile; index++)
			source.Append("void M").Append(index).Append("(){Run();}\n");
		source.Append('}');
		Assert.True(source.Length < TreeSitterCodeCompressor.MaximumParsableCharacters);
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var scope = compressor.CreateScope(Path.GetTempPath());
		var stopwatch = Stopwatch.StartNew();

		var analysis = scope.Analyze(
			"Generated.cs",
			"Generated.cs",
			source.ToString(),
			TestContext.Current.CancellationToken);
		stopwatch.Stop();

		Assert.Equal(CodeCompressionOutcome.UnchangedGateRejected, analysis.Plan.Outcome);
		Assert.Empty(analysis.Plan.Edits);
		Assert.True(
			stopwatch.Elapsed < TimeSpan.FromSeconds(20),
			$"Complexity gate took {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");
	}

	[Fact]
	public void QueryMatchLimitExceededDuringLazyEnumeration_RejectsAndCachesOnlyTheSafePlan()
	{
		using var harness = CodeCompressionTestHarness.For("javascript");
		var boundedPack = harness.Pack with
		{
			DeclarationsQuery = harness.Pack.DeclarationsQuery + """

			(array
			  (identifier) @limit_pre
			  (identifier) @limit_post)
			"""
		};
		const uint matchLimit = 32;
		var compressor = new TreeSitterCodeCompressor(
			CodeCompressionTestHarness.CreateLocator(),
			[boundedPack],
			matchLimit);
		using var session = new CodeCompressionSession(compressor);
		var identifiers = string.Join(", ", Enumerable.Range(0, 64).Select(index => $"value{index}"));
		var source = $$"""
			const values = [{{identifiers}}];
			function work() {
			    return 42;
			}
			""";
		using (var query = new Query(harness.Language, boundedPack.DeclarationsQuery))
		using (var tree = harness.Parser.Parse(source)!)
		using (var cursor = new QueryCursor { MatchLimit = matchLimit })
		{
			cursor.Execute(query, tree.RootNode);
			Assert.False(cursor.IsMatchLimitExceeded);
			_ = cursor.Matches.Count();
			Assert.True(cursor.IsMatchLimitExceeded);
		}

		for (var pass = 0; pass < 2; pass++)
		{
			using var scope = session.BeginOutput("project", ["sample.js"]);
			var result = scope.Transform(
				"sample.js",
				"sample.js",
				source,
				TestContext.Current.CancellationToken);
			var snapshot = scope.Complete();

			Assert.Equal(source, result.Text);
			var unchanged = Assert.Single(snapshot.Unchanged);
			Assert.Equal(CodeCompressionOutcome.UnchangedGateRejected, unchanged.Outcome);
		}

		Assert.Equal(1, session.Diagnostics.CacheEntries);
		Assert.Equal(1, session.Diagnostics.CacheHits);
	}

	[Fact]
	public void CommentQueryMatchLimitExceededRejectsTheWholeFileAndCachesTheRefusal()
	{
		using var harness = CodeCompressionTestHarness.For("javascript");
		var boundedPack = harness.Pack with
		{
			CommentsQuery = harness.Pack.CommentsQuery + """

			(array
			  (identifier) @comment
			  (identifier) @comment)
			"""
		};
		const uint matchLimit = 32;
		using var compressor = new TreeSitterCodeCompressor(
			CodeCompressionTestHarness.CreateLocator(),
			[boundedPack],
			matchLimit);
		using var session = new CodeCompressionSession(compressor);
		var identifiers = string.Join(", ", Enumerable.Range(0, 64).Select(index => $"value{index}"));
		var source = $"// docs\nconst values = [{identifiers}];\n";

		for (var pass = 0; pass < 2; pass++)
		{
			using var scope = new CodeCompressionContext(
				"project",
				session,
				CodeTransformKinds.Comments).BeginOutput(["sample.js"]);
			var result = scope.Transform(
				"sample.js",
				"sample.js",
				source,
				TestContext.Current.CancellationToken);
			var snapshot = scope.Complete();

			Assert.Equal(source, result.Text);
			Assert.Equal(
				CodeCompressionOutcome.UnchangedGateRejected,
				Assert.Single(snapshot.Unchanged).Outcome);
		}

		Assert.Equal(1, session.Diagnostics.AnalysisExecutions);
		Assert.Equal(1, session.Diagnostics.CacheEntries);
		Assert.Equal(1, session.Diagnostics.CacheHits);
	}

	[Fact]
	public void CommentQueryIsLazyAndACorruptOptionalQueryDoesNotPoisonBodyCompression()
	{
		using var harness = CodeCompressionTestHarness.For("csharp");
		var pack = harness.Pack with
		{
			CommentsQuery = "(definitely_not_a_csharp_node) @comment"
		};
		using var compressor = CodeCompressionTestHarness.CreateCompressor(pack);

		using (var bodies = compressor.CreateScope(
			       Path.GetTempPath(),
			       CodeTransformKinds.Bodies))
		{
			var analysis = bodies.Analyze(
				"Widget.cs",
				"Widget.cs",
				CodeCompressionFixtures.CSharp,
				TestContext.Current.CancellationToken);
			Assert.Equal(CodeCompressionOutcome.Compressed, analysis.Plan.Outcome);
		}

		using (var comments = compressor.CreateScope(
			       Path.GetTempPath(),
			       CodeTransformKinds.Comments))
		{
			var analysis = comments.Analyze(
				"Widget.cs",
				"Widget.cs",
				CodeCompressionFixtures.CSharp,
				TestContext.Current.CancellationToken);
			Assert.Equal(CodeCompressionOutcome.UnchangedParseFailed, analysis.Plan.Outcome);
		}

		using var secondBodies = compressor.CreateScope(
			Path.GetTempPath(),
			CodeTransformKinds.Bodies);
		var secondAnalysis = secondBodies.Analyze(
			"Widget.cs",
			"Widget.cs",
			CodeCompressionFixtures.CSharp,
			TestContext.Current.CancellationToken);
		Assert.Equal(CodeCompressionOutcome.Compressed, secondAnalysis.Plan.Outcome);
	}

	[Fact]
	public void CompressionIsDeterministic()
	{
		var first = Compress("Widget.cs", CodeCompressionFixtures.CSharp);
		var second = Compress("Widget.cs", CodeCompressionFixtures.CSharp);

		Assert.Equal(first.Text, second.Text);
		Assert.Equal(first.Plan.Edits.Count, second.Plan.Edits.Count);
	}

	[Fact]
	public void GrammarResolutionAndCompiledWorkersAreReusedAcrossOutputScopes()
	{
		using var harness = CodeCompressionTestHarness.For("csharp");
		var locator = new CountingGrammarLibraryLocator(CodeCompressionTestHarness.CreateLocator());
		using var compressor = new TreeSitterCodeCompressor(locator, [harness.Pack]);

		for (var iteration = 0; iteration < 4; iteration++)
		{
			using var scope = compressor.CreateScope(Path.GetTempPath());
			var analysis = scope.Analyze(
				"Widget.cs",
				"Widget.cs",
				CodeCompressionFixtures.CSharp,
				TestContext.Current.CancellationToken);
			Assert.Equal(CodeCompressionOutcome.Compressed, analysis.Plan.Outcome);
		}

		Assert.Equal(1, locator.ResolveCount);
		Assert.Equal(1, compressor.RuntimeDiagnostics.CompiledQuerySets);
		Assert.Equal(1, compressor.RuntimeDiagnostics.MaterializedWorkers);
		Assert.Equal(1, compressor.RuntimeDiagnostics.AvailableWorkers);
		Assert.Equal(0, compressor.RuntimeDiagnostics.LeasedWorkers);
	}

	[Fact]
	public void FullLanguageCatalogLoadsOnlyTheGrammarUsedByTheProject()
	{
		var locator = new CountingGrammarLibraryLocator(CodeCompressionTestHarness.CreateLocator());
		using var compressor = new TreeSitterCodeCompressor(locator);
		using var scope = compressor.CreateScope(Path.GetTempPath());

		var analysis = scope.Analyze(
			"Widget.cs",
			"Widget.cs",
			CodeCompressionFixtures.CSharp,
			TestContext.Current.CancellationToken);

		Assert.Equal(CodeCompressionOutcome.Compressed, analysis.Plan.Outcome);
		Assert.Equal(1, locator.ResolveCount);
		Assert.Equal(1, compressor.RuntimeDiagnostics.CompiledQuerySets);
		Assert.Equal(1, compressor.RuntimeDiagnostics.MaterializedWorkers);
		Assert.Equal(1, compressor.RuntimeDiagnostics.AvailableWorkers);
		Assert.Equal(0, compressor.RuntimeDiagnostics.LeasedWorkers);
	}

	[Fact]
	public async Task ConcurrentAnalysis_SharesCompiledQueriesAndBoundsNativeWorkers()
	{
		using var harness = CodeCompressionTestHarness.For("csharp");
		var locator = new CountingGrammarLibraryLocator(CodeCompressionTestHarness.CreateLocator());
		using var compressor = new TreeSitterCodeCompressor(locator, [harness.Pack]);
		using var scope = compressor.CreateScope(Path.GetTempPath());
		var tasks = Enumerable.Range(0, 64)
			.Select(index => Task.Run(() => scope.Analyze(
				$"Widget{index}.cs",
				$"Widget{index}.cs",
				CodeCompressionFixtures.CSharp,
				TestContext.Current.CancellationToken)))
			.ToArray();

		var analyses = await Task.WhenAll(tasks);

		Assert.All(analyses, analysis =>
			Assert.Equal(CodeCompressionOutcome.Compressed, analysis.Plan.Outcome));
		var diagnostics = compressor.RuntimeDiagnostics;
		Assert.Equal(1, locator.ResolveCount);
		Assert.Equal(1, diagnostics.CompiledQuerySets);
		Assert.InRange(
			diagnostics.MaterializedWorkers,
			1,
			Math.Min(16, Math.Max(1, Environment.ProcessorCount)));
		Assert.Equal(diagnostics.MaterializedWorkers, diagnostics.AvailableWorkers);
		Assert.Equal(0, diagnostics.LeasedWorkers);
	}

	[Fact]
	public async Task MixedLanguageStress_HasGlobalActiveAndPerLanguageRetainedWorkerBounds()
	{
		var locator = new CountingGrammarLibraryLocator(CodeCompressionTestHarness.CreateLocator());
		using var compressor = new TreeSitterCodeCompressor(locator);
		using var scope = compressor.CreateScope(Path.GetTempPath());
		var bodyPacks = CodeCompressionTestHarness.LanguagePacks
			.Where(static pack => (pack.TransformCapabilities & CodeTransformKinds.Bodies) != 0)
			.ToArray();
		var tasks = bodyPacks
			.SelectMany(pack => Enumerable.Range(0, 12).Select(index => (pack, index)))
			.Select(item => Task.Run(() => scope.Analyze(
				$"{item.pack.Id}-{item.index}{item.pack.Extensions[0]}",
				$"{item.pack.Id}-{item.index}{item.pack.Extensions[0]}",
				CodeCompressionTestHarness.FixtureFor(item.pack.Id),
				TestContext.Current.CancellationToken)))
			.ToArray();

		var analyses = await Task.WhenAll(tasks);

		Assert.All(analyses, analysis =>
			Assert.Equal(CodeCompressionOutcome.Compressed, analysis.Plan.Outcome));
		var diagnostics = compressor.RuntimeDiagnostics;
		Assert.Equal(bodyPacks.Length, locator.ResolveCount);
		Assert.Equal(bodyPacks.Length, diagnostics.CompiledQuerySets);
		Assert.Equal(0, diagnostics.LeasedWorkers);
		Assert.Equal(0, diagnostics.GlobalActiveWorkers);
		Assert.InRange(
			diagnostics.GlobalPeakActiveWorkers,
			1,
			diagnostics.GlobalWorkerCapacity);
		Assert.InRange(
			diagnostics.AvailableWorkers,
			bodyPacks.Length,
			bodyPacks.Length * 2);
		Assert.Equal(diagnostics.AvailableWorkers, diagnostics.MaterializedWorkers);
		Assert.Equal(diagnostics.AvailableWorkers, diagnostics.GlobalRetainedWorkers);
		Assert.InRange(
			diagnostics.GlobalRetainedWorkers,
			1,
			diagnostics.GlobalRetainedWorkerCapacity);
	}

	[Fact]
	public void DisposingPoolWithAnActiveLease_DefersSharedRuntimeDisposalUntilReturn()
	{
		using var harness = CodeCompressionTestHarness.For("csharp");
		var pool = new LanguageWorkerPool(CodeCompressionTestHarness.CreateLocator(), harness.Pack);
		var lease = pool.Rent(TestContext.Current.CancellationToken);

		pool.Dispose();

		Assert.Equal(1, pool.Diagnostics.CompiledQuerySets);
		Assert.Equal(1, pool.Diagnostics.LeasedWorkers);
		using (var tree = lease.Worker.Parser.Parse(CodeCompressionFixtures.CSharp))
			Assert.NotNull(tree);

		lease.Dispose();

		Assert.Equal(0, pool.Diagnostics.CompiledQuerySets);
		Assert.Equal(0, pool.Diagnostics.LeasedWorkers);
		Assert.Throws<ObjectDisposedException>(() => pool.Rent(CancellationToken.None));
	}

	[Fact]
	public void TransientRuntimeFailure_IsRetriedByTheNextOutputScope()
	{
		using var harness = CodeCompressionTestHarness.For("csharp");
		var locator = new FailOnceGrammarLibraryLocator(CodeCompressionTestHarness.CreateLocator());
		using var compressor = new TreeSitterCodeCompressor(locator, [harness.Pack]);

		using (var firstScope = compressor.CreateScope(Path.GetTempPath()))
		{
			var first = firstScope.Analyze(
				"Widget.cs",
				"Widget.cs",
				CodeCompressionFixtures.CSharp,
				TestContext.Current.CancellationToken);
			Assert.Equal(CodeCompressionOutcome.UnchangedParseFailed, first.Plan.Outcome);
		}

		using var secondScope = compressor.CreateScope(Path.GetTempPath());
		var second = secondScope.Analyze(
			"Widget.cs",
			"Widget.cs",
			CodeCompressionFixtures.CSharp,
			TestContext.Current.CancellationToken);

		Assert.Equal(CodeCompressionOutcome.Compressed, second.Plan.Outcome);
		Assert.Equal(2, locator.ResolveCount);
		Assert.Equal(1, compressor.RuntimeDiagnostics.CompiledQuerySets);
	}

	[Fact]
	public async Task ConcurrentRetryAfterTransientFailure_CreatesOneRuntime()
	{
		using var harness = CodeCompressionTestHarness.For("csharp");
		var locator = new FailOnceGrammarLibraryLocator(CodeCompressionTestHarness.CreateLocator());
		using var compressor = new TreeSitterCodeCompressor(locator, [harness.Pack]);
		using (var failedScope = compressor.CreateScope(Path.GetTempPath()))
		{
			var failed = failedScope.Analyze(
				"failed.cs",
				"failed.cs",
				CodeCompressionFixtures.CSharp,
				TestContext.Current.CancellationToken);
			Assert.Equal(CodeCompressionOutcome.UnchangedParseFailed, failed.Plan.Outcome);
		}

		using var retryScope = compressor.CreateScope(Path.GetTempPath());
		var tasks = Enumerable.Range(0, 24)
			.Select(index => Task.Run(() => retryScope.Analyze(
				$"Widget{index}.cs",
				$"Widget{index}.cs",
				CodeCompressionFixtures.CSharp,
				TestContext.Current.CancellationToken)))
			.ToArray();

		var analyses = await Task.WhenAll(tasks);

		Assert.All(analyses, analysis =>
			Assert.Equal(CodeCompressionOutcome.Compressed, analysis.Plan.Outcome));
		Assert.Equal(2, locator.ResolveCount);
		Assert.Equal(1, compressor.RuntimeDiagnostics.CompiledQuerySets);
		Assert.InRange(
			compressor.RuntimeDiagnostics.GlobalPeakActiveWorkers,
			1,
			compressor.RuntimeDiagnostics.GlobalWorkerCapacity);
	}

	[Fact]
	public void PermanentQueryCompilationFailure_IsNotRetriedAcrossScopes()
	{
		using var harness = CodeCompressionTestHarness.For("csharp");
		var locator = new CountingGrammarLibraryLocator(CodeCompressionTestHarness.CreateLocator());
		var invalidPack = harness.Pack with { BodiesQuery = "(" };
		using var compressor = new TreeSitterCodeCompressor(locator, [invalidPack]);

		for (var iteration = 0; iteration < 3; iteration++)
		{
			using var scope = compressor.CreateScope(Path.GetTempPath());
			var analysis = scope.Analyze(
				$"Widget{iteration}.cs",
				$"Widget{iteration}.cs",
				CodeCompressionFixtures.CSharp,
				TestContext.Current.CancellationToken);
			Assert.Equal(CodeCompressionOutcome.UnchangedParseFailed, analysis.Plan.Outcome);
		}

		Assert.Equal(1, locator.ResolveCount);
		Assert.Equal(0, compressor.RuntimeDiagnostics.CompiledQuerySets);
	}

	[Fact]
	public void ExistingScopeCompletesAfterCompressorDispose_AndNewScopeIsRejected()
	{
		using var harness = CodeCompressionTestHarness.For("csharp");
		var compressor = new TreeSitterCodeCompressor(
			CodeCompressionTestHarness.CreateLocator(),
			[harness.Pack]);
		using var scope = compressor.CreateScope(Path.GetTempPath());

		compressor.Dispose();

		var analysis = scope.Analyze(
			"Widget.cs",
			"Widget.cs",
			CodeCompressionFixtures.CSharp,
			TestContext.Current.CancellationToken);
		Assert.Equal(CodeCompressionOutcome.Compressed, analysis.Plan.Outcome);
		Assert.Throws<ObjectDisposedException>(() => compressor.CreateScope(Path.GetTempPath()));
	}

	[Fact]
	public void SessionDispose_AllowsExistingTreeSitterOutputScopeToFinishWithoutPublishing()
	{
		using var harness = CodeCompressionTestHarness.For("csharp");
		var compressor = new TreeSitterCodeCompressor(
			CodeCompressionTestHarness.CreateLocator(),
			[harness.Pack]);
		var session = new CodeCompressionSession(compressor);
		using var scope = session.BeginOutput("project", ["Widget.cs"]);
		var publishedSnapshots = 0;
		session.SnapshotPublished += (_, _) => publishedSnapshots++;

		session.Dispose();
		var result = scope.Transform(
			"Widget.cs",
			"Widget.cs",
			CodeCompressionFixtures.CSharp,
			TestContext.Current.CancellationToken);
		var localSnapshot = scope.Complete();

		Assert.DoesNotContain("Console.WriteLine", result.Text, StringComparison.Ordinal);
		Assert.Equal(1, localSnapshot.CompressedFiles);
		Assert.Equal(CodeCompressionSnapshot.Empty, session.Snapshot);
		Assert.Equal(0, publishedSnapshots);
	}

	[Fact]
	public async Task DisposingPool_UnblocksAWaitingRentWithoutLeakingWorkers()
	{
		using var harness = CodeCompressionTestHarness.For("csharp");
		var pool = new LanguageWorkerPool(CodeCompressionTestHarness.CreateLocator(), harness.Pack);
		var leases = Enumerable.Range(0, Math.Clamp(Environment.ProcessorCount, 1, 8))
			.Select(_ => pool.Rent(TestContext.Current.CancellationToken))
			.ToArray();
		var waiting = Task.Run(() => pool.Rent(CancellationToken.None));
		await Task.Delay(50, TestContext.Current.CancellationToken);

		pool.Dispose();

		await Assert.ThrowsAsync<ObjectDisposedException>(async () => await waiting);
		foreach (var lease in leases)
			lease.Dispose();
		Assert.Equal(0, pool.Diagnostics.MaterializedWorkers);
		Assert.Equal(0, pool.Diagnostics.LeasedWorkers);
	}

	[Fact]
	public void CompressedOutputIsNeverLargerThanTheSource()
	{
		foreach (var (path, source) in new[]
		         {
			         ("Widget.cs", CodeCompressionFixtures.CSharp),
			         ("model.py", CodeCompressionFixtures.PythonSource)
		         })
		{
			var (plan, text) = Compress(path, source);
			Assert.True(text.Length <= source.Length, $"{path} grew from {source.Length} to {text.Length}");
			Assert.Equal(plan.TransformedLength, text.Length);
		}
	}

	[Fact]
	public void OffsetsOutsideRemovedBodies_StillPointAtTheSameCharacters()
	{
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var scope = compressor.CreateScope(Path.GetTempPath());
		var source = CodeCompressionFixtures.CSharp;
		var plan = scope.Analyze("Widget.cs", "Widget.cs", source, TestContext.Current.CancellationToken).Plan;
		var applied = plan.Apply(source);

		var checkedOffsets = 0;
		for (var offset = 0; offset < source.Length; offset++)
		{
			if (plan.Edits.Any(edit => offset >= edit.SourceStart && offset < edit.SourceEnd))
				continue;
			Assert.True(applied.Map.TryToTransformed(offset, out var transformed));
			Assert.Equal(source[offset], applied.Text[transformed]);
			checkedOffsets++;
		}

		Assert.True(checkedOffsets > 0);
	}

	private sealed class CountingGrammarLibraryLocator(IGrammarLibraryLocator inner)
		: IGrammarLibraryLocator
	{
		private int _resolveCount;

		public string StrategyName => inner.StrategyName;
		public int ResolveCount => Volatile.Read(ref _resolveCount);
		public IReadOnlyList<string> EnumerateLibraries() => inner.EnumerateLibraries();

		public string Resolve(string libraryBaseName)
		{
			Interlocked.Increment(ref _resolveCount);
			return inner.Resolve(libraryBaseName);
		}
	}

	private sealed class FailOnceGrammarLibraryLocator(IGrammarLibraryLocator inner)
		: IGrammarLibraryLocator
	{
		private int _resolveCount;

		public string StrategyName => inner.StrategyName;
		public int ResolveCount => Volatile.Read(ref _resolveCount);
		public IReadOnlyList<string> EnumerateLibraries() => inner.EnumerateLibraries();

		public string Resolve(string libraryBaseName)
		{
			if (Interlocked.Increment(ref _resolveCount) == 1)
				throw new IOException("Injected transient grammar access failure.");
			return inner.Resolve(libraryBaseName);
		}
	}
}
