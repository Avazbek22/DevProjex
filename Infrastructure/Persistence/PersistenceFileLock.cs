namespace DevProjex.Infrastructure.Persistence;

public static class PersistenceFileLock
{
	private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

	public static ValueTask<IDisposable> AcquireAsync(
		string primaryPath,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(primaryPath);
		var normalizedPath = Path.GetFullPath(primaryPath);
		return CrossProcessFileLock.AcquireAsync(
			new JsonStoreFileSet(
				normalizedPath,
				normalizedPath + ".bak",
				normalizedPath + ".lock"),
			DefaultTimeout,
			cancellationToken);
	}
}
