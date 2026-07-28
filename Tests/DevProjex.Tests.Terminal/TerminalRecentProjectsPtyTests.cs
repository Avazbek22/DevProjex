using DevProjex.Infrastructure.ProjectProfiles;
using DevProjex.Infrastructure.RecentProjects;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalRecentProjectsPtyTests
{
	[Fact(Timeout = 90_000)]
	public async Task PopulatedRecentSelectionOpensWorkspaceAndMovesEntryToFront()
	{
		using var firstProject = CreateProject("FirstProject", "FirstMarker.cs");
		using var secondProject = CreateProject("Second Project", "SecondMarker.cs");
		using var welcomeDirectory = new TemporaryDirectory();
		welcomeDirectory.WriteFile("notes.txt", "not a project");

		await using var terminal = await TerminalPtyHarness.StartAsync(
			welcomeDirectory.Path,
			["--language", "en"],
			initializeDataRoot: dataRoot =>
			{
				var store = new RecentProjectsStore(() => dataRoot);
				var snapshot = store.AddFolder(null, secondProject.Path);
				store.AddFolder(snapshot, firstProject.Path);
			},
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"Choose a workspace action",
			cancellationToken: TestContext.Current.CancellationToken);
		await SelectWelcomeActionAsync(
			terminal,
			"Recent projects",
			TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var recent = await terminal.WaitForScreenAsync(
			"FirstProject",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("Second Project", recent, StringComparison.Ordinal);

		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> [+] Second Project",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Remove entry",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> [+] Second Project",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Loading project",
			cancellationToken: TestContext.Current.CancellationToken);
		var workspace = await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("Second Project", workspace, StringComparison.Ordinal);
		Assert.Contains("SecondMarker.cs", workspace, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendShiftTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Back to Welcome",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Choose a workspace action",
			cancellationToken: TestContext.Current.CancellationToken);
		await SelectWelcomeActionAsync(
			terminal,
			"Recent projects",
			TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var reordered = await terminal.WaitForScreenAsync(
			"Second Project",
			cancellationToken: TestContext.Current.CancellationToken);
		var secondRow = Array.FindIndex(
			reordered.Split('\n'),
			line => line.Contains("Second Project", StringComparison.Ordinal));
		var firstRow = Array.FindIndex(
			reordered.Split('\n'),
			line => line.Contains("FirstProject", StringComparison.Ordinal));
		Assert.True(secondRow >= 0 && firstRow > secondRow, reordered);

		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Remove entry",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 90_000)]
	public async Task UnicodeRecentWithValidLocalProfileOpensUsingLocalSelection()
	{
		using var project = CreateProject("Проект с пробелом", "UnicodeMarker.cs");
		using var welcomeDirectory = new TemporaryDirectory();
		welcomeDirectory.WriteFile("notes.txt", "not a project");

		await using var terminal = await TerminalPtyHarness.StartAsync(
			welcomeDirectory.Path,
			["--language", "en"],
			initializeDataRoot: dataRoot =>
			{
				new RecentProjectsStore(() => dataRoot).AddFolder(null, project.Path);
				new ProjectProfileStore(() => dataRoot).SaveProfile(
					project.Path,
					new ProjectSelectionProfile(
						SelectedRootFolders: ["src"],
						SelectedExtensions: [".cs"],
						SelectedIgnoreOptions: []));
			},
			cancellationToken: TestContext.Current.CancellationToken);

		await OpenRecentOverlayAsync(terminal, TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Проект с пробелом",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var workspace = await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains("Profile: Local", workspace, StringComparison.Ordinal);
		Assert.Contains("UnicodeMarker.cs", workspace, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task LocalProfileWithUnavailableSelectionsOpensWithDiagnostics()
	{
		using var project = CreateProject("StaleSelectionProject", "AvailableMarker.cs");
		using var welcomeDirectory = new TemporaryDirectory();
		welcomeDirectory.WriteFile("notes.txt", "not a project");

		await using var terminal = await TerminalPtyHarness.StartAsync(
			welcomeDirectory.Path,
			["--language", "en"],
			initializeDataRoot: dataRoot =>
			{
				new RecentProjectsStore(() => dataRoot).AddFolder(null, project.Path);
				new ProjectProfileStore(() => dataRoot).SaveProfile(
					project.Path,
					new ProjectSelectionProfile(
						SelectedRootFolders: ["removed-root"],
						SelectedExtensions: [".removed"],
						SelectedIgnoreOptions: []));
			},
			cancellationToken: TestContext.Current.CancellationToken);

		await OpenRecentOverlayAsync(terminal, TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var workspace = await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains("Profile: Local", workspace, StringComparison.Ordinal);
		Assert.Contains("Warnings 2", workspace, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task InvalidLocalProfileOffersStandardRecoveryAndKeepsSessionAlive()
	{
		using var project = CreateProject("InvalidProfileProject", "RecoveredMarker.cs");
		using var welcomeDirectory = new TemporaryDirectory();
		welcomeDirectory.WriteFile("notes.txt", "not a project");

		await using var terminal = await TerminalPtyHarness.StartAsync(
			welcomeDirectory.Path,
			["--language", "en"],
			initializeDataRoot: dataRoot =>
			{
				new RecentProjectsStore(() => dataRoot).AddFolder(null, project.Path);
				var profileStore = new ProjectProfileStore(() => dataRoot);
				Assert.True(profileStore.EnsureStorageExists());
				File.WriteAllText(profileStore.GetPath(), "{ invalid-primary");
				File.WriteAllText(profileStore.GetPath() + ".bak", "{ invalid-backup");
			},
			cancellationToken: TestContext.Current.CancellationToken);

		await OpenRecentOverlayAsync(terminal, TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var recovery = await terminal.WaitForScreenAsync(
			"Local profile recovery",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("Use Standard", recovery, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);

		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var workspace = await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("Profile: Standard", workspace, StringComparison.Ordinal);
		Assert.Contains("RecoveredMarker.cs", workspace, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task MissingRecentPathCanBeRemovedWithoutLeavingWelcome()
	{
		using var welcomeDirectory = new TemporaryDirectory();
		welcomeDirectory.WriteFile("notes.txt", "not a project");
		var missingPath = Path.Combine(welcomeDirectory.Path, "Deleted Project");
		string? dataRoot = null;

		await using var terminal = await TerminalPtyHarness.StartAsync(
			welcomeDirectory.Path,
			["--language", "en"],
			initializeDataRoot: root =>
			{
				dataRoot = root;
				new RecentProjectsStore(() => root).AddFolder(null, missingPath);
			},
			cancellationToken: TestContext.Current.CancellationToken);

		await OpenRecentOverlayAsync(terminal, TestContext.Current.CancellationToken);
		var recent = await terminal.WaitForScreenAsync(
			"Deleted Project",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("Unavailable", recent, StringComparison.Ordinal);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var error = await terminal.WaitForScreenAsync(
			"selected project directory is unavailable",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("Remove entry", error, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);

		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"(none available)",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.NotNull(dataRoot);
		Assert.Empty(new RecentProjectsStore(() => dataRoot).Load().RecentFolders);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"(none available)",
			cancellationToken: TestContext.Current.CancellationToken);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task CorruptPrimaryRecentDatabaseRecoversEntryFromBackup()
	{
		using var project = CreateProject("BackupProject", "BackupMarker.cs");
		using var welcomeDirectory = new TemporaryDirectory();
		welcomeDirectory.WriteFile("notes.txt", "not a project");

		await using var terminal = await TerminalPtyHarness.StartAsync(
			welcomeDirectory.Path,
			["--language", "en"],
			initializeDataRoot: dataRoot =>
			{
				var store = new RecentProjectsStore(() => dataRoot);
				store.AddFolder(null, project.Path);
				File.WriteAllText(store.GetPath(), "{ invalid-primary");
			},
			cancellationToken: TestContext.Current.CancellationToken);

		await OpenRecentOverlayAsync(terminal, TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"BackupProject",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var workspace = await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("BackupMarker.cs", workspace, StringComparison.Ordinal);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task LockedRecentDatabaseCanRetryAfterWriterReleasesIt()
	{
		using var project = CreateProject("LockedRecentProject", "LockMarker.cs");
		using var welcomeDirectory = new TemporaryDirectory();
		welcomeDirectory.WriteFile("notes.txt", "not a project");
		FileStream? heldLock = null;
		try
		{
			await using var terminal = await TerminalPtyHarness.StartAsync(
				welcomeDirectory.Path,
				["--language", "en"],
				initializeDataRoot: dataRoot =>
				{
					var store = new RecentProjectsStore(() => dataRoot);
					store.AddFolder(null, project.Path);
					heldLock = new FileStream(
						store.GetPath() + ".lock",
						FileMode.OpenOrCreate,
						FileAccess.ReadWrite,
						FileShare.None);
				},
				cancellationToken: TestContext.Current.CancellationToken);

			await OpenRecentOverlayAsync(terminal, TestContext.Current.CancellationToken);
			var unavailable = await terminal.WaitForScreenAsync(
				"Recent project history is temporarily unavailable",
				cancellationToken: TestContext.Current.CancellationToken);
			Assert.Contains("Retry", unavailable, StringComparison.Ordinal);
			Assert.False(terminal.HasExited);

			heldLock?.Dispose();
			heldLock = null;
			await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
			await terminal.WaitForScreenAsync(
				"LockedRecentProject",
				cancellationToken: TestContext.Current.CancellationToken);
			await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
			var workspace = await terminal.WaitForScreenAsync(
				"PROJECT TREE",
				cancellationToken: TestContext.Current.CancellationToken);
			Assert.Contains("LockMarker.cs", workspace, StringComparison.Ordinal);
			await ExitAsync(terminal);
		}
		finally
		{
			heldLock?.Dispose();
		}
	}

	[Fact(Timeout = 90_000)]
	public async Task MouseDoubleClickOnRecentEntryOpensItsWorkspace()
	{
		using var project = CreateProject("MouseRecentProject", "MouseMarker.cs");
		using var welcomeDirectory = new TemporaryDirectory();
		welcomeDirectory.WriteFile("notes.txt", "not a project");
		await using var terminal = await TerminalPtyHarness.StartAsync(
			welcomeDirectory.Path,
			["--language", "en"],
			initializeDataRoot: dataRoot =>
				new RecentProjectsStore(() => dataRoot).AddFolder(null, project.Path),
			cancellationToken: TestContext.Current.CancellationToken);

		await OpenRecentOverlayAsync(terminal, TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"MouseRecentProject",
			cancellationToken: TestContext.Current.CancellationToken);
		var row = terminal.FindVisibleRow("MouseRecentProject");
		Assert.True(row >= 0);
		await terminal.SendMouseClickAsync(
			column: 20,
			row,
			clickCount: 2,
			cancellationToken: TestContext.Current.CancellationToken);

		var workspace = await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("MouseMarker.cs", workspace, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await ExitAsync(terminal);
	}

	private static TestProject CreateProject(string directoryName, string markerFile)
	{
		var root = new TemporaryDirectory();
		var project = root.CreateDirectory(directoryName);
		File.WriteAllText(Path.Combine(project, "global.json"), "{}", new UTF8Encoding(false));
		Directory.CreateDirectory(Path.Combine(project, "src"));
		File.WriteAllText(
			Path.Combine(project, "src", markerFile),
			"internal sealed class Marker {}",
			new UTF8Encoding(false));
		return new TestProject(root, project);
	}

	private static async Task SelectWelcomeActionAsync(
		TerminalPtyHarness terminal,
		string action,
		CancellationToken cancellationToken)
	{
		for (var attempt = 0; attempt < 10; attempt++)
		{
			var screen = terminal.CaptureScreen();
			if (screen.Contains($"> {action}", StringComparison.Ordinal))
				return;
			await terminal.SendDownAsync(cancellationToken);
			await Task.Delay(40, cancellationToken);
		}

		throw new Xunit.Sdk.XunitException(
			$"Could not select welcome action '{action}'.\n{terminal.CaptureScreen()}");
	}

	private static async Task OpenRecentOverlayAsync(
		TerminalPtyHarness terminal,
		CancellationToken cancellationToken)
	{
		await terminal.WaitForScreenAsync(
			"Choose a workspace action",
			cancellationToken: cancellationToken);
		await SelectWelcomeActionAsync(terminal, "Recent projects", cancellationToken);
		await terminal.SendEnterAsync(cancellationToken);
	}

	private static async Task ExitAsync(TerminalPtyHarness terminal)
	{
		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	private sealed class TestProject(
		TemporaryDirectory owner,
		string path) : IDisposable
	{
		public string Path { get; } = path;

		public void Dispose()
		{
			owner.Dispose();
		}
	}
}
