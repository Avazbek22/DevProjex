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
	private const int RecentProcessLimit = 40;
	private const int ProcessNameLimit = 25;

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
			return builder.ToString();
		}
		finally
		{
			foreach (var process in processes)
				process.Dispose();
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
