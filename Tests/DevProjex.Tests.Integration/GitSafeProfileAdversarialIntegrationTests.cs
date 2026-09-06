namespace DevProjex.Tests.Integration;

public sealed class GitSafeProfileAdversarialIntegrationTests
{
	[Fact]
	public async Task LocalRead_PositiveControlTriggersHostileFsMonitorButSafeReadsDoNot()
	{
		using var fixture = await HostileGitFixture.CreateAsync(TestContext.Current.CancellationToken);
		var markerCommand = fixture.CreateMarkerCommand("fsmonitor");
		fixture.RunGit("config", "core.fsmonitor", markerCommand);

		fixture.RunGit("status", "--short");
		Assert.True(File.Exists(fixture.MarkerPath("fsmonitor")),
			"Positive control failed: unprotected git did not execute core.fsmonitor.");
		File.Delete(fixture.MarkerPath("fsmonitor"));

		Assert.True(GitTrackedPathIndexCache.TryLoad(
			fixture.RepositoryPath,
			Path.Combine(fixture.RepositoryPath, ".git"),
			TestContext.Current.CancellationToken,
			out var tracked));
		Assert.True(tracked.Contains(Path.Combine(fixture.RepositoryPath, "tracked.txt")));

		var staged = await new GitScopePathProvider().ResolveAsync(
			fixture.RepositoryPath,
			GitFilteringMode.Staged,
			diffRange: null,
			TestContext.Current.CancellationToken);
		Assert.True(staged.IsAvailable, staged.FailureReason);
		Assert.False(File.Exists(fixture.MarkerPath("fsmonitor")));
	}

	[Fact]
	public async Task Changes_RefusesCleanAndProcessFiltersWithoutExecutingAnyRepositoryProgram()
	{
		using var fixture = await HostileGitFixture.CreateAsync(TestContext.Current.CancellationToken);
		fixture.RunGit("config", "filter.hostile.clean", fixture.CreateMarkerCommand("clean"));
		fixture.RunGit("config", "filter.hostile.process", fixture.CreateMarkerCommand("process"));
		fixture.RunGit("config", "diff.external", fixture.CreateMarkerCommand("diff-external"));
		fixture.RunGit("config", "diff.hostile.command", fixture.CreateMarkerCommand("diff-command"));
		fixture.RunGit("config", "diff.hostile.textconv", fixture.CreateMarkerCommand("textconv"));
		fixture.RunGit("config", "log.showSignature", "true");
		fixture.RunGit("config", "gpg.program", fixture.CreateMarkerCommand("gpg"));
		fixture.RunGit("config", "core.pager", fixture.CreateMarkerCommand("pager"));
		fixture.RunGit("config", "alias.diff", "!" + fixture.CreateMarkerCommand("alias-diff"));
		fixture.RunGit("config", "alias.rev-parse", "!" + fixture.CreateMarkerCommand("alias-rev-parse"));
		fixture.RunGit("config", "credential.helper", fixture.CreateMarkerCommand("credential"));
		fixture.RunGit("config", "core.gitProxy", fixture.CreateMarkerCommand("proxy"));
		fixture.RunGit("config", "core.sshCommand", fixture.CreateMarkerCommand("ssh-command"));

		var changes = await new GitScopePathProvider().ResolveAsync(
			fixture.RepositoryPath,
			GitFilteringMode.Changes,
			diffRange: null,
			TestContext.Current.CancellationToken);

		Assert.False(changes.IsAvailable);
		Assert.Equal(GitScopeFilter.UnsafeFilterDiagnosticCode, changes.FailureDiagnosticCode);
		Assert.Equal("hostile", changes.FailureDetail);
		Assert.Contains("hostile", changes.FailureReason, StringComparison.Ordinal);
		Assert.Empty(Directory.EnumerateFiles(fixture.MarkersDirectory));

		var staged = await new GitScopePathProvider().ResolveAsync(
			fixture.RepositoryPath,
			GitFilteringMode.Staged,
			diffRange: null,
			TestContext.Current.CancellationToken);
		Assert.True(staged.IsAvailable, staged.FailureReason);
		var refDiff = await new GitScopePathProvider().ResolveAsync(
			fixture.RepositoryPath,
			GitFilteringMode.Diff,
			"HEAD~1..HEAD",
			TestContext.Current.CancellationToken);
		Assert.True(refDiff.IsAvailable, refDiff.FailureReason);
		Assert.Empty(Directory.EnumerateFiles(fixture.MarkersDirectory));
	}

	[Fact]
	public async Task ManagedCheckoutAndWorktreeUseEmptyHooksAndFilterOverrides()
	{
		using var fixture = await HostileGitFixture.CreateAsync(TestContext.Current.CancellationToken);
		var container = fixture.CreateManagedContainer();
		var basePath = Path.Combine(container, RepositoryCacheLayout.BaseDirectoryName);
		Directory.Move(fixture.RepositoryPath, basePath);
		fixture.RepositoryPath = basePath;
		var hookDirectory = Path.Combine(basePath, ".git", "hostile-hooks");
		Directory.CreateDirectory(hookDirectory);
		var hookMarker = fixture.CreateMarkerProgram("post-checkout", hookDirectory, "post-checkout");
		fixture.RunGit("config", "core.hooksPath", hookDirectory);
		fixture.RunGit("config", "filter.hostile.smudge", fixture.CreateMarkerCommand("smudge"));
		fixture.RunGit("config", "filter.hostile.process", fixture.CreateMarkerCommand("process"));

		await using var lease = await RepositoryFileLease.AcquireExclusiveAsync(
			RepositoryCacheLayout.GetBaseOperationLockPath(container, basePath),
			TestContext.Current.CancellationToken);
		var inspection = await GitRepositorySafetyInspector.InspectAsync(
			basePath,
			TestContext.Current.CancellationToken);
		Assert.True(inspection.IsComplete);

		await RunProtectedAsync(
			basePath,
			GitProcessOperation.ManagedCheckout(
				GitManagedCheckoutKind.Detach,
				"HEAD",
				filterDrivers: inspection.CheckoutFilterDrivers),
			TestContext.Current.CancellationToken);
		var worktreePath = Path.Combine(container, RepositoryCacheLayout.WorktreesDirectoryName, "1");
		Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
		await RunProtectedAsync(
			basePath,
			GitProcessOperation.ManagedWorktreeAdd(
				worktreePath,
				"HEAD",
				inspection.CheckoutFilterDrivers),
			TestContext.Current.CancellationToken);

		Assert.False(File.Exists(hookMarker));
		Assert.False(File.Exists(fixture.MarkerPath("smudge")));
		Assert.False(File.Exists(fixture.MarkerPath("process")));
	}

	[Fact]
	public async Task ExplicitNetworkRejectsMutableRepositoryTransportOverridesBeforeFetch()
	{
		using var fixture = await HostileGitFixture.CreateAsync(TestContext.Current.CancellationToken);
		var container = fixture.CreateManagedContainer("network-managed");
		var clonePath = Path.Combine(container, RepositoryCacheLayout.BaseDirectoryName);
		var service = new GitRepositoryService(allowFileTransportForTests: true);
		var clone = await service.CloneAsync(
			new Uri(fixture.RepositoryPath).AbsoluteUri,
			clonePath,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.True(clone.Success, clone.ErrorMessage);

		fixture.RunGitAt(clonePath, "config", "url.file:///redirected/.insteadOf", new Uri(fixture.RepositoryPath).AbsoluteUri);
		fixture.RunGitAt(clonePath, "config", "core.gitProxy", fixture.CreateMarkerCommand("git-proxy"));
		fixture.RunGitAt(clonePath, "config", "core.sshCommand", fixture.CreateMarkerCommand("ssh-command"));

		Assert.False(await service.PullUpdatesAsync(
			clonePath,
			cancellationToken: TestContext.Current.CancellationToken));
		Assert.Empty(Directory.EnumerateFiles(fixture.MarkersDirectory));
	}

	[Fact]
	public void TypedProfilesPinExecutableArgumentsAndEnvironment()
	{
		using var fixture = new TemporaryDirectory();
		var repository = fixture.CreateDirectory("repository");
		Directory.CreateDirectory(Path.Combine(repository, ".git"));
		var startInfo = GitProcessStartInfoFactory.Create(
			repository,
			GitProcessOperation.ReadWorkingChanges());

		Assert.True(Path.IsPathFullyQualified(startInfo.FileName));
		Assert.False(GitExecutableLocator.IsSafeForRepository(
			Path.Combine(repository, OperatingSystem.IsWindows() ? "git.exe" : "git"),
			repository));
		Assert.Equal("1", startInfo.Environment["GIT_CONFIG_NOSYSTEM"]);
		Assert.Equal("1", startInfo.Environment["GIT_ATTR_NOSYSTEM"]);
		Assert.Equal("0", startInfo.Environment["GIT_OPTIONAL_LOCKS"]);
		Assert.Equal("1", startInfo.Environment["GIT_NO_LAZY_FETCH"]);
		Assert.Equal(string.Empty, startInfo.Environment["GIT_ALLOW_PROTOCOL"]);
		Assert.Equal("0", startInfo.Environment["GIT_PROTOCOL_FROM_USER"]);
		Assert.False(startInfo.Environment.ContainsKey("SSH_AUTH_SOCK"));
		Assert.Contains("--no-pager", startInfo.ArgumentList);
		Assert.Contains("--no-optional-locks", startInfo.ArgumentList);
		Assert.Contains("core.fsmonitor=false", startInfo.ArgumentList);
		Assert.Contains("protocol.allow=never", startInfo.ArgumentList);
		Assert.Equal("diff", startInfo.ArgumentList[^6]);
		Assert.Equal("--", startInfo.ArgumentList[^1]);

		var localUrl = new Uri(repository).AbsoluteUri;
		Assert.Throws<ArgumentException>(() =>
			GitProcessOperation.CloneRepository(localUrl, fixture.CreateDirectory("strict-target")));
		foreach (var unsupported in new[]
		         {
			         "http://example.test/repository.git",
			         "git://example.test/repository.git",
			         "ext::hostile-helper"
		         })
		{
			Assert.Throws<ArgumentException>(() =>
				GitProcessOperation.CloneRepository(
					unsupported,
					fixture.CreateDirectory(Guid.NewGuid().ToString("N"))));
		}
		var explicitlyAllowed = GitProcessOperation.CloneRepository(
			localUrl,
			fixture.CreateDirectory("allowed-target"),
			allowFileTransport: true);
		Assert.Equal("file", explicitlyAllowed.AllowedProtocols);
	}

	private static async Task RunProtectedAsync(
		string workingDirectory,
		GitProcessOperation operation,
		CancellationToken cancellationToken)
	{
		using var process = new Process
		{
			StartInfo = GitProcessStartInfoFactory.Create(workingDirectory, operation)
		};
		Assert.True(process.Start());
		process.StandardInput.Close();
		var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var error = process.StandardError.ReadToEndAsync(cancellationToken);
		await process.WaitForExitAsync(cancellationToken);
		await Task.WhenAll(output, error);
		Assert.True(process.ExitCode == 0, await error);
	}

	private sealed class HostileGitFixture : IDisposable
	{
		private readonly TemporaryDirectory _temporary;

		private HostileGitFixture(TemporaryDirectory temporary, string repositoryPath, string markersDirectory)
		{
			_temporary = temporary;
			RepositoryPath = repositoryPath;
			MarkersDirectory = markersDirectory;
		}

		public string RepositoryPath { get; set; }
		public string MarkersDirectory { get; }

		public static async Task<HostileGitFixture> CreateAsync(CancellationToken cancellationToken)
		{
			var temporary = new TemporaryDirectory();
			var repository = temporary.CreateDirectory("repository");
			var markers = temporary.CreateDirectory("markers");
			var fixture = new HostileGitFixture(temporary, repository, markers);
			try
			{
				fixture.RunGit("init", "--initial-branch=main");
				fixture.RunGit("config", "user.name", "DevProjex Safety Tests");
				fixture.RunGit("config", "user.email", "safety@devprojex.local");
				await File.WriteAllTextAsync(
					Path.Combine(repository, ".gitattributes"),
					"tracked.txt filter=hostile diff=hostile\n",
					cancellationToken);
				await File.WriteAllTextAsync(Path.Combine(repository, "tracked.txt"), "one\n", cancellationToken);
				fixture.RunGit("add", ".gitattributes", "tracked.txt");
				fixture.RunGit("commit", "-m", "initial");
				await File.WriteAllTextAsync(Path.Combine(repository, "tracked.txt"), "two\n", cancellationToken);
				fixture.RunGit("add", "tracked.txt");
				fixture.RunGit("commit", "-m", "second");
				await File.AppendAllTextAsync(Path.Combine(repository, "tracked.txt"), "working\n", cancellationToken);
				await File.WriteAllTextAsync(Path.Combine(repository, "staged.txt"), "staged\n", cancellationToken);
				fixture.RunGit("add", "staged.txt");

				var nested = Path.Combine(repository, "nested");
				Directory.CreateDirectory(nested);
				fixture.RunGitAt(nested, "init");
				var linked = temporary.CreateDirectory("linked-parent");
				Directory.Delete(linked);
				fixture.RunGit("worktree", "add", "--detach", linked, "HEAD");
				return fixture;
			}
			catch
			{
				fixture.Dispose();
				throw;
			}
		}

		public string MarkerPath(string name) => Path.Combine(MarkersDirectory, name + ".marker");

		public string CreateMarkerCommand(string name) =>
			CreateMarkerProgram(name, _temporary.CreateDirectory("programs"), name + (OperatingSystem.IsWindows() ? ".ps1" : ".sh"));

		public string CreateMarkerProgram(string name, string directory, string fileName)
		{
			Directory.CreateDirectory(directory);
			var path = Path.Combine(directory, fileName);
			var marker = MarkerPath(name);
			if (OperatingSystem.IsWindows())
			{
				File.WriteAllText(
					path,
					$"Set-Content -LiteralPath '{marker.Replace("'", "''", StringComparison.Ordinal)}' -Value invoked\r\nexit 1\r\n",
					new UTF8Encoding(false));
				return $"powershell.exe -NoLogo -NoProfile -NonInteractive -File '{path.Replace('\\', '/')}'";
			}

			File.WriteAllText(
				path,
				$"#!/bin/sh\nprintf invoked > '{marker.Replace("'", "'\\''", StringComparison.Ordinal)}'\nexit 1\n",
				new UTF8Encoding(false));
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			return path;
		}

		public string CreateManagedContainer(string name = "managed")
		{
			var container = _temporary.CreateDirectory(name);
			File.WriteAllText(Path.Combine(container, RepositoryCacheLayout.MarkerFileName), "git");
			return container;
		}

		public void RunGit(params string[] arguments) => RunGitAt(RepositoryPath, arguments);

		public void RunGitAt(string workingDirectory, params string[] arguments)
		{
			var startInfo = new ProcessStartInfo(GitRuntime.GitExecutable)
			{
				WorkingDirectory = workingDirectory,
				UseShellExecute = false,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			foreach (var argument in arguments)
				startInfo.ArgumentList.Add(argument);
			using var process = Process.Start(startInfo);
			Assert.NotNull(process);
			process.StandardInput.Close();
			var output = process.StandardOutput.ReadToEnd();
			var error = process.StandardError.ReadToEnd();
			Assert.True(process.WaitForExit(30_000), "Fixture Git command timed out.");
			Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}{output}");
		}

		public void Dispose() => _temporary.Dispose();
	}
}
