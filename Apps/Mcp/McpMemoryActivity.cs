namespace DevProjex.Mcp;

internal static class McpMemoryActivity
{
	private static readonly object Sync = new();
	private static int active;
	private static bool maintenance;
	private static TaskCompletionSource idle = Completed();

	public static IDisposable Enter()
	{
		lock (Sync)
		{
			while (maintenance) Monitor.Wait(Sync);
			if (active++ == 0) idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
			return new Lease(false);
		}
	}

	public static async Task<IDisposable> EnterMaintenanceAsync(CancellationToken token)
	{
		while (true)
		{
			Task wait;
			lock (Sync)
			{
				token.ThrowIfCancellationRequested();
				if (active == 0 && !maintenance)
				{
					maintenance = true;
					idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
					return new Lease(true);
				}
				wait = idle.Task;
			}
			await wait.WaitAsync(token).ConfigureAwait(false);
		}
	}

	private static TaskCompletionSource Completed()
	{
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		completion.SetResult();
		return completion;
	}

	private sealed class Lease(bool exclusive) : IDisposable
	{
		private int disposed;
		public void Dispose()
		{
			if (Interlocked.Exchange(ref disposed, 1) != 0) return;
			lock (Sync)
			{
				if (exclusive) { maintenance = false; Monitor.PulseAll(Sync); }
				else active--;
				if (active == 0) idle.TrySetResult();
			}
		}
	}
}
