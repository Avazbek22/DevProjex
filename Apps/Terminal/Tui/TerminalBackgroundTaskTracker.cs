namespace DevProjex.Terminal.Tui;

internal sealed class TerminalBackgroundTaskTracker
{
	private readonly object _gate = new();
	private readonly HashSet<Task> _tasks = [];
	private TaskCompletionSource? _completion;
	private bool _completionRequested;

	public Task Track(Task task)
	{
		ArgumentNullException.ThrowIfNull(task);

		lock (_gate)
		{
			if (_completionRequested)
				throw new InvalidOperationException("The terminal session is already stopping.");
			if (task.IsCompleted)
			{
				ObserveFailure(task);
				return task;
			}

			_tasks.Add(task);
		}

		_ = task.ContinueWith(
			static (completed, state) =>
				((TerminalBackgroundTaskTracker)state!).OnTaskCompleted(completed),
			this,
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
		return task;
	}

	public Task CompleteAsync()
	{
		lock (_gate)
		{
			_completionRequested = true;
			if (_tasks.Count == 0)
				return Task.CompletedTask;

			_completion ??= new TaskCompletionSource(
				TaskCreationOptions.RunContinuationsAsynchronously);
			return _completion.Task;
		}
	}

	private void OnTaskCompleted(Task task)
	{
		ObserveFailure(task);
		TaskCompletionSource? completion = null;
		lock (_gate)
		{
			_tasks.Remove(task);
			if (_completionRequested && _tasks.Count == 0)
				completion = _completion;
		}

		completion?.TrySetResult();
	}

	private static void ObserveFailure(Task task)
	{
		if (task.IsFaulted)
			_ = task.Exception;
	}
}
