using System.Runtime.InteropServices;

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
	private static readonly string GitExecutable =
		RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "git.exe" : "git";
	private readonly object _supportSync = new();
	private Task<bool>? _supportProbe;

	public Task<bool> IsSupportedAsync(string basePath, CancellationToken cancellationToken)
	{
		lock (_supportSync)
		{
			_supportProbe ??= ProbeSupportAsync(basePath);
			return _supportProbe.WaitAsync(cancellationToken);
		}
	}

	public async Task<bool> PreparePrimaryAsync(
		string basePath,
		string? branch,
		CancellationToken cancellationToken)
	{
		var revision = await ResolveRevisionAsync(basePath, branch, cancellationToken)
			.ConfigureAwait(false);
		if (!await RunAsync(basePath, ["checkout", "--detach", revision], cancellationToken)
			.ConfigureAwait(false))
		{
			return false;
		}

		return await RecordSessionBranchAsync(basePath, branch, cancellationToken)
			.ConfigureAwait(false);
	}

	public async Task<bool> CreateDetachedAsync(
		string basePath,
		string worktreePath,
		string? branch,
		CancellationToken cancellationToken)
	{
		var revision = await ResolveRevisionAsync(basePath, branch, cancellationToken)
			.ConfigureAwait(false);
		if (!await RunAsync(
				basePath,
				["worktree", "add", "--detach", worktreePath, revision],
				cancellationToken)
			.ConfigureAwait(false))
		{
			TryDeletePartialWorktree(worktreePath);
			return false;
		}

		if (await RecordSessionBranchAsync(worktreePath, branch, cancellationToken)
			.ConfigureAwait(false))
		{
			return true;
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
		await RunAsync(
				basePath,
				["worktree", "remove", "--force", worktreePath],
				cancellationToken)
			.ConfigureAwait(false);
	}

	public async Task PruneAsync(string basePath, CancellationToken cancellationToken)
	{
		await RunAsync(basePath, ["worktree", "prune"], cancellationToken).ConfigureAwait(false);
	}

	private async Task<bool> ProbeSupportAsync(string basePath)
	{
		try
		{
			return await RunAsync(
					basePath,
					["worktree", "list", "--porcelain"],
					CancellationToken.None)
				.ConfigureAwait(false);
		}
		catch
		{
			return false;
		}
	}

	private static async Task<string> ResolveRevisionAsync(
		string basePath,
		string? branch,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(branch))
			return "HEAD";

		foreach (var candidate in new[]
		{
			$"refs/remotes/origin/{branch}",
			$"refs/heads/{branch}"
		})
		{
			if (await RunAsync(
					basePath,
					["rev-parse", "--verify", "--quiet", candidate],
					cancellationToken)
				.ConfigureAwait(false))
			{
				return candidate;
			}
		}

		return "HEAD";
	}

	private static async Task<bool> RecordSessionBranchAsync(
		string repositoryPath,
		string? branch,
		CancellationToken cancellationToken)
	{
		if (!await RunAsync(
				repositoryPath,
				["config", "extensions.worktreeConfig", "true"],
				cancellationToken)
			.ConfigureAwait(false))
		{
			return false;
		}

		if (string.IsNullOrWhiteSpace(branch))
		{
			await RunAsync(
					repositoryPath,
					["config", "--worktree", "--unset-all", "devprojex.branch"],
					cancellationToken)
				.ConfigureAwait(false);
			return true;
		}

		return await RunAsync(
				repositoryPath,
				["config", "--worktree", "devprojex.branch", branch.Trim()],
				cancellationToken)
			.ConfigureAwait(false);
	}

	private static async Task<bool> RunAsync(
		string workingDirectory,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var startInfo = new ProcessStartInfo
		{
			FileName = GitExecutable,
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		GitProcessEnvironmentSanitizer.RemoveRepositoryOverrides(startInfo);

		using var process = new Process { StartInfo = startInfo };
		if (!process.Start())
			return false;

		var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
		try
		{
			await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
			await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
			return process.ExitCode == 0;
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
