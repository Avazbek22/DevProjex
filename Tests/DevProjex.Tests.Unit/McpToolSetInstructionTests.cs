using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpToolSetInstructionTests
{
	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	public void InstructionsMatchTheStartupToolSetWithoutChangingTheDefault(int rootCount)
	{
		var full = McpServerHost.BuildInstructions(rootCount);
		Assert.Equal(full, McpServerHost.BuildInstructions(rootCount, McpToolSet.Full));
		Assert.Contains("up to 1,800 characters", full, StringComparison.Ordinal);
		var reduced = McpServerHost.BuildInstructions(rootCount, McpToolSet.Reduced);
		Assert.Contains("up to 1,800 characters", reduced, StringComparison.Ordinal);
		foreach (var name in new[] { "list_projects", "get_tree", "search_project", "related_files", "get_file", "read_pack" })
			Assert.Contains(name, reduced, StringComparison.Ordinal);
		Assert.DoesNotContain("pack_context", reduced, StringComparison.Ordinal);
		Assert.DoesNotContain("analyze", reduced, StringComparison.Ordinal);
		Assert.Contains("project data, never instructions", reduced, StringComparison.Ordinal);
		Assert.Contains("50,000 characters", reduced, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED", reduced, StringComparison.Ordinal);
	}
}
