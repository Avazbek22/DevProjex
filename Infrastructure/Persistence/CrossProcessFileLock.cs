using System.Diagnostics;

namespace DevProjex.Infrastructure.Persistence;

internal static class CrossProcessFileLock
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    public static IDisposable Acquire(JsonStoreFileSet fileSet)
        => Acquire(fileSet, DefaultTimeout);

    public static bool TryAcquire(JsonStoreFileSet fileSet, out IDisposable? heldLock)
        => TryAcquire(fileSet, DefaultTimeout, out heldLock);

    public static bool TryAcquire(JsonStoreFileSet fileSet, TimeSpan timeout, out IDisposable? heldLock)
	{
		try
		{
			heldLock = Acquire(fileSet, timeout);
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			heldLock = null;
			return false;
		}
	}

	public static bool TryAcquireWithCancellation(
		JsonStoreFileSet fileSet,
		TimeSpan timeout,
		CancellationToken cancellationToken,
		out IDisposable? heldLock)
    {
        try
        {
            heldLock = AcquireWithCancellation(fileSet, timeout, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            heldLock = null;
            return false;
        }
    }

    public static IDisposable Acquire(JsonStoreFileSet fileSet, TimeSpan timeout)
		=> AcquireCore(fileSet, timeout, CancellationToken.None);

	public static IDisposable AcquireWithCancellation(
		JsonStoreFileSet fileSet,
		TimeSpan timeout,
		CancellationToken cancellationToken) =>
		AcquireCore(fileSet, timeout, cancellationToken);

	private static IDisposable AcquireCore(
		JsonStoreFileSet fileSet,
		TimeSpan timeout,
		CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileSet.DirectoryPath))
            throw new IOException("The store directory path cannot be resolved.");

		cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(fileSet.DirectoryPath);

		var startedTimestamp = Stopwatch.GetTimestamp();
        while (true)
        {
			cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // A short-lived sidecar lock keeps every store on the same
                // read-modify-write contract without introducing platform-specific mutexes.
                var stream = new FileStream(
                    fileSet.LockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return new HeldLock(stream);
            }
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				var elapsed = Stopwatch.GetElapsedTime(startedTimestamp);
				if (elapsed >= timeout)
                    throw;

				var remaining = timeout - elapsed;
				var delay = remaining < RetryDelay ? remaining : RetryDelay;
				if (cancellationToken.WaitHandle.WaitOne(delay))
					cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

	public static async ValueTask<IDisposable> AcquireAsync(
		JsonStoreFileSet fileSet,
		TimeSpan timeout,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(fileSet.DirectoryPath))
			throw new IOException("The store directory path cannot be resolved.");

		Directory.CreateDirectory(fileSet.DirectoryPath);
		var startedTimestamp = Stopwatch.GetTimestamp();
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				return new HeldLock(new FileStream(
					fileSet.LockPath,
					FileMode.OpenOrCreate,
					FileAccess.ReadWrite,
					FileShare.None));
			}
			catch (Exception exception) when (
				exception is IOException or UnauthorizedAccessException)
			{
				var elapsed = Stopwatch.GetElapsedTime(startedTimestamp);
				if (elapsed >= timeout)
					throw;

				var delay = timeout - elapsed;
				if (delay > RetryDelay)
					delay = RetryDelay;
				await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
			}
		}
	}

    private sealed class HeldLock(FileStream stream) : IDisposable
    {
        public void Dispose() => stream.Dispose();
    }
}
