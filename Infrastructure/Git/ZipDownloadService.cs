using System.Buffers;
using System.IO.Compression;
using System.Net;
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
    private bool _disposed;

    public ZipDownloadService()
        : this(new HttpClient())
    {
    }

    internal ZipDownloadService(HttpMessageHandler handler)
        : this(CreateHttpClient(handler))
    {
    }

    private ZipDownloadService(HttpClient httpClient)
    {
        _httpClient = httpClient;
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

        try
        {
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

            Directory.CreateDirectory(targetDirectory);

            await using (var archiveStream = OpenAsyncFileForRead(tempZipPath))
            using (var archive = await ZipArchive.CreateAsync(
                       archiveStream,
                       ZipArchiveMode.Read,
                       leaveOpen: false,
                       entryNameEncoding: null,
                       cancellationToken).ConfigureAwait(false))
            {
                // GitHub ZIPs usually have a root folder like "repo-main/".
                // Detect it once and strip it from extracted paths.
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

                    // Remove root folder from path
                    if (!string.IsNullOrEmpty(rootFolder) && StartsWithFolderPrefix(entryPath, rootFolder))
                        entryPath = entryPath[(rootFolder.Length + 1)..];

                    if (string.IsNullOrEmpty(entryPath))
                        continue;

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

                        await entry.ExtractToFileAsync(destinationPath, overwrite: true, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    processedEntries++;
                    if (totalEntries > 0 && processedEntries % ExtractionProgressReportInterval == 0)
                    {
                        var percent = (int)(processedEntries * 100 / totalEntries);
                        // Report only percentage - caller shows localized "Extracting..." message.
                        ReportPercent(progress, percent, ref lastExtractionPercent);
                    }
                }

                ReportPercent(progress, 100, ref lastExtractionPercent);
            }

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
            // Cleanup temp file
            try
            {
                if (File.Exists(tempZipPath))
                    File.Delete(tempZipPath);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
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
}
