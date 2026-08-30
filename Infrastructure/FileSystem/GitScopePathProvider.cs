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
	private readonly IGitPathComparisonSemanticsResolver _pathComparisonSemanticsResolver;

	public GitScopePathProvider(IGitPathComparisonSemanticsResolver? pathComparisonSemanticsResolver = null)
	{
		_pathComparisonSemanticsResolver = pathComparisonSemanticsResolver ??
		                                   GitConfigPathComparisonSemanticsResolver.Instance;
	}

	public Task<GitScopePathResult> ResolveAsync(
		string projectRoot,
		GitFilteringMode mode,
		string? diffRange,
		CancellationToken cancellationToken = default) =>
		ResolveCoreAsync(projectRoot, mode, diffRange, repositoryRoots: null, cancellationToken);

	public Task<GitScopePathResult> ResolveAsync(
		string projectRoot,
		GitFilteringMode mode,
		string? diffRange,
		IReadOnlyCollection<string> repositoryRoots,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(repositoryRoots);
		return ResolveCoreAsync(projectRoot, mode, diffRange, repositoryRoots, cancellationToken);
	}

	private async Task<GitScopePathResult> ResolveCoreAsync(
		string projectRoot,
		GitFilteringMode mode,
		string? diffRange,
		IReadOnlyCollection<string>? repositoryRoots,
		CancellationToken cancellationToken)
	{
		using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutSource.CancelAfter(CommandTimeout);
		try
		{
			return await ResolveWithinTimeoutAsync(
					projectRoot,
					mode,
					diffRange,
					repositoryRoots,
					timeoutSource.Token)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return GitScopePathResult.Unavailable("Git state resolution exceeded 30 seconds.");
		}
	}

	private async Task<GitScopePathResult> ResolveWithinTimeoutAsync(
		string projectRoot,
		GitFilteringMode mode,
		string? diffRange,
		IReadOnlyCollection<string>? repositoryRoots,
		CancellationToken cancellationToken)
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

		if (Volatile.Read(ref _gitAvailability) < 0)
			return GitScopePathResult.Unavailable("Git is not available on PATH.");
		var resolvedRepositoryRoots = ResolveRepositoryRoots(
			normalizedProjectRoot,
			repositoryRoots,
			cancellationToken);
		if (resolvedRepositoryRoots.Count == 0)
			return GitScopePathResult.Unavailable("The project is not inside a Git repository.");

		var included = new HashSet<string>(StringComparer.Ordinal);
		var deleted = new HashSet<string>(StringComparer.Ordinal);
		var matchers = new List<GitTrackedPathIndex>(resolvedRepositoryRoots.Count);
		foreach (var repositoryRoot in resolvedRepositoryRoots)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var repositoryResult = await ResolveRepositoryAsync(
					normalizedProjectRoot,
					repositoryRoot,
					mode,
					diffRange,
					cancellationToken)
				.ConfigureAwait(false);
			if (!repositoryResult.IsAvailable)
				return GitScopePathResult.Unavailable(repositoryResult.FailureReason);

			included.UnionWith(repositoryResult.IncludedPaths);
			deleted.UnionWith(repositoryResult.DeletedPaths);
			matchers.Add(repositoryResult.PathMatcher!);
		}

		return new GitScopePathResult(
			true,
			included,
			deleted.Count,
			PathMatchers: matchers);
	}

	private async Task<RepositoryScopeResult> ResolveRepositoryAsync(
		string normalizedProjectRoot,
		string repositoryRoot,
		GitFilteringMode mode,
		string? diffRange,
		CancellationToken cancellationToken)
	{
		var comparisonSemantics = _pathComparisonSemanticsResolver.Resolve(repositoryRoot);
		cancellationToken.ThrowIfCancellationRequested();
		if (!comparisonSemantics.IsAuthoritative)
			return RepositoryScopeResult.Unavailable("Git path comparison settings could not be resolved.");

		IReadOnlyList<GitCommandOutput> outputs;
		switch (mode)
		{
			case GitFilteringMode.Staged:
				outputs =
				[
					await RunAsync(
						repositoryRoot,
						CreateDiffArguments(cached: true, diffRange: null),
						cancellationToken).ConfigureAwait(false)
				];
				break;
			case GitFilteringMode.Changes:
				var outputBudget = new GitScopeOutputBudget(MaximumOutputBytes);
				var stagedTask = RunAsync(
					repositoryRoot,
					CreateDiffArguments(cached: true, diffRange: null),
					cancellationToken,
					outputBudget);
				var unstagedTask = RunAsync(
					repositoryRoot,
					CreateDiffArguments(cached: false, diffRange: null),
					cancellationToken,
					outputBudget);
				var untrackedTask = RunAsync(
					repositoryRoot,
					["ls-files", "--others", "--exclude-standard", "-z", "--"],
					cancellationToken,
					outputBudget);
				outputs = await Task.WhenAll(stagedTask, unstagedTask, untrackedTask)
					.ConfigureAwait(false);
				break;
			case GitFilteringMode.Diff:
				if (!GitScopeSelection.IsValidDiffRange(diffRange))
					return RepositoryScopeResult.Unavailable("The Git diff range is invalid.");
				outputs =
				[
					await RunAsync(
						repositoryRoot,
						CreateDiffArguments(cached: false, diffRange),
						cancellationToken).ConfigureAwait(false)
				];
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
		}

		var failed = outputs.FirstOrDefault(static output => !output.Succeeded);
		if (failed is not null)
			return RepositoryScopeResult.Unavailable(failed.FailureReason);

		var included = new HashSet<string>(StringComparer.Ordinal);
		var deleted = new HashSet<string>(StringComparer.Ordinal);
		var unsupportedDirectories = new HashSet<string>(StringComparer.Ordinal);
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
					return RepositoryScopeResult.Unavailable("Git returned an invalid untracked-file list.");
				}
				continue;
			}

			if (!TryParseNameStatus(
				output.Values,
				repositoryRoot,
				normalizedProjectRoot,
				included,
				deleted,
				unsupportedDirectories))
			{
				return RepositoryScopeResult.Unavailable("Git returned an invalid name-status result.");
			}
		}

		ReconcileWorkingTreePaths(included, deleted, cancellationToken);
		deleted.UnionWith(unsupportedDirectories);
		var matcher = new GitTrackedPathIndex(
			repositoryRoot,
			included.Select(path => PathUtility.GetPortableRelativePath(repositoryRoot, path)),
			comparisonSemantics);
		deleted.RemoveWhere(matcher.Contains);

		return new RepositoryScopeResult(true, included, deleted, matcher);
	}

	internal static IReadOnlyList<string> CreateDiffArguments(bool cached, string? diffRange)
	{
		var arguments = new List<string>(8)
		{
			"diff",
			"--no-ext-diff",
			"--no-textconv",
			"--name-status",
			"-z"
		};
		if (cached)
			arguments.Add("--cached");
		if (!string.IsNullOrEmpty(diffRange))
			arguments.Add(diffRange);
		arguments.Add("--");
		return arguments;
	}

	private static IReadOnlyList<string> ResolveRepositoryRoots(
		string projectRoot,
		IReadOnlyCollection<string>? repositoryRoots,
		CancellationToken cancellationToken)
	{
		var resolved = new HashSet<string>(StringComparer.Ordinal);
		if (repositoryRoots is not null)
		{
			foreach (var candidate in repositoryRoots)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (string.IsNullOrWhiteSpace(candidate))
					continue;
				try
				{
					var normalized = PathUtility.Normalize(candidate);
					if (PathUtility.IsPathInside(normalized, projectRoot) ||
					    PathUtility.IsPathInside(projectRoot, normalized))
					{
						resolved.Add(normalized);
					}
				}
				catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
				{
				}
			}
		}

		if (GitTrackedPathIndexCache.TryFindNearestRepositoryBoundary(
			projectRoot,
			cancellationToken,
			out var nearestRepositoryRoot))
		{
			resolved.Add(nearestRepositoryRoot);
		}

		return resolved
			.OrderBy(static path => path.Length)
			.ThenBy(static path => path, PathComparer.Default)
			.ThenBy(static path => path, StringComparer.Ordinal)
			.ToArray();
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

		foreach (var path in deleted.ToArray())
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (File.Exists(path))
			{
				deleted.Remove(path);
				included.Add(path);
				continue;
			}
			if (Directory.Exists(path))
				deleted.Remove(path);
		}

		deleted.ExceptWith(included);
	}

	internal static bool TryParseNameStatus(
		IReadOnlyList<string> values,
		string repositoryRoot,
		string projectRoot,
		ISet<string> included,
		ISet<string> deleted,
		ISet<string>? unsupportedDirectories = null)
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
			var added = TryAddProjectPath(
				repositoryRoot,
				projectRoot,
				firstPath,
				included,
				rejectDirectories: true);
			if (code == 'T' && !added && unsupportedDirectories is not null)
			{
				TryAddProjectPath(
					repositoryRoot,
					projectRoot,
					firstPath,
					unsupportedDirectories);
			}
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

	private static bool TryAddProjectPath(
		string repositoryRoot,
		string projectRoot,
		string gitPath,
		ISet<string> destination,
		bool rejectDirectories = false)
	{
		if (string.IsNullOrEmpty(gitPath) || Path.IsPathRooted(gitPath))
			return false;

		try
		{
			var platformPath = gitPath.Replace('/', Path.DirectorySeparatorChar);
			var fullPath = PathUtility.Normalize(Path.Combine(repositoryRoot, platformPath));
			if (PathUtility.IsPathInside(fullPath, projectRoot) &&
			    (!rejectDirectories || !Directory.Exists(fullPath)))
			{
				destination.Add(fullPath);
				return true;
			}
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
		}
		return false;
	}

	private static async Task<GitCommandOutput> RunAsync(
		string repositoryRoot,
		IReadOnlyList<string> commandArguments,
		CancellationToken cancellationToken,
		GitScopeOutputBudget? outputBudget = null)
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
			MaximumPathLength,
			outputBudget is null ? null : outputBudget.TryReserve);
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

	internal static ProcessStartInfo CreateStartInfo(
		string repositoryRoot,
		IReadOnlyList<string> commandArguments)
	{
		var arguments = new List<string>(commandArguments.Count + 6)
		{
			"-C",
			repositoryRoot,
			"-c",
			"core.quotepath=false",
			"-c",
			"core.fsmonitor=false"
		};
		arguments.AddRange(commandArguments);
		var startInfo = GitProcessStartInfoFactory.Create(repositoryRoot, arguments);
		startInfo.StandardOutputEncoding = new UTF8Encoding(
			encoderShouldEmitUTF8Identifier: false,
			throwOnInvalidBytes: false);
		startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
		startInfo.Environment["GIT_NO_LAZY_FETCH"] = "1";
		return startInfo;
	}

	internal sealed class GitScopeOutputBudget
	{
		private long _remainingBytes;

		public GitScopeOutputBudget(long maximumBytes)
		{
			if (maximumBytes <= 0)
				throw new ArgumentOutOfRangeException(nameof(maximumBytes));

			_remainingBytes = maximumBytes;
		}

		internal long RemainingBytes => Volatile.Read(ref _remainingBytes);

		public bool TryReserve(long bytes)
		{
			if (bytes <= 0)
				throw new ArgumentOutOfRangeException(nameof(bytes));

			while (true)
			{
				var remaining = Volatile.Read(ref _remainingBytes);
				if (remaining < bytes)
					return false;
				if (Interlocked.CompareExchange(ref _remainingBytes, remaining - bytes, remaining) == remaining)
					return true;
			}
		}
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

	private sealed record RepositoryScopeResult(
		bool IsAvailable,
		IReadOnlySet<string> IncludedPaths,
		IReadOnlySet<string> DeletedPaths,
		GitTrackedPathIndex? PathMatcher = null,
		string? FailureReason = null)
	{
		public static RepositoryScopeResult Unavailable(string? reason) =>
			new(
				false,
				new HashSet<string>(StringComparer.Ordinal),
				new HashSet<string>(StringComparer.Ordinal),
				FailureReason: reason);
	}

	private sealed record GitCommandOutput(
		bool Succeeded,
		IReadOnlyList<string> Values,
		string? FailureReason)
	{
		public static GitCommandOutput Failed(string reason) => new(false, [], reason);
	}
}
