using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace DevProjex.Tests.Shared.Git;

/// <summary>
/// Decides what a test does when Git cannot be used. A developer machine without Git skips the test. Every CI
/// runner has Git, so there a missing or stalled Git means the runner is broken: the test fails and says what
/// was running instead of turning into a skip that leaves the job green.
/// </summary>
internal static class GitTestRequirement
{
	internal const string ContinuousIntegrationVariable = "CI";
	public const string TraceVariable = "GIT_TRACE2_PERF";
	private const int TraceLineLimit = 20;
	private const int RecentProcessLimit = 40;
	private const int ProcessNameLimit = 25;
	private const int GitProcessLimit = 6;
	private const int ModuleLimit = 60;

	/// <summary>Skips the test on a developer machine and fails it on a CI runner.</summary>
	[DoesNotReturn]
	public static void ReportUnavailable(string reason) =>
		ReportUnavailable(
			reason,
			Environment.GetEnvironmentVariable(ContinuousIntegrationVariable),
			DescribeProcesses);

	[DoesNotReturn]
	internal static void ReportUnavailable(
		string reason,
		string? continuousIntegration,
		Func<string> describeProcesses)
	{
		if (IsContinuousIntegration(continuousIntegration))
		{
			Xunit.Assert.Fail(
				$"{reason} Every CI runner has Git, so this is a runner failure rather than a test that does " +
				$"not apply.{Environment.NewLine}{describeProcesses()}");
		}
		Xunit.Assert.Skip(reason);
	}

	internal static bool IsContinuousIntegration(string? value) =>
		string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(value, "1", StringComparison.Ordinal);

	/// <summary>The exception for a Git command that did not exit in time, with the processes running then.</summary>
	public static TimeoutException Timeout(string message, Exception? inner = null) =>
		new($"{message}{Environment.NewLine}{DescribeProcesses()}", inner);

	/// <summary>A file for <see cref="TraceVariable"/>, where Git records how far it got.</summary>
	public static string CreateTraceFile() =>
		Path.Combine(Path.GetTempPath(), $"devprojex-git-trace-{Guid.NewGuid():N}.log");

	public static void DeleteTraceFile(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// A trace file left in the temporary folder changes nothing for the next run.
		}
	}

	/// <summary>
	/// Waits for a Git process. On a timeout the processes are described while the stalled Git still runs,
	/// together with its trace, and only then is it stopped and a <see cref="TimeoutException"/> thrown.
	/// </summary>
	public static async Task WaitForExitAsync(
		Process process,
		string command,
		string? traceFile,
		TimeSpan timeout,
		CancellationToken cancellationToken)
	{
		using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		limit.CancelAfter(timeout);
		try
		{
			await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
		{
			var processes = DescribeProcesses();
			var trace = traceFile is null ? string.Empty : Environment.NewLine + ReadTrace(traceFile);
			Stop(process);
			throw new TimeoutException(
				$"{command} (process {process.Id}) did not exit within {timeout.TotalSeconds:N0} seconds." +
				$"{Environment.NewLine}{processes}{trace}",
				exception);
		}
	}

	internal static string ReadTrace(string path)
	{
		try
		{
			if (!File.Exists(path))
				return "Git wrote no trace, so it stopped before its trace started.";
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using var reader = new StreamReader(stream);
			var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
			return lines.Length == 0
				? "Git's trace is empty, so it stopped before its trace started."
				: $"Last Git trace lines:{Environment.NewLine}{string.Join(Environment.NewLine, lines.TakeLast(TraceLineLimit).Select(static line => line.TrimEnd('\r')))}";
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return $"Git's trace is unreadable ({exception.GetType().Name}).";
		}
	}

	private static void Stop(Process process)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
			process.WaitForExit(5_000);
		}
		catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
		{
			// The process is already gone or cannot be stopped; the timeout is reported either way.
		}
	}

	/// <summary>
	/// Lists the processes started most recently and the busiest process names. It reads the process table in
	/// this process instead of starting a tool, because a stalled runner may not start new processes at all.
	/// </summary>
	public static string DescribeProcesses()
	{
		var now = DateTime.Now;
		var processes = Process.GetProcesses();
		try
		{
			var described = processes
				.Select(static process => (Name: ReadName(process), Id: process.Id, Started: ReadStartTime(process)))
				.ToArray();
			var builder = new StringBuilder();
			builder.AppendLine(
				CultureInfo.InvariantCulture,
				$"Processes at {DateTime.UtcNow:O}: {described.Length} running. Most recently started:");
			foreach (var process in described
						 .Where(static process => process.Started is not null)
						 .OrderByDescending(static process => process.Started)
						 .Take(RecentProcessLimit))
			{
				builder.AppendLine(
					CultureInfo.InvariantCulture,
					$"  {process.Name} [{process.Id}] started {(now - process.Started!.Value).TotalSeconds:N0} s ago");
			}
			builder.Append("By name: ");
			builder.AppendJoin(
				", ",
				described
					.GroupBy(static process => process.Name, StringComparer.Ordinal)
					.OrderByDescending(static group => group.Count())
					.ThenBy(static group => group.Key, StringComparer.Ordinal)
					.Take(ProcessNameLimit)
					.Select(static group => $"{group.Key} x{group.Count()}"));
			// A Git that never exits is described in detail: what its threads wait on, and which modules it
			// had loaded, show whether it stopped in the loader, in a library, or on another process.
			foreach (var process in processes
						 .Where(static process => string.Equals(ReadName(process), "git", StringComparison.OrdinalIgnoreCase))
						 .Take(GitProcessLimit))
			{
				builder.AppendLine();
				builder.Append(CultureInfo.InvariantCulture, $"git [{process.Id}]: {DescribeThreads(process)}; modules: {DescribeModules(process)}");
			}
			return builder.ToString();
		}
		finally
		{
			foreach (var process in processes)
				process.Dispose();
		}
	}

	private static string DescribeThreads(Process process)
	{
		try
		{
			var threads = process.Threads.Cast<ProcessThread>().ToArray();
			var states = threads
				.Select(static thread =>
				{
					try
					{
						return thread.ThreadState == System.Diagnostics.ThreadState.Wait
							? $"Wait/{thread.WaitReason}"
							: thread.ThreadState.ToString();
					}
					catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or Win32Exception)
					{
						return "unknown";
					}
				})
				.GroupBy(static state => state, StringComparer.Ordinal)
				.Select(static group => $"{group.Key} x{group.Count()}");
			return $"{threads.Length} thread(s) {string.Join(", ", states)}";
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or Win32Exception)
		{
			return $"threads unreadable ({exception.GetType().Name})";
		}
	}

	private static string DescribeModules(Process process)
	{
		try
		{
			return string.Join(
				", ",
				process.Modules.Cast<ProcessModule>()
					.Select(static module => module.ModuleName)
					.Take(ModuleLimit));
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or Win32Exception)
		{
			return $"unreadable ({exception.GetType().Name})";
		}
	}

	private static string ReadName(Process process)
	{
		try
		{
			return process.ProcessName;
		}
		catch (InvalidOperationException)
		{
			return "(exited)";
		}
	}

	private static DateTime? ReadStartTime(Process process)
	{
		try
		{
			return process.StartTime;
		}
		catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
		{
			return null;
		}
	}
}
