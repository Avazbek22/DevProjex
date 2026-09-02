namespace DevProjex.Terminal.Tui;

internal enum TerminalRepositoryClonePhase
{
	Cloning,
	SwitchingBranch,
	GettingUpdates
}

internal sealed class TerminalRepositoryCloneLease(
	GitCloneResult result,
	IRepositoryCacheSession session,
	bool updateFailed) : IDisposable
{
	private IRepositoryCacheSession? _session = session;

	public GitCloneResult Result { get; } = result;
	public bool UpdateFailed { get; } = updateFailed;

	public IRepositoryCacheSession DetachSession()
	{
		var session = Interlocked.Exchange(ref _session, null);
		return session ?? throw new ObjectDisposedException(nameof(TerminalRepositoryCloneLease));
	}

	public void Dispose() => Interlocked.Exchange(ref _session, null)?.Dispose();
}

internal sealed class TerminalRepositoryCloneCoordinator(
	IGitRepositoryService git,
	IRepoCacheService cache)
{
	public async Task<TerminalRepositoryCloneLease> AcquireAsync(
		string sourceUrl,
		IProgress<string>? progress,
		Func<TerminalRepositoryClonePhase, Task>? phaseChanged,
		CancellationToken cancellationToken)
	{
		var safeUrl = RepositoryUrlUtility.ToSafeDisplay(sourceUrl);
		string? stagingPath = null;
		IRepositoryCacheSession? session = null;
		try
		{
			await using (await cache
				             .AcquireRepositoryOperationAsync(safeUrl, cancellationToken)
				             .ConfigureAwait(false))
			{
				session = await cache
					.TryAcquireRepositorySessionAsync(safeUrl, cancellationToken: cancellationToken)
					.ConfigureAwait(false);
				if (session is not null)
				{
					var cachedLease = await RefreshCachedAsync(
							session,
							progress,
							phaseChanged,
							cancellationToken)
						.ConfigureAwait(false);
					session = null;
					return cachedLease;
				}

				if (phaseChanged is not null)
					await phaseChanged(TerminalRepositoryClonePhase.Cloning).ConfigureAwait(false);
				stagingPath = cache.CreateRepositoryStagingDirectory(safeUrl);
				var result = await git
					.CloneAsync(sourceUrl, stagingPath, progress, cancellationToken)
					.ConfigureAwait(false);
				if (!result.Success || !Directory.Exists(result.LocalPath))
					throw new InvalidOperationException("The repository clone did not produce a usable working tree.");

				var resultUrl = string.IsNullOrWhiteSpace(result.RepositoryUrl)
					? safeUrl
					: RepositoryUrlUtility.ToSafeDisplay(result.RepositoryUrl);
				var cachePath = cache.PublishRepositoryDirectory(stagingPath, resultUrl);
				stagingPath = null;
				cache.RecordIndexedRepository(resultUrl, cachePath, result.DefaultBranch);
				session = await cache
					.TryAcquireRepositorySessionAsync(resultUrl, cancellationToken: cancellationToken)
					.ConfigureAwait(false);
				if (session is null)
					throw new InvalidOperationException("The published repository cache session is unavailable.");

				var lease = new TerminalRepositoryCloneLease(
					result with
					{
						LocalPath = session.RepositoryPath,
						RepositoryUrl = resultUrl
					},
					session,
					updateFailed: false);
				session = null;
				return lease;
			}
		}
		finally
		{
			session?.Dispose();
			if (stagingPath is not null)
				cache.DeleteRepositoryDirectory(stagingPath);
		}
	}

	private async Task<TerminalRepositoryCloneLease> RefreshCachedAsync(
		IRepositoryCacheSession session,
		IProgress<string>? progress,
		Func<TerminalRepositoryClonePhase, Task>? phaseChanged,
		CancellationToken cancellationToken)
	{
		var branch = session.Branch;
		var updateFailed = false;
		if (session.ContentKind == RepositoryCacheContentKind.Git)
		{
			var refresh = await CachedRepositoryRefreshCoordinator.RefreshAsync(
				git,
				session.RepositoryPath,
				branch,
				phaseChanged is null
					? null
					: phase => phaseChanged(
						phase == CachedRepositoryRefreshPhase.SwitchingBranch
							? TerminalRepositoryClonePhase.SwitchingBranch
							: TerminalRepositoryClonePhase.GettingUpdates),
				progress,
				cancellationToken).ConfigureAwait(false);
			branch = refresh.Branch;
			updateFailed = refresh.UpdateFailed;
		}

		var result = new GitCloneResult(
			true,
			session.RepositoryPath,
			session.ContentKind == RepositoryCacheContentKind.Git
				? ProjectSourceType.GitClone
				: ProjectSourceType.ZipDownload,
			branch,
			RepositoryUrlUtility.GetRepositoryName(session.RepositoryUrl),
			session.RepositoryUrl,
			null);
		var lease = new TerminalRepositoryCloneLease(result, session, updateFailed);
		return lease;
	}
}
