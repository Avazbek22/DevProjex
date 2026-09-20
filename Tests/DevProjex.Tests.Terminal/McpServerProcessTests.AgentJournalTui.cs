using System.Diagnostics;
using DevProjex.Infrastructure.AgentJournal;
using DevProjex.Infrastructure.LiveContext;
using DevProjex.Infrastructure.ProjectProfiles;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact(Timeout = 180_000)]
	public async Task LiveCallsProfileChangesAndTerminalJournalShareOneReceipt()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/global.json", "{}");
		workspace.WriteFile("project/src/App.cs", "class App { }");
		workspace.WriteFile("project/docs/Notes.md", "# Notes");
		var exportPath = Path.Combine(workspace.Path, "tui-receipt.md");
		var cliExportPath = Path.Combine(workspace.Path, "cli-receipt.md");
		string? dataRoot = null;
		await using var terminal = await TerminalPtyHarness.StartAsync(
			project,
			["tui", project, "--profile", "standard", "--screen", "inline", "--no-mouse", "--language", "en"],
			columns: 180,
			rows: 44,
			cancellationToken: TestContext.Current.CancellationToken,
			initializeDataRoot: root => dataRoot = root);
		await terminal.WaitForStableScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync(":set activity on\r", TestContext.Current.CancellationToken);

		Assert.NotNull(dataRoot);
		await using (var server = await ActualMcpProcess.StartAsync(
			project,
			dataRoot!,
			arguments: ["--live"],
			clientInfo: new Implementation { Name = "terminal-journal-test", Version = "1.0" }))
		{
			await CallAsync(server, "list_projects", new Dictionary<string, object?>());
			await CallAsync(
				server,
				"get_tree",
				new Dictionary<string, object?> { ["format"] = "text" });

			await terminal.SendAsync(":select all off\r", TestContext.Current.CancellationToken);
			await terminal.SendAsync(":select src on\r", TestContext.Current.CancellationToken);
			await WaitForSelectedPathAsync(dataRoot!, project, "src");

			await CallAsync(
				server,
				"get_tree",
				new Dictionary<string, object?> { ["format"] = "text" });
			await CallAsync(
				server,
				"get_file",
				new Dictionary<string, object?> { ["path"] = "src/App.cs" });

			var activity = await terminal.WaitForScreenAsync(
				"Agent activity: get_file (4 calls)",
				timeout: TimeSpan.FromSeconds(30),
				cancellationToken: TestContext.Current.CancellationToken);
			Assert.Contains("Agent activity: get_file (4 calls)", activity, StringComparison.Ordinal);
			await terminal.SendAsync(":filter App.cs\r", TestContext.Current.CancellationToken);
			await terminal.WaitForScreenAsync(
				"A App.cs",
				timeout: TimeSpan.FromSeconds(15),
				cancellationToken: TestContext.Current.CancellationToken);

			await terminal.SendAsync(":mcp log\r", TestContext.Current.CancellationToken);
			var journal = await terminal.WaitForScreenAsync(
				"UTC | Tool",
				timeout: TimeSpan.FromSeconds(30),
				cancellationToken: TestContext.Current.CancellationToken);
			Assert.Contains("4 calls", journal, StringComparison.Ordinal);
			Assert.Contains("get_file", journal, StringComparison.Ordinal);
			Assert.Contains("terminal-journal-test", journal, StringComparison.Ordinal);
			await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
			await terminal.WaitForScreenWithoutAsync(
				"UTC | Tool",
				cancellationToken: TestContext.Current.CancellationToken);

			await terminal.SendAsync(
				$":mcp log export \"{exportPath}\" markdown last\r",
				TestContext.Current.CancellationToken);
			await terminal.WaitForScreenAsync(
				"Agent journal exported",
				timeout: TimeSpan.FromSeconds(30),
				cancellationToken: TestContext.Current.CancellationToken);

			var liveSessions = new LiveSessionRegistry(() => dataRoot!);
			using var store = new AgentJournalStore(
				() => dataRoot!,
				activeSessionProvider: () => liveSessions.ReadActive());
			var session = Assert.Single(await store.ListSessionsAsync(
				project,
				cancellationToken: TestContext.Current.CancellationToken));
			var receipt = Assert.IsType<AgentJournalReceipt>(await store.ReadReceiptAsync(
				session.Id,
				TestContext.Current.CancellationToken));
			var cli = RunJournalExport(dataRoot!, project, session.Id, cliExportPath);
			Assert.Equal(CommandLineExitCodes.Success, cli.ExitCode);
			Assert.Empty(cli.StandardError);
			Assert.Equal(
				await File.ReadAllBytesAsync(cliExportPath, TestContext.Current.CancellationToken),
				await File.ReadAllBytesAsync(exportPath, TestContext.Current.CancellationToken));
			Assert.Equal(
				new AgentJournalReceiptFormatter().FormatMarkdown(receipt),
				await File.ReadAllTextAsync(exportPath, TestContext.Current.CancellationToken));

			await terminal.SendAsync(":mcp log clear\r", TestContext.Current.CancellationToken);
			await terminal.WaitForScreenAsync(
				"End live MCP sessions before clearing their journal.",
				timeout: TimeSpan.FromSeconds(15),
				cancellationToken: TestContext.Current.CancellationToken);
		}

		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				timeout: TimeSpan.FromSeconds(30),
				cancellationToken: TestContext.Current.CancellationToken));
	}

	private static TerminalTestProcessResult RunJournalExport(
		string dataRoot,
		string project,
		string sessionId,
		string outputPath)
	{
		var startInfo = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add(PublishedApplicationLocator.FindApplicationAssembly());
		startInfo.ArgumentList.Add("--language");
		startInfo.ArgumentList.Add("en");
		startInfo.ArgumentList.Add("mcp");
		startInfo.ArgumentList.Add("log");
		startInfo.ArgumentList.Add(project);
		startInfo.ArgumentList.Add("--session");
		startInfo.ArgumentList.Add(sessionId);
		startInfo.ArgumentList.Add("--format");
		startInfo.ArgumentList.Add("markdown");
		startInfo.ArgumentList.Add("--output");
		startInfo.ArgumentList.Add(outputPath);
		startInfo.Environment[InvocationEnvironment.TerminalHostVariable] = "1";
		startInfo.Environment[InvocationEnvironment.InternalDataRootVariable] = dataRoot;
		return TerminalTestProcess.Run(startInfo);
	}

	private static async Task CallAsync(
		ActualMcpProcess server,
		string tool,
		IReadOnlyDictionary<string, object?> arguments)
	{
		var result = await server.Client.CallToolAsync(
			tool,
			arguments,
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		Assert.NotEqual(true, result.IsError);
	}

	private static async Task WaitForSelectedPathAsync(
		string dataRoot,
		string project,
		string expectedPath)
	{
		var timeout = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
		while (DateTimeOffset.UtcNow < timeout)
		{
			var stored = new ProjectProfileStore(() => dataRoot).LookupProfile(
				project,
				TimeSpan.FromSeconds(2));
			if (stored.Profile?.SelectedPaths is { Count: 1 } selectedPaths &&
				string.Equals(selectedPaths.Single(), expectedPath, StringComparison.Ordinal))
			{
				return;
			}
			await Task.Delay(100, TestContext.Current.CancellationToken);
		}
		throw new TimeoutException("The live project profile was not updated by Terminal Workspace.");
	}
}
