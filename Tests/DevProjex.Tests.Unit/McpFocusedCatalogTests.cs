using DevProjex.Mcp;
using System.Text.RegularExpressions;

namespace DevProjex.Tests.Unit;

public sealed class McpFocusedCatalogTests
{
	[Fact]
	public void EveryStartupConfigurationNamesOnlyPublishedToolsAndParameters()
	{
		using var workspace = new TemporaryDirectory();
		using var packs = new McpPackRegistry(workspace.Path);
		var target = new DevProjexMcpTools(new McpRootRegistry([workspace.Path]),
			new Lazy<McpProjectService>(() => throw new InvalidOperationException()), packs);
		var universe = new[] { "list_projects", "get_tree", "analyze", "pack_context", "read_pack", "search_project", "related_files", "get_file", "get_diff" };
		var parameterNames = new[] { "tracked_only", "git_scope", "max_file_bytes", "profile", "exclude_patterns", "paths", "include_patterns", "pattern", "max_depth", "format", "start_line", "end_line", "start_column", "symbol", "requests", "context_lines", "ignore_case", "symbols", "max_results", "direction", "pack_id" };
		foreach (var toolSet in new[] { McpToolSet.Full, McpToolSet.Reduced })
			foreach (var count in new[] { 1, 2 })
				foreach (var remote in new[] { false, true })
					foreach (var exclusions in new[] { false, true })
						foreach (var live in new[] { false, true })
						{
							var tools = new DevProjexMcpToolCatalog(target, remote, exclusions, toolSet, live: live, rootCount: count)
								.Select(static tool => tool.ProtocolTool).ToDictionary(static tool => tool.Name);
							var instructions = McpServerHost.BuildInstructions(count, toolSet, live: live, allowRemote: remote);
							foreach (var name in universe.Except(tools.Keys))
							{
								Assert.DoesNotContain(name, instructions, StringComparison.Ordinal);
								foreach (var tool in tools.Values)
								{
									Assert.DoesNotContain(name, tool.Description!, StringComparison.Ordinal);
									Assert.DoesNotContain(name, tool.InputSchema.GetRawText(), StringComparison.Ordinal);
								}
							}
							foreach (var tool in tools.Values)
							{
								Assert.InRange(tool.Description!.Length, 1, 800);
								Assert.InRange(tool.Description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, 40, 100);
								foreach (var sentence in (tool.Name == "list_projects" ? Array.Empty<string>() : tool.Description.Split(". ", StringSplitOptions.RemoveEmptyEntries)))
									foreach (var name in parameterNames.Where(name =>
										(name.Contains('_') || sentence.StartsWith("Key parameters:", StringComparison.Ordinal) ||
											Regex.IsMatch(sentence, @"\b" + name + "=") ||
											Regex.IsMatch(sentence, "^" + name + @" (reads|selects|mixes)\b")) &&
										Regex.IsMatch(sentence, @"\b" + name + @"\b")))
										Assert.True(tool.InputSchema.GetProperty("properties").TryGetProperty(name, out _) ||
											tools.Values.Any(neighbor => sentence.Contains(neighbor.Name, StringComparison.Ordinal) &&
												neighbor.InputSchema.GetProperty("properties").TryGetProperty(name, out _)), $"{tool.Name}: {sentence}");
							}
						}
	}

	[Fact]
	public void ReducedInstructionsRemoveDiscoveryOnlyForOneLocalRootWithoutRemote()
	{
		var one = McpServerHost.BuildInstructions(1, McpToolSet.Reduced);
		Assert.StartsWith("One local root is configured: omit project and use project-relative paths. ", one, StringComparison.Ordinal);
		Assert.DoesNotContain("list_projects", one, StringComparison.Ordinal);
		Assert.Equal(966, one.Length);
		Assert.Contains("list_projects reports startup defaults, not live-profile settings.",
			McpServerHost.BuildInstructions(1, McpToolSet.Reduced, allowRemote: true), StringComparison.Ordinal);
		Assert.Contains("When the project is unknown, use list_projects.",
			McpServerHost.BuildInstructions(2, McpToolSet.Reduced), StringComparison.Ordinal);
		Assert.Equal(McpServerHost.BuildInstructions(1), McpServerHost.BuildInstructions(1, allowRemote: true));
	}
}
