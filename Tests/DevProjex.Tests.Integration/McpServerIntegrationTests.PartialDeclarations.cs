namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	private const string BrokenSignatureInterface = """
		export interface HandlerInterface {
		  <P extends string = string>(handler: H<P>): HonoBase<P>
		  <P extends string = string>(handler: H<P>): HonoBase<P>
		  <P extends string = string>(handler: H<P>): HonoBase<P>
		}
		""";

	[Theory]
	[InlineData("namespace Target {}")]
	[InlineData("namespace Target;")]
	public async Task SymbolsCoverageTreatsAnIndexedNamespaceAsADeclaration(string source)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "Namespace.cs"), source);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var response = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = "Target",
			["symbols"] = true
		});

		Assert.Contains("Target", ExtractSpotlightBody(AllText(response)), StringComparison.Ordinal);
		Assert.DoesNotContain("[Declarations not indexed]", AllText(response), StringComparison.Ordinal);
		Assert.DoesNotContain("[Symbols coverage] declarations-not-indexed=", AllText(response), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("Target", true)]
	[InlineData("AbsentName", true)]
	[InlineData("Target", false)]
	public async Task SymbolsSearchReportsUnindexedFilesWithoutLeakingTheirPaths(string pattern, bool symbols)
	{
		const string marker = "Zq9UnindexedPath";
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, marker + ".ts"), BrokenSignatureInterface);
		File.WriteAllText(Path.Combine(project, "Target.cs"), "class Target {}\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var response = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = pattern,
			["symbols"] = symbols,
			["context_lines"] = 0
		});
		var text = AllText(response);

		Assert.NotEqual(true, response.IsError);
		if (symbols)
		{
			Assert.Contains("[Declarations not indexed] " + marker + ".ts", ExtractSpotlightBody(text), StringComparison.Ordinal);
			AssertOccurrencesAreSpotlighted(text, marker, nameof(SymbolsSearchReportsUnindexedFilesWithoutLeakingTheirPaths));
			AssertTrustedTrailerOutsideSpotlight(response,
				"[Symbols coverage] declarations-not-indexed=1; search without symbols=true still reads these files.");
			if (pattern == "Target")
				Assert.True(text.IndexOf("class Target", StringComparison.Ordinal) < text.IndexOf("[Declarations not indexed]", StringComparison.Ordinal));
		}
		else
		{
			Assert.DoesNotContain("[Declarations not indexed]", text, StringComparison.Ordinal);
			Assert.DoesNotContain("[Symbols coverage] declarations-not-indexed=", text, StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task SymbolsCoverageCountsEveryUnindexedFileButListsAtMostEightPaths()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		for (var index = 0; index < 10; index++)
			File.WriteAllText(Path.Combine(project, $"broken-{index:D2}.ts"), BrokenSignatureInterface);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var response = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = "AbsentName",
			["symbols"] = true
		});
		var body = ExtractSpotlightBody(AllText(response));

		Assert.Equal(8, body.Split('\n').Count(line => line.StartsWith("[Declarations not indexed] ", StringComparison.Ordinal)));
		AssertTrustedTrailerOutsideSpotlight(response,
			"[Symbols coverage] declarations-not-indexed=10; search without symbols=true still reads these files.");
	}

	[Fact]
	public async Task SymbolsCoverageSharesTheBodyBudgetWithMatchesAndDeclarationHeaders()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "Target.cs"), string.Join('\n', Enumerable.Repeat(
			"class Target { string Padding = \"" + new string('x', 180) + "\"; }", 120)));
		File.WriteAllText(Path.Combine(project, "broken.ts"), BrokenSignatureInterface);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var response = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = "Target",
			["symbols"] = true,
			["max_results"] = 200,
			["context_lines"] = 0
		});
		var body = ExtractSpotlightBody(AllText(response));

		Assert.Contains("[Declarations not indexed] broken.ts", body, StringComparison.Ordinal);
		Assert.True(body.Length <= 16_000, $"Search body exceeded its character limit: {body.Length}.");
		Assert.Contains("matches retained=120/120", AllText(response), StringComparison.Ordinal);
		AssertTrustedTrailerOutsideSpotlight(response,
			"[Symbols coverage] declarations-not-indexed=1; search without symbols=true still reads these files.");
	}
}
