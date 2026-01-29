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
/// </summary>
public sealed class GitRepositoryService : IGitRepositoryService
{
    private static readonly string GitExecutable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "git.exe" : "git";

    public async Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunGitCommandAsync(null, "--version", cancellationToken);
            return result.ExitCode == 0 && result.Output.Contains("git version");
        }
        catch
        {
            return false;
        }
    }

    public async Task<GitCloneResult> CloneAsync(
        string url,
        string targetDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var repoName = ExtractRepositoryName(url);

        try
        {
            // Clone with depth 1 for faster download
            var result = await RunGitCommandAsync(
                null,
                $"clone --depth 1 \"{url}\" \"{targetDirectory}\"",
                cancellationToken,
                progress);

            if (result.ExitCode != 0)
            {
                return new GitCloneResult(
                    Success: false,
                    LocalPath: targetDirectory,
                    SourceType: ProjectSourceType.GitClone,
                    DefaultBranch: null,
                    RepositoryName: repoName,
                    ErrorMessage: result.Error);
            }

            // Determine default branch
            var defaultBranch = await GetDefaultBranchAsync(targetDirectory, cancellationToken);

            return new GitCloneResult(
                Success: true,
                LocalPath: targetDirectory,
                SourceType: ProjectSourceType.GitClone,
                DefaultBranch: defaultBranch,
                RepositoryName: repoName,
                ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
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
                ErrorMessage: ex.Message);
        }
    }

    private static string ExtractRepositoryName(string url)
    {
        try
        {
            var trimmed = url.Trim();
            if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[..^4];

            // Extract last segment as repo name
            var uri = new Uri(trimmed);
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 1)
                return segments[^1];
        }
        catch
        {
            // Fallback: try simple string parsing
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

    public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        var branches = new List<GitBranch>();

        try
        {
            // Get current branch first
            var currentBranch = await GetCurrentBranchAsync(repositoryPath, cancellationToken);

            // Get local branches
            var localResult = await RunGitCommandAsync(repositoryPath, "branch", cancellationToken);
            var localBranches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (localResult.ExitCode == 0)
            {
                foreach (var line in localResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith('*'))
                        trimmed = trimmed[1..].Trim();

                    if (!string.IsNullOrEmpty(trimmed))
                        localBranches.Add(trimmed);
                }
            }

            // Use ls-remote to get ALL branches from remote (most reliable method)
            var lsRemoteResult = await RunGitCommandAsync(repositoryPath, "ls-remote --heads origin", cancellationToken);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (lsRemoteResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(lsRemoteResult.Output))
            {
                // ls-remote output format: "sha1\trefs/heads/branch-name"
                foreach (var line in lsRemoteResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed))
                        continue;

                    // Extract branch name from "refs/heads/branch-name"
                    var refsHeadsPrefix = "refs/heads/";
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
                // Fallback: try fetch and branch -r if ls-remote fails
                await RunGitCommandAsync(repositoryPath, "fetch --all --prune", cancellationToken);
                var remoteResult = await RunGitCommandAsync(repositoryPath, "branch -r", cancellationToken);

                if (remoteResult.ExitCode == 0)
                {
                    foreach (var line in remoteResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed))
                            continue;

                        // Skip HEAD pointer
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

            // Sort: active first, then alphabetically
            branches.Sort((a, b) =>
            {
                if (a.IsActive != b.IsActive)
                    return a.IsActive ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch
        {
            // Return empty list on error
        }

        return branches;
    }

    public async Task<bool> SwitchBranchAsync(
        string repositoryPath,
        string branchName,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            progress?.Report("Switching branch...");

            // First try to checkout existing local branch
            var result = await RunGitCommandAsync(repositoryPath, $"checkout \"{branchName}\"", cancellationToken);

            if (result.ExitCode != 0)
            {
                // Try to checkout tracking remote branch
                result = await RunGitCommandAsync(
                    repositoryPath,
                    $"checkout -b \"{branchName}\" \"origin/{branchName}\"",
                    cancellationToken);
            }

            return result.ExitCode == 0;
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

    public async Task<bool> PullUpdatesAsync(
        string repositoryPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            progress?.Report("Fetching...");
            var fetchResult = await RunGitCommandAsync(repositoryPath, "fetch", cancellationToken);

            if (fetchResult.ExitCode != 0)
                return false;

            progress?.Report("Pulling...");
            var pullResult = await RunGitCommandAsync(repositoryPath, "pull --ff-only", cancellationToken);

            return pullResult.ExitCode == 0;
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

    public async Task<string?> GetCurrentBranchAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunGitCommandAsync(repositoryPath, "rev-parse --abbrev-ref HEAD", cancellationToken);

            if (result.ExitCode == 0)
            {
                var branch = result.Output.Trim();
                return string.IsNullOrEmpty(branch) || branch == "HEAD" ? null : branch;
            }
        }
        catch
        {
            // Ignore
        }

        return null;
    }

    private async Task<string?> GetDefaultBranchAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        // Try to get default branch from symbolic ref
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

        // Fallback: check if main or master exists
        var branchResult = await RunGitCommandAsync(repositoryPath, "branch -r", cancellationToken);
        if (branchResult.ExitCode == 0)
        {
            var branches = branchResult.Output;
            if (branches.Contains("origin/main"))
                return "main";
            if (branches.Contains("origin/master"))
                return "master";
        }

        return await GetCurrentBranchAsync(repositoryPath, cancellationToken);
    }

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

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                outputBuilder.AppendLine(e.Data);
                progress?.Report(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                errorBuilder.AppendLine(e.Data);
                // Git often writes progress to stderr
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
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore kill errors
            }
            throw;
        }
    }

    private sealed record GitCommandResult(int ExitCode, string Output, string Error);
}
