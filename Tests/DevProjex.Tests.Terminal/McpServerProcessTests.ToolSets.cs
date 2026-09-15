using ModelContextProtocol;
using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	private static readonly string[] ReducedTools =
		["list_projects", "get_tree", "read_pack", "search_project", "related_files", "get_file"];

	[Theory]
	[InlineData("full", 27_900)]
	[InlineData("reduced", 17_500)]
	public async Task RealProcessPublishesTheSelectedToolSetWithinItsOwnBudget(string toolSet, int ceiling)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/App.cs", "internal sealed class App;\n");
		var (process, client, error, output) = await StartPublishedMcpAsync(
			workspace, project, ["--tool-set", toolSet]);
		try
		{
			var tools = await client.ListToolsAsync(options: null, TestContext.Current.CancellationToken);
			Assert.Equal(toolSet == "full" ? ExpectedTools : ReducedTools, tools.Select(tool => tool.Name));
			foreach (var tool in tools)
			{
				var description = Assert.IsType<string>(tool.Description);
				Assert.InRange(description.Length, 1, 800);
				Assert.InRange(description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, 40, 100);
			}
			var size = ReadToolsListResult(output.GetRecordedText()).Length;
			TestContext.Current.TestOutputHelper!.WriteLine($"{toolSet} catalog characters={size}");
			Assert.InRange(size, 1, ceiling);
			var instructions = Assert.IsType<string>(client.ServerInstructions);
			Assert.Contains("DEVPROJEX_REDACTED", instructions, StringComparison.Ordinal);
			if (toolSet == "reduced")
			{
				Assert.DoesNotContain("analyze", instructions, StringComparison.Ordinal);
				Assert.DoesNotContain("pack_context", instructions, StringComparison.Ordinal);
				foreach (var tool in tools)
				{
					foreach (var hidden in new[] { "analyze", "pack_context" })
					{
						Assert.DoesNotContain(hidden, tool.Description ?? string.Empty, StringComparison.Ordinal);
						Assert.DoesNotContain(hidden, tool.ProtocolTool.InputSchema.GetRawText(), StringComparison.Ordinal);
					}
				}
				foreach (var hidden in new[] { "analyze", "pack_context" })
				{
					var exception = await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync(
						hidden, new Dictionary<string, object?>(), progress: null, options: null,
						TestContext.Current.CancellationToken).AsTask());
					Assert.Contains(hidden, exception.Message, StringComparison.Ordinal);
					Assert.Contains("Unknown tool", exception.Message, StringComparison.Ordinal);
				}
			}
		}
		finally
		{
			process.StandardInput.Close();
			await client.DisposeAsync();
		}
		await CompletePublishedMcpAsync(process, error, output);
	}

	[Fact]
	public async Task RealProcessReadsSearchAndRelatedStoredPagesWithTheReducedToolSet()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		workspace.WriteFile("project/Target.ts", "export class Target {}\n");
		workspace.WriteFile("project/App.ts", "import { Target } from './Target';\n" +
			string.Join("\n", Enumerable.Range(0, 300).Select(index => $"// stored-marker-{index} " + new string('x', 100))));
		for (var index = 0; index < 700; index++)
			workspace.WriteFile($"project/related/very-long-related-target-{index:D4}.ts",
				$"import {{ Target }} from '../Target'; export const value{index} = Target;\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"),
			["--tool-set", "reduced"]);
		foreach (var request in new[]
		{
			("search_project", new Dictionary<string, object?> { ["pattern"] = "stored-marker", ["max_results"] = 200 }),
			("related_files", new Dictionary<string, object?> { ["path"] = "Target.ts", ["direction"] = "dependents" })
		})
		{
			var result = AllProcessText(await CallAsync(server, request.Item1, request.Item2));
			var pattern = request.Item1 == "search_project"
				? @"\[Search stored\] pack_id=([0-9a-f]+)"
				: "Related-files result stored as '([^']+)'";
			var match = Regex.Match(result, pattern, RegexOptions.None, TimeSpan.FromSeconds(2));
			Assert.True(match.Success, result);
			var packId = match.Groups[1].Value;
			var page = await CallAsync(server, "read_pack", new Dictionary<string, object?> { ["pack_id"] = packId });
			Assert.NotEqual(true, page.IsError);
			Assert.Contains(request.Item1 == "search_project" ? "App.ts" : "very-long-related-target-",
				AllProcessText(page), StringComparison.Ordinal);
		}
	}
}
