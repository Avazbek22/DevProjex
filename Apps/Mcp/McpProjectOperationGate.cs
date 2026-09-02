namespace DevProjex.Mcp;

internal sealed class McpProjectOperationGate
{
	private readonly SemaphoreSlim _semaphore = new(1, 1);

	public async Task<TResult> RunAsync<TResult>(
		Func<Task<TResult>> operation,
		CancellationToken cancellationToken)
	{
		await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			return await operation().ConfigureAwait(false);
		}
		finally
		{
			_semaphore.Release();
		}
	}
}
