using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DevProjex.Application.Services;
using DevProjex.Kernel;
using DevProjex.Kernel.Models;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Execution;
using DevProjex.Terminal.Tui;
using DevProjex.Tests.Terminal.Progress;

namespace DevProjex.Tests.Terminal.ProgressHost;

internal static class Program
{
	public static int Main(string[] args)
	{
		if (string.Equals(
			    Environment.GetEnvironmentVariable(
				    MacOsTerminalLifecycleProtocol.EnabledVariable),
			    "1",
			    StringComparison.Ordinal))
		{
			return RunMacOsTerminalLifecycleProtocol();
		}

		if (string.Equals(
			    Environment.GetEnvironmentVariable(
				    TerminalSignalCheckpointProtocol.EnabledVariable),
			    "1",
			    StringComparison.Ordinal))
		{
			return RunSignalCheckpointProtocol();
		}

		var dataRoot = Environment.GetEnvironmentVariable(
			InvocationEnvironment.InternalDataRootVariable);
		if (string.IsNullOrWhiteSpace(dataRoot) ||
		    !Path.IsPathFullyQualified(dataRoot))
		{
			Console.Error.WriteLine("The isolated terminal test data root is required.");
			return CommandLineExitCodes.RuntimeError;
		}

		var observer = FileTerminalOperationObserver.Create(dataRoot);
		var environment = new InvocationEnvironment(hasAttachedConsole: true);
		var services = new TerminalServiceFactory(() => dataRoot);
		using var cancellation = TerminalCancellationCoordinator.Register();
		return new TerminalApplication(
				environment,
				services,
				developerCommandRunner: null,
				operationObserver: observer)
			.RunAsync(args, cancellation.Token)
			.GetAwaiter()
			.GetResult();
	}

	private static int RunMacOsTerminalLifecycleProtocol()
	{
		if (!OperatingSystem.IsMacOS())
			return CommandLineExitCodes.RuntimeError;

		var verifyContinue = IsEnabled(
			MacOsTerminalLifecycleProtocol.VerifyContinueVariable);
		var delayContinueIntoSecondSession = IsEnabled(
			MacOsTerminalLifecycleProtocol.DelayContinueIntoSecondSessionVariable);
		var verifyActiveContinue = IsEnabled(
			MacOsTerminalLifecycleProtocol.VerifyActiveContinueVariable);
		var spawnIsolatedChild = IsEnabled(
			MacOsTerminalLifecycleProtocol.SpawnIsolatedChildVariable);
		var verifyDiscardSurvivor = IsEnabled(
			MacOsTerminalLifecycleProtocol.VerifyDiscardSurvivorVariable);
		var native = MacOsConsoleNative.Instance;
		if (native.GetStandardInputAttributes(
			    out var initialAttributes,
			    out _) < 0)
		{
			return CommandLineExitCodes.RuntimeError;
		}
		if (!VerifyRuntimeConsoleInputMode(native, initialAttributes))
			return CommandLineExitCodes.RuntimeError;

		var sessionCount = int.TryParse(
			Environment.GetEnvironmentVariable(
				MacOsTerminalLifecycleProtocol.SessionCountVariable),
			NumberStyles.None,
			CultureInfo.InvariantCulture,
			out var requestedSessionCount)
			? requestedSessionCount
			: 1;
		if (sessionCount is < 1 or > 2 ||
		    (delayContinueIntoSecondSession && sessionCount != 2))
			return CommandLineExitCodes.UsageError;

		using var continueObserved =
			verifyContinue ||
			delayContinueIntoSecondSession ||
			verifyActiveContinue
				? new ManualResetEventSlim()
				: null;
		using var continueProbe =
			continueObserved is not null
				? PosixSignalRegistration.Create(
					PosixSignal.SIGCONT,
					context =>
					{
						if (context.Cancel)
							continueObserved.Set();
					})
				: null;
		ManualResetEventSlim? delayedContinueEntered = null;
		ManualResetEventSlim? releaseDelayedContinue = null;
		PosixSignalRegistration? delayedContinueBlocker = null;
		Process? isolatedChild = null;
		string? isolatedChildReleasePath = null;
		var outputSuspended = false;
		try
		{
			for (var session = 1; session <= sessionCount; session++)
			{
				using (var input = new MacOsConsoleInput())
				{
					if (session == 2 && delayContinueIntoSecondSession)
					{
						releaseDelayedContinue!.Set();
						if (!continueObserved!.Wait(TimeSpan.FromSeconds(5)))
							return CommandLineExitCodes.RuntimeError;
						delayedContinueBlocker!.Dispose();
						delayedContinueBlocker = null;
					}

					if (session == 1 && verifyActiveContinue)
					{
						if (native.GetStandardInputAttributes(
							    out var activeAttributes,
							    out _) < 0)
						{
							return CommandLineExitCodes.RuntimeError;
						}

						Console.Out.Write(
							MacOsTerminalLifecycleProtocol.NormalKeypadModeSequence);
						Console.Out.Flush();
						if (native.SetStandardInputAttributes(
							    initialAttributes,
							    out _) < 0)
						{
							return CommandLineExitCodes.RuntimeError;
						}

						continueObserved!.Reset();
						if (kill(getpid(), DarwinContinueSignal) != 0 ||
						    !continueObserved.Wait(TimeSpan.FromSeconds(5)) ||
						    native.GetStandardInputAttributes(
							    out var resumedAttributes,
							    out _) < 0 ||
						    !resumedAttributes.Equals(activeAttributes) ||
						    resumedAttributes.HasPendingInput)
						{
							return CommandLineExitCodes.RuntimeError;
						}

						Console.Out.WriteLine(
							MacOsTerminalLifecycleProtocol.ActiveContinueVerified);
						Console.Out.Flush();
					}

					if (session == 1 && spawnIsolatedChild)
					{
						(isolatedChild, isolatedChildReleasePath) =
							StartIsolatedChild();
						if (isolatedChild is null)
							return CommandLineExitCodes.RuntimeError;
						Console.Out.WriteLine(
							MacOsTerminalLifecycleProtocol.IsolatedChildReady);
						Console.Out.Flush();
					}

					Console.Out.WriteLine(
						MacOsTerminalLifecycleProtocol.ReadyPrefix +
						session.ToString(CultureInfo.InvariantCulture) +
						"__");
					Console.Out.Flush();

					while (!input.Peek())
						Thread.Yield();

					using var keys = input.Read().GetEnumerator();
					if (!keys.MoveNext())
						return CommandLineExitCodes.RuntimeError;
					var expectedKey = session == sessionCount ? 'q' : '1';
					if (keys.Current.KeyChar != expectedKey)
						return CommandLineExitCodes.UsageError;

					if (session == sessionCount &&
					    IsEnabled(
						    MacOsTerminalLifecycleProtocol.GatePendingInputVariable) &&
					    !WaitForPendingInputRelease(
						    verifyDiscardSurvivor,
						    out outputSuspended))
					{
						return CommandLineExitCodes.RuntimeError;
					}
				}

				if (outputSuspended)
				{
					if (tcflow(
						    StandardOutputDescriptor,
						    DarwinOutputFlowOn) != 0)
					{
						return CommandLineExitCodes.RuntimeError;
					}

					outputSuspended = false;
					Console.Out.Flush();
				}

				// Queue the continue after session one has restored its exact
				// baseline, then hold the managed callback until session two is
				// active. This reproduces the cross-session dispatch race.
				if (session == 1 && delayContinueIntoSecondSession)
				{
					continueObserved!.Reset();
					delayedContinueEntered = new ManualResetEventSlim();
					releaseDelayedContinue = new ManualResetEventSlim();
					delayedContinueBlocker = PosixSignalRegistration.Create(
						PosixSignal.SIGCONT,
						context =>
						{
							GC.KeepAlive(context);
							delayedContinueEntered.Set();
							_ = releaseDelayedContinue.Wait(
								TimeSpan.FromSeconds(5));
						});
					if (kill(getpid(), DarwinContinueSignal) != 0 ||
					    !delayedContinueEntered.Wait(TimeSpan.FromSeconds(5)))
					{
						return CommandLineExitCodes.RuntimeError;
					}
				}
			}

			if (isolatedChild is not null)
			{
				File.WriteAllText(isolatedChildReleasePath!, string.Empty);
				if (!isolatedChild.WaitForExit(5_000))
					return CommandLineExitCodes.RuntimeError;
				_ = isolatedChild.StandardOutput.ReadToEnd();
				_ = isolatedChild.StandardError.ReadToEnd();
				if (isolatedChild.ExitCode != 0)
					return CommandLineExitCodes.RuntimeError;
			}

			if (!AttributesMatch(native, initialAttributes))
				return CommandLineExitCodes.RuntimeError;

			if (verifyContinue)
			{
				continueObserved!.Reset();
				if (kill(getpid(), DarwinContinueSignal) != 0 ||
				    !continueObserved.Wait(TimeSpan.FromSeconds(5)) ||
				    !AttributesMatch(native, initialAttributes))
				{
					return CommandLineExitCodes.RuntimeError;
				}

				Console.Out.WriteLine(
					MacOsTerminalLifecycleProtocol.ContinueVerified);
				Console.Out.Flush();
			}

			return CommandLineExitCodes.Success;
		}
		finally
		{
			if (outputSuspended)
			{
				_ = tcflow(
					StandardOutputDescriptor,
					DarwinOutputFlowOn);
			}
			releaseDelayedContinue?.Set();
			delayedContinueBlocker?.Dispose();
			delayedContinueEntered?.Dispose();
			releaseDelayedContinue?.Dispose();
			if (isolatedChild is not null)
			{
				TryTerminate(isolatedChild);
				isolatedChild.Dispose();
			}
		}
	}

	private static bool VerifyRuntimeConsoleInputMode(
		MacOsConsoleNative native,
		MacOsConsoleNative.TerminalAttributes initialAttributes)
	{
		var expectedAttributes = initialAttributes.ToConsoleInputMode();
		bool matchesRuntimeMode;
		using (new ConsoleControlCPolicy(SystemConsoleKeySource.Instance))
		{
			matchesRuntimeMode =
				native.GetStandardInputAttributes(
					out var actualAttributes,
					out _) >= 0 &&
				actualAttributes.Equals(expectedAttributes);
		}

		if (native.SetStandardInputAttributes(initialAttributes, out _) < 0)
			return false;

		return matchesRuntimeMode &&
		       AttributesMatch(native, initialAttributes);
	}

	private static bool IsEnabled(string variable) =>
		string.Equals(
			Environment.GetEnvironmentVariable(variable),
			"1",
			StringComparison.Ordinal);

	private static void TryTerminate(Process process)
	{
		if (process.HasExited)
			return;

		process.Kill(entireProcessTree: true);
		_ = process.WaitForExit(5_000);
	}

	private static (Process? Process, string? ReleasePath) StartIsolatedChild()
	{
		var dataRoot = Environment.GetEnvironmentVariable(
			InvocationEnvironment.InternalDataRootVariable);
		if (string.IsNullOrWhiteSpace(dataRoot) ||
		    !Path.IsPathFullyQualified(dataRoot))
		{
			return (null, null);
		}

		Directory.CreateDirectory(dataRoot);
		var releasePath = Path.Combine(dataRoot, "release-macos-child");
		// Reuse a production TUI-reachable noninteractive child contract. The
		// executable is replaced by a deterministic test gate, while the managed
		// stream ownership flags remain exactly those used by doctor.
		var startInfo = DoctorCommandHandler.CreateGitVersionStartInfo();
		startInfo.FileName = "/bin/sh";
		startInfo.ArgumentList.Clear();
		startInfo.ArgumentList.Add("-c");
		startInfo.ArgumentList.Add(
			"while [ ! -e \"$1\" ]; do sleep 0.01; done");
		startInfo.ArgumentList.Add("devprojex-terminal-child");
		startInfo.ArgumentList.Add(releasePath);
		var process = new Process { StartInfo = startInfo };
		if (!process.Start())
		{
			process.Dispose();
			return (null, null);
		}
		process.StandardInput.Close();

		return (process, releasePath);
	}

	private static bool WaitForPendingInputRelease(
		bool queueDiscardSurvivor,
		out bool outputSuspended)
	{
		outputSuspended = false;
		var dataRoot = Environment.GetEnvironmentVariable(
			InvocationEnvironment.InternalDataRootVariable);
		if (string.IsNullOrWhiteSpace(dataRoot) ||
		    !Path.IsPathFullyQualified(dataRoot))
		{
			return false;
		}

		var releasePath = Path.Combine(
			dataRoot,
			MacOsTerminalLifecycleProtocol.PendingInputReleaseFileName);
		Console.Out.WriteLine(MacOsTerminalLifecycleProtocol.PendingInputReady);
		Console.Out.Flush();

		var timeout = Stopwatch.StartNew();
		while (!File.Exists(releasePath))
		{
			if (timeout.Elapsed >= TimeSpan.FromSeconds(30))
				return false;

			// This wait exists only in the native PTY test host. stdin remains
			// untouched so the test can place bytes in Darwin's kernel raw queue.
			Thread.Sleep(10);
		}

		if (queueDiscardSurvivor)
		{
			if (tcflow(
				    StandardOutputDescriptor,
				    DarwinOutputFlowOff) != 0)
			{
				return false;
			}

			outputSuspended = true;
			Console.Out.WriteLine(
				MacOsTerminalLifecycleProtocol.DiscardSurvivor);
			Console.Out.Flush();
		}

		return true;
	}

	private static bool AttributesMatch(
		MacOsConsoleNative native,
		MacOsConsoleNative.TerminalAttributes expected) =>
		native.GetStandardInputAttributes(out var actual, out _) >= 0 &&
		actual.Equals(expected) &&
		!actual.HasPendingInput;

	private const int DarwinContinueSignal = 19;
	private const int StandardOutputDescriptor = 1;
	private const int DarwinOutputFlowOff = 1;
	private const int DarwinOutputFlowOn = 2;

	[DllImport("libc", EntryPoint = "getpid")]
	private static extern int getpid();

	[DllImport("libc", EntryPoint = "kill", SetLastError = true)]
	private static extern int kill(int processId, int signal);

	[DllImport("libc", EntryPoint = "tcflow", SetLastError = true)]
	private static extern int tcflow(int descriptor, int action);

	private static int RunSignalCheckpointProtocol()
	{
		using var cancellation = TerminalCancellationCoordinator.Register();
		Console.Out.WriteLine(TerminalSignalCheckpointProtocol.Ready);
		Console.Out.Flush();

		cancellation.Token.WaitHandle.WaitOne();
		Console.Out.WriteLine(
			TerminalSignalCheckpointProtocol.CancellationObserved);
		Console.Out.Flush();

		// The second interrupt must take the native termination path only after
		// the first cancellation has been observed by the process.
		using var nativeTerminationGate = new ManualResetEvent(false);
		nativeTerminationGate.WaitOne();
		return CommandLineExitCodes.Canceled;
	}
}

internal sealed class FileTerminalOperationObserver : ITerminalOperationObserver
{
	private readonly string _directory;
	private readonly Queue<int> _pendingPercentages;
	private readonly HashSet<TerminalOperationPhase> _pendingPhases;

	private FileTerminalOperationObserver(
		string directory,
		IEnumerable<int> percentages,
		IEnumerable<TerminalOperationPhase> phases)
	{
		_directory = directory;
		_pendingPercentages = new Queue<int>(percentages);
		_pendingPhases = phases.ToHashSet();
	}

	public static FileTerminalOperationObserver Create(string dataRoot)
	{
		var percentages = ParsePercentages(
			Environment.GetEnvironmentVariable(
				TerminalProgressCheckpointProtocol.CheckpointsVariable));
		var phases = ParsePhases(
			Environment.GetEnvironmentVariable(
				TerminalProgressCheckpointProtocol.PhasesVariable));
		if (percentages.Length == 0 && phases.Length == 0)
		{
			throw new InvalidOperationException(
				"The progress test host requires at least one checkpoint.");
		}

		var directory = Path.Combine(
			dataRoot,
			TerminalProgressCheckpointProtocol.DirectoryName);
		Directory.CreateDirectory(directory);
		return new FileTerminalOperationObserver(
			directory,
			percentages,
			phases);
	}

	public ValueTask ObservePhaseAsync(
		TerminalOperationPhase phase,
		CancellationToken cancellationToken)
	{
		if (!_pendingPhases.Remove(phase))
			return ValueTask.CompletedTask;

		return new ValueTask(Task.Run(
			() => PauseAt(ToToken(phase), cancellationToken),
			CancellationToken.None));
	}

	public void ObserveProgress(
		ProjectCopyExportProgress progress,
		CancellationToken cancellationToken)
	{
		if (_pendingPercentages.Count == 0 || progress.TotalEntryCount <= 0)
			return;

		var actualPercentage = Math.Clamp(
			(int)Math.Floor(
				progress.ProcessedEntryCount * 100d /
				progress.TotalEntryCount),
			0,
			100);
		while (_pendingPercentages.TryPeek(out var requestedPercentage) &&
		       actualPercentage >= requestedPercentage)
		{
			_pendingPercentages.Dequeue();
			PauseAt(
				requestedPercentage.ToString(CultureInfo.InvariantCulture),
				cancellationToken);
		}
	}

	private void PauseAt(
		string checkpoint,
		CancellationToken cancellationToken)
	{
		var reachedPath = Path.Combine(
			_directory,
			TerminalProgressCheckpointProtocol.GetReachedFileName(checkpoint));
		var releasePath = Path.Combine(
			_directory,
			TerminalProgressCheckpointProtocol.GetReleaseFileName(checkpoint));
		File.WriteAllText(
			reachedPath,
			checkpoint,
			new UTF8Encoding(false));
		if (File.Exists(releasePath))
			return;

		using var changed = new AutoResetEvent(initialState: false);
		using var watcher = new FileSystemWatcher(_directory)
		{
			EnableRaisingEvents = true,
			NotifyFilter = NotifyFilters.FileName
		};
		FileSystemEventHandler release = (_, args) =>
		{
			if (string.Equals(
				    args.FullPath,
				    releasePath,
				    PathUtility.DefaultComparison))
			{
				changed.Set();
			}
		};
		watcher.Created += release;
		watcher.Renamed += (_, args) =>
		{
			if (string.Equals(
				    args.FullPath,
				    releasePath,
				    PathUtility.DefaultComparison))
			{
				changed.Set();
			}
		};

		while (!File.Exists(releasePath))
		{
			var signaled = WaitHandle.WaitAny(
				[changed, cancellationToken.WaitHandle]);
			if (signaled == 1)
				cancellationToken.ThrowIfCancellationRequested();
		}
	}

	private static int[] ParsePercentages(string? value) =>
		(value ?? string.Empty)
			.Split(
				',',
				StringSplitOptions.RemoveEmptyEntries |
				StringSplitOptions.TrimEntries)
			.Select(static item => int.TryParse(
				item,
				NumberStyles.None,
				CultureInfo.InvariantCulture,
				out var percentage)
				? percentage
				: -1)
			.Where(static percentage => percentage is > 0 and < 100)
			.Distinct()
			.Order()
			.ToArray();

	private static TerminalOperationPhase[] ParsePhases(string? value) =>
		(value ?? string.Empty)
			.Split(
				',',
				StringSplitOptions.RemoveEmptyEntries |
				StringSplitOptions.TrimEntries)
			.Select(TryParsePhase)
			.Where(static phase => phase is not null)
			.Select(static phase => phase!.Value)
			.Distinct()
			.ToArray();

	private static TerminalOperationPhase? TryParsePhase(string value) =>
		value switch
		{
			"clone-connecting" => TerminalOperationPhase.CloneConnecting,
			"project-loading" => TerminalOperationPhase.ProjectLoading,
			"preparing" => TerminalOperationPhase.Preparing,
			"writing-context" => TerminalOperationPhase.WritingContext,
			_ => null
		};

	private static string ToToken(TerminalOperationPhase phase) =>
		phase switch
		{
			TerminalOperationPhase.CloneConnecting => "clone-connecting",
			TerminalOperationPhase.ProjectLoading => "project-loading",
			TerminalOperationPhase.Preparing => "preparing",
			TerminalOperationPhase.WritingContext => "writing-context",
			_ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
		};
}
