using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Unit;

public sealed class DependencyDeclarationGapTests
{
	[Theory]
	[InlineData("types.ts", LanguageId.TypeScript)]
	[InlineData("types.tsx", LanguageId.Tsx)]
	public void CallableInterfaceKeepsItsHeaderAndExactRangeWhenItsBodyNeedsRecovery(string path, LanguageId language)
	{
		const string source = """
			export interface Respond {
			  <T extends string = string>(value: T): Response<T>
			  <T extends string = string>(value: T): Response<T>
			}
			export type After = string
			""";
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, path, language, source);

		var declaration = Assert.Single(facts.Declarations, item => item.Identity.QualifiedName.EndsWith("#Respond", StringComparison.Ordinal));
		Assert.Equal(SymbolKind.Interface, declaration.Identity.SymbolKind);
		Assert.Equal((1, 4), (Assert.Single(declaration.DeclarationSites).Line, declaration.DeclarationSites[0].EndLine));
		var navigation = Assert.Single(facts.NavigationDeclarations, item => item.Name == "Respond");
		Assert.Equal((1, 4), (navigation.StartLine, navigation.EndLine));
		Assert.Equal(source[..source.IndexOf("\nexport type", StringComparison.Ordinal)]["export ".Length..],
			source[navigation.StartIndex..navigation.EndIndex]);
		Assert.DoesNotContain(facts.References, item => item.Name == "Response");
		Assert.Equal(facts.NavigationDeclarations,
			extractor.ExtractNavigation(path, source, "snapshot", TestContext.Current.CancellationToken));
	}

	[Theory]
	[InlineData("functions.ts", LanguageId.TypeScript)]
	[InlineData("functions.tsx", LanguageId.Tsx)]
	[InlineData("functions.js", LanguageId.JavaScript)]
	public void ExportedConstFunctionsHaveDependencyFactsAndExactNavigationRanges(string path, LanguageId language)
	{
		const string source = """
			export const arrow = (value) => {
			  return value
			}
			export const expression = function (value) {
			  return value
			}
			const local = () => 1
			export let mutable = () => 1
			""";
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, path, language, source);

		Assert.Equal(new[] { "arrow", "expression" }, facts.Declarations.Select(item => item.Identity.QualifiedName.Split('#').Last()).Order(StringComparer.Ordinal));
		Assert.All(facts.Declarations, item => Assert.Equal(SymbolKind.Function, item.Identity.SymbolKind));
		Assert.Equal(new[] { (1, 3), (4, 6) }, facts.Declarations.Select(item =>
			(Assert.Single(item.DeclarationSites).Line, item.DeclarationSites[0].EndLine)));
		var arrow = Assert.Single(facts.NavigationDeclarations, item => item.Name == "arrow");
		var expression = Assert.Single(facts.NavigationDeclarations, item => item.Name == "expression");
		Assert.Equal((1, 3), (arrow.StartLine, arrow.EndLine));
		Assert.Equal((4, 6), (expression.StartLine, expression.EndLine));
		Assert.Equal("arrow = (value) => {\n  return value\n}", source[arrow.StartIndex..arrow.EndIndex]);
		Assert.Equal("expression = function (value) {\n  return value\n}", source[expression.StartIndex..expression.EndIndex]);
	}

	private static FileFacts Extract(TreeSitterDependencyFactExtractor extractor, string path, LanguageId language, string source) =>
		extractor.Extract(new PreparedDependencySource(path, path, "fixture", language, "snapshot", "fixture", source),
			new DependencyFactsLimits(), TestContext.Current.CancellationToken);
}
