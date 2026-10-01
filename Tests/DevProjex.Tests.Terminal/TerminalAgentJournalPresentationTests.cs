using DevProjex.Infrastructure.AgentJournal;
using DevProjex.Infrastructure.LiveContext;
using DevProjex.Infrastructure.ResourceStore;

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
		var session = CreateSession() with
		{
			Roots = [new AgentJournalRoot(workspace.Path, "project")]
		};
		var receipt = new AgentJournalReceipt(
			session,
			AgentJournalTotals.Empty,
			[
				new AgentJournalDeliveredPath("src/App.cs", 2),
				new AgentJournalDeliveredPath("README.md", 1)
			],
			[CreateCall(1, "get_file", ["src/App.cs", "README.md"])]);

		var paths = TerminalAgentJournalPresentation.BuildDeliveredPathSet(
			workspace.Path,
			receipt);

		Assert.Contains(Path.GetFullPath(Path.Combine(workspace.Path, "src", "App.cs")), paths);
		Assert.Contains(Path.GetFullPath(Path.Combine(workspace.Path, "README.md")), paths);
	}

	[Fact]
	public void ActivitySnapshotKeepsPerPathCountsAndNamesOnlyTheFocusedDelivery()
	{
		using var workspace = new TemporaryDirectory();
		var first = CreateCall(1, "get_tree", ["src/App.cs"]);
		var latest = CreateCall(2, "get_file", ["src/App.cs", "README.md"]);
		var session = CreateSession() with
		{
			Totals = CreateSession().Totals with { Calls = 2 },
			Roots = [new AgentJournalRoot(workspace.Path, "project")]
		};
		var receipt = new AgentJournalReceipt(
			session,
			session.Totals,
			[
				new AgentJournalDeliveredPath("src/App.cs", 2),
				new AgentJournalDeliveredPath("README.md", 1)
			],
			[first, latest]);

		var snapshot = TerminalAgentJournalSnapshot.Create(workspace.Path, receipt);

		Assert.Equal(
			2,
			snapshot.DeliveredPathCalls[Path.GetFullPath(Path.Combine(workspace.Path, "src", "App.cs"))]);
		Assert.Equal(
			"Agent received 2 times",
			TerminalAgentJournalPresentation.BuildFocusedDeliveryHint(
				snapshot,
				Path.GetFullPath(Path.Combine(workspace.Path, "src", "App.cs"))));
		Assert.Equal(
			"Agent received 1 time",
			TerminalAgentJournalPresentation.BuildFocusedDeliveryHint(
				snapshot,
				Path.GetFullPath(Path.Combine(workspace.Path, "README.md"))));
		Assert.Null(TerminalAgentJournalPresentation.BuildFocusedDeliveryHint(
			snapshot,
			Path.GetFullPath(Path.Combine(workspace.Path, "docs", "Notes.md"))));
		Assert.Null(TerminalAgentJournalPresentation.BuildFocusedDeliveryHint(snapshot, focusedTreePath: null));
	}

	[Fact]
	public void DeliveredPathsAreFilteredByTheMatchingRootIndex()
	{
		using var workspace = new TemporaryDirectory();
		var firstRoot = workspace.CreateDirectory("first");
		var secondRoot = workspace.CreateDirectory("second");
		var session = CreateSession() with
		{
			Roots =
			[
				new AgentJournalRoot(firstRoot, "first"),
				new AgentJournalRoot(secondRoot, "second")
			]
		};
		var calls = new[]
		{
			CreateCall(1, "get_file", ["src/App.cs"]) with { RootIndex = 0 },
			CreateCall(2, "get_file", ["src/App.cs"]) with { RootIndex = 1 }
		};
		var receipt = new AgentJournalReceipt(session, session.Totals, [], calls);

		var first = TerminalAgentJournalPresentation.BuildDeliveredPathSet(firstRoot, receipt);
		var second = TerminalAgentJournalPresentation.BuildDeliveredPathSet(secondRoot, receipt);

		Assert.Equal([Path.Combine(firstRoot, "src", "App.cs")], first);
		Assert.Equal([Path.Combine(secondRoot, "src", "App.cs")], second);
	}

	[Fact]
	public void ActivityTraceStartsAtTheCurrentProjectOpeningBoundary()
	{
		using var workspace = new TemporaryDirectory();
		var session = CreateSession() with
		{
			Roots = [new AgentJournalRoot(workspace.Path, "project")]
		};
		var beforeOpen = new AgentJournalActivitySnapshot(
			session,
			CreateCall(2, "get_file", ["src/Old.cs"]),
			[CreateCall(1, "get_tree", ["src/Old.cs"]), CreateCall(2, "get_file", ["src/Old.cs"])],
			RequiresReset: false,
			LastEventUtc: null);

		var opened = TerminalAgentJournalSnapshot.Create(
			workspace.Path,
			beforeOpen,
			previous: null,
			baselineSequence: 2);
		var afterOpen = new AgentJournalActivitySnapshot(
			session with { Totals = session.Totals with { Calls = 4 } },
			CreateCall(4, "get_file", ["src/New.cs"]),
			[CreateCall(3, "get_file", ["src/New.cs"]), CreateCall(4, "get_file", ["src/New.cs"])],
			RequiresReset: false,
			LastEventUtc: null);
		var updated = TerminalAgentJournalSnapshot.Create(
			workspace.Path,
			afterOpen,
			opened,
			baselineSequence: 2);

		Assert.Empty(opened.DeliveredPathCalls);
		Assert.DoesNotContain(
			Path.Combine(workspace.Path, "src", "Old.cs"),
			updated.DeliveredPathCalls.Keys);
		Assert.Equal(2, updated.DeliveredPathCalls[Path.Combine(workspace.Path, "src", "New.cs")]);
	}

	[Fact]
	public void ActivitySnapshotCountsDeliveriesForTheMatchingRootOnly()
	{
		using var workspace = new TemporaryDirectory();
		var firstRoot = workspace.CreateDirectory("first");
		var secondRoot = workspace.CreateDirectory("second");
		var session = CreateSession() with
		{
			Roots =
			[
				new AgentJournalRoot(firstRoot, "first"),
				new AgentJournalRoot(secondRoot, "second")
			]
		};
		var firstRootCall = CreateCall(1, "get_file", ["src/App.cs"]) with { RootIndex = 0 };
		var secondRootCall = CreateCall(2, "get_file", ["src/Other.cs"]) with { RootIndex = 1 };
		var receipt = new AgentJournalReceipt(
			session,
			session.Totals,
			[],
			[firstRootCall, secondRootCall]);

		var first = TerminalAgentJournalSnapshot.Create(firstRoot, receipt);
		var second = TerminalAgentJournalSnapshot.Create(secondRoot, receipt);

		Assert.Equal([Path.Combine(firstRoot, "src", "App.cs")], first.DeliveredPathCalls.Keys);
		Assert.Equal([Path.Combine(secondRoot, "src", "Other.cs")], second.DeliveredPathCalls.Keys);
	}

	[Fact]
	public void AppendedActivityCountsDeliveriesForTheMatchingRootOnly()
	{
		using var workspace = new TemporaryDirectory();
		var firstRoot = workspace.CreateDirectory("first");
		var secondRoot = workspace.CreateDirectory("second");
		var session = CreateSession() with
		{
			Roots =
			[
				new AgentJournalRoot(firstRoot, "first"),
				new AgentJournalRoot(secondRoot, "second")
			]
		};
		var firstRootCall = CreateCall(1, "get_file", ["src/App.cs"]) with { RootIndex = 0 };
		var secondRootCall = CreateCall(2, "get_file", ["src/Other.cs"]) with { RootIndex = 1 };
		var activity = new AgentJournalActivitySnapshot(
			session,
			secondRootCall,
			[firstRootCall, secondRootCall],
			RequiresReset: false,
			LastEventUtc: secondRootCall.Utc);

		var first = TerminalAgentJournalSnapshot.Create(firstRoot, activity, previous: null, baselineSequence: 0);
		var second = TerminalAgentJournalSnapshot.Create(secondRoot, activity, previous: null, baselineSequence: 0);

		Assert.Equal([Path.Combine(firstRoot, "src", "App.cs")], first.DeliveredPathCalls.Keys);
		Assert.Equal([Path.Combine(secondRoot, "src", "Other.cs")], second.DeliveredPathCalls.Keys);
		Assert.Equal(2, TerminalAgentJournalSnapshot.ResolveReadCursor(activity));
	}

	[Fact]
	public void SessionOpeningBaselineDistinguishesAnExistingSessionFromANewSession()
	{
		var calls = new[] { CreateCall(6, "get_tree", []), CreateCall(7, "get_file", []) };

		Assert.Equal(7, TerminalAgentJournalSnapshot.ResolveOpeningBaseline(
			calls,
			workspaceOpenedUtc: calls[^1].Utc.AddSeconds(1)));
		Assert.Equal(0, TerminalAgentJournalSnapshot.ResolveOpeningBaseline(
			calls,
			workspaceOpenedUtc: calls[0].Utc.AddSeconds(-1)));
	}

	[Fact]
	public void CallsMadeAfterTheProjectOpenedStayInTheTraceWhenActivityIsEnabledLater()
	{
		using var workspace = new TemporaryDirectory();
		var session = CreateSession() with
		{
			Totals = CreateSession().Totals with { Calls = 3 },
			Roots = [new AgentJournalRoot(workspace.Path, "project")]
		};
		var beforeOpen = CreateCall(1, "get_tree", ["src/Old.cs"]);
		var afterOpen = new[]
		{
			CreateCall(2, "get_file", ["README.md"]),
			CreateCall(3, "get_file", ["src/New.cs"])
		};
		var openedUtc = beforeOpen.Utc.AddMilliseconds(500);
		var activity = new AgentJournalActivitySnapshot(
			session,
			afterOpen[^1],
			[beforeOpen, .. afterOpen],
			RequiresReset: false,
			LastEventUtc: afterOpen[^1].Utc);

		var snapshot = TerminalAgentJournalSnapshot.Create(
			workspace.Path,
			activity,
			previous: null,
			TerminalAgentJournalSnapshot.ResolveOpeningBaseline(activity.AppendedCalls, openedUtc));

		Assert.Contains(Path.Combine(workspace.Path, "README.md"), snapshot.DeliveredPathCalls.Keys);
		Assert.Contains(Path.Combine(workspace.Path, "src", "New.cs"), snapshot.DeliveredPathCalls.Keys);
		Assert.DoesNotContain(Path.Combine(workspace.Path, "src", "Old.cs"), snapshot.DeliveredPathCalls.Keys);
	}

	[Fact]
	public void WorkspaceReplacementKeepsTheJournalBaselineForTheSameProject()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var otherProject = workspace.CreateDirectory("other");

		Assert.True(TerminalWorkspaceSession.ShouldResetAgentJournalOpeningBaseline(null, project));
		Assert.False(TerminalWorkspaceSession.ShouldResetAgentJournalOpeningBaseline(project, project));
		Assert.True(TerminalWorkspaceSession.ShouldResetAgentJournalOpeningBaseline(project, otherProject));
	}

	[Fact]
	public void IncompleteHistoryIsNamedInCallDetails()
	{
		var marker = CreateCall(3, "journal", []) with
		{
			Arguments = new Dictionary<string, string> { ["lost_events"] = "2" },
			Notices = ["history-incomplete"]
		};

		var text = TerminalAgentJournalPresentation.BuildCallDetails(CreateSession(), [marker]);

		Assert.Contains("History is incomplete: 2 events", text, StringComparison.Ordinal);
	}

	[Fact]
	public void IncompleteHistoryUsesLocalizedCount()
	{
		var marker = CreateCall(3, "journal", []) with
		{
			Arguments = new Dictionary<string, string> { ["lost_events"] = "2" },
			Notices = ["history-incomplete"]
		};

		var text = TerminalAgentJournalPresentation.BuildCallDetails(
			CreateSession(),
			[marker],
			(key, fallback) => key == "AgentJournal.Notice.HistoryIncomplete"
				? "Localized incomplete: {0}"
				: fallback);

		Assert.Contains("Localized incomplete: 2", text, StringComparison.Ordinal);
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
		var session = CreateSession() with
		{
			Roots = [new AgentJournalRoot(workspace.Path, "project")]
		};
		var receipt = new AgentJournalReceipt(
			session,
			session.Totals,
			[new AgentJournalDeliveredPath("src/App.cs", 1)],
			[call]);
		var snapshot = TerminalAgentJournalSnapshot.Create(workspace.Path, receipt);

		_ = new TerminalAgentJournalSessionRow(session, Localize).ToString();
		_ = TerminalAgentJournalPresentation.BuildSessionHeader(Localize);
		_ = TerminalAgentJournalPresentation.BuildCallDetails(session, [call], Localize);
		_ = TerminalAgentJournalPresentation.BuildFocusedDeliveryHint(
			snapshot,
			Path.GetFullPath(Path.Combine(workspace.Path, "src", "App.cs")),
			Localize);

		Assert.Contains("AgentJournal.Mode.Live", keys);
		Assert.Contains("AgentJournal.Column.Tool", keys);
		Assert.Contains("AgentJournal.Footer", keys);
		Assert.Contains("AgentActivity.Tree.ToolTip.One", keys);
		Assert.Contains("AgentJournal.Column.Session", keys);
		Assert.Contains("AgentJournal.Column.Client", keys);
	}

	[Fact]
	public void FocusedDeliveryHintUsesTheWorkspaceLanguageWithoutToolsOrCallCounts()
	{
		using var workspace = new TemporaryDirectory();
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.Ru);
		var session = CreateSession() with
		{
			Totals = CreateSession().Totals with { Calls = 5 },
			Roots = [new AgentJournalRoot(workspace.Path, "project")]
		};
		var calls = new[]
		{
			CreateCall(1, "get_file", ["README.md"]),
			CreateCall(2, "search_project", ["README.md"]),
			CreateCall(3, "pack_context", ["README.md"])
		};
		var snapshot = TerminalAgentJournalSnapshot.Create(
			workspace.Path,
			new AgentJournalReceipt(session, session.Totals, [], calls));

		var hint = TerminalAgentJournalPresentation.BuildFocusedDeliveryHint(
			snapshot,
			Path.GetFullPath(Path.Combine(workspace.Path, "README.md")),
			(key, _) => localization[key],
			AppLanguage.Ru);

		Assert.Equal("Агент получил 3 раза", hint);
		Assert.All(
			new[] { "get_file", "search_project", "pack_context", "5", localization["Menu.View.AgentActivity"] },
			fragment => Assert.DoesNotContain(fragment, hint, StringComparison.Ordinal));
	}

	[Theory]
	[InlineData(AppLanguage.Ru, 1, "Агент получил 1 раз")]
	[InlineData(AppLanguage.Ru, 3, "Агент получил 3 раза")]
	[InlineData(AppLanguage.Ru, 5, "Агент получил 5 раз")]
	[InlineData(AppLanguage.Ru, 21, "Агент получил 21 раз")]
	[InlineData(AppLanguage.En, 1, "Agent received 1 time")]
	[InlineData(AppLanguage.En, 3, "Agent received 3 times")]
	[InlineData(AppLanguage.Pl, 1, "Agent otrzymał 1 raz")]
	[InlineData(AppLanguage.Pl, 3, "Agent otrzymał 3 razy")]
	[InlineData(AppLanguage.Uk, 1, "Агент отримав 1 раз")]
	[InlineData(AppLanguage.Uk, 3, "Агент отримав 3 рази")]
	[InlineData(AppLanguage.Uk, 5, "Агент отримав 5 разів")]
	public void FocusedDeliveryHintUsesTheLanguagePluralForm(
		AppLanguage language,
		int deliveries,
		string expected)
	{
		using var workspace = new TemporaryDirectory();
		var localization = new LocalizationService(new JsonLocalizationCatalog(), language);
		var session = CreateSession() with
		{
			Roots = [new AgentJournalRoot(workspace.Path, "project")]
		};
		var calls = Enumerable.Range(1, deliveries)
			.Select(static sequence => CreateCall(sequence, "get_file", ["README.md"]))
			.ToArray();
		var snapshot = TerminalAgentJournalSnapshot.Create(
			workspace.Path,
			new AgentJournalReceipt(session, session.Totals, [], calls));

		var hint = TerminalAgentJournalPresentation.BuildFocusedDeliveryHint(
			snapshot,
			Path.GetFullPath(Path.Combine(workspace.Path, "README.md")),
			(key, _) => localization[key],
			language);

		Assert.Equal(expected, hint);
	}

	[Fact]
	public void JournalSessionLabelsFollowTheWorkspaceLanguage()
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.Ru);
		string Localize(string key, string _) => localization[key];

		var header = TerminalAgentJournalPresentation.BuildSessionHeader(Localize);
		var details = TerminalAgentJournalPresentation.BuildCallDetails(CreateSession(), [], Localize);

		Assert.StartsWith($"{localization["AgentJournal.Column.Session"]} | ", header, StringComparison.Ordinal);
		Assert.Contains($"{localization["AgentJournal.Column.Session"]}: session-42", details, StringComparison.Ordinal);
		Assert.Contains(
			$"{localization["AgentJournal.Column.Client"]}: client | " +
			$"{localization["AgentJournal.Column.Mode"]}: {localization["AgentJournal.Mode.Live"]} | " +
			$"{localization["AgentJournal.Live"]}: {localization["Terminal.Value.Yes"]}",
			details,
			StringComparison.Ordinal);
		Assert.DoesNotContain("Session ", details, StringComparison.Ordinal);
		Assert.DoesNotContain("Client ", details, StringComparison.Ordinal);
	}

	[Fact]
	public void LiveIndicatorIgnoresStandardSessions()
	{
		var now = new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);
		var standard = new LiveSessionRecord(
			42,
			now,
			"standard-client",
			"1.0",
			[@"C:\work\project"],
			now,
			AgentJournalMode.Standard);
		var live = standard with
		{
			Pid = 43,
			ClientName = "live-client",
			Mode = AgentJournalMode.Live
		};

		Assert.Equal(
			string.Empty,
			TerminalAgentJournalPresentation.BuildLiveSessionIndicator([standard], null));
		Assert.Equal(
			"Live context (live-client)",
			TerminalAgentJournalPresentation.BuildLiveSessionIndicator([standard, live], null));
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
