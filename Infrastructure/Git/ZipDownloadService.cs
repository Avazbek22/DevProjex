using System.Buffers;
using System.IO.Compression;
using System.Net;
using System.Security;
using System.Text.RegularExpressions;

namespace DevProjex.Infrastructure.Git;

/// <summary>
/// Downloads and extracts GitHub repositories as ZIP archives.
/// Fallback for when Git CLI is not available.
/// </summary>
public sealed partial class ZipDownloadService : IZipDownloadService, IDisposable
{
    private const int StreamBufferSize = 81920;
    private const int ExtractionProgressReportInterval = 50;

    private readonly HttpClient _httpClient;
    private readonly ZipResourceLimits _limits;
    private readonly Func<ZipArchiveEntry, Stream> _openEntryStream;
    private bool _disposed;

    public ZipDownloadService()
        : this(new HttpClient(), ZipResourceLimits.Default, static entry => entry.Open())
    {
    }

    internal ZipDownloadService(HttpMessageHandler handler)
        : this(CreateHttpClient(handler), ZipResourceLimits.Default, static entry => entry.Open())
    {
    }

    internal ZipDownloadService(HttpMessageHandler handler, ZipResourceLimits limits)
        : this(CreateHttpClient(handler), limits, static entry => entry.Open())
    {
    }

    internal ZipDownloadService(
        HttpMessageHandler handler,
        ZipResourceLimits limits,
        Func<ZipArchiveEntry, Stream> openEntryStream)
        : this(CreateHttpClient(handler), limits, openEntryStream)
    {
    }

    private ZipDownloadService(
        HttpClient httpClient,
        ZipResourceLimits limits,
        Func<ZipArchiveEntry, Stream> openEntryStream)
    {
        _httpClient = httpClient;
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _openEntryStream = openEntryStream ?? throw new ArgumentNullException(nameof(openEntryStream));
        _limits.Validate();
        _httpClient.Timeout = TimeSpan.FromMinutes(10);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DevProjex/1.0");
    }

    public async Task<GitCloneResult> DownloadAndExtractAsync(
        string repositoryUrl,
        string targetDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var repoName = ExtractRepositoryName(repositoryUrl);

        if (!TryGetZipUrl(repositoryUrl, out var zipUrl, out var branch))
        {
            return new GitCloneResult(
                Success: false,
                LocalPath: targetDirectory,
                SourceType: ProjectSourceType.ZipDownload,
                DefaultBranch: null,
                RepositoryName: repoName,
                RepositoryUrl: repositoryUrl,
                ErrorMessage: "Could not determine ZIP download URL");
        }

        var tempZipPath = Path.Combine(Path.GetTempPath(), $"devprojex_{Guid.NewGuid():N}.zip");
        var cleanupTargetDirectory = false;
        var completed = false;

        try
        {
            cleanupTargetDirectory = IsMissingOrEmptyDirectory(targetDirectory);

            // Download ZIP - try main branch first, then master if 404
            HttpResponseMessage? response = null;
            try
            {
                response = await _httpClient.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                // If 404 and we tried "main", try "master"
                if (response.StatusCode == HttpStatusCode.NotFound && branch == "main")
                {
                    response.Dispose();

                    // Try with master branch
                    if (TryGetZipUrlWithBranch(repositoryUrl, "master", out var masterZipUrl))
                    {
                        branch = "master";
                        response = await _httpClient.GetAsync(masterZipUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    }
                }

                response.EnsureSuccessStatusCode();
            }
            catch
            {
                response?.Dispose();
                throw;
            }

            using (response)
            {
                var totalBytes = response.Content.Headers.ContentLength;
                await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var fileStream = OpenAsyncFileForWrite(tempZipPath);

                var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
                long totalRead = 0;
                var lastDownloadPercent = -1;

                try
                {
                    ReportPercent(progress, 0, ref lastDownloadPercent);

                    int bytesRead;
                    while ((bytesRead = await contentStream
                               .ReadAsync(buffer.AsMemory(0, StreamBufferSize), cancellationToken)
                               .ConfigureAwait(false)) > 0)
                    {
                        if (bytesRead > _limits.MaxDownloadedArchiveBytes - totalRead)
                            throw ZipResourceLimits.CreateLimitException(
                                "downloaded archive size",
                                _limits.MaxDownloadedArchiveBytes);

                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                        totalRead += bytesRead;

                        if (totalBytes.HasValue && totalBytes.Value > 0)
                        {
                            var percent = (int)(totalRead * 100 / totalBytes.Value);
                            // Report only percentage - caller shows localized "Downloading..." message.
                            ReportPercent(progress, percent, ref lastDownloadPercent);
                        }
                    }

                    // Some servers do not send Content-Length, so the loop cannot calculate a
                    // percentage. Always close a successful download with 100% for stable UI/tests.
                    ReportPercent(progress, 100, ref lastDownloadPercent);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Notify caller that we're switching to extraction phase
            progress?.Report("::EXTRACTING::");

            await ExtractArchiveAsync(
                    tempZipPath,
                    targetDirectory,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            completed = true;
            return new GitCloneResult(
                Success: true,
                LocalPath: targetDirectory,
                SourceType: ProjectSourceType.ZipDownload,
                DefaultBranch: branch,
                RepositoryName: repoName,
                RepositoryUrl: repositoryUrl,
                ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return new GitCloneResult(
                Success: false,
                LocalPath: targetDirectory,
                SourceType: ProjectSourceType.ZipDownload,
                DefaultBranch: null,
                RepositoryName: repoName,
                RepositoryUrl: repositoryUrl,
                ErrorMessage: ex.Message);
        }
        catch (Exception ex)
        {
            return new GitCloneResult(
                Success: false,
                LocalPath: targetDirectory,
                SourceType: ProjectSourceType.ZipDownload,
                DefaultBranch: null,
                RepositoryName: repoName,
                RepositoryUrl: repositoryUrl,
                ErrorMessage: ex.Message);
        }
        finally
        {
            TryDeleteFile(tempZipPath);
            if (!completed && cleanupTargetDirectory)
                TryDeleteDirectory(targetDirectory);
        }
    }

    private async Task ExtractArchiveAsync(
        string archivePath,
        string targetDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        await using var archiveStream = OpenAsyncFileForRead(archivePath);
        var archiveSize = archiveStream.Length;
        using var archive = await ZipArchive.CreateAsync(
                archiveStream,
                ZipArchiveMode.Read,
                leaveOpen: false,
                entryNameEncoding: null,
                cancellationToken)
            .ConfigureAwait(false);

        var declaredTotal = ValidateArchiveMetadata(archive, archiveSize);
        EnsureAvailableFreeSpace(targetDirectory, declaredTotal);
        Directory.CreateDirectory(targetDirectory);

        var extractionBudget = new ZipExtractionBudget(archiveSize, _limits);
        var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        try
        {
            string? rootFolder = null;
            var totalEntries = archive.Entries.Count;
            var processedEntries = 0;
            var lastExtractionPercent = -1;

            ReportPercent(progress, 0, ref lastExtractionPercent);

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entryPath = entry.FullName;
                if (string.IsNullOrEmpty(rootFolder))
                    rootFolder = TryGetTopLevelFolder(entryPath);

                if (!string.IsNullOrEmpty(rootFolder) && StartsWithFolderPrefix(entryPath, rootFolder))
                    entryPath = entryPath[(rootFolder.Length + 1)..];

                if (!string.IsNullOrEmpty(entryPath))
                {
                    var destinationPath = ResolveSafeDestinationPath(targetDirectory, entryPath);
                    if (IsDirectoryEntry(entry))
                    {
                        Directory.CreateDirectory(destinationPath);
                    }
                    else
                    {
                        var directory = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(directory))
                            Directory.CreateDirectory(directory);

                        await ExtractEntryAsync(
                                entry,
                                destinationPath,
                                extractionBudget,
                                buffer,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                processedEntries++;
                if (totalEntries > 0 && processedEntries % ExtractionProgressReportInterval == 0)
                {
                    var percent = (int)((long)processedEntries * 100 / totalEntries);
                    ReportPercent(progress, percent, ref lastExtractionPercent);
                }
            }

            ReportPercent(progress, 100, ref lastExtractionPercent);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task ExtractEntryAsync(
        ZipArchiveEntry entry,
        string destinationPath,
        ZipExtractionBudget extractionBudget,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        await using (var entryStream = _openEntryStream(entry))
        await using (var output = new LimitedExtractionWriteStream(
                         OpenAsyncFileForWrite(destinationPath),
                         extractionBudget,
                         entry.FullName))
        {
            int bytesRead;
            while ((bytesRead = await entryStream
                       .ReadAsync(buffer.AsMemory(0, StreamBufferSize), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            }
        }

        File.SetLastWriteTime(destinationPath, entry.LastWriteTime.DateTime);
    }

    private long ValidateArchiveMetadata(ZipArchive archive, long archiveSize)
    {
        if (archive.Entries.Count > _limits.MaxEntryCount)
            throw ZipResourceLimits.CreateLimitException("entry count", _limits.MaxEntryCount);

        long declaredTotal = 0;
        foreach (var entry in archive.Entries)
        {
            if (CountPathSegments(entry.FullName) > _limits.MaxPathDepth)
                throw ZipResourceLimits.CreateLimitException(
                    "entry path depth",
                    _limits.MaxPathDepth,
                    entry.FullName);
            if (entry.Length > _limits.MaxSingleEntryBytes)
                throw ZipResourceLimits.CreateLimitException(
                    "single entry size",
                    _limits.MaxSingleEntryBytes,
                    entry.FullName);

            if (entry.Length > _limits.MaxTotalExtractedBytes - declaredTotal)
                throw ZipResourceLimits.CreateLimitException(
                    "total extracted size",
                    _limits.MaxTotalExtractedBytes);
            declaredTotal += entry.Length;
        }

        if (ZipResourceLimits.ExceedsCompressionRatio(
                declaredTotal,
                archiveSize,
                _limits.MaxCompressionRatio))
        {
            throw ZipResourceLimits.CreateLimitException(
                "compression ratio",
                _limits.MaxCompressionRatio);
        }

        return declaredTotal;
    }

    public bool TryGetZipUrl(string repositoryUrl, out string zipUrl, out string? branch)
    {
        // Try main branch first (most common default)
        branch = "main";
        return TryGetZipUrlWithBranch(repositoryUrl, branch, out zipUrl);
    }

    /// <summary>
    /// Tries to get ZIP download URL for a specific branch.
    /// </summary>
    private bool TryGetZipUrlWithBranch(string repositoryUrl, string branchName, out string zipUrl)
    {
        zipUrl = string.Empty;

        if (string.IsNullOrWhiteSpace(repositoryUrl))
            return false;

        // Normalize URL
        var url = repositoryUrl.Trim();
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];

        // Try to match GitHub URL patterns
        var match = GitHubUrlPattern().Match(url);
        if (!match.Success)
            return false;

        var owner = match.Groups["owner"].Value;
        var repo = match.Groups["repo"].Value;

        zipUrl = $"https://github.com/{owner}/{repo}/archive/refs/heads/{branchName}.zip";
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _httpClient.Dispose();
        _disposed = true;
    }

    private static string ExtractRepositoryName(string url)
    {
        var match = GitHubUrlPattern().Match(url);
        if (match.Success)
            return match.Groups["repo"].Value;

        // Fallback
        try
        {
            var trimmed = url.Trim();
            if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[..^4];

            var lastSlash = trimmed.LastIndexOf('/');
            if (lastSlash >= 0 && lastSlash < trimmed.Length - 1)
                return trimmed[(lastSlash + 1)..];
        }
        catch
        {
            // Ignore
        }

        return "repository";
    }

    [GeneratedRegex(@"^https?://(?:www\.)?github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/?", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubUrlPattern();

    private static string? TryGetTopLevelFolder(string entryPath)
    {
        if (string.IsNullOrEmpty(entryPath))
            return null;

        var slashIndex = entryPath.IndexOf('/');
        return slashIndex > 0 ? entryPath[..slashIndex] : null;
    }

    private static bool StartsWithFolderPrefix(string value, string folderName)
    {
        if (value.Length < folderName.Length + 1)
            return false;

        return value.StartsWith(folderName, StringComparison.Ordinal) &&
               value[folderName.Length] == '/';
    }

    internal static string ResolveSafeDestinationPath(string targetDirectory, string entryPath)
    {
        var normalizedEntryPath = entryPath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var targetRoot = Path.GetFullPath(targetDirectory);
        var destinationPath = Path.GetFullPath(Path.Combine(targetRoot, normalizedEntryPath));

        // ZIP entry names are external input. Keep extraction read-only outside the
        // selected target even if an archive contains "../" or absolute-like entries.
        if (!IsPathWithinDirectory(destinationPath, targetRoot))
            throw new InvalidDataException($"ZIP entry points outside the target directory: {entryPath}");

        return destinationPath;
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry)
        => entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
           entry.FullName.EndsWith("\\", StringComparison.Ordinal) ||
           string.IsNullOrEmpty(entry.Name);

    private static bool IsPathWithinDirectory(string path, string directory)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var separator = Path.DirectorySeparatorChar.ToString();
        var normalizedDirectory = directory.EndsWith(separator, StringComparison.Ordinal)
            ? directory
            : directory + separator;

        return path.Equals(directory, comparison) ||
               path.StartsWith(normalizedDirectory, comparison);
    }

    private static FileStream OpenAsyncFileForRead(string path)
        => new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            StreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static FileStream OpenAsyncFileForWrite(string path)
        => new(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            StreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return new HttpClient(handler, disposeHandler: true);
    }

    private static void ReportPercent(IProgress<string>? progress, int percent, ref int lastReportedPercent)
    {
        if (progress is null)
            return;

        var normalizedPercent = Math.Clamp(percent, 0, 100);
        if (normalizedPercent == lastReportedPercent)
            return;

        lastReportedPercent = normalizedPercent;
        progress.Report($"{normalizedPercent}%");
    }

    private void EnsureAvailableFreeSpace(string targetDirectory, long declaredExtractedBytes)
    {
        var requiredBytes = checked(declaredExtractedBytes + _limits.FreeSpaceReserveBytes);
        long availableBytes;
        try
        {
            var fullPath = Path.GetFullPath(targetDirectory);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
                return;

            availableBytes = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            IOException or
            UnauthorizedAccessException or
            SecurityException or
            NotSupportedException)
        {
            return;
        }

        if (availableBytes < requiredBytes)
        {
            throw new IOException(
                $"Insufficient free space for ZIP extraction. Required {requiredBytes:N0} bytes, " +
                $"available {availableBytes:N0} bytes.");
        }
    }

    private static int CountPathSegments(string path)
    {
        var segments = 0;
        var insideSegment = false;
        foreach (var character in path)
        {
            if (character is '/' or '\\')
            {
                insideSegment = false;
                continue;
            }

            if (!insideSegment)
            {
                segments++;
                insideSegment = true;
            }
        }

        return segments;
    }

    private static bool IsMissingOrEmptyDirectory(string path) =>
        !Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any();

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Cleanup is best effort; the primary operation result must remain observable.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Cleanup is best effort; the primary operation result must remain observable.
        }
    }
}

internal sealed record ZipResourceLimits
{
    private const long GiB = 1024L * 1024 * 1024;

    public static ZipResourceLimits Default { get; } = new();

    // GitHub source archives can be large, but a 2 GiB compressed fallback is already exceptional.
    public long MaxDownloadedArchiveBytes { get; init; } = 2 * GiB;

    // Four times the compressed cap leaves room for legitimate repositories without allowing unbounded expansion.
    public long MaxTotalExtractedBytes { get; init; } = 8 * GiB;

    // A single repository file above 2 GiB is not suitable for the application's analysis pipeline.
    public long MaxSingleEntryBytes { get; init; } = 2 * GiB;

    // This is far above normal source trees while bounding central-directory and filesystem pressure.
    public int MaxEntryCount { get; init; } = 250_000;

    // Deep generated paths remain supported while pathological filesystem nesting is rejected.
    public int MaxPathDepth { get; init; } = 128;

    // A 200:1 aggregate ratio rejects archive bombs while allowing highly compressible source and metadata.
    public int MaxCompressionRatio { get; init; } = 200;

    // Keep workspace and OS operations viable while extraction is in progress.
    public long FreeSpaceReserveBytes { get; init; } = GiB;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDownloadedArchiveBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTotalExtractedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSingleEntryBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxEntryCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPathDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxCompressionRatio);
        ArgumentOutOfRangeException.ThrowIfNegative(FreeSpaceReserveBytes);
        if (MaxSingleEntryBytes > MaxTotalExtractedBytes)
        {
            throw new ArgumentException(
                "The single-entry ZIP limit cannot exceed the total extraction limit.");
        }
    }

    public static bool ExceedsCompressionRatio(
        long extractedBytes,
        long archiveBytes,
        int maximumRatio)
    {
        if (extractedBytes == 0)
            return false;
        if (archiveBytes <= 0)
            return true;
        if (archiveBytes > long.MaxValue / maximumRatio)
            return false;
        return extractedBytes > archiveBytes * maximumRatio;
    }

    public static InvalidDataException CreateLimitException(
        string resource,
        long limit,
        string? entryName = null)
    {
        var entrySuffix = string.IsNullOrEmpty(entryName) ? string.Empty : $" Entry: {entryName}.";
        return new InvalidDataException(
            $"ZIP {resource} exceeds the configured limit ({limit:N0}).{entrySuffix}");
    }
}

internal sealed class ZipExtractionBudget(long archiveBytes, ZipResourceLimits limits)
{
    private long _totalWritten;

    public void ReserveWrite(string entryName, int byteCount, ref long entryWritten)
    {
        if (byteCount > limits.MaxSingleEntryBytes - entryWritten)
            throw ZipResourceLimits.CreateLimitException(
                "single entry size",
                limits.MaxSingleEntryBytes,
                entryName);
        if (byteCount > limits.MaxTotalExtractedBytes - _totalWritten)
            throw ZipResourceLimits.CreateLimitException(
                "total extracted size",
                limits.MaxTotalExtractedBytes);

        var nextEntryWritten = entryWritten + byteCount;
        var nextTotalWritten = _totalWritten + byteCount;
        if (ZipResourceLimits.ExceedsCompressionRatio(
                nextTotalWritten,
                archiveBytes,
                limits.MaxCompressionRatio))
        {
            throw ZipResourceLimits.CreateLimitException(
                "compression ratio",
                limits.MaxCompressionRatio);
        }

        entryWritten = nextEntryWritten;
        _totalWritten = nextTotalWritten;
    }

}

internal sealed class LimitedExtractionWriteStream(
    Stream inner,
    ZipExtractionBudget budget,
    string entryName) : Stream
{
    private long _entryWritten;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        budget.ReserveWrite(entryName, count, ref _entryWritten);
        inner.Write(buffer, offset, count);
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        budget.ReserveWrite(entryName, buffer.Length, ref _entryWritten);
        return inner.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        budget.ReserveWrite(entryName, count, ref _entryWritten);
        return inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
