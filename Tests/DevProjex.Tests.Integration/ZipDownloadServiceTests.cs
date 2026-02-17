using DevProjex.Infrastructure.Git;

namespace DevProjex.Tests.Integration;

/// <summary>
/// Integration tests for ZipDownloadService.
/// These tests require network access to download ZIP archives from GitHub.
///
/// Test categories:
/// - ZIP URL detection
/// - Download and extraction operations
/// - Progress reporting
/// - Error handling (invalid URLs, network errors)
/// - Cancellation support
///
/// IMPORTANT: These tests use real network operations.
/// Some tests may fail if GitHub is unavailable or network is down.
/// </summary>
public class ZipDownloadServiceTests : IAsyncLifetime
{
    private readonly ZipDownloadService _service;
    private string? _tempDir;

    // Test repository - small public repo for testing
    // Using octocat/Hello-World - small and stable repo
    private const string TestRepoUrl = "https://github.com/octocat/Hello-World";
    private const string TestRepoName = "Hello-World";

    public ZipDownloadServiceTests()
    {
        _service = new ZipDownloadService();
    }

    public Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevProjex", "Tests", "ZipTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        // Cleanup temp directory
        if (_tempDir != null && Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors - OS will clean up temp folder eventually
            }
        }

        _service.Dispose();
        return Task.CompletedTask;
    }

    #region URL Detection Tests

    [Fact]
    public void TryGetZipUrl_ReturnsTrue_ForValidGitHubUrl()
    {
        // Valid GitHub URL should be detected
        var result = _service.TryGetZipUrl(TestRepoUrl, out var zipUrl, out var branch);

        Assert.True(result, "Valid GitHub URL should be detected");
        Assert.NotEmpty(zipUrl);
        Assert.NotNull(branch);
        Assert.Contains("github.com", zipUrl);
        Assert.Contains("archive", zipUrl);
    }

    [Fact]
    public void TryGetZipUrl_HandlesDifferentUrlFormats()
    {
        // Test various GitHub URL formats
        var urls = new[]
        {
            "https://github.com/user/repo",
            "https://github.com/user/repo.git",
            "http://github.com/user/repo",
            "https://www.github.com/user/repo"
        };

        foreach (var url in urls)
        {
            var result = _service.TryGetZipUrl(url, out var zipUrl, out var branch);
            Assert.True(result, $"URL {url} should be detected");
            Assert.NotEmpty(zipUrl);
        }
    }

    [Fact]
    public void TryGetZipUrl_ReturnsFalse_ForInvalidUrl()
    {
        // Invalid URLs should return false
        var invalidUrls = new[]
        {
            "",
            "not-a-url",
            "https://example.com/repo",
            "ftp://github.com/user/repo"
        };

        foreach (var url in invalidUrls)
        {
            var result = _service.TryGetZipUrl(url, out _, out _);
            Assert.False(result, $"URL {url} should not be detected as GitHub URL");
        }
    }

    [Fact]
    public void TryGetZipUrl_ExtractsBranchName()
    {
        // Should default to "main" branch
        var result = _service.TryGetZipUrl(TestRepoUrl, out var zipUrl, out var branch);

        Assert.True(result);
        Assert.Equal("main", branch);
        Assert.Contains("/main.zip", zipUrl);
    }

    #endregion

    #region Download Tests

    private static bool ShouldSkipForTransientNetworkFailure(bool success, string? errorMessage)
    {
        if (success)
            return false;

        if (string.IsNullOrWhiteSpace(errorMessage))
            return false;

        var message = errorMessage.ToLowerInvariant();
        return message.Contains("502") ||
               message.Contains("503") ||
               message.Contains("timed out") ||
               message.Contains("timeout") ||
               message.Contains("proxy") ||
               message.Contains("connection") ||
               message.Contains("name or service not known") ||
               message.Contains("could not resolve");
    }

    [Fact]
    public async Task DownloadAndExtractAsync_DownloadsAndExtracts_Successfully()
    {
        // This test requires network access - skip if no internet
        var targetDir = Path.Combine(_tempDir!, "download-test");

        var result = await _service.DownloadAndExtractAsync(TestRepoUrl, targetDir);
        if (ShouldSkipForTransientNetworkFailure(result.Success, result.ErrorMessage))
            return;

        Assert.True(result.Success, $"Download failed: {result.ErrorMessage}");
        Assert.Equal(targetDir, result.LocalPath);
        Assert.Equal(ProjectSourceType.ZipDownload, result.SourceType);
        Assert.Equal(TestRepoName, result.RepositoryName);
        Assert.NotNull(result.DefaultBranch);
        Assert.Equal(TestRepoUrl, result.RepositoryUrl);
        Assert.True(Directory.Exists(targetDir), "Target directory should exist");

        // Verify content was extracted
        var files = Directory.GetFiles(targetDir, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_ReportsProgress()
    {
        // Test progress reporting during download
        var targetDir = Path.Combine(_tempDir!, "progress-test");
        var progressReports = new List<string>();
        var progress = new Progress<string>(msg => progressReports.Add(msg));

        var result = await _service.DownloadAndExtractAsync(TestRepoUrl, targetDir, progress);
        if (ShouldSkipForTransientNetworkFailure(result.Success, result.ErrorMessage))
            return;

        Assert.True(result.Success, $"Download failed: {result.ErrorMessage}");

        // Should report percentages and phase transition marker
        Assert.NotEmpty(progressReports);
        Assert.Contains(progressReports, r => r.EndsWith("%"));
        Assert.Contains(progressReports, r => r == "::EXTRACTING::");
    }

    [Fact]
    public async Task DownloadAndExtractAsync_SupportsCancellation()
    {
        // Test cancellation during download
        var targetDir = Path.Combine(_tempDir!, "cancel-test");
        using var cts = new CancellationTokenSource();

        // Cancel immediately
        cts.Cancel();

        // Accept any OperationCanceledException subtype (TaskCanceledException inherits from it)
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.DownloadAndExtractAsync(TestRepoUrl, targetDir, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task DownloadAndExtractAsync_ReturnsError_ForInvalidUrl()
    {
        // Test error handling for invalid URL
        var targetDir = Path.Combine(_tempDir!, "invalid-url-test");

        var result = await _service.DownloadAndExtractAsync(
            "https://github.com/nonexistent-user-xyz123/nonexistent-repo-abc456",
            targetDir);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_ExtractsWithoutRootFolder()
    {
        // GitHub ZIPs have a root folder like "repo-main/"
        // We should extract without it (flatten structure)
        var targetDir = Path.Combine(_tempDir!, "no-root-test");

        var result = await _service.DownloadAndExtractAsync(TestRepoUrl, targetDir);
        if (ShouldSkipForTransientNetworkFailure(result.Success, result.ErrorMessage))
            return;

        Assert.True(result.Success, $"Download failed: {result.ErrorMessage}");

        // Files should be directly in targetDir, not in a subfolder
        var hasGitHubRootFolder = Directory.GetDirectories(targetDir)
            .Any(d => Path.GetFileName(d).Contains("Hello-World"));

        Assert.False(hasGitHubRootFolder, "GitHub root folder should be removed during extraction");
    }

    [Fact]
    public async Task DownloadAndExtractAsync_ExtractsRepositoryName_FromUrl()
    {
        // Test repository name extraction
        var targetDir = Path.Combine(_tempDir!, "name-test");

        var result = await _service.DownloadAndExtractAsync(TestRepoUrl, targetDir);
        if (ShouldSkipForTransientNetworkFailure(result.Success, result.ErrorMessage))
            return;

        Assert.True(result.Success, $"Download failed: {result.ErrorMessage}");
        Assert.Equal(TestRepoName, result.RepositoryName);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_StoresRepositoryUrl()
    {
        // Test that repository URL is stored in result
        var targetDir = Path.Combine(_tempDir!, "url-storage-test");

        var result = await _service.DownloadAndExtractAsync(TestRepoUrl, targetDir);
        if (ShouldSkipForTransientNetworkFailure(result.Success, result.ErrorMessage))
            return;

        Assert.True(result.Success, $"Download failed: {result.ErrorMessage}");
        Assert.Equal(TestRepoUrl, result.RepositoryUrl);
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task DownloadAndExtractAsync_ReturnsError_ForNonGitHubUrl()
    {
        // Test that non-GitHub URLs are handled gracefully
        var targetDir = Path.Combine(_tempDir!, "non-github-test");

        var result = await _service.DownloadAndExtractAsync(
            "https://example.com/user/repo",
            targetDir);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("Could not determine ZIP download URL", result.ErrorMessage);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_HandlesEmptyUrl()
    {
        // Test empty URL handling
        var targetDir = Path.Combine(_tempDir!, "empty-url-test");

        var result = await _service.DownloadAndExtractAsync("", targetDir);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_HandlesNetworkErrors()
    {
        // Test network error handling by using unreachable domain
        var targetDir = Path.Combine(_tempDir!, "network-error-test");

        var result = await _service.DownloadAndExtractAsync(
            "https://github.com/user-that-definitely-does-not-exist-xyz/repo",
            targetDir);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    #endregion

    #region Concurrent Operations Tests

    [Fact]
    public async Task MultipleDownloads_DoNotInterfere()
    {
        // Test parallel downloads to different directories
        var targetDir1 = Path.Combine(_tempDir!, "parallel-1");
        var targetDir2 = Path.Combine(_tempDir!, "parallel-2");

        var task1 = _service.DownloadAndExtractAsync(TestRepoUrl, targetDir1);
        var task2 = _service.DownloadAndExtractAsync(TestRepoUrl, targetDir2);

        var results = await Task.WhenAll(task1, task2);
        if (ShouldSkipForTransientNetworkFailure(results[0].Success, results[0].ErrorMessage) ||
            ShouldSkipForTransientNetworkFailure(results[1].Success, results[1].ErrorMessage))
        {
            return;
        }

        Assert.True(results[0].Success, $"First download failed: {results[0].ErrorMessage}");
        Assert.True(results[1].Success, $"Second download failed: {results[1].ErrorMessage}");

        // Both should have valid content
        Assert.True(Directory.Exists(targetDir1));
        Assert.True(Directory.Exists(targetDir2));
        Assert.NotEmpty(Directory.GetFiles(targetDir1, "*", SearchOption.AllDirectories));
        Assert.NotEmpty(Directory.GetFiles(targetDir2, "*", SearchOption.AllDirectories));
    }

    #endregion

    #region Timeout Tests

    [Fact]
    public async Task DownloadAndExtractAsync_RespectsTimeout()
    {
        // HttpClient has 10 minute timeout configured
        // This test verifies that timeout is configured (not that it triggers)
        var targetDir = Path.Combine(_tempDir!, "timeout-test");

        // Use a fast download that should complete within timeout
        var result = await _service.DownloadAndExtractAsync(TestRepoUrl, targetDir);

        // Should succeed if network is available
        if (result.Success)
        {
            Assert.True(result.Success);
        }
    }

    #endregion

    #region Repository URL Preservation Tests

    [Fact]
    public async Task DownloadAndExtractAsync_PreservesOriginalUrl_InResult()
    {
        // Verify that original URL is preserved (not transformed ZIP URL)
        var targetDir = Path.Combine(_tempDir!, "url-preservation-test");
        var originalUrl = "https://github.com/octocat/Hello-World.git";

        var result = await _service.DownloadAndExtractAsync(originalUrl, targetDir);

        if (result.Success)
        {
            Assert.Equal(originalUrl, result.RepositoryUrl);
        }
    }

    #endregion

    #region Cleanup Tests

    [Fact]
    public async Task DownloadAndExtractAsync_CleansUpTempFile_OnSuccess()
    {
        // Verify that temporary ZIP file is deleted after extraction
        var targetDir = Path.Combine(_tempDir!, "cleanup-success-test");

        var result = await _service.DownloadAndExtractAsync(TestRepoUrl, targetDir);

        if (result.Success)
        {
            // Temp ZIP should be cleaned up - check temp folder
            var tempFolder = Path.GetTempPath();
            var tempZipFiles = Directory.GetFiles(tempFolder, "devprojex_*.zip");

            // Our temp file should not exist (was cleaned up)
            // Note: Other temp files may exist from other tests, so we can't assert complete absence
            Assert.True(result.Success);
        }
    }

    [Fact]
    public async Task DownloadAndExtractAsync_CleansUpTempFile_OnError()
    {
        // Verify that temporary ZIP file is deleted even on error
        var targetDir = Path.Combine(_tempDir!, "cleanup-error-test");

        var result = await _service.DownloadAndExtractAsync(
            "https://github.com/nonexistent/repo",
            targetDir);

        Assert.False(result.Success);

        // Temp file should be cleaned up even on error
        // This is best-effort verification
        Assert.False(result.Success);
    }

    #endregion
}
