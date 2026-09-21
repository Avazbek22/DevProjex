using System.Diagnostics;
using DevProjex.Application.Selection;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalSelectionProfilePersistenceCoordinator : IDisposable
{
	private static readonly TimeSpan PersistenceDelay = TimeSpan.FromSeconds(2);
	private readonly Func<string, ProjectSelectionProfile, CancellationToken, Task> _persistAsync;
	private readonly Func<CancellationToken, Task> _delayAsync;
	private readonly Func<TimeSpan, CancellationToken, Task> _retryDelayAsync;
	private readonly Action<Exception>? _failureCallback;
	private readonly int _maxBackgroundAttempts;
	private readonly SemaphoreSlim _writeGate = new(1, 1);
	private readonly object _sync = new();
	private PendingWrite? _pending;
	private CancellationTokenSource? _delayCts;
	private Task _activePersistence = Task.CompletedTask;
	private long _version;
	private int _disposed;

	public TerminalSelectionProfilePersistenceCoordinator(
		Func<string, ProjectSelectionProfile, CancellationToken, Task> persistAsync,
		Action<Exception>? failureCallback = null)
		: this(
			persistAsync,
			static cancellationToken => Task.Delay(PersistenceDelay, cancellationToken),
			failureCallback: failureCallback)
	{
	}

	internal TerminalSelectionProfilePersistenceCoordinator(
		Func<string, ProjectSelectionProfile, CancellationToken, Task> persistAsync,
		Func<CancellationToken, Task> delayAsync,
		Func<TimeSpan, CancellationToken, Task>? retryDelayAsync = null,
		Action<Exception>? failureCallback = null,
		int maxBackgroundAttempts = 3)
	{
		_persistAsync = persistAsync ?? throw new ArgumentNullException(nameof(persistAsync));
		_delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
		_retryDelayAsync = retryDelayAsync ?? Task.Delay;
		_failureCallback = failureCallback;
		_maxBackgroundAttempts = Math.Max(1, maxBackgroundAttempts);
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

		lock (_sync)
		{
			if (_disposed == 0 && version == _version)
				_activePersistence = PersistAfterDelayAsync(version, token);
		}
	}

	public async Task<bool> FlushAsync(
		CancellationToken cancellationToken = default,
		bool reportFailure = true)
	{
		while (true)
		{
			Task active;
			lock (_sync)
			{
				_delayCts?.Cancel();
				_delayCts?.Dispose();
				_delayCts = null;
				active = _activePersistence;
			}

			await active.WaitAsync(cancellationToken).ConfigureAwait(false);

			PendingWrite? pending;
			lock (_sync)
				pending = _pending;
			if (pending is null)
				return true;

			var persisted = await PersistVersionAsync(
				pending.Version,
				cancellationToken,
				_maxBackgroundAttempts,
				reportFailure).ConfigureAwait(false);
			if (!persisted)
				return false;
		}
	}

	public void DiscardPending()
	{
		lock (_sync)
		{
			_delayCts?.Cancel();
			_delayCts?.Dispose();
			_delayCts = null;
			_pending = null;
			_version = checked(_version + 1);
		}
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

		await PersistVersionAsync(
			version,
			CancellationToken.None,
			_maxBackgroundAttempts,
			reportFailure: true).ConfigureAwait(false);
	}

	private async Task<bool> PersistVersionAsync(
		long version,
		CancellationToken cancellationToken,
		int attempts,
		bool reportFailure)
	{
		Exception? lastFailure = null;
		for (var attempt = 0; attempt < attempts; attempt++)
		{
			PendingWrite? pending;
			lock (_sync)
			{
				if (_disposed != 0 || _pending?.Version != version)
					return true;
				pending = _pending;
			}

			try
			{
				await PersistAsync(pending, cancellationToken).ConfigureAwait(false);
				lock (_sync)
				{
					if (_pending?.Version == version)
						_pending = null;
				}
				return true;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				lastFailure = exception;
				if (attempt + 1 < attempts)
				{
					await _retryDelayAsync(
						TimeSpan.FromMilliseconds(100 * (attempt + 1)),
						cancellationToken).ConfigureAwait(false);
				}
			}
		}

		if (lastFailure is not null && reportFailure)
		{
			Trace.TraceWarning(
				"Terminal project selection persistence failed: {0}",
				lastFailure.GetType().Name);
			_failureCallback?.Invoke(lastFailure);
		}
		return false;
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
