using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Models;

namespace DevProjex.Infrastructure.Git;

/// <summary>
/// Downloads and extracts GitHub repositories as ZIP archives.
/// Fallback for when Git CLI is not available.
/// </summary>
public sealed partial class ZipDownloadService : IZipDownloadService, IDisposable
{
    private readonly HttpClient _httpClient;
    private bool _disposed;

    public ZipDownloadService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
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
            // Download ZIP
            using (var response = await _httpClient.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength;
                await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = File.Create(tempZipPath);

                var buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    totalRead += bytesRead;

                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        var percent = (int)(totalRead * 100 / totalBytes.Value);
                        progress?.Report($"Downloading... {percent}%");
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Extract ZIP
            Directory.CreateDirectory(targetDirectory);

            using (var archive = ZipFile.OpenRead(tempZipPath))
            {
                // GitHub ZIPs have a root folder like "repo-main/"
                // We need to extract contents without that root folder
                var entries = archive.Entries.ToList();
                var rootFolder = entries
                    .Select(e => e.FullName.Split('/')[0])
                    .FirstOrDefault(n => !string.IsNullOrEmpty(n));

                var totalEntries = entries.Count;
                var processedEntries = 0;

                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var entryPath = entry.FullName;

                    // Remove root folder from path
                    if (rootFolder is not null && entryPath.StartsWith(rootFolder + "/"))
                        entryPath = entryPath[(rootFolder.Length + 1)..];

                    if (string.IsNullOrEmpty(entryPath))
                        continue;

                    var destinationPath = Path.Combine(targetDirectory, entryPath.Replace('/', Path.DirectorySeparatorChar));

                    // Create directory or extract file
                    if (entry.FullName.EndsWith('/'))
                    {
                        Directory.CreateDirectory(destinationPath);
                    }
                    else
                    {
                        var directory = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(directory))
                            Directory.CreateDirectory(directory);

                        entry.ExtractToFile(destinationPath, overwrite: true);
                    }

                    processedEntries++;
                    if (processedEntries % 50 == 0)
                    {
                        var percent = (int)(processedEntries * 100 / totalEntries);
                        progress?.Report($"Extracting... {percent}%");
                    }
                }
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
        zipUrl = string.Empty;
        branch = null;

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

        // Default to main branch, will redirect to actual default if needed
        branch = "main";
        zipUrl = $"https://github.com/{owner}/{repo}/archive/refs/heads/{branch}.zip";

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
}
