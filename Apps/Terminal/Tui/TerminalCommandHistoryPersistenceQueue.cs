namespace DevProjex.Terminal.Tui;

internal sealed class TerminalCommandHistoryPersistenceQueue(
	Func<IReadOnlyList<string>, AppLanguage?, CancellationToken, Task> saveAsync,
	CancellationToken cancellationToken = default)
{
	private readonly object _gate = new();
	private PendingSettings? _pendingSettings;
	private bool _workerRunning;
	private Task? _drainTask;

	public Task? Enqueue(IReadOnlyList<string> history, AppLanguage? language = null)
	{
		ArgumentNullException.ThrowIfNull(history);

		lock (_gate)
		{
			_pendingSettings = new PendingSettings(
				history,
				language ?? _pendingSettings?.Language);
			if (_workerRunning)
				return null;

			_workerRunning = true;
			_drainTask = DrainAsync();
			return _drainTask;
		}
	}

	public Task CompleteAsync()
	{
		lock (_gate)
			return _drainTask ?? Task.CompletedTask;
	}

	private async Task DrainAsync()
	{
		Exception? unexpectedFailure = null;
		while (true)
		{
			PendingSettings settings;
			lock (_gate)
			{
				settings = _pendingSettings!;
				_pendingSettings = null;
			}

			try
			{
				await saveAsync(settings.History, settings.Language, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
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
				if (_pendingSettings is not null)
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

	private sealed record PendingSettings(
		IReadOnlyList<string> History,
		AppLanguage? Language);
}
