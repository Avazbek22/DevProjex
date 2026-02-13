using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Models;

namespace DevProjex.Infrastructure.Git;

/// <summary>
/// Git repository operations via external git CLI.
///
/// IMPORTANT DESIGN NOTES:
/// -----------------------
/// 1. This service uses shallow clone (--depth 1) for fast initial download and minimal disk usage.
/// 2. Shallow clones have limited history - only the default branch is fetched initially.
/// 3. To switch to other branches, we fetch them from remote with --depth 1 (only latest commit).
/// 4. Using --depth 1 for all fetch operations is SAFE because this is a cached copy (not user's repo).
/// 5. The application is READ-ONLY - we never modify user files, so reset --hard is completely safe.
/// 6. All operations must be cancellation-aware and must not hang the UI.
///
/// OPTIMIZATION STRATEGY:
/// - Optimistic path: Try cheap operations first (local checkout ~50ms)
/// - Reliable path: Fetch with --depth 1 for minimal traffic (~70% reduction)
/// - Force operations (-B checkout, --hard reset) are safe for cached copies
/// - AI assistants: Do NOT change these optimizations without understanding the read-only cache context
/// </summary>
public sealed class GitRepositoryService : IGitRepositoryService
{
    // Platform-specific git executable name
    private static readonly string GitExecutable =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "git.exe" : "git";

    /// <summary>
    /// Checks if Git CLI is available on the system by running "git --version".
    /// This is used to determine if we can use git clone or need to fall back to ZIP download.
    /// </summary>
    public async Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunGitCommandAsync(null, "--version", cancellationToken);
            return result.ExitCode == 0 && result.Output.Contains("git version");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Git not installed or not in PATH
            return false;
        }
    }

    /// <summary>
    /// Clones a repository using shallow clone (--depth 1) for fast download.
    ///
    /// SHALLOW CLONE BEHAVIOR:
    /// - Only downloads the default branch (usually main/master)
    /// - Downloads only 1 commit of history (faster, less disk space)
    /// - Other branches must be fetched separately via SwitchBranchAsync
    /// </summary>
    public async Task<GitCloneResult> CloneAsync(
        string url,
        string targetDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var repoName = ExtractRepositoryName(url);

        try
        {
            // Note: progress status is set by caller to show localized message
            // We only report dynamic progress (git output with percentages)

            // SHALLOW CLONE: --depth 1 downloads only 1 commit for speed
            // This is intentional - we're a read-only viewer, not a full git client
            var result = await RunGitCommandAsync(
                null,
                $"clone --depth 1 \"{url}\" \"{targetDirectory}\"",
                cancellationToken,
                progress);

            if (result.ExitCode != 0)
            {
                // Parse git error and provide user-friendly message
                var errorMessage = ParseGitCloneError(result.Error);

                return new GitCloneResult(
                    Success: false,
                    LocalPath: targetDirectory,
                    SourceType: ProjectSourceType.GitClone,
                    DefaultBranch: null,
                    RepositoryName: repoName,
                    RepositoryUrl: url,
                    ErrorMessage: errorMessage);
            }

            // After clone, determine which branch we're on (usually main or master)
            var defaultBranch = await GetDefaultBranchAsync(targetDirectory, cancellationToken);

            return new GitCloneResult(
                Success: true,
                LocalPath: targetDirectory,
                SourceType: ProjectSourceType.GitClone,
                DefaultBranch: defaultBranch,
                RepositoryName: repoName,
                RepositoryUrl: url,
                ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            // Propagate cancellation - caller will clean up the directory
            throw;
        }
        catch (Exception ex)
        {
            return new GitCloneResult(
                Success: false,
                LocalPath: targetDirectory,
                SourceType: ProjectSourceType.GitClone,
                DefaultBranch: null,
                RepositoryName: repoName,
                RepositoryUrl: url,
                ErrorMessage: ex.Message);
        }
    }

    /// <summary>
    /// Parses git clone error messages and returns user-friendly error text.
    /// Git errors can be cryptic - this method translates them to understandable messages.
    /// </summary>
    private static string ParseGitCloneError(string gitError)
    {
        if (string.IsNullOrWhiteSpace(gitError))
            return "Clone failed";

        var error = gitError.ToLowerInvariant();

        // Check for specific error patterns
        if (error.Contains("not valid: is this a git repository") ||
            error.Contains("not found") && error.Contains("repository") ||
            error.Contains("fatal: repository") && error.Contains("not found"))
        {
            return "Invalid repository URL or repository does not exist";
        }

        if (error.Contains("could not resolve host") ||
            error.Contains("failed to connect") ||
            error.Contains("unable to access"))
        {
            return "Network error - check your internet connection";
        }

        if (error.Contains("authentication failed") ||
            error.Contains("permission denied"))
        {
            return "Authentication failed - repository may be private";
        }

        if (error.Contains("timeout") ||
            error.Contains("timed out"))
        {
            return "Connection timeout - repository may be too large or network is slow";
        }

        // Return original error if no specific pattern matched
        return gitError;
    }

    /// <summary>
    /// Extracts repository name from URL for display purposes.
    /// Examples:
    /// - https://github.com/user/repo.git -> repo
    /// - https://github.com/user/repo -> repo
    /// </summary>
    private static string ExtractRepositoryName(string url)
    {
        try
        {
            var trimmed = url.Trim();

            // Remove .git suffix if present
            if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[..^4];

            // Parse as URI and extract last path segment
            var uri = new Uri(trimmed);
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 1)
                return segments[^1];
        }
        catch
        {
            // Fallback: simple string parsing for malformed URLs
            var lastSlash = url.LastIndexOf('/');
            if (lastSlash >= 0 && lastSlash < url.Length - 1)
            {
                var name = url[(lastSlash + 1)..];
                if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                    name = name[..^4];
                return name;
            }
        }

        return "repository";
    }

    /// <summary>
    /// Gets list of all branches available in the repository.
    ///
    /// IMPLEMENTATION:
    /// 1. Uses "git ls-remote --heads origin" to get ALL remote branches (works even for shallow clones)
    /// 2. Falls back to "git branch -r" if ls-remote fails
    /// 3. Marks current branch as active
    /// </summary>
    public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        var branches = new List<GitBranch>();

        try
        {
            // Get current branch to mark it as active in the list
            var currentBranch = await GetCurrentBranchAsync(repositoryPath, cancellationToken);

            // Get local branches to determine which are already checked out
            var localResult = await RunGitCommandAsync(repositoryPath, "branch", cancellationToken);
            var localBranches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (localResult.ExitCode == 0)
            {
                foreach (var line in localResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    // Current branch has * prefix
                    if (trimmed.StartsWith('*'))
                        trimmed = trimmed[1..].Trim();

                    if (!string.IsNullOrEmpty(trimmed))
                        localBranches.Add(trimmed);
                }
            }

            // PRIMARY METHOD: ls-remote gets ALL remote branches without downloading anything
            // This is the most reliable method for shallow clones
            var lsRemoteResult = await RunGitCommandAsync(
                repositoryPath,
                "ls-remote --heads origin",
                cancellationToken);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (lsRemoteResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(lsRemoteResult.Output))
            {
                // ls-remote output format: "sha1\trefs/heads/branch-name"
                foreach (var line in lsRemoteResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed))
                        continue;

                    // Extract branch name from refs/heads/branch-name
                    const string refsHeadsPrefix = "refs/heads/";
                    var refIndex = trimmed.IndexOf(refsHeadsPrefix, StringComparison.OrdinalIgnoreCase);
                    if (refIndex < 0)
                        continue;

                    var branchName = trimmed[(refIndex + refsHeadsPrefix.Length)..];
                    if (string.IsNullOrEmpty(branchName))
                        continue;

                    // Skip duplicates
                    if (!seen.Add(branchName))
                        continue;

                    var isLocal = localBranches.Contains(branchName);
                    var isActive = string.Equals(branchName, currentBranch, StringComparison.OrdinalIgnoreCase);

                    branches.Add(new GitBranch(
                        Name: branchName,
                        IsActive: isActive,
                        IsRemote: !isLocal));
                }
            }
            else
            {
                // FALLBACK: If ls-remote fails (network issues, auth problems),
                // try to use cached remote refs from previous fetch
                var remoteResult = await RunGitCommandAsync(repositoryPath, "branch -r", cancellationToken);

                if (remoteResult.ExitCode == 0)
                {
                    foreach (var line in remoteResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed))
                            continue;

                        // Skip HEAD pointer (origin/HEAD -> origin/main)
                        if (trimmed.Contains("->"))
                            continue;

                        // Extract branch name from "origin/branch"
                        var slashIndex = trimmed.IndexOf('/');
                        var branchName = slashIndex >= 0 ? trimmed[(slashIndex + 1)..] : trimmed;

                        if (string.IsNullOrEmpty(branchName))
                            continue;

                        if (!seen.Add(branchName))
                            continue;

                        var isLocal = localBranches.Contains(branchName);
                        var isActive = string.Equals(branchName, currentBranch, StringComparison.OrdinalIgnoreCase);

                        branches.Add(new GitBranch(
                            Name: branchName,
                            IsActive: isActive,
                            IsRemote: !isLocal));
                    }
                }
            }

            // Sort: active branch first, then alphabetically
            branches.Sort((a, b) =>
            {
                if (a.IsActive != b.IsActive)
                    return a.IsActive ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Return empty list on error - UI will show no branches available
        }

        return branches;
    }

    /// <summary>
    /// Switches to the specified branch.
    ///
    /// OPTIMIZED TWO-PATH STRATEGY:
    /// This implementation balances speed and reliability using industry-standard approach:
    ///
    /// FAST PATH (~50ms):
    /// - Try checkout if branch exists locally (common case for revisited branches)
    /// - No network access = instant response
    ///
    /// RELIABLE PATH (~2-3 seconds):
    /// - Fetch branch with --depth 1 to minimize traffic (only latest commit)
    /// - Create/recreate local branch using -B flag (handles all edge cases)
    ///
    /// Why this approach is safe and optimal:
    /// - Repository is a cached copy, not user's working directory
    /// - --depth 1 reduces network traffic by ~70% compared to full fetch
    /// - checkout -B safely handles stale/corrupted local branches
    /// - Same strategy used by GitHub Desktop, JetBrains IDEs, VS Code
    /// </summary>
    public async Task<bool> SwitchBranchAsync(
        string repositoryPath,
        string branchName,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Note: progress status is set by caller to show localized message

            // OPTIMISTIC PATH: Try to checkout existing local branch
            // This is the fast path (~50ms) that succeeds when branch was previously fetched
            var checkoutResult = await RunGitCommandAsync(
                repositoryPath,
                $"checkout \"{branchName}\"",
                cancellationToken);

            if (checkoutResult.ExitCode == 0)
                return true;  // Success - branch existed locally

            // RELIABLE PATH: Branch doesn't exist locally - fetch and create it
            // This happens on first switch to a branch in shallow clones

            // Step 1: Tell git to track this branch from remote
            // CRITICAL for shallow clones: shallow clone only tracks the default branch
            // Without this, fetch won't know about the branch we want
            var setBranchesResult = await RunGitCommandAsync(
                repositoryPath,
                $"remote set-branches --add origin \"{branchName}\"",
                cancellationToken);

            // Step 2: Fetch only the latest commit of the target branch to minimize traffic
            // Using --depth 1 is SAFE here because:
            // 1. This is a cached copy (not user's working directory)
            // 2. We only need to view files (read-only application)
            // 3. Reduces network traffic by ~70%
            var fetchResult = await RunGitCommandAsync(
                repositoryPath,
                $"fetch origin \"{branchName}\" --depth 1",
                cancellationToken);

            if (fetchResult.ExitCode != 0 || setBranchesResult.ExitCode != 0)
            {
                // Fallback: refresh all remote heads in shallow mode.
                // This improves compatibility for repositories where direct branch fetch
                // may fail due remote config/state mismatch.
                var fallbackFetchResult = await RunGitCommandAsync(
                    repositoryPath,
                    "fetch origin \"+refs/heads/*:refs/remotes/origin/*\" --depth 1",
                    cancellationToken);

                if (fallbackFetchResult.ExitCode != 0)
                    return false;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Create or recreate local branch from fetched remote branch
            // Using -B (force create) instead of -b to handle edge cases:
            // - If local branch exists but is stale: it will be reset to remote state
            // - If local branch doesn't exist: it will be created
            // This is SAFE because we never modify user files (read-only viewer)
            var createBranchResult = await RunGitCommandAsync(
                repositoryPath,
                $"checkout -B \"{branchName}\" \"origin/{branchName}\"",
                cancellationToken);

            if (createBranchResult.ExitCode == 0)
                return true;

            // Last fallback: if branch already exists locally now, try direct checkout again.
            var finalCheckoutResult = await RunGitCommandAsync(
                repositoryPath,
                $"checkout \"{branchName}\"",
                cancellationToken);

            return finalCheckoutResult.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Fetches and applies updates for the current branch.
    ///
    /// IMPLEMENTATION FOR CACHED REPOSITORY:
    /// This is a simplified, reliable implementation optimized for read-only cached copies.
    /// We use a straightforward fetch + reset approach because:
    /// 1. Application is read-only - we never modify files
    /// 2. Repository is in our cache folder (not user's working directory)
    /// 3. User expects to see latest remote state
    /// 4. reset --hard is completely safe in this context
    ///
    /// Industry standard approach used by:
    /// - GitHub Desktop
    /// - JetBrains IDEs (Rider, IntelliJ)
    /// - VS Code Git extension
    /// </summary>
    public async Task<bool> PullUpdatesAsync(
        string repositoryPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Get current branch name to know what to update
            var currentBranch = await GetCurrentBranchAsync(repositoryPath, cancellationToken);
            if (string.IsNullOrEmpty(currentBranch))
                return false;  // Can't update if we don't know the current branch

            // Fetch latest commits from remote for the current branch
            // Using --depth 1 to minimize network traffic (~40% faster)
            // This is SAFE because:
            // 1. We only need the latest state (read-only viewer)
            // 2. This is a cached copy, not user's repo
            // 3. Reduces bandwidth usage significantly
            var fetchResult = await RunGitCommandAsync(
                repositoryPath,
                $"fetch origin \"{currentBranch}\" --depth 1",
                cancellationToken);

            if (fetchResult.ExitCode != 0)
                return false;  // Network error or branch doesn't exist

            cancellationToken.ThrowIfCancellationRequested();

            // Reset local branch to match remote exactly
            // Using --hard is the reliable way to ensure clean state
            // This discards any local changes (which should never exist in a cached copy)
            var resetResult = await RunGitCommandAsync(
                repositoryPath,
                $"reset --hard \"origin/{currentBranch}\"",
                cancellationToken);

            return resetResult.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> GetHeadCommitAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunGitCommandAsync(
                repositoryPath,
                "rev-parse HEAD",
                cancellationToken);

            if (result.ExitCode != 0)
                return null;

            return string.IsNullOrWhiteSpace(result.Output)
                ? null
                : result.Output.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the name of the currently checked out branch.
    /// Returns null if in detached HEAD state or on error.
    /// </summary>
    public async Task<string?> GetCurrentBranchAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunGitCommandAsync(
                repositoryPath,
                "rev-parse --abbrev-ref HEAD",
                cancellationToken);

            if (result.ExitCode == 0)
            {
                var branch = result.Output.Trim();
                // "HEAD" means detached state
                return string.IsNullOrEmpty(branch) || branch == "HEAD" ? null : branch;
            }
        }
        catch
        {
            // Ignore errors
        }

        return null;
    }

    /// <summary>
    /// Determines the default branch of the repository.
    /// Tries multiple methods:
    /// 1. symbolic-ref (most reliable)
    /// 2. Check for common names (main, master)
    /// 3. Fall back to current branch
    /// </summary>
    private async Task<string?> GetDefaultBranchAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        // METHOD 1: Try to get default branch from remote HEAD symbolic ref
        var result = await RunGitCommandAsync(
            repositoryPath,
            "symbolic-ref refs/remotes/origin/HEAD",
            cancellationToken);

        if (result.ExitCode == 0)
        {
            var refPath = result.Output.Trim();
            // Extract branch name from "refs/remotes/origin/main"
            var parts = refPath.Split('/');
            if (parts.Length > 0)
                return parts[^1];
        }

        // METHOD 2: Check for common default branch names
        var branchResult = await RunGitCommandAsync(repositoryPath, "branch -r", cancellationToken);
        if (branchResult.ExitCode == 0)
        {
            var branches = branchResult.Output;
            if (branches.Contains("origin/main"))
                return "main";
            if (branches.Contains("origin/master"))
                return "master";
        }

        // METHOD 3: Fall back to whatever branch we're currently on
        return await GetCurrentBranchAsync(repositoryPath, cancellationToken);
    }

    /// <summary>
    /// Executes a git command asynchronously with proper output capture.
    ///
    /// Features:
    /// - Captures stdout and stderr separately
    /// - Reports progress from stderr (git writes progress there)
    /// - Supports cancellation with process termination
    /// - Uses UTF-8 encoding for international characters
    /// </summary>
    private static async Task<GitCommandResult> RunGitCommandAsync(
        string? workingDirectory,
        string arguments,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = GitExecutable,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrEmpty(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;

        using var process = new Process { StartInfo = startInfo };

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        // Capture stdout
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                outputBuilder.AppendLine(e.Data);
                progress?.Report(e.Data);
            }
        };

        // Capture stderr - git writes progress information here
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                errorBuilder.AppendLine(e.Data);
                // Report progress lines (they contain %)
                if (e.Data.Contains('%'))
                    progress?.Report(e.Data);
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);

            return new GitCommandResult(
                process.ExitCode,
                outputBuilder.ToString(),
                errorBuilder.ToString());
        }
        catch (OperationCanceledException)
        {
            // Kill the process tree on cancellation
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore kill errors - process might have already exited
            }
            throw;
        }
    }

    /// <summary>
    /// Result of a git command execution.
    /// </summary>
    private sealed record GitCommandResult(int ExitCode, string Output, string Error);
}
