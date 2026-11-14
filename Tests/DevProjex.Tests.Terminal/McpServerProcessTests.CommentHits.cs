namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[InlineData("sample.ts", "export class Sample {\n  /**\n   * needle\n   */\n  run() {\n    return 123;\n  }\n}\n", "Sample.run", 5, 7)]
	[InlineData("sample.py", "class Sample:\n    # needle\n    def run(self):\n        return 123\n", "Sample.run", 3, 4)]
	[InlineData("sample.cs", "class Sample {\n  // needle\n  public int Run() {\n    return 123;\n  }\n}\n", "Sample.Run", 3, 5)]
	public async Task RealProcessCommentHitsNameAndReadTheMemberOnMcpAndCli(
		string path, string content, string symbol, int start, int end)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("comment-project");
		workspace.WriteFile("comment-project/" + path, content);
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var result = await CallAsync(server, "search_project", new() { ["pattern"] = "needle", ["context_lines"] = 0 });
		Assert.NotEqual(true, result.IsError);
		var text = Normalize(AllProcessText(result));
		Assert.Contains("in " + symbol, text, StringComparison.Ordinal);
		Assert.Contains($"{path} {symbol} {start}-{end}", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Best declaration body", text, StringComparison.Ordinal);
		var selected = await CallAsync(server, "get_file", new() { ["path"] = path, ["symbol"] = symbol });
		Assert.NotEqual(true, selected.IsError);
		Assert.Contains($"Lines: {start}-{end} of", BatchData(selected), StringComparison.Ordinal);
		Assert.DoesNotContain("needle", BatchData(selected), StringComparison.Ordinal);

		var cli = RunCliSearch(workspace, project);
		Assert.True(cli.ExitCode == 0, cli.StandardError + cli.StandardOutput);
		using var document = JsonDocument.Parse(cli.StandardOutput);
		var declaration = Assert.Single(document.RootElement.GetProperty("declarations").EnumerateArray());
		Assert.Equal(symbol, declaration.GetProperty("symbol").GetString());
		Assert.Equal(start, declaration.GetProperty("startLine").GetInt32());
		Assert.Equal(end, declaration.GetProperty("endLine").GetInt32());
		Assert.Equal(symbol, Assert.Single(document.RootElement.GetProperty("matches").EnumerateArray()).GetProperty("declaration").GetString());
	}

	[Fact]
	public async Task RealProcessAQualifiedMemberNameInItsCommentSelectsItsOwnBodyOnMcpAndCli()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("named-comment-project");
		workspace.WriteFile("named-comment-project/sample.ts",
			"export class Sample {\n  // Sample.run returns a value.\n  run() {\n    return 123;\n  }\n}\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		const string pattern = @"Sample\.run";
		var result = await CallAsync(server, "search_project", new() { ["pattern"] = pattern, ["context_lines"] = 0 });
		Assert.NotEqual(true, result.IsError);
		var text = Normalize(AllProcessText(result));
		Assert.Contains("in Sample.run", text, StringComparison.Ordinal);
		Assert.Contains("sample.ts Sample.run 3-5", text, StringComparison.Ordinal);
		var body = ExtractBestDeclarationBody(text);
		Assert.Contains("return 123", body, StringComparison.Ordinal);
		Assert.DoesNotContain("returns a value", body, StringComparison.Ordinal);
		var cli = RunCommentSearch(workspace, project, pattern);
		Assert.True(cli.ExitCode == 0, cli.StandardError + cli.StandardOutput);
		using var document = JsonDocument.Parse(cli.StandardOutput);
		var declaration = Assert.Single(document.RootElement.GetProperty("declarations").EnumerateArray());
		Assert.Equal("Sample.run", declaration.GetProperty("symbol").GetString());
		Assert.Equal(3, declaration.GetProperty("startLine").GetInt32());
		Assert.Contains("return 123", declaration.GetProperty("body").GetString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessAnExampleNamesItsDeclarationButTheCodeMatchKeepsTheBody()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("example-comment-project");
		workspace.WriteFile("example-comment-project/sample.ts",
			"/** needle example */\nexport function unrelated() {\n  return 123;\n}\nexport function execute() {\n  return needle();\n}\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var result = await CallAsync(server, "search_project", new() { ["pattern"] = "needle", ["context_lines"] = 0 });
		Assert.NotEqual(true, result.IsError);
		var text = Normalize(AllProcessText(result));
		Assert.Contains("in unrelated", text, StringComparison.Ordinal);
		Assert.Contains("sample.ts unrelated 2-4", text, StringComparison.Ordinal);
		Assert.Contains("sample.ts execute 5-7", text, StringComparison.Ordinal);
		var body = ExtractBestDeclarationBody(text);
		Assert.Contains("return needle()", body, StringComparison.Ordinal);
		Assert.DoesNotContain("return 123", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessHeadsAMergedGroupWithItsLeadingDeclarationComment()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("adjacent-comment-project");
		workspace.WriteFile("adjacent-comment-project/Doc.cs",
			"namespace P;\n\n/// <summary>marker above the type</summary>\npublic sealed class Doc\n{\n\tpublic int Marker() => 1;\n}\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var result = await CallAsync(server, "search_project", new() { ["pattern"] = "marker|Marker", ["context_lines"] = 3 });
		Assert.NotEqual(true, result.IsError);
		var text = Normalize(AllProcessText(result));
		var header = text.IndexOf("in P.Doc\n", StringComparison.Ordinal);
		var comment = text.IndexOf("3:/// <summary>marker above the type", StringComparison.Ordinal);
		Assert.True(header >= 0 && comment > header, text);
		Assert.Contains("Doc.cs P.Doc 4-7", text, StringComparison.Ordinal);
		Assert.Contains("Doc.cs P.Doc.Marker 6-6", text, StringComparison.Ordinal);
		var body = ExtractBestDeclarationBody(text);
		Assert.Contains("public int Marker()", body, StringComparison.Ordinal);
		Assert.DoesNotContain("summary", body, StringComparison.Ordinal);
	}

	private static TerminalTestProcessResult RunCommentSearch(TemporaryDirectory workspace, string project, string pattern)
	{
		var start = new System.Diagnostics.ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		foreach (var argument in new[] { PublishedApplicationLocator.FindApplicationAssembly(), "search", pattern, project,
			"--regex", "--format", "json", "--git-mode", "none", "--exclude", "none", "--language", "en", "--plain", "--progress", "never" })
			start.ArgumentList.Add(argument);
		start.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = workspace.CreateDirectory("named-cli-data");
		return TerminalTestProcess.Run(start, TimeSpan.FromMinutes(1));
	}
}
