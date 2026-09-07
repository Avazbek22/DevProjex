using ModelContextProtocol.Protocol;
using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public void RealCliProcessExportsFocusOrderAndValidatesTheRankPair()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateRankingFixture(workspace);
		var dataRoot = workspace.CreateDirectory("cli-focus-data");
		var focused = RunFocusCli(
			dataRoot,
			project,
			"text",
			includeRank: true,
			"A.cs",
			Path.Combine(project, "A.cs"));

		Assert.Equal(0, focused.ExitCode);
		Assert.True(
			focused.StandardOutput.IndexOf("A.cs:", StringComparison.Ordinal) <
			focused.StandardOutput.IndexOf("B.cs:", StringComparison.Ordinal),
			focused.StandardOutput);
		Assert.Contains("[Ranking] focus-v1 · 1 seed", focused.StandardError, StringComparison.Ordinal);
		Assert.Contains("[Ranking top] A.cs — seed", focused.StandardError, StringComparison.Ordinal);
		var json = RunFocusCli(dataRoot, project, "json", includeRank: true, "A.cs");
		Assert.Equal(0, json.ExitCode);
		using (var document = JsonDocument.Parse(json.StandardOutput))
		{
			var ranking = document.RootElement.GetProperty("ranking");
			Assert.Equal("focus-v1", ranking.GetProperty("algorithm").GetString());
			Assert.Equal("focus-v1", ranking.GetProperty("focus").GetProperty("algorithm").GetString());
			Assert.Equal(0, ranking.GetProperty("top")[0].GetProperty("hop").GetInt32());
			Assert.True(ranking.GetProperty("top")[0].GetProperty("baseImportancePriority").GetInt32() > 0);
		}

		var withoutRank = RunFocusCli(dataRoot, project, "text", includeRank: false, "A.cs");
		Assert.Equal(CommandLineExitCodes.UsageError, withoutRank.ExitCode);
		Assert.Contains("--focus", withoutRank.StandardError, StringComparison.Ordinal);
		Assert.Contains("--rank", withoutRank.StandardError, StringComparison.Ordinal);

		var tooMany = RunFocusCli(
			dataRoot,
			project,
			"text",
			includeRank: true,
			Enumerable.Repeat("A.cs", 17).ToArray());
		Assert.Equal(CommandLineExitCodes.UsageError, tooMany.ExitCode);
	}

	[Fact]
	public async Task RealMcpProcessPublishesAndEnforcesFocusRankingContract()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateRankingFixture(workspace);
		workspace.WriteFile("project/D[one].cs", "namespace Fixture; public sealed class DOne { }");
		workspace.CreateDirectory("project/folder");
		workspace.WriteFile("project/.excluded.cs", "namespace Fixture; public sealed class Excluded { }");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("focus-data"));

		var tools = await server.Client.ListToolsAsync(
			options: null,
			TestContext.Current.CancellationToken);
		var packTool = tools.Single(static tool => tool.Name == "pack_context");
		var focusSchema = packTool.ProtocolTool.InputSchema
			.GetProperty("properties")
			.GetProperty("focus");
		Assert.Equal(2, focusSchema.GetProperty("oneOf").GetArrayLength());
		Assert.Equal(16, focusSchema.GetProperty("oneOf")[1].GetProperty("maxItems").GetInt32());

		var focused = await server.Client.CallToolAsync(
			"pack_context",
			new Dictionary<string, object?>
			{
				["view"] = "content",
				["format"] = "text",
				["rank"] = "importance",
				["focus"] = new[] { "A.cs", Path.Combine(project, "A.cs") }
			},
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var text = AllProcessText(focused);
		Assert.NotEqual(true, focused.IsError);
		Assert.Contains("[Ranking] focus-v1 · 1 seed", text, StringComparison.Ordinal);
		Assert.True(text.IndexOf("A.cs:", StringComparison.Ordinal) < text.IndexOf("B.cs:", StringComparison.Ordinal), text);
		Assert.Contains("[Ranking top] A.cs — seed", text, StringComparison.Ordinal);
		Assert.Contains("dependency of A.cs", text, StringComparison.Ordinal);
		var rankingIndex = text.IndexOf("[Ranking] focus-v1", StringComparison.Ordinal);
		var mainDataClose = text.IndexOf("</untrusted-data-", StringComparison.Ordinal);
		var rankingTopIndex = text.IndexOf("[Ranking top] A.cs", StringComparison.Ordinal);
		var rankingDataOpen = text.LastIndexOf("<untrusted-data-", rankingTopIndex, StringComparison.Ordinal);
		var rankingDataClose = text.IndexOf("</untrusted-data-", rankingTopIndex, StringComparison.Ordinal);
		Assert.True(rankingIndex > mainDataClose, text);
		Assert.True(rankingDataOpen > rankingIndex && rankingDataClose > rankingTopIndex, text);

		foreach (var validFocus in new object[]
		         {
		         	"D\\[one\\].cs",
		         	Path.Combine(project, "B.cs")
		         })
		{
			var result = await server.Client.CallToolAsync(
				"pack_context",
				new Dictionary<string, object?>
				{
					["view"] = "content",
					["format"] = "text",
					["rank"] = "importance",
					["focus"] = validFocus
				},
				progress: null,
				options: null,
				TestContext.Current.CancellationToken);
			Assert.NotEqual(true, result.IsError);
			Assert.Contains("[Ranking] focus-v1", AllProcessText(result), StringComparison.Ordinal);
		}

		var invalidCases = new Dictionary<string, object?>[]
		{
			new() { ["focus"] = "A.cs" },
			new() { ["rank"] = "importance", ["focus"] = Array.Empty<string>() },
			new() { ["rank"] = "importance", ["focus"] = "" },
			new() { ["rank"] = "importance", ["focus"] = null },
			new() { ["rank"] = "importance", ["focus"] = 42 },
			new() { ["rank"] = "importance", ["focus"] = new object[] { "A.cs", 42 } },
			new() { ["rank"] = "importance", ["focus"] = Enumerable.Repeat("A.cs", 17).ToArray() },
			new() { ["rank"] = "importance", ["focus"] = "folder" },
			new() { ["rank"] = "importance", ["focus"] = "a.cs" },
			new() { ["rank"] = "importance", ["focus"] = Path.Combine(workspace.Path, "outside.cs") },
			new()
			{
				["rank"] = "importance",
				["focus"] = "A.cs",
				["include_patterns"] = new[] { "B.cs" }
			}
		};
		foreach (var invalidArguments in invalidCases)
		{
			var invalid = await server.Client.CallToolAsync(
				"pack_context",
				invalidArguments,
				progress: null,
				options: null,
				TestContext.Current.CancellationToken);
			Assert.Equal(true, invalid.IsError);
			var error = AllProcessText(invalid);
			Assert.Contains("DPX-MCP-", error, StringComparison.Ordinal);
			if (invalidArguments.ContainsKey("focus") && !invalidArguments.ContainsKey("rank"))
				Assert.Contains("focus", error, StringComparison.Ordinal);
		}
	}

	private static TerminalTestProcessResult RunFocusCli(
		string dataRoot,
		string project,
		string format,
		bool includeRank,
		params string[] focus)
	{
		var startInfo = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add(PublishedApplicationLocator.FindApplicationAssembly());
		foreach (var argument in new[]
		         {
		         	"--language", "en", "export", "context", project,
			         "--view", "content", "--format", format,
		         	"--git-mode", "none", "--exclude", "none", "-o", "-", "--progress", "never"
		         })
		{
			startInfo.ArgumentList.Add(argument);
		}
		if (includeRank)
		{
			startInfo.ArgumentList.Add("--rank");
			startInfo.ArgumentList.Add("importance");
		}
		foreach (var path in focus)
		{
			startInfo.ArgumentList.Add("--focus");
			startInfo.ArgumentList.Add(path);
		}
		startInfo.Environment[InvocationEnvironment.TerminalHostVariable] = "1";
		startInfo.Environment[InvocationEnvironment.InternalDataRootVariable] = dataRoot;
		return TerminalTestProcess.Run(startInfo);
	}
}
