using System.Text.RegularExpressions;

namespace DevProjex.Tests.Unit;

public sealed partial class PerformanceRunnerContractTests
{
	[Fact]
	public void LocalRunnerMapsEveryOptInFlagAndRejectsSkippedOnlyRuns()
	{
		var repositoryRoot = FindRepositoryRoot();
		var script = File.ReadAllText(Path.Combine(repositoryRoot, "Scripts", "perf-local.ps1"));
		var testFlags = Directory
			.EnumerateFiles(Path.Combine(repositoryRoot, "Tests"), "*.cs", SearchOption.AllDirectories)
			.Where(static path =>
				!IsBuildOutput(path) &&
				!string.Equals(
					Path.GetFileName(path),
					"PerformanceRunnerContractTests.cs",
					StringComparison.Ordinal))
			.Select(File.ReadAllText)
			.Where(static source => source.Contains("Environment.GetEnvironmentVariable", StringComparison.Ordinal))
			.SelectMany(static source => OptInVariableRegex().Matches(source).Select(static match => match.Value))
			.ToHashSet(StringComparer.Ordinal);
		var runnerFlags = OptInVariableRegex()
			.Matches(script)
			.Select(static match => match.Value)
			.ToHashSet(StringComparer.Ordinal);

		Assert.NotEmpty(testFlags);
		Assert.Equal(
			testFlags.Order(StringComparer.Ordinal),
			runnerFlags.Order(StringComparer.Ordinal));
		foreach (var flag in testFlags)
			Assert.True(
				OptInVariableRegex().Matches(script).Count(match => match.Value == flag) >= 2,
				$"Performance opt-in variable is not mapped to a named scenario: {flag}");

		Assert.Contains("[string[]]$Scenario = @(\"Smoke\")", script, StringComparison.Ordinal);
		Assert.DoesNotContain("DEVPROJEX_RUN_LOCAL_PERF", script, StringComparison.Ordinal);
		Assert.Contains("DevProjex.Tests.Integration.csproj", script, StringComparison.Ordinal);
		Assert.Contains("DevProjex.Tests.Unit.csproj", script, StringComparison.Ordinal);
		Assert.Contains("DevProjex.Tests.Terminal.csproj", script, StringComparison.Ordinal);
		Assert.Contains("trx;LogFileName=", script, StringComparison.Ordinal);
		Assert.Contains("GetAttribute(\"executed\")", script, StringComparison.Ordinal);
		Assert.Contains("GetAttribute(\"passed\")", script, StringComparison.Ordinal);
		Assert.Contains("$counters.Passed -le 0", script, StringComparison.Ordinal);
		Assert.Contains("only skipped or missing tests", script, StringComparison.Ordinal);
	}

	private static bool IsBuildOutput(string path)
	{
		var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
		return normalized.Contains(
			$"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
			StringComparison.OrdinalIgnoreCase) ||
		       normalized.Contains(
			       $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
			       StringComparison.OrdinalIgnoreCase);
	}

	private static string FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DevProjex.sln")))
			directory = directory.Parent;
		return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
	}

	[GeneratedRegex(@"DEVPROJEX_RUN_[A-Z0-9_]+", RegexOptions.CultureInvariant)]
	private static partial Regex OptInVariableRegex();
}
