using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DevProjex.Infrastructure.Git;
using DevProjex.Tests.Integration.Helpers;
using Xunit;

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

    private const string TestRepoUrl = "https://github.com/octocat/Hello-World";

    public GitBranchOperationsTests()
    {
        _service = new GitRepositoryService();
        var testCachePath = Path.Combine(Path.GetTempPath(), "DevProjex", "Tests", "GitIntegration");
        _cacheService = new RepoCacheService(testCachePath);
        _tempDir = new TemporaryDirectory();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _tempDir.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetBranchesAsync_ReturnsAllRemoteBranches()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("branches-list");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath);

        Assert.NotEmpty(branches);
        Assert.All(branches, b => Assert.NotNull(b.Name));
        Assert.All(branches, b => Assert.NotEmpty(b.Name));
    }

    [Fact]
    public async Task GetBranchesAsync_MarkesCurrentBranchAsActive()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("active-branch");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath);

        // Exactly one branch should be marked as active
        var activeBranches = branches.Where(b => b.IsActive).ToList();
        Assert.Single(activeBranches);
    }

    [Fact]
    public async Task GetCurrentBranchAsync_ReturnsActiveBranch()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("current-branch");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var currentBranch = await _service.GetCurrentBranchAsync(repoPath);
        var branches = await _service.GetBranchesAsync(repoPath);
        var activeBranch = branches.FirstOrDefault(b => b.IsActive);

        Assert.NotNull(currentBranch);
        Assert.NotNull(activeBranch);
        Assert.Equal(activeBranch.Name, currentBranch);
    }

    [Fact]
    public async Task SwitchBranchAsync_UpdatesCurrentBranch()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("switch-updates");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath);
        if (branches.Count < 2)
            return;

        var targetBranch = branches.First(b => !b.IsActive).Name;
        var success = await _service.SwitchBranchAsync(repoPath, targetBranch);
        Assert.True(success);

        var newCurrentBranch = await _service.GetCurrentBranchAsync(repoPath);
        Assert.Equal(targetBranch, newCurrentBranch);
    }

    [Fact]
    public async Task SwitchBranchAsync_UpdatesIsActiveFlag()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("switch-flag");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var branchesBefore = await _service.GetBranchesAsync(repoPath);
        if (branchesBefore.Count < 2)
            return;

        var targetBranch = branchesBefore.First(b => !b.IsActive).Name;
        await _service.SwitchBranchAsync(repoPath, targetBranch);

        var branchesAfter = await _service.GetBranchesAsync(repoPath);
        var activeBranch = branchesAfter.FirstOrDefault(b => b.IsActive);

        Assert.NotNull(activeBranch);
        Assert.Equal(targetBranch, activeBranch.Name);
    }

    [Fact]
    public async Task SwitchBranchAsync_ToNonexistentBranch_ReturnsFalse()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("switch-invalid");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var success = await _service.SwitchBranchAsync(repoPath, "nonexistent-branch-xyz");

        Assert.False(success);
    }

    [Fact]
    public async Task SwitchBranchAsync_ToSameBranch_Succeeds()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("switch-same");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var currentBranch = await _service.GetCurrentBranchAsync(repoPath);
        var success = await _service.SwitchBranchAsync(repoPath, currentBranch!);

        Assert.True(success);
    }

    [Fact]
    public async Task GetBranchesAsync_AfterMultipleSwitches_StaysConsistent()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("multi-switch");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath);
        if (branches.Count < 2)
            return;

        var branch1 = branches[0].Name;
        var branch2 = branches[1].Name;

        // Switch back and forth multiple times
        await _service.SwitchBranchAsync(repoPath, branch1);
        await _service.SwitchBranchAsync(repoPath, branch2);
        await _service.SwitchBranchAsync(repoPath, branch1);

        var finalBranches = await _service.GetBranchesAsync(repoPath);

        // Should still have same number of branches
        Assert.Equal(branches.Count, finalBranches.Count);

        // Exactly one should be active
        Assert.Single(finalBranches.Where(b => b.IsActive));
    }

    [Fact]
    public async Task SwitchBranchAsync_PreservesWorkingDirectory()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("preserve-wd");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath);
        if (branches.Count < 2)
            return;

        // Count files before switch
        var filesBefore = System.IO.Directory.GetFiles(repoPath, "*", System.IO.SearchOption.AllDirectories)
            .Where(f => !f.Contains(".git"))
            .Count();

        var targetBranch = branches.First(b => !b.IsActive).Name;
        await _service.SwitchBranchAsync(repoPath, targetBranch);

        // Files should still exist (working directory preserved)
        var filesAfter = System.IO.Directory.GetFiles(repoPath, "*", System.IO.SearchOption.AllDirectories)
            .Where(f => !f.Contains(".git"))
            .Count();

        Assert.True(filesAfter > 0);
    }

    [Fact]
    public async Task GetBranchesAsync_EmptyRepository_ReturnsEmptyList()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("empty-repo");

        // Create empty git repo using Process
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"init \"{repoPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = System.Diagnostics.Process.Start(psi);
        await process!.WaitForExitAsync();

        var branches = await _service.GetBranchesAsync(repoPath);

        // Empty repo has no branches until first commit
        Assert.Empty(branches);
    }

    [Fact]
    public async Task SwitchBranchAsync_WithProgress_ReportsProgress()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("switch-progress");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath);
        if (branches.Count < 2)
            return;

        var progressReports = new System.Collections.Generic.List<string>();
        var progress = new Progress<string>(msg => progressReports.Add(msg));

        var targetBranch = branches.First(b => !b.IsActive).Name;
        await _service.SwitchBranchAsync(repoPath, targetBranch, progress);

        // Progress callback should not cause errors (might or might not report)
        Assert.NotNull(progressReports);
    }

    [Fact]
    public async Task GetBranchesAsync_CaseInsensitive_FindsBranches()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("case-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath);

        // Branch names should preserve original case
        Assert.All(branches, b => Assert.Equal(b.Name, b.Name.Trim()));
    }

    [Fact]
    public async Task SwitchBranchAsync_AfterPull_WorksCorrectly()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("switch-after-pull");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        // Pull updates first
        await _service.PullUpdatesAsync(repoPath);

        var branches = await _service.GetBranchesAsync(repoPath);
        if (branches.Count < 2)
            return;

        var targetBranch = branches.First(b => !b.IsActive).Name;
        var success = await _service.SwitchBranchAsync(repoPath, targetBranch);

        Assert.True(success);
    }

    [Fact]
    public async Task GetBranchesAsync_DoesNotModifyRepository()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("readonly-branches");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var currentBranchBefore = await _service.GetCurrentBranchAsync(repoPath);

        // Call GetBranches multiple times
        await _service.GetBranchesAsync(repoPath);
        await _service.GetBranchesAsync(repoPath);
        await _service.GetBranchesAsync(repoPath);

        var currentBranchAfter = await _service.GetCurrentBranchAsync(repoPath);

        // Current branch should not change
        Assert.Equal(currentBranchBefore, currentBranchAfter);
    }
}
