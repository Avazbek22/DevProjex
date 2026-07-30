using System.Globalization;
using DevProjex.Tests.Terminal.Progress;
using Terminal.Gui.Drivers;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class MacOsTerminalNativePtyTests
{
	[Theory(Timeout = 90_000)]
	[InlineData("\u0013", "VSTOP")]
	[InlineData("\u000f", "VDISCARD")]
	[InlineData("\u0003", "VINTR")]
	public async Task PendingControlInputCannotStopOrDiscardShellRestoration(
		string pendingControl,
		string controlName)
	{
		if (!OperatingSystem.IsMacOS())
		{
			Assert.Skip(
				$"{controlName} replay semantics are specific to Darwin's line discipline.");
		}

		using var workspace = new TemporaryDirectory();
		string? internalDataRoot = null;
		await using var terminal = await StartLifecycleHostAsync(
			workspace.Path,
			gatePendingInput: true,
			verifyDiscardSurvivor: string.Equals(
				controlName,
				"VDISCARD",
				StringComparison.Ordinal),
			configureControlCharacters: true,
			initializeDataRoot: dataRoot =>
			{
				internalDataRoot = dataRoot;
				Directory.CreateDirectory(dataRoot);
			},
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForRawOutputAsync(
			MacOsTerminalLifecycleProtocol.ReadyPrefix + "1__",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		await terminal.WaitForRawOutputAsync(
			MacOsTerminalLifecycleProtocol.PendingInputReady,
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync(
			pendingControl + "\u0015",
			TestContext.Current.CancellationToken);
		File.WriteAllText(
			Path.Combine(
				internalDataRoot!,
				MacOsTerminalLifecycleProtocol.PendingInputReleaseFileName),
			string.Empty);
		await terminal.CompleteShellRestorationHandshakeAsync(
			TestContext.Current.CancellationToken);
		if (string.Equals(
			    controlName,
			    "VDISCARD",
			    StringComparison.Ordinal))
		{
			await terminal.WaitForRawOutputAsync(
				MacOsTerminalLifecycleProtocol.DiscardSurvivor,
				cancellationToken: TestContext.Current.CancellationToken);
		}

		AssertShellRestored(terminal.RawOutput);
		await terminal.ReleaseParentShellAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 90_000)]
	public async Task RepeatedRawSessionsPreserveAnInitiallyDisabledSignalPolicy()
	{
		if (!OperatingSystem.IsMacOS())
		{
			Assert.Skip(
				"Darwin termios re-entry is validated on native macOS runners.");
		}

		using var workspace = new TemporaryDirectory();
		await using var terminal = await StartLifecycleHostAsync(
			workspace.Path,
			sessionCount: 2,
			disableSignalGeneration: true,
			delayContinueIntoSecondSession: true,
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForRawOutputAsync(
			MacOsTerminalLifecycleProtocol.ReadyPrefix + "1__",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("1", TestContext.Current.CancellationToken);
		await terminal.WaitForRawOutputAsync(
			MacOsTerminalLifecycleProtocol.ReadyPrefix + "2__",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		await terminal.CompleteShellRestorationHandshakeAsync(
			TestContext.Current.CancellationToken);

		AssertShellRestored(terminal.RawOutput);
		await terminal.ReleaseParentShellAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 90_000)]
	public async Task IsolatedChildCannotTakeTerminalOwnershipAcrossTeardown()
	{
		if (!OperatingSystem.IsMacOS())
		{
			Assert.Skip(
				"Darwin child-process terminal ownership is validated on native macOS runners.");
		}

		using var workspace = new TemporaryDirectory();
		await using var terminal = await StartLifecycleHostAsync(
			workspace.Path,
			spawnIsolatedChild: true,
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForRawOutputAsync(
			MacOsTerminalLifecycleProtocol.IsolatedChildReady,
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForRawOutputAsync(
			MacOsTerminalLifecycleProtocol.ReadyPrefix + "1__",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		await terminal.CompleteShellRestorationHandshakeAsync(
			TestContext.Current.CancellationToken);

		AssertShellRestored(terminal.RawOutput);
		await terminal.ReleaseParentShellAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 90_000)]
	public async Task ContinueAfterTeardownCannotReapplyTheRuntimeRawCache()
	{
		if (!OperatingSystem.IsMacOS())
		{
			Assert.Skip(
				".NET's Darwin SIGCONT terminal cache is validated on native macOS runners.");
		}

		using var workspace = new TemporaryDirectory();
		await using var terminal = await StartLifecycleHostAsync(
			workspace.Path,
			disableSignalGeneration: true,
			verifyContinue: true,
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForRawOutputAsync(
			MacOsTerminalLifecycleProtocol.ReadyPrefix + "1__",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		await terminal.WaitForRawOutputAsync(
			MacOsTerminalLifecycleProtocol.ContinueVerified,
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.CompleteShellRestorationHandshakeAsync(
			TestContext.Current.CancellationToken);

		AssertShellRestored(terminal.RawOutput);
		await terminal.ReleaseParentShellAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 90_000)]
	public async Task ContinueReappliesDeterministicRawModeDuringActiveSession()
	{
		if (!OperatingSystem.IsMacOS())
		{
			Assert.Skip(
				"Darwin active-session SIGCONT recovery is validated on native macOS runners.");
		}

		using var workspace = new TemporaryDirectory();
		await using var terminal = await StartLifecycleHostAsync(
			workspace.Path,
			verifyActiveContinue: true,
			terminalIdentity: "xterm-noapp",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForRawOutputAsync(
			MacOsTerminalLifecycleProtocol.ActiveContinueVerified,
			cancellationToken: TestContext.Current.CancellationToken);
		var normalModeIndex = terminal.RawOutput.LastIndexOf(
			MacOsTerminalLifecycleProtocol.NormalKeypadModeSequence,
			StringComparison.Ordinal);
		Assert.True(
			normalModeIndex >= 0,
			"The PTY did not observe the deliberate normal-keypad transition.");
		var restoredApplicationModeIndex = terminal.RawOutput.IndexOf(
			MacOsTerminalLifecycleProtocol.TermInfoApplicationKeypadModeSequence,
			normalModeIndex +
			MacOsTerminalLifecycleProtocol.NormalKeypadModeSequence.Length,
			StringComparison.Ordinal);
		Assert.True(
			restoredApplicationModeIndex > normalModeIndex,
			"SIGCONT did not restore application cursor/keypad mode.");
		var resumedOutput = terminal.RawOutput[
			(normalModeIndex +
			 MacOsTerminalLifecycleProtocol.NormalKeypadModeSequence.Length)..];
		Assert.DoesNotContain(
			"\u001b[?1h",
			resumedOutput,
			StringComparison.Ordinal);
		await terminal.WaitForRawOutputAsync(
			MacOsTerminalLifecycleProtocol.ReadyPrefix + "1__",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		await terminal.CompleteShellRestorationHandshakeAsync(
			TestContext.Current.CancellationToken);

		AssertShellRestored(terminal.RawOutput);
		await terminal.ReleaseParentShellAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Theory(Timeout = 90_000)]
	[InlineData(
		"alternate",
		"xterm-256color",
		"\u001b[?1l\u001b>",
		"\u001b[?1h\u001b=")]
	[InlineData(
		"inline",
		"xterm-256color",
		"\u001b[?1l\u001b>",
		"\u001b[?1h\u001b=")]
	[InlineData("alternate", "xterm-noapp", "\u001b>", "\u001b=")]
	[InlineData("inline", "xterm-noapp", "\u001b>", "\u001b=")]
	public async Task CtrlZHandsExactTerminalToShellAndResumesUsableTui(
		string screenMode,
		string terminalIdentity,
		string shellKeypadMode,
		string applicationKeypadMode)
	{
		if (!OperatingSystem.IsMacOS())
		{
			Assert.Skip(
				"Darwin foreground job control is validated on native macOS runners.");
		}

		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("global.json", "{}");
		workspace.WriteFile("src/App.cs", "internal sealed class App {}");
		string? internalDataRoot = null;
		await using var terminal = await TerminalPtyHarness.StartAsync(
			workspace.Path,
			[
				"tui",
				workspace.Path,
				"--profile",
				"standard",
				"--screen",
				screenMode,
				"--no-mouse",
				"--language",
				"en"
			],
			columns: 120,
			rows: 30,
			environment: new Dictionary<string, string>
			{
				["TERM"] = terminalIdentity
			},
			cancellationToken: TestContext.Current.CancellationToken,
			initializeDataRoot: dataRoot =>
			{
				internalDataRoot = dataRoot;
				Directory.CreateDirectory(dataRoot);
			},
			writeShellCompletionMarker: true,
			gateUnixJobControlSuspend: true);

		await terminal.WaitForScreenAsync(
			"App.cs",
			cancellationToken: TestContext.Current.CancellationToken);
		var suspendBoundary = terminal.RawOutput.Length;
		await terminal.SendAsync(
			"\u001a",
			TestContext.Current.CancellationToken);
		await terminal.WaitForRawOutputAfterAsync(
			TerminalPtyHarness.SuspendedShellTerminalStateRestoredMarker,
			suspendBoundary,
			cancellationToken: TestContext.Current.CancellationToken);

		var suspendedOutput = terminal.RawOutput;
		var suspendedMarkerIndex = suspendedOutput.IndexOf(
			TerminalPtyHarness.SuspendedShellTerminalStateRestoredMarker,
			suspendBoundary,
			StringComparison.Ordinal);
		Assert.True(suspendedMarkerIndex > suspendBoundary);
		var terminalHandoff =
			suspendedOutput[suspendBoundary..suspendedMarkerIndex];
		Assert.Contains(
			EscSeqUtils.CSI_ResetAttributes,
			terminalHandoff,
			StringComparison.Ordinal);
		Assert.Contains(
			shellKeypadMode,
			terminalHandoff,
			StringComparison.Ordinal);
		Assert.True(
			terminalHandoff.LastIndexOf(
				shellKeypadMode,
				StringComparison.Ordinal) >
			terminalHandoff.LastIndexOf(
				applicationKeypadMode,
				StringComparison.Ordinal),
			"The shell was not left in the final terminfo keypad-local mode.");
		Assert.DoesNotContain(
			TerminalPtyHarness.SuspendedShellTerminalStateMismatchMarker,
			suspendedOutput,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			TerminalPtyHarness.SuspendedJobMissingMarker,
			suspendedOutput,
			StringComparison.Ordinal);
		if (string.Equals(
			    screenMode,
			    "alternate",
			    StringComparison.Ordinal))
		{
			Assert.Contains(
				EscSeqUtils.CSI_RestoreCursorAndRestoreAltBufferWithBackscroll,
				terminalHandoff,
				StringComparison.Ordinal);
		}
		else
		{
			Assert.DoesNotContain(
				EscSeqUtils.CSI_RestoreCursorAndRestoreAltBufferWithBackscroll,
				terminalHandoff,
				StringComparison.Ordinal);
		}
		Assert.False(terminal.HasExited);

		var resumeBoundary = terminal.RawOutput.Length;
		File.WriteAllText(
			Path.Combine(
				internalDataRoot!,
				TerminalPtyHarness.JobControlResumeFileName),
			string.Empty);
		await terminal.WaitForRawOutputAfterAsync(
			EscSeqUtils.CSI_EnableBracketedPaste,
			resumeBoundary,
			cancellationToken: TestContext.Current.CancellationToken);
		var resumedOutput = terminal.RawOutput[resumeBoundary..];
		Assert.Contains(
			applicationKeypadMode,
			resumedOutput,
			StringComparison.Ordinal);
		Assert.True(
			resumedOutput.LastIndexOf(
				applicationKeypadMode,
				StringComparison.Ordinal) >
			resumedOutput.LastIndexOf(
				shellKeypadMode,
				StringComparison.Ordinal),
			"The resumed TUI was not left in the final terminfo keypad-transmit mode.");
		await terminal.SendAsync("?", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"ACTION PALETTE",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"ACTION PALETTE",
			cancellationToken: TestContext.Current.CancellationToken);
		var exitBoundary = terminal.RawOutput.Length;
		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		await terminal.CompleteShellRestorationHandshakeAsync(
			TestContext.Current.CancellationToken);

		AssertShellRestored(terminal.RawOutput);
		Assert.Contains(
			shellKeypadMode,
			terminal.RawOutput[exitBoundary..],
			StringComparison.Ordinal);
		var exitOutput = terminal.RawOutput[exitBoundary..];
		Assert.True(
			exitOutput.LastIndexOf(
				shellKeypadMode,
				StringComparison.Ordinal) >
			exitOutput.LastIndexOf(
				applicationKeypadMode,
				StringComparison.Ordinal),
			"Normal exit did not leave the shell in terminfo keypad-local mode.");
		await terminal.ReleaseParentShellAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	private static Task<TerminalPtyHarness> StartLifecycleHostAsync(
		string workingDirectory,
		int sessionCount = 1,
		bool disableSignalGeneration = false,
		bool verifyContinue = false,
		bool delayContinueIntoSecondSession = false,
		bool verifyActiveContinue = false,
		bool gatePendingInput = false,
		bool verifyDiscardSurvivor = false,
		bool spawnIsolatedChild = false,
		bool configureControlCharacters = false,
		string terminalIdentity = "xterm-256color",
		Action<string>? initializeDataRoot = null,
		CancellationToken cancellationToken = default) =>
		TerminalPtyHarness.StartAsync(
			workingDirectory,
			environment: new Dictionary<string, string>
			{
				[MacOsTerminalLifecycleProtocol.EnabledVariable] = "1",
				[MacOsTerminalLifecycleProtocol.SessionCountVariable] =
					sessionCount.ToString(CultureInfo.InvariantCulture),
				[MacOsTerminalLifecycleProtocol.VerifyContinueVariable] =
					verifyContinue ? "1" : "0",
				[MacOsTerminalLifecycleProtocol.DelayContinueIntoSecondSessionVariable] =
					delayContinueIntoSecondSession ? "1" : "0",
				[MacOsTerminalLifecycleProtocol.VerifyActiveContinueVariable] =
					verifyActiveContinue ? "1" : "0",
				[MacOsTerminalLifecycleProtocol.GatePendingInputVariable] =
					gatePendingInput ? "1" : "0",
				[MacOsTerminalLifecycleProtocol.VerifyDiscardSurvivorVariable] =
					verifyDiscardSurvivor ? "1" : "0",
				[MacOsTerminalLifecycleProtocol.SpawnIsolatedChildVariable] =
					spawnIsolatedChild ? "1" : "0",
				["TERM"] = terminalIdentity
			},
			cancellationToken: cancellationToken,
			initializeDataRoot: initializeDataRoot,
			writeShellCompletionMarker: true,
			useProgressCheckpointHost: true,
			disableUnixSignalGeneration: disableSignalGeneration,
			configureDarwinControlCharacters: configureControlCharacters);

	private static void AssertShellRestored(string output)
	{
		var markerIndex = output.LastIndexOf(
			TerminalPtyHarness.ShellCompletionMarker,
			StringComparison.Ordinal);
		Assert.True(markerIndex >= 0, "The parent shell did not resume.");
		Assert.Null(
			TerminalPtyStateAssertions.FindUnixTerminalStateMismatch(
				output,
				markerIndex));
		Assert.Contains(
			TerminalPtyHarness.ShellTerminalStateRestoredMarker,
			output[..markerIndex],
			StringComparison.Ordinal);
	}
}
