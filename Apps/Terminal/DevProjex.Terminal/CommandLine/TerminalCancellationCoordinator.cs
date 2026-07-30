using System.Runtime.InteropServices;

namespace DevProjex.Terminal.CommandLine;

public sealed class TerminalCancellationCoordinator : IDisposable
{
	private readonly CancellationTokenSource _cancellationSource = new();
	private readonly InterruptPolicy _interruptPolicy = new();
	private readonly List<PosixSignalRegistration> _signalRegistrations = [];
	private readonly object _lifetimeGate = new();
	private readonly bool _consoleHandlerRegistered;
	private int _activeCallbacks;
	private bool _cancellationSourceDisposed;
	private bool _disposed;

	internal TerminalCancellationCoordinator(bool registerSystemHandlers)
	{
		if (!registerSystemHandlers)
			return;
		if (OperatingSystem.IsWindows())
		{
			Console.CancelKeyPress += HandleConsoleCancel;
			_consoleHandlerRegistered = true;
		}
		else
		{
			Register(PosixSignal.SIGINT);
			Register(PosixSignal.SIGTERM);
			Register(PosixSignal.SIGHUP);
		}
	}

	public CancellationToken Token => _cancellationSource.Token;

	public static TerminalCancellationCoordinator Register() => new(registerSystemHandlers: true);

	public void Dispose()
	{
		var disposeCancellationSource = false;
		lock (_lifetimeGate)
		{
			if (_disposed)
				return;
			_disposed = true;
			if (_activeCallbacks == 0)
			{
				_cancellationSourceDisposed = true;
				disposeCancellationSource = true;
			}
		}

		if (_consoleHandlerRegistered)
			Console.CancelKeyPress -= HandleConsoleCancel;
		foreach (var registration in _signalRegistrations)
			registration.Dispose();
		_signalRegistrations.Clear();
		if (disposeCancellationSource)
			_cancellationSource.Dispose();
	}

	private void HandleConsoleCancel(object? sender, ConsoleCancelEventArgs eventArgs)
	{
		TryHandleInterrupt(() => eventArgs.Cancel = true);
	}

	private void Register(PosixSignal signal)
	{
		_signalRegistrations.Add(PosixSignalRegistration.Create(
			signal,
			context =>
			{
				TryHandleInterrupt(() => context.Cancel = true);
			}));
	}

	internal bool TryHandleInterrupt(Action suppressNativeAction)
	{
		ArgumentNullException.ThrowIfNull(suppressNativeAction);
		if (!TryEnterCallback())
			return false;
		try
		{
			if (!_interruptPolicy.TryBeginCancellation())
				return false;

			// Suppress the native action before synchronous cancellation callbacks run.
			suppressNativeAction();
			_cancellationSource.Cancel();
			return true;
		}
		finally
		{
			ExitCallback();
		}
	}

	private bool TryEnterCallback()
	{
		lock (_lifetimeGate)
		{
			if (_disposed)
				return false;
			_activeCallbacks++;
			return true;
		}
	}

	private void ExitCallback()
	{
		var disposeCancellationSource = false;
		lock (_lifetimeGate)
		{
			_activeCallbacks--;
			if (_disposed &&
			    _activeCallbacks == 0 &&
			    !_cancellationSourceDisposed)
			{
				_cancellationSourceDisposed = true;
				disposeCancellationSource = true;
			}
		}

		if (disposeCancellationSource)
			_cancellationSource.Dispose();
	}
}

internal sealed class InterruptPolicy
{
	private int _interruptCount;

	public bool TryBeginCancellation() =>
		Interlocked.Increment(ref _interruptCount) == 1;
}
