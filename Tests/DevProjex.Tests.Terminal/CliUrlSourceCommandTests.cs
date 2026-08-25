using System.Diagnostics;
using DevProjex.Infrastructure.Git;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Tests.Terminal;

public sealed class CliUrlSourceCommandTests
{
	[Theory]
	[InlineData(false, true, false)]
	[InlineData(true, false, false)]
	[InlineData(true, true, true)]
	public async Task NonInteractiveTuiUrlFailsBeforeGitOrCacheAccess(
		bool isInputInteractive,
		bool isOutputInteractive,
		bool isTermDumb)
	{
		using var data = new TemporaryDirectory();
		var git = new CountingGitRepositoryService();
		var services = new TerminalServiceFactory(() => data.Path).Create(AppLanguage.En) with
		{
			GitRepositoryService = git
		};
		var environment = new TestTerminalEnvironment
		{
			IsInputInteractive = isInputInteractive,
			IsOutputInteractive = isOutputInteractive,
			IsTermDumb = isTermDumb
		};

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(_ => services))
			.RunAsync(
				["tui", "https://github.com/example/repository.git", "--language", "en"],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Equal(0, git.CallCount);
		Assert.Empty(services.RepoCacheService.ListCacheEntriesForManagement().Entries);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-TUI-NOT-INTERACTIVE", environment.StandardError, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("analyze")]
	[InlineData("context")]
	[InlineData("project")]
	[InlineData("open")]
	public async Task InvalidSelectionFileFailsBeforeGitOrCacheAccess(string command)
	{
		using var data = new TemporaryDirectory();
		var git = new CountingGitRepositoryService();
		var services = new TerminalServiceFactory(() => data.Path).Create(AppLanguage.En) with
		{
			GitRepositoryService = git
		};
		var environment = new TestTerminalEnvironment();
		var missingSelectionFile = Path.Combine(data.Path, "missing-selection.txt");
		var repositoryUrl = "https://github.com/example/repository.git";
		var arguments = command switch
		{
			"analyze" => new[] { "analyze", repositoryUrl },
			"context" => new[] { "export", "context", repositoryUrl },
			"project" =>
			[
				"export", "project", repositoryUrl,
				"--as", "zip", "-o", Path.Combine(data.Path, "output.zip")
			],
			"open" => new[] { "open", repositoryUrl },
			_ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
		};

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(_ => services))
			.RunAsync(
				[.. arguments, "--select-from", missingSelectionFile],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Equal(0, git.CallCount);
		Assert.Empty(services.RepoCacheService.ListCacheEntriesForManagement().Entries);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-CLI-SELECT-FROM-INVALID", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExistingUnixDirectoryWithColonIsAnalyzedLocallyWithoutClone()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("Colon is not valid in a Windows directory name.");

		using var workspace = new TemporaryDirectory();
		using var data = new TemporaryDirectory();
		var projectPath = workspace.CreateDirectory("repo.name:copy");
		File.WriteAllText(Path.Combine(projectPath, "app.txt"), "content\n");
		var git = new CountingGitRepositoryService();
		var services = new TerminalServiceFactory(() => data.Path).Create(AppLanguage.En) with
		{
			GitRepositoryService = git
		};
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(_ => services))
			.RunAsync(
				["analyze", projectPath, "--git-mode", "none", "--format", "json", "-o", "-"],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(0, git.CallCount);
		using var report = JsonDocument.Parse(environment.StandardOutput);
		Assert.Equal(1, report.RootElement.GetProperty("inventory").GetProperty("files").GetInt32());
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task UrlSourceClonesReusesCacheSelectsBranchAndRecordsRecentHistory()
	{
		if (!IsGitAvailable())
			Assert.Skip("Git is unavailable on this test host.");

		using var workspace = new TemporaryDirectory();
		using var data = new TemporaryDirectory();
		var source = workspace.CreateDirectory("source");
		RunGit(source, "init", "--initial-branch=main");
		RunGit(source, "config", "user.email", "terminal-tests@devprojex.local");
		RunGit(source, "config", "user.name", "DevProjex Terminal Tests");
		workspace.WriteFile("source/main.txt", "main\n");
		RunGit(source, "add", ".");
		RunGit(source, "commit", "-m", "main");
		RunGit(source, "checkout", "-b", "feature/test");
		workspace.WriteFile("source/feature.txt", "feature\n");
		RunGit(source, "add", ".");
		RunGit(source, "commit", "-m", "feature");
		RunGit(source, "checkout", "main");
		var bare = Path.Combine(workspace.Path, "origin.git");
		RunGit(workspace.Path, "clone", "--bare", source, bare);
		var repositoryUrl = new Uri(bare + Path.DirectorySeparatorChar).AbsoluteUri;
		var factory = new TerminalServiceFactory(() => data.Path);

		using var main = await AnalyzeAsync(factory, repositoryUrl, "main");
		using var feature = await AnalyzeAsync(factory, repositoryUrl, "feature/test");
		Assert.Equal(1, main.RootElement.GetProperty("inventory").GetProperty("files").GetInt32());
		Assert.Equal(2, feature.RootElement.GetProperty("inventory").GetProperty("files").GetInt32());
		var missingBranchEnvironment = new TestTerminalEnvironment();
		var missingBranchExitCode = await new TerminalApplication(missingBranchEnvironment, factory)
			.RunAsync(
				["analyze", repositoryUrl, "--branch", "missing-branch", "--format", "json"],
				TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.RuntimeError, missingBranchExitCode);
		Assert.Empty(missingBranchEnvironment.StandardOutput);
		Assert.Contains(
			"DPX-CLI-GIT-BRANCH-UNAVAILABLE",
			missingBranchEnvironment.StandardError,
			StringComparison.Ordinal);

		var cache = new RepoCacheService(Path.Combine(data.Path, "RepoCache"));
		Assert.Single(cache.ListIndexedRepositories());
		var offlineOrigin = bare + ".offline";
		Directory.Move(bare, offlineOrigin);
		using var offline = await AnalyzeAsync(factory, repositoryUrl, "main");
		Assert.Equal(1, offline.RootElement.GetProperty("inventory").GetProperty("files").GetInt32());
		Assert.Single(cache.ListIndexedRepositories());

		var recentEnvironment = new TestTerminalEnvironment();
		var recentExitCode = await new TerminalApplication(recentEnvironment, factory).RunAsync(
			["recent", "--kind", "repository", "--format", "json"],
			TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.Success, recentExitCode);
		using var recent = JsonDocument.Parse(recentEnvironment.StandardOutput);
		var item = Assert.Single(recent.RootElement.GetProperty("items").EnumerateArray());
		Assert.Equal("repository", item.GetProperty("kind").GetString());
		Assert.Equal(repositoryUrl.TrimEnd('/'), item.GetProperty("url").GetString()?.TrimEnd('/'));
	}

	[Fact]
	public async Task BranchWithLocalProjectFailsInsteadOfBeingIgnored()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("app.txt", "content\n");
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["analyze", workspace.Path, "--branch", "main"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-CLI-GIT-BRANCH-LOCAL", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UrlSourceFlowsThroughContextAndProjectExportsUsingTheManagedCache()
	{
		if (!IsGitAvailable())
			Assert.Skip("Git is unavailable on this test host.");

		using var workspace = new TemporaryDirectory();
		using var data = new TemporaryDirectory();
		var source = workspace.CreateDirectory("export-source");
		RunGit(source, "init", "--initial-branch=main");
		RunGit(source, "config", "user.email", "terminal-tests@devprojex.local");
		RunGit(source, "config", "user.name", "DevProjex Terminal Tests");
		workspace.WriteFile("export-source/src/remote.cs", "internal sealed class RemoteMarker {}\n");
		RunGit(source, "add", ".");
		RunGit(source, "commit", "-m", "initial");
		var bare = Path.Combine(workspace.Path, "export-origin.git");
		RunGit(workspace.Path, "clone", "--bare", source, bare);
		var repositoryUrl = new Uri(bare + Path.DirectorySeparatorChar).AbsoluteUri;
		var factory = new TerminalServiceFactory(() => data.Path);
		var contextEnvironment = new TestTerminalEnvironment();

		var contextExitCode = await new TerminalApplication(contextEnvironment, factory).RunAsync(
			[
				"export", "context", repositoryUrl,
				"--git-mode", "none", "--view", "content", "--format", "text", "-o", "-", "--plain",
				"--progress", "never"
			],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, contextExitCode);
		Assert.Contains("internal sealed class RemoteMarker", contextEnvironment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(contextEnvironment.StandardError);

		var destination = Path.Combine(workspace.Path, "exported-project");
		var projectEnvironment = new TestTerminalEnvironment();
		var projectExitCode = await new TerminalApplication(projectEnvironment, factory).RunAsync(
			[
				"export", "project", repositoryUrl,
				"--git-mode", "none", "--as", "folder", "-o", destination,
				"--progress", "never"
			],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, projectExitCode);
		Assert.Equal(
			"internal sealed class RemoteMarker {}\n",
			File.ReadAllText(Path.Combine(destination, "src", "remote.cs")).ReplaceLineEndings("\n"));
		Assert.Empty(projectEnvironment.StandardError);
		Assert.Single(new RepoCacheService(Path.Combine(data.Path, "RepoCache")).ListIndexedRepositories());
	}

	[Fact]
	public async Task RedirectedUrlCloneProgressNeverContaminatesContextPayloadAndStaysBounded()
	{
		if (!IsGitAvailable())
			Assert.Skip("Git is unavailable on this test host.");

		using var workspace = new TemporaryDirectory();
		using var data = new TemporaryDirectory();
		var source = workspace.CreateDirectory("progress-source");
		RunGit(source, "init", "--initial-branch=main");
		RunGit(source, "config", "user.email", "terminal-tests@devprojex.local");
		RunGit(source, "config", "user.name", "DevProjex Terminal Tests");
		workspace.WriteFile("progress-source/src/payload.cs", "internal sealed class PayloadMarker {}\n");
		RunGit(source, "add", ".");
		RunGit(source, "commit", "-m", "initial");
		var bare = Path.Combine(workspace.Path, "progress-origin.git");
		RunGit(workspace.Path, "clone", "--bare", source, bare);
		var repositoryUrl = new Uri(bare + Path.DirectorySeparatorChar).AbsoluteUri;
		var quietEnvironment = new TestTerminalEnvironment();
		var progressEnvironment = new TestTerminalEnvironment();
		string[] commonArguments =
		[
			"export", "context", repositoryUrl,
			"--git-mode", "none", "--view", "content", "--format", "text", "-o", "-", "--plain",
			"--language", "en"
		];

		var factory = new TerminalServiceFactory(() => data.Path);
		var progressExitCode = await new TerminalApplication(
				progressEnvironment,
				factory)
			.RunAsync(commonArguments, TestContext.Current.CancellationToken);
		var quietExitCode = await new TerminalApplication(
				quietEnvironment,
				factory)
			.RunAsync(
				[.. commonArguments, "--progress", "never"],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, quietExitCode);
		Assert.Equal(CommandLineExitCodes.Success, progressExitCode);
		Assert.Equal(quietEnvironment.StandardOutput, progressEnvironment.StandardOutput);
		Assert.Empty(quietEnvironment.StandardError);
		var progressLines = progressEnvironment.StandardError
			.ReplaceLineEndings("\n")
			.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		Assert.InRange(progressLines.Length, 2, 6);
		Assert.StartsWith("Cloning ", progressLines[0], StringComparison.Ordinal);
		Assert.Equal("Clone completed.", progressLines[^1]);
	}

	[Fact]
	public async Task MissingFileRemoteReturnsRuntimeFailureWithoutPayload()
	{
		using var workspace = new TemporaryDirectory();
		using var data = new TemporaryDirectory();
		var missing = Path.Combine(workspace.Path, "missing.git");
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => data.Path))
			.RunAsync(
				["analyze", new Uri(missing).AbsoluteUri, "--format", "json"],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.RuntimeError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-CLI-GIT-CLONE-FAILED", environment.StandardError, StringComparison.Ordinal);
		var cacheRoot = Path.Combine(data.Path, "RepoCache");
		Assert.Empty(new RepoCacheService(cacheRoot).ListIndexedRepositories());
	}

	[Fact]
	public async Task CancellationDuringCloneRemovesPartialStagingDirectory()
	{
		using var data = new TemporaryDirectory();
		var clone = new BlockingCloneService();
		var services = new TerminalServiceFactory(() => data.Path).Create(AppLanguage.En) with
		{
			GitRepositoryService = clone
		};
		var environment = new TestTerminalEnvironment();
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		var resolver = new TerminalProjectSourceResolver(
			services,
			environment,
			new TerminalOutputOptions());
		var operation = resolver.ResolveAsync(
			"https://github.com/example/cancel.git",
			branch: null,
			cancellation.Token);
		await clone.Started.Task.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);

		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
		var stagingRoot = Path.Combine(data.Path, "RepoCache", ".staging");
		Assert.False(Directory.Exists(clone.TargetDirectory));
		Assert.Empty(
			Directory.Exists(stagingRoot)
				? Directory.EnumerateDirectories(stagingRoot)
					.Where(static path => !Path.GetFileName(path).Equals(".trash", StringComparison.Ordinal))
				: []);
	}

	private static async Task<JsonDocument> AnalyzeAsync(
		TerminalServiceFactory factory,
		string repositoryUrl,
		string branch)
	{
		var environment = new TestTerminalEnvironment();
		var exitCode = await new TerminalApplication(environment, factory).RunAsync(
			[
				"analyze", repositoryUrl,
				"--branch", branch,
				"--git-mode", "none",
				"--format", "json", "-o", "-", "--progress", "never"
			],
			TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardError);
		return JsonDocument.Parse(environment.StandardOutput);
	}

	private static bool IsGitAvailable()
	{
		try
		{
			using var process = Process.Start(new ProcessStartInfo("git", "--version")
			{
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true
			});
			process?.WaitForExit(5_000);
			return process is { HasExited: true, ExitCode: 0 };
		}
		catch
		{
			return false;
		}
	}

	private static void RunGit(string workingDirectory, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		var result = TerminalTestProcess.Run(startInfo);
		Assert.True(
			result.ExitCode == 0,
			$"git {string.Join(' ', arguments)} failed: {result.StandardOutput}{result.StandardError}");
	}

	private sealed class BlockingCloneService : IGitRepositoryService
	{
		public TaskCompletionSource Started { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public string TargetDirectory { get; private set; } = string.Empty;

		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) =>
			Task.FromResult(true);

		public async Task<GitCloneResult> CloneAsync(
			string url,
			string targetDirectory,
			IProgress<string>? progress = null,
			CancellationToken cancellationToken = default)
		{
			TargetDirectory = targetDirectory;
			File.WriteAllText(Path.Combine(targetDirectory, "partial.pack"), "partial");
			Started.TrySetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			throw new UnreachableException();
		}

		public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<string?> GetDefaultBranchAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<bool> SwitchBranchAsync(
			string repositoryPath,
			string branchName,
			IProgress<string>? progress = null,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<bool> PullUpdatesAsync(
			string repositoryPath,
			IProgress<string>? progress = null,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<string?> GetHeadCommitAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<string?> GetCurrentBranchAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<string?> GetRemoteUrlAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class CountingGitRepositoryService : IGitRepositoryService
	{
		public int CallCount { get; private set; }

		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default)
		{
			CallCount++;
			return Task.FromResult(true);
		}

		public Task<GitCloneResult> CloneAsync(
			string url,
			string targetDirectory,
			IProgress<string>? progress = null,
			CancellationToken cancellationToken = default) => Unexpected<GitCloneResult>();

		public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) => Unexpected<IReadOnlyList<GitBranch>>();

		public Task<string?> GetDefaultBranchAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) => Unexpected<string?>();

		public Task<bool> SwitchBranchAsync(
			string repositoryPath,
			string branchName,
			IProgress<string>? progress = null,
			CancellationToken cancellationToken = default) => Unexpected<bool>();

		public Task<bool> PullUpdatesAsync(
			string repositoryPath,
			IProgress<string>? progress = null,
			CancellationToken cancellationToken = default) => Unexpected<bool>();

		public Task<string?> GetHeadCommitAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) => Unexpected<string?>();

		public Task<string?> GetCurrentBranchAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) => Unexpected<string?>();

		public Task<string?> GetRemoteUrlAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) => Unexpected<string?>();

		private Task<T> Unexpected<T>()
		{
			CallCount++;
			throw new InvalidOperationException("Git must not be called for this source.");
		}
	}
}
