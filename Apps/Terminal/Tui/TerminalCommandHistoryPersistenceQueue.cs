namespace DevProjex.Terminal.Tui;

internal sealed class TerminalCommandHistoryPersistenceQueue(
	Func<IReadOnlyList<string>, CancellationToken, Task> saveAsync)
{
	private readonly object _gate = new();
	private IReadOnlyList<string>? _pendingHistory;
	private bool _workerRunning;

	public Task? Enqueue(IReadOnlyList<string> history)
	{
		ArgumentNullException.ThrowIfNull(history);

		lock (_gate)
		{
			_pendingHistory = history;
			if (_workerRunning)
				return null;

			_workerRunning = true;
			return DrainAsync();
		}
	}

	private async Task DrainAsync()
	{
		Exception? unexpectedFailure = null;
		while (true)
		{
			IReadOnlyList<string> history;
			lock (_gate)
			{
				history = _pendingHistory!;
				_pendingHistory = null;
			}

			try
			{
				await saveAsync(history, CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// Command execution remains usable when per-user history cannot be persisted.
			}
			catch (Exception exception)
			{
				unexpectedFailure ??= exception;
			}

			lock (_gate)
			{
				if (_pendingHistory is not null)
					continue;

				_workerRunning = false;
				break;
			}
		}

		if (unexpectedFailure is not null)
			System.Runtime.ExceptionServices.ExceptionDispatchInfo
				.Capture(unexpectedFailure)
				.Throw();
	}
}
