namespace DevProjex.Tests.Mcp;

internal sealed class ProgressDelivery
{
	private readonly object sync = new();
	private readonly List<float> values = [];
	private TaskCompletionSource changed = NewSignal();

	public void Report(float value)
	{
		TaskCompletionSource preceding;
		lock (sync)
		{
			values.Add(value);
			preceding = changed;
			changed = NewSignal();
		}
		preceding.TrySetResult();
	}

	public async Task<float[]> WaitForCountAsync(int count, CancellationToken cancellationToken)
	{
		while (true)
		{
			Task signal;
			lock (sync)
			{
				if (values.Count >= count)
					return values.ToArray();
				signal = changed.Task;
			}
			await signal.WaitAsync(cancellationToken);
		}
	}

	private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
