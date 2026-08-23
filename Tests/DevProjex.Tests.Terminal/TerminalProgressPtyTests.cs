using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalProgressPtyTests
{
	[Fact(Timeout = 180_000)]
	public async Task RealFolderExportExposesCancelableActiveProgress()
	{
		using var project = CreateLargeProject();
		using var output = new TemporaryDirectory();
		var destination = Path.Combine(output.Path, "project-export");
		string? dataRoot = null;
		await using var terminal = await TerminalPtyHarness.StartAsync(
			project.Path,
			[
				"tui",
				project.Path,
				"--profile",
				"standard",
				"--screen",
				"inline",
				"--no-mouse",
				"--language",
				"en"
			],
			environment: new Dictionary<string, string>
			{
				[TerminalProgressCheckpointProtocol.CheckpointsVariable] = "25"
			},
			initializeDataRoot: path => dataRoot = path,
			useProgressCheckpointHost: true,
			cancellationToken: TestContext.Current.CancellationToken,
			writeShellCompletionMarker: true);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("Z", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Choose the physical output kind",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await ReplacePromptTextAsync(terminal, destination);
		await terminal.WaitForScreenAsync(
			"Export?",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);

		Assert.NotNull(dataRoot);
		await WaitForFileAsync(
			Path.Combine(
				dataRoot,
				TerminalProgressCheckpointProtocol.DirectoryName,
				TerminalProgressCheckpointProtocol.GetReachedFileName("25")),
			TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"25%",
			cancellationToken: TestContext.Current.CancellationToken);
		var active = await terminal.WaitForScreenAsync(
			"Esc or Ctrl+C",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("Copying files", active, StringComparison.Ordinal);
		Assert.Contains("25%", active, StringComparison.Ordinal);
		Assert.Contains("Esc or Ctrl+C", active, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);

		await terminal.SendCtrlCAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Operation canceled",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(Directory.Exists(destination));
		Assert.False(terminal.HasExited);
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);

		await StartFolderExportAsync(
			terminal,
			destination,
			TestContext.Current.CancellationToken);
		var completed = await terminal.WaitForScreenAsync(
			"Export completed:",
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("Export completed:", completed, StringComparison.Ordinal);
		Assert.True(Directory.Exists(destination));
		Assert.Equal(
			1_001,
			Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Count());
		Assert.False(terminal.HasExited);

		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendShiftTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendF6Async(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		await terminal.CompleteShellRestorationHandshakeAsync(
			TestContext.Current.CancellationToken);
		TerminalPtyStateAssertions.AssertRestoredAtShellCompletion(
			terminal.RawOutput,
			"inline");
		await terminal.ReleaseParentShellAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	private static TemporaryDirectory CreateLargeProject()
	{
		var project = new TemporaryDirectory();
		project.WriteFile("global.json", "{}");
		var payload = Enumerable.Range(0, 4 * 1024)
			.Select(index => (byte)(index * 31))
			.ToArray();
		for (var index = 0; index < 1_000; index++)
		{
			var path = Path.Combine(project.Path, "src", $"File{index:D4}.bin");
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllBytes(path, payload);
		}

		return project;
	}

	private static async Task StartFolderExportAsync(
		TerminalPtyHarness terminal,
		string destination,
		CancellationToken cancellationToken)
	{
		await terminal.SendAsync("Z", cancellationToken);
		await terminal.WaitForScreenAsync(
			"Choose the physical output kind",
			cancellationToken: cancellationToken);
		await terminal.SendTabAsync(cancellationToken);
		await terminal.SendTabAsync(cancellationToken);
		await terminal.SendEnterAsync(cancellationToken);
		await ReplacePromptTextAsync(terminal, destination);
		await terminal.WaitForScreenAsync(
			"Export?",
			cancellationToken: cancellationToken);
		await terminal.SendEnterAsync(cancellationToken);
	}

	private static async Task WaitForFileAsync(
		string path,
		CancellationToken cancellationToken)
	{
		var timeout = Stopwatch.StartNew();
		while (timeout.Elapsed < TimeSpan.FromSeconds(45))
		{
			if (File.Exists(path))
				return;
			await Task.Delay(25, cancellationToken);
		}

		throw new TimeoutException($"Timed out waiting for progress checkpoint: {path}");
	}

	private static async Task ReplacePromptTextAsync(
		TerminalPtyHarness terminal,
		string value)
	{
		await terminal.WaitForScreenAsync(
			"Exact destination:",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendCtrlAAsync(TestContext.Current.CancellationToken);
		await terminal.SendAsync(value, TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
	}
}
