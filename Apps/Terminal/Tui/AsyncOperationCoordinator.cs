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

	public AsyncOperationCoordinator(CancellationToken sessionToken) =>
		_sessionToken = sessionToken;

	public CancellationTokenSource Start(WorkspaceOperationKind kind)
	{
		Cancel(kind);
		var source = CancellationTokenSource.CreateLinkedTokenSource(_sessionToken);
		_operations[kind] = new OperationState(source, null);
		return source;
	}

	public void Track(WorkspaceOperationKind kind, CancellationTokenSource source, Task task)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(task);
		if (!IsCurrent(kind, source))
			throw new InvalidOperationException("The operation token is not current.");
		_operations[kind] = new OperationState(source, task);
	}

	public bool IsCurrent(WorkspaceOperationKind kind, CancellationTokenSource source) =>
		_operations.TryGetValue(kind, out var state) && ReferenceEquals(state.Source, source);

	public bool IsRunning(WorkspaceOperationKind kind) =>
		_operations.TryGetValue(kind, out var state) && !state.Source.IsCancellationRequested;

	public Task? GetTask(WorkspaceOperationKind kind) =>
		_operations.TryGetValue(kind, out var state) ? state.Task : null;

	public CancellationTokenSource? GetSource(WorkspaceOperationKind kind) =>
		_operations.TryGetValue(kind, out var state) ? state.Source : null;

	public void AssignSource(WorkspaceOperationKind kind, CancellationTokenSource? source)
	{
		if (source is null)
		{
			_operations.Remove(kind);
			return;
		}
		var task = _operations.TryGetValue(kind, out var current) ? current.Task : null;
		_operations[kind] = new OperationState(source, task);
	}

	public void AssignTask(WorkspaceOperationKind kind, Task? task)
	{
		if (!_operations.TryGetValue(kind, out var current))
		{
			if (task is not null)
				throw new InvalidOperationException("An operation token must be assigned before its task.");
			return;
		}
		_operations[kind] = current with { Task = task };
	}

	public void Complete(WorkspaceOperationKind kind, CancellationTokenSource source, bool dispose = true)
	{
		if (IsCurrent(kind, source))
			_operations.Remove(kind);
		if (dispose)
			source.Dispose();
	}

	public void Cancel(WorkspaceOperationKind kind)
	{
		if (!_operations.Remove(kind, out var state))
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

	public void Dispose()
	{
		foreach (var kind in _operations.Keys.ToArray())
			Cancel(kind);
	}

	private sealed record OperationState(CancellationTokenSource Source, Task? Task);
}
