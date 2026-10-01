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
			Assert.Contains("--timeout 20m", step, StringComparison.Ordinal);
			Assert.DoesNotContain("--blame-hang", step, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void ReleaseTestStepsRequireExecutedResultsAndPreserveNativeFailures()
	{
		var workflow = File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "release-validate.yml"));
		var markers = new[]
		{
			"Published Single-File Extraction Contract",
			"Published Completion Native Shell Integration",
			"Nested-Mount Destination Safety Gate (Linux x64)",
			"Portable Launcher Smoke (Windows)",
			"Portable Launcher ConPTY TUI Smoke (Windows)",
			"Published Native PTY TUI Smoke (Unix)"
		};
		Assert.Equal(markers.Length, workflow.Split("dotnet test ", StringSplitOptions.None).Length - 1);
		foreach (var marker in markers)
		{
			var step = ExtractStep(workflow, $"      - name: {marker}\n");
			Assert.Contains("trx;LogFileName=", step, StringComparison.Ordinal);
			Assert.Contains("--results-directory", step, StringComparison.Ordinal);
			Assert.Contains("$LASTEXITCODE -ne 0", step, StringComparison.Ordinal);
			Assert.Contains("./Scripts/ci/Test-ExecutedTests.ps1", step, StringComparison.Ordinal);
			Assert.Contains("-ResultsPath", step, StringComparison.Ordinal);
			Assert.Contains("-JobName", step, StringComparison.Ordinal);
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
