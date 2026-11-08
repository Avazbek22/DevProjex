using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Unit;

public sealed class DependencyPythonNavigationTests
{
	[Fact]
	public void ModuleAssignmentsHaveExactNavigationRangesWithoutCreatingDependencyDeclarations()
	{
		const string source = """
			DEFAULT_TIMEOUT = 5
			ANNOTATED: tuple[int, int] = (
			    1, 2
			)
			FIRST, SECOND = (1, 2)
			MISSING: int
			class Holder:
			    CLASS_FIELD = 1
			def run():
			    LOCAL = 1
			""";
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, source);

		foreach (var (name, start, end) in new[] { ("DEFAULT_TIMEOUT", 1, 1), ("ANNOTATED", 2, 4), ("FIRST", 5, 5), ("SECOND", 5, 5) })
		{
			var declaration = Assert.Single(facts.NavigationDeclarations, item => item.Name == name);
			Assert.Equal(NavigationSymbolKind.Field, declaration.Kind);
			Assert.Null(declaration.Owner);
			Assert.Equal((start, end), (declaration.StartLine, declaration.EndLine));
		}
		Assert.DoesNotContain(facts.NavigationDeclarations, item => item.Name is "MISSING" or "CLASS_FIELD" or "LOCAL");
		Assert.Equal(2, facts.Declarations.Count);
		Assert.Equal(facts.NavigationDeclarations,
			extractor.ExtractNavigation("constants.py", source, "snapshot", TestContext.Current.CancellationToken));
		var annotation = Assert.Single(facts.NavigationDeclarations, item => item.Name == "ANNOTATED");
		Assert.Equal("ANNOTATED: tuple[int, int] = (\n    1, 2\n)", source[annotation.StartIndex..annotation.EndIndex]);
	}

	[Theory]
	[InlineData("del CONFIG['key']", true)]
	[InlineData("del CONFIG.attr", true)]
	[InlineData("del (CONFIG['key'], OTHER.attr)", true)]
	[InlineData("del CONFIG", false)]
	[InlineData("del (CONFIG)", false)]
	[InlineData("del CONFIG, OTHER", false)]
	[InlineData("del (CONFIG,\n    OTHER)", false)]
	[InlineData("del (CONFIG, # remove the binding\n    OTHER)", false)]
	public void DeletionUnbindsNamesButDoesNotUnbindTheOwnerOfAnAttributeOrItem(string deletion, bool present)
	{
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, "CONFIG = {}\nOTHER = object()\n" + deletion + "\n");

		Assert.Equal(present, facts.NavigationDeclarations.Any(item => item.Name == "CONFIG"));
	}

	[Fact]
	public void TupleAndChainedBindingsKeepEveryNameAndTheLastUnconditionalAssignment()
	{
		const string source = """
			FIRST, (SECOND, *REST) = (1, (2, 3, 4))
			CHAIN = OTHER = 5
			del FIRST
			FIRST = 6
			OTHER: int
			""";
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, source);

		Assert.Equal(new[] { "CHAIN", "FIRST", "OTHER", "REST", "SECOND" },
			facts.NavigationDeclarations.Select(item => item.Name).Order(StringComparer.Ordinal));
		Assert.Equal(4, Assert.Single(facts.NavigationDeclarations, item => item.Name == "FIRST").StartLine);
		Assert.Equal(2, Assert.Single(facts.NavigationDeclarations, item => item.Name == "OTHER").StartLine);
	}

	[Fact]
	public void ConditionalBindingsDoNotReplaceTheLastCertainModuleDeclaration()
	{
		const string source = """
			UNCERTAIN = 1
			if condition:
			    UNCERTAIN = 2
			    CONDITIONAL_ONLY = 3
			CERTAIN = 4
			def local():
			    CERTAIN = 5
			REBOUND = 6
			def REBOUND():
			    pass
			def REPLACED():
			    pass
			REPLACED = 7
			IMPORTED = 8
			import elsewhere as IMPORTED
			""";
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, source);

		Assert.DoesNotContain(facts.NavigationDeclarations, item => item.Name is "UNCERTAIN" or "CONDITIONAL_ONLY" or "IMPORTED");
		Assert.Equal(5, Assert.Single(facts.NavigationDeclarations, item => item.Name == "CERTAIN").StartLine);
		Assert.Equal(NavigationSymbolKind.Function, Assert.Single(facts.NavigationDeclarations, item => item.Name == "REBOUND").Kind);
		Assert.Equal(13, Assert.Single(facts.NavigationDeclarations, item => item.Name == "REPLACED").StartLine);
	}

	[Fact]
	public void ConditionalImportShadowsAnEarlierModuleAssignmentWithoutChangingDependencyResolution()
	{
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, "CONFIG = 1\nif condition:\n    from elsewhere import CONFIG\nRETAINED = 2\n");

		Assert.DoesNotContain(facts.NavigationDeclarations, item => item.Name == "CONFIG");
		Assert.Equal(4, Assert.Single(facts.NavigationDeclarations, item => item.Name == "RETAINED").StartLine);
		Assert.Empty(facts.Declarations);
		Assert.Single(facts.Imports);
	}

	[Theory]
	[InlineData("for CONFIG in values:\n    pass", false)]
	[InlineData("class CONFIG:\n    pass", false)]
	[InlineData("@decorate\ndef CONFIG():\n    pass", false)]
	[InlineData("from elsewhere import *", false)]
	[InlineData("if condition:\n    def local():\n        CONFIG = 2", true)]
	[InlineData("CONFIG.attr = 2", true)]
	[InlineData("CONFIG['key'] = 2", true)]
	public void OnlyModuleNameBindingsCanReplaceAModuleAssignment(string followingStatement, bool present)
	{
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, "CONFIG = {}\n" + followingStatement + "\n");

		Assert.Equal(present, facts.NavigationDeclarations.Any(item => item.Name == "CONFIG" && item.Kind == NavigationSymbolKind.Field));
	}

	private static FileFacts Extract(TreeSitterDependencyFactExtractor extractor, string source) =>
		extractor.Extract(new PreparedDependencySource("constants.py", "constants.py", "fixture", LanguageId.Python,
			"snapshot", "fixture", source), new DependencyFactsLimits(), TestContext.Current.CancellationToken);
}
