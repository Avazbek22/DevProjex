namespace DevProjex.Avalonia.Coordinators;

internal enum CachedRepositoryRefreshPhase
{
	SwitchingBranch,
	GettingUpdates
}

internal readonly record struct CachedRepositoryRefreshResult(
	string? Branch,
	bool UpdateFailed);

internal static class CachedRepositoryRefreshCoordinator
{
	public static async Task<CachedRepositoryRefreshResult> RefreshAsync(
		IGitRepositoryService gitService,
		string repositoryPath,
		string? fallbackBranch,
		Action<CachedRepositoryRefreshPhase>? phaseChanged,
		IProgress<string>? progress,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(gitService);
		var updateFailed = false;
		var branch = fallbackBranch;

		try
		{
			var defaultBranch = await gitService
				.GetDefaultBranchAsync(repositoryPath, cancellationToken)
				.ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			if (!string.IsNullOrWhiteSpace(defaultBranch))
			{
				phaseChanged?.Invoke(CachedRepositoryRefreshPhase.SwitchingBranch);
				var switched = await gitService
					.SwitchBranchAsync(repositoryPath, defaultBranch, progress, cancellationToken)
					.ConfigureAwait(false);
				cancellationToken.ThrowIfCancellationRequested();
				if (switched)
					branch = defaultBranch;
				updateFailed |= !switched;
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			updateFailed = true;
		}

		try
		{
			branch = await gitService
				.GetCurrentBranchAsync(repositoryPath, cancellationToken)
				.ConfigureAwait(false) ?? branch;
			cancellationToken.ThrowIfCancellationRequested();
			phaseChanged?.Invoke(CachedRepositoryRefreshPhase.GettingUpdates);
			var pulled = await gitService
				.PullUpdatesAsync(repositoryPath, progress, cancellationToken)
				.ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			updateFailed |= !pulled;
			branch = await gitService
				.GetCurrentBranchAsync(repositoryPath, cancellationToken)
				.ConfigureAwait(false) ?? branch;
			cancellationToken.ThrowIfCancellationRequested();
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			updateFailed = true;
		}

		return new CachedRepositoryRefreshResult(branch, updateFailed);
	}
}
