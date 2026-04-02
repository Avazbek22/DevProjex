namespace DevProjex.Infrastructure.Persistence;

internal static class CrossProcessFileLock
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    public static IDisposable Acquire(JsonStoreFileSet fileSet)
        => Acquire(fileSet, DefaultTimeout);

    public static bool TryAcquire(JsonStoreFileSet fileSet, out IDisposable? heldLock)
    {
        try
        {
            heldLock = Acquire(fileSet);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            heldLock = null;
            return false;
        }
    }

    public static IDisposable Acquire(JsonStoreFileSet fileSet, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(fileSet.DirectoryPath))
            throw new IOException("The store directory path cannot be resolved.");

        Directory.CreateDirectory(fileSet.DirectoryPath);

        var startedUtc = DateTime.UtcNow;
        while (true)
        {
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
                if (DateTime.UtcNow - startedUtc >= timeout)
                    throw;

                Thread.Sleep(RetryDelay);
            }
        }
    }

    private sealed class HeldLock(FileStream stream) : IDisposable
    {
        public void Dispose() => stream.Dispose();
    }
}
