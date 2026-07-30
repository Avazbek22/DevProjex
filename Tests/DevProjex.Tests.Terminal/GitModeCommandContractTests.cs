namespace DevProjex.Tests.Terminal;

public sealed class GitModeCommandContractTests
{
	[Fact]
	public async Task ExplicitTrackedModeWithoutRepositoryWritesReportThenReturnsPolicyFailure()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("tracked-looking.cs", "class App {}");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"analyze", workspace.Path,
			"--format", "json",
			"--git-mode", "tracked",
			"--exclude", "none",
			"--strict");

		Assert.Equal(CommandLineExitCodes.PolicyFailure, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		var diagnostic = Assert.Single(
			document.RootElement.GetProperty("diagnostics").EnumerateArray(),
			static item => item.GetProperty("code").GetString() == "DPX-GIT-TRACKED-INDEX-UNAVAILABLE");
		Assert.Equal("error", diagnostic.GetProperty("severity").GetString());
		Assert.Contains("DPX-GIT-TRACKED-INDEX-UNAVAILABLE", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain("\u001b", environment.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExplicitTrackedModePreventsContextAndProjectOutputsWhenNoIndexIsReadable()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("app.cs", "class App {}");
		var contextPath = Path.Combine(workspace.Path, "outside", "context.md");
		var projectPath = Path.Combine(workspace.Path, "outside", "submission");

		var contextEnvironment = new TestTerminalEnvironment();
		var contextExit = await RunAsync(
			workspace,
			contextEnvironment,
			"export", "context", workspace.Path,
			"--git-mode", "tracked",
			"--exclude", "none",
			"-o", contextPath);
		Assert.Equal(CommandLineExitCodes.PolicyFailure, contextExit);
		Assert.False(File.Exists(contextPath));
		Assert.Empty(contextEnvironment.StandardOutput);
		Assert.Contains("DPX-GIT-TRACKED-INDEX-UNAVAILABLE", contextEnvironment.StandardError, StringComparison.Ordinal);

		var projectEnvironment = new TestTerminalEnvironment();
		var projectExit = await RunAsync(
			workspace,
			projectEnvironment,
			"export", "project", workspace.Path,
			"--as", "folder",
			"--git-mode", "tracked",
			"--exclude", "none",
			"-o", projectPath);
		Assert.Equal(CommandLineExitCodes.PolicyFailure, projectExit);
		Assert.False(Directory.Exists(projectPath));
		Assert.Empty(projectEnvironment.StandardOutput);
		Assert.Contains("DPX-GIT-TRACKED-INDEX-UNAVAILABLE", projectEnvironment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task StandaloneGitIgnoreModeFiltersPatternsWithoutPretendingTrackedModeIsReady()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile(".gitignore", "*.tmp\n");
		workspace.WriteFile("included.cs", "class App {}");
		workspace.WriteFile("ignored.tmp", "noise");

		var gitIgnoreEnvironment = new TestTerminalEnvironment();
		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(
				workspace,
				gitIgnoreEnvironment,
				"analyze", workspace.Path,
				"--format", "json",
				"--git-mode", "gitignore",
				"--exclude", "none"));
		using (var document = JsonDocument.Parse(gitIgnoreEnvironment.StandardOutput))
		{
			Assert.Equal(2, document.RootElement
				.GetProperty("inventory").GetProperty("files").GetInt32());
			Assert.Empty(document.RootElement.GetProperty("diagnostics").EnumerateArray());
		}

		var noneEnvironment = new TestTerminalEnvironment();
		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(
				workspace,
				noneEnvironment,
				"analyze", workspace.Path,
				"--format", "json",
				"--git-mode", "none",
				"--exclude", "none"));
		using var unfilteredDocument = JsonDocument.Parse(noneEnvironment.StandardOutput);
		Assert.Equal(
			3,
			unfilteredDocument.RootElement.GetProperty("inventory").GetProperty("files").GetInt32());
	}

	[Fact]
	public async Task NestedStandaloneGitIgnoreIsAppliedAtItsOwnScope()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("root.tmp", "visible");
		workspace.WriteFile("nested/.gitignore", "*.tmp\n!keep.tmp\n");
		workspace.WriteFile("nested/drop.tmp", "ignored");
		workspace.WriteFile("nested/keep.tmp", "kept");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"export", "context", workspace.Path,
			"--view", "tree",
			"--format", "json",
			"--git-mode", "gitignore",
			"--exclude", "none",
			"-o", "-");

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains("root.tmp", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("nested/keep.tmp", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("nested/drop.tmp", environment.StandardOutput, StringComparison.Ordinal);
	}

	private static Task<int> RunAsync(
		TemporaryDirectory workspace,
		TestTerminalEnvironment environment,
		params string[] arguments) =>
		new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(arguments, TestContext.Current.CancellationToken);
}
