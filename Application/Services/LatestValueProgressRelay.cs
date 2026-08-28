namespace DevProjex.Application.Services;

/// <summary>
/// Coalesces producer progress into at most one pending UI dispatch while retaining the latest value.
/// </summary>
public sealed class LatestValueProgressRelay<T>(
	Action<Action> schedule,
	Action<T> consume) : IProgress<T>, IDisposable
{
	private readonly object _gate = new();
	private TaskCompletionSource? _completion;
	private Exception? _failure;
	private T? _latest;
	private long _generation;
	private bool _accepting = true;
	private bool _dispatchPending;
	private bool _hasLatest;
	private bool _terminated;

	public void Report(T value)
	{
		long generation;
		lock (_gate)
		{
			if (!_accepting || _terminated)
				return;

			_latest = value;
			_hasLatest = true;
			if (_dispatchPending)
				return;

			_dispatchPending = true;
			generation = _generation;
		}

		Schedule(generation);
	}

	public Task CompleteAsync()
	{
		long generation = 0;
		var scheduleDispatch = false;
		Task completionTask;
		lock (_gate)
		{
			if (_completion is not null)
				return _completion.Task;

			_completion = new TaskCompletionSource(
				TaskCreationOptions.RunContinuationsAsynchronously);
			completionTask = _completion.Task;
			_accepting = false;
			if (_terminated)
			{
				CompleteTerminationLocked();
				return completionTask;
			}

			if (_dispatchPending)
				return completionTask;
			if (!_hasLatest)
			{
				TerminateLocked();
				CompleteTerminationLocked();
				return completionTask;
			}

			_dispatchPending = true;
			generation = _generation;
			scheduleDispatch = true;
		}

		if (scheduleDispatch)
			Schedule(generation);
		return completionTask;
	}

	public void Dispose()
	{
		TaskCompletionSource? completion;
		lock (_gate)
		{
			if (_terminated)
				return;

			_accepting = false;
			_hasLatest = false;
			_latest = default;
			TerminateLocked();
			completion = _completion;
		}

		completion?.TrySetCanceled();
	}

	private void Schedule(long generation)
	{
		try
		{
			schedule(() => Drain(generation));
		}
		catch (Exception exception)
		{
			Fail(generation, exception);
		}
	}

	private void Drain(long generation)
	{
		T value;
		lock (_gate)
		{
			if (_terminated || generation != _generation)
				return;
			if (!_hasLatest)
			{
				_dispatchPending = false;
				CompleteIfRequestedLocked();
				return;
			}

			value = _latest!;
			_latest = default;
			_hasLatest = false;
		}

		try
		{
			consume(value);
		}
		catch (Exception exception)
		{
			Fail(generation, exception);
			return;
		}

		var scheduleNext = false;
		lock (_gate)
		{
			if (_terminated || generation != _generation)
				return;
			if (_hasLatest)
			{
				scheduleNext = true;
			}
			else
			{
				_dispatchPending = false;
				CompleteIfRequestedLocked();
			}
		}

		if (scheduleNext)
			Schedule(generation);
	}

	private void CompleteIfRequestedLocked()
	{
		if (_completion is null)
			return;

		TerminateLocked();
		CompleteTerminationLocked();
	}

	private void Fail(long generation, Exception exception)
	{
		TaskCompletionSource? completion;
		lock (_gate)
		{
			if (_terminated || generation != _generation)
				return;

			_failure = exception;
			_accepting = false;
			_hasLatest = false;
			_latest = default;
			TerminateLocked();
			completion = _completion;
		}

		completion?.TrySetException(exception);
	}

	private void TerminateLocked()
	{
		_terminated = true;
		_dispatchPending = false;
		_generation++;
	}

	private void CompleteTerminationLocked()
	{
		if (_completion is null)
			return;
		if (_failure is not null)
			_completion.TrySetException(_failure);
		else
			_completion.TrySetResult();
	}
}
