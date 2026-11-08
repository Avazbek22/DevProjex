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
			AssertCliDeclarationRange(workspace, project, path, name, start, end);
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
		AssertCliDeclarationRange(workspace, project, "types.ts", "Respond", 1, 4);
		var file = Normalize(AllProcessText(await CallAsync(server, "get_file", new Dictionary<string, object?>
		{
			["path"] = "types.ts",
			["symbol"] = "Respond"
		})));
		Assert.Contains("Lines: 1-4 of ", file, StringComparison.Ordinal);
		Assert.Equal(2, file.Split("Response<T>", StringSplitOptions.None).Length - 1);
		Assert.DoesNotContain("After = string", file, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PythonModuleConstantsRoundTripFromSymbolSearchToTheirExactRanges()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/config.py", """
			DEFAULT_TIMEOUT_CONFIG: int = (
			    5
			)
			MAX_URL_LENGTH, OTHER = (4096, 2)
			REMOVED = 1
			del REMOVED
			def configure():
			    LOCAL = 2
			""");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		foreach (var (name, start, end) in new[] { ("DEFAULT_TIMEOUT_CONFIG", 1, 3), ("MAX_URL_LENGTH", 4, 4) })
		{
			var search = Normalize(AllProcessText(await CallAsync(server, "search_project", new Dictionary<string, object?>
			{
				["pattern"] = name,
				["symbols"] = true,
				["context_lines"] = 0
			})));
			Assert.Contains($"config.py {name} {start}-{end}", search, StringComparison.Ordinal);
			AssertCliDeclarationRange(workspace, project, "config.py", name, start, end);
			var file = Normalize(AllProcessText(await CallAsync(server, "get_file", new Dictionary<string, object?>
			{
				["path"] = "config.py",
				["symbol"] = name
			})));
			Assert.Contains($"Lines: {start}-{end} of ", file, StringComparison.Ordinal);
			Assert.Contains(name, file, StringComparison.Ordinal);
			Assert.DoesNotContain("REMOVED", file, StringComparison.Ordinal);
		}
		var removed = AllProcessText(await CallAsync(server, "search_project", new Dictionary<string, object?>
		{
			["pattern"] = "REMOVED",
			["symbols"] = true
		}));
		Assert.Contains("[No matches]", removed, StringComparison.Ordinal);
	}

	private static void AssertCliDeclarationRange(TemporaryDirectory workspace, string project,
		string path, string symbol, int start, int end)
	{
		var result = RunCliSymbolSearch(workspace, project, symbol);
		Assert.True(result.ExitCode == 0, result.StandardError + result.StandardOutput);
		using var json = JsonDocument.Parse(result.StandardOutput);
		var declaration = Assert.Single(json.RootElement.GetProperty("declarations").EnumerateArray());
		Assert.Equal(path, declaration.GetProperty("path").GetString());
		Assert.Equal(symbol, declaration.GetProperty("symbol").GetString());
		Assert.Equal((start, end), (declaration.GetProperty("startLine").GetInt32(), declaration.GetProperty("endLine").GetInt32()));
	}
}
