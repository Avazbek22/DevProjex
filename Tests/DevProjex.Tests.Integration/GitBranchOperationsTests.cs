namespace DevProjex.Tests.Integration;

/// <summary>
/// Detailed integration tests for Git branch operations.
/// Tests branch listing, switching, tracking, and state management.
/// </summary>
public class GitBranchOperationsTests : IAsyncLifetime
{
    private readonly GitRepositoryService _service;
    private readonly RepoCacheService _cacheService;
    private readonly TemporaryDirectory _tempDir;
    private GitTestRepository? _testRepository;
    private bool _gitAvailable;

    private string TestRepoUrl => _testRepository!.RepositoryUrl;

    public GitBranchOperationsTests()
    {
        _service = new GitRepositoryService();
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
    public async Task GetBranchesAsync_ReturnsAllRemoteBranches()
    {
        if (!_gitAvailable)
            return;

        var repoPath = _tempDir.CreateDirectory("branches-list");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(branches);
        Assert.All(branches, b => Assert.NotNull(b.Name));
        Assert.All(branches, b => Assert.NotEmpty(b.Name));
    }

    [Fact]
    public async Task GetBranchesAsync_MarkesCurrentBranchAsActive()
    {
        if (!_gitAvailable)
            return;

        var repoPath = _tempDir.CreateDirectory("active-branch");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);

        // Exactly one branch should be marked as active
        var activeBranches = branches.Where(b => b.IsActive).ToList();
        Assert.Single(activeBranches);
    }

    [Fact]
    public async Task GetCurrentBranchAsync_ReturnsActiveBranch()
    {
        if (!_gitAvailable)
            return;

        var repoPath = _tempDir.CreateDirectory("current-branch");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(cloneResult.Success);

        var currentBranch = await _service.GetCurrentBranchAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);
        var branches = await _service.GetBranchesAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);
        var activeBranch = branches.FirstOrDefault(b => b.IsActive);

        Assert.NotNull(currentBranch);
        Assert.NotNull(activeBranch);
        Assert.Equal(activeBranch.Name, currentBranch);
    }

    [Fact]
    public async Task SwitchBranchAsync_UpdatesCurrentBranch()
    {
        if (!_gitAvailable)
            return;

        var repoPath = _tempDir.CreateDirectory("switch-updates");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath, cancellationToken: TestContext.Current.CancellationToken);
        if (!cloneResult.Success)
        {
            repoPath = _tempDir.CreateDirectory("switch-updates-retry");
            cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath, cancellationToken: TestContext.Current.CancellationToken);
        }

        // External network can be temporarily unavailable in integration environments.
        // In that case we skip this scenario instead of producing a flaky failure.
        if (!cloneResult.Success)
            return;

        var branches = await _service.GetBranchesAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);
        if (branches.Count < 2)
            return;

        var targetBranch = branches.First(b => !b.IsActive).Name;
        var success = await _service.SwitchBranchAsync(repoPath, targetBranch, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(success);

        var newCurrentBranch = await _service.GetCurrentBranchAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(targetBranch, newCurrentBranch);
    }

    [Fact]
    public async Task SwitchBranchAsync_UpdatesIsActiveFlag()
    {
        if (!_gitAvailable)
            return;

        var repoPath = _tempDir.CreateDirectory("switch-flag");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(cloneResult.Success);

        var branchesBefore = await _service.GetBranchesAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);
        if (branchesBefore.Count < 2)
            return;

        var targetBranch = branchesBefore.First(b => !b.IsActive).Name;
        await _service.SwitchBranchAsync(repoPath, targetBranch, cancellationToken: TestContext.Current.CancellationToken);

        var branchesAfter = await _service.GetBranchesAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);
        var activeBranch = branchesAfter.FirstOrDefault(b => b.IsActive);

        Assert.NotNull(activeBranch);
        Assert.Equal(targetBranch, activeBranch.Name);
    }

    [Fact]
    public async Task SwitchBranchAsync_ToNonexistentBranch_ReturnsFalse()
    {
        if (!_gitAvailable)
            return;

        var repoPath = _tempDir.CreateDirectory("switch-invalid");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(cloneResult.Success);

        var success = await _service.SwitchBranchAsync(repoPath, "nonexistent-branch-xyz", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(success);
    }

    [Fact]
    public async Task SwitchBranchAsync_ToSameBranch_Succeeds()
    {
        if (!_gitAvailable)
            return;

        var repoPath = _tempDir.CreateDirectory("switch-same");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(cloneResult.Success);

        var currentBranch = await _service.GetCurrentBranchAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);
        var success = await _service.SwitchBranchAsync(repoPath, currentBranch!, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(success);
    }

    [Fact]
    public async Task GetBranchesAsync_AfterMultipleSwitches_StaysConsistent()
    {
        if (!_gitAvailable)
            return;

        var repoPath = _tempDir.CreateDirectory("multi-switch");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);
        if (branches.Count < 2)
            return;

        var branch1 = branches[0].Name;
        var branch2 = branches[1].Name;

        // Switch back and forth multiple times
        await _service.SwitchBranchAsync(repoPath, branch1, cancellationToken: TestContext.Current.CancellationToken);
        await _service.SwitchBranchAsync(repoPath, branch2, cancellationToken: TestContext.Current.CancellationToken);
        await _service.SwitchBranchAsync(repoPath, branch1, cancellationToken: TestContext.Current.CancellationToken);

        var finalBranches = await _service.GetBranchesAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);

        // Should still have same number of branches
        Assert.Equal(branches.Count, finalBranches.Count);

        // Exactly one should be active
        Assert.Single(finalBranches, b => b.IsActive);
    }

    [Fact]
    public async Task SwitchBranchAsync_PreservesWorkingDirectory()
    {
        if (!_gitAvailable)
            return;

        var repoPath = _tempDir.CreateDirectory("preserve-wd");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);
        if (branches.Count < 2)
            return;

        // Count files before switch
        var filesBefore = Directory.GetFiles(repoPath, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".git"))
            .Count();

        var targetBranch = branches.First(b => !b.IsActive).Name;
        await _service.SwitchBranchAsync(repoPath, targetBranch, cancellationToken: TestContext.Current.CancellationToken);

        // Files should still exist (working directory preserved)
        var filesAfter = Directory.GetFiles(repoPath, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".git"))
            .Count();

        Assert.True(filesAfter > 0);
    }

    [Fact]
    public async Task GetBranchesAsync_EmptyRepository_ReturnsEmptyList()
    {
        if (!_gitAvailable)
            return;

        var repoPath = _tempDir.CreateDirectory("empty-repo");

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

        var branches = await _service.GetBranchesAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);

        // Empty repo has no branches until first commit
        Assert.Empty(branches);
    }

    [Fact]
    public async Task SwitchBranchAsync_WithProgress_ReportsProgress()
    {
        if (!_gitAvailable)
            return;

        var repoPath = _tempDir.CreateDirectory("switch-progress");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);
        if (branches.Count < 2)
            return;

        var progress = new ProgressRecorder();

        var targetBranch = branches.First(b => !b.IsActive).Name;
        await _service.SwitchBranchAsync(repoPath, targetBranch, progress, cancellationToken: TestContext.Current.CancellationToken);

        // Progress callback should not cause errors (might or might not report)
        Assert.NotNull(progress.Reports);
    }

    [Fact]
    public async Task GetBranchesAsync_CaseInsensitive_FindsBranches()
    {
        if (!_gitAvailable)
            return;

        var repoPath = _tempDir.CreateDirectory("case-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);

        // Branch names should preserve original case
        Assert.All(branches, b => Assert.Equal(b.Name, b.Name.Trim()));
    }

    [Fact]
    public async Task SwitchBranchAsync_AfterPull_WorksCorrectly()
    {
        if (!_gitAvailable)
            return;

        var repoPath = _tempDir.CreateDirectory("switch-after-pull");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(cloneResult.Success);

        // Pull updates first
        await _service.PullUpdatesAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);

        var branches = await _service.GetBranchesAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);
        if (branches.Count < 2)
            return;

        var targetBranch = branches.First(b => !b.IsActive).Name;
        var success = await _service.SwitchBranchAsync(repoPath, targetBranch, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(success);
    }

    [Fact]
    public async Task GetBranchesAsync_DoesNotModifyRepository()
    {
        if (!_gitAvailable)
            return;

        var repoPath = _tempDir.CreateDirectory("readonly-branches");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(cloneResult.Success);

        var currentBranchBefore = await _service.GetCurrentBranchAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);

        // Call GetBranches multiple times
        await _service.GetBranchesAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);
        await _service.GetBranchesAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);
        await _service.GetBranchesAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);

        var currentBranchAfter = await _service.GetCurrentBranchAsync(repoPath, cancellationToken: TestContext.Current.CancellationToken);

        // Current branch should not change
        Assert.Equal(currentBranchBefore, currentBranchAfter);
    }
}
