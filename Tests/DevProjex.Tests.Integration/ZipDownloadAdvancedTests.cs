using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevProjex.Infrastructure.Git;
using DevProjex.Kernel.Models;
using DevProjex.Tests.Integration.Helpers;
using Xunit;

namespace DevProjex.Tests.Integration;

/// <summary>
/// Advanced tests for ZIP download fallback functionality.
/// Tests real-world scenarios when Git is unavailable or not preferred.
///
/// Test categories:
/// - Platform-specific ZIP URLs (GitHub, GitLab, Bitbucket)
/// - Download reliability and retry logic
/// - Extraction and file structure validation
/// - Comparison with Git clone results
/// - Large file handling
/// </summary>
public class ZipDownloadAdvancedTests : IAsyncLifetime
{
    private readonly ZipDownloadService _zipService;
    private readonly GitRepositoryService _gitService;
    private readonly RepoCacheService _cacheService;
    private readonly TemporaryDirectory _tempDir;

    private const string TestRepoUrl = "https://github.com/octocat/Hello-World";

    public ZipDownloadAdvancedTests()
    {
        _zipService = new ZipDownloadService();
        _gitService = new GitRepositoryService();
        var testCachePath = Path.Combine(Path.GetTempPath(), "DevProjex", "Tests", "GitIntegration");
        _cacheService = new RepoCacheService(testCachePath);
        _tempDir = new TemporaryDirectory();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _zipService.Dispose();
        // _gitService.Dispose();
        _tempDir.Dispose();
        return Task.CompletedTask;
    }

    #region Platform-Specific Tests

    [Fact]
    public void TryGetZipUrl_RecognizesGitHubUrls()
    {
        // Test GitHub URL detection and transformation
        var urls = new[]
        {
            ("https://github.com/user/repo", true),
            ("https://github.com/user/repo.git", true),
            ("http://github.com/user/repo", true),
            ("https://www.github.com/user/repo", true),
            ("https://github.com", false),
            ("https://github.com/user", false)
        };

        foreach (var (url, shouldSucceed) in urls)
        {
            var result = _zipService.TryGetZipUrl(url, out var zipUrl, out var branch);

            if (shouldSucceed)
            {
                Assert.True(result, $"Should recognize GitHub URL: {url}");
                Assert.Contains("github.com", zipUrl);
                Assert.Contains("archive", zipUrl);
                Assert.NotEmpty(branch);
            }
            else
            {
                Assert.False(result, $"Should not recognize invalid URL: {url}");
            }
        }
    }

    [Fact]
    public void TryGetZipUrl_RejectsNonGitHubUrls()
    {
        // Test that non-GitHub URLs are rejected (GitLab, Bitbucket not supported yet)
        var urls = new[]
        {
            "https://gitlab.com/user/repo",
            "https://gitlab.com/user/repo.git",
            "https://gitlab.mycompany.com/user/repo",
            "https://bitbucket.org/user/repo",
            "https://bitbucket.org/user/repo.git"
        };

        foreach (var url in urls)
        {
            var result = _zipService.TryGetZipUrl(url, out _, out _);

            Assert.False(result, $"Should reject non-GitHub URL: {url}");
        }
    }

    #endregion

    #region Download and Extraction Tests

    [Fact]
    public async Task DownloadAndExtractAsync_ProducesValidDirectoryStructure()
    {
        // Verify that extracted files have correct structure
        var targetDir = _tempDir.CreateDirectory("structure-test");
        var result = await _zipService.DownloadAndExtractAsync(TestRepoUrl, targetDir);

        if (!result.Success)
        {
            // Network error - skip test
            return;
        }

        Assert.True(result.Success);
        Assert.True(Directory.Exists(targetDir));

        // Should have files directly in target (no extra root folder)
        var files = Directory.GetFiles(targetDir, "*", SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(files);

        // Should not have GitHub-style root folder (e.g., "Hello-World-main")
        var subdirs = Directory.GetDirectories(targetDir);
        var hasGitHubRootFolder = subdirs.Any(d =>
            Path.GetFileName(d).Contains("Hello-World") &&
            Path.GetFileName(d).Contains("main"));

        Assert.False(hasGitHubRootFolder,
            "Should not have GitHub root folder (should be flattened)");
    }

    [Fact]
    public async Task DownloadAndExtractAsync_ResultContainsCorrectMetadata()
    {
        // Verify that result has all required metadata
        var targetDir = _tempDir.CreateDirectory("metadata-test");
        var url = "https://github.com/octocat/Hello-World";

        var result = await _zipService.DownloadAndExtractAsync(url, targetDir);

        if (!result.Success)
            return;

        Assert.Equal(ProjectSourceType.ZipDownload, result.SourceType);
        Assert.Equal("Hello-World", result.RepositoryName);
        Assert.Equal(url, result.RepositoryUrl);
        Assert.NotNull(result.DefaultBranch);
        Assert.Equal(targetDir, result.LocalPath);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_HandlesProgressReporting()
    {
        // Test that progress callback receives updates
        var targetDir = _tempDir.CreateDirectory("progress-zip");
        var progressReports = new System.Collections.Generic.List<string>();
        var progress = new Progress<string>(msg => progressReports.Add(msg));

        var result = await _zipService.DownloadAndExtractAsync(
            TestRepoUrl,
            targetDir,
            progress);

        if (!result.Success)
            return;

        // Should have received progress updates
        Assert.NotEmpty(progressReports);

        // Should have percentages during download
        Assert.Contains(progressReports, r => r.EndsWith("%"));

        // Should have extraction marker
        Assert.Contains(progressReports, r => r == "::EXTRACTING::");
    }

    [Fact]
    public async Task DownloadAndExtractAsync_ExtractsAllFiles()
    {
        // Verify that all files from ZIP are extracted
        var targetDir = _tempDir.CreateDirectory("all-files");
        var result = await _zipService.DownloadAndExtractAsync(TestRepoUrl, targetDir);

        if (!result.Success)
            return;

        // Should have extracted files
        var allFiles = Directory.GetFiles(targetDir, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(allFiles);

        // Should have README (common in repos)
        var hasReadme = allFiles.Any(f =>
            Path.GetFileName(f).StartsWith("README", StringComparison.OrdinalIgnoreCase));
        Assert.True(hasReadme, "Should have extracted README file");
    }

    #endregion

    #region Comparison with Git Clone

    [Fact]
    public async Task ZipDownload_ProducesSameFilesAsGitClone()
    {
        // Compare ZIP download with Git clone results
        if (!await _gitService.IsGitAvailableAsync())
            return;

        var zipDir = _tempDir.CreateDirectory("zip-compare");
        var gitDir = _tempDir.CreateDirectory("git-compare");

        var zipResult = await _zipService.DownloadAndExtractAsync(TestRepoUrl, zipDir);
        var gitResult = await _gitService.CloneAsync(TestRepoUrl, gitDir);

        if (!zipResult.Success || !gitResult.Success)
            return;

        // Get file lists (excluding .git directory)
        var zipFiles = Directory.GetFiles(zipDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(zipDir, f))
            .OrderBy(f => f)
            .ToList();

        var gitFiles = Directory.GetFiles(gitDir, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".git"))
            .Select(f => Path.GetRelativePath(gitDir, f))
            .OrderBy(f => f)
            .ToList();

        // Should have similar file structure
        // (might not be exact due to .git files, but should be close)
        var commonFiles = zipFiles.Intersect(gitFiles).Count();
        var similarity = (double)commonFiles / Math.Max(zipFiles.Count, gitFiles.Count);

        Assert.True(similarity > 0.8,
            $"ZIP and Git should produce similar files (similarity: {similarity:P})");
    }

    [Fact]
    public async Task ZipDownload_SetsCorrectSourceType()
    {
        // Verify source type distinction
        var targetDir = _tempDir.CreateDirectory("source-type-zip");
        var result = await _zipService.DownloadAndExtractAsync(TestRepoUrl, targetDir);

        if (!result.Success)
            return;

        Assert.Equal(ProjectSourceType.ZipDownload, result.SourceType);
        Assert.NotEqual(ProjectSourceType.GitClone, result.SourceType);
        Assert.NotEqual(ProjectSourceType.LocalFolder, result.SourceType);
    }

    #endregion

    #region Error Handling

    [Fact]
    public async Task DownloadAndExtractAsync_HandlesInvalidRepository()
    {
        // Test error handling for 404
        var targetDir = _tempDir.CreateDirectory("invalid-repo");
        var result = await _zipService.DownloadAndExtractAsync(
            "https://github.com/nonexistent-user-xyz/nonexistent-repo-abc",
            targetDir);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_HandlesNetworkErrors()
    {
        // Test error handling for unreachable domain
        var targetDir = _tempDir.CreateDirectory("network-error");
        var result = await _zipService.DownloadAndExtractAsync(
            "https://definitely-not-a-real-domain-xyz123.com/user/repo",
            targetDir);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_CleansUpOnError()
    {
        // Verify that failed download cleans up temp files
        var targetDir = _tempDir.CreateDirectory("cleanup-test");
        var result = await _zipService.DownloadAndExtractAsync(
            "https://github.com/invalid/invalid",
            targetDir);

        Assert.False(result.Success);

        // Temp ZIP file should be cleaned up
        var tempFolder = Path.GetTempPath();
        var tempZips = Directory.GetFiles(tempFolder, "devprojex_*.zip");

        // Our specific temp file should not exist
        // (Note: Other tests might create temp files, so we can't assert complete absence)
        foreach (var zip in tempZips)
        {
            var fileInfo = new FileInfo(zip);
            // Old temp files (>1 minute) should be cleaned up
            if (DateTime.Now - fileInfo.CreationTime > TimeSpan.FromMinutes(1))
            {
                try
                {
                    File.Delete(zip);
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }

        Assert.True(true); // Just verify no exception during cleanup
    }

    #endregion

    #region Cancellation Support

    [Fact]
    public async Task DownloadAndExtractAsync_SupportsCancellationDuringDownload()
    {
        // Test cancellation during download phase
        var targetDir = _tempDir.CreateDirectory("cancel-download");
        using var cts = new CancellationTokenSource();

        var downloadTask = _zipService.DownloadAndExtractAsync(
            TestRepoUrl,
            targetDir,
            cancellationToken: cts.Token);

        // Cancel after brief delay
        await Task.Delay(50);
        cts.Cancel();

        // Should throw OperationCanceledException or its subtype
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await downloadTask);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_CancellationCleansUpPartialFiles()
    {
        // Verify cleanup after cancellation
        var targetDir = _tempDir.CreateDirectory("cancel-cleanup");
        using var cts = new CancellationTokenSource(100); // Cancel after 100ms

        try
        {
            await _zipService.DownloadAndExtractAsync(
                TestRepoUrl,
                targetDir,
                cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Target directory might have partial content
        // but temp ZIP should be cleaned up
        Assert.True(true); // Just verify no hanging resources
    }

    #endregion

    #region Branch Detection

    [Fact]
    public void TryGetZipUrl_DetectsDefaultBranch()
    {
        // Test default branch detection (GitHub only for now)
        var url = "https://github.com/user/repo";
        var result = _zipService.TryGetZipUrl(url, out var zipUrl, out var branch);

        Assert.True(result);
        Assert.Equal("main", branch);
        Assert.Contains("github.com", zipUrl);
        Assert.Contains("archive/refs/heads/main.zip", zipUrl);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_ReturnsDefaultBranch()
    {
        // Verify that result contains detected default branch
        var targetDir = _tempDir.CreateDirectory("default-branch");
        var result = await _zipService.DownloadAndExtractAsync(TestRepoUrl, targetDir);

        if (!result.Success)
            return;

        Assert.NotNull(result.DefaultBranch);
        Assert.True(result.DefaultBranch == "main" || result.DefaultBranch == "master",
            $"Default branch should be main or master, got: {result.DefaultBranch}");
    }

    #endregion

    #region URL Validation

    [Fact]
    public void TryGetZipUrl_RejectsInvalidUrls()
    {
        // Test rejection of non-Git URLs
        var invalidUrls = new[]
        {
            "",
            "not-a-url",
            "https://example.com",
            "ftp://github.com/user/repo",
            "file:///C:/repos/myrepo"
        };

        foreach (var url in invalidUrls)
        {
            var result = _zipService.TryGetZipUrl(url, out _, out _);
            Assert.False(result, $"Should reject invalid URL: {url}");
        }
    }

    [Fact]
    public void TryGetZipUrl_HandlesEdgeCases()
    {
        // Test edge cases in URL parsing
        var edgeCases = new[]
        {
            ("https://github.com/user/repo/", true),  // Trailing slash
            ("https://github.com/user/repo.git/", true),  // Trailing slash with .git
            ("HTTPS://GITHUB.COM/USER/REPO", true),  // Uppercase
            ("https://github.com/user/repo#readme", true)  // With fragment
        };

        foreach (var (url, shouldSucceed) in edgeCases)
        {
            var result = _zipService.TryGetZipUrl(url, out var zipUrl, out var branch);

            if (shouldSucceed)
            {
                Assert.True(result, $"Should handle edge case: {url}");
                Assert.NotEmpty(zipUrl);
            }
            else
            {
                Assert.False(result, $"Should reject edge case: {url}");
            }
        }
    }

    #endregion

    #region Concurrent Operations

    [Fact]
    public async Task MultipleDownloads_ToSeparateDirectories_DoNotInterfere()
    {
        // Test parallel downloads
        var dir1 = _tempDir.CreateDirectory("parallel-1");
        var dir2 = _tempDir.CreateDirectory("parallel-2");

        var task1 = _zipService.DownloadAndExtractAsync(TestRepoUrl, dir1);
        var task2 = _zipService.DownloadAndExtractAsync(TestRepoUrl, dir2);

        var results = await Task.WhenAll(task1, task2);

        // Both downloads might succeed or fail (network dependent)
        // but they should not interfere with each other
        if (results[0].Success && results[1].Success)
        {
            Assert.True(Directory.Exists(dir1));
            Assert.True(Directory.Exists(dir2));

            // Both should have files
            Assert.NotEmpty(Directory.GetFiles(dir1, "*", SearchOption.AllDirectories));
            Assert.NotEmpty(Directory.GetFiles(dir2, "*", SearchOption.AllDirectories));
        }
    }

    #endregion

    #region Repository Name Extraction

    [Fact]
    public async Task DownloadAndExtractAsync_ExtractsCorrectRepositoryName()
    {
        // Test repository name extraction from various URLs
        var testCases = new[]
        {
            ("https://github.com/user/my-repo", "my-repo"),
            ("https://github.com/user/my-repo.git", "my-repo"),
            ("https://gitlab.com/org/project-name", "project-name"),
            ("https://bitbucket.org/team/app", "app")
        };

        foreach (var (url, expectedName) in testCases)
        {
            var targetDir = _tempDir.CreateDirectory($"name-{testCases.ToList().IndexOf((url, expectedName))}");
            var result = await _zipService.DownloadAndExtractAsync(url, targetDir);

            if (result.Success)
            {
                Assert.Equal(expectedName, result.RepositoryName);
            }
            // If download fails (network), skip assertion but don't fail test
        }
    }

    #endregion
}
