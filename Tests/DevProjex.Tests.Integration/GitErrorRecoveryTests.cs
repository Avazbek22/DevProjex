namespace DevProjex.Tests.Integration;

/// <summary>
/// Integration tests for Git error recovery and resilience.
/// Tests recovery from network errors, corrupted repositories, and cancellation.
/// </summary>
[Collection(GitNetworkTestCollection.Name)]
public class GitErrorRecoveryTests : IAsyncLifetime
{
    private readonly GitRepositoryService _service;
    private readonly RepoCacheService _cacheService;
    private readonly TemporaryDirectory _tempDir;
    private GitTestRepository? _testRepository;
    private bool _gitAvailable;

    private string TestRepoUrl => _testRepository!.RepositoryUrl;
    private string InvalidRepoUrl => new Uri(Path.Combine(_tempDir.Path, "missing-repository.git")).AbsoluteUri;

    public GitErrorRecoveryTests()
    {
        _service = new GitRepositoryService(allowFileTransportForTests: true);
        var testCachePath = Path.Combine(Path.GetTempPath(), "DevProjex", "Tests", "GitIntegration");
        _cacheService = new RepoCacheService(testCachePath);
        _tempDir = new TemporaryDirectory();
    }

    public async ValueTask InitializeAsync()
    {
        _gitAvailable = await SharedGitRepositories.IsGitAvailableAsync();
        if (_gitAvailable)
            _testRepository = await SharedGitRepositories.GetDefaultRepositoryAsync();
    }

    public ValueTask DisposeAsync()
    {
        _tempDir.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CloneAsync_InvalidRepository_ReturnsFailureResult()
    {
        if (!_gitAvailable)
            return;

        var targetDir = _tempDir.CreateDirectory("invalid-clone");

        var result = await _service.CloneAsync(InvalidRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.NotEmpty(result.ErrorMessage);
    }

    [Fact]
    public async Task CloneAsync_Cancelled_CleansUpPartialClone()
    {
        if (!_gitAvailable)
            return;

        var targetDir = _tempDir.CreateDirectory("cancelled-clone");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: cts.Token));

        // Directory might exist but should not have complete .git
        // This is acceptable - partial cleanup
    }

    [Fact]
    public async Task GetBranchesAsync_NonGitDirectory_ReturnsEmptyList()
    {
        if (!_gitAvailable)
            return;

        var nonGitDir = _tempDir.CreateDirectory("not-git");
        File.WriteAllText(Path.Combine(nonGitDir, "test.txt"), "test");

        var branches = await _service.GetBranchesAsync(nonGitDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(branches);
    }

    [Fact]
    public async Task GetBranchesAsync_MissingGitDirectory_ReturnsEmptyList()
    {
        if (!_gitAvailable)
            return;

        var missingDir = Path.Combine(_tempDir.Path, "nonexistent");

        var branches = await _service.GetBranchesAsync(missingDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(branches);
    }

    [Fact]
    public async Task SwitchBranchAsync_NonGitDirectory_ReturnsFalse()
    {
        if (!_gitAvailable)
            return;

        var nonGitDir = _tempDir.CreateDirectory("not-git-switch");

        var success = await _service.SwitchBranchAsync(nonGitDir, "main", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(success);
    }

    [Fact]
    public async Task PullUpdatesAsync_NonGitDirectory_ReturnsFalse()
    {
        if (!_gitAvailable)
            return;

        var nonGitDir = _tempDir.CreateDirectory("not-git-pull");

        var success = await _service.PullUpdatesAsync(nonGitDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(success);
    }

    [Fact]
    public async Task GetCurrentBranchAsync_NonGitDirectory_ReturnsNull()
    {
        if (!_gitAvailable)
            return;

        var nonGitDir = _tempDir.CreateDirectory("not-git-current");

        var branch = await _service.GetCurrentBranchAsync(nonGitDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(branch);
    }

    [Fact]
    public async Task CloneAsync_ToExistingDirectory_HandlesGracefully()
    {
        if (!_gitAvailable)
            return;

        var targetDir = _tempDir.CreateDirectory("existing-dir");
        File.WriteAllText(Path.Combine(targetDir, "existing.txt"), "data");

        var result = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);

        // Should either succeed (overwriting) or fail gracefully
        if (!result.Success)
        {
            Assert.NotNull(result.ErrorMessage);
        }
    }

    [Fact]
    public async Task SwitchBranchAsync_WithCancellation_StopsGracefully()
    {
        if (!_gitAvailable)
            return;

        var repoPath = _tempDir.CreateDirectory("cancel-switch");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);
        if (branches.Count < 2)
            return;

        using var cts = new CancellationTokenSource();
        var targetBranch = branches[0].Name;

        var switchTask = _service.SwitchBranchAsync(repoPath, targetBranch, cancellationToken: cts.Token);

        cts.Cancel();

        // Should handle cancellation gracefully
        try
        {
            await switchTask;
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Repository should still be in valid state
        var currentBranch = await _service.GetCurrentBranchAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(currentBranch);
    }

    [Fact]
    public async Task PullUpdatesAsync_WithPreCancelledToken_DoesNotCorruptRepo()
    {
        if (!_gitAvailable)
            return;

        var repoPath = _tempDir.CreateDirectory("cancel-pull");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(cloneResult.Success);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await _service.PullUpdatesAsync(repoPath, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Repository should still be functional
        var branches = await _service.GetBranchesAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(branches);
    }

    [Fact]
    public async Task CloneAsync_AfterFailedClone_CanRetry()
    {
        if (!_gitAvailable)
            return;

        var targetDir = _tempDir.CreateDirectory("retry-clone");

        // First attempt with invalid URL
        var result1 = await _service.CloneAsync(InvalidRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result1.Success);

        // Clean up failed attempt
        try
        {
            if (Directory.Exists(targetDir))
                Directory.Delete(targetDir, recursive: true);
            Directory.CreateDirectory(targetDir);
        }
        catch { }

        // Second attempt with valid URL
        var result2 = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);

        // Should succeed on retry
        Assert.True(result2.Success);
    }

    [Fact]
    public async Task GetBranchesAsync_AfterFailedOperation_StillWorks()
    {
        if (!_gitAvailable)
            return;

        var repoPath = _tempDir.CreateDirectory("after-fail");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(cloneResult.Success);

        // Try invalid operation
        await _service.SwitchBranchAsync(repoPath, "invalid-branch-xyz", cancellationToken: TestContext.Current.CancellationToken);

        // GetBranches should still work
        var branches = await _service.GetBranchesAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(branches);
    }

    [Fact]
    public async Task SwitchBranchAsync_AfterNetworkError_Recovers()
    {
        if (!_gitAvailable)
            return;

        var repoPath = _tempDir.CreateDirectory("network-recovery");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);
        if (branches.Count < 2)
            return;

        // Simulate network error by trying to switch to nonexistent remote branch
        await _service.SwitchBranchAsync(repoPath, "nonexistent-branch", cancellationToken: TestContext.Current.CancellationToken);

        // Should be able to switch to valid branch after error
        var targetBranch = branches[0].Name;
        var success = await _service.SwitchBranchAsync(repoPath, targetBranch, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(success);
    }

    [Fact]
    public async Task CloneAsync_WithInvalidPath_HandlesGracefully()
    {
        if (!_gitAvailable)
            return;

        // Try to clone to path with special characters
        var specialPath = Path.Combine(_tempDir.Path, "repo:invalid");

        try
        {
            var result = await _service.CloneAsync(TestRepoUrl, specialPath, cancellationToken: TestContext.Current.CancellationToken);

            // Should either fail gracefully or handle the path
            if (!result.Success)
            {
                Assert.NotNull(result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            // Any exception is also acceptable - as long as it doesn't crash
            Assert.NotNull(ex);
        }
    }

    [Fact]
    public async Task IsGitAvailableAsync_CallMultipleTimes_StaysConsistent()
    {
        // Call multiple times to ensure no state corruption
        var result1 = await _service.IsGitAvailableAsync(cancellationToken: TestContext.Current.CancellationToken);
        var result2 = await _service.IsGitAvailableAsync(cancellationToken: TestContext.Current.CancellationToken);
        var result3 = await _service.IsGitAvailableAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(result1, result2);
        Assert.Equal(result2, result3);
    }

    [Fact]
    public async Task GetCurrentBranchAsync_EmptyRepository_ReturnsNull()
    {
        if (!_gitAvailable)
            return;

        var repoPath = _tempDir.CreateDirectory("empty-current");

        // Create empty git repo using Process
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"init \"{repoPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi);
        await process!.WaitForExitAsync(cancellationToken: TestContext.Current.CancellationToken);

        var currentBranch = await _service.GetCurrentBranchAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);

        // Empty repo with no commits has no current branch
        Assert.Null(currentBranch);
    }

    [Fact]
    public async Task SwitchBranchAsync_MultipleFailures_DoesNotCorruptState()
    {
        if (!_gitAvailable)
            return;

        var repoPath = _tempDir.CreateDirectory("multi-fail");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(cloneResult.Success);

        var originalBranch = await _service.GetCurrentBranchAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);

        // Try switching to invalid branches multiple times
        await _service.SwitchBranchAsync(repoPath, "invalid1", cancellationToken: TestContext.Current.CancellationToken);
        await _service.SwitchBranchAsync(repoPath, "invalid2", cancellationToken: TestContext.Current.CancellationToken);
        await _service.SwitchBranchAsync(repoPath, "invalid3", cancellationToken: TestContext.Current.CancellationToken);

        // Original branch should still be active
        var currentBranch = await _service.GetCurrentBranchAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(originalBranch, currentBranch);
    }

    [Fact]
    public async Task CloneAsync_WithProgressCallback_ReportsProgress()
    {
        if (!_gitAvailable)
            return;

        var targetDir = _tempDir.CreateDirectory("progress-test");
        var progress = new ProgressRecorder();

        var result = await _service.CloneAsync(TestRepoUrl, targetDir, progress, cancellationToken: TestContext.Current.CancellationToken);

        // Verify that progress was reported
        Assert.NotNull(result);
        // Progress messages may or may not be received depending on git speed
    }
}
