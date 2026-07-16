using System.Collections.Concurrent;

namespace DevProjex.Infrastructure.RecentProjects;

public sealed class RecentFolderAvailabilityService(Func<string, bool>? directoryExists = null)
{
	private const int MaxConcurrentChecks = 4;
	private readonly Func<string, bool> _directoryExists = directoryExists ?? Directory.Exists;

	public Task<bool> IsAvailableAsync(string path, CancellationToken cancellationToken = default)
		=> Task.Run(() => IsAvailable(path), cancellationToken);

	public async Task<IReadOnlyDictionary<string, bool>> CheckAsync(
		IEnumerable<string> paths,
		CancellationToken cancellationToken = default)
	{
		var snapshot = paths
			.Where(static path => !string.IsNullOrWhiteSpace(path))
			.Distinct(PathComparer.Default)
			.ToArray();
		var results = new ConcurrentDictionary<string, bool>(PathComparer.Default);

		await Parallel.ForEachAsync(
			snapshot,
			new ParallelOptions
			{
				CancellationToken = cancellationToken,
				MaxDegreeOfParallelism = MaxConcurrentChecks
			},
			(path, _) =>
			{
				results[path] = IsAvailable(path);
				return ValueTask.CompletedTask;
			}).ConfigureAwait(false);

		return new Dictionary<string, bool>(results, PathComparer.Default);
	}

	private bool IsAvailable(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return false;

		try
		{
			return _directoryExists(path);
		}
		catch
		{
			return false;
		}
	}
}
