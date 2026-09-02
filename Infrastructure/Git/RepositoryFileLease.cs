namespace DevProjex.Infrastructure.Git;

internal sealed class RepositoryFileLease : IDisposable, IAsyncDisposable
{
	private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);
	private FileStream? _stream;

	private RepositoryFileLease(FileStream stream)
	{
		_stream = stream;
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
			lease = new RepositoryFileLease(stream);
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
		Interlocked.Exchange(ref _stream, null)?.Dispose();
	}

	public ValueTask DisposeAsync()
	{
		Dispose();
		return ValueTask.CompletedTask;
	}
}
