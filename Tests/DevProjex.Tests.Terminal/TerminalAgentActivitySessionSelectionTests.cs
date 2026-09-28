namespace DevProjex.Tests.Terminal;

public sealed class TerminalAgentActivitySessionSelectionTests
{
	[Fact]
	public void IdleNewerLiveSessionDoesNotHideAnOlderSessionWithCalls()
	{
		var idle = CreateSession("newer", calls: 0);
		var active = CreateSession("older", calls: 2);

		var selected = TerminalWorkspaceSession.SelectActivitySession([idle, active]);

		Assert.Same(active, selected);
	}

	[Fact]
	public void NewestLiveSessionIsUsedWhenNoSessionHasCalls()
	{
		var newest = CreateSession("newer", calls: 0);
		var older = CreateSession("older", calls: 0);

		Assert.Same(newest, TerminalWorkspaceSession.SelectActivitySession([newest, older]));
	}

	[Fact]
	public void CompletedAndStandardSessionsAreIgnored()
	{
		var completed = CreateSession("completed", calls: 3, isLive: false);
		var standard = CreateSession("standard", calls: 3, mode: AgentJournalMode.Standard);

		Assert.Null(TerminalWorkspaceSession.SelectActivitySession([completed, standard]));
	}

	private static AgentJournalSession CreateSession(
		string id,
		long calls,
		bool isLive = true,
		AgentJournalMode mode = AgentJournalMode.Live)
	{
		var started = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
		return new AgentJournalSession(
			id,
			started,
			EndedUtc: null,
			Pid: 42,
			started,
			"sample-client",
			"1.0",
			mode,
			[new AgentJournalRoot("/project", "project")],
			AgentJournalToolSet.Full,
			"5.2.0",
			HidePrivateData: false,
			AgentJournalTotals.Empty with { Calls = calls },
			isLive);
	}
}
