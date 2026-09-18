using System.Diagnostics;
using DevProjex.Application.Selection;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalSelectionProfilePersistenceCoordinator : IDisposable
{
	private static readonly TimeSpan PersistenceDelay = TimeSpan.FromSeconds(2);
	private readonly Func<string, ProjectSelectionProfile, CancellationToken, Task> _persistAsync;
	private readonly Func<CancellationToken, Task> _delayAsync;
	private readonly SemaphoreSlim _writeGate = new(1, 1);
	private readonly object _sync = new();
	private PendingWrite? _pending;
	private CancellationTokenSource? _delayCts;
	private long _version;
	private int _disposed;

	public TerminalSelectionProfilePersistenceCoordinator(
		Func<string, ProjectSelectionProfile, CancellationToken, Task> persistAsync)
		: this(
			persistAsync,
			static cancellationToken => Task.Delay(PersistenceDelay, cancellationToken))
	{
	}

	internal TerminalSelectionProfilePersistenceCoordinator(
		Func<string, ProjectSelectionProfile, CancellationToken, Task> persistAsync,
		Func<CancellationToken, Task> delayAsync)
	{
		_persistAsync = persistAsync ?? throw new ArgumentNullException(nameof(persistAsync));
		_delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
	}

	public void Schedule(string projectPath, ProjectSelectionProfile profile)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
		ArgumentNullException.ThrowIfNull(profile);
		if (Volatile.Read(ref _disposed) != 0)
			return;

		CancellationToken token;
		long version;
		lock (_sync)
		{
			if (_disposed != 0)
				return;

			_delayCts?.Cancel();
			_delayCts?.Dispose();
			_delayCts = new CancellationTokenSource();
			token = _delayCts.Token;
			version = checked(++_version);
			_pending = new PendingWrite(
				Path.GetFullPath(projectPath),
				ProjectSelectionProfileBuilder.Clone(profile),
				version);
		}

		_ = PersistAfterDelayAsync(version, token);
	}

	public async Task FlushAsync(CancellationToken cancellationToken = default)
	{
		PendingWrite? pending;
		lock (_sync)
		{
			_delayCts?.Cancel();
			_delayCts?.Dispose();
			_delayCts = null;
			pending = _pending;
			_pending = null;
			_version = checked(_version + 1);
		}

		if (pending is not null)
			await PersistAsync(pending, cancellationToken).ConfigureAwait(false);
	}

	private async Task PersistAfterDelayAsync(long version, CancellationToken cancellationToken)
	{
		try
		{
			await _delayAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return;
		}

		PendingWrite? pending;
		lock (_sync)
		{
			if (_disposed != 0 || version != _version || _pending?.Version != version)
				return;

			pending = _pending;
			_pending = null;
		}

		if (pending is null)
			return;

		try
		{
			await PersistAsync(pending, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			Trace.TraceWarning(
				"Terminal project selection persistence failed: {0}",
				exception.GetType().Name);
		}
	}

	private async Task PersistAsync(PendingWrite pending, CancellationToken cancellationToken)
	{
		await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await _persistAsync(
				pending.ProjectPath,
				pending.Profile,
				cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_writeGate.Release();
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		lock (_sync)
		{
			_delayCts?.Cancel();
			_delayCts?.Dispose();
			_delayCts = null;
			_pending = null;
			_version = checked(_version + 1);
		}
		_writeGate.Dispose();
	}

	private sealed record PendingWrite(
		string ProjectPath,
		ProjectSelectionProfile Profile,
		long Version);
}
