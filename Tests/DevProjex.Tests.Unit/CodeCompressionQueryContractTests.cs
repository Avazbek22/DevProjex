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
		Assert.NotNull(harness.Preserves);
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
	public void LeadingDocstringPreservationIsEnabledOnlyForPython()
	{
		var python = Assert.Single(
			CodeCompressionTestHarness.LanguagePacks,
			static pack => pack.Id.Equals("python", StringComparison.Ordinal));

		Assert.True(python.PreserveLeadingDocstring);
		Assert.All(
			CodeCompressionTestHarness.LanguagePacks.Where(
				static pack => !pack.Id.Equals("python", StringComparison.Ordinal)),
			static pack => Assert.False(pack.PreserveLeadingDocstring));
	}

	[Fact]
	public void ExpressionBodyStylesMatchTheLanguagePackContract()
	{
		var packs = CodeCompressionTestHarness.LanguagePacks.ToDictionary(
			static pack => pack.Id,
			StringComparer.Ordinal);

		Assert.Equal(ExpressionBodyStyle.Declaration, packs["csharp"].ExpressionBodyStyle);
		Assert.Equal(ExpressionBodyStyle.Inline, packs["javascript"].ExpressionBodyStyle);
		Assert.Equal(ExpressionBodyStyle.Inline, packs["typescript"].ExpressionBodyStyle);
		Assert.Equal(ExpressionBodyStyle.Inline, packs["tsx"].ExpressionBodyStyle);
		Assert.All(
			packs.Values.Where(static pack => pack.ExpressionBodyStyle != ExpressionBodyStyle.None),
			static pack => Assert.Contains("@expression", pack.BodiesQuery, StringComparison.Ordinal));
		Assert.All(
			packs.Values.Where(static pack => pack.Id is not ("csharp" or "javascript" or "typescript" or "tsx")),
			static pack =>
			{
				Assert.Equal(ExpressionBodyStyle.None, pack.ExpressionBodyStyle);
				Assert.DoesNotContain("@expression", pack.BodiesQuery, StringComparison.Ordinal);
			});
	}

	[Fact]
	public void CSharpExecutableOwnersContainOnlyNamedBlockForms()
	{
		using var harness = CodeCompressionTestHarness.For("csharp");

		Assert.Contains("method_declaration", harness.Pack.ExecutableOwnerKinds);
		Assert.Contains("accessor_declaration", harness.Pack.ExecutableOwnerKinds);
		Assert.DoesNotContain("lambda_expression", harness.Pack.ExecutableOwnerKinds);
		Assert.DoesNotContain("anonymous_method_expression", harness.Pack.ExecutableOwnerKinds);
		Assert.DoesNotContain("property_declaration", harness.Pack.ExecutableOwnerKinds);
		Assert.DoesNotContain("indexer_declaration", harness.Pack.ExecutableOwnerKinds);
		Assert.DoesNotContain("field_declaration", harness.Pack.ExecutableOwnerKinds);
		Assert.DoesNotContain("event_field_declaration", harness.Pack.ExecutableOwnerKinds);
	}

	[Fact]
	public void CSharpFieldAndEventDeclarationsCaptureOnlyDeclaratorNames()
	{
		const string source = """
			internal sealed class Coordinator
			{
			    private readonly System.Func<int> _factory = Create, _fallback = () => Fallback();
			    private event System.Action? Changed = Handle;
			    private static int Create() => 42;
			    private static int Fallback() => 0;
			    private static void Handle() { }
			}
			""";
		using var harness = CodeCompressionTestHarness.For("csharp");
		using var tree = harness.Parser.Parse(source)!;
		using var cursor = harness.Declarations.Execute(tree.RootNode);
		var declarationNames = cursor.Matches
			.Select(match => new
			{
				Kind = match.Captures
					.Single(static capture => capture.Name.Equals("declaration", StringComparison.Ordinal))
					.Node.Type,
				Names = match.Captures
					.Where(static capture => capture.Name.Equals("name", StringComparison.Ordinal))
					.Select(static capture => capture.Node.Text)
					.ToArray()
			})
			.Where(static capture => capture.Kind is "field_declaration" or "event_field_declaration")
			.SelectMany(static capture => capture.Names.Select(name => (capture.Kind, Name: name)))
			.OrderBy(static capture => capture.Kind, StringComparer.Ordinal)
			.ThenBy(static capture => capture.Name, StringComparer.Ordinal)
			.ToArray();

		Assert.Equal(
			[
				("event_field_declaration", "Changed"),
				("field_declaration", "_factory"),
				("field_declaration", "_fallback")
			],
			declarationNames);
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
	}
}
