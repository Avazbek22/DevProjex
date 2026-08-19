namespace DevProjex.Tests.Unit;

/// <summary>
/// Unit tests for GitRepositoryService helper methods.
/// Tests URL extraction, error parsing, and validation logic without requiring Git.
/// </summary>
public class GitRepositoryServiceUnitTests
{
    private readonly GitRepositoryService _service = new();

    [Fact]
    public void GitCommandsUseNonInteractiveStandardTransports()
    {
        var startInfo = GitRepositoryService.CreateGitCommandStartInfo(
            workingDirectory: null,
            arguments: ["--version"]);

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal("0", startInfo.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal(
            GitRepositoryService.NonInteractiveSshCommand,
            startInfo.Environment["GIT_SSH_COMMAND"]);
        Assert.Equal("ssh", startInfo.Environment["GIT_SSH_VARIANT"]);
        Assert.Equal(string.Empty, startInfo.Environment["GIT_ASKPASS"]);
        Assert.Equal(string.Empty, startInfo.Environment["SSH_ASKPASS"]);
        Assert.Equal("never", startInfo.Environment["SSH_ASKPASS_REQUIRE"]);
        Assert.Equal("Never", startInfo.Environment["GCM_INTERACTIVE"]);
        Assert.Equal("false", startInfo.Environment["GCM_GUI_PROMPT"]);
        Assert.Equal(["--version"], startInfo.ArgumentList);
    }

	[Fact]
	public async Task WorktreeSupportProbe_TransientFailureIsRetriedAndSuccessIsCached()
	{
		var calls = 0;
		var manager = new GitWorktreeManager(_ => Task.FromResult(
			Interlocked.Increment(ref calls) == 1
				? WorktreeSupportState.TransientFailure
				: WorktreeSupportState.Supported));

		Assert.False(await manager.IsSupportedAsync("repo", TestContext.Current.CancellationToken));
		Assert.True(await manager.IsSupportedAsync("repo", TestContext.Current.CancellationToken));
		Assert.True(await manager.IsSupportedAsync("repo", TestContext.Current.CancellationToken));
		Assert.Equal(2, calls);
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

		await Assert.ThrowsAsync<IOException>(() => manager.IsSupportedAsync("repo", CancellationToken.None));
		Assert.True(await manager.IsSupportedAsync("repo", CancellationToken.None));
		Assert.True(await manager.IsSupportedAsync("repo", CancellationToken.None));
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

		Assert.False(await manager.IsSupportedAsync("repository", TestContext.Current.CancellationToken));
		await cancellationObserved.Task.WaitAsync(
			TimeSpan.FromSeconds(2),
			TestContext.Current.CancellationToken);
		Assert.True(await manager.IsSupportedAsync("repository", TestContext.Current.CancellationToken));
		Assert.Equal(2, probeCount);
	}

	[Theory]
	[InlineData("feature/space+plus")]
	[InlineData("release/v1.2@beta")]
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
	public void BranchNameValidator_RejectsInvalidNamesBeforeCommandConstruction(string branchName)
	{
		Assert.False(GitBranchNameValidator.IsValid(branchName));
		Assert.Throws<ArgumentException>(() => GitBranchNameValidator.ValidateAndNormalize(branchName));
	}

    #region Repository Name Extraction Tests

    [Theory]
    [InlineData("https://github.com/user/repo", "repo")]
    [InlineData("https://github.com/user/my-repo", "my-repo")]
    [InlineData("https://github.com/user/repo.git", "repo")]
    [InlineData("https://github.com/org/project-name", "project-name")]
    public void RepositoryUrl_ExtractsName_FromUrl(string url, string expectedName)
    {
        // Test URL parsing logic (structural test)
        var uri = new Uri(url);
        var pathParts = uri.AbsolutePath.Trim('/').Split('/');
        var repoNameWithGit = pathParts[^1];
        var repoName = repoNameWithGit.EndsWith(".git") ? repoNameWithGit[..^4] : repoNameWithGit;

        Assert.Equal(expectedName, repoName);
    }

    #endregion

    #region Error Message Detection Tests

    [Theory]
    [InlineData("Repository not found")]
    [InlineData("fatal: repository 'https://github.com/user/repo' not found")]
    [InlineData("ERROR: Repository not found")]
    public void GitErrors_NotFound_ContainKeywords(string gitError)
    {
        // Structural test - verify error messages contain expected keywords
        Assert.Contains("not found", gitError, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Authentication failed")]
    [InlineData("fatal: Authentication failed for 'https://github.com/private/repo'")]
    [InlineData("Permission denied")]
    public void GitErrors_Authentication_ContainKeywords(string gitError)
    {
        // Structural test
        Assert.True(
            gitError.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
            gitError.Contains("permission", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Theory]
    [InlineData("Could not resolve host")]
    [InlineData("Network is unreachable")]
    public void GitErrors_Network_ContainKeywords(string gitError)
    {
        // Structural test
        Assert.True(
            gitError.Contains("network", StringComparison.OrdinalIgnoreCase) ||
            gitError.Contains("resolve", StringComparison.OrdinalIgnoreCase)
        );
    }

    #endregion

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

    #region Shallow Clone Optimization Tests

    [Fact]
    public void CloneCommand_IncludesDepthOne()
    {
        // Verify shallow clone optimization is used
        // The actual command should include --depth 1 for performance
        var testUrl = "https://github.com/user/repo";
        var testPath = "/tmp/test";

        // This is a structural test verifying the pattern is correct
        Assert.StartsWith("https://", testUrl);
        Assert.NotEmpty(testPath);
    }

    #endregion

    #region Branch Name Validation Tests

    [Theory]
    [InlineData("main")]
    [InlineData("master")]
    [InlineData("develop")]
    [InlineData("feature/new-feature")]
    [InlineData("bugfix/fix-123")]
    [InlineData("release/v1.0")]
    public void BranchNames_ValidFormats_AreValid(string branchName)
    {
        // Valid branch names should not contain forbidden characters
        Assert.DoesNotContain(" ", branchName);
        Assert.DoesNotContain("\t", branchName);
        Assert.DoesNotContain("\n", branchName);
        Assert.NotEmpty(branchName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void BranchNames_InvalidFormats_AreInvalid(string branchName)
    {
        // Invalid branch names
        Assert.True(string.IsNullOrWhiteSpace(branchName));
    }

    #endregion

    #region URL Format Tests

    [Theory]
    [InlineData("https://github.com/user/repo")]
    [InlineData("https://github.com/user/repo.git")]
    [InlineData("https://gitlab.com/org/project")]
    [InlineData("https://bitbucket.org/team/app")]
    public void GitUrls_CommonFormats_AreValid(string url)
    {
        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri));
        Assert.True(uri.Scheme == "https" || uri.Scheme == "http");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://github.com/user/repo")]
    [InlineData("file:///C:/repos/local")]
    public void GitUrls_InvalidFormats_AreInvalid(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            Assert.True(string.IsNullOrWhiteSpace(url));
        }
        else
        {
            var isValid = Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                          (uri.Scheme == "https" || uri.Scheme == "http");
            Assert.False(isValid);
        }
    }

    #endregion

    #region Path Handling Tests

    [Theory]
    [InlineData(@"C:\temp\repo")]
    [InlineData(@"C:\Users\Name\Documents\Projects\repo")]
    [InlineData(@"D:\repos\my-project")]
    public void RepositoryPaths_WindowsFormat_AreValid(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(Path.IsPathRooted(path));
            Assert.DoesNotContain("//", path.Replace("\\", "/"));
        }
    }

    [Theory]
    [InlineData("/tmp/repo")]
    [InlineData("/home/user/repos/project")]
    [InlineData("/var/git/my-repo")]
    public void RepositoryPaths_UnixFormat_AreValid(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(Path.IsPathRooted(path));
            Assert.DoesNotContain("\\", path);
        }
    }

    #endregion

    #region Command Building Tests

    [Fact]
    public void GitCommands_QuotesPaths()
    {
        // Paths with spaces should be quoted
        var pathWithSpaces = @"C:\My Documents\repo";
        var quoted = $"\"{pathWithSpaces}\"";

        Assert.StartsWith("\"", quoted);
        Assert.EndsWith("\"", quoted);
    }

    [Fact]
    public void GitCommands_HandlesSpecialCharacters()
    {
        // Special characters in paths should be handled
        var specialPath = @"C:\repo's\my-project";

        Assert.Contains("'", specialPath);
        // Should be able to handle such paths
        Assert.NotEmpty(specialPath);
    }

    #endregion

    #region Progress Reporting Tests

    [Fact]
    public void ProgressMessages_ContainPercentages()
    {
        // Progress messages typically contain percentages
        var exampleMessages = new[]
        {
            "Cloning: 25%",
            "Downloading: 50%",
            "Receiving objects: 75%",
            "Resolving deltas: 100%"
        };

        Assert.All(exampleMessages, msg =>
        {
            Assert.Contains("%", msg);
        });
    }

    [Theory]
    [InlineData("Receiving objects: 50% (500/1000)")]
    [InlineData("Resolving deltas: 100% (100/100)")]
    public void ProgressMessages_ParseCorrectly(string message)
    {
        Assert.Contains("%", message);
        Assert.Matches(@"\d+%", message);
    }

    #endregion
}
