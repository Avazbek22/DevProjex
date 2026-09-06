namespace DevProjex.Tests.Unit;

/// <summary>
/// Unit tests for GitRepositoryService process and parsing contracts without requiring Git.
/// </summary>
public class GitRepositoryServiceUnitTests
{
    private readonly GitRepositoryService _service = new();

    [Fact]
    public void GitCommandsUseNonInteractiveStandardTransports()
    {
        var startInfo = GitRepositoryService.CreateGitCommandStartInfo(
            workingDirectory: null,
			operation: GitProcessOperation.ReadRemoteUrl());

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal("0", startInfo.Environment["GIT_TERMINAL_PROMPT"]);
		Assert.False(startInfo.Environment.ContainsKey("GIT_SSH_COMMAND"));
        Assert.Equal(string.Empty, startInfo.Environment["GIT_ASKPASS"]);
        Assert.Equal(string.Empty, startInfo.Environment["SSH_ASKPASS"]);
        Assert.Equal("never", startInfo.Environment["SSH_ASKPASS_REQUIRE"]);
        Assert.Equal("Never", startInfo.Environment["GCM_INTERACTIVE"]);
        Assert.Equal("false", startInfo.Environment["GCM_GUI_PROMPT"]);
		Assert.Contains("--no-pager", startInfo.ArgumentList);
		Assert.Contains("core.fsmonitor=false", startInfo.ArgumentList);
		Assert.Contains("config", startInfo.ArgumentList);
		Assert.Contains("remote.origin.url", startInfo.ArgumentList);
    }

	[Fact]
	public async Task WorktreeSupportProbe_TransientFailureIsReportedAndSuccessIsCached()
	{
		var calls = 0;
		var manager = new GitWorktreeManager(_ => Task.FromResult(
			Interlocked.Increment(ref calls) == 1
				? WorktreeSupportState.TransientFailure
				: WorktreeSupportState.Supported));

		Assert.Equal(
			WorktreeSupportState.TransientFailure,
			await manager.GetSupportStateAsync("repo", TestContext.Current.CancellationToken));
		Assert.Equal(
			WorktreeSupportState.Supported,
			await manager.GetSupportStateAsync("repo", TestContext.Current.CancellationToken));
		Assert.Equal(
			WorktreeSupportState.Supported,
			await manager.GetSupportStateAsync("repo", TestContext.Current.CancellationToken));
		Assert.Equal(2, calls);
	}

	[Fact]
	public async Task WorktreeProcessOutput_AtLimitIsPreserved()
	{
		const int limit = 257;
		var expected = new string('x', limit);
		using var reader = new StringReader(expected);

		var result = await GitProcessOutputReader.ReadAsync(
			reader,
			limit,
			TestContext.Current.CancellationToken);

		Assert.False(result.ExceededLimit);
		Assert.Equal(expected, result.Text);
	}

	[Fact]
	public async Task WorktreeProcessOutput_OverLimitIsDrainedAndRejected()
	{
		const int limit = 257;
		using var reader = new StringReader(new string('x', limit + 4096));

		var result = await GitProcessOutputReader.ReadAsync(
			reader,
			limit,
			TestContext.Current.CancellationToken);

		Assert.True(result.ExceededLimit);
		Assert.Empty(result.Text);
		Assert.Equal(-1, reader.Peek());
	}

	[Fact]
	public async Task WorktreeProcessOutput_CancellationObservationWaitsForPipeCleanup()
	{
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var reader = Task.Run(
			async () =>
			{
				await release.Task;
				throw new IOException("Pipe closed during process cancellation.");
			},
			TestContext.Current.CancellationToken);

		var observation = GitProcessOutputReader.ObserveCompletionAsync(reader);
		Assert.False(observation.IsCompleted);

		release.SetResult();
		await observation;
		Assert.True(reader.IsFaulted);
	}

	[Fact]
	public async Task ExitedProcessOutputDrainClosesStuckReadersWithinTheBoundedCleanup()
	{
		var reader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var closeCount = 0;

		var drainedNormally = await GitProcessOutputReader.WaitForCompletionAfterExitAsync(
			() =>
			{
				Interlocked.Increment(ref closeCount);
				reader.TrySetException(new IOException("Pipe closed."));
			},
			TimeSpan.FromMilliseconds(50),
			reader.Task);

		Assert.False(drainedNormally);
		Assert.Equal(1, closeCount);
		Assert.True(reader.Task.IsFaulted);
	}

	[Fact]
	public async Task WorktreeSupportProbe_FaultedProbeIsRetriedOnNextRequest()
	{
		var calls = 0;
		var manager = new GitWorktreeManager(_ =>
		{
			calls++;
			return calls == 1
				? Task.FromException<WorktreeSupportState>(new IOException("transient probe failure"))
				: Task.FromResult(WorktreeSupportState.Supported);
		});

		await Assert.ThrowsAsync<IOException>(() => manager.GetSupportStateAsync("repo", CancellationToken.None));
		Assert.Equal(
			WorktreeSupportState.Supported,
			await manager.GetSupportStateAsync("repo", CancellationToken.None));
		Assert.Equal(
			WorktreeSupportState.Supported,
			await manager.GetSupportStateAsync("repo", CancellationToken.None));
		Assert.Equal(2, calls);
	}

	[Fact]
	public async Task WorktreeSupportProbe_TransientResultAfterWaiterCancellationIsRetried()
	{
		var calls = 0;
		var firstProbe = new TaskCompletionSource<WorktreeSupportState>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var manager = new GitWorktreeManager(_ => Interlocked.Increment(ref calls) == 1
			? firstProbe.Task
			: Task.FromResult(WorktreeSupportState.Supported));
		using var cancellation = new CancellationTokenSource();

		var canceledWaiter = manager.GetSupportStateAsync("repository", cancellation.Token);
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter);
		firstProbe.SetResult(WorktreeSupportState.TransientFailure);
		await firstProbe.Task;

		Assert.Equal(
			WorktreeSupportState.Supported,
			await manager.GetSupportStateAsync("repository", TestContext.Current.CancellationToken));
		Assert.Equal(2, calls);
	}

	[Fact]
	public async Task WorktreeSupportProbe_TimeoutIsTransientAndNextRequestStartsNewProbe()
	{
		var probeCount = 0;
		var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var manager = new GitWorktreeManager(
			async (_, _, cancellationToken) =>
			{
				var attempt = Interlocked.Increment(ref probeCount);
				if (attempt > 1)
					return new GitWorktreeManager.GitProcessResult(0, string.Empty, string.Empty);
				try
				{
					await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					cancellationObserved.TrySetResult();
					throw;
				}
				return new GitWorktreeManager.GitProcessResult(-1, string.Empty, string.Empty);
			},
			TimeSpan.FromMilliseconds(50));

		Assert.Equal(
			WorktreeSupportState.TransientFailure,
			await manager.GetSupportStateAsync("repository", TestContext.Current.CancellationToken));
		await cancellationObserved.Task.WaitAsync(
			TimeSpan.FromSeconds(2),
			TestContext.Current.CancellationToken);
		Assert.Equal(
			WorktreeSupportState.Supported,
			await manager.GetSupportStateAsync("repository", TestContext.Current.CancellationToken));
		Assert.Equal(2, probeCount);
	}

	[Fact]
	public async Task WorktreeSupportProbe_TransientProcessStartFailureIsRetried()
	{
		var probeCount = 0;
		var manager = new GitWorktreeManager(
			(_, _, _) => Interlocked.Increment(ref probeCount) == 1
				? Task.FromException<GitWorktreeManager.GitProcessResult>(
					new System.ComponentModel.Win32Exception(5))
				: Task.FromResult(new GitWorktreeManager.GitProcessResult(0, string.Empty, string.Empty)),
			TimeSpan.FromSeconds(1));

		Assert.Equal(
			WorktreeSupportState.TransientFailure,
			await manager.GetSupportStateAsync("repository", TestContext.Current.CancellationToken));
		Assert.Equal(
			WorktreeSupportState.Supported,
			await manager.GetSupportStateAsync("repository", TestContext.Current.CancellationToken));
		Assert.Equal(2, probeCount);
	}

	[Fact]
	public async Task WorktreeSupportProbe_MissingGitExecutableIsCachedAsPermanent()
	{
		var probeCount = 0;
		var manager = new GitWorktreeManager(
			(_, _, _) =>
			{
				Interlocked.Increment(ref probeCount);
				return Task.FromException<GitWorktreeManager.GitProcessResult>(
					new System.ComponentModel.Win32Exception(2));
			},
			TimeSpan.FromSeconds(1));

		Assert.Equal(
			WorktreeSupportState.PermanentUnsupported,
			await manager.GetSupportStateAsync("repository", TestContext.Current.CancellationToken));
		Assert.Equal(
			WorktreeSupportState.PermanentUnsupported,
			await manager.GetSupportStateAsync("repository", TestContext.Current.CancellationToken));
		Assert.Equal(1, probeCount);
	}

	[Fact]
	public async Task DetachedWorktree_VerificationFailureIsNotMaskedByStalledCleanup()
	{
		var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var repositoryPath = Path.GetFullPath("repository");
		var worktreePath = Path.GetFullPath("worktree");
		var manager = new GitWorktreeManager(
			async (workingDirectory, arguments, cancellationToken) =>
			{
				if (arguments is ["rev-parse", "--verify", "--quiet", "--end-of-options", _] &&
				    !string.Equals(workingDirectory, worktreePath, StringComparison.Ordinal))
					return new GitWorktreeManager.GitProcessResult(0, "0123456789abcdef\n", string.Empty);
				if (arguments is ["worktree", "add", ..])
					return new GitWorktreeManager.GitProcessResult(0, string.Empty, string.Empty);
				if (arguments is ["rev-parse", "--verify", "--quiet", "--end-of-options", _])
					throw new IOException("verification failed");
				if (arguments is ["worktree", "remove", ..])
				{
					cleanupStarted.TrySetResult();
					await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				}
				throw new InvalidOperationException("Unexpected git command.");
			},
			TimeSpan.FromSeconds(1),
			TimeSpan.FromMilliseconds(25));

		var exception = await Assert.ThrowsAsync<IOException>(() => manager.CreateDetachedAsync(
			repositoryPath,
			worktreePath,
			branch: null,
			TestContext.Current.CancellationToken));

		Assert.Equal("verification failed", exception.Message);
		Assert.True(cleanupStarted.Task.IsCompletedSuccessfully);
	}

	[Theory]
	[InlineData("feature/space+plus")]
	[InlineData("release/v1.2@beta")]
	[InlineData("feature.LOCK")]
	[InlineData("feature/next\u0085checkpoint")]
	[InlineData("@")]
	[InlineData("тема/исправление")]
	public void BranchNameValidator_AcceptsValidUnusualNames(string branchName)
	{
		Assert.True(GitBranchNameValidator.IsValid(branchName));
		Assert.Equal(branchName, GitBranchNameValidator.ValidateAndNormalize(branchName));
	}

	[Theory]
	[InlineData("-feature")]
	[InlineData("feature..next")]
	[InlineData("feature lock")]
	[InlineData("feature/@{next")]
	[InlineData("feature/.hidden")]
	[InlineData("feature.lock")]
	[InlineData("feature\\name")]
	[InlineData("feature/next\u001Fcheckpoint")]
	[InlineData("feature/next\u007Fcheckpoint")]
	public void BranchNameValidator_RejectsInvalidNamesBeforeCommandConstruction(string branchName)
	{
		Assert.False(GitBranchNameValidator.IsValid(branchName));
		Assert.Throws<ArgumentException>(() => GitBranchNameValidator.ValidateAndNormalize(branchName));
	}

	[Theory]
	[InlineData("https://github.com/user/repo", "repo")]
	[InlineData("https://github.com/user/my-repo.git", "my-repo")]
	[InlineData("git@github.com:user/repo.git", "repo")]
	[InlineData("https://github.com/org/project-name?tab=readme", "project-name")]
	public void ExtractRepositoryName_UsesTheCloneMetadataParser(string url, string expectedName)
	{
		Assert.Equal(expectedName, GitRepositoryService.ExtractRepositoryName(url));
	}

	[Theory]
	[InlineData("fatal: repository 'https://github.com/user/repo' not found", "Invalid repository URL or repository does not exist")]
	[InlineData("fatal: unable to access: Could not resolve host", "Network error - check your internet connection")]
	[InlineData("fatal: Authentication failed for private repository", "Authentication failed - repository may be private")]
	[InlineData("Permission denied (publickey)", "Authentication failed - repository may be private")]
	[InlineData("operation timed out", "Connection timeout - repository may be too large or network is slow")]
	[InlineData("", "Clone failed")]
	public void ParseGitCloneError_MapsKnownFailureClasses(string gitError, string expected)
	{
		Assert.Equal(expected, GitRepositoryService.ParseGitCloneError(gitError));
	}

	[Fact]
	public void ParseGitCloneError_DoesNotEchoUnknownAuthenticatedStderr()
	{
		const string stderr = "fatal: transport rejected https://user:token@example.test/repo";

		var result = GitRepositoryService.ParseGitCloneError(stderr);

		Assert.Equal("Clone failed", result);
		Assert.DoesNotContain("token", result, StringComparison.Ordinal);
	}

    #region Default Branch Detection Tests

    [Fact]
    public void GetDefaultBranchAsync_UsesCommonDefaults()
    {
        Assert.Equal(
            "release/v1",
            GitRepositoryService.ResolveRemoteHeadBranch("refs/remotes/origin/release/v1\n"));
        Assert.Equal(
            "main",
            GitRepositoryService.ResolveCommonDefaultBranch("  origin/master\n  origin/main\n"));
        Assert.Equal(
            "master",
            GitRepositoryService.ResolveCommonDefaultBranch("  origin/master\n"));
        Assert.Equal(
            "main",
            GitRepositoryService.ResolveCommonDefaultBranch("  origin/HEAD -> origin/main\n"));
    }

    [Theory]
    [InlineData("origin/mainline")]
    [InlineData("origin/masterpiece")]
    [InlineData("origin/user/main")]
    public void GetDefaultBranchAsync_DoesNotMatchBranchNameSubstrings(string remoteBranch)
    {
        Assert.Null(GitRepositoryService.ResolveCommonDefaultBranch(remoteBranch));
    }

    [Theory]
    [InlineData("")]
    [InlineData("refs/remotes/upstream/main")]
    [InlineData("refs/remotes/origin/")]
    public void GetDefaultBranchAsync_RejectsMalformedRemoteHead(string symbolicReference)
    {
        Assert.Null(GitRepositoryService.ResolveRemoteHeadBranch(symbolicReference));
    }

    #endregion

	[Theory]
	[InlineData("Receiving objects: 50% (500/1000)", 50)]
	[InlineData("remote: Counting objects: 100% (12/12)", 100)]
	[InlineData("prefix 150% then 7%", 7)]
	public void TryExtractProgressPercent_ReturnsTheFirstValidPercentage(string message, int expected)
	{
		Assert.True(GitRepositoryService.TryExtractProgressPercent(message, out var percent));
		Assert.Equal(expected, percent);
	}

	[Theory]
	[InlineData("")]
	[InlineData("Receiving objects without percentage")]
	[InlineData("Receiving objects: 101%")]
	[InlineData("Receiving objects: -1%")]
	[InlineData("Receiving objects: 1.5%")]
	[InlineData("Receiving objects: phase1%")]
	public void TryExtractProgressPercent_RejectsMissingOrOutOfRangeValues(string message)
	{
		Assert.False(GitRepositoryService.TryExtractProgressPercent(message, out _));
	}

	[Theory]
	[InlineData("Receiving objects: 50% (5/10)", true)]
	[InlineData(" remote: Enumerating objects: 12", true)]
	[InlineData("remote: warning https://user:token@example.test 50%", false)]
	[InlineData("fatal: Authentication failed 50%", false)]
	public void IsSafeGitProgressLine_UsesAClosedPhaseAllowlist(string message, bool expected)
	{
		Assert.Equal(expected, GitRepositoryService.IsSafeGitProgressLine(message));
	}

	[Theory]
	[InlineData("Receiving objects: 50% (5/10)", 50, true, false)]
	[InlineData("fatal: Authentication failed 50%", 50, false, true)]
	[InlineData("remote: warning https://user:token@example.test 50%", 50, false, true)]
	[InlineData("fatal: repository not found", null, false, true)]
	public void ClassifyGitStderrLine_RetainsErrorsThatContainPercentages(
		string message,
		int? expectedPercent,
		bool expectedSafeProgressLine,
		bool expectedRetainForError)
	{
		var classification = GitRepositoryService.ClassifyGitStderrLine(message);

		Assert.Equal(expectedPercent, classification.Percent);
		Assert.Equal(expectedSafeProgressLine, classification.IsSafeProgressLine);
		Assert.Equal(expectedRetainForError, classification.RetainForError);
	}

	[Fact]
	public async Task GitProcessLinePump_SplitsEveryLineEndingAndBoundsFrames()
	{
		var frames = new List<GitProcessLineFrame>();
		var input = new string('x', 4_095) + "\r\none\rtwo\n\nend";

		await GitProcessLinePump.ReadAsync(
			new StringReader(input),
			maximumFrameCharacters: 10,
			frames.Add,
			TestContext.Current.CancellationToken);

		Assert.Equal(
			[
				new GitProcessLineFrame(new string('x', 10), ExceededLimit: true),
				new GitProcessLineFrame("one", ExceededLimit: false),
				new GitProcessLineFrame("two", ExceededLimit: false),
				new GitProcessLineFrame(string.Empty, ExceededLimit: false),
				new GitProcessLineFrame("end", ExceededLimit: false)
			],
			frames);
	}
}
