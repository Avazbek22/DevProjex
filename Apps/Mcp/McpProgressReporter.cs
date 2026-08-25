using System.Diagnostics;

namespace DevProjex.Mcp;

internal sealed class McpProgressReporter(IProgress<ProgressNotificationValue> destination)
{
	private const double Total = 100d;
	private const double MinimumIntermediateStep = 5d;
	private static readonly TimeSpan MinimumIntermediateInterval = TimeSpan.FromMilliseconds(250);
	private readonly object _sync = new();
	private double _lastProgress;
	private long _lastReportTimestamp;

	public void Milestone(double progress, string message) =>
		Report(progress, message, force: true);

	public IProgress<ProjectCopyExportProgress> Measure(
		string phase,
		double start,
		double end) =>
		new SynchronousProgress<ProjectCopyExportProgress>(value =>
		{
			var total = Math.Max(0, value.TotalEntryCount);
			var processed = Math.Clamp(value.ProcessedEntryCount, 0, total);
			var fraction = total == 0
				? 1d
				: processed / (double)total;
			var mapped = start + Math.Clamp(fraction, 0d, 1d) * (end - start);
			Report(mapped, $"{phase} {processed}/{total}", force: false);
		});

	private void Report(double progress, string message, bool force)
	{
		lock (_sync)
		{
			var normalized = Math.Clamp(progress, 0d, Total);
			if (normalized <= _lastProgress)
				return;

			var now = Stopwatch.GetTimestamp();
			if (!force &&
			    (normalized - _lastProgress < MinimumIntermediateStep ||
			     _lastReportTimestamp != 0 &&
			     Stopwatch.GetElapsedTime(_lastReportTimestamp, now) < MinimumIntermediateInterval))
			{
				return;
			}

			_lastProgress = normalized;
			_lastReportTimestamp = now;
			destination.Report(new ProgressNotificationValue
			{
				Progress = (float)normalized,
				Total = (float)Total,
				Message = message
			});
		}
	}

	private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
	{
		public void Report(T value) => report(value);
	}
}
