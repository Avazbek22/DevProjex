namespace DevProjex.Terminal.Tui;

internal enum WorkspaceOperationKind
{
	Active,
	Projection,
	Preview,
	SettingsRefresh,
	PreviewSearch,
	TransientStatus,
	CommandResult
}

internal sealed class AsyncOperationCoordinator : IDisposable
{
	private readonly Dictionary<WorkspaceOperationKind, OperationState> _operations = [];
	private readonly CancellationToken _sessionToken;
	private readonly object _sync = new();

	public AsyncOperationCoordinator(CancellationToken sessionToken) =>
		_sessionToken = sessionToken;

	public CancellationTokenSource Start(WorkspaceOperationKind kind)
	{
		var source = CancellationTokenSource.CreateLinkedTokenSource(_sessionToken);
		OperationState? previous;
		lock (_sync)
		{
			_operations.Remove(kind, out previous);
			_operations[kind] = new OperationState(source, null);
		}
		CancelAndDispose(previous);
		return source;
	}

	public void Track(WorkspaceOperationKind kind, CancellationTokenSource source, Task task)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(task);
		if (!TryTrack(kind, source, task))
			throw new InvalidOperationException("The operation token is not current.");
	}

	public bool TryTrack(WorkspaceOperationKind kind, CancellationTokenSource source, Task task)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(task);
		lock (_sync)
		{
			if (!_operations.TryGetValue(kind, out var state) ||
				!ReferenceEquals(state.Source, source))
			{
				return false;
			}
			_operations[kind] = state with { Task = task };
			return true;
		}
	}

	public bool TryTrackCurrent(WorkspaceOperationKind kind, Task task)
	{
		ArgumentNullException.ThrowIfNull(task);
		lock (_sync)
		{
			if (!_operations.TryGetValue(kind, out var state))
				return false;
			_operations[kind] = state with { Task = task };
			return true;
		}
	}

	public bool IsCurrent(WorkspaceOperationKind kind, CancellationTokenSource source) =>
		Read(kind, state => ReferenceEquals(state.Source, source), false);

	public bool IsRunning(WorkspaceOperationKind kind) =>
		Read(kind, static state => !state.Source.IsCancellationRequested, false);

	public Task? GetTask(WorkspaceOperationKind kind) =>
		Read<Task?>(kind, static state => state.Task, null);

	public CancellationTokenSource? GetSource(WorkspaceOperationKind kind) =>
		Read<CancellationTokenSource?>(kind, static state => state.Source, null);

	public void Complete(WorkspaceOperationKind kind, CancellationTokenSource source, bool dispose = true)
	{
		lock (_sync)
		{
			if (_operations.TryGetValue(kind, out var state) && ReferenceEquals(state.Source, source))
				_operations.Remove(kind);
		}
		if (dispose)
			source.Dispose();
	}

	public void Cancel(WorkspaceOperationKind kind)
	{
		OperationState? state;
		lock (_sync)
		{
			_operations.Remove(kind, out state);
		}
		CancelAndDispose(state);
	}

	public void Dispose()
	{
		OperationState[] states;
		lock (_sync)
		{
			states = _operations.Values.ToArray();
			_operations.Clear();
		}
		foreach (var state in states)
			CancelAndDispose(state);
	}

	private T Read<T>(WorkspaceOperationKind kind, Func<OperationState, T> selector, T fallback)
	{
		lock (_sync)
			return _operations.TryGetValue(kind, out var state) ? selector(state) : fallback;
	}

	private static void CancelAndDispose(OperationState? state)
	{
		if (state is null)
			return;
		try
		{
			state.Source.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		state.Source.Dispose();
	}

	private sealed record OperationState(CancellationTokenSource Source, Task? Task);
}
