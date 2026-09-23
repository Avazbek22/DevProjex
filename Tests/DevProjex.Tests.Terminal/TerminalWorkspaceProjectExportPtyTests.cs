namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalWorkspaceProjectExportPtyTests
{
	[Fact(Timeout = 120_000)]
	public async Task ExistingFolderExportConfirmationCannotOverwriteDestination()
	{
		using var project = new TemporaryDirectory();
		using var output = new TemporaryDirectory();
		project.WriteFile("Source.cs", "public sealed class Source { }");
		var destination = output.CreateDirectory("existing-export");
		var sentinel = output.WriteFile("existing-export/Keep.txt", "keep this folder");
		await using var terminal = await StartWorkspaceAsync(project.Path);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(
			$":export folder \"{destination}\"\r",
			TestContext.Current.CancellationToken);
		var confirmation = await terminal.WaitForScreenAsync(
			"Export?",
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains("Dry run", confirmation, StringComparison.Ordinal);
		Assert.DoesNotContain("Overwrite", confirmation, StringComparison.Ordinal);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var canceled = await terminal.WaitForScreenWithoutAsync(
			"Export?",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.DoesNotContain("Export completed:", canceled, StringComparison.Ordinal);
		Assert.Equal("keep this folder", File.ReadAllText(sentinel));
		Assert.False(File.Exists(Path.Combine(destination, "Source.cs")));
		await QuitAsync(terminal);
	}

	[Fact(Timeout = 120_000)]
	public async Task HideSecretsFolderExportReportsAndExcludesMalformedUtf8BomSource()
	{
		using var project = new TemporaryDirectory();
		using var output = new TemporaryDirectory();
		project.WriteFile("Valid.cs", "public sealed class Valid { }");
		var malformedPath = Path.Combine(project.Path, "Malformed.cs");
		File.WriteAllBytes(malformedPath, [0xEF, 0xBB, 0xBF, 0xC3, 0x28]);
		var destination = Path.Combine(output.Path, "protected-export");
		await using var terminal = await StartWorkspaceAsync(project.Path);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(":set hide-secrets on\r", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Hide secrets: enabled",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync(
			$":export folder \"{destination}\"\r",
			TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Export?",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);

		var completed = await terminal.WaitForScreenAsync(
			"Files excluded from content output: 1.",
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("Export completed:", completed, StringComparison.Ordinal);
		Assert.True(File.Exists(Path.Combine(destination, "Valid.cs")));
		Assert.False(File.Exists(Path.Combine(destination, "Malformed.cs")));
		Assert.False(terminal.HasExited);
		await QuitAsync(terminal);
	}

	private static Task<TerminalPtyHarness> StartWorkspaceAsync(string projectPath) =>
		TerminalPtyHarness.StartAsync(
			projectPath,
			["tui", projectPath, "--profile", "standard", "--screen", "inline", "--no-mouse", "--language", "en"],
			columns: 180,
			rows: 40,
			cancellationToken: TestContext.Current.CancellationToken);

	private static async Task QuitAsync(TerminalPtyHarness terminal)
	{
		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				timeout: TimeSpan.FromSeconds(30),
				cancellationToken: TestContext.Current.CancellationToken));
	}
}
