using System.Diagnostics;

namespace DevProjex.Tests.Integration;

[Collection(GitNetworkTestCollection.Name)]
public sealed class GitRemoteSourceHardeningIntegrationTests
{
	[Theory]
	[InlineData("ssh://alice@git.example.test/team/repo.git", "ssh://bob@git.example.test/team/repo.git")]
	[InlineData("https://alice@git.example.test/team/repo.git", "https://bob@git.example.test/team/repo.git")]
	[InlineData("https://git.example.test/team/repo.git", "ssh://git.example.test/team/repo.git")]
	public void CacheIdentityPreservesUserAndTransport(string left, string right)
	{
		Assert.NotEqual(
			RepositoryUrlUtility.GetSourceCacheKey(left),
			RepositoryUrlUtility.GetSourceCacheKey(right));
	}

	[Theory]
	[InlineData("https://git.example.test/team/repo", "https://GIT.EXAMPLE.TEST/team/repo.git")]
	[InlineData("ssh://git@git.example.test/team/repo", "git@git.example.test:team/repo.git")]
	public void RemoteGitSuffixAndEquivalentSshFormsShareIdentity(string left, string right)
	{
		Assert.Equal(
			RepositoryUrlUtility.GetSourceCacheKey(left),
			RepositoryUrlUtility.GetSourceCacheKey(right));
	}

	[Fact]
	public void DistinctLocalGitDirectoriesDoNotShareCacheIdentity()
	{
		using var temporary = new TemporaryDirectory();
		var repository = temporary.CreateDirectory("repo");
		var bareRepository = temporary.CreateDirectory("repo.git");
		RunGit(repository, "init", "--initial-branch=main");
		RunGit(bareRepository, "init", "--bare", "--initial-branch=main");

		var repositoryUrl = new Uri(repository).AbsoluteUri;
		var bareRepositoryUrl = new Uri(bareRepository).AbsoluteUri;
		Assert.NotEqual(
			RepositoryUrlUtility.GetSourceCacheKey(repositoryUrl),
			RepositoryUrlUtility.GetSourceCacheKey(bareRepositoryUrl));

		using var cache = new TemporaryDirectory();
		using var service = new RepoCacheService(Path.Combine(cache.Path, "RepoCache"));
		var first = service.CreateRepositoryDirectory(repositoryUrl);
		var second = service.CreateRepositoryDirectory(bareRepositoryUrl);
		service.RecordIndexedRepository(repositoryUrl, first, "main");
		service.RecordIndexedRepository(bareRepositoryUrl, second, "main");
		Assert.Equal(2, service.ListIndexedRepositories().Count);
	}

	[Fact]
	public void LocalGitSuffixAliasSharesIdentityOnlyWhenItTargetsTheSameDirectory()
	{
		using var temporary = new TemporaryDirectory();
		var repository = temporary.CreateDirectory("repository");
		RunGit(repository, "init", "--initial-branch=main");
		var alias = Path.Combine(temporary.Path, "repository.git");
		try
		{
			Directory.CreateSymbolicLink(alias, repository);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			Assert.Skip($"Directory symbolic links are unavailable: {exception.GetType().Name}.");
		}

		Assert.Equal(
			RepositoryUrlUtility.GetSourceCacheKey(new Uri(repository).AbsoluteUri),
			RepositoryUrlUtility.GetSourceCacheKey(new Uri(alias).AbsoluteUri));
	}

	[Theory]
	[InlineData("http://example.test/team/repo.git")]
	[InlineData("git://example.test/team/repo.git")]
	[InlineData("file:///tmp/repo.git")]
	public void ProductionSourceValidatorRejectsDisallowedTransports(string source)
	{
		Assert.False(RepositoryUrlUtility.IsSupportedCloneSource(source));
	}

	[Fact]
	public void ExplicitNetworkPreservesOnlyTrustedProxyAndCertificateEnvironment()
	{
		var names = new[]
		{
			"HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY", "SSL_CERT_FILE", "SSL_CERT_DIR", "GIT_SSL_CAINFO"
		};
		var previous = names.ToDictionary(
			static name => name,
			static name => Environment.GetEnvironmentVariable(name),
			StringComparer.OrdinalIgnoreCase);
		var previousProxyCommand = Environment.GetEnvironmentVariable("GIT_PROXY_COMMAND");
		try
		{
			foreach (var name in names)
				Environment.SetEnvironmentVariable(name, $"trusted-{name.ToLowerInvariant()}");
			Environment.SetEnvironmentVariable("GIT_PROXY_COMMAND", "repository-controlled-command");

			using var temporary = new TemporaryDirectory();
			var operation = GitProcessOperation.CloneRepository(
				"https://example.test/team/repo.git",
				Path.Combine(temporary.Path, "clone"));
			var startInfo = GitProcessStartInfoFactory.Create(null, operation);

			foreach (var name in names)
				Assert.Equal($"trusted-{name.ToLowerInvariant()}", startInfo.Environment[name]);
			Assert.False(startInfo.Environment.ContainsKey("GIT_PROXY_COMMAND"));
			Assert.DoesNotContain("http.proxy=", startInfo.ArgumentList);
		}
		finally
		{
			foreach (var (name, value) in previous)
				Environment.SetEnvironmentVariable(name, value);
			Environment.SetEnvironmentVariable("GIT_PROXY_COMMAND", previousProxyCommand);
		}
	}

	[Fact]
	public async Task ActiveLocalBranchDeletedFromRemoteRemainsVisible()
	{
		using var temporary = new TemporaryDirectory();
		var source = temporary.CreateDirectory("source");
		RunGit(source, "init", "--initial-branch=main");
		RunGit(source, "config", "user.name", "DevProjex Tests");
		RunGit(source, "config", "user.email", "tests@devprojex.local");
		await File.WriteAllTextAsync(
			Path.Combine(source, "main.txt"),
			"main\n",
			TestContext.Current.CancellationToken);
		RunGit(source, "add", ".");
		RunGit(source, "commit", "-m", "main");
		RunGit(source, "checkout", "-b", "retained-local");
		await File.WriteAllTextAsync(
			Path.Combine(source, "branch.txt"),
			"branch\n",
			TestContext.Current.CancellationToken);
		RunGit(source, "add", ".");
		RunGit(source, "commit", "-m", "branch");
		var remote = Path.Combine(temporary.Path, "remote.git");
		RunGit(temporary.Path, "clone", "--bare", source, remote);

		var target = Path.Combine(temporary.Path, "managed", RepositoryCacheLayout.BaseDirectoryName);
		Directory.CreateDirectory(Path.GetDirectoryName(target)!);
		var service = new GitRepositoryService(allowFileTransportForTests: true);
		var clone = await service.CloneAsync(
			new Uri(remote).AbsoluteUri,
			target,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.True(clone.Success, clone.ErrorMessage);
		Assert.Equal("retained-local", await service.GetCurrentBranchAsync(
			target,
			TestContext.Current.CancellationToken));
		RunGit(remote, "update-ref", "-d", "refs/heads/retained-local");

		var branches = await service.GetBranchesAsync(
			target,
			TestContext.Current.CancellationToken);
		var retained = Assert.Single(branches, static branch => branch.Name == "retained-local");
		Assert.True(retained.IsActive);
		Assert.False(retained.IsRemote);
	}

	[Fact]
	public async Task RealGitCloneIsStoppedWhenCacheQuotaIsExceeded()
	{
		using var temporary = new TemporaryDirectory();
		var source = temporary.CreateDirectory("quota-source");
		RunGit(source, "init", "--initial-branch=main");
		RunGit(source, "config", "user.name", "DevProjex Tests");
		RunGit(source, "config", "user.email", "tests@devprojex.local");
		var random = new byte[2 * 1024 * 1024];
		Random.Shared.NextBytes(random);
		await File.WriteAllBytesAsync(
			Path.Combine(source, "large.bin"),
			random,
			TestContext.Current.CancellationToken);
		RunGit(source, "add", ".");
		RunGit(source, "commit", "-m", "large source");
		var remote = Path.Combine(temporary.Path, "quota-remote.git");
		RunGit(temporary.Path, "clone", "--bare", source, remote);
		var progressFrames = new List<string>();
		var limits = new GitRepositoryResourceLimits
		{
			MaximumRepositoryBytes = 64 * 1024,
			FreeSpaceReserveBytes = 0,
			PollInterval = TimeSpan.FromMilliseconds(1),
			AvailableFreeSpace = static _ => long.MaxValue
		};
		using var service = new GitRepositoryService(
			allowFileTransportForTests: true,
			retainTestManagedMarker: true,
			limits);
		var target = Path.Combine(temporary.Path, "quota-target");

		var result = await service.CloneAsync(
			new Uri(remote).AbsoluteUri,
			target,
			new SynchronousProgress(progressFrames.Add),
			TestContext.Current.CancellationToken);

		Assert.False(result.Success);
		Assert.Equal(GitRepositoryService.CacheQuotaDiagnostic, result.ErrorMessage);
		Assert.Contains(GitRepositoryService.CacheQuotaDiagnostic, progressFrames);
	}

	[Fact]
	public async Task CloneFailsBeforeStartingWhenDestinationReserveIsUnavailable()
	{
		using var temporary = new TemporaryDirectory();
		var target = Path.Combine(temporary.Path, "reserve-target");
		var limits = new GitRepositoryResourceLimits
		{
			MaximumRepositoryBytes = 1024,
			FreeSpaceReserveBytes = 1,
			AvailableFreeSpace = static _ => 0
		};
		using var service = new GitRepositoryService(
			allowFileTransportForTests: true,
			retainTestManagedMarker: true,
			limits);

		var result = await service.CloneAsync(
			new Uri(temporary.Path).AbsoluteUri,
			target,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.False(result.Success);
		Assert.Equal(GitRepositoryService.CacheReserveDiagnostic, result.ErrorMessage);
		Assert.False(Directory.Exists(target));
	}

	[Fact]
	public void FreeSpaceProbeChoosesTheLongestDestinationMount()
	{
		using var temporary = new TemporaryDirectory();
		var targetMount = temporary.CreateDirectory("mounted-cache");
		var destination = Path.Combine(targetMount, "staging", "repository");
		var root = Path.GetPathRoot(temporary.Path)!;

		var selected = GitRepositoryResourceLimits.ResolveDestinationDriveRoot(
			destination,
			[root, targetMount]);

		Assert.Equal(Path.GetFullPath(targetMount), selected, PathComparer.Default);
	}

	private static void RunGit(string workingDirectory, params string[] arguments)
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
		Assert.True(process.WaitForExit(30_000), "Git fixture command timed out.");
		Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}{output}");
	}

	private sealed class SynchronousProgress(Action<string> report) : IProgress<string>
	{
		public void Report(string value) => report(value);
	}
}
