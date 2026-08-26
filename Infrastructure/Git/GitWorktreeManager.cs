using System.ComponentModel;

namespace DevProjex.Infrastructure.Git;

internal interface IGitWorktreeManager
{
	Task<WorktreeSupportState> GetSupportStateAsync(string basePath, CancellationToken cancellationToken);
	Task<bool> PreparePrimaryAsync(
		string basePath,
		string? branch,
		CancellationToken cancellationToken);
	Task<bool> CreateDetachedAsync(
		string basePath,
		string worktreePath,
		string? branch,
		CancellationToken cancellationToken);
	Task RemoveAsync(
		string basePath,
		string worktreePath,
		CancellationToken cancellationToken);
	Task PruneAsync(string basePath, CancellationToken cancellationToken);
}

internal sealed class GitWorktreeManager : IGitWorktreeManager
{
	private static readonly TimeSpan SupportProbeTimeout = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan RecoveryCleanupTimeout = TimeSpan.FromSeconds(10);
	internal const int MaximumProcessOutputCharacters = GitProcessOutputReader.MaximumOutputCharacters;
	private readonly object _supportSync = new();
	private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<GitProcessResult>> _runAsync;
	private readonly Func<string, Task<WorktreeSupportState>> _probeSupport;
	private readonly TimeSpan _recoveryCleanupTimeout;
	private Task<WorktreeSupportState>? _supportProbe;
	private WorktreeSupportState _cachedSupportState;

	public GitWorktreeManager()
	{
		_runAsync = RunAsync;
		_probeSupport = basePath => ProbeSupportAsync(basePath, _runAsync, SupportProbeTimeout);
		_recoveryCleanupTimeout = RecoveryCleanupTimeout;
	}

	internal GitWorktreeManager(Func<string, Task<WorktreeSupportState>> probeSupport)
	{
		_runAsync = RunAsync;
		_probeSupport = probeSupport ?? throw new ArgumentNullException(nameof(probeSupport));
		_recoveryCleanupTimeout = RecoveryCleanupTimeout;
	}

	internal GitWorktreeManager(
		Func<string, IReadOnlyList<string>, CancellationToken, Task<GitProcessResult>> runAsync,
		TimeSpan supportProbeTimeout,
		TimeSpan? recoveryCleanupTimeout = null)
	{
		ArgumentNullException.ThrowIfNull(runAsync);
		if (supportProbeTimeout <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(supportProbeTimeout));
		if (recoveryCleanupTimeout is { } cleanupTimeout && cleanupTimeout <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(recoveryCleanupTimeout));
		_runAsync = runAsync;
		_probeSupport = basePath => ProbeSupportAsync(basePath, _runAsync, supportProbeTimeout);
		_recoveryCleanupTimeout = recoveryCleanupTimeout ?? RecoveryCleanupTimeout;
	}

	public async Task<WorktreeSupportState> GetSupportStateAsync(
		string basePath,
		CancellationToken cancellationToken)
	{
		Task<WorktreeSupportState> probe;
		lock (_supportSync)
		{
			PublishCompletedProbeLocked();
			if (_cachedSupportState != WorktreeSupportState.Unknown)
				return _cachedSupportState;
			_supportProbe ??= _probeSupport(basePath);
			probe = _supportProbe;
		}

		WorktreeSupportState state;
		try
		{
			state = await probe.WaitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch when (probe.IsFaulted)
		{
			lock (_supportSync)
			{
				if (ReferenceEquals(_supportProbe, probe))
					_supportProbe = null;
			}
			throw;
		}
		lock (_supportSync)
		{
			if (ReferenceEquals(_supportProbe, probe))
			{
				_supportProbe = null;
				if (state is WorktreeSupportState.Supported or WorktreeSupportState.PermanentUnsupported)
					_cachedSupportState = state;
			}
		}

		return state;
	}

	private void PublishCompletedProbeLocked()
	{
		if (_supportProbe is not { IsCompleted: true } completed)
			return;

		_supportProbe = null;
		if (completed.IsCompletedSuccessfully)
		{
			var state = completed.GetAwaiter().GetResult();
			if (state is WorktreeSupportState.Supported or WorktreeSupportState.PermanentUnsupported)
				_cachedSupportState = state;
			return;
		}

		_ = completed.Exception;
	}

	public async Task<bool> PreparePrimaryAsync(
		string basePath,
		string? branch,
		CancellationToken cancellationToken)
	{
		var revision = await ResolveRevisionAsync(basePath, branch, cancellationToken)
			.ConfigureAwait(false);
		if (!await RunSuccessfulAsync(basePath, ["checkout", "--detach", revision], cancellationToken)
			.ConfigureAwait(false))
		{
			return false;
		}

		return await VerifyAndRecordAsync(basePath, revision, branch, cancellationToken).ConfigureAwait(false);
	}

	public async Task<bool> CreateDetachedAsync(
		string basePath,
		string worktreePath,
		string? branch,
		CancellationToken cancellationToken)
	{
		var revision = await ResolveRevisionAsync(basePath, branch, cancellationToken)
			.ConfigureAwait(false);
		if (!await RunSuccessfulAsync(
				basePath,
				["worktree", "add", "--detach", worktreePath, revision],
				cancellationToken)
			.ConfigureAwait(false))
		{
			TryDeletePartialWorktree(worktreePath);
			return false;
		}

		try
		{
			if (await VerifyAndRecordAsync(worktreePath, revision, branch, cancellationToken)
				    .ConfigureAwait(false))
			{
				return true;
			}
		}
		catch
		{
			await TryCleanupDetachedAsync(basePath, worktreePath).ConfigureAwait(false);
			throw;
		}

		await TryCleanupDetachedAsync(basePath, worktreePath).ConfigureAwait(false);
		return false;
	}

	private async Task TryCleanupDetachedAsync(string basePath, string worktreePath)
	{
		using var timeout = new CancellationTokenSource(_recoveryCleanupTimeout);
		try
		{
			await RemoveAsync(basePath, worktreePath, timeout.Token).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
		{
		}
		finally
		{
			TryDeletePartialWorktree(worktreePath);
		}
	}

	public async Task RemoveAsync(
		string basePath,
		string worktreePath,
		CancellationToken cancellationToken)
	{
		await RunSuccessfulAsync(
				basePath,
				["worktree", "remove", "--force", worktreePath],
				cancellationToken)
			.ConfigureAwait(false);
	}

	public async Task PruneAsync(string basePath, CancellationToken cancellationToken)
	{
		await RunSuccessfulAsync(basePath, ["worktree", "prune"], cancellationToken).ConfigureAwait(false);
	}

	private static async Task<WorktreeSupportState> ProbeSupportAsync(
		string basePath,
		Func<string, IReadOnlyList<string>, CancellationToken, Task<GitProcessResult>> runAsync,
		TimeSpan timeout)
	{
		using var timeoutSource = new CancellationTokenSource(timeout);
		try
		{
			var result = await runAsync(
					basePath,
					["worktree", "list", "--porcelain"],
					timeoutSource.Token)
				.ConfigureAwait(false);
			if (result.ExitCode == 0)
				return WorktreeSupportState.Supported;
			return result.Error.Contains("not a git command", StringComparison.OrdinalIgnoreCase) ||
			       result.Error.Contains("unknown subcommand", StringComparison.OrdinalIgnoreCase)
				? WorktreeSupportState.PermanentUnsupported
				: WorktreeSupportState.TransientFailure;
		}
		catch (Win32Exception exception)
		{
			return IsPermanentGitStartFailure(exception)
				? WorktreeSupportState.PermanentUnsupported
				: WorktreeSupportState.TransientFailure;
		}
		catch (PlatformNotSupportedException)
		{
			return WorktreeSupportState.PermanentUnsupported;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return WorktreeSupportState.TransientFailure;
		}
		catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
		{
			return WorktreeSupportState.TransientFailure;
		}
	}

	private static bool IsPermanentGitStartFailure(Win32Exception exception)
	{
		if (exception.NativeErrorCode == 2)
			return true;

		return OperatingSystem.IsWindows() && exception.NativeErrorCode is 3 or 193 or 216;
	}

	private async Task<string> ResolveRevisionAsync(
		string basePath,
		string? branch,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(branch))
			return await ResolveCommitAsync(basePath, "HEAD", branch, cancellationToken).ConfigureAwait(false);

		var normalizedBranch = GitBranchNameValidator.ValidateAndNormalize(branch);

		foreach (var candidate in new[]
		{
			$"refs/remotes/origin/{normalizedBranch}",
			$"refs/heads/{normalizedBranch}"
		})
		{
			var commit = await TryResolveCommitAsync(basePath, candidate, cancellationToken).ConfigureAwait(false);
			if (commit is not null)
				return commit;
		}

		throw new RepositoryBranchUnavailableException(
			normalizedBranch,
			RepositoryBranchUnavailableReason.NotFound);
	}

	private async Task<string> ResolveCommitAsync(
		string basePath,
		string revision,
		string? branch,
		CancellationToken cancellationToken)
	{
		var commit = await TryResolveCommitAsync(basePath, revision, cancellationToken).ConfigureAwait(false);
		if (commit is not null)
			return commit;
		throw new RepositoryBranchUnavailableException(
			branch ?? "HEAD",
			RepositoryBranchUnavailableReason.NotFound);
	}

	private async Task<string?> TryResolveCommitAsync(
		string basePath,
		string revision,
		CancellationToken cancellationToken)
	{
		var result = await _runAsync(
			basePath,
			["rev-parse", "--verify", "--quiet", $"{revision}^{{commit}}"],
			cancellationToken).ConfigureAwait(false);
		if (result.ExitCode != 0)
			return null;
		var commit = result.Output.Trim();
		return commit.Length == 0 ? null : commit;
	}

	private async Task<bool> VerifyAndRecordAsync(
		string repositoryPath,
		string expectedCommit,
		string? branch,
		CancellationToken cancellationToken)
	{
		var head = await _runAsync(repositoryPath, ["rev-parse", "HEAD"], cancellationToken)
			.ConfigureAwait(false);
		if (head.ExitCode != 0 ||
		    !string.Equals(head.Output.Trim(), expectedCommit, StringComparison.OrdinalIgnoreCase))
		{
			throw new RepositoryBranchUnavailableException(
				branch ?? "HEAD",
				RepositoryBranchUnavailableReason.RevisionMismatch);
		}

		return await RecordSessionBranchAsync(repositoryPath, branch, cancellationToken).ConfigureAwait(false);
	}

	private async Task<bool> RecordSessionBranchAsync(
		string repositoryPath,
		string? branch,
		CancellationToken cancellationToken)
	{
		if (!await RunSuccessfulAsync(
				repositoryPath,
				["config", "extensions.worktreeConfig", "true"],
				cancellationToken)
			.ConfigureAwait(false))
		{
			return false;
		}

		if (string.IsNullOrWhiteSpace(branch))
		{
			await RunSuccessfulAsync(
					repositoryPath,
					["config", "--worktree", "--unset-all", "devprojex.branch"],
					cancellationToken)
				.ConfigureAwait(false);
			return true;
		}

		return await RunSuccessfulAsync(
				repositoryPath,
				["config", "--worktree", "devprojex.branch", branch.Trim()],
				cancellationToken)
			.ConfigureAwait(false);
	}

	private async Task<bool> RunSuccessfulAsync(
		string workingDirectory,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken) =>
		(await _runAsync(workingDirectory, arguments, cancellationToken).ConfigureAwait(false)).ExitCode == 0;

	private static async Task<GitProcessResult> RunAsync(
		string workingDirectory,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var startInfo = GitProcessStartInfoFactory.Create(
			workingDirectory,
			arguments,
			redirectStandardInput: false);

		using var process = new Process { StartInfo = startInfo };
		if (!process.Start())
			return new GitProcessResult(-1, string.Empty, string.Empty);

		var outputTask = GitProcessOutputReader.ReadAsync(
			process.StandardOutput,
			MaximumProcessOutputCharacters,
			cancellationToken);
		var errorTask = GitProcessOutputReader.ReadAsync(
			process.StandardError,
			MaximumProcessOutputCharacters,
			cancellationToken);
		try
		{
			await GitRepositoryService
				.WaitForExitOrTerminateAsync(process, cancellationToken)
				.ConfigureAwait(false);
			await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
			var output = await outputTask.ConfigureAwait(false);
			var error = await errorTask.ConfigureAwait(false);
			return output.ExceededLimit || error.ExceededLimit
				? new GitProcessResult(-1, string.Empty, "Git process output exceeded the safety limit.")
				: new GitProcessResult(process.ExitCode, output.Text, error.Text);
		}
		catch (OperationCanceledException)
		{
			await GitProcessOutputReader
				.ObserveCompletionAsync(outputTask, errorTask)
				.ConfigureAwait(false);

			throw;
		}
	}

	internal sealed record GitProcessResult(int ExitCode, string Output, string Error);

	private static void TryDeletePartialWorktree(string path)
	{
		try
		{
			if (Directory.Exists(path))
				Directory.Delete(path, recursive: true);
		}
		catch
		{
		}
	}
}

internal enum WorktreeSupportState
{
	Unknown = 0,
	Supported = 1,
	PermanentUnsupported = 2,
	TransientFailure = 3
}
