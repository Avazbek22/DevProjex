namespace DevProjex.Infrastructure.FileSystem;

internal sealed class SharedAsyncOperation<T>
{
	private readonly object _sync = new();
	private readonly CancellationTokenSource _cancellation = new();
	private readonly Lazy<Task<T>> _task;
	private readonly Action<SharedAsyncOperation<T>> _releaseOwner;
	private int _waiterCount;
	private bool _acceptingWaiters = true;
	private bool _completed;
	private bool _cancellationPending;
	private bool _releaseNotified;
	private bool _disposed;

	public SharedAsyncOperation(
		Func<CancellationToken, Task<T>> operation,
		Action<SharedAsyncOperation<T>> releaseOwner)
	{
		ArgumentNullException.ThrowIfNull(operation);
		ArgumentNullException.ThrowIfNull(releaseOwner);
		_releaseOwner = releaseOwner;
		_task = new Lazy<Task<T>>(
			() => RunAsync(operation),
			LazyThreadSafetyMode.ExecutionAndPublication);
	}

	public Task<T> Task => _task.Value;

	public bool TryAcquire(out IDisposable lease)
	{
		lock (_sync)
		{
			if (!_acceptingWaiters)
			{
				lease = null!;
				return false;
			}

			_waiterCount++;
			lease = new Lease(this);
			return true;
		}
	}

	public void DisposeUnused()
	{
		lock (_sync)
		{
			if (_waiterCount != 0 || _task.IsValueCreated || _disposed)
				return;

			_acceptingWaiters = false;
			_disposed = true;
		}

		_cancellation.Dispose();
	}

	private async Task<T> RunAsync(Func<CancellationToken, Task<T>> operation)
	{
		try
		{
			return await operation(_cancellation.Token).ConfigureAwait(false);
		}
		finally
		{
			Complete();
		}
	}

	private void Complete()
	{
		var notifyOwner = false;
		var disposeCancellation = false;
		lock (_sync)
		{
			_completed = true;
			_acceptingWaiters = false;
			notifyOwner = MarkOwnerForRelease();
			disposeCancellation = TryMarkCancellationForDisposal();
		}

		if (notifyOwner)
			_releaseOwner(this);
		if (disposeCancellation)
			_cancellation.Dispose();
	}

	private void ReleaseWaiter()
	{
		var cancelOperation = false;
		var notifyOwner = false;
		var disposeCancellation = false;
		lock (_sync)
		{
			_waiterCount--;
			if (_waiterCount < 0)
				throw new InvalidOperationException("A shared operation lease was released more than once.");

			if (_waiterCount == 0)
			{
				_acceptingWaiters = false;
				if (_completed)
				{
					disposeCancellation = TryMarkCancellationForDisposal();
				}
				else
				{
					_cancellationPending = true;
					cancelOperation = true;
					notifyOwner = MarkOwnerForRelease();
				}
			}
		}

		if (notifyOwner)
			_releaseOwner(this);
		if (disposeCancellation)
			_cancellation.Dispose();
		if (!cancelOperation)
			return;

		try
		{
			_cancellation.Cancel();
		}
		finally
		{
			lock (_sync)
			{
				_cancellationPending = false;
				disposeCancellation = TryMarkCancellationForDisposal();
			}

			if (disposeCancellation)
				_cancellation.Dispose();
		}
	}

	private bool MarkOwnerForRelease()
	{
		if (_releaseNotified)
			return false;

		_releaseNotified = true;
		return true;
	}

	private bool TryMarkCancellationForDisposal()
	{
		if (_disposed || !_completed || _waiterCount != 0 || _cancellationPending)
			return false;

		_disposed = true;
		return true;
	}

	private sealed class Lease(SharedAsyncOperation<T> owner) : IDisposable
	{
		private SharedAsyncOperation<T>? _owner = owner;

		public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseWaiter();
	}
}
