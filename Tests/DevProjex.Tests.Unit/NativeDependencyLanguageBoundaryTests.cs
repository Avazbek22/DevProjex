namespace DevProjex.Tests.Unit;

public sealed class NativeDependencyLanguageBoundaryTests
{
	[Fact]
	public void UnexpectedBlockProducesAnErrorNode()
	{
		const string source = "public sealed class Broken { public void Run( { } }";
		using var harness = CodeCompressionTestHarness.For("csharp");
		using var tree = harness.Parser.Parse(source)!;

		Assert.True(tree.RootNode.HasError);
		Assert.Contains(Descendants(tree.RootNode), static node => node.IsError);
	}

	[Fact]
	public void OmittedSemicolonProducesAMissingNode()
	{
		const string source = "public sealed class Broken { Target Value }";
		using var harness = CodeCompressionTestHarness.For("csharp");
		using var tree = harness.Parser.Parse(source)!;

		Assert.True(tree.RootNode.HasError);
		Assert.Contains(Descendants(tree.RootNode), static node => node.IsMissing);
	}

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

	private static IEnumerable<TreeSitter.Node> Descendants(TreeSitter.Node root)
	{
		var pending = new Stack<TreeSitter.Node>();
		pending.Push(root);
		while (pending.TryPop(out var node))
		{
			yield return node;
			foreach (var child in node.Children)
				pending.Push(child);
		}
	}
}
