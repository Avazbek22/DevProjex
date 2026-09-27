using DevProjex.Infrastructure.ResourceStore;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalAgentJournalCommandTests
{
	private static readonly LocalizationService English =
		new(new JsonLocalizationCatalog(), AppLanguage.En);

	[Fact]
	public void ExportIntoTheProjectReportsAnUnsafeDestination()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var destination = TerminalWorkspaceSession.ResolveAgentJournalExportDestination(
			project,
			"receipt.md");

		var exception = Assert.ThrowsAny<Exception>(() =>
			ExactOutputDestinationValidator.ValidateContext(project, destination, overwrite: false));
		var error = TerminalWorkspaceSession.MapAgentJournalFailure(exception, English);

		Assert.Equal("DPX-EXPORT-UNSAFE-DESTINATION", error.Code);
		Assert.Equal(English["Terminal.Error.UnsafeDestination"], error.Message);
	}

	[Fact]
	public void ExportIntoAMissingFolderReportsAnUnavailableDestination()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var destination = TerminalWorkspaceSession.ResolveAgentJournalExportDestination(
			project,
			Path.Combine("..", "missing", "receipt.md"));

		var exception = Assert.ThrowsAny<Exception>(() =>
			ExactOutputDestinationValidator.ValidateContext(project, destination, overwrite: false));
		var error = TerminalWorkspaceSession.MapAgentJournalFailure(exception, English);

		Assert.Equal("DPX-EXPORT-DESTINATION-UNAVAILABLE", error.Code);
		Assert.Equal(English["Error.ProjectCopy.DestinationUnavailable"], error.Message);
	}

	[Fact]
	public void UnexpectedJournalFailuresStillProduceAVisibleError()
	{
		var error = TerminalWorkspaceSession.MapAgentJournalFailure(
			new InvalidOperationException("unexpected"),
			English);

		Assert.Equal("DPX-TUI-OPERATION-FAILED", error.Code);
		Assert.Equal(English["Terminal.Tui.Error.OperationFailed"], error.Message);
	}

	[Fact]
	public void UnknownSessionInANonEmptyJournalIsNotReportedAsAnEmptyJournal()
	{
		IReadOnlyList<AgentJournalSession> sessions = [CreateSession("first"), CreateSession("second")];

		Assert.Null(TerminalWorkspaceSession.SelectAgentJournalSessions(sessions, "missing"));
		Assert.Equal(
			["second"],
			TerminalWorkspaceSession.SelectAgentJournalSessions(sessions, "second")!
				.Select(static session => session.Id));
		Assert.Same(sessions, TerminalWorkspaceSession.SelectAgentJournalSessions(sessions, "last"));
		Assert.Same(sessions, TerminalWorkspaceSession.SelectAgentJournalSessions(sessions, null));
	}

	[Fact]
	public void UnknownSessionInAnEmptyJournalKeepsTheEmptyJournalGuidance()
	{
		var selected = TerminalWorkspaceSession.SelectAgentJournalSessions([], "missing");

		Assert.NotNull(selected);
		Assert.Empty(selected);
	}

	private static AgentJournalSession CreateSession(string id)
	{
		var started = new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);
		return new AgentJournalSession(
			id,
			started,
			started.AddMinutes(1),
			42,
			started.AddSeconds(-1),
			"client",
			"1.0",
			AgentJournalMode.Live,
			[new AgentJournalRoot(@"C:\work\project", "project")],
			AgentJournalToolSet.Full,
			"5.2",
			false,
			AgentJournalTotals.Empty,
			false);
	}
}
