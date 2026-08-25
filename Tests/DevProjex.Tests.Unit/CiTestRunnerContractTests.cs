namespace DevProjex.Tests.Unit;

public sealed class CiTestRunnerContractTests
{
	[Fact]
	public void UiSuiteUsesNativeMicrosoftTestingPlatformTimeoutAndReporting()
	{
		var workflow = File.ReadAllText(Path.Combine(
			FindRepositoryRoot(),
			".github",
			"workflows",
			"dotnet.yml"));
		var windowsStep = ExtractStep(workflow, "      - name: Run UI Tests\n");
		var linuxStep = ExtractStep(workflow, "      - name: Run UI Tests (Linux headless)\n");

		foreach (var step in new[] { windowsStep, linuxStep })
		{
			Assert.Contains("matrix.suite_id == 'ui'", step, StringComparison.Ordinal);
			Assert.Contains("--results-directory", step, StringComparison.Ordinal);
			Assert.Contains("--report-xunit-trx", step, StringComparison.Ordinal);
			Assert.Contains("--timeout 8m", step, StringComparison.Ordinal);
			Assert.DoesNotContain("--blame-hang", step, StringComparison.Ordinal);
		}
	}

	private static string ExtractStep(string workflow, string marker)
	{
		var normalized = workflow.Replace("\r\n", "\n", StringComparison.Ordinal);
		var start = normalized.IndexOf(marker, StringComparison.Ordinal);
		Assert.True(start >= 0, $"Workflow step was not found: {marker.Trim()}");
		var end = normalized.IndexOf("\n      - name:", start + marker.Length, StringComparison.Ordinal);
		return normalized[start..(end >= 0 ? end : normalized.Length)];
	}

	private static string FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DevProjex.sln")))
			directory = directory.Parent;
		return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
	}
}
