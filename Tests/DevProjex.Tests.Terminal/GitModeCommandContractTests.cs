using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

public sealed class GitModeCommandContractTests
{
	[Fact]
	public async Task DesktopOpenRejectsDiffScopeBeforeLaunchingTheGui()
	{
		using var workspace = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"open", workspace.Path,
			"--git-mode", "diff:main..feature",
			"--language", "en");

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains("Desktop supports", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task StagedScopeSelectsIndexChangesButExportsCurrentWorktreeContent()
	{
		using var workspace = new TemporaryDirectory();
		EnsureRepository(workspace.Path);
		workspace.WriteFile("Selected.cs", "baseline\n");
		workspace.WriteFile("Other.cs", "other\n");
		CommitAll(workspace.Path, "baseline");
		workspace.WriteFile("Selected.cs", "staged-version\n");
		Assert.True(TryRunGit(workspace.Path, "add", "Selected.cs"));
		workspace.WriteFile("Selected.cs", "current-worktree-version\n");
		workspace.WriteFile("Untracked.cs", "untracked\n");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"export", "context", workspace.Path,
			"--view", "content",
			"--format", "text",
			"--git-mode", "staged",
			"--exclude", "none",
			"-o", "-");

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains("current-worktree-version", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("staged-version", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("untracked", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task ChangesScopeIncludesUntrackedButNeverGitIgnoredFiles()
	{
		using var workspace = new TemporaryDirectory();
		EnsureRepository(workspace.Path);
		workspace.WriteFile(".gitignore", "*.ignored\n");
		workspace.WriteFile("Tracked.cs", "baseline\n");
		CommitAll(workspace.Path, "baseline");
		workspace.WriteFile("Tracked.cs", "changed\n");
		workspace.WriteFile("Untracked.cs", "visible\n");
		workspace.WriteFile("Secret.ignored", "ignored\n");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"tree", workspace.Path,
			"--git-mode", "changes",
			"--exclude", "none",
			"--format", "text");

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains("Tracked.cs", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("Untracked.cs", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("Secret.ignored", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task StagedDeletionProducesAWarningWithoutInventingFileContent()
	{
		using var workspace = new TemporaryDirectory();
		EnsureRepository(workspace.Path);
		workspace.WriteFile("Deleted.cs", "deleted\n");
		workspace.WriteFile("Kept.cs", "kept\n");
		CommitAll(workspace.Path, "baseline");
		File.Delete(Path.Combine(workspace.Path, "Deleted.cs"));
		Assert.True(TryRunGit(workspace.Path, "add", "--update"));
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"tree", workspace.Path,
			"--git-mode", "staged",
			"--exclude", "none",
			"--format", "text");

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.DoesNotContain("Deleted.cs", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains(GitScopeFilter.DeletedDiagnosticCode, environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("1", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task DiffScopeUsesTheRequestedRefsAndReportsItsFullMachineToken()
	{
		using var workspace = new TemporaryDirectory();
		EnsureRepository(workspace.Path);
		workspace.WriteFile("Changed.cs", "baseline\n");
		workspace.WriteFile("Untouched.cs", "untouched\n");
		CommitAll(workspace.Path, "baseline");
		var baseline = ReadGit(workspace.Path, "rev-parse", "HEAD");
		workspace.WriteFile("Changed.cs", "committed-change\n");
		CommitAll(workspace.Path, "change");
		var changed = ReadGit(workspace.Path, "rev-parse", "HEAD");
		workspace.WriteFile("Changed.cs", "current-worktree-content\n");
		var environment = new TestTerminalEnvironment();
		var token = $"diff:{baseline}..{changed}";

		var exitCode = await RunAsync(
			workspace,
			environment,
			"export", "context", workspace.Path,
			"--view", "content",
			"--format", "json",
			"--git-mode", token,
			"--exclude", "none",
			"-o", "-");

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.Equal(token, document.RootElement.GetProperty("selection").GetProperty("gitMode").GetString());
		var serialized = document.RootElement.GetRawText();
		Assert.Contains("current-worktree-content", serialized, StringComparison.Ordinal);
		Assert.DoesNotContain("Untouched.cs", serialized, StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Theory]
	[InlineData("staged")]
	[InlineData("changes")]
	[InlineData("diff:main..feature")]
	public async Task LocalProfilesRejectMomentaryGitModes(string mode)
	{
		using var workspace = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"profile", "save", workspace.Path,
			"--git-mode", mode,
			"--exclude", "none");

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-CLI-PROFILE-INVALID", environment.StandardError, StringComparison.Ordinal);
	}

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
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			foreach (var argument in arguments)
				startInfo.ArgumentList.Add(argument);

			using var process = Process.Start(startInfo);
			if (process is null)
				return false;
			process.StandardInput.Close();
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

	private static void EnsureRepository(string path)
	{
		if (!TryRunGit(path, "init", "--quiet"))
			Assert.Skip("Git is not available in this test environment.");
		Assert.True(TryRunGit(path, "config", "user.name", "DevProjex Tests"));
		Assert.True(TryRunGit(path, "config", "user.email", "devprojex-tests@example.invalid"));
	}

	private static void CommitAll(string path, string message)
	{
		Assert.True(TryRunGit(path, "add", "--all"));
		Assert.True(TryRunGit(path, "commit", "--quiet", "-m", message));
	}

	private static string ReadGit(string workingDirectory, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Git did not start.");
		process.StandardInput.Close();
		var output = process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		Assert.True(process.WaitForExit(10_000));
		Assert.True(process.ExitCode == 0, error);
		return output.Trim();
	}
}
