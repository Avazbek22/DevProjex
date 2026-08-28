using System.Diagnostics;
using System.Globalization;
using System.Text;
using DevProjex.Application.Preview;
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
		if (args is ["--pipe-flood"])
		{
			Console.Error.Write(new string('x', 1024 * 1024));
			Console.Out.Write("completed");
			return CommandLineExitCodes.Success;
		}
		if (args is ["--stdin-eof"])
		{
			_ = Console.In.ReadToEnd();
			Console.Out.Write("eof");
			return CommandLineExitCodes.Success;
		}
		if (args is ["--hold-process-tree", var parentLockPath, var parentReadyPath])
		{
			return HoldProcessTree(parentLockPath, parentReadyPath);
		}
		if (args is ["--hold-lock", var childLockPath, var childReadyPath])
		{
			return HoldLock(childLockPath, childReadyPath);
		}
		if (args is ["--file-backed-export", var sourcePath, var destinationPath,
		    var readyPath, var cancelPath, var outcomePath])
		{
			RunFileBackedExportAsync(
					sourcePath,
					destinationPath,
					readyPath,
					cancelPath,
					outcomePath)
				.GetAwaiter()
				.GetResult();
			return CommandLineExitCodes.Success;
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

	private static int HoldProcessTree(string lockPath, string readyPath)
	{
		using var child = new Process
		{
			StartInfo = CreateSelfStartInfo("--hold-lock", lockPath, readyPath)
		};
		if (!child.Start())
			return CommandLineExitCodes.RuntimeError;

		child.WaitForExit();
		return child.ExitCode;
	}

	private static int HoldLock(string lockPath, string readyPath)
	{
		using var heldHandle = new FileStream(
			lockPath,
			FileMode.OpenOrCreate,
			FileAccess.ReadWrite,
			FileShare.None);
		File.WriteAllText(readyPath, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

		using var exitSignal = new ManualResetEventSlim(initialState: false);
		exitSignal.Wait();
		return CommandLineExitCodes.Success;
	}

	private static ProcessStartInfo CreateSelfStartInfo(params string[] arguments)
	{
		var executable = Environment.ProcessPath ??
		                 throw new InvalidOperationException("The test host executable path is unavailable.");
		var startInfo = new ProcessStartInfo
		{
			FileName = executable,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		return startInfo;
	}

	private static async Task RunFileBackedExportAsync(
		string sourcePath,
		string destinationPath,
		string readyPath,
		string cancelPath,
		string outcomePath)
	{
		var sourceLength = new FileInfo(sourcePath).Length;
		using var document = new FileBackedPreviewTextDocument(
			sourcePath,
			[0],
			sourceLength,
			maxLineLength: 0,
			characterCount: sourceLength);
		await using var destinationFile = new FileStream(
			destinationPath,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.Read,
			bufferSize: 64 * 1024,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		using var cancellation = new CancellationTokenSource();
		await using var destination = new DelayedWriteStream(
			destinationFile,
			cancelPath,
			cancellation);
		var pendingReadyPath = readyPath + ".pending";
		await File.WriteAllTextAsync(
			pendingReadyPath,
			Environment.WorkingSet.ToString(CultureInfo.InvariantCulture));
		File.Move(pendingReadyPath, readyPath);

		try
		{
			await new TextFileExportService().WriteAsync(
				destination,
				document,
				cancellation.Token);
			await File.WriteAllTextAsync(outcomePath, "completed");
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
		{
			await File.WriteAllTextAsync(outcomePath, "canceled");
		}
	}

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
	private readonly object _observationSync = new();
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
		var token = ToToken(phase);
		RecordObservation(token);
		if (!_pendingPhases.Remove(phase))
			return ValueTask.CompletedTask;

		return new ValueTask(Task.Run(
			() => PauseAt(token, cancellationToken),
			CancellationToken.None));
	}

	private void RecordObservation(string checkpoint)
	{
		lock (_observationSync)
		{
			File.AppendAllText(
				Path.Combine(
					_directory,
					TerminalProgressCheckpointProtocol.GetObservedFileName(checkpoint)),
				checkpoint + Environment.NewLine,
				new UTF8Encoding(false));
		}
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
			"background-refresh" => TerminalOperationPhase.BackgroundRefresh,
			"preparing" => TerminalOperationPhase.Preparing,
			"writing-context" => TerminalOperationPhase.WritingContext,
			_ => null
		};

	private static string ToToken(TerminalOperationPhase phase) =>
		phase switch
		{
			TerminalOperationPhase.CloneConnecting => "clone-connecting",
			TerminalOperationPhase.ProjectLoading => "project-loading",
			TerminalOperationPhase.BackgroundRefresh => "background-refresh",
			TerminalOperationPhase.Preparing => "preparing",
			TerminalOperationPhase.WritingContext => "writing-context",
			_ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
		};
}
