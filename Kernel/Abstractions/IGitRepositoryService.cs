using DevProjex.Kernel.Models;

namespace DevProjex.Kernel.Abstractions;

/// <summary>
/// Provides Git repository operations.
/// </summary>
public interface IGitRepositoryService
{
    /// <summary>
    /// Checks if Git CLI is available on the system.
    /// </summary>
    Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clones a repository using Git CLI.
    /// </summary>
    Task<GitCloneResult> CloneAsync(
        string url,
        string targetDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets list of branches for the repository.
    /// </summary>
    Task<IReadOnlyList<GitBranch>> GetBranchesAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches to the specified branch.
    /// </summary>
    Task<bool> SwitchBranchAsync(
        string repositoryPath,
        string branchName,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches and pulls updates for the current branch.
    /// </summary>
    Task<bool> PullUpdatesAsync(
        string repositoryPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current HEAD commit hash.
    /// </summary>
    Task<string?> GetHeadCommitAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current branch name.
    /// </summary>
    Task<string?> GetCurrentBranchAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default);
}
