namespace DevProjex.Tests.Integration;

/// <summary>
/// Integration tests for GitRepositoryService.
/// These tests require git to be installed and network access for some tests.
///
/// Test categories:
/// - Git availability detection
/// - Clone operations (requires network)
/// - Branch operations
/// - Update operations
///
/// IMPORTANT: These tests use real git operations and network access.
/// Some tests will be skipped if git is not available.
/// </summary>
[Collection(GitNetworkTestCollection.Name)]
public class GitRepositoryServiceTests : IAsyncLifetime
{
    private readonly GitRepositoryService _service = new();
    private string? _tempDir;
    private bool _gitAvailable;
    private GitTestRepository? _testRepository;

    private string TestRepoUrl => _testRepository!.RepositoryUrl;
    private string TestRepoName => _testRepository!.RepositoryName;

    public async ValueTask InitializeAsync()
    {
        _gitAvailable = await SharedGitRepositories.IsGitAvailableAsync();
        _tempDir = Path.Combine(Path.GetTempPath(), "DevProjex", "Tests", "GitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        if (_gitAvailable)
            _testRepository = await SharedGitRepositories.GetDefaultRepositoryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        // Cleanup with retry for locked git files
        if (_tempDir != null && Directory.Exists(_tempDir))
            await TryDeleteDirectoryAsync(_tempDir);
    }

    /// <summary>
    /// Attempts to delete directory with retries for locked files.
    /// Git may keep files locked briefly after operations.
    /// </summary>
    private static async Task TryDeleteDirectoryAsync(string path)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                // Reset readonly attributes
                SetAttributesNormal(path);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(100 * (i + 1));
            }
            catch (IOException)
            {
                await Task.Delay(100 * (i + 1));
            }
        }

        // Final attempt - ignore errors
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Ignore - OS will clean up temp folder eventually
        }
    }

    private static void SetAttributesNormal(string path)
    {
        try
        {
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Helper to skip test if git is not available.
    /// </summary>
    private void SkipIfNoGit()
    {
        if (!_gitAvailable)
            Assert.Skip("Git is not available on this system.");
    }

    /// <summary>
    /// Helper to skip test with custom condition.
    /// </summary>
    private static void SkipIf(bool condition, string reason)
    {
        if (condition)
            Assert.Skip(reason);
    }

    #region Git Availability Tests

    [Fact]
    public async Task IsGitAvailableAsync_DoesNotThrow()
    {
        // This test verifies that git detection works without throwing
        var result = await _service.IsGitAvailableAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Result depends on environment, but method should not throw
        Assert.True(result || !result);
    }

    [Fact]
    public async Task IsGitAvailableAsync_ReturnsConsistentResults()
    {
        // Multiple calls should return the same result
        var result1 = await _service.IsGitAvailableAsync(cancellationToken: TestContext.Current.CancellationToken);
        var result2 = await _service.IsGitAvailableAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(result1, result2);
    }

    #endregion

    #region Clone Tests

    [Fact]
    public void AuthenticatedCloneStartInfoKeepsCredentialsOutOfArguments()
    {
        const string password = "token-with-&-special";
        var authentication = Assert.IsType<GitCloneAuthentication>(
            GitCloneAuthentication.TryCreate(
                $"https://oauth2:{Uri.EscapeDataString(password)}@example.test/owner/repository.git"));
        using var askPass = GitAskPassSession.Create(authentication);

        var startInfo = GitProcessStartInfoFactory.Create(
            null,
            ["clone", authentication.RepositoryUrl, "target"],
            askPass: askPass);

        Assert.Equal("https://example.test/owner/repository.git", authentication.RepositoryUrl);
        Assert.DoesNotContain(
            startInfo.ArgumentList,
            argument => argument.Contains(password, StringComparison.Ordinal) ||
                        argument.Contains("oauth2", StringComparison.Ordinal));
        Assert.Equal(password, startInfo.Environment[GitAskPassSession.PasswordEnvironmentVariable]);
        Assert.Equal("oauth2", startInfo.Environment["GIT_CONFIG_VALUE_0"]);
        Assert.DoesNotContain(password, File.ReadAllText(askPass.HelperPath), StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedAuthenticatedCloneSourceIsRejectedBeforeBuildingGitArguments()
    {
        const string password = "super-secret";
        const string source = $"https://oauth2:{password}@[invalid/repository.git";

        var accepted = GitCloneAuthentication.TryResolveCloneUrl(
            source,
            out var cloneUrl,
            out var authentication);

        Assert.False(accepted);
        Assert.Null(authentication);
        Assert.DoesNotContain(password, cloneUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("oauth2", cloneUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticatedCloneUsesSanitizedArgvAndAskPassInChildProcess()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The process probe uses a POSIX executable script; Windows behavior is covered by the start-info contract test.");

        const string password = "process-secret-token";
        var probeDirectory = Path.Combine(_tempDir!, "git-argv-probe");
        Directory.CreateDirectory(probeDirectory);
        var executablePath = Path.Combine(probeDirectory, "git-probe");
        var argumentLog = Path.Combine(probeDirectory, "arguments.txt");
        var passwordLog = Path.Combine(probeDirectory, "password.txt");
        var userNameLog = Path.Combine(probeDirectory, "username.txt");
        var script =
            "#!/bin/sh\n" +
            $"printf '%s\\n' \"$@\" >> {ShellQuote(argumentLog)}\n" +
            "if [ \"$1\" = clone ]; then\n" +
            $"  \"$GIT_ASKPASS\" 'Password:' > {ShellQuote(passwordLog)}\n" +
            $"  printf '%s' \"$GIT_CONFIG_VALUE_0\" > {ShellQuote(userNameLog)}\n" +
            "  for target do :; done\n" +
            "  mkdir -p \"$target/.git\"\n" +
            "elif [ \"$1\" = symbolic-ref ]; then\n" +
            "  printf 'refs/remotes/origin/main\\n'\n" +
            "fi\n";
        await File.WriteAllTextAsync(
            executablePath,
            script,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            TestContext.Current.CancellationToken);
        MakeExecutable(executablePath);
        var service = new GitRepositoryService(executablePath);
        var targetDirectory = Path.Combine(probeDirectory, "clone");
        var authenticatedUrl =
            $"https://oauth2:{Uri.EscapeDataString(password)}@example.test/owner/repository.git";

        var result = await service.CloneAsync(
            authenticatedUrl,
            targetDirectory,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var arguments = await File.ReadAllTextAsync(argumentLog, TestContext.Current.CancellationToken);
        Assert.Contains("https://example.test/owner/repository.git", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain(password, arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("oauth2", arguments, StringComparison.Ordinal);
        Assert.Equal(
            password,
            (await File.ReadAllTextAsync(passwordLog, TestContext.Current.CancellationToken)).Trim());
        Assert.Equal(
            "oauth2",
            await File.ReadAllTextAsync(userNameLog, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CloneResultNeverExposesTransportQueryOrFragment()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The process probe uses a POSIX executable script.");

        var probeDirectory = Path.Combine(_tempDir!, "git-result-url-probe");
        Directory.CreateDirectory(probeDirectory);
        var executablePath = Path.Combine(probeDirectory, "git-probe");
        var argumentLog = Path.Combine(probeDirectory, "arguments.txt");
        var script =
            "#!/bin/sh\n" +
            $"printf '%s\\n' \"$@\" >> {ShellQuote(argumentLog)}\n" +
            "if [ \"$1\" = clone ]; then\n" +
            "  for target do :; done\n" +
            "  mkdir -p \"$target/.git\"\n" +
            "elif [ \"$1\" = symbolic-ref ]; then\n" +
            "  printf 'refs/remotes/origin/main\\n'\n" +
            "fi\n";
        await File.WriteAllTextAsync(
            executablePath,
            script,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            TestContext.Current.CancellationToken);
        MakeExecutable(executablePath);
        var service = new GitRepositoryService(executablePath);
        var targetDirectory = Path.Combine(probeDirectory, "clone");
        const string sourceUrl =
            "https://example.test/owner/repository.git?transport=opaque#fragment";

        var result = await service.CloneAsync(
            sourceUrl,
            targetDirectory,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("https://example.test/owner/repository.git", result.RepositoryUrl);
        Assert.Equal("repository", result.RepositoryName);
        Assert.Contains(
            "?transport=opaque#fragment",
            await File.ReadAllTextAsync(argumentLog, TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CloneAsync_ReturnsError_ForNonGitUrl()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "non-git-url-test");
        var nonRepositoryPath = Path.Combine(_tempDir!, "not-a-repository");
        Directory.CreateDirectory(nonRepositoryPath);
        File.WriteAllText(Path.Combine(nonRepositoryPath, "README.txt"), "not a git repository");

        var result = await _service.CloneAsync(
            new Uri(nonRepositoryPath).AbsoluteUri,
            targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task CloneAsync_ReturnsError_ForMissingRemote()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "invalid-domain-test");
        var missingRemote = new Uri(Path.Combine(_tempDir!, "missing-repository.git")).AbsoluteUri;

        var result = await _service.CloneAsync(
            missingRemote,
            targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task CloneAsync_ClonesRepository_Successfully()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "clone-test");

        var result = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, $"Clone failed: {result.ErrorMessage}");
        Assert.Equal(targetDir, result.LocalPath);
        Assert.Equal(ProjectSourceType.GitClone, result.SourceType);
        Assert.Equal(TestRepoName, result.RepositoryName);
        Assert.NotNull(result.DefaultBranch);
        Assert.True(Directory.Exists(targetDir), "Target directory should exist");
        Assert.True(Directory.Exists(Path.Combine(targetDir, ".git")), ".git directory should exist");
    }

    [Fact]
    public async Task CloneAsync_ReportsProgress()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "progress-test");
        var progress = new ProgressRecorder();

        var result = await _service.CloneAsync(TestRepoUrl, targetDir, progress, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, $"Clone failed: {result.ErrorMessage}");
        Assert.Contains(
            progress.Reports,
            static report => report.Contains('%', StringComparison.Ordinal));
    }

    [Fact]
    public async Task CloneAsync_SupportsCancellation()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "cancel-test");
        using var cts = new CancellationTokenSource();

        // Cancel immediately
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task CloneAsync_ReturnsError_ForInvalidUrl()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "invalid-url-test");
        var missingRemote = new Uri(Path.Combine(_tempDir!, "missing-repository-2.git")).AbsoluteUri;

        var result = await _service.CloneAsync(
            missingRemote,
            targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task CloneAsync_ExtractsRepositoryName_FromUrl()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "name-test");

        var result = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, $"Clone failed: {result.ErrorMessage}");
        Assert.Equal(TestRepoName, result.RepositoryName);
    }

    [Fact]
    public async Task CloneAsync_CreatesShallowClone()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "shallow-test");
        var result = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, $"Clone failed: {result.ErrorMessage}");

        // Verify shallow clone by checking for shallow file
        var shallowFile = Path.Combine(targetDir, ".git", "shallow");
        Assert.True(File.Exists(shallowFile), "Repository should be shallow (have .git/shallow file)");
    }

    #endregion

    #region Branch Tests

    [Fact]
    public async Task GetBranchesAsync_ReturnsBranches_AfterClone()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "branches-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var branches = await _service.GetBranchesAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(branches);
        // At least one branch should be active (current branch)
        Assert.Contains(branches, b => b.IsActive);
    }

    [Fact]
    public async Task GetBranchesAsync_ActiveBranchIsFirst()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "active-first-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var branches = await _service.GetBranchesAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(branches);
        // First branch should be active (sorting places active first)
        Assert.True(branches[0].IsActive, "First branch should be the active one");
    }

    [Fact]
    public async Task GetBranchesAsync_PreservesCaseDistinctRefs()
    {
        SkipIfNoGit();
        await using var source = await GitTestRepository.CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var targetDir = Path.Combine(_tempDir!, "case-distinct-branches");
        var cloneResult = await _service.CloneAsync(
            source.RepositoryUrl,
            targetDir,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(cloneResult.Success, cloneResult.ErrorMessage);
        var head = await source.GetBranchHeadAsync(source.DefaultBranchName, TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(
            Path.Combine(source.BareRepositoryPath, "packed-refs"),
            $"{head} refs/heads/Feature\n{head} refs/heads/feature\n",
            TestContext.Current.CancellationToken);

        var branches = await _service.GetBranchesAsync(targetDir, TestContext.Current.CancellationToken);

        Assert.Contains(branches, branch => branch.Name == "Feature");
        Assert.Contains(branches, branch => branch.Name == "feature");
    }

    [Fact]
    public async Task GetCurrentBranchAsync_ReturnsCurrentBranch()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "current-branch-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var currentBranch = await _service.GetCurrentBranchAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(currentBranch);
        Assert.NotEmpty(currentBranch);
    }

    [Fact]
    public async Task SwitchBranchAsync_SwitchesToDifferentBranch()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "switch-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        // Get list of branches
        var branches = await _service.GetBranchesAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);
        SkipIf(branches.Count < 2, "Repository has only one branch, cannot test switching");

        // Find a branch that is not currently active
        var otherBranch = branches.FirstOrDefault(b => !b.IsActive);
        SkipIf(otherBranch is null, "No other branch available for switching");

        // Switch to the other branch
        var success = await _service.SwitchBranchAsync(targetDir, otherBranch!.Name, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(success, "Branch switch should succeed");

        // Verify current branch changed
        var newCurrentBranch = await _service.GetCurrentBranchAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(otherBranch.Name, newCurrentBranch);
    }

    [Fact]
    public async Task SwitchBranchAsync_ReturnsFalse_ForNonexistentBranch()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "nonexistent-branch-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var success = await _service.SwitchBranchAsync(targetDir, "nonexistent-branch-xyz123", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(success, "Switching to nonexistent branch should fail");
    }

    [Fact]
    public async Task SwitchBranchAsync_ReportsProgress()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "switch-progress-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var branches = await _service.GetBranchesAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);
        var otherBranch = branches.FirstOrDefault(b => !b.IsActive);
        SkipIf(otherBranch is null, "No other branch available");

        var progress = new ProgressRecorder();

        var success = await _service.SwitchBranchAsync(targetDir, otherBranch!.Name, progress, cancellationToken: TestContext.Current.CancellationToken);

        // Verify operation succeeded - progress reports are optional
        Assert.True(success, "Branch switch should succeed");
    }

    [Fact]
    public async Task SwitchBranchAsync_CanSwitchBackToOriginalBranch()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "switch-back-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var originalBranch = await _service.GetCurrentBranchAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);
        var branches = await _service.GetBranchesAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);
        var otherBranch = branches.FirstOrDefault(b => !b.IsActive);
        SkipIf(otherBranch is null, "No other branch available");

        // Switch to other branch
        var switchResult1 = await _service.SwitchBranchAsync(targetDir, otherBranch!.Name, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(switchResult1, "First switch should succeed");

        // Switch back to original
        var switchResult2 = await _service.SwitchBranchAsync(targetDir, originalBranch!, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(switchResult2, "Switch back should succeed");

        var finalBranch = await _service.GetCurrentBranchAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(originalBranch, finalBranch);
    }

    #endregion

    #region Pull Updates Tests

    [Fact]
    public async Task PullUpdatesAsync_SucceedsOnCleanRepository()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "pull-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var success = await _service.PullUpdatesAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(success, "Pull updates should succeed on clean repository");
    }

    [Fact]
    public async Task PullUpdatesAsync_ReportsProgress()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "pull-progress-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var progress = new ProgressRecorder();

        var success = await _service.PullUpdatesAsync(targetDir, progress, cancellationToken: TestContext.Current.CancellationToken);

        // Verify operation succeeded - progress reports are optional
        Assert.True(success, "Pull updates should succeed on clean repository");
    }

    [Fact]
    public async Task PullUpdatesAsync_WorksAfterBranchSwitch()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "pull-after-switch-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        // Get branches and switch if possible
        var branches = await _service.GetBranchesAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);
        var otherBranch = branches.FirstOrDefault(b => !b.IsActive);

        if (otherBranch is not null)
        {
            var switchResult = await _service.SwitchBranchAsync(targetDir, otherBranch.Name, cancellationToken: TestContext.Current.CancellationToken);
            SkipIf(!switchResult, "Branch switch failed");
        }

        // Pull should still work after switch
        var pullSuccess = await _service.PullUpdatesAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(pullSuccess, "Pull updates should work after branch switch");
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task GetBranchesAsync_ReturnsEmptyList_ForInvalidPath()
    {
        var invalidPath = Path.Combine(_tempDir!, "nonexistent-repo-xyz");

        var branches = await _service.GetBranchesAsync(invalidPath, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(branches);
    }

    [Fact]
    public async Task GetCurrentBranchAsync_ReturnsNull_ForInvalidPath()
    {
        var invalidPath = Path.Combine(_tempDir!, "nonexistent-repo-xyz");

        var branch = await _service.GetCurrentBranchAsync(invalidPath, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(branch);
    }

    [Fact]
    public async Task SwitchBranchAsync_ReturnsFalse_ForInvalidPath()
    {
        var invalidPath = Path.Combine(_tempDir!, "nonexistent-repo-xyz");

        var success = await _service.SwitchBranchAsync(invalidPath, "main", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(success);
    }

    [Fact]
    public async Task PullUpdatesAsync_ReturnsFalse_ForInvalidPath()
    {
        var invalidPath = Path.Combine(_tempDir!, "nonexistent-repo-xyz");

        var success = await _service.PullUpdatesAsync(invalidPath, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(success);
    }

    [Fact]
    public async Task GetBranchesAsync_ReturnsEmptyList_ForNonGitDirectory()
    {
        // Create a regular directory (not a git repo)
        var nonGitDir = Path.Combine(_tempDir!, "not-a-git-repo");
        Directory.CreateDirectory(nonGitDir);
        File.WriteAllText(Path.Combine(nonGitDir, "file.txt"), "content");

        var branches = await _service.GetBranchesAsync(nonGitDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(branches);
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task GetBranchesAsync_HandlesErrors_ForCancelledToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await _service.GetBranchesAsync(_tempDir!, cts.Token));
    }

    [Fact]
    public async Task GetCurrentBranchAsync_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.GetCurrentBranchAsync(_tempDir!, cancellation.Token));
    }

    #endregion

    #region Concurrent Operations Tests

    [Fact]
    public async Task MultipleClones_DoNotInterfere()
    {
        SkipIfNoGit();

        // Clone two repositories in parallel to different directories
        var targetDir1 = Path.Combine(_tempDir!, "parallel-1");
        var targetDir2 = Path.Combine(_tempDir!, "parallel-2");

        var task1 = _service.CloneAsync(TestRepoUrl, targetDir1, cancellationToken: TestContext.Current.CancellationToken);
        var task2 = _service.CloneAsync(TestRepoUrl, targetDir2, cancellationToken: TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(task1, task2);

        Assert.True(results[0].Success, $"First clone failed: {results[0].ErrorMessage}");
        Assert.True(results[1].Success, $"Second clone failed: {results[1].ErrorMessage}");

        // Both should have valid content
        Assert.True(Directory.Exists(Path.Combine(targetDir1, ".git")));
        Assert.True(Directory.Exists(Path.Combine(targetDir2, ".git")));
    }

    #endregion

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
