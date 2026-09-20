using DevProjex.Infrastructure.AgentJournal;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalAgentJournalPtyTests
{
	[Fact(Timeout = 150_000)]
	public async Task JournalOverlayExportAndClearUseTheSharedReceipt()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/global.json", "{}");
		workspace.WriteFile("project/src/App.cs", "class App { }");
		var destination = Path.Combine(workspace.Path, "receipt.md");
		string? expectedReceipt = null;

		await using var terminal = await TerminalPtyHarness.StartAsync(
			project,
			["tui", project, "--profile", "standard", "--screen", "inline", "--no-mouse", "--language", "en"],
			columns: 160,
			rows: 42,
			cancellationToken: TestContext.Current.CancellationToken,
			initializeDataRoot: dataRoot => expectedReceipt = WriteJournalFixture(dataRoot, project));
		await terminal.WaitForStableScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(":mcp log\r", TestContext.Current.CancellationToken);
		var overlay = await terminal.WaitForScreenAsync(
			"get_file",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("Totals", overlay, StringComparison.Ordinal);
		Assert.Contains("terminal-test", overlay, StringComparison.Ordinal);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);

		await terminal.SendAsync(
			$":mcp log export \"{destination}\" markdown last\r",
			TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Agent journal exported",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Equal(expectedReceipt, await File.ReadAllTextAsync(
			destination,
			TestContext.Current.CancellationToken));

		await terminal.SendAsync(":mcp log clear\r", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Clear journal sessions for this project?",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Agent journal cleared (1 sessions)",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				timeout: TimeSpan.FromSeconds(30),
				cancellationToken: TestContext.Current.CancellationToken));
	}

	private static string WriteJournalFixture(string dataRoot, string projectRoot)
	{
		var started = new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);
		const int pid = 4242;
		var session = new AgentJournalSession(
			AgentJournalStore.CreateSessionId(started, pid),
			started,
			null,
			pid,
			started.AddSeconds(-1),
			"terminal-test",
			"1.0",
			AgentJournalMode.Live,
			[new AgentJournalRoot(projectRoot, "project")],
			AgentJournalToolSet.Full,
			"5.2",
			false,
			AgentJournalTotals.Empty,
			false);
		var call = new AgentJournalCall(
			1,
			started.AddSeconds(2),
			"get_file",
			0,
			new Dictionary<string, string> { ["path"] = "src/App.cs" },
			1,
			12,
			80,
			20,
			1,
			["src/App.cs"],
			0,
			1,
			0,
			[],
			null);
		var totals = new AgentJournalTotals(1, 80, 20, 1, 1, 0, 0);
		using var store = new AgentJournalStore(
			() => dataRoot,
			activeSessionProvider: static () => []);
		store.StartSession(session).AsTask().GetAwaiter().GetResult();
		store.RecordCall(session.Id, call).AsTask().GetAwaiter().GetResult();
		store.EndSession(session.Id, started.AddSeconds(3), totals).AsTask().GetAwaiter().GetResult();
		var receipt = store.ReadReceiptAsync(session.Id).AsTask().GetAwaiter().GetResult();
		return new AgentJournalReceiptFormatter().FormatMarkdown(Assert.IsType<AgentJournalReceipt>(receipt));
	}
}
