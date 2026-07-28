using System.Globalization;
using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalProgressTestCheckpoint
{
	internal const string CheckpointsVariable = "DEVPROJEX_INTERNAL_TUI_PROGRESS_CHECKPOINTS";
	internal const string PhasesVariable = "DEVPROJEX_INTERNAL_TUI_PROGRESS_PHASES";

	private readonly string _directory;
	private readonly Queue<int> _pendingPercentages;
	private readonly HashSet<string> _pendingPhases;

	private TerminalProgressTestCheckpoint(
		string directory,
		IEnumerable<int> percentages,
		IEnumerable<string> phases)
	{
		_directory = directory;
		_pendingPercentages = new Queue<int>(percentages);
		_pendingPhases = phases.ToHashSet(StringComparer.Ordinal);
	}

	public static TerminalProgressTestCheckpoint? TryCreate()
	{
		var value = Environment.GetEnvironmentVariable(CheckpointsVariable);
		var phaseValue = Environment.GetEnvironmentVariable(PhasesVariable);
		var dataRoot = Environment.GetEnvironmentVariable(
			InvocationEnvironment.InternalDataRootVariable);
		if ((string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(phaseValue)) ||
		    string.IsNullOrWhiteSpace(dataRoot))
			return null;

		var percentages = (value ?? string.Empty)
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
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
		var phases = (phaseValue ?? string.Empty)
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Where(static phase => phase.All(character =>
				character is >= 'a' and <= 'z' or '-'))
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		if (percentages.Length == 0 && phases.Length == 0)
			return null;

		try
		{
			var directory = Path.Combine(dataRoot, "tui-progress-checkpoints");
			Directory.CreateDirectory(directory);
			return new TerminalProgressTestCheckpoint(directory, percentages, phases);
		}
		catch
		{
			return null;
		}
	}

	public Task PausePhaseAsync(string phase, CancellationToken cancellationToken)
	{
		if (!_pendingPhases.Remove(phase))
			return Task.CompletedTask;

		return Task.Run(
			() => PauseAt(phase, cancellationToken),
			CancellationToken.None);
	}

	public void PauseIfReached(
		ProjectCopyExportProgress progress,
		CancellationToken cancellationToken)
	{
		if (_pendingPercentages.Count == 0 || progress.TotalEntryCount <= 0)
			return;

		var actualPercentage = Math.Clamp(
			(int)Math.Floor(progress.ProcessedEntryCount * 100d / progress.TotalEntryCount),
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

	private void PauseAt(string checkpoint, CancellationToken cancellationToken)
	{
		var reachedPath = Path.Combine(_directory, $"reached-{checkpoint}");
		var releasePath = Path.Combine(_directory, $"release-{checkpoint}");
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
			var signaled = WaitHandle.WaitAny([changed, cancellationToken.WaitHandle]);
			if (signaled == 1)
				cancellationToken.ThrowIfCancellationRequested();
		}
	}
}
