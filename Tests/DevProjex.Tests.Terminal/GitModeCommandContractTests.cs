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
	public async Task StagedScopeNarrowsProjectCopyAndUsesCurrentWorktreeContent()
	{
		using var workspace = new TemporaryDirectory();
		using var output = new TemporaryDirectory();
		EnsureRepository(workspace.Path);
		workspace.WriteFile("Selected.cs", "baseline\n");
		workspace.WriteFile("Other.cs", "other\n");
		CommitAll(workspace.Path, "baseline");
		workspace.WriteFile("Selected.cs", "staged-version\n");
		Assert.True(TryRunGit(workspace.Path, "add", "Selected.cs"));
		workspace.WriteFile("Selected.cs", "current-worktree-version\n");
		var destination = Path.Combine(output.Path, "submission");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"export", "project", workspace.Path,
			"--as", "folder",
			"--git-mode", "staged",
			"--exclude", "none",
			"-o", destination);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(
			"current-worktree-version\n",
			await File.ReadAllTextAsync(
				Path.Combine(destination, "Selected.cs"),
				TestContext.Current.CancellationToken));
		Assert.False(File.Exists(Path.Combine(destination, "Other.cs")));
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task ChangesScopeIncludesUntrackedButNeverGitIgnoredFiles()
	{
		using var workspace = new TemporaryDirectory();
		EnsureRepository(workspace.Path);
		workspace.WriteFile(".gitignore", "*.ignored\n");
		workspace.WriteFile("Tracked.cs", "baseline\n");
		workspace.WriteFile("Staged.cs", "staged-baseline\n");
		workspace.WriteFile("Tracked.ignored", "tracked-baseline\n");
		Assert.True(TryRunGit(workspace.Path, "add", "-f", "Tracked.ignored"));
		CommitAll(workspace.Path, "baseline");
		workspace.WriteFile("Tracked.cs", "changed\n");
		workspace.WriteFile("Staged.cs", "staged-change\n");
		Assert.True(TryRunGit(workspace.Path, "add", "Staged.cs"));
		workspace.WriteFile("Tracked.ignored", "tracked-ignored-change\n");
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
		Assert.Contains("Staged.cs", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("Tracked.ignored", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("Untracked.cs", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("Secret.ignored", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task StagedScopeDoesNotLeakCommittedBaselineBeforeOrAfterAFileIsStaged()
	{
		using var workspace = new TemporaryDirectory();
		EnsureRepository(workspace.Path);
		workspace.WriteFile(".internal/Nested.cs", "dot-folder-baseline\n");
		workspace.WriteFile(".metadata", "dot-file-baseline\n");
		workspace.WriteFile("LICENSE", "extensionless-baseline\n");
		workspace.WriteFile("Baseline.cs", "ordinary-baseline\n");
		workspace.WriteFile("Selected.cs", "selected-baseline\n");
		CommitAll(workspace.Path, "baseline");

		var cleanEnvironment = new TestTerminalEnvironment();
		var cleanExitCode = await RunAsync(
			workspace,
			cleanEnvironment,
			"analyze", workspace.Path,
			"--format", "json",
			"--git-mode", "staged",
			"--exclude", "none");

		Assert.Equal(CommandLineExitCodes.Success, cleanExitCode);
		using (var document = JsonDocument.Parse(cleanEnvironment.StandardOutput))
		{
			Assert.Equal(
				0,
				document.RootElement.GetProperty("inventory").GetProperty("files").GetInt32());
		}
		Assert.Empty(cleanEnvironment.StandardError);

		workspace.WriteFile("Selected.cs", "selected-staged\n");
		Assert.True(TryRunGit(workspace.Path, "add", "Selected.cs"));
		var stagedEnvironment = new TestTerminalEnvironment();
		var stagedExitCode = await RunAsync(
			workspace,
			stagedEnvironment,
			"tree", workspace.Path,
			"--git-mode", "staged",
			"--exclude", "none",
			"--format", "text");

		Assert.Equal(CommandLineExitCodes.Success, stagedExitCode);
		Assert.Contains("Selected.cs", stagedEnvironment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("Baseline.cs", stagedEnvironment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("Nested.cs", stagedEnvironment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain(".metadata", stagedEnvironment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("LICENSE", stagedEnvironment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(stagedEnvironment.StandardError);
	}

	[Fact]
	public async Task ExplicitRootAndPathSelectionCannotExpandStagedScope()
	{
		using var workspace = new TemporaryDirectory();
		EnsureRepository(workspace.Path);
		workspace.WriteFile("baseline/Baseline.cs", "committed-baseline-marker\n");
		workspace.WriteFile("staged/Selected.cs", "selected-baseline\n");
		CommitAll(workspace.Path, "baseline");
		workspace.WriteFile("staged/Selected.cs", "selected-staged-marker\n");
		Assert.True(TryRunGit(workspace.Path, "add", "staged/Selected.cs"));
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"export", "context", workspace.Path,
			"--view", "content",
			"--format", "json",
			"--git-mode", "staged",
			"--root", "baseline",
			"--select", "baseline/Baseline.cs",
			"--extension", ".cs",
			"--exclude", "none",
			"-o", "-");

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.Equal(
			"staged",
			document.RootElement.GetProperty("selection").GetProperty("gitMode").GetString());
		Assert.Empty(document.RootElement.GetProperty("files").EnumerateArray());
		Assert.Equal(0, document.RootElement.GetProperty("metrics").GetProperty("files").GetInt32());
		Assert.DoesNotContain("committed-baseline-marker", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Theory]
	[InlineData("staged", "json")]
	[InlineData("staged", "xml")]
	[InlineData("changes", "json")]
	[InlineData("changes", "xml")]
	public async Task MachineContextKeepsExplicitExtensionsWhileGitScopeNarrowsFiles(
		string gitMode,
		string format)
	{
		using var workspace = new TemporaryDirectory();
		EnsureRepository(workspace.Path);
		workspace.WriteFile("Selected.cs", "selected-baseline\n");
		workspace.WriteFile("Documentation.md", "documentation-baseline\n");
		CommitAll(workspace.Path, "baseline");

		var clean = await ExportMachineContextAsync(workspace, gitMode, format);
		Assert.Equal(gitMode, clean.GitMode);
		Assert.Equal([".cs", ".md"], clean.Extensions);
		Assert.Empty(clean.Files);
		Assert.Equal(0, clean.MetricFiles);

		workspace.WriteFile("Selected.cs", "selected-current\n");
		if (gitMode == "staged")
			Assert.True(TryRunGit(workspace.Path, "add", "Selected.cs"));

		var changed = await ExportMachineContextAsync(workspace, gitMode, format);
		Assert.Equal(gitMode, changed.GitMode);
		Assert.Equal([".cs", ".md"], changed.Extensions);
		Assert.Equal("Selected.cs", Path.GetFileName(Assert.Single(changed.Files)));
		Assert.Equal(1, changed.MetricFiles);
	}

	[Fact]
	public async Task StagedDeletionProducesAWarningWithoutInventingFileContent()
	{
		using var workspace = new TemporaryDirectory();
		EnsureRepository(workspace.Path);
		workspace.WriteFile("Deleted.cs", "deleted\n");
		workspace.WriteFile("Kept.cs", "kept\n");
		workspace.WriteFile("RenameSource.cs", "rename-source-marker\n");
		CommitAll(workspace.Path, "baseline");
		File.Delete(Path.Combine(workspace.Path, "Deleted.cs"));
		Assert.True(TryRunGit(workspace.Path, "mv", "RenameSource.cs", "Renamed.cs"));
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
		Assert.DoesNotContain("RenameSource.cs", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("Renamed.cs", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains(GitScopeFilter.DeletedDiagnosticCode, environment.StandardError, StringComparison.Ordinal);
		Assert.Matches(@"(?<!\d)2(?!\d)", environment.StandardError);
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
		var cleanEnvironment = new TestTerminalEnvironment();
		var cleanExitCode = await RunAsync(
			workspace,
			cleanEnvironment,
			"analyze", workspace.Path,
			"--format", "json",
			"--git-mode", $"diff:{baseline}..HEAD",
			"--exclude", "none");
		Assert.Equal(CommandLineExitCodes.Success, cleanExitCode);
		using (var cleanDocument = JsonDocument.Parse(cleanEnvironment.StandardOutput))
		{
			Assert.Equal(
				0,
				cleanDocument.RootElement.GetProperty("inventory").GetProperty("files").GetInt32());
		}
		Assert.Empty(cleanEnvironment.StandardError);

		workspace.WriteFile("Changed.cs", "committed-change\n");
		Assert.True(TryRunGit(workspace.Path, "add", "Changed.cs"));
		Assert.True(TryRunGit(workspace.Path, "commit", "--quiet", "-m", "change"));
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

	[Fact]
	public async Task DiffScopeWithMissingRefFailsClosedWithoutWritingADocument()
	{
		using var workspace = new TemporaryDirectory();
		EnsureRepository(workspace.Path);
		workspace.WriteFile("Tracked.cs", "baseline\n");
		CommitAll(workspace.Path, "baseline");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"export", "context", workspace.Path,
			"--view", "content",
			"--format", "json",
			"--git-mode", "diff:refs/heads/does-not-exist..HEAD",
			"--exclude", "none",
			"-o", "-");

		Assert.Equal(CommandLineExitCodes.PolicyFailure, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains(GitScopeFilter.UnavailableDiagnosticCode, environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task HelpAdvertisesOnlyGitModesSupportedByEachCommand()
	{
		using var workspace = new TemporaryDirectory();
		var direct = new TestTerminalEnvironment();
		var desktop = new TestTerminalEnvironment();
		var profileSave = new TestTerminalEnvironment();

		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(workspace, direct, "analyze", "--language", "en", "--help"));
		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(workspace, desktop, "open", "--language", "en", "--help"));
		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(workspace, profileSave, "profile", "save", "--language", "en", "--help"));

		Assert.Contains("diff:<ref>..<ref>", direct.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("staged", desktop.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("changes", desktop.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("diff:<ref>..<ref>", desktop.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("tracked", profileSave.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("staged", profileSave.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("changes", profileSave.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("diff:<ref>..<ref>", profileSave.StandardOutput, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("staged")]
	[InlineData("changes")]
	[InlineData("diff:HEAD..HEAD")]
	public async Task MomentaryGitModesOutsideRepositoryFailClosed(string gitMode)
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("Local.cs", "local\n");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"tree", workspace.Path,
			"--git-mode", gitMode,
			"--exclude", "none",
			"--format", "text");

		Assert.Equal(CommandLineExitCodes.PolicyFailure, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains(GitScopeFilter.UnavailableDiagnosticCode, environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UnavailableMomentaryScopeKeepsTheRequestedAnalyzeMachineReport()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("Local.cs", "local\n");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"analyze", workspace.Path,
			"--format", "json",
			"--git-mode", "staged",
			"--exclude", "none");

		Assert.Equal(CommandLineExitCodes.PolicyFailure, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.Equal("staged", document.RootElement.GetProperty("selection").GetProperty("gitMode").GetString());
		Assert.Equal(0, document.RootElement.GetProperty("inventory").GetProperty("files").GetInt32());
		Assert.Contains(
			document.RootElement.GetProperty("diagnostics").EnumerateArray(),
			static diagnostic =>
				diagnostic.GetProperty("code").GetString() == GitScopeFilter.UnavailableDiagnosticCode);
		Assert.Contains(GitScopeFilter.UnavailableDiagnosticCode, environment.StandardError, StringComparison.Ordinal);
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
			"--exclude", "none",
			"--language", "en");

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-CLI-PROFILE-INVALID", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains(
			"Momentary Git modes cannot be saved in profiles",
			environment.StandardError,
			StringComparison.Ordinal);
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
	[InlineData("staged")]
	[InlineData("changes")]
	public async Task OpenMomentaryModeFailsBeforeDesktopLaunchWhenGitIndexIsUnavailable(string mode)
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/App.cs", "class App {}\n");
		workspace.CreateDirectory(".git");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"open", workspace.Path,
			"--git-mode", mode,
			"--exclude", "none");

		Assert.Equal(CommandLineExitCodes.PolicyFailure, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains(
			GitScopeFilter.UnavailableDiagnosticCode,
			environment.StandardError,
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(GitFilteringMode.Staged)]
	[InlineData(GitFilteringMode.Changes)]
	public async Task DesktopOpenReadinessAcceptsValidMomentaryScopeWithoutGitIgnore(
		GitFilteringMode mode)
	{
		using var workspace = new TemporaryDirectory();
		using var data = new TemporaryDirectory();
		EnsureRepository(workspace.Path);
		workspace.WriteFile("Tracked.cs", "baseline\n");
		CommitAll(workspace.Path, "baseline");
		workspace.WriteFile("Tracked.cs", "changed\n");
		if (mode == GitFilteringMode.Staged)
			Assert.True(TryRunGit(workspace.Path, "add", "Tracked.cs"));
		using var services = new TerminalServiceFactory(() => data.Path).Create(AppLanguage.En);

		var diagnostics = await DesktopOpenGitReadinessValidator.ValidateAsync(
			services,
			workspace.Path,
			CreateGitSelection(mode),
			TestContext.Current.CancellationToken);

		Assert.DoesNotContain(
			diagnostics,
			static diagnostic => diagnostic.Severity == ContextDiagnosticSeverity.Error);
	}

	[Theory]
	[InlineData(GitFilteringMode.Staged)]
	[InlineData(GitFilteringMode.Changes)]
	public async Task DesktopOpenReadinessRejectsMomentaryScopeOutsideARepository(
		GitFilteringMode mode)
	{
		using var workspace = new TemporaryDirectory();
		using var data = new TemporaryDirectory();
		workspace.WriteFile("App.cs", "class App {}\n");
		using var services = new TerminalServiceFactory(() => data.Path).Create(AppLanguage.En);

		var diagnostics = await DesktopOpenGitReadinessValidator.ValidateAsync(
			services,
			workspace.Path,
			CreateGitSelection(mode),
			TestContext.Current.CancellationToken);

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal(GitScopeFilter.UnavailableDiagnosticCode, diagnostic.Code);
		Assert.Equal(ContextDiagnosticSeverity.Error, diagnostic.Severity);
	}

	[Fact]
	public async Task DesktopOpenReadinessRejectsPartiallyUnavailableGitState()
	{
		using var workspace = new TemporaryDirectory();
		using var data = new TemporaryDirectory();
		EnsureRepository(workspace.Path);
		workspace.WriteFile("src/App.cs", "class App {}\n");
		CommitAll(workspace.Path, "baseline");
		var nestedRepository = workspace.CreateDirectory("vendor");
		EnsureRepository(nestedRepository);
		File.WriteAllText(Path.Combine(nestedRepository, "Dependency.cs"), "class Dependency {}\n");
		CommitAll(nestedRepository, "nested baseline");
		Assert.True(TryRunGit(workspace.Path, "add", "vendor"));
		CommitAll(workspace.Path, "track nested repository");
		var nestedIndex = Path.Combine(nestedRepository, ".git", "index");
		File.Delete(nestedIndex);
		Directory.CreateDirectory(nestedIndex);
		using var services = new TerminalServiceFactory(() => data.Path).Create(AppLanguage.En);

		var diagnostics = await DesktopOpenGitReadinessValidator.ValidateAsync(
			services,
			workspace.Path,
			CreateGitSelection(GitFilteringMode.Changes),
			TestContext.Current.CancellationToken);

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal(GitScopeFilter.UnavailableDiagnosticCode, diagnostic.Code);
		Assert.Equal(ContextDiagnosticSeverity.Error, diagnostic.Severity);
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

	private static async Task<int> RunAsync(
		TemporaryDirectory workspace,
		TestTerminalEnvironment environment,
		params string[] arguments)
	{
		using var dataRoot = new TemporaryDirectory();
		return await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => dataRoot.Path))
			.RunAsync(arguments, TestContext.Current.CancellationToken);
	}

	private static ProjectSelectionSpec CreateGitSelection(GitFilteringMode mode) =>
		ProjectSelectionSpec.Standard with
		{
			GitMode = mode,
			GitDiffRange = null,
			Exclusions = []
		};

	private static async Task<(string GitMode, string[] Extensions, string[] Files, int MetricFiles)>
		ExportMachineContextAsync(
		TemporaryDirectory workspace,
		string gitMode,
		string format)
	{
		var environment = new TestTerminalEnvironment();
		var exitCode = await RunAsync(
			workspace,
			environment,
			"export", "context", workspace.Path,
			"--view", "content",
			"--format", format,
			"--git-mode", gitMode,
			"--exclude", "none",
			"--extension", ".cs",
			"--extension", ".md",
			"-o", "-");
		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardError);

		if (format == "json")
		{
			using var document = JsonDocument.Parse(environment.StandardOutput);
			return (
				document.RootElement.GetProperty("selection").GetProperty("gitMode").GetString()!,
				document.RootElement.GetProperty("selection").GetProperty("extensions")
					.EnumerateArray().Select(static item => item.GetString()!).ToArray(),
				document.RootElement.GetProperty("files").EnumerateArray()
					.Select(static file => file.GetProperty("path").GetString()!).ToArray(),
				document.RootElement.GetProperty("metrics").GetProperty("files").GetInt32());
		}

		var xml = System.Xml.Linq.XDocument.Parse(environment.StandardOutput);
		return (
			xml.Root!.Element("selection")!.Element("gitMode")!.Value,
			xml.Root.Element("selection")!.Element("extensions")!.Elements("extension")
				.Select(static item => item.Value).ToArray(),
			xml.Root.Element("files")!.Elements("file")
				.Select(static file => file.Attribute("path")!.Value).ToArray(),
			int.Parse(
				xml.Root.Element("metrics")!.Element("files")!.Value,
				System.Globalization.CultureInfo.InvariantCulture));
	}

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
		var hooksPath = Directory.CreateDirectory(Path.Combine(path, ".git", "devprojex-test-hooks")).FullName;
		var excludesPath = Path.Combine(path, ".git", "devprojex-test-excludes");
		File.WriteAllText(excludesPath, string.Empty);
		Assert.True(TryRunGit(path, "config", "user.name", "DevProjex Tests"));
		Assert.True(TryRunGit(path, "config", "user.email", "devprojex-tests@example.invalid"));
		Assert.True(TryRunGit(path, "config", "commit.gpgSign", "false"));
		Assert.True(TryRunGit(path, "config", "core.hooksPath", hooksPath));
		Assert.True(TryRunGit(path, "config", "core.excludesFile", excludesPath));
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
