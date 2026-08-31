using System.ComponentModel;
using System.Globalization;
using DevProjex.Application.Context;

namespace DevProjex.Infrastructure.Git;

public sealed class GitRemoteDiffRangeResolver
{
	private const int MaximumFetchDepth = 10_000;
	private const int MaximumOutputCharacters = 32 * 1024;
	private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

	public async Task<string?> ResolveAsync(
		string repositoryPath,
		string repositoryUrl,
		string diffRange,
		string? branch,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);
		var (left, right) = GitScopeSelection.SplitDiffRange(diffRange);
		if (!GitCloneAuthentication.TryResolveCloneUrl(
			repositoryUrl,
			out _,
			out var authentication))
		{
			return null;
		}
		using var askPass = authentication is null ? null : GitAskPassSession.Create(authentication);
		using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutSource.CancelAfter(OperationTimeout);
		IAsyncDisposable? baseLock = null;
		try
		{
			if (RepositoryCacheLayout.IsManaged(repositoryPath))
			{
				var container = RepositoryCacheLayout.GetContainer(repositoryPath);
				baseLock = await RepositoryFileLease.AcquireExclusiveAsync(
					RepositoryCacheLayout.GetBaseOperationLockPath(container, repositoryPath),
					timeoutSource.Token).ConfigureAwait(false);
			}

			var resolvedLeft = await ResolveReferenceAsync(
				repositoryPath,
				left,
				branch,
				askPass,
				timeoutSource.Token).ConfigureAwait(false);
			var resolvedRight = await ResolveReferenceAsync(
				repositoryPath,
				right,
				branch,
				askPass,
				timeoutSource.Token).ConfigureAwait(false);
			return resolvedLeft is null || resolvedRight is null
				? null
				: $"{resolvedLeft}..{resolvedRight}";
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return null;
		}
		finally
		{
			if (baseLock is not null)
				await baseLock.DisposeAsync().ConfigureAwait(false);
		}
	}

	private static async Task<string?> ResolveReferenceAsync(
		string repositoryPath,
		string reference,
		string? branch,
		GitAskPassSession? askPass,
		CancellationToken cancellationToken)
	{
		var existing = await ResolveCommitAsync(repositoryPath, reference, cancellationToken)
			.ConfigureAwait(false);
		if (existing is not null)
			return existing;

		var (baseReference, suffix, depth) = SplitRevision(reference);
		if (string.Equals(baseReference, "HEAD", StringComparison.Ordinal))
		{
			var deepen = await RunAsync(
				repositoryPath,
				["fetch", "--quiet", "--no-tags", "--deepen", depth.ToString(CultureInfo.InvariantCulture), "origin"],
				cancellationToken,
				askPass).ConfigureAwait(false);
			if (deepen?.ExitCode == 0)
			{
				existing = await ResolveCommitAsync(repositoryPath, reference, cancellationToken)
					.ConfigureAwait(false);
				if (existing is not null)
					return existing;
			}

			if (string.IsNullOrWhiteSpace(branch))
				return null;
			baseReference = branch;
		}

		var remoteReference = NormalizeRemoteReference(baseReference);
		var fetch = await RunAsync(
			repositoryPath,
			[
				"fetch",
				"--quiet",
				"--no-tags",
				"--depth",
				depth.ToString(CultureInfo.InvariantCulture),
				"origin",
				remoteReference
			],
			cancellationToken,
			askPass).ConfigureAwait(false);
		return fetch?.ExitCode == 0
			? await ResolveCommitAsync(repositoryPath, "FETCH_HEAD" + suffix, cancellationToken)
				.ConfigureAwait(false)
			: null;
	}

	private static async Task<string?> ResolveCommitAsync(
		string repositoryPath,
		string reference,
		CancellationToken cancellationToken)
	{
		var result = await RunAsync(
			repositoryPath,
			["rev-parse", "--verify", "--quiet", "--end-of-options", reference + "^{commit}"],
			cancellationToken).ConfigureAwait(false);
		return result is { ExitCode: 0 } && !string.IsNullOrWhiteSpace(result.Output)
			? result.Output.Trim()
			: null;
	}

	private static (string BaseReference, string Suffix, int Depth) SplitRevision(string reference)
	{
		var operatorIndex = reference.IndexOfAny(['~', '^']);
		if (operatorIndex < 0)
			return (reference, string.Empty, 1);

		var suffix = reference[operatorIndex..];
		long depth = 1;
		for (var index = 0; index < suffix.Length; index++)
		{
			if (suffix[index] is not ('~' or '^'))
				continue;
			var start = ++index;
			while (index < suffix.Length && char.IsAsciiDigit(suffix[index]))
				index++;
			var count = start == index || !int.TryParse(
				suffix.AsSpan(start, index - start),
				NumberStyles.None,
				CultureInfo.InvariantCulture,
				out var parsed)
				? 1
				: parsed;
			depth = Math.Min(int.MaxValue, depth + count);
			index--;
		}

		return (reference[..operatorIndex], suffix, (int)Math.Min(MaximumFetchDepth, depth));
	}

	private static string NormalizeRemoteReference(string reference)
	{
		const string remotePrefix = "refs/remotes/origin/";
		if (reference.StartsWith(remotePrefix, StringComparison.Ordinal))
			return "refs/heads/" + reference[remotePrefix.Length..];
		return reference.StartsWith("origin/", StringComparison.Ordinal)
			? reference["origin/".Length..]
			: reference;
	}

	private static async Task<GitCommandResult?> RunAsync(
		string repositoryPath,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken,
		GitAskPassSession? askPass = null)
	{
		cancellationToken.ThrowIfCancellationRequested();
		using var process = new Process
		{
			StartInfo = GitProcessStartInfoFactory.Create(repositoryPath, arguments, askPass: askPass)
		};
		try
		{
			if (!process.Start())
				return null;
			process.StandardInput.Close();
		}
		catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
		{
			return null;
		}

		var outputTask = GitProcessOutputReader.ReadAsync(
			process.StandardOutput,
			MaximumOutputCharacters,
			cancellationToken);
		var errorTask = GitProcessOutputReader.ReadAsync(
			process.StandardError,
			MaximumOutputCharacters,
			cancellationToken);
		try
		{
			await GitRepositoryService.WaitForExitOrTerminateAsync(process, cancellationToken)
				.ConfigureAwait(false);
			if (!await GitProcessOutputReader
				    .WaitForCompletionAfterExitAsync(process, outputTask, errorTask)
				    .ConfigureAwait(false))
			{
				return null;
			}

			var output = await outputTask.ConfigureAwait(false);
			var error = await errorTask.ConfigureAwait(false);
			if (output.ExceededLimit || error.ExceededLimit)
				return null;
			return new GitCommandResult(process.ExitCode, output.Text);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			await GitProcessOutputReader
				.ObserveAfterTerminationAsync(process, outputTask, errorTask)
				.ConfigureAwait(false);
			throw;
		}
	}

	private sealed record GitCommandResult(int ExitCode, string Output);
}
