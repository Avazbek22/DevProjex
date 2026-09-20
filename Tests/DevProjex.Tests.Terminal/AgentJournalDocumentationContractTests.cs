namespace DevProjex.Tests.Terminal;

public sealed class AgentJournalDocumentationContractTests
{
	[Fact]
	public void McpDocumentationDefinesJournalContentsRetentionAndEveryReaderSurface()
	{
		var root = FindRepositoryRoot();
		var server = File.ReadAllText(Path.Combine(root, "Docs", "McpServer.md"));

		Assert.Contains("## Agent journal", server, StringComparison.Ordinal);
		Assert.Contains("30 days", server, StringComparison.Ordinal);
		Assert.Contains("200 sessions", server, StringComparison.Ordinal);
		Assert.Contains("does not store file contents", server, StringComparison.Ordinal);
		Assert.Contains("GUI", server, StringComparison.Ordinal);
		Assert.Contains("Terminal Workspace", server, StringComparison.Ordinal);
		Assert.Contains("CLI", server, StringComparison.Ordinal);
		Assert.Contains("context receipt", server, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void TerminalDocumentationListsJournalCommandsAndActivityToggle()
	{
		var terminal = File.ReadAllText(Path.Combine(
			FindRepositoryRoot(),
			"Docs",
			"TerminalWorkspace.md"));

		Assert.Contains("`mcp log [session <id>\\|last]`", terminal, StringComparison.Ordinal);
		Assert.Contains("`mcp log export <path> [markdown\\|json] [session <id>\\|last]`", terminal, StringComparison.Ordinal);
		Assert.Contains("`mcp log clear`", terminal, StringComparison.Ordinal);
		Assert.Contains("`set activity on|off`", terminal, StringComparison.Ordinal);
	}

	[Fact]
	public void SecurityDocumentationKeepsTheJournalLocalAndTelemetryFree()
	{
		var security = File.ReadAllText(Path.Combine(
			FindRepositoryRoot(),
			"Docs",
			"Security.md"));

		Assert.Contains("agent journal", security, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("never leaves the machine", security, StringComparison.Ordinal);
		Assert.Contains("no project file contents", security, StringComparison.Ordinal);
		Assert.Contains("does not change the telemetry-free guarantee", security, StringComparison.Ordinal);
	}

	[Fact]
	public void ReadmeNamesTheJournalWithoutPromisingContentCapture()
	{
		var readme = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "README.md"));

		Assert.Contains("agent journal", readme, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("context receipt", readme, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("metadata and counts", readme, StringComparison.Ordinal);
	}

	private static string FindRepositoryRoot()
	{
		var current = new DirectoryInfo(AppContext.BaseDirectory);
		while (current is not null && !File.Exists(Path.Combine(current.FullName, "DevProjex.sln")))
			current = current.Parent;
		return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
	}
}
