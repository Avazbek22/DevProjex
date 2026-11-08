using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Unit;

public sealed class DependencyDeclarationGapTests
{
	[Theory]
	[InlineData("types.ts", LanguageId.TypeScript, "\n")]
	[InlineData("types.ts", LanguageId.TypeScript, "\r\n")]
	[InlineData("types.tsx", LanguageId.Tsx, "\n")]
	[InlineData("types.tsx", LanguageId.Tsx, "\r\n")]
	public void CallableInterfaceKeepsItsHeaderAndExactRangeWhenItsBodyNeedsRecovery(string path, LanguageId language, string newline)
	{
		var source = """
			export interface Respond {
			  <T extends string = string>(value: T): Response<T>
			  <T extends string = string>(value: T): Response<T>
			}
			export type After = string
			""".ReplaceLineEndings(newline);
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, path, language, source);

		var declaration = Assert.Single(facts.Declarations, item => item.Identity.QualifiedName.EndsWith("#Respond", StringComparison.Ordinal));
		Assert.Equal(SymbolKind.Interface, declaration.Identity.SymbolKind);
		Assert.Equal((1, 4), (Assert.Single(declaration.DeclarationSites).Line, declaration.DeclarationSites[0].EndLine));
		var navigation = Assert.Single(facts.NavigationDeclarations, item => item.Name == "Respond");
		Assert.Equal((1, 4), (navigation.StartLine, navigation.EndLine));
		Assert.Equal(source["export ".Length..source.IndexOf(newline + "export type", StringComparison.Ordinal)],
			source[navigation.StartIndex..navigation.EndIndex]);
		Assert.DoesNotContain(facts.References, item => item.Name == "Response");
		Assert.Equal(facts.NavigationDeclarations,
			extractor.ExtractNavigation(path, source, "snapshot", TestContext.Current.CancellationToken));
	}

	[Theory]
	[InlineData("functions.ts", LanguageId.TypeScript, "\n")]
	[InlineData("functions.ts", LanguageId.TypeScript, "\r\n")]
	[InlineData("functions.tsx", LanguageId.Tsx, "\n")]
	[InlineData("functions.tsx", LanguageId.Tsx, "\r\n")]
	[InlineData("functions.js", LanguageId.JavaScript, "\n")]
	[InlineData("functions.js", LanguageId.JavaScript, "\r\n")]
	public void ExportedConstFunctionsHaveDependencyFactsAndExactNavigationRanges(string path, LanguageId language, string newline)
	{
		var source = """
			export const arrow = (value) => {
			  return value
			}
			export const expression = function (value) {
			  return value
			}
			const local = () => 1
			export let mutable = () => 1
			""".ReplaceLineEndings(newline);
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
		Assert.Equal("arrow = (value) => {\n  return value\n}".ReplaceLineEndings(newline), source[arrow.StartIndex..arrow.EndIndex]);
		Assert.Equal("expression = function (value) {\n  return value\n}".ReplaceLineEndings(newline), source[expression.StartIndex..expression.EndIndex]);
	}

	[Theory]
	[InlineData("functions.ts", LanguageId.TypeScript)]
	[InlineData("functions.tsx", LanguageId.Tsx)]
	public void ExportedGenericFunctionsKeepTheArityOfTheirInitializer(string path, LanguageId language)
	{
		const string source = """
			export const identity = <T,>(value: T): T => value
			export const pair = function <T, U>(first: T, second: U) {
			  return [first, second]
			}
			""";
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, path, language, source);

		var identity = Assert.Single(facts.Declarations, item => item.Identity.QualifiedName.EndsWith("#identity", StringComparison.Ordinal));
		var pair = Assert.Single(facts.Declarations, item => item.Identity.QualifiedName.EndsWith("#pair", StringComparison.Ordinal));
		Assert.Equal(1, identity.Identity.GenericArity);
		Assert.Equal(2, pair.Identity.GenericArity);
		Assert.Equal((2, 4), (Assert.Single(pair.DeclarationSites).Line, pair.DeclarationSites[0].EndLine));
	}

	private static FileFacts Extract(TreeSitterDependencyFactExtractor extractor, string path, LanguageId language, string source) =>
		extractor.Extract(new PreparedDependencySource(path, path, "fixture", language, "snapshot", "fixture", source),
			new DependencyFactsLimits(), TestContext.Current.CancellationToken);
}
