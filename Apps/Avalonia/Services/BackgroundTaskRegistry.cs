namespace DevProjex.Avalonia.Services;

internal sealed class BackgroundTaskRegistry : IDisposable
{
	private readonly object _gate = new();
	private readonly HashSet<Task> _tasks = [];
	private readonly CancellationTokenSource _lifetime;
	private readonly Action<string, Exception> _reportFailure;
	private int _disposed;

	public BackgroundTaskRegistry(
		CancellationToken lifetimeToken = default,
		Action<string, Exception>? reportFailure = null)
	{
		_lifetime = lifetimeToken.CanBeCanceled
			? CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken)
			: new CancellationTokenSource();
		_reportFailure = reportFailure ?? (static (operationName, exception) =>
			Trace.TraceError("Background task '{0}' failed: {1}", operationName, exception));
	}

	public CancellationToken LifetimeToken => _lifetime.Token;

	internal int TrackedTaskCount
	{
		get
		{
			lock (_gate)
				return _tasks.Count;
		}
	}

	public void Register(Task task, string operationName)
	{
		ArgumentNullException.ThrowIfNull(task);
		ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

		lock (_gate)
			_tasks.Add(task);

		_ = ObserveAsync(task, operationName);
	}

	private async Task ObserveAsync(Task task, string operationName)
	{
		try
		{
			await task.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (task.IsCanceled || _lifetime.IsCancellationRequested)
		{
			// Superseded work and window shutdown are expected cancellation boundaries.
		}
		catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested)
		{
			// A coordinator can dispose its linked cancellation source during shutdown.
		}
		catch (Exception exception)
		{
			try
			{
				_reportFailure(operationName, exception);
			}
			catch (Exception reportingException)
			{
				Trace.TraceError(
					"Reporting background task '{0}' failed: {1}",
					operationName,
					reportingException);
			}
		}
		finally
		{
			lock (_gate)
				_tasks.Remove(task);
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		try
		{
			_lifetime.Cancel();
		}
		finally
		{
			_lifetime.Dispose();
		}
	}
}
