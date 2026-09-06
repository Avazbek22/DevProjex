using System.Runtime.ExceptionServices;
using DevProjex.Infrastructure.Processes;
using DevProjex.Kernel;

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
    private const int CommandOutputBufferChars = 64 * 1024;
    private const int CommandErrorBufferChars = 64 * 1024;
    internal const int MaximumProgressFrameCharacters = 4 * 1024;
    private readonly string? _gitExecutable;

    public GitRepositoryService()
    {
    }

    internal GitRepositoryService(string gitExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gitExecutable);
        _gitExecutable = gitExecutable;
    }

    /// <summary>
    /// Checks if Git CLI is available on the system by running "git --version".
    /// This is used to determine if we can use git clone or need to fall back to ZIP download.
    /// </summary>
    public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default)
    {
		cancellationToken.ThrowIfCancellationRequested();
		return Task.FromResult(GitRuntime.VersionDisplay.StartsWith("git version ", StringComparison.OrdinalIgnoreCase));
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
        var sourceAccepted = GitCloneAuthentication.TryResolveCloneUrl(
            url,
            out var cloneUrl,
            out var authentication);
        var resultRepositoryUrl = RepositoryUrlUtility.ToSafeDisplay(cloneUrl);
        var repoName = RepositoryUrlUtility.GetRepositoryName(resultRepositoryUrl);

        try
        {
            if (!sourceAccepted)
            {
                return new GitCloneResult(
                    Success: false,
                    LocalPath: targetDirectory,
                    SourceType: ProjectSourceType.GitClone,
                    DefaultBranch: null,
                    RepositoryName: repoName,
                    RepositoryUrl: resultRepositoryUrl,
                    ErrorMessage: "Clone failed");
            }

            // Note: progress status is set by caller to show localized message
            // We only report dynamic progress (git output with percentages)

            // Git suppresses transfer progress when stderr is redirected. --progress is required
            // here so a long clone cannot look frozen while the external process is still active.
            // SHALLOW CLONE: --depth 1 downloads only 1 commit for speed.
            using var askPass = authentication is null
                ? null
                : GitAskPassSession.Create(authentication);
            var result = await RunGitCommandAsync(
                null,
				GitProcessOperation.CloneRepository(cloneUrl, targetDirectory),
                cancellationToken,
                progress,
                askPass);

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
                    RepositoryUrl: resultRepositoryUrl,
                    ErrorMessage: errorMessage);
            }

			GitRemoteIdentityStore.Write(targetDirectory, cloneUrl);

            // After clone, determine which branch we're on (usually main or master)
            var defaultBranch = await GetDefaultBranchAsync(targetDirectory, cancellationToken);

            return new GitCloneResult(
                Success: true,
                LocalPath: targetDirectory,
                SourceType: ProjectSourceType.GitClone,
                DefaultBranch: defaultBranch,
                RepositoryName: repoName,
                RepositoryUrl: resultRepositoryUrl,
                ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            // Propagate cancellation - caller will clean up the directory
            throw;
        }
        catch
        {
            return new GitCloneResult(
                Success: false,
                LocalPath: targetDirectory,
                SourceType: ProjectSourceType.GitClone,
                DefaultBranch: null,
                RepositoryName: repoName,
                RepositoryUrl: resultRepositoryUrl,
                ErrorMessage: "Clone failed");
        }
    }

    public async Task<string?> GetRemoteUrlAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunGitCommandAsync(
                repositoryPath,
				GitProcessOperation.ReadRemoteUrl(),
                cancellationToken);
            if (result.ExitCode != 0)
                return null;

            return result.Output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses git clone error messages and returns user-friendly error text.
    /// Git errors can be cryptic - this method translates them to understandable messages.
    /// </summary>
    internal static string ParseGitCloneError(string gitError)
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

        // Do not surface arbitrary Git stderr because it may echo authenticated URLs.
        return "Clone failed";
    }

    /// <summary>
    /// Extracts repository name from URL for display purposes.
    /// Examples:
    /// - https://github.com/user/repo.git -> repo
    /// - https://github.com/user/repo -> repo
    /// </summary>
    internal static string ExtractRepositoryName(string url) =>
        RepositoryUrlUtility.GetRepositoryName(url);

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
			var localResult = await RunGitCommandAsync(
				repositoryPath,
				GitProcessOperation.ListBranches(GitBranchListKind.Local),
				cancellationToken);
            var localBranches = new HashSet<string>(StringComparer.Ordinal);

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
			var remoteUrl = await GetVerifiedNetworkRemoteAsync(repositoryPath, cancellationToken)
				.ConfigureAwait(false);
			var lsRemoteResult = remoteUrl is null
				? GitCommandResult.Failed("The saved remote identity is unavailable or changed.")
				: await RunGitCommandAsync(
					repositoryPath,
					GitProcessOperation.ListBranches(GitBranchListKind.RemoteHeads, remoteUrl),
					cancellationToken).ConfigureAwait(false);

            var seen = new HashSet<string>(StringComparer.Ordinal);

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
                    var isActive = string.Equals(branchName, currentBranch, StringComparison.Ordinal);

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
				var remoteResult = await RunGitCommandAsync(
					repositoryPath,
					GitProcessOperation.ListBranches(GitBranchListKind.CachedRemote),
					cancellationToken);

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
                        var isActive = string.Equals(branchName, currentBranch, StringComparison.Ordinal);

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
                var displayOrder = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return displayOrder != 0
                    ? displayOrder
                    : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
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
            branchName = GitBranchNameValidator.ValidateAndNormalize(branchName);
            // Git resolves the otherwise valid branch name "@" as HEAD for a bare checkout target.
            if (branchName == "@")
                return false;
            if (RepositoryCacheLayout.IsManaged(repositoryPath))
            {
                return await SwitchManagedWorktreeBranchAsync(
                    repositoryPath,
                    branchName,
                    cancellationToken);
            }

			// A user-selected working tree is read-only to DevProjex. Branch changes are
			// permitted only in an application-owned cache under its lease protocol.
			return false;
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
            currentBranch = GitBranchNameValidator.ValidateAndNormalize(currentBranch);

            // Fetch latest commits from remote for the current branch
            // Using --depth 1 to minimize network traffic (~40% faster)
            // This is SAFE because:
            // 1. We only need the latest state (read-only viewer)
            // 2. This is a cached copy, not user's repo
            // 3. Reduces bandwidth usage significantly
			if (!RepositoryCacheLayout.IsManaged(repositoryPath))
				return false;
			var remoteUrl = await GetVerifiedNetworkRemoteAsync(repositoryPath, cancellationToken)
				.ConfigureAwait(false);
			if (remoteUrl is null)
				return false;
			await using var baseLock = await RepositoryFileLease.AcquireExclusiveAsync(
				RepositoryCacheLayout.GetBaseOperationLockPath(
					RepositoryCacheLayout.GetContainer(repositoryPath),
					repositoryPath),
				cancellationToken);
			var fetchResult = await RunGitCommandAsync(
				repositoryPath,
				GitProcessOperation.FetchBranch(remoteUrl, currentBranch),
				cancellationToken);

            if (fetchResult.ExitCode != 0)
                return false;  // Network error or branch doesn't exist

            cancellationToken.ThrowIfCancellationRequested();

            // Reset local branch to match remote exactly
            // Using --hard is the reliable way to ensure clean state
            // This discards any local changes (which should never exist in a cached copy)
            var resetResult = await RunGitCommandAsync(
                repositoryPath,
				GitProcessOperation.ManagedCheckout(
					GitManagedCheckoutKind.HardReset,
					$"refs/remotes/origin/{currentBranch}"),
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
				GitProcessOperation.ResolveCommit("HEAD"),
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
				GitProcessOperation.ListBranches(GitBranchListKind.Current),
                cancellationToken);

            if (result.ExitCode == 0)
            {
                var branch = result.Output.Trim();
                // "HEAD" means detached state
                if (!string.IsNullOrEmpty(branch) && branch != "HEAD")
                    return branch;

                if (RepositoryCacheLayout.IsManaged(repositoryPath))
                {
                    var configured = await RunGitCommandAsync(
                        repositoryPath,
						GitProcessOperation.ReadConfigValue(GitConfigReadKind.WorktreeBranch),
                        cancellationToken);
                    if (configured.ExitCode == 0 && !string.IsNullOrWhiteSpace(configured.Output))
                        return configured.Output.Trim();
                }

                return null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Ignore errors
        }

        return null;
    }

    private async Task<bool> SwitchManagedWorktreeBranchAsync(
        string repositoryPath,
        string branchName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            return false;
        branchName = GitBranchNameValidator.ValidateAndNormalize(branchName);

        var container = RepositoryCacheLayout.GetContainer(repositoryPath);
        var basePath = Path.Combine(container, RepositoryCacheLayout.BaseDirectoryName);
        if (!Directory.Exists(basePath))
            basePath = repositoryPath;

        await using var baseLock = await RepositoryFileLease.AcquireExclusiveAsync(
            RepositoryCacheLayout.GetBaseOperationLockPath(container, basePath),
            cancellationToken);

        var revision = $"refs/remotes/origin/{branchName}";
        var verify = await RunGitCommandAsync(
            repositoryPath,
			GitProcessOperation.ResolveCommit(revision),
            cancellationToken);
        if (verify.ExitCode != 0)
        {
			var remoteUrl = await GetVerifiedNetworkRemoteAsync(repositoryPath, cancellationToken)
				.ConfigureAwait(false);
			if (remoteUrl is null)
				return false;
            var setBranches = await RunGitCommandAsync(
                repositoryPath,
				GitProcessOperation.ManagedConfigWrite(
					GitManagedConfigWriteKind.AddTrackedBranch,
					branchName),
                cancellationToken);
            var fetch = await RunGitCommandAsync(
                repositoryPath,
				GitProcessOperation.FetchBranch(remoteUrl, branchName),
                cancellationToken);
            if (setBranches.ExitCode != 0 || fetch.ExitCode != 0)
                return false;
        }

        var checkout = await RunGitCommandAsync(
            repositoryPath,
			GitProcessOperation.ManagedCheckout(GitManagedCheckoutKind.Detach, revision),
            cancellationToken);
        if (checkout.ExitCode != 0)
            return false;

        var config = await RunGitCommandAsync(
            repositoryPath,
			GitProcessOperation.ManagedConfigWrite(
				GitManagedConfigWriteKind.SetWorktreeBranch,
				branchName),
            cancellationToken);
        return config.ExitCode == 0;
    }

    /// <summary>
    /// Determines the default branch of the repository.
    /// Tries multiple methods:
    /// 1. symbolic-ref (most reliable)
    /// 2. Check for common names (main, master)
    /// 3. Fall back to current branch
    /// </summary>
    public async Task<string?> GetDefaultBranchAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        // METHOD 1: Try to get default branch from remote HEAD symbolic ref
        var result = await RunGitCommandAsync(
            repositoryPath,
			GitProcessOperation.ListBranches(GitBranchListKind.RemoteHead),
            cancellationToken);

        if (result.ExitCode == 0)
        {
            var remoteHeadBranch = ResolveRemoteHeadBranch(result.Output);
            if (remoteHeadBranch is not null)
                return remoteHeadBranch;
        }

        // METHOD 2: Check for common default branch names
		var branchResult = await RunGitCommandAsync(
			repositoryPath,
			GitProcessOperation.ListBranches(GitBranchListKind.CachedRemote),
			cancellationToken);
        if (branchResult.ExitCode == 0)
        {
            var commonDefault = ResolveCommonDefaultBranch(branchResult.Output);
            if (commonDefault is not null)
                return commonDefault;
        }

        // METHOD 3: Fall back to whatever branch we're currently on
        return await GetCurrentBranchAsync(repositoryPath, cancellationToken);
    }

    internal static string? ResolveRemoteHeadBranch(string symbolicReference)
    {
        const string RemoteHeadPrefix = "refs/remotes/origin/";
        var reference = symbolicReference.Trim();
        if (!reference.StartsWith(RemoteHeadPrefix, StringComparison.Ordinal) ||
            reference.Length == RemoteHeadPrefix.Length)
        {
            return null;
        }

        return reference[RemoteHeadPrefix.Length..];
    }

    internal static string? ResolveCommonDefaultBranch(string remoteBranches)
    {
        var hasMaster = false;
        foreach (var rawLine in remoteBranches.AsSpan().EnumerateLines())
        {
            var line = rawLine.Trim();
            if (line.StartsWith("* ", StringComparison.Ordinal))
                line = line[2..].TrimStart();
            if (line.Equals("origin/main", StringComparison.Ordinal) ||
                line.EndsWith("-> origin/main", StringComparison.Ordinal))
            {
                return "main";
            }

            hasMaster |= line.Equals("origin/master", StringComparison.Ordinal) ||
                         line.EndsWith("-> origin/master", StringComparison.Ordinal);
        }

        return hasMaster ? "master" : null;
    }

	private async Task<string?> GetVerifiedNetworkRemoteAsync(
		string repositoryPath,
		CancellationToken cancellationToken)
	{
		if (!RepositoryCacheLayout.IsManaged(repositoryPath))
			return null;
		var remoteUrl = await GetRemoteUrlAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(remoteUrl) || !GitRemoteIdentityStore.Matches(repositoryPath, remoteUrl))
			return null;
		var overrides = await RunGitCommandAsync(
			repositoryPath,
			GitProcessOperation.ReadConfigValue(GitConfigReadKind.NetworkOverrides),
			cancellationToken).ConfigureAwait(false);
		if (overrides.ExitCode == 0 && !string.IsNullOrWhiteSpace(overrides.Output))
			return null;
		try
		{
			return GitNetworkPolicy.ValidateUrl(remoteUrl);
		}
		catch (ArgumentException)
		{
			return null;
		}
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
    private async Task<GitCommandResult> RunGitCommandAsync(
        string? workingDirectory,
		GitProcessOperation operation,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null,
        GitAskPassSession? askPass = null)
    {
        // Honor pre-canceled tokens before spawning git. Without this guard a very fast
        // command such as "git --version" can complete before WaitForExitAsync observes
        // cancellation, which makes cancellation behavior platform-timing dependent.
        cancellationToken.ThrowIfCancellationRequested();

		var startInfo = _gitExecutable is null
			? GitProcessStartInfoFactory.Create(workingDirectory, operation, askPass: askPass)
			: GitProcessStartInfoFactory.CreateForTesting(workingDirectory, operation, _gitExecutable);
		if (_gitExecutable is not null)
			askPass?.Apply(startInfo);
		using var deadlineSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadlineSource.CancelAfter(operation.Deadline);
		var operationToken = deadlineSource.Token;

        using var process = new Process { StartInfo = startInfo };

        var outputBuffer = new BoundedLineBuffer(CommandOutputBufferChars);
        var errorBuffer = new BoundedLineBuffer(CommandErrorBufferChars);
        var progressObserver = progress is null ? null : new GitProgressObserver(progress);
        var lastReportedPercent = -1;

        process.Start();
        process.StandardInput.Close();

        var outputPump = GitProcessLinePump.ReadAsync(
            process.StandardOutput,
            CommandOutputBufferChars,
            frame => outputBuffer.Add(frame.Text, frame.ExceededLimit),
			operationToken);
        var errorPump = GitProcessLinePump.ReadAsync(
            process.StandardError,
            CommandErrorBufferChars,
            HandleErrorFrame,
			operationToken);

        try
        {
			await WaitForExitOrTerminateAsync(process, operationToken);
            _ = await GitProcessOutputReader
                .WaitForCompletionAfterExitAsync(process, outputPump, errorPump)
                .ConfigureAwait(false);
            progressObserver?.ThrowIfFaulted();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await GitProcessOutputReader
                .ObserveAfterTerminationAsync(process, outputPump, errorPump)
                .ConfigureAwait(false);
            throw;
        }

		var outputExceeded = outputBuffer.ExceededLimit || errorBuffer.ExceededLimit;
		return new GitCommandResult(
			outputExceeded ? -1 : process.ExitCode,
			outputBuffer.ToString(),
			outputExceeded ? "Git process output exceeded the safety limit." : errorBuffer.ToString());

        void HandleErrorFrame(GitProcessLineFrame frame)
        {
            var line = frame.Text;
            var classification = ClassifyGitStderrLine(line);
            if (classification.Percent is { } percent)
            {
                if (progressObserver is not null)
                {
                    var previousPercent = Interlocked.Exchange(ref lastReportedPercent, percent);
                    if (classification.IsSafeProgressLine)
                    {
                        // Preserve the phase detail for richer consumers without also
                        // emitting a second standalone percentage for the same Git line.
                        progressObserver.Report(SanitizeProgressFrame(line));
                    }
                    else if (previousPercent != percent)
                    {
                        progressObserver.Report($"{percent}%");
                    }
                }

                if (!classification.RetainForError)
                {
                    // Progress lines can be very noisy during clone/fetch and are not
                    // needed in the final error payload.
                    return;
                }
            }

            errorBuffer.Add(line, frame.ExceededLimit);

            if (progressObserver is not null &&
                classification.Percent is null &&
                classification.IsSafeProgressLine)
            {
                progressObserver.Report(SanitizeProgressFrame(line));
            }
        }
    }

    private static string SanitizeProgressFrame(string value)
    {
        var result = new StringBuilder(Math.Min(value.Length, MaximumProgressFrameCharacters));
        SingleLineTextEscaping.AppendBounded(
            result,
            value.AsSpan(),
            MaximumProgressFrameCharacters);
        return result.ToString();
    }

	internal static ProcessStartInfo CreateGitCommandStartInfo(
		string? workingDirectory,
		GitProcessOperation operation) =>
		GitProcessStartInfoFactory.Create(workingDirectory, operation);

    internal static async Task WaitForExitOrTerminateAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        await ExternalProcessLifetime
            .WaitForExitOrTerminateAsync(process, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static bool TryExtractProgressPercent(string line, out int percent)
    {
        percent = -1;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var percentIndex = line.IndexOf('%');
        while (percentIndex >= 0)
        {
            var end = percentIndex - 1;
            while (end >= 0 && char.IsWhiteSpace(line[end]))
                end--;

            if (end >= 0)
            {
                var start = end;
                while (start >= 0 && char.IsDigit(line[start]))
                    start--;

                var length = end - start;
                var hasInvalidNumericPrefix = start >= 0 &&
                                              (char.IsLetterOrDigit(line[start]) ||
                                               line[start] is '+' or '-' or '.' or '_');
                if (!hasInvalidNumericPrefix &&
                    length > 0 &&
                    int.TryParse(line.AsSpan(start + 1, length), out var value) &&
                    value is >= 0 and <= 100)
                {
                    percent = value;
                    return true;
                }
            }

            percentIndex = line.IndexOf('%', percentIndex + 1);
        }

        return false;
    }

    internal static bool IsSafeGitProgressLine(string line)
    {
        var trimmed = line.AsSpan().TrimStart();
        return trimmed.StartsWith("remote: Enumerating objects:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("remote: Counting objects:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("remote: Compressing objects:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Receiving objects:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Resolving deltas:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Checking out files:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Updating files:", StringComparison.OrdinalIgnoreCase);
    }

    internal static GitStderrLineClassification ClassifyGitStderrLine(string line)
    {
        var hasPercent = TryExtractProgressPercent(line, out var percent);
        var isSafeProgressLine = IsSafeGitProgressLine(line);
        return new GitStderrLineClassification(
            hasPercent ? percent : null,
            isSafeProgressLine,
            RetainForError: !hasPercent || !isSafeProgressLine);
    }

    internal readonly record struct GitStderrLineClassification(
        int? Percent,
        bool IsSafeProgressLine,
        bool RetainForError);

    private sealed class BoundedLineBuffer(int maxChars)
    {
        private readonly int _maxChars = Math.Max(1024, maxChars);
        private readonly Queue<string> _lines = new();
        private readonly object _sync = new();
        private int _charCount;
		private bool _exceededLimit;

		public bool ExceededLimit
		{
			get
			{
				lock (_sync)
					return _exceededLimit;
			}
		}

        public void Add(string line, bool exceededLineLimit)
        {
            lock (_sync)
            {
                if (exceededLineLimit)
                {
					_exceededLimit = true;
                    _lines.Clear();
                    _charCount = 0;
                    return;
                }

                _lines.Enqueue(line);
                _charCount += line.Length + Environment.NewLine.Length;

                while (_charCount > _maxChars && _lines.Count > 0)
                {
					_exceededLimit = true;
                    var removed = _lines.Dequeue();
                    _charCount -= removed.Length + Environment.NewLine.Length;
                }
            }
        }

        public override string ToString()
        {
            lock (_sync)
            {
                if (_lines.Count == 0)
                    return string.Empty;

                var sb = new StringBuilder(_charCount + Environment.NewLine.Length);
                var isFirst = true;
                foreach (var line in _lines)
                {
                    if (!isFirst)
                        sb.AppendLine();
                    sb.Append(line);
                    isFirst = false;
                }

                return sb.ToString();
            }
        }
    }

    private sealed class GitProgressObserver(IProgress<string> destination)
    {
        private IProgress<string>? _destination = destination;
        private ExceptionDispatchInfo? _failure;

        public void Report(string value)
        {
            var current = Volatile.Read(ref _destination);
            if (current is null)
                return;

            try
            {
                current.Report(value);
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(
                    ref _failure,
                    ExceptionDispatchInfo.Capture(exception),
                    null);
                Interlocked.Exchange(ref _destination, null);
            }
        }

        public void ThrowIfFaulted() => Volatile.Read(ref _failure)?.Throw();
    }

    /// <summary>
    /// Result of a git command execution.
    /// </summary>
	private sealed record GitCommandResult(int ExitCode, string Output, string Error)
	{
		public static GitCommandResult Failed(string error) => new(-1, string.Empty, error);
	}
}
