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
		Assert.Contains("search-query text", server, StringComparison.Ordinal);
		Assert.Contains("Active Standard and Live sessions are exempt", server, StringComparison.Ordinal);
		Assert.Contains("only when its text was actually returned", server, StringComparison.Ordinal);
		Assert.Contains("mark the history incomplete", server, StringComparison.Ordinal);
		Assert.Contains("GUI", server, StringComparison.Ordinal);
		Assert.Contains("Terminal Workspace", server, StringComparison.Ordinal);
		Assert.Contains("CLI", server, StringComparison.Ordinal);
		Assert.Contains("context receipt", server, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("120", server, StringComparison.Ordinal);
		Assert.Contains("0.036 ms", server, StringComparison.Ordinal);
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
		Assert.Contains("search-query text", security, StringComparison.Ordinal);
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

	[Fact]
	public void FirstRunDocumentationExplainsModesVerificationAndInstallationBoundaries()
	{
		var root = FindRepositoryRoot();
		var readme = File.ReadAllText(Path.Combine(root, "README.md"));
		var server = File.ReadAllText(Path.Combine(root, "Docs", "McpServer.md"));
		var installation = File.ReadAllText(Path.Combine(root, "Docs", "Installation.md"));

		foreach (var document in new[] { readme, server })
		{
			Assert.Contains("Live context uses `--live`", document, StringComparison.Ordinal);
			Assert.Contains("does not follow the window selection", document, StringComparison.Ordinal);
			Assert.Contains("`/mcp`", document, StringComparison.Ordinal);
			Assert.Contains("`codex mcp list`", document, StringComparison.Ordinal);
			Assert.Contains("Through DevProjex, show the tree of the current selection", document, StringComparison.Ordinal);
			Assert.Contains("appears in the agent journal", document, StringComparison.Ordinal);
			Assert.DoesNotContain("Desktop connections always include `--live`", document, StringComparison.Ordinal);
		}

		Assert.Contains("GitHub release builds are unsigned", installation, StringComparison.Ordinal);
		Assert.Contains("installed on Windows", installation, StringComparison.Ordinal);
		Assert.Contains("WSL", installation, StringComparison.Ordinal);
		Assert.Contains("Microsoft Store", installation, StringComparison.Ordinal);
		Assert.Contains("Help → Terminal command setup", installation, StringComparison.Ordinal);
		Assert.Contains("/usr/local/bin/devprojex", installation, StringComparison.Ordinal);
		Assert.DoesNotContain("Move `DevProjex` to a directory on `PATH`", installation, StringComparison.Ordinal);

		Assert.Contains("### MCP catalog size", server, StringComparison.Ordinal);
		Assert.Contains("data sent to the client", server, StringComparison.Ordinal);
		Assert.Contains("depends on the client", server, StringComparison.Ordinal);
		Assert.DoesNotContain("already paid for", server, StringComparison.Ordinal);
	}

	private static string FindRepositoryRoot()
	{
		var current = new DirectoryInfo(AppContext.BaseDirectory);
		while (current is not null && !File.Exists(Path.Combine(current.FullName, "DevProjex.sln")))
			current = current.Parent;
		return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
	}
}
