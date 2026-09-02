namespace DevProjex.Application.Compression;

/// <summary>
/// Bounds retained work by bytes without blocking worker threads. Requests are granted strictly
/// in arrival order; a large head request is never bypassed by smaller requests behind it.
/// </summary>
internal sealed class WeightedByteBudget : IDisposable
{
	private readonly object _sync = new();
	private readonly long _maximumBytes;
	private long _availableBytes;
	private Waiter? _head;
	private Waiter? _tail;
	private int _pendingWaiters;
	private bool _disposed;

	public WeightedByteBudget(long maximumBytes)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
		_maximumBytes = maximumBytes;
		_availableBytes = maximumBytes;
	}

	internal WeightedByteBudgetDiagnostics Diagnostics
	{
		get
		{
			lock (_sync)
			{
				return new WeightedByteBudgetDiagnostics(
					_maximumBytes,
					_availableBytes,
					_maximumBytes - _availableBytes,
					_pendingWaiters);
			}
		}
	}

	public ValueTask<Lease> AcquireAsync(long bytes, CancellationToken cancellationToken = default)
	{
		if (cancellationToken.IsCancellationRequested)
			return ValueTask.FromCanceled<Lease>(cancellationToken);

		var requestedBytes = Math.Clamp(bytes, 1, _maximumBytes);
		Waiter waiter;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_head is null && requestedBytes <= _availableBytes)
			{
				_availableBytes -= requestedBytes;
				return ValueTask.FromResult(new Lease(this, requestedBytes));
			}

			waiter = new Waiter(this, requestedBytes, cancellationToken);
			EnqueueLocked(waiter);
		}

		var registration = cancellationToken.CanBeCanceled
			? cancellationToken.UnsafeRegister(
				static state => ((Waiter)state!).Owner.Cancel((Waiter)state!),
				waiter)
			: default;
		AttachRegistration(waiter, registration);
		return new ValueTask<Lease>(waiter.Completion.Task);
	}

	public void Dispose()
	{
		Waiter? completions = null;
		Waiter? completionTail = null;
		lock (_sync)
		{
			if (_disposed)
				return;
			_disposed = true;
			while (_head is { } waiter)
			{
				RemoveLocked(waiter);
				waiter.State = WaiterState.Disposed;
				waiter.CompletionRegistration = DetachRegistration(waiter);
				AppendCompletion(ref completions, ref completionTail, waiter);
			}
		}

		var exception = new ObjectDisposedException(nameof(WeightedByteBudget));
		while (completions is { } waiter)
		{
			completions = waiter.CompletionNext;
			waiter.CompletionNext = null;
			waiter.CompletionRegistration.Dispose();
			waiter.Completion.TrySetException(exception);
		}
	}

	private void AttachRegistration(Waiter waiter, CancellationTokenRegistration registration)
	{
		var disposeRegistration = false;
		lock (_sync)
		{
			if (waiter.State == WaiterState.Pending)
			{
				waiter.Registration = registration;
				waiter.HasRegistration = registration.Token.CanBeCanceled;
			}
			else
			{
				disposeRegistration = registration.Token.CanBeCanceled;
			}
		}

		if (disposeRegistration)
			registration.Dispose();
	}

	private void Cancel(Waiter waiter)
	{
		Waiter? grants;
		CancellationTokenRegistration registration;
		lock (_sync)
		{
			if (waiter.State != WaiterState.Pending)
				return;
			waiter.State = WaiterState.Canceled;
			RemoveLocked(waiter);
			registration = DetachRegistration(waiter);
			grants = DrainWaitersLocked();
		}

		waiter.Completion.TrySetCanceled(waiter.CancellationToken);
		registration.Dispose();
		CompleteGrants(grants);
	}

	private void Release(long bytes)
	{
		Waiter? grants = null;
		lock (_sync)
		{
			var availableBytes = checked(_availableBytes + bytes);
			if (availableBytes > _maximumBytes)
				throw new InvalidOperationException("The weighted byte budget was released beyond its capacity.");
			_availableBytes = availableBytes;
			if (!_disposed)
				grants = DrainWaitersLocked();
		}

		CompleteGrants(grants);
	}

	private Waiter? DrainWaitersLocked()
	{
		Waiter? completions = null;
		Waiter? completionTail = null;
		while (_head is { } waiter && waiter.RequestedBytes <= _availableBytes)
		{
			RemoveLocked(waiter);
			waiter.State = WaiterState.Granted;
			_availableBytes -= waiter.RequestedBytes;
			waiter.GrantedLease = new Lease(this, waiter.RequestedBytes);
			waiter.CompletionRegistration = DetachRegistration(waiter);
			AppendCompletion(ref completions, ref completionTail, waiter);
		}
		return completions;
	}

	private void EnqueueLocked(Waiter waiter)
	{
		waiter.QueuePrevious = _tail;
		if (_tail is null)
			_head = waiter;
		else
			_tail.QueueNext = waiter;
		_tail = waiter;
		_pendingWaiters++;
	}

	private void RemoveLocked(Waiter waiter)
	{
		if (waiter.QueuePrevious is { } previous)
			previous.QueueNext = waiter.QueueNext;
		else
			_head = waiter.QueueNext;
		if (waiter.QueueNext is { } next)
			next.QueuePrevious = waiter.QueuePrevious;
		else
			_tail = waiter.QueuePrevious;
		waiter.QueuePrevious = null;
		waiter.QueueNext = null;
		_pendingWaiters--;
	}

	private static CancellationTokenRegistration DetachRegistration(Waiter waiter)
	{
		if (!waiter.HasRegistration)
			return default;
		waiter.HasRegistration = false;
		return waiter.Registration;
	}

	private static void AppendCompletion(
		ref Waiter? head,
		ref Waiter? tail,
		Waiter waiter)
	{
		if (tail is null)
			head = waiter;
		else
			tail.CompletionNext = waiter;
		tail = waiter;
	}

	private static void CompleteGrants(Waiter? grants)
	{
		while (grants is { } waiter)
		{
			grants = waiter.CompletionNext;
			waiter.CompletionNext = null;
			waiter.CompletionRegistration.Dispose();
			waiter.Completion.TrySetResult(waiter.GrantedLease!);
		}
	}

	public sealed class Lease : IDisposable
	{
		private WeightedByteBudget? _owner;
		private readonly long _bytes;

		internal Lease(WeightedByteBudget owner, long bytes)
		{
			_owner = owner;
			_bytes = bytes;
		}

		public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_bytes);
	}

	private sealed class Waiter(
		WeightedByteBudget owner,
		long requestedBytes,
		CancellationToken cancellationToken)
	{
		public WeightedByteBudget Owner { get; } = owner;
		public long RequestedBytes { get; } = requestedBytes;
		public CancellationToken CancellationToken { get; } = cancellationToken;
		public TaskCompletionSource<Lease> Completion { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public Waiter? QueuePrevious { get; set; }
		public Waiter? QueueNext { get; set; }
		public Waiter? CompletionNext { get; set; }
		public Lease? GrantedLease { get; set; }
		public CancellationTokenRegistration Registration { get; set; }
		public CancellationTokenRegistration CompletionRegistration { get; set; }
		public WaiterState State { get; set; }
		public bool HasRegistration { get; set; }
	}

	private enum WaiterState
	{
		Pending,
		Granted,
		Canceled,
		Disposed
	}
}

internal readonly record struct WeightedByteBudgetDiagnostics(
	long MaximumBytes,
	long AvailableBytes,
	long IssuedBytes,
	int PendingWaiters);
