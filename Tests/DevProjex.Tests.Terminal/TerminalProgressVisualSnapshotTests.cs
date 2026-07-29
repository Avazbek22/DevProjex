using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalProgressVisualSnapshotTests
{
	[Fact(Timeout = 120_000)]
	public async Task MeasuredExportSnapshotsCoverPreparationProgressCompactAndCompletion()
	{
		using var project = CreateProject("DevProjex-Tui-Progress-Project");
		using var output = new FixedTemporaryDirectory("DevProjex-Tui-Progress-Output");
		var destination = Path.Combine(output.Path, "project-export");
		string? dataRoot = null;
		await using var terminal = await StartProjectAsync(
			project.Path,
			"en",
			columns: 120,
			rows: 30,
			new Dictionary<string, string>
			{
				[TerminalProgressCheckpointProtocol.CheckpointsVariable] = "25,50,90",
				[TerminalProgressCheckpointProtocol.PhasesVariable] = "preparing"
			},
			path => dataRoot = path);

		await OpenFolderExportDestinationAsync(
			terminal,
			destination,
			TestContext.Current.CancellationToken);
		var checkpointRoot = GetCheckpointRoot(dataRoot);

		await WaitForCheckpointAsync(
			checkpointRoot,
			"preparing",
			TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "Preparing context");
		Verify(
			"workspace-progress-preparing-en-120x30",
			terminal,
			project.Path,
			(output.Path, "<OUTPUT_ROOT>"));
		ReleaseCheckpoint(checkpointRoot, "preparing");
		await ConfirmExportSummaryAsync(
			terminal,
			TestContext.Current.CancellationToken);

		await WaitForCheckpointAsync(
			checkpointRoot,
			"25",
			TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "25%");
		Verify(
			"workspace-progress-25-en-120x30",
			terminal,
			project.Path,
			(output.Path, "<OUTPUT_ROOT>"));
		ReleaseCheckpoint(checkpointRoot, "25");

		await WaitForCheckpointAsync(
			checkpointRoot,
			"50",
			TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "50%");
		Verify(
			"workspace-progress-50-en-120x30",
			terminal,
			project.Path,
			(output.Path, "<OUTPUT_ROOT>"));
		await terminal.ResizeAsync(80, 24, TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Up/Down Move",
			cancellationToken: TestContext.Current.CancellationToken);
		await WaitForStableMeasuredScreenAsync(terminal, "50%");
		Verify(
			"workspace-progress-50-en-80x24",
			terminal,
			project.Path,
			(output.Path, "<OUTPUT_ROOT>"));
		await terminal.ResizeAsync(120, 30, TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		await WaitForStableMeasuredScreenAsync(terminal, "50%");
		ReleaseCheckpoint(checkpointRoot, "50");

		await WaitForCheckpointAsync(
			checkpointRoot,
			"90",
			TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "90%");
		Verify(
			"workspace-progress-90-en-120x30",
			terminal,
			project.Path,
			(output.Path, "<OUTPUT_ROOT>"));
		ReleaseCheckpoint(checkpointRoot, "90");

		await WaitForStableScreenAsync(terminal, "Equivalent command:");
		Verify(
			"workspace-progress-complete-en-120x30",
			terminal,
			project.Path,
			(output.Path, "<OUTPUT_ROOT>"));
		Assert.True(File.Exists(Path.Combine(destination, "src", "File0100.bin")));
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Equivalent command:",
			cancellationToken: TestContext.Current.CancellationToken);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task CanceledMeasuredExportSnapshotKeepsWorkspaceUsable()
	{
		using var project = CreateProject("DevProjex-Tui-Cancel-Project");
		using var output = new FixedTemporaryDirectory("DevProjex-Tui-Cancel-Output");
		var destination = Path.Combine(output.Path, "project-export");
		string? dataRoot = null;
		await using var terminal = await StartProjectAsync(
			project.Path,
			"en",
			120,
			30,
			new Dictionary<string, string>
			{
				[TerminalProgressCheckpointProtocol.CheckpointsVariable] = "50"
			},
			path => dataRoot = path);

		await OpenFolderExportDestinationAsync(
			terminal,
			destination,
			TestContext.Current.CancellationToken);
		await ConfirmExportSummaryAsync(
			terminal,
			TestContext.Current.CancellationToken);
		await WaitForCheckpointAsync(
			GetCheckpointRoot(dataRoot),
			"50",
			TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "50%");
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "Operation canceled");
		Verify(
			"workspace-progress-canceled-en-120x30",
			terminal,
			project.Path,
			(output.Path, "<OUTPUT_ROOT>"));
		Assert.False(Directory.Exists(destination));
		Assert.False(terminal.HasExited);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task ContextExportSnapshotUsesIndeterminateWritingPhase()
	{
		using var project = CreateProject("DevProjex-Tui-Context-Project");
		using var output = new FixedTemporaryDirectory("DevProjex-Tui-Context-Output");
		var destination = Path.Combine(output.Path, "context-output.md");
		string? dataRoot = null;
		await using var terminal = await StartProjectAsync(
			project.Path,
			"en",
			120,
			30,
			new Dictionary<string, string>
			{
				[TerminalProgressCheckpointProtocol.PhasesVariable] = "writing-context"
			},
			path => dataRoot = path);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("E", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Destination:",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendCtrlAAsync(TestContext.Current.CancellationToken);
		await terminal.SendAsync(destination, TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await ConfirmExportSummaryAsync(
			terminal,
			TestContext.Current.CancellationToken);

		var checkpointRoot = GetCheckpointRoot(dataRoot);
		await WaitForCheckpointAsync(
			checkpointRoot,
			"writing-context",
			TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "Esc or Ctrl+C");
		Assert.Contains(
			"Writing context document",
			terminal.CaptureScreen(),
			StringComparison.Ordinal);
		Verify(
			"workspace-progress-context-indeterminate-en-120x30",
			terminal,
			project.Path,
			(output.Path, "<OUTPUT_ROOT>"));
		ReleaseCheckpoint(checkpointRoot, "writing-context");

		await terminal.WaitForScreenAsync(
			"Equivalent command:",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.True(File.Exists(destination));
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Equivalent command:",
			cancellationToken: TestContext.Current.CancellationToken);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task ZipExportSnapshotUsesMeasuredProgressAndCancelableStaging()
	{
		using var project = CreateProject("DevProjex-Tui-Zip-Project");
		using var output = new FixedTemporaryDirectory("DevProjex-Tui-Zip-Output");
		var destination = Path.Combine(output.Path, "project-export.zip");
		string? dataRoot = null;
		await using var terminal = await StartProjectAsync(
			project.Path,
			"en",
			120,
			30,
			new Dictionary<string, string>
			{
				[TerminalProgressCheckpointProtocol.CheckpointsVariable] = "50"
			},
			path => dataRoot = path);

		await OpenZipExportDestinationAsync(
			terminal,
			destination,
			TestContext.Current.CancellationToken);
		await ConfirmExportSummaryAsync(
			terminal,
			TestContext.Current.CancellationToken);
		await WaitForCheckpointAsync(
			GetCheckpointRoot(dataRoot),
			"50",
			TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "Building ZIP");
		await terminal.WaitForScreenAsync(
			"50%",
			cancellationToken: TestContext.Current.CancellationToken);
		Verify(
			"workspace-progress-50-zip-en-120x30",
			terminal,
			project.Path,
			(output.Path, "<OUTPUT_ROOT>"));

		await terminal.SendCtrlCAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Operation canceled",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(File.Exists(destination));
		Assert.Empty(Directory.EnumerateFiles(output.Path, "*.tmp", SearchOption.AllDirectories));
		Assert.False(terminal.HasExited);
		await ExitAsync(terminal);
	}

	[Theory(Timeout = 120_000)]
	[InlineData("ru", "Копирование файлов", "workspace-progress-50-ru-120x30", false)]
	[InlineData("en", "Copying files", "workspace-progress-50-monochrome-en-120x30", true)]
	public async Task ActiveProgressSnapshotsCoverRussianAndMonochrome(
		string language,
		string phase,
		string snapshot,
		bool monochrome)
	{
		using var project = CreateProject(
			monochrome
				? "DevProjex-Tui-Monochrome-Project"
				: "DevProjex-Tui-Russian-Project");
		using var output = new FixedTemporaryDirectory(
			monochrome
				? "DevProjex-Tui-Monochrome-Output"
				: "DevProjex-Tui-Russian-Output");
		var destination = Path.Combine(output.Path, "project-export");
		string? dataRoot = null;
		var environment = new Dictionary<string, string>
		{
			[TerminalProgressCheckpointProtocol.CheckpointsVariable] = "50"
		};
		if (monochrome)
			environment["NO_COLOR"] = "1";
		await using var terminal = await StartProjectAsync(
			project.Path,
			language,
			120,
			30,
			environment,
			path => dataRoot = path);

		await OpenFolderExportDestinationAsync(
			terminal,
			destination,
			TestContext.Current.CancellationToken,
			language);
		await ConfirmExportSummaryAsync(
			terminal,
			TestContext.Current.CancellationToken,
			language);
		await WaitForCheckpointAsync(
			GetCheckpointRoot(dataRoot),
			"50",
			TestContext.Current.CancellationToken);
		var active = await WaitForStableScreenAsync(terminal, phase);
		if (monochrome)
			Assert.Matches(@"\[[#\-]+\] 50%", active);
		Verify(
			snapshot,
			terminal,
			project.Path,
			(output.Path, "<OUTPUT_ROOT>"));
		await terminal.SendCtrlCAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			language == "ru" ? "Операция отменена" : "Operation canceled",
			cancellationToken: TestContext.Current.CancellationToken);
		await ExitAsync(terminal);
	}

	private static async Task<TerminalPtyHarness> StartProjectAsync(
		string projectPath,
		string language,
		int columns,
		int rows,
		IReadOnlyDictionary<string, string> environment,
		Action<string> initializeDataRoot) =>
		await TerminalPtyHarness.StartAsync(
			projectPath,
			[
				"tui",
				projectPath,
				"--profile",
				"standard",
				"--screen",
				"inline",
				"--no-mouse",
				"--language",
				language
			],
			columns,
			rows,
			environment,
			TestContext.Current.CancellationToken,
			initializeDataRoot,
			useProgressCheckpointHost: true);

	private static async Task OpenFolderExportDestinationAsync(
		TerminalPtyHarness terminal,
		string destination,
		CancellationToken cancellationToken,
		string language = "en")
	{
		var outputKindPrompt = language == "ru"
			? "Выберите тип физического результата"
			: "Choose the physical output kind";
		var destinationPrompt = language == "ru"
			? "Точный путь назначения:"
			: "Exact destination:";
		await terminal.WaitForScreenAsync(
			language == "ru" ? "ДЕРЕВО ПРОЕКТА" : "PROJECT TREE",
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: cancellationToken);
		await terminal.SendAsync("Z", cancellationToken);
		await terminal.WaitForScreenAsync(outputKindPrompt, cancellationToken: cancellationToken);
		await terminal.SendTabAsync(cancellationToken);
		await terminal.SendTabAsync(cancellationToken);
		await terminal.SendEnterAsync(cancellationToken);
		await terminal.WaitForScreenAsync(destinationPrompt, cancellationToken: cancellationToken);
		await terminal.SendCtrlAAsync(cancellationToken);
		await terminal.SendAsync(destination, cancellationToken);
		await terminal.SendEnterAsync(cancellationToken);
	}

	private static async Task OpenZipExportDestinationAsync(
		TerminalPtyHarness terminal,
		string destination,
		CancellationToken cancellationToken)
	{
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: cancellationToken);
		await terminal.SendAsync("Z", cancellationToken);
		await terminal.WaitForScreenAsync(
			"Choose the physical output kind",
			cancellationToken: cancellationToken);
		await terminal.SendTabAsync(cancellationToken);
		await terminal.SendTabAsync(cancellationToken);
		await terminal.SendTabAsync(cancellationToken);
		await terminal.SendEnterAsync(cancellationToken);
		await terminal.WaitForScreenAsync(
			"Exact destination:",
			cancellationToken: cancellationToken);
		await terminal.SendCtrlAAsync(cancellationToken);
		await terminal.SendAsync(destination, cancellationToken);
		await terminal.SendEnterAsync(cancellationToken);
	}

	private static async Task ConfirmExportSummaryAsync(
		TerminalPtyHarness terminal,
		CancellationToken cancellationToken,
		string language = "en")
	{
		var ready = language == "ru"
			? "Состояние назначения: Готово"
			: "Destination state: Ready";
		await terminal.WaitForScreenAsync(ready, cancellationToken: cancellationToken);
		await terminal.SendTabAsync(cancellationToken);
		await terminal.SendTabAsync(cancellationToken);
		await terminal.SendTabAsync(cancellationToken);
		await terminal.SendEnterAsync(cancellationToken);
	}

	private static OwnedProject CreateProject(string name)
	{
		var owner = new TemporaryDirectory();
		var projectPath = owner.CreateDirectory(name);
		File.WriteAllText(
			Path.Combine(projectPath, "global.json"),
			"{}",
			new UTF8Encoding(false));
		var payload = Enumerable.Range(0, 1024)
			.Select(static index => (byte)(index * 31))
			.ToArray();
		for (var index = 1; index <= 100; index++)
		{
			var path = Path.Combine(projectPath, "src", $"File{index:D4}.bin");
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllBytes(path, payload);
		}
		return new OwnedProject(owner, projectPath);
	}

	private static string GetCheckpointRoot(string? dataRoot)
	{
		Assert.False(string.IsNullOrWhiteSpace(dataRoot));
		return Path.Combine(
			dataRoot!,
			TerminalProgressCheckpointProtocol.DirectoryName);
	}

	private static async Task WaitForCheckpointAsync(
		string root,
		string checkpoint,
		CancellationToken cancellationToken)
	{
		var path = Path.Combine(
			root,
			TerminalProgressCheckpointProtocol.GetReachedFileName(checkpoint));
		var timeout = Stopwatch.StartNew();
		while (timeout.Elapsed < TimeSpan.FromSeconds(45))
		{
			if (File.Exists(path))
				return;
			await Task.Delay(25, cancellationToken);
		}
		throw new TimeoutException($"Timed out waiting for progress checkpoint: {path}");
	}

	private static void ReleaseCheckpoint(string root, string checkpoint) =>
		File.WriteAllText(
			Path.Combine(
				root,
				TerminalProgressCheckpointProtocol.GetReleaseFileName(checkpoint)),
			string.Empty,
			new UTF8Encoding(false));

	private static async Task<string> WaitForStableScreenAsync(
		TerminalPtyHarness terminal,
		string expected)
	{
		var stableSamples = 0;
		var timeout = Stopwatch.StartNew();
		while (timeout.Elapsed < TimeSpan.FromSeconds(10))
		{
			var screen = terminal.CaptureScreen();
			if (!string.IsNullOrWhiteSpace(screen) &&
			    screen.Contains(expected, StringComparison.Ordinal))
			{
				stableSamples++;
				if (stableSamples >= 3)
					return screen;
			}
			else
			{
				stableSamples = 0;
			}

			await Task.Delay(80, TestContext.Current.CancellationToken);
		}

		throw new TimeoutException(
			$"Progress screen did not remain visibly stable for '{expected}'.\n" +
			terminal.CaptureScreen());
	}

	private static async Task WaitForStableMeasuredScreenAsync(
		TerminalPtyHarness terminal,
		string expected)
	{
		await terminal.WaitForScreenAsync(
			expected,
			cancellationToken: TestContext.Current.CancellationToken);
		var previous = terminal.CaptureScreen();
		var stableSamples = 0;
		var timeout = Stopwatch.StartNew();
		while (timeout.Elapsed < TimeSpan.FromSeconds(5))
		{
			await Task.Delay(80, TestContext.Current.CancellationToken);
			var current = terminal.CaptureScreen();
			if (string.Equals(previous, current, StringComparison.Ordinal))
			{
				stableSamples++;
				if (stableSamples >= 3)
					return;
			}
			else
			{
				previous = current;
				stableSamples = 0;
			}
		}
		throw new TimeoutException($"Measured progress screen did not stabilize.\n{previous}");
	}

	private static void Verify(
		string name,
		TerminalPtyHarness terminal,
		string projectPath,
		params (string Value, string Replacement)[] replacements)
	{
		var normalizedValues = new[]
			{
				(projectPath, "<PROJECT_ROOT>"),
				(Path.GetDirectoryName(projectPath) ?? string.Empty, "<TEMP_ROOT>"),
				(Path.GetFileName(projectPath), "<PROJECT>")
			}
			.Concat(replacements)
			.ToArray();
		TerminalScreenSnapshot.Verify(name, terminal.CaptureScreen(), normalizedValues);
		TerminalVisualArtifactWriter.WriteIfRequested(name, terminal);
	}

	private static async Task ExitAsync(TerminalPtyHarness terminal)
	{
		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	private sealed class FixedTemporaryDirectory : IDisposable
	{
		public FixedTemporaryDirectory(string name)
		{
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name);
			Delete();
			Directory.CreateDirectory(Path);
		}

		public string Path { get; }

		public void Dispose() => Delete();

		private void Delete()
		{
			if (Directory.Exists(Path))
				Directory.Delete(Path, recursive: true);
		}
	}

	private sealed class OwnedProject(
		TemporaryDirectory owner,
		string path) : IDisposable
	{
		public string Path { get; } = path;

		public void Dispose() => owner.Dispose();
	}
}
