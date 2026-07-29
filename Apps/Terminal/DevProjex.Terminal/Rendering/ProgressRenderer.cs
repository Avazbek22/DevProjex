using System.Diagnostics;
using System.Threading.Channels;
using DevProjex.Terminal.CommandLine;
using Spectre.Console;

namespace DevProjex.Terminal.Rendering;

public sealed class ProgressRenderer(
	ITerminalEnvironment environment,
	TerminalOutputOptions options,
	LocalizationService localization)
{
	public async Task<T> RunProjectExportAsync<T>(
		Func<IProgress<ProjectCopyExportProgress>?, Task<T>> operation)
	{
		if (ShouldRenderStaticProgress())
			return await RunPlainProjectExportAsync(operation).ConfigureAwait(false);

		var capabilities = TerminalCapabilities.Resolve(environment, options, forStandardError: true);
		if (!capabilities.UseInteractiveProgress)
			return await operation(null).ConfigureAwait(false);
		if (!capabilities.UseAnsi)
			return await RunPlainProjectExportAsync(operation).ConfigureAwait(false);

		var channel = Channel.CreateBounded<ProjectCopyExportProgress>(
			new BoundedChannelOptions(32)
			{
				SingleReader = true,
				SingleWriter = false,
				AllowSynchronousContinuations = false,
				FullMode = BoundedChannelFullMode.DropOldest
			});
		var progress = new CallbackProgress<ProjectCopyExportProgress>(
			value => channel.Writer.TryWrite(value));
		var operationTask = operation(progress);
		var console = AnsiConsoleFactory.Create(environment.Error, capabilities);

		await console.Progress()
			.AutoClear(false)
			.HideCompleted(false)
			.Columns(
				new TaskDescriptionColumn(),
				new ProgressBarColumn(),
				new PercentageColumn(),
				new ElapsedTimeColumn())
			.StartAsync(async context =>
			{
				var task = context.AddTask(
					localization["Terminal.Progress.PreparingProjectExport"],
					maxValue: 1);
				task.IsIndeterminate = true;
				while (!operationTask.IsCompleted)
				{
					while (channel.Reader.TryRead(out var update))
						ApplyUpdate(task, update);

					await Task.WhenAny(operationTask, Task.Delay(40)).ConfigureAwait(false);
				}

				while (channel.Reader.TryRead(out var update))
					ApplyUpdate(task, update);
				task.IsIndeterminate = false;
				task.Value = task.MaxValue;
			}).ConfigureAwait(false);

		channel.Writer.TryComplete();
		return await operationTask.ConfigureAwait(false);
	}

	private bool ShouldRenderStaticProgress() =>
		options.Progress == TerminalProgressMode.Always &&
		options.Verbosity is not (
			TerminalVerbosity.Quiet or
			TerminalVerbosity.Minimal) &&
		(options.Plain ||
		 environment.IsTermDumb ||
		 !environment.IsErrorInteractive);

	private async Task<T> RunPlainProjectExportAsync<T>(
		Func<IProgress<ProjectCopyExportProgress>?, Task<T>> operation)
	{
		var state = new LatestProgressState();
		var progress = new CallbackProgress<ProjectCopyExportProgress>(state.Update);
		var stopwatch = Stopwatch.StartNew();
		var operationTask = operation(progress);
		long renderedRevision = 0;
		var lastRender = TimeSpan.MinValue;

		while (!operationTask.IsCompleted)
		{
			await Task.WhenAny(operationTask, Task.Delay(100)).ConfigureAwait(false);
			var snapshot = state.Read();
			if (snapshot.Revision == renderedRevision ||
			    renderedRevision > 0 && stopwatch.Elapsed - lastRender < TimeSpan.FromMilliseconds(250))
			{
				continue;
			}

			WritePlainProgress(snapshot.Progress, stopwatch.Elapsed);
			renderedRevision = snapshot.Revision;
			lastRender = stopwatch.Elapsed;
		}

		var finalSnapshot = state.Read();
		if (finalSnapshot.Revision > 0 && finalSnapshot.Revision != renderedRevision)
			WritePlainProgress(finalSnapshot.Progress, stopwatch.Elapsed);
		return await operationTask.ConfigureAwait(false);
	}

	private void ApplyUpdate(
		ProgressTask task,
		ProjectCopyExportProgress update)
	{
		var total = Math.Max(1, update.TotalEntryCount);
		task.IsIndeterminate = false;
		task.MaxValue = total;
		task.Value = Math.Clamp(update.ProcessedEntryCount, 0, total);
		task.Description = localization.Format(
			"Terminal.Progress.ExportProject",
			update.ProcessedEntryCount,
			update.TotalEntryCount,
			FormatBytes(update.BytesWritten));
	}

	private void WritePlainProgress(
		ProjectCopyExportProgress update,
		TimeSpan elapsed)
	{
		var total = Math.Max(1, update.TotalEntryCount);
		var processed = Math.Clamp(update.ProcessedEntryCount, 0, total);
		var percentage = (int)Math.Floor(processed * 100d / total);
		environment.Error.WriteLine(
			$"{localization.Format(
				"Terminal.Progress.ExportProject",
				processed,
				update.TotalEntryCount,
				FormatBytes(update.BytesWritten))} | " +
			$"{percentage}% | {elapsed:m\\:ss}");
	}

	private static string FormatBytes(long bytes)
	{
		string[] units = ["B", "KB", "MB", "GB", "TB"];
		var value = Math.Max(0, bytes);
		var unit = 0;
		var display = (double)value;
		while (display >= 1024 && unit < units.Length - 1)
		{
			display /= 1024;
			unit++;
		}

		return unit == 0
			? $"{value} {units[unit]}"
			: $"{display:0.##} {units[unit]}";
	}

	private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
	{
		public void Report(T value) => callback(value);
	}

	private sealed class LatestProgressState
	{
		private readonly object _gate = new();
		private ProjectCopyExportProgress _progress = new(0, 0, 0, 0);
		private long _revision;

		public void Update(ProjectCopyExportProgress progress)
		{
			lock (_gate)
			{
				_progress = progress;
				_revision++;
			}
		}

		public (ProjectCopyExportProgress Progress, long Revision) Read()
		{
			lock (_gate)
				return (_progress, _revision);
		}
	}
}
