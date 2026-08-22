using System.Diagnostics;
using DevProjex.Infrastructure.Git;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Tests.Terminal;

public sealed class CliUrlSourceCommandTests
{
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
				"--format", "json", "-o", "-"
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
		using var process = new Process
		{
			StartInfo = new ProcessStartInfo("git")
			{
				WorkingDirectory = workingDirectory,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true
			}
		};
		foreach (var argument in arguments)
			process.StartInfo.ArgumentList.Add(argument);
		process.Start();
		var standardOutput = process.StandardOutput.ReadToEnd();
		var standardError = process.StandardError.ReadToEnd();
		process.WaitForExit();
		Assert.True(
			process.ExitCode == 0,
			$"git {string.Join(' ', arguments)} failed: {standardOutput}{standardError}");
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
}
