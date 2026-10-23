using System.Diagnostics;

namespace DevProjex.Tests.Unit;

public sealed class GitTestRequirementTests
{
	private const string Reason = "Git is not available in this test environment.";

	[Theory]
	[InlineData("true", true)]
	[InlineData("TRUE", true)]
	[InlineData("1", true)]
	[InlineData(null, false)]
	[InlineData("", false)]
	[InlineData("false", false)]
	[InlineData("0", false)]
	public void ContinuousIntegrationFollowsTheStandardVariable(string? value, bool expected) =>
		Assert.Equal(expected, GitTestRequirement.IsContinuousIntegration(value));

	[Fact]
	public void UnavailableGitFailsOnACiRunnerAndListsTheProcesses()
	{
		var exception = Assert.ThrowsAny<Exception>(() =>
			GitTestRequirement.ReportUnavailable(Reason, "true", static () => "process list"));

		Assert.IsType<Xunit.Sdk.FailException>(exception);
		Assert.StartsWith(Reason, exception.Message, StringComparison.Ordinal);
		Assert.Contains("runner failure", exception.Message, StringComparison.Ordinal);
		Assert.EndsWith("process list", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void UnavailableGitSkipsOnADeveloperMachineWithoutListingProcesses()
	{
		var listed = false;
		Exception? exception = null;

		// Caught by hand: a skip that escapes the delegate would skip this test instead of being observed.
		try
		{
			GitTestRequirement.ReportUnavailable(Reason, null, () =>
			{
				listed = true;
				return "process list";
			});
		}
		catch (Exception caught)
		{
			exception = caught;
		}

		Assert.IsType<Xunit.Sdk.SkipException>(exception);
		Assert.Contains(Reason, exception.Message, StringComparison.Ordinal);
		Assert.False(listed);
	}

	[Fact]
	public void ProcessListShowsAProcessStartedJustBefore()
	{
		using var child = StartSleeper();
		try
		{
			var description = GitTestRequirement.DescribeProcesses();

			Assert.StartsWith("Processes at ", description, StringComparison.Ordinal);
			Assert.Contains($"[{child.Id}]", description, StringComparison.Ordinal);
			Assert.Contains("By name: ", description, StringComparison.Ordinal);
		}
		finally
		{
			Stop(child);
		}
	}

	[Fact]
	public async Task AStalledProcessIsDescribedWhileItRunsAndThenStopped()
	{
		using var child = StartSleeper();
		var trace = GitTestRequirement.CreateTraceFile();
		try
		{
			var exception = await Assert.ThrowsAsync<TimeoutException>(() => GitTestRequirement.WaitForExitAsync(
				child,
				"sleeper",
				trace,
				TimeSpan.FromMilliseconds(300),
				TestContext.Current.CancellationToken));

			Assert.StartsWith($"sleeper (process {child.Id}) did not exit within", exception.Message, StringComparison.Ordinal);
			// Listed means it was described before it was stopped.
			Assert.Contains($"[{child.Id}]", exception.Message, StringComparison.Ordinal);
			Assert.Contains("Git wrote no trace", exception.Message, StringComparison.Ordinal);
			Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
			Assert.True(child.HasExited);
		}
		finally
		{
			Stop(child);
			GitTestRequirement.DeleteTraceFile(trace);
		}
	}

	[Fact]
	public async Task TheTestsOwnCancellationIsNotReportedAsATimeout()
	{
		using var child = StartSleeper();
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		try
		{
			var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GitTestRequirement.WaitForExitAsync(
				child,
				"sleeper",
				null,
				TimeSpan.FromSeconds(30),
				cancellation.Token));

			Assert.IsNotType<TimeoutException>(exception);
		}
		finally
		{
			Stop(child);
		}
	}

	[Fact]
	public void TraceShowsItsLastLinesOrThatGitStoppedBeforeTracing()
	{
		var path = GitTestRequirement.CreateTraceFile();
		try
		{
			Assert.Contains("wrote no trace", GitTestRequirement.ReadTrace(path), StringComparison.Ordinal);

			File.WriteAllText(path, string.Empty);
			Assert.Contains("trace is empty", GitTestRequirement.ReadTrace(path), StringComparison.Ordinal);

			File.WriteAllLines(path, Enumerable.Range(1, 30).Select(static line => $"line {line}"));
			var lines = GitTestRequirement.ReadTrace(path).Split(Environment.NewLine);
			Assert.Equal("Last Git trace lines:", lines[0]);
			Assert.Equal(Enumerable.Range(11, 20).Select(static line => $"line {line}"), lines.Skip(1));
		}
		finally
		{
			GitTestRequirement.DeleteTraceFile(path);
		}
	}

	private static Process StartSleeper() =>
		Process.Start(new ProcessStartInfo(
			OperatingSystem.IsWindows() ? "cmd.exe" : "sleep",
			OperatingSystem.IsWindows() ? "/c ping -n 6 127.0.0.1 >nul" : "5")
		{
			UseShellExecute = false,
			CreateNoWindow = true
		})!;

	private static void Stop(Process process)
	{
		if (!process.HasExited)
			process.Kill(entireProcessTree: true);
		process.WaitForExit(10_000);
	}

	[Fact]
	public void TimeoutKeepsTheMessageAndTheCauseAndAddsTheProcessList()
	{
		var cause = new OperationCanceledException();

		var exception = GitTestRequirement.Timeout("git status did not exit.", cause);

		Assert.StartsWith("git status did not exit.", exception.Message, StringComparison.Ordinal);
		Assert.Contains("Processes at ", exception.Message, StringComparison.Ordinal);
		Assert.Same(cause, exception.InnerException);
	}
}
