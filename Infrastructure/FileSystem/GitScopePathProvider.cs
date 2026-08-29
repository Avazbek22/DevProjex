using System.Buffers;
using System.ComponentModel;
using DevProjex.Application.Context;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.Git;

namespace DevProjex.Infrastructure.FileSystem;

public sealed class GitScopePathProvider : IGitScopePathProvider
{
	private const long MaximumOutputBytes = 64L * 1024 * 1024;
	private const int MaximumPathLength = 32768;
	private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
	private static int _gitAvailability;

	public async Task<GitScopePathResult> ResolveAsync(
		string projectRoot,
		GitFilteringMode mode,
		string? diffRange,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		if (!GitScopeSelection.IsMomentary(mode))
			throw new ArgumentOutOfRangeException(nameof(mode), mode, "A momentary Git scope is required.");

		string normalizedProjectRoot;
		try
		{
			normalizedProjectRoot = PathUtility.Normalize(projectRoot);
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			return GitScopePathResult.Unavailable("The project root is invalid.");
		}

		if (!GitTrackedPathIndexCache.TryFindNearestRepositoryBoundary(
			normalizedProjectRoot,
			cancellationToken,
			out var repositoryRoot))
		{
			return GitScopePathResult.Unavailable("The project is not inside a Git repository.");
		}
		if (Volatile.Read(ref _gitAvailability) < 0)
			return GitScopePathResult.Unavailable("Git is not available on PATH.");

		IReadOnlyList<GitCommandOutput> outputs;
		switch (mode)
		{
			case GitFilteringMode.Staged:
				outputs =
				[
					await RunAsync(
						repositoryRoot,
						["diff", "--name-status", "-z", "--cached", "--"],
						cancellationToken).ConfigureAwait(false)
				];
				break;
			case GitFilteringMode.Changes:
				var stagedTask = RunAsync(
					repositoryRoot,
					["diff", "--name-status", "-z", "--cached", "--"],
					cancellationToken);
				var unstagedTask = RunAsync(
					repositoryRoot,
					["diff", "--name-status", "-z", "--"],
					cancellationToken);
				var untrackedTask = RunAsync(
					repositoryRoot,
					["ls-files", "--others", "--exclude-standard", "-z", "--"],
					cancellationToken);
				outputs = await Task.WhenAll(stagedTask, unstagedTask, untrackedTask)
					.ConfigureAwait(false);
				break;
			case GitFilteringMode.Diff:
				if (!GitScopeSelection.IsValidDiffRange(diffRange))
					return GitScopePathResult.Unavailable("The Git diff range is invalid.");
				outputs =
				[
					await RunAsync(
						repositoryRoot,
						["diff", "--name-status", "-z", diffRange!, "--"],
						cancellationToken).ConfigureAwait(false)
				];
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
		}

		var failed = outputs.FirstOrDefault(static output => !output.Succeeded);
		if (failed is not null)
			return GitScopePathResult.Unavailable(failed.FailureReason);

		var included = new HashSet<string>(PathComparer.Default);
		var deleted = new HashSet<string>(PathComparer.Default);
		for (var index = 0; index < outputs.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var output = outputs[index];
			if (mode == GitFilteringMode.Changes && index == 2)
			{
				if (!TryAddUntrackedPaths(
					output.Values,
					repositoryRoot,
					normalizedProjectRoot,
					included))
				{
					return GitScopePathResult.Unavailable("Git returned an invalid untracked-file list.");
				}
				continue;
			}

			if (!TryParseNameStatus(
				output.Values,
				repositoryRoot,
				normalizedProjectRoot,
				included,
				deleted))
			{
				return GitScopePathResult.Unavailable("Git returned an invalid name-status result.");
			}
		}

		ReconcileWorkingTreePaths(included, deleted, cancellationToken);

		return new GitScopePathResult(true, included, deleted.Count);
	}

	private static void ReconcileWorkingTreePaths(
		HashSet<string> included,
		HashSet<string> deleted,
		CancellationToken cancellationToken)
	{
		foreach (var path in included.ToArray())
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (File.Exists(path))
				continue;

			included.Remove(path);
			deleted.Add(path);
		}

		deleted.ExceptWith(included);
	}

	internal static bool TryParseNameStatus(
		IReadOnlyList<string> values,
		string repositoryRoot,
		string projectRoot,
		ISet<string> included,
		ISet<string> deleted)
	{
		ArgumentNullException.ThrowIfNull(values);
		ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentNullException.ThrowIfNull(included);
		ArgumentNullException.ThrowIfNull(deleted);

		for (var index = 0; index < values.Count;)
		{
			var status = values[index++];
			if (status.Length == 0 || index >= values.Count)
				return false;

			var code = status[0];
			var firstPath = values[index++];
			if (code is 'R' or 'C')
			{
				if (index >= values.Count)
					return false;
				var destinationPath = values[index++];
				if (code == 'R')
					TryAddProjectPath(repositoryRoot, projectRoot, firstPath, deleted);
				TryAddProjectPath(
					repositoryRoot,
					projectRoot,
					destinationPath,
					included,
					rejectDirectories: true);
				continue;
			}

			if (code == 'D')
			{
				TryAddProjectPath(repositoryRoot, projectRoot, firstPath, deleted);
				continue;
			}

			if (code is not ('A' or 'M' or 'T' or 'U' or 'X' or 'B'))
				return false;
			TryAddProjectPath(
				repositoryRoot,
				projectRoot,
				firstPath,
				included,
				rejectDirectories: true);
		}

		return true;
	}

	internal static bool TryAddUntrackedPaths(
		IReadOnlyList<string> values,
		string repositoryRoot,
		string projectRoot,
		ISet<string> included)
	{
		ArgumentNullException.ThrowIfNull(values);
		foreach (var value in values)
		{
			if (string.IsNullOrEmpty(value))
				return false;
			TryAddProjectPath(
				repositoryRoot,
				projectRoot,
				value,
				included,
				rejectDirectories: true);
		}
		return true;
	}

	private static void TryAddProjectPath(
		string repositoryRoot,
		string projectRoot,
		string gitPath,
		ISet<string> destination,
		bool rejectDirectories = false)
	{
		if (string.IsNullOrEmpty(gitPath) || Path.IsPathRooted(gitPath))
			return;

		try
		{
			var platformPath = gitPath.Replace('/', Path.DirectorySeparatorChar);
			var fullPath = PathUtility.Normalize(Path.Combine(repositoryRoot, platformPath));
			if (PathUtility.IsPathInside(fullPath, projectRoot) &&
			    (!rejectDirectories || !Directory.Exists(fullPath)))
			{
				destination.Add(fullPath);
			}
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
		}
	}

	private static async Task<GitCommandOutput> RunAsync(
		string repositoryRoot,
		IReadOnlyList<string> commandArguments,
		CancellationToken cancellationToken)
	{
		using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutSource.CancelAfter(CommandTimeout);
		using var process = new Process
		{
			StartInfo = CreateStartInfo(repositoryRoot, commandArguments)
		};
		try
		{
			if (!process.Start())
				return GitCommandOutput.Failed("Git could not be started.");
			process.StandardInput.Close();
			Volatile.Write(ref _gitAvailability, 1);
		}
		catch (Win32Exception exception)
		{
			if (GitTrackedPathIndexCache.IsPermanentGitStartFailure(exception))
				Volatile.Write(ref _gitAvailability, -1);
			return GitCommandOutput.Failed("Git is not available on PATH.");
		}

		var outputLimitReached = false;
		var valuesTask = GitTrackedPathIndexCache.ReadNullDelimitedPathsAsync(
			process.StandardOutput,
			timeoutSource.Token,
			() =>
			{
				outputLimitReached = true;
				TryTerminate(process);
			},
			MaximumOutputBytes,
			MaximumPathLength);
		var errorTask = DrainAsync(process.StandardError, timeoutSource.Token);
		try
		{
			await GitRepositoryService
				.WaitForExitOrTerminateAsync(process, timeoutSource.Token)
				.ConfigureAwait(false);
			if (!await GitProcessOutputReader
				    .WaitForCompletionAfterExitAsync(process, valuesTask, errorTask)
				    .ConfigureAwait(false))
			{
				return GitCommandOutput.Failed("Git output could not be read.");
			}
			var values = await valuesTask.ConfigureAwait(false);
			await errorTask.ConfigureAwait(false);
			if (outputLimitReached || values is null)
				return GitCommandOutput.Failed("Git state output exceeded the supported limit.");
			return process.ExitCode == 0
				? new GitCommandOutput(true, values, null)
				: GitCommandOutput.Failed("Git could not resolve the requested state or references.");
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			await GitProcessOutputReader
				.ObserveAfterTerminationAsync(process, valuesTask, errorTask)
				.ConfigureAwait(false);
			return GitCommandOutput.Failed("Git state resolution exceeded 30 seconds.");
		}
		catch (OperationCanceledException)
		{
			await GitProcessOutputReader
				.ObserveAfterTerminationAsync(process, valuesTask, errorTask)
				.ConfigureAwait(false);
			throw;
		}
	}

	private static ProcessStartInfo CreateStartInfo(
		string repositoryRoot,
		IReadOnlyList<string> commandArguments)
	{
		var arguments = new List<string>(commandArguments.Count + 4)
		{
			"-C",
			repositoryRoot,
			"-c",
			"core.quotepath=false"
		};
		arguments.AddRange(commandArguments);
		var startInfo = GitProcessStartInfoFactory.Create(repositoryRoot, arguments);
		startInfo.StandardOutputEncoding = new UTF8Encoding(
			encoderShouldEmitUTF8Identifier: false,
			throwOnInvalidBytes: false);
		startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
		return startInfo;
	}

	private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
	{
		var buffer = ArrayPool<char>.Shared.Rent(1024);
		try
		{
			while (await reader
			       .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
			       .ConfigureAwait(false) > 0)
			{
			}
		}
		finally
		{
			ArrayPool<char>.Shared.Return(buffer, clearArray: true);
		}
	}

	private static void TryTerminate(Process process)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
		{
		}
	}

	private sealed record GitCommandOutput(
		bool Succeeded,
		IReadOnlyList<string> Values,
		string? FailureReason)
	{
		public static GitCommandOutput Failed(string reason) => new(false, [], reason);
	}
}
