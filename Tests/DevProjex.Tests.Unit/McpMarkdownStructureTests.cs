using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpMarkdownStructureTests
{
	[Theory]
	[InlineData("    ```")]
	[InlineData("\t~~~")]
	[InlineData("```language`text")]
	public void InvalidFenceOpeningsDoNotHideFollowingHeadings(string opening)
	{
		var source = string.Join('\n', "# Before", opening, "# Visible", "# After");
		var outline = Assert.IsType<McpLargeFileOutline>(McpLargeFileOutlineBuilder.Build("sample.md", source,
			static () => [], TestContext.Current.CancellationToken, requireLargeFile: false));
		Assert.Equal("1-2 Before\n3-3 Visible\n4-4 After\n", outline.Map);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	public void HeadingsAcceptUpToThreeLeadingSpacesWithoutAcceptingIndentedCode(int indentation)
	{
		var indent = new string(' ', indentation);
		var source = $"{indent}# First\r\ntext\n{indent}## Child\r\ntext\n    # Code\n{indent}# Last";
		var outline = Assert.IsType<McpLargeFileOutline>(McpLargeFileOutlineBuilder.Build("sample.md", source,
			static () => [], TestContext.Current.CancellationToken, requireLargeFile: false));
		Assert.Equal("1-5 First\n  3-5 Child\n6-6 Last\n", outline.Map);
	}

	[Theory]
	[InlineData("```text", "~~~", "```")]
	[InlineData("~~~text", "```", "~~~")]
	[InlineData("````text", "```", "````")]
	[InlineData("~~~~text", "~~~", "~~~~")]
	[InlineData("```text", "```still code", "```")]
	[InlineData("~~~text", "~~~still code", "~~~")]
	public void OnlyAMatchingFenceCanExposeTheFollowingHeading(string opening, string inside, string closing)
	{
		foreach (var ending in new[] { "\n", "\r\n", "\r" })
		{
			var source = string.Join(ending, "# Before", opening, inside, "# Hidden", closing, "# After");
			var outline = Assert.IsType<McpLargeFileOutline>(McpLargeFileOutlineBuilder.Build("sample.md", source,
				static () => [], TestContext.Current.CancellationToken, requireLargeFile: false));
			Assert.Equal("1-5 Before\n6-6 After\n", outline.Map);
			Assert.Equal(2, outline.Entries);
		}
	}

	[Theory]
	[InlineData("```", "````")]
	[InlineData("~~~", "~~~~")]
	[InlineData("  ```language", "   ```  ")]
	[InlineData("   ~~~language", " ~~~\t")]
	public void ALongerOrIndentedMatchingFenceClosesTheBlock(string opening, string closing)
	{
		var source = string.Join('\n', "# Before", opening, "# Hidden", closing, "# After");
		var outline = Assert.IsType<McpLargeFileOutline>(McpLargeFileOutlineBuilder.Build("sample.mdx", source,
			static () => [], TestContext.Current.CancellationToken, requireLargeFile: false));
		Assert.Equal("1-4 Before\n5-5 After\n", outline.Map);
	}
}
