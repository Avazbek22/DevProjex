using System.Collections.Concurrent;

namespace DevProjex.Infrastructure.Git;

internal sealed class RepositoryFileLease : IDisposable, IAsyncDisposable
{
	private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);
	private static readonly ConcurrentDictionary<string, int> ActiveLeasePaths =
		new(PathComparer.Default);
	private readonly string _path;
	private FileStream? _stream;

	private RepositoryFileLease(string path, FileStream stream)
	{
		_path = path;
		_stream = stream;
		ActiveLeasePaths.AddOrUpdate(path, 1, static (_, count) => count + 1);
	}

	public static bool TryAcquireExclusive(string path, out RepositoryFileLease? lease) =>
		TryAcquire(path, FileShare.None, out lease);

	public static bool TryAcquireShared(string path, out RepositoryFileLease? lease) =>
		TryAcquire(path, FileShare.ReadWrite, out lease);

	public static async Task<RepositoryFileLease> AcquireExclusiveAsync(
		string path,
		CancellationToken cancellationToken)
	{
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (TryAcquireExclusive(path, out var lease))
				return lease!;

			await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
		}
	}

	private static bool TryAcquire(
		string path,
		FileShare share,
		out RepositoryFileLease? lease)
	{
		lease = null;
		try
		{
			var directory = Path.GetDirectoryName(path);
			if (string.IsNullOrWhiteSpace(directory))
				return false;

			Directory.CreateDirectory(directory);
			var stream = new FileStream(
				path,
				FileMode.OpenOrCreate,
				FileAccess.ReadWrite,
				share,
				bufferSize: 1,
				FileOptions.None);
			lease = new RepositoryFileLease(PathUtility.Normalize(path), stream);
			return true;
		}
		catch (Exception ex) when (ex is
			IOException or
			UnauthorizedAccessException or
			ArgumentException or
			NotSupportedException)
		{
			return false;
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _stream, null) is not { } stream)
			return;
		stream.Dispose();
		ActiveLeasePaths.AddOrUpdate(_path, 0, static (_, count) => Math.Max(0, count - 1));
		if (ActiveLeasePaths.TryGetValue(_path, out var count) && count == 0)
			ActiveLeasePaths.TryRemove(new KeyValuePair<string, int>(_path, 0));
	}

	internal static bool HasActiveLeaseWithin(string directory)
	{
		var normalized = PathUtility.Normalize(directory);
		return ActiveLeasePaths.Any(pair => pair.Value > 0 && PathUtility.IsPathInside(pair.Key, normalized));
	}

	public ValueTask DisposeAsync()
	{
		Dispose();
		return ValueTask.CompletedTask;
	}
}
