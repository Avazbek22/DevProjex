using System.ComponentModel;

namespace DevProjex.Infrastructure.Git;

internal interface IGitWorktreeManager
{
	Task<bool> IsSupportedAsync(string basePath, CancellationToken cancellationToken);
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
	private readonly object _supportSync = new();
	private readonly Func<string, Task<WorktreeSupportState>> _probeSupport;
	private Task<WorktreeSupportState>? _supportProbe;
	private WorktreeSupportState _cachedSupportState;

	public GitWorktreeManager()
	{
		_probeSupport = basePath => ProbeSupportAsync(basePath, RunAsync, SupportProbeTimeout);
	}

	internal GitWorktreeManager(Func<string, Task<WorktreeSupportState>> probeSupport)
	{
		_probeSupport = probeSupport ?? throw new ArgumentNullException(nameof(probeSupport));
	}

	internal GitWorktreeManager(
		Func<string, IReadOnlyList<string>, CancellationToken, Task<GitProcessResult>> runAsync,
		TimeSpan supportProbeTimeout)
	{
		ArgumentNullException.ThrowIfNull(runAsync);
		if (supportProbeTimeout <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(supportProbeTimeout));
		_probeSupport = basePath => ProbeSupportAsync(basePath, runAsync, supportProbeTimeout);
	}

	public async Task<bool> IsSupportedAsync(string basePath, CancellationToken cancellationToken)
	{
		Task<WorktreeSupportState> probe;
		lock (_supportSync)
		{
			if (_cachedSupportState != WorktreeSupportState.Unknown)
				return _cachedSupportState == WorktreeSupportState.Supported;
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

		return state == WorktreeSupportState.Supported;
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
			await RemoveAsync(basePath, worktreePath, CancellationToken.None).ConfigureAwait(false);
			TryDeletePartialWorktree(worktreePath);
			throw;
		}

		await RemoveAsync(basePath, worktreePath, CancellationToken.None).ConfigureAwait(false);
		TryDeletePartialWorktree(worktreePath);
		return false;
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
		catch (Exception exception) when (exception is Win32Exception or PlatformNotSupportedException)
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

	private static async Task<string> ResolveRevisionAsync(
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

	private static async Task<string> ResolveCommitAsync(
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

	private static async Task<string?> TryResolveCommitAsync(
		string basePath,
		string revision,
		CancellationToken cancellationToken)
	{
		var result = await RunAsync(
			basePath,
			["rev-parse", "--verify", "--quiet", $"{revision}^{{commit}}"],
			cancellationToken).ConfigureAwait(false);
		if (result.ExitCode != 0)
			return null;
		var commit = result.Output.Trim();
		return commit.Length == 0 ? null : commit;
	}

	private static async Task<bool> VerifyAndRecordAsync(
		string repositoryPath,
		string expectedCommit,
		string? branch,
		CancellationToken cancellationToken)
	{
		var head = await RunAsync(repositoryPath, ["rev-parse", "HEAD"], cancellationToken)
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

	private static async Task<bool> RecordSessionBranchAsync(
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

	private static async Task<bool> RunSuccessfulAsync(
		string workingDirectory,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken) =>
		(await RunAsync(workingDirectory, arguments, cancellationToken).ConfigureAwait(false)).ExitCode == 0;

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

		var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
		try
		{
			await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
			await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
			return new GitProcessResult(process.ExitCode, await outputTask, await errorTask);
		}
		catch (OperationCanceledException)
		{
			try
			{
				if (!process.HasExited)
					process.Kill(entireProcessTree: true);
			}
			catch
			{
			}

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
