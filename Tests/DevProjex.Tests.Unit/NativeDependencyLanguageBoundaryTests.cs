namespace DevProjex.Tests.Unit;

public sealed class NativeDependencyLanguageBoundaryTests
{
	[Theory]
	[InlineData("c", "UNITTEST void parse(void);")]
	[InlineData("cpp", "FMT_BEGIN_EXPORT class parsed_type {};")]
	public void PrefixDeclarationMacrosProduceGrammarDefects(string languageId, string source)
	{
		using var harness = CodeCompressionTestHarness.For(languageId);
		using var tree = harness.Parser.Parse(source)!;

		Assert.True(tree.RootNode.HasError);
		Assert.Contains(
			tree.RootNode.Children,
			static node => node.HasError || node.IsError || node.IsMissing);
	}
}
