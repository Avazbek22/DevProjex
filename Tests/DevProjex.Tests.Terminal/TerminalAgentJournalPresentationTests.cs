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

		Assert.Contains("live", row, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("client\\nname 1.0", row, StringComparison.Ordinal);
		Assert.Contains("| 3 | 120 | 30 | 2 | 5 |", row, StringComparison.Ordinal);
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

		Assert.Contains("# | UTC | Tool | Root | Revision | Duration ms | Characters | Tokens | Files | Masked | Notices | Error", text, StringComparison.Ordinal);
		Assert.Contains("1 | 2026-09-20T08:15:30.0000000Z | get_file | 0 | 7 | 12 | 80 | 20 | 1 | 3 | outside-selection | -", text, StringComparison.Ordinal);
		Assert.Contains("3 calls · 120 characters · ≈30 tokens · 2 files · 5 masked · 1 errors", text, StringComparison.Ordinal);
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

	[Fact]
	public void ActivitySnapshotKeepsTheLatestCallAndPerPathCounts()
	{
		using var workspace = new TemporaryDirectory();
		var first = CreateCall(1, "get_tree", ["src/App.cs"]);
		var latest = CreateCall(2, "get_file", ["src/App.cs", "README.md"]);
		var session = CreateSession() with { Totals = CreateSession().Totals with { Calls = 2 } };
		var receipt = new AgentJournalReceipt(
			session,
			session.Totals,
			[
				new AgentJournalDeliveredPath("src/App.cs", 2),
				new AgentJournalDeliveredPath("README.md", 1)
			],
			[first, latest]);

		var snapshot = TerminalAgentJournalSnapshot.Create(workspace.Path, receipt);

		Assert.Equal("get_file", snapshot.LatestCall?.Tool);
		Assert.Equal(2, snapshot.TotalCalls);
		Assert.Equal(
			2,
			snapshot.DeliveredPathCalls[Path.GetFullPath(Path.Combine(workspace.Path, "src", "App.cs"))]);
		Assert.Equal(
			"Agent activity: focused file delivered in 2 calls; get_file (2 calls)",
			TerminalAgentJournalPresentation.BuildActivityIndicator(
				snapshot,
				Path.GetFullPath(Path.Combine(workspace.Path, "src", "App.cs")),
				compact: false));
		Assert.Equal(
			"A F:1 get_file (2)",
			TerminalAgentJournalPresentation.BuildActivityIndicator(
				snapshot,
				Path.GetFullPath(Path.Combine(workspace.Path, "README.md")),
				compact: true));
	}

	[Fact]
	public void JournalPresentationUsesTheSharedLocalizationKeys()
	{
		using var workspace = new TemporaryDirectory();
		var keys = new HashSet<string>(StringComparer.Ordinal);
		string Localize(string key, string fallback)
		{
			keys.Add(key);
			return fallback;
		}
		var call = CreateCall(1, "get_file", ["src/App.cs"]);
		var session = CreateSession();
		var receipt = new AgentJournalReceipt(
			session,
			session.Totals,
			[new AgentJournalDeliveredPath("src/App.cs", 1)],
			[call]);
		var snapshot = TerminalAgentJournalSnapshot.Create(workspace.Path, receipt);

		_ = new TerminalAgentJournalSessionRow(session, Localize).ToString();
		_ = TerminalAgentJournalPresentation.BuildSessionHeader(Localize);
		_ = TerminalAgentJournalPresentation.BuildCallDetails(session, [call], Localize);
		_ = TerminalAgentJournalPresentation.BuildActivityIndicator(
			snapshot,
			Path.GetFullPath(Path.Combine(workspace.Path, "src", "App.cs")),
			compact: false,
			Localize);

		Assert.Contains("AgentJournal.Mode.Live", keys);
		Assert.Contains("AgentJournal.Column.Tool", keys);
		Assert.Contains("AgentJournal.Footer", keys);
		Assert.Contains("Menu.View.AgentActivity", keys);
		Assert.Contains("AgentActivity.Tree.ToolTip", keys);
		Assert.Contains("AgentActivity.Status.Calls", keys);
	}

	private static AgentJournalCall CreateCall(
		long sequence,
		string tool,
		IReadOnlyList<string> paths) => new(
		sequence,
		new DateTimeOffset(2026, 9, 20, 8, 15, 30, TimeSpan.Zero).AddSeconds(sequence),
		tool,
		0,
		new Dictionary<string, string>(),
		7,
		12,
		80,
		20,
		paths.Count,
		paths,
		0,
		0,
		0,
		[],
		null);

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
