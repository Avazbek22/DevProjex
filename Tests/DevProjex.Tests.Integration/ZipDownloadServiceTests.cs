using System.IO.Compression;
using System.Net;

namespace DevProjex.Tests.Integration;

/// <summary>
/// Integration tests for ZipDownloadService.
/// Test categories:
/// - ZIP URL detection
/// - Download and extraction operations
/// - Progress reporting
/// - Error handling (invalid URLs, network errors)
/// - Cancellation support
/// </summary>
public class ZipDownloadServiceTests : IAsyncLifetime
{
    private readonly ZipDownloadService _service = new(new GitHubArchiveHandler());
    private string? _tempDir;

    private const string TestRepoUrl = "https://github.com/octocat/Hello-World";
    private const string TestRepoName = "Hello-World";

    public ValueTask InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevProjex", "Tests", "ZipTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
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
        return ValueTask.CompletedTask;
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
    public void TryGetZipUrl_StripsQueryAndFragmentBeforeBuildingArchiveUrl()
    {
        var result = _service.TryGetZipUrl(
            "https://github.com/owner/repo.git?tab=readme#files",
            out var zipUrl,
            out _);

        Assert.True(result);
        Assert.Equal("https://github.com/owner/repo/archive/refs/heads/main.zip", zipUrl);
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

    [Fact]
    public void DefaultResourceLimits_MatchTheUntrustedArchivePolicy()
    {
        const long gibibyte = 1024L * 1024 * 1024;
        var limits = ZipResourceLimits.Default;

        Assert.Equal(2 * gibibyte, limits.MaxDownloadedArchiveBytes);
        Assert.Equal(8 * gibibyte, limits.MaxTotalExtractedBytes);
        Assert.Equal(2 * gibibyte, limits.MaxSingleEntryBytes);
        Assert.Equal(250_000, limits.MaxEntryCount);
        Assert.Equal(128, limits.MaxPathDepth);
        Assert.Equal(200, limits.MaxCompressionRatio);
        Assert.Equal(gibibyte, limits.FreeSpaceReserveBytes);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_DownloadsAndExtracts_Successfully()
    {
        var targetDir = Path.Combine(_tempDir!, "download-test");

        var result = await _service.DownloadAndExtractAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);

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
    public async Task DownloadAndExtractAsync_UsesDefaultBranchFromGitHubMetadata()
    {
        var archive = CreateArchive(("repo-develop/file.txt", "develop"u8.ToArray()));
        var handler = new DefaultBranchArchiveHandler("develop", archive);
        using var service = new ZipDownloadService(handler, ZipResourceLimits.Default with
        {
            FreeSpaceReserveBytes = 0
        });
        var targetDir = Path.Combine(_tempDir!, "develop-default");

        var result = await service.DownloadAndExtractAsync(
            "https://github.com/owner/repo",
            targetDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("develop", result.DefaultBranch);
        Assert.Contains("/refs/heads/develop.zip", handler.ArchiveRequestUri, StringComparison.Ordinal);
        Assert.Equal("develop", await File.ReadAllTextAsync(
            Path.Combine(targetDir, "file.txt"),
            TestContext.Current.CancellationToken));
    }

	[Fact]
	public async Task DownloadAndExtractAsync_DefaultBranchWithSlash_PreservesBranchSegmentsInArchiveUrl()
	{
		var archive = CreateArchive(("repo-release-5.1/file.txt", "release"u8.ToArray()));
		var handler = new DefaultBranchArchiveHandler("release/5.1", archive);
		using var service = new ZipDownloadService(handler, ZipResourceLimits.Default with
		{
			FreeSpaceReserveBytes = 0
		});
		var targetDir = Path.Combine(_tempDir!, "release-default");

		var result = await service.DownloadAndExtractAsync(
			"https://github.com/owner/repo",
			targetDir,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(result.Success, result.ErrorMessage);
		Assert.Equal("release/5.1", result.DefaultBranch);
		Assert.Contains(
			"/archive/refs/heads/release/5.1.zip",
			handler.ArchiveRequestUri,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task DownloadAndExtractAsync_OversizedRepositoryMetadataUsesLegacyBranchFallback()
	{
		var archive = CreateArchive(("repo-main/file.txt", "main"u8.ToArray()));
		var metadata = Encoding.UTF8.GetBytes(
			$"{{\"default_branch\":\"develop\",\"padding\":\"{new string('x', 70 * 1024)}\"}}");
		var handler = new DefaultBranchArchiveHandler("develop", archive, metadata);
		using var service = new ZipDownloadService(handler, ZipResourceLimits.Default with
		{
			FreeSpaceReserveBytes = 0
		});
		var targetDir = Path.Combine(_tempDir!, "oversized-metadata");

		var result = await service.DownloadAndExtractAsync(
			"https://github.com/owner/repo",
			targetDir,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(result.Success, result.ErrorMessage);
		Assert.Equal("main", result.DefaultBranch);
		Assert.Contains("/refs/heads/main.zip", handler.ArchiveRequestUri, StringComparison.Ordinal);
	}

	[Fact]
	public void CreateZipUrl_EncodesSpecialCharactersInsideEachBranchSegment()
	{
		var url = ZipDownloadService.CreateZipUrl(
			"owner",
			"repo",
			"release candidate/#5");

		Assert.Equal(
			"https://github.com/owner/repo/archive/refs/heads/release%20candidate/%235.zip",
			url);
	}

    [Theory]
	[InlineData("nul")]
	[InlineData("CON.txt")]
	[InlineData("com1.tar.gz")]
	[InlineData("COM\u00B9.txt")]
	[InlineData("src/LPT\u00B2/file.cs")]
	[InlineData("src/AUX/x.cs")]
	public async Task DownloadAndExtractAsync_RejectsWindowsReservedDeviceNames(string entryPath)
	{
		var archive = CreateArchive(($"repo-main/{entryPath}", "payload"u8.ToArray()));
		using var service = new ZipDownloadService(
			new ArchiveBytesHandler(archive),
			ZipResourceLimits.Default with { FreeSpaceReserveBytes = 0 });
		var targetDir = Path.Combine(_tempDir!, $"reserved-{Guid.NewGuid():N}");

		var result = await service.DownloadAndExtractAsync(
			TestRepoUrl,
			targetDir,
			cancellationToken: TestContext.Current.CancellationToken);

		if (OperatingSystem.IsWindows())
		{
			Assert.False(result.Success);
			Assert.Contains("reserved Windows device name", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
			Assert.False(Directory.Exists(targetDir));
		}
		else
		{
			Assert.True(result.Success, result.ErrorMessage);
		}
	}

	[Theory]
	[InlineData("COM10")]
	[InlineData("console.log")]
	[InlineData("nullable.cs")]
	public async Task DownloadAndExtractAsync_AllowsNonReservedSimilarNames(string entryPath)
	{
		var archive = CreateArchive(($"repo-main/{entryPath}", "payload"u8.ToArray()));
		using var service = new ZipDownloadService(
			new ArchiveBytesHandler(archive),
			ZipResourceLimits.Default with { FreeSpaceReserveBytes = 0 });
		var targetDir = Path.Combine(_tempDir!, $"allowed-{Guid.NewGuid():N}");

		var result = await service.DownloadAndExtractAsync(
			TestRepoUrl,
			targetDir,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(result.Success, result.ErrorMessage);
		Assert.True(File.Exists(Path.Combine(targetDir, entryPath.Replace('/', Path.DirectorySeparatorChar))));
	}

	[Fact]
	public async Task DownloadAndExtractAsync_RejectsNtfsAlternateDataStreamPathOnWindows()
	{
		const string entryPath = "readme.txt:hidden";
		var archive = CreateArchive(($"repo-main/{entryPath}", "payload"u8.ToArray()));
		using var service = new ZipDownloadService(
			new ArchiveBytesHandler(archive),
			ZipResourceLimits.Default with { FreeSpaceReserveBytes = 0 });
		var targetDir = Path.Combine(_tempDir!, $"alternate-stream-{Guid.NewGuid():N}");

		var result = await service.DownloadAndExtractAsync(
			TestRepoUrl,
			targetDir,
			cancellationToken: TestContext.Current.CancellationToken);

		if (OperatingSystem.IsWindows())
		{
			Assert.False(result.Success);
			Assert.Contains("alternate data stream", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
			Assert.False(Directory.Exists(targetDir));
		}
		else
		{
			Assert.True(result.Success, result.ErrorMessage);
			Assert.True(File.Exists(Path.Combine(targetDir, entryPath)));
		}
	}

    [Theory]
    [InlineData("case")]
    [InlineData("duplicate")]
    [InlineData("file-directory")]
    [InlineData("unicode")]
    public async Task DownloadAndExtractAsync_RejectsCanonicalPathCollisions(string collisionKind)
    {
        var entries = collisionKind switch
        {
            "case" => new[]
            {
                ("repo-main/Foo.cs", "first"u8.ToArray()),
                ("repo-main/foo.cs", "second"u8.ToArray())
            },
            "duplicate" => new[]
            {
                ("repo-main/file.txt", "first"u8.ToArray()),
                ("repo-main/file.txt", "second"u8.ToArray())
            },
            "file-directory" => new[]
            {
                ("repo-main/path", "file"u8.ToArray()),
                ("repo-main/path/child.txt", "child"u8.ToArray())
            },
            "unicode" => new[]
            {
                ("repo-main/caf\u00E9.txt", "first"u8.ToArray()),
                ("repo-main/cafe\u0301.txt", "second"u8.ToArray())
            },
            _ => throw new ArgumentOutOfRangeException(nameof(collisionKind))
        };
        var archive = CreateArchive(entries);
        using var service = new ZipDownloadService(
            new ArchiveBytesHandler(archive),
            ZipResourceLimits.Default with { FreeSpaceReserveBytes = 0 });
        var targetDir = Path.Combine(_tempDir!, $"collision-{collisionKind}");

        var result = await service.DownloadAndExtractAsync(
            TestRepoUrl,
            targetDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("same file-system path", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(targetDir));
    }

    [Fact]
    public async Task DownloadAndExtractAsync_RejectsTraversalWhenItIsTheFirstArchiveEntry()
    {
        var archive = CreateArchive(("../outside.txt", "payload"u8.ToArray()));
        using var service = new ZipDownloadService(
            new ArchiveBytesHandler(archive),
            ZipResourceLimits.Default with { FreeSpaceReserveBytes = 0 });
        var targetDir = Path.Combine(_tempDir!, "first-entry-traversal");

        var result = await service.DownloadAndExtractAsync(
            TestRepoUrl,
            targetDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("invalid path", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(targetDir));
        Assert.False(File.Exists(Path.Combine(_tempDir!, "outside.txt")));
    }

    [Fact]
    public async Task DownloadAndExtractAsync_OnWindowsRejectsDriveLikeFirstArchiveEntry()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var archive = CreateArchive(("C:/outside.txt", "payload"u8.ToArray()));
        using var service = new ZipDownloadService(
            new ArchiveBytesHandler(archive),
            ZipResourceLimits.Default with { FreeSpaceReserveBytes = 0 });
        var targetDir = Path.Combine(_tempDir!, "first-entry-drive");

        var result = await service.DownloadAndExtractAsync(
            TestRepoUrl,
            targetDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("alternate data stream", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(targetDir));
    }

    [Fact]
    public async Task DownloadAndExtractAsync_FailureDuringStagedExtractionLeavesOccupiedTargetUntouched()
    {
        var archive = CreateArchive(
            ("repo-main/first.txt", "first"u8.ToArray()),
            ("repo-main/second.txt", "second"u8.ToArray()));
        var openedEntries = 0;
        using var service = new ZipDownloadService(
            new ArchiveBytesHandler(archive),
            ZipResourceLimits.Default with { FreeSpaceReserveBytes = 0 },
            entry => Interlocked.Increment(ref openedEntries) == 2
                ? throw new IOException("Simulated extraction failure.")
                : entry.Open());
        var targetDir = Path.Combine(_tempDir!, "occupied-target");
        Directory.CreateDirectory(targetDir);
        var originalPath = Path.Combine(targetDir, "original.txt");
        await File.WriteAllTextAsync(originalPath, "original", TestContext.Current.CancellationToken);

        var result = await service.DownloadAndExtractAsync(
            TestRepoUrl,
            targetDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("original", await File.ReadAllTextAsync(originalPath, TestContext.Current.CancellationToken));
        Assert.Single(Directory.EnumerateFileSystemEntries(targetDir));
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(_tempDir!),
            path => Path.GetFileName(path).Contains(".occupied-target.extract-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DownloadAndExtractAsync_ReportsProgress()
    {
        // Test progress reporting during download
        var targetDir = Path.Combine(_tempDir!, "progress-test");
        var progress = new ProgressRecorder();

        var result = await _service.DownloadAndExtractAsync(TestRepoUrl, targetDir, progress, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, $"Download failed: {result.ErrorMessage}");

        ProgressAssertions.AssertCompletedZipDownload(progress.Reports);
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
            targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_ExtractsWithoutRootFolder()
    {
        // GitHub ZIPs have a root folder like "repo-main/"
        // We should extract without it (flatten structure)
        var targetDir = Path.Combine(_tempDir!, "no-root-test");

        var result = await _service.DownloadAndExtractAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);

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

        var result = await _service.DownloadAndExtractAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, $"Download failed: {result.ErrorMessage}");
        Assert.Equal(TestRepoName, result.RepositoryName);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_StoresRepositoryUrl()
    {
        // Test that repository URL is stored in result
        var targetDir = Path.Combine(_tempDir!, "url-storage-test");

        var result = await _service.DownloadAndExtractAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);

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
            targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("Could not determine ZIP download URL", result.ErrorMessage);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_HandlesEmptyUrl()
    {
        // Test empty URL handling
        var targetDir = Path.Combine(_tempDir!, "empty-url-test");

        var result = await _service.DownloadAndExtractAsync("", targetDir, cancellationToken: TestContext.Current.CancellationToken);

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
            targetDir, cancellationToken: TestContext.Current.CancellationToken);

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

        var task1 = _service.DownloadAndExtractAsync(TestRepoUrl, targetDir1, cancellationToken: TestContext.Current.CancellationToken);
        var task2 = _service.DownloadAndExtractAsync(TestRepoUrl, targetDir2, cancellationToken: TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(task1, task2);

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
        // The deterministic response must complete within the production timeout.
        var targetDir = Path.Combine(_tempDir!, "timeout-test");

        var result = await _service.DownloadAndExtractAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
    }

    #endregion

    #region Repository URL Preservation Tests

    [Fact]
    public async Task DownloadAndExtractAsync_PreservesOriginalUrl_InResult()
    {
        // Verify that original URL is preserved (not transformed ZIP URL)
        var targetDir = Path.Combine(_tempDir!, "url-preservation-test");
        var originalUrl = "https://github.com/octocat/Hello-World.git";

        var result = await _service.DownloadAndExtractAsync(originalUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(originalUrl, result.RepositoryUrl);
    }

    #endregion

    #region Cleanup Tests

    [Fact]
    public void TemporaryArchiveHandle_DeletesStorageWhenClosed()
    {
        var path = Path.Combine(_tempDir!, $"{Guid.NewGuid():N}.zip");

        using (var stream = ZipDownloadService.OpenTemporaryArchive(path))
        {
            stream.WriteByte(1);
            Assert.Equal(1, stream.Length);
        }

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DownloadAndExtractAsync_CleansUpTempFile_OnSuccess()
    {
        // Verify that temporary ZIP file is deleted after extraction
        var targetDir = Path.Combine(_tempDir!, "cleanup-success-test");

        var result = await _service.DownloadAndExtractAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(targetDir, "README.md")));
    }

    [Fact]
    public async Task DownloadAndExtractAsync_CleansUpTempFile_OnError()
    {
        // Verify that temporary ZIP file is deleted even on error
        var targetDir = Path.Combine(_tempDir!, "cleanup-error-test");

        var result = await _service.DownloadAndExtractAsync(
            "https://github.com/nonexistent/repo",
            targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);

        // Temp file should be cleaned up even on error
        // This is best-effort verification
        Assert.False(result.Success);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_RejectsActualCompressionRatioAndCleansStaging()
    {
        var archive = CreateArchive(("repo-main/payload.bin", new byte[1]));
        var limits = ZipResourceLimits.Default with
        {
            MaxCompressionRatio = 2,
            FreeSpaceReserveBytes = 0
        };
        var targetDir = CreateEmptyTarget("ratio-limit");
        using var service = new ZipDownloadService(
            new ArchiveBytesHandler(archive),
            limits,
            _ => new MemoryStream(new byte[4096], writable: false));

        var result = await service.DownloadAndExtractAsync(
            TestRepoUrl,
            targetDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("compression ratio", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(targetDir));
    }

    [Fact]
    public async Task DownloadAndExtractAsync_RejectsEntryCountBeforeExtraction()
    {
        var archive = CreateArchive(
            ("repo-main/a.txt", "a"u8.ToArray()),
            ("repo-main/b.txt", "b"u8.ToArray()),
            ("repo-main/c.txt", "c"u8.ToArray()));
        var limits = ZipResourceLimits.Default with
        {
            MaxEntryCount = 2,
            FreeSpaceReserveBytes = 0
        };
        var targetDir = CreateEmptyTarget("entry-count-limit");
        using var service = new ZipDownloadService(new ArchiveBytesHandler(archive), limits);

        var result = await service.DownloadAndExtractAsync(
            TestRepoUrl,
            targetDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("entry count", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(targetDir));
    }

    [Fact]
    public async Task DownloadAndExtractAsync_RejectsActualEntryBytesWhenDeclaredSizeIsSmall()
    {
        var archive = CreateArchive(("repo-main/payload.bin", new byte[1]));
        var limits = ZipResourceLimits.Default with
        {
            MaxSingleEntryBytes = 64,
            MaxCompressionRatio = 10_000,
            FreeSpaceReserveBytes = 0
        };
        var targetDir = CreateEmptyTarget("entry-size-limit");
        using var service = new ZipDownloadService(
            new ArchiveBytesHandler(archive),
            limits,
            _ => new MemoryStream(new byte[1024], writable: false));

        var result = await service.DownloadAndExtractAsync(
            TestRepoUrl,
            targetDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("single entry size", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(targetDir));
    }

    [Fact]
    public async Task DownloadAndExtractAsync_RejectsActualAggregateExtractedBytes()
    {
        var archive = CreateArchive(
            ("repo-main/a.bin", new byte[1]),
            ("repo-main/b.bin", new byte[1]));
        var expandedEntries = new Queue<Stream>(
        [
            new MemoryStream(new byte[80], writable: false),
            new MemoryStream(new byte[80], writable: false)
        ]);
        var limits = ZipResourceLimits.Default with
        {
            MaxTotalExtractedBytes = 100,
            MaxSingleEntryBytes = 90,
            MaxCompressionRatio = 10_000,
            FreeSpaceReserveBytes = 0
        };
        var targetDir = CreateEmptyTarget("aggregate-size-limit");
        using var service = new ZipDownloadService(
            new ArchiveBytesHandler(archive),
            limits,
            _ => expandedEntries.Dequeue());

        var result = await service.DownloadAndExtractAsync(
            TestRepoUrl,
            targetDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("total extracted size", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(targetDir));
    }

    [Fact]
    public async Task DownloadAndExtractAsync_RejectsExcessiveEntryPathDepth()
    {
        var archive = CreateArchive(("repo-main/a/b/c/file.txt", "content"u8.ToArray()));
        var limits = ZipResourceLimits.Default with
        {
            MaxPathDepth = 4,
            FreeSpaceReserveBytes = 0
        };
        var targetDir = CreateEmptyTarget("path-depth-limit");
        using var service = new ZipDownloadService(new ArchiveBytesHandler(archive), limits);

        var result = await service.DownloadAndExtractAsync(
            TestRepoUrl,
            targetDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("path depth", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(targetDir));
    }

    [Fact]
    public async Task DownloadAndExtractAsync_RejectsActualDownloadWithoutContentLength()
    {
        var payload = new byte[129];
        var limits = ZipResourceLimits.Default with
        {
            MaxDownloadedArchiveBytes = 128,
            FreeSpaceReserveBytes = 0
        };
        var targetDir = CreateEmptyTarget("download-size-limit");
        using var service = new ZipDownloadService(
            new ArchiveBytesHandler(payload, includeContentLength: false),
            limits);

        var result = await service.DownloadAndExtractAsync(
            TestRepoUrl,
            targetDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("downloaded archive size", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(targetDir));
    }

    [Fact]
    public async Task DownloadAndExtractAsync_DoesNotTrustOversizedContentLength()
    {
        var archive = CreateArchive(("repo-main/file.txt", "content"u8.ToArray()));
        var limits = ZipResourceLimits.Default with
        {
            MaxDownloadedArchiveBytes = archive.Length + 1,
            FreeSpaceReserveBytes = 0
        };
        var targetDir = Path.Combine(_tempDir!, "advisory-content-length");
        using var service = new ZipDownloadService(
            new ArchiveBytesHandler(
                archive,
                declaredContentLength: limits.MaxDownloadedArchiveBytes + 1024),
            limits);

        var result = await service.DownloadAndExtractAsync(
            TestRepoUrl,
            targetDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(targetDir, "file.txt")));
    }

    [Fact]
    public async Task DownloadAndExtractAsync_ExtractsSmallArchiveWithinLimits()
    {
        var archive = CreateArchive(("repo-main/src/app.txt", "content"u8.ToArray()));
        var limits = new ZipResourceLimits
        {
            MaxDownloadedArchiveBytes = 4096,
            MaxTotalExtractedBytes = 1024,
            MaxSingleEntryBytes = 512,
            MaxEntryCount = 4,
            MaxPathDepth = 8,
            MaxCompressionRatio = 200,
            FreeSpaceReserveBytes = 0
        };
        var targetDir = Path.Combine(_tempDir!, "small-archive");
        using var service = new ZipDownloadService(new ArchiveBytesHandler(archive), limits);

        var result = await service.DownloadAndExtractAsync(
            TestRepoUrl,
            targetDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("content", await File.ReadAllTextAsync(
            Path.Combine(targetDir, "src", "app.txt"),
            TestContext.Current.CancellationToken));
    }

    private string CreateEmptyTarget(string name)
    {
        var target = Path.Combine(_tempDir!, name);
        Directory.CreateDirectory(target);
        return target;
    }

    private static byte[] CreateArchive(params (string Path, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.SmallestSize);
                using var output = entry.Open();
                output.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private sealed class ArchiveBytesHandler(
        byte[] archive,
        bool includeContentLength = true,
        long? declaredContentLength = null)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpContent content = includeContentLength
                ? new ByteArrayContent(archive)
                : new UnknownLengthContent(archive);
            if (declaredContentLength.HasValue)
                content.Headers.ContentLength = declaredContentLength.Value;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class DefaultBranchArchiveHandler(
	    string defaultBranch,
	    byte[] archive,
	    byte[]? metadata = null) : HttpMessageHandler
    {
        public string? ArchiveRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (uri.StartsWith("https://api.github.com/repos/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
	                Content = metadata is null
	                    ? new StringContent($"{{\"default_branch\":\"{defaultBranch}\"}}")
	                    : new UnknownLengthContent(metadata)
                });
            }

            ArchiveRequestUri = uri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive)
            });
        }
    }

    private sealed class UnknownLengthContent(byte[] content) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            stream.Write(content);
            return Task.CompletedTask;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class GitHubArchiveHandler : HttpMessageHandler
    {
        // Keep URL generation, streaming, extraction, and error mapping under test
        // without making their result depend on external DNS or GitHub availability.
        private static readonly byte[] RepositoryArchive = CreateRepositoryArchive();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestUri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (requestUri.Contains("user-that-definitely-does-not-exist", StringComparison.Ordinal))
                throw new HttpRequestException("Simulated DNS failure.");

            if (requestUri.Contains("nonexistent", StringComparison.Ordinal) ||
                requestUri.Contains("/invalid/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(RepositoryArchive)
                });
        }

        private static byte[] CreateRepositoryArchive()
        {
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteEntry(archive, "Hello-World-main/README.md", "# Hello World\n");
                WriteEntry(
                    archive,
                    "Hello-World-main/src/App.cs",
                    "internal static class App { }\n");
            }

            return buffer.ToArray();
        }

        private static void WriteEntry(ZipArchive archive, string path, string content)
        {
            var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }
    }

    #endregion
}
