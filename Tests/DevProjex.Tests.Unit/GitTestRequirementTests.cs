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
		using var child = Process.Start(new ProcessStartInfo(
			OperatingSystem.IsWindows() ? "cmd.exe" : "sleep",
			OperatingSystem.IsWindows() ? "/c ping -n 6 127.0.0.1 >nul" : "5")
		{
			UseShellExecute = false,
			CreateNoWindow = true
		})!;
		try
		{
			var description = GitTestRequirement.DescribeProcesses();

			Assert.StartsWith("Processes at ", description, StringComparison.Ordinal);
			Assert.Contains($"[{child.Id}]", description, StringComparison.Ordinal);
			Assert.Contains("By name: ", description, StringComparison.Ordinal);
		}
		finally
		{
			if (!child.HasExited)
				child.Kill(entireProcessTree: true);
			child.WaitForExit(10_000);
		}
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
