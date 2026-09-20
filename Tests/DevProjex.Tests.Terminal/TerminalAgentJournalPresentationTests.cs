namespace DevProjex.Tests.Terminal;

public sealed class TerminalAgentJournalPresentationTests
{
	[Fact]
	public void SessionRowsExposeModeClientAndBoundedTotalsWithoutControlCharacters()
	{
		var session = CreateSession() with
		{
			ClientName = "client\nname",
			Totals = new AgentJournalTotals(3, 120, 30, 2, 1, 4, 1)
		};

		var row = new TerminalAgentJournalSessionRow(session).ToString();

		Assert.Contains("live", row, StringComparison.Ordinal);
		Assert.Contains("client\\nname", row, StringComparison.Ordinal);
		Assert.Contains("3 calls", row, StringComparison.Ordinal);
		Assert.Contains("2 files", row, StringComparison.Ordinal);
		Assert.DoesNotContain('\n', row);
	}

	[Fact]
	public void CallDetailsUseStableColumnsAndIncludeTotals()
	{
		var calls = new[]
		{
			new AgentJournalCall(
				1,
				new DateTimeOffset(2026, 9, 20, 8, 15, 30, TimeSpan.Zero),
				"get_file",
				0,
				new Dictionary<string, string>(),
				7,
				12,
				80,
				20,
				1,
				["src/App.cs"],
				0,
				2,
				1,
				[AgentJournalNoticeCodes.OutsideSelection],
				null)
		};

		var text = TerminalAgentJournalPresentation.BuildCallDetails(CreateSession(), calls);

		Assert.Contains("UTC | Tool | Duration | Characters | Tokens | Files | Secrets | Private data | Result", text, StringComparison.Ordinal);
		Assert.Contains("08:15:30 | get_file | 12 ms | 80 | 20 | 1 | 2 | 1 | ok", text, StringComparison.Ordinal);
		Assert.Contains("Totals | 3 calls | 120 characters | 30 tokens | 2 files | 1 secrets | 4 private data | 1 errors", text, StringComparison.Ordinal);
	}

	[Fact]
	public void DeliveredPathsMatchCanonicalProjectPathsOnly()
	{
		using var workspace = new TemporaryDirectory();
		var receipt = new AgentJournalReceipt(
			CreateSession(),
			AgentJournalTotals.Empty,
			[
				new AgentJournalDeliveredPath("src/App.cs", 2),
				new AgentJournalDeliveredPath("README.md", 1)
			],
			[]);

		var paths = TerminalAgentJournalPresentation.BuildDeliveredPathSet(
			workspace.Path,
			receipt);

		Assert.Contains(Path.GetFullPath(Path.Combine(workspace.Path, "src", "App.cs")), paths);
		Assert.Contains(Path.GetFullPath(Path.Combine(workspace.Path, "README.md")), paths);
	}

	private static AgentJournalSession CreateSession() => new(
		"session-42",
		new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero),
		null,
		42,
		new DateTimeOffset(2026, 9, 20, 7, 59, 0, TimeSpan.Zero),
		"client",
		"1.0",
		AgentJournalMode.Live,
		[new AgentJournalRoot(@"C:\work\project", "project")],
		AgentJournalToolSet.Full,
		"5.2",
		false,
		new AgentJournalTotals(3, 120, 30, 2, 1, 4, 1),
		true);
}
