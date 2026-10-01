using DevProjex.Application.Ranking;
using ModelContextProtocol.Protocol;
using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealMcpProcessPacksImportanceOrderAndRejectsInvalidRankingArguments()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateRankingFixture(workspace);
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var pack = await server.Client.CallToolAsync(
			"pack_context",
			new Dictionary<string, object?>
			{
				["view"] = "content",
				["format"] = "text",
				["rank"] = "importance"
			},
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var text = AllProcessText(pack);

		Assert.NotEqual(true, pack.IsError);
		Assert.True(text.IndexOf("B.cs:", StringComparison.Ordinal) < text.IndexOf("A.cs:", StringComparison.Ordinal), text);
		Assert.Contains("[Ranking] importance-v1", text, StringComparison.Ordinal);
		Assert.Contains("[Ranking coverage] facts", text, StringComparison.Ordinal);
		Assert.Contains("internal reference resolution 2/2 (100%)", text, StringComparison.Ordinal);
		Assert.Contains("unique resolved file pairs 1", text, StringComparison.Ordinal);
		Assert.Contains("[Ranking top] B.cs", text, StringComparison.Ordinal);
		var rankingIndex = text.IndexOf("[Ranking] importance-v1", StringComparison.Ordinal);
		var rankingTopIndex = text.IndexOf("[Ranking top] B.cs", StringComparison.Ordinal);
		var mainDataClose = text.IndexOf("</untrusted-data-", StringComparison.Ordinal);
		var rankingDataOpen = text.LastIndexOf("<untrusted-data-", rankingTopIndex, StringComparison.Ordinal);
		var rankingDataClose = text.IndexOf("</untrusted-data-", rankingTopIndex, StringComparison.Ordinal);
		Assert.True(
			rankingIndex > mainDataClose,
			text);
		Assert.True(
			rankingDataOpen > rankingIndex && rankingTopIndex > rankingDataOpen && rankingDataClose > rankingTopIndex,
			text);

		foreach (var invalidArguments in new[]
		         {
			         new Dictionary<string, object?> { ["rank"] = "popular" },
			         new Dictionary<string, object?> { ["rank"] = "importance", ["view"] = "tree" }
		         })
		{
			var invalid = await server.Client.CallToolAsync(
				"pack_context",
				invalidArguments,
				progress: null,
				options: null,
				TestContext.Current.CancellationToken);
			Assert.Equal(true, invalid.IsError);
			Assert.Contains("DPX-MCP-INVALID-ARGUMENTS", AllProcessText(invalid), StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task RealCliProcessExportsImportanceOrderInTextAndJson()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateRankingFixture(workspace);
		var dataRoot = workspace.CreateDirectory("data");

		var text = RunRankingCli(dataRoot, project, "text");
		Assert.Equal(0, text.ExitCode);
		Assert.True(text.StandardOutput.IndexOf("B.cs:", StringComparison.Ordinal) < text.StandardOutput.IndexOf("A.cs:", StringComparison.Ordinal), text.StandardOutput);
		Assert.Contains("[Ranking] importance-v1", text.StandardError, StringComparison.Ordinal);
		Assert.Contains("internal reference resolution 2/2 (100%)", text.StandardError, StringComparison.Ordinal);
		Assert.Contains("unique resolved file pairs 1", text.StandardError, StringComparison.Ordinal);

		var json = RunRankingCli(dataRoot, project, "json");
		Assert.Equal(0, json.ExitCode);
		using var document = JsonDocument.Parse(json.StandardOutput);
		var ranking = document.RootElement.GetProperty("ranking");
		Assert.Equal("importance-v1", ranking.GetProperty("algorithm").GetString());
		Assert.Equal("B.cs", ranking.GetProperty("top")[0].GetProperty("path").GetString());
	}

	[Fact]
	public async Task ExportWithoutRankDoesNotInvokeRankingPipeline()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/source.cs", "class Source;");
		using var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("data"))
			.Create(AppLanguage.En);
		var environment = new TestTerminalEnvironment();
		var ranking = new CountingRankingService();
		var request = new ExportContextCommandRequest(
			ProjectPath: project,
			Selection: new ProjectSelectionSpec(GitMode: GitFilteringMode.None, Exclusions: []),
			View: ProjectContextView.Content,
			Format: ProjectContextDocumentFormat.Text,
			OutputPath: "-",
			Force: false,
			DryRun: false,
			MaximumEstimatedTokens: null,
			Output: new TerminalOutputOptions(Progress: TerminalProgressMode.Never),
			Rank: null);

		var exitCode = await new ExportContextCommandHandler(services, environment, ranking)
			.ExecuteAsync(request, TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(0, ranking.CallCount);
		Assert.DoesNotContain("[Ranking]", environment.StandardError, StringComparison.Ordinal);
	}

	private static string CreateRankingFixture(TemporaryDirectory workspace)
	{
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
		workspace.WriteFile("project/A.cs", "namespace Fixture; public sealed class A { private readonly B first = new(); private readonly D second = new(); }");
		workspace.WriteFile("project/B.cs", "namespace Fixture; public sealed class B { } public sealed class D { }");
		workspace.WriteFile("project/C.cs", "namespace Fixture; public sealed class C { }");
		RunGitAsync(project, "init", "--quiet").GetAwaiter().GetResult();
		RunGitAsync(project, "config", "user.name", "DevProjex Ranking Tests").GetAwaiter().GetResult();
		RunGitAsync(project, "config", "user.email", "ranking@devprojex.local").GetAwaiter().GetResult();
		RunGitAsync(project, "add", "--all").GetAwaiter().GetResult();
		RunGitAsync(project, "commit", "--quiet", "-m", "ranking fixture").GetAwaiter().GetResult();
		return project;
	}

	private static TerminalTestProcessResult RunRankingCli(string dataRoot, string project, string format)
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
			         "--view", "content", "--format", format, "--rank", "importance",
			         "--git-mode", "none", "--exclude", "none", "-o", "-", "--progress", "never"
		         })
		{
			startInfo.ArgumentList.Add(argument);
		}
		startInfo.Environment[InvocationEnvironment.TerminalHostVariable] = "1";
		startInfo.Environment[InvocationEnvironment.InternalDataRootVariable] = dataRoot;
		return TerminalTestProcess.Run(startInfo);
	}

	private sealed class CountingRankingService : IImportanceRankingService
	{
		public int CallCount { get; private set; }

		public Task<ImportanceRankingReport> RankAsync(
			string sourceRoot,
			IReadOnlyList<string> candidateFiles,
			CancellationToken cancellationToken = default)
		{
			CallCount++;
			throw new InvalidOperationException("The ranking pipeline must stay dormant without --rank.");
		}
	}
}
