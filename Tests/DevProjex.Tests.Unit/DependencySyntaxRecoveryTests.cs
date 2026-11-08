using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Unit;

public sealed class DependencySyntaxRecoveryTests
{
	private const string TypeScriptSource = """
		import type { Context } from './context'
		export type Before = Context
		export interface HandlerInterface {
		  <P extends string = string>(handler: H<P>): HonoBase<P>
		  <P extends string = string>(handler: H<P>): HonoBase<P>
		  <P extends string = string>(handler: H<P>): HonoBase<P>
		}
		export type TypedResponse<T = unknown> = { _data: T }
		export type After = TypedResponse
		""";

	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	public void GenericCallSignaturesDropTheirBodyAndKeepTheInterfaceHeaderAndIndependentFacts(string newline)
	{
		var source = TypeScriptSource.ReplaceLineEndings(newline);
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, "types.ts", LanguageId.TypeScript, source);

		Assert.Equal(DependencyFileStatus.Supported, facts.Status);
		Assert.True(facts.HasSyntaxErrors);
		Assert.Contains(facts.Declarations, declaration => declaration.Identity.QualifiedName.EndsWith("#Before", StringComparison.Ordinal));
		Assert.Contains(facts.Declarations, declaration => declaration.Identity.QualifiedName.EndsWith("#TypedResponse", StringComparison.Ordinal));
		Assert.Contains(facts.Declarations, declaration => declaration.Identity.QualifiedName.EndsWith("#After", StringComparison.Ordinal));
		Assert.Single(facts.Imports);
		Assert.DoesNotContain(facts.References, reference => reference.Name is "H" or "HonoBase");
		Assert.Contains(facts.NavigationDeclarations, declaration => declaration.Name == "HandlerInterface" && declaration.StartLine == 3 && declaration.EndLine == 7);
		Assert.Contains(facts.NavigationDeclarations, declaration => declaration.Name == "TypedResponse" && declaration.StartLine == 8);
		var partial = Assert.IsType<DependencyPartialParseDiagnostic>(facts.PartialParse);
		Assert.Equal(1, partial.DroppedConstructs);
		Assert.Equal(new DependencySourceLineRange(3, 7), Assert.Single(partial.Ranges));
		Assert.False(partial.RangesTruncated);
		Assert.Equal(facts.NavigationDeclarations,
			extractor.ExtractNavigation("types.ts", source, "snapshot", TestContext.Current.CancellationToken));
	}

	[Fact]
	public void RecoveryKeepsDeclarationsOnBothSidesOfAWideDamagedRoot()
	{
		var prefix = string.Concat(Enumerable.Range(0, 400).Select(index => $"export type Before{index} = string\n"));
		var suffix = string.Concat(Enumerable.Range(0, 400).Select(index => $"export type Tail{index} = Before{index}\n"));
		var source = prefix + TypeScriptSource + "\n" + suffix;
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, "types.ts", LanguageId.TypeScript, source);

		Assert.Equal(DependencyFileStatus.Supported, facts.Status);
		Assert.Equal(804, facts.Declarations.Count);
		Assert.Contains(facts.Declarations, declaration => declaration.Identity.QualifiedName.EndsWith("#Before399", StringComparison.Ordinal));
		Assert.Contains(facts.Declarations, declaration => declaration.Identity.QualifiedName.EndsWith("#Tail399", StringComparison.Ordinal));
		Assert.Contains(facts.NavigationDeclarations, declaration => declaration.Name == "TypedResponse" && declaration.StartLine == 408);
		Assert.Equal(new DependencySourceLineRange(403, 407), Assert.Single(facts.PartialParse!.Ranges));
	}

	[Fact]
	public void ExplicitSignatureSeparatorsNeedNoRecovery()
	{
		var source = TypeScriptSource.Replace("HonoBase<P>", "HonoBase<P>;", StringComparison.Ordinal);
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, "types.ts", LanguageId.TypeScript, source);

		Assert.Equal(DependencyFileStatus.Supported, facts.Status);
		Assert.False(facts.HasSyntaxErrors);
		Assert.Null(facts.PartialParse);
		Assert.Contains(facts.NavigationDeclarations, declaration => declaration.Name == "HandlerInterface");
		Assert.Equal(1, extractor.ParseCount);
	}

	[Fact]
	public void RealDelimiterTokensIgnoreBracesInCommentsAndStringsAndPreserveUnicodeOffsets()
	{
		var source = "// Кириллица 😀\n" + TypeScriptSource.Replace("export interface HandlerInterface {",
			"export interface HandlerInterface {\n  // } {\n  label: '}'", StringComparison.Ordinal);
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, "types.ts", LanguageId.TypeScript, source);

		Assert.Contains(facts.Declarations, declaration => declaration.Identity.QualifiedName.EndsWith("#After", StringComparison.Ordinal));
		var typed = Assert.Single(facts.NavigationDeclarations, declaration => declaration.Name == "TypedResponse");
		Assert.Equal("type TypedResponse<T = unknown> = { _data: T }", source[typed.StartIndex..typed.EndIndex]);
		Assert.Equal(new DependencySourceLineRange(4, 10), Assert.Single(facts.PartialParse!.Ranges));
	}

	[Fact]
	public void RecoveryDoesNotMistakeATemplateSubstitutionForTheConstructionCloser()
	{
		var source = TypeScriptSource.Replace("HandlerInterface {", "HandlerInterface {\n  template: `route/${string}`;", StringComparison.Ordinal);
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, "types.ts", LanguageId.TypeScript, source);

		Assert.Contains(facts.NavigationDeclarations, declaration => declaration.Name == "TypedResponse");
		Assert.Equal(new DependencySourceLineRange(3, 8), Assert.Single(facts.PartialParse!.Ranges));
	}

	[Fact]
	public void AFileContainingOnlyTheDamagedConstructionStillHasNoFacts()
	{
		var source = string.Join('\n', TypeScriptSource.Split('\n').Skip(2).Take(5))
			.Replace("interface HandlerInterface", "interface", StringComparison.Ordinal);
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, "broken.ts", LanguageId.TypeScript, source);

		Assert.Equal(DependencyFileStatus.ExtractionFailed, facts.Status);
		Assert.Equal("syntax tree contains errors", facts.StatusReason);
		Assert.Empty(facts.Declarations);
		Assert.Empty(facts.Imports);
		Assert.Empty(facts.References);
		Assert.Empty(facts.NavigationDeclarations);
		Assert.Equal(new DependencySourceLineRange(1, 5), Assert.Single(facts.PartialParse!.Ranges));
	}

	[Fact]
	public void CSharpDamageKeepsImportsAndDeclarationsOnBothSides()
	{
		const string source = "using System;\nclass Before {}\nclass Broken { int Value }\nclass After {}\n";
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, "types.cs", LanguageId.CSharp, source);

		Assert.Equal(DependencyFileStatus.Supported, facts.Status);
		Assert.Contains(facts.Declarations, declaration => declaration.Identity.QualifiedName == "Before");
		Assert.Contains(facts.Declarations, declaration => declaration.Identity.QualifiedName == "After");
		Assert.Contains("System", facts.ContextNamespaces);
		Assert.DoesNotContain(facts.NavigationDeclarations, declaration => declaration.Name == "Broken");
		Assert.Equal(new DependencySourceLineRange(3, 3), Assert.Single(facts.PartialParse!.Ranges));
	}

	private static FileFacts Extract(TreeSitterDependencyFactExtractor extractor, string path, LanguageId language, string source) =>
		extractor.Extract(new PreparedDependencySource(path, path, "fixture", language, "snapshot", "fixture", source),
			new DependencyFactsLimits(), TestContext.Current.CancellationToken);
}
