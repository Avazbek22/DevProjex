using DevProjex.Mcp;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Unit;

public sealed class McpResponseLineEndingPropertiesTests
{
	[Fact]
	public void GeneratedTextBlocksNormalizeOnlyLineBreaksAndNormalizationIsIdempotent()
	{
		const int seed = 71239;
		var random = new Random(seed);
		string[] pieces = ["plain", "\r", "\n", "\r\n", "\r\r\n", "名前🙂", "\ud800", "\udfff", "\u0000", "\t"];
		for (var sample = 0; sample < 300; sample++)
		{
			var input = string.Concat(Enumerable.Range(0, random.Next(50)).Select(_ => pieces[random.Next(pieces.Length)]));
			var expected = new StringBuilder();
			for (var index = 0; index < input.Length; index++)
			{
				if (input[index] != '\r')
					expected.Append(input[index]);
				else
				{
					expected.Append('\n');
					if (index + 1 < input.Length && input[index + 1] == '\n')
						index++;
				}
			}
			var first = new TextContentBlock { Text = input };
			var second = new TextContentBlock { Text = input + "\r\n" };
			var response = new CallToolResult { Content = [first, second], IsError = sample % 2 == 0 };
			McpToolResults.NormalizeLineEndings(response);
			Assert.True(first.Text == expected.ToString(), $"seed={seed}; sample={sample}");
			Assert.Equal(expected + "\n", second.Text);
			Assert.Equal(sample % 2 == 0, response.IsError);
			McpToolResults.NormalizeLineEndings(response);
			Assert.Equal(expected.ToString(), first.Text);
			Assert.Equal(expected + "\n", second.Text);
		}
	}
}
