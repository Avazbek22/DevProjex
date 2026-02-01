using DevProjex.Kernel.Models;

namespace DevProjex.Kernel.Abstractions;

/// <summary>
/// Downloads and extracts repository as ZIP archive (fallback for when Git is unavailable).
/// </summary>
public interface IZipDownloadService
{
    /// <summary>
    /// Downloads and extracts a GitHub repository as ZIP.
    /// </summary>
    Task<GitCloneResult> DownloadAndExtractAsync(
        string repositoryUrl,
        string targetDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tries to convert a repository URL to a ZIP download URL.
    /// </summary>
    bool TryGetZipUrl(string repositoryUrl, out string zipUrl, out string? branch);
}
