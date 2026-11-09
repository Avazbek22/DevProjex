namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	private const string RemovedBatchReadInstruction = "batch known selections in one get_file call";
	private const string RemovedSymbolCounters = "[Symbols] annotated=";

	[Fact]
	public async Task RealProcessSearchWithDeclarationsCarriesNoReadInstructionOrSymbolCounters()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile(
			"project/src/App.cs",
			"namespace P;\n\npublic sealed class App\n{\n\tpublic int Marker() => 1;\n}\n");
		workspace.WriteFile(
			"project/src/Other.cs",
			"namespace P;\n\npublic sealed class Other\n{\n\tpublic int Marker() => 2;\n}\n");
		workspace.WriteFile("project/README.md", "# Notes\n\nMarker in prose\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var response = await CallAsync(server, "search_project", new Dictionary<string, object?>
		{
			["pattern"] = "Marker",
			["context_lines"] = 0
		});
		var text = Normalize(AllProcessText(response));

		Assert.NotEqual(true, response.IsError);
		Assert.Contains("Declarations found (path, symbol, lines):", text, StringComparison.Ordinal);
		Assert.Contains("in P.App", text, StringComparison.Ordinal);
		Assert.Contains("[Search boundary] complete", text, StringComparison.Ordinal);
		Assert.DoesNotContain(RemovedBatchReadInstruction, text, StringComparison.Ordinal);
		Assert.DoesNotContain(RemovedSymbolCounters, text, StringComparison.Ordinal);
		Assert.DoesNotContain("[Next read]", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessSymbolSearchStillWarnsAboutFilesWithoutADeclarationIndex()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/App.cs", "namespace P;\npublic sealed class Widget { }\n");
		workspace.WriteFile("project/notes.txt", "Widget mentioned in plain text.\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var text = Normalize(AllProcessText(await CallAsync(server, "search_project", new Dictionary<string, object?>
		{
			["pattern"] = "Widget",
			["symbols"] = true,
			["context_lines"] = 0
		})));

		// The coverage gap is the one fact a caller cannot infer from the hits, so it survives.
		Assert.Contains("[Symbols coverage] declarations-not-indexed=1;", text, StringComparison.Ordinal);
		Assert.DoesNotContain(RemovedSymbolCounters, text, StringComparison.Ordinal);
		Assert.DoesNotContain(RemovedBatchReadInstruction, text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessCappedSearchStillPointsAtItsStoredContinuation()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile(
			"project/App.cs",
			"namespace P;\npublic sealed class App\n{\n" +
			string.Join('\n', Enumerable.Range(0, 12).Select(index => $"\tpublic int Probe{index:D2}() => {index};")) +
			"\n}\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var text = Normalize(AllProcessText(await CallAsync(server, "search_project", new Dictionary<string, object?>
		{
			["pattern"] = "public int Probe",
			["context_lines"] = 0,
			["max_results"] = 1
		})));

		Assert.Contains(
			"[Next read] Call read_pack with the reported pack_id for the remaining retained matches.",
			text,
			StringComparison.Ordinal);
		Assert.DoesNotContain(RemovedBatchReadInstruction, text, StringComparison.Ordinal);
		Assert.DoesNotContain(RemovedSymbolCounters, text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessGetFileReadsCarryNoReadInstructionOrSymbolCounters()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile(
			"project/src/App.cs",
			"namespace P;\n\npublic sealed class App\n{\n\tpublic int Run() => 1;\n}\n");
		workspace.WriteFile(
			"project/src/Other.cs",
			"namespace P;\n\npublic sealed class Other\n{\n\tpublic int Run() => 2;\n}\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var single = await CallAsync(server, "get_file", new Dictionary<string, object?>
		{
			["path"] = "src/App.cs",
			["symbol"] = "P.App"
		});
		var batch = await CallAsync(server, "get_file", new Dictionary<string, object?>
		{
			["requests"] = new object[]
			{
				new { path = "src/App.cs", symbol = "P.App" },
				new { path = "src/Other.cs" }
			}
		});

		foreach (var response in new[] { single, batch })
		{
			var text = Normalize(AllProcessText(response));
			Assert.NotEqual(true, response.IsError);
			Assert.Contains("public int Run() => 1;", text, StringComparison.Ordinal);
			Assert.DoesNotContain(RemovedBatchReadInstruction, text, StringComparison.Ordinal);
			Assert.DoesNotContain(RemovedSymbolCounters, text, StringComparison.Ordinal);
		}
		Assert.Contains("public int Run() => 2;", Normalize(AllProcessText(batch)), StringComparison.Ordinal);
	}
}
