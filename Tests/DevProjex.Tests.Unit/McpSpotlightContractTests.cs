using DevProjex.Mcp;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Unit;

public sealed class McpSpotlightContractTests
{
	[Fact]
	public void TextResultRejectsAnUnclosedServerCreatedProjectBlock()
	{
		var wrapped = McpSpotlight.Wrap("project data");
		var truncated = wrapped[..wrapped.LastIndexOf("</untrusted-data-", StringComparison.Ordinal)];

		var exception = Assert.Throws<InvalidOperationException>(() => McpToolResults.TextSuccess(truncated));

		Assert.Contains("not closed", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void TextResultAcceptsSeveralCompleteProjectBlocks()
	{
		var text = McpSpotlight.Wrap("first") + "\n\n" + McpSpotlight.Wrap("second");

		var result = McpToolResults.TextSuccess(text);

		Assert.NotEqual(true, result.IsError);
	}

	[Fact]
	public void CompletedResponseRequiresEveryTextBlockToBeBalancedIndependently()
	{
		var wrapped = McpSpotlight.Wrap("project data");
		var closingAt = wrapped.LastIndexOf("</untrusted-data-", StringComparison.Ordinal);
		var result = new CallToolResult
		{
			Content =
			[
				new TextContentBlock { Text = wrapped[..closingAt] },
				new TextContentBlock { Text = wrapped[closingAt..] }
			]
		};

		Assert.Throws<InvalidOperationException>(() => McpToolResults.EnsureBalanced(result));
	}
}
