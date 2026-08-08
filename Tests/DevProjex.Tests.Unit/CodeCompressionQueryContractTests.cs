using DevProjex.Application.Compression;
using DevProjex.Infrastructure.Compression;

namespace DevProjex.Tests.Unit;

/// <summary>
/// Compiles every shipped query against the grammar that actually ships, and proves each one
/// captures something.
///
/// This is not defensive paranoia. A tree-sitter query that references a node type or field the
/// grammar does not have is an "impossible pattern": it fails to COMPILE, and it takes the whole
/// query file down with it, so the language silently stops compressing. DevProjex ships a
/// self-contained single-file binary per RID with no way to patch a query after release, which
/// makes such a mistake unfixable until the next version.
///
/// It has already happened once here: the canonical-looking Python docstring pattern
/// "(block . (expression_statement (string) @doc))" is an impossible pattern against the grammar in
/// TreeSitter.DotNet 1.3.0, which wants a bare "(block . (string) @doc)".
/// </summary>
public sealed class CodeCompressionQueryContractTests
{
	private static readonly string[] ExpectedLanguageIds =
	[
		"c", "cpp", "csharp", "go", "java", "javascript", "python", "rust", "tsx", "typescript"
	];

	[Fact]
	public void ShippedLanguageSetMatchesTheProductContract()
	{
		Assert.Equal(ExpectedLanguageIds, CodeCompressionTestHarness.LanguageIds);
	}

	public static TheoryData<string> LanguageIds()
	{
		var data = new TheoryData<string>();
		foreach (var id in CodeCompressionTestHarness.LanguageIds)
			data.Add(id);
		return data;
	}

	[Theory]
	[MemberData(nameof(LanguageIds))]
	public void EveryQueryCompilesAgainstTheShippedGrammar(string languageId)
	{
		using var harness = CodeCompressionTestHarness.For(languageId);

		// Compiling is the assertion: an impossible pattern throws here rather than at run time.
		Assert.NotNull(harness.Bodies);
		Assert.NotNull(harness.Declarations);
	}

	[Theory]
	[MemberData(nameof(LanguageIds))]
	public void EveryQueryCapturesSomethingOnItsFixture(string languageId)
	{
		using var harness = CodeCompressionTestHarness.For(languageId);

		Assert.True(harness.CountCaptures(harness.Bodies) > 0, "the bodies query captured nothing");
		Assert.True(harness.CountCaptures(harness.Declarations) > 0, "the declarations query captured nothing");
	}

	[Fact]
	public void PythonDocstringQueryUsesTheFormTheShippedGrammarAccepts()
	{
		using var harness = CodeCompressionTestHarness.For("python");

		Assert.NotNull(harness.Docstrings);
		Assert.True(harness.CountCaptures(harness.Docstrings!) > 0);
	}

	[Fact]
	public void CSharpExecutableOwnersNameActualBodiesRatherThanTheirContainingFields()
	{
		using var harness = CodeCompressionTestHarness.For("csharp");

		Assert.Contains("lambda_expression", harness.Pack.ExecutableOwnerKinds);
		Assert.Contains("anonymous_method_expression", harness.Pack.ExecutableOwnerKinds);
		Assert.DoesNotContain("field_declaration", harness.Pack.ExecutableOwnerKinds);
		Assert.DoesNotContain("event_field_declaration", harness.Pack.ExecutableOwnerKinds);
	}

	[Fact]
	public void NoQueryCapturesAContainerBody()
	{
		// The safety list is defence in depth, but if a pattern ever does reach a container the
		// splice would delete a class or namespace body wholesale.
		foreach (var languageId in CodeCompressionTestHarness.LanguageIds)
		{
			using var harness = CodeCompressionTestHarness.For(languageId);
			var captured = harness.CapturedNodeTypes(harness.Bodies);
			var containers = captured.Intersect(harness.Pack.ContainerNodeTypes).ToArray();
			Assert.True(containers.Length == 0, $"{languageId}: bodies query captured container nodes {string.Join(", ", containers)}");
		}
	}

	[Fact]
	public void EveryPackDeclaresAPlaceholderThatIsNotABareEllipsis()
	{
		// A bare "…" is not valid syntax anywhere, so the reverse-parse gate refuses every file and
		// the feature compresses nothing while looking conservative. Measured: 872 of 872 files.
		foreach (var languageId in CodeCompressionTestHarness.LanguageIds)
		{
			using var harness = CodeCompressionTestHarness.For(languageId);
			Assert.NotEqual("…", harness.Pack.BlockPlaceholder.Trim());
			Assert.NotEmpty(harness.Pack.BlockPlaceholder);
		}
	}

	[Theory]
	[MemberData(nameof(LanguageIds))]
	public void ShippedPlaceholdersDoNotExposeCommentSyntax(string languageId)
	{
		using var harness = CodeCompressionTestHarness.For(languageId);

		Assert.DoesNotContain("/*", harness.Pack.BlockPlaceholder, StringComparison.Ordinal);
		Assert.DoesNotContain("*/", harness.Pack.BlockPlaceholder, StringComparison.Ordinal);
		if (harness.Pack.ExpressionPlaceholder is { } expressionPlaceholder)
		{
			Assert.DoesNotContain("/*", expressionPlaceholder, StringComparison.Ordinal);
			Assert.DoesNotContain("*/", expressionPlaceholder, StringComparison.Ordinal);
		}
	}
}
