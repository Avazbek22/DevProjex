namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[InlineData("functions.ts")]
	[InlineData("functions.js")]
	public async Task ExportedConstFunctionsRoundTripFromSymbolSearchToTheirExactRanges(string path)
	{
		const string source = """
			export const arrow = (value) => {
			  return value
			}
			export const expression = function (value) {
			  return value
			}
			export const notSelected = 42
			""";
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/" + path, source);
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		foreach (var (name, start, end) in new[] { ("arrow", 1, 3), ("expression", 4, 6) })
		{
			var search = Normalize(AllProcessText(await CallAsync(server, "search_project", new Dictionary<string, object?>
			{
				["pattern"] = name,
				["symbols"] = true,
				["context_lines"] = 0
			})));
			Assert.Contains($"{path} {name} {start}-{end}", search, StringComparison.Ordinal);
			var file = Normalize(AllProcessText(await CallAsync(server, "get_file", new Dictionary<string, object?>
			{
				["path"] = path,
				["symbol"] = name
			})));
			Assert.Contains($"Lines: {start}-{end} of ", file, StringComparison.Ordinal);
			Assert.Contains("return value", file, StringComparison.Ordinal);
			Assert.DoesNotContain("notSelected", file, StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task CallableInterfaceWithRecoveredBodyRoundTripsToItsWholeRange()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/types.ts", """
			export interface Respond {
			  <T extends string = string>(value: T): Response<T>
			  <T extends string = string>(value: T): Response<T>
			}
			export type After = string
			""");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var search = Normalize(AllProcessText(await CallAsync(server, "search_project", new Dictionary<string, object?>
		{
			["pattern"] = "Respond",
			["symbols"] = true,
			["context_lines"] = 0
		})));
		Assert.Contains("types.ts Respond 1-4", search, StringComparison.Ordinal);
		var file = Normalize(AllProcessText(await CallAsync(server, "get_file", new Dictionary<string, object?>
		{
			["path"] = "types.ts",
			["symbol"] = "Respond"
		})));
		Assert.Contains("Lines: 1-4 of ", file, StringComparison.Ordinal);
		Assert.Equal(2, file.Split("Response<T>", StringSplitOptions.None).Length - 1);
		Assert.DoesNotContain("After = string", file, StringComparison.Ordinal);
	}
}
