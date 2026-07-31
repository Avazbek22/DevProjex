using System.Diagnostics;
using DevProjex.Application.Context;

namespace DevProjex.Tests.Terminal;

public sealed class GitModeCommandContractTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task OpenTrackedModeFailsBeforeDesktopLaunchWhenIndexIsUnavailable(
		bool createRepositoryBoundary)
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/App.cs", "class App {}\n");
		if (createRepositoryBoundary)
			workspace.CreateDirectory(".git");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"open", workspace.Path,
			"--git-mode", "tracked",
			"--exclude", "none");

		Assert.Equal(CommandLineExitCodes.PolicyFailure, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains(
			ProjectContextGitReadiness.UnavailableDiagnosticCode,
			environment.StandardError,
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public async Task ExplicitTrackedModeWithoutRepositoryWritesReportThenReturnsPolicyFailure(
		bool strict,
		bool writeToFile)
	{
		using var workspace = new TemporaryDirectory();
		using var destination = new TemporaryDirectory();
		workspace.WriteFile("tracked-looking.cs", "class App {}");
		var environment = new TestTerminalEnvironment();
		var reportPath = Path.Combine(destination.Path, "analysis.json");
		var arguments = new List<string>
		{
			"analyze", workspace.Path,
			"--format", "json",
			"--git-mode", "tracked",
			"--exclude", "none"
		};
		if (strict)
			arguments.Add("--strict");
		if (writeToFile)
		{
			arguments.Add("--output");
			arguments.Add(reportPath);
		}

		var exitCode = await RunAsync(
			workspace,
			environment,
			[.. arguments]);

		Assert.Equal(CommandLineExitCodes.PolicyFailure, exitCode);
		var payload = writeToFile
			? File.ReadAllText(reportPath)
			: environment.StandardOutput;
		using var document = JsonDocument.Parse(payload);
		var diagnostic = Assert.Single(
			document.RootElement.GetProperty("diagnostics").EnumerateArray(),
			static item => item.GetProperty("code").GetString() == "DPX-GIT-TRACKED-INDEX-UNAVAILABLE");
		Assert.Equal("error", diagnostic.GetProperty("severity").GetString());
		Assert.Contains("DPX-GIT-TRACKED-INDEX-UNAVAILABLE", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain("\u001b", payload, StringComparison.Ordinal);
		if (writeToFile)
			Assert.Equal(Path.GetFullPath(reportPath), environment.StandardOutput.Trim());
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
	public async Task ExplicitTrackedModeAcceptsReadableEmptyIndexAsReady()
	{
		using var workspace = new TemporaryDirectory();
		if (!TryRunGit(workspace.Path, "init", "--quiet"))
			Assert.Skip("Git is not available in this test environment.");
		Assert.True(
			TryRunGit(workspace.Path, "read-tree", "--empty"),
			"Git initialized the repository but could not create a readable empty index.");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"analyze", workspace.Path,
			"--format", "json",
			"--git-mode", "tracked",
			"--exclude", "none");

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.Equal(0, document.RootElement.GetProperty("inventory").GetProperty("files").GetInt32());
		Assert.Empty(document.RootElement.GetProperty("diagnostics").EnumerateArray());
		Assert.Empty(environment.StandardError);
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

	private static bool TryRunGit(string workingDirectory, params string[] arguments)
	{
		try
		{
			var startInfo = new ProcessStartInfo("git")
			{
				WorkingDirectory = workingDirectory,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			foreach (var argument in arguments)
				startInfo.ArgumentList.Add(argument);

			using var process = Process.Start(startInfo);
			if (process is null)
				return false;
			var outputTask = process.StandardOutput.ReadToEndAsync();
			var errorTask = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(10_000))
			{
				process.Kill(entireProcessTree: true);
				return false;
			}

			_ = outputTask.GetAwaiter().GetResult();
			_ = errorTask.GetAwaiter().GetResult();
			return process.ExitCode == 0;
		}
		catch (Exception exception) when (exception is
		       System.ComponentModel.Win32Exception or
		       IOException or
		       InvalidOperationException)
		{
			return false;
		}
	}
}
