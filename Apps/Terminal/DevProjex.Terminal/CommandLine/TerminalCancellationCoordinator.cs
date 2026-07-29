using System.Runtime.InteropServices;

namespace DevProjex.Terminal.CommandLine;

public sealed class TerminalCancellationCoordinator : IDisposable
{
	private readonly CancellationTokenSource _cancellationSource = new();
	private readonly InterruptPolicy _interruptPolicy = new();
	private readonly List<PosixSignalRegistration> _signalRegistrations = [];
	private bool _disposed;

	private TerminalCancellationCoordinator()
	{
		if (OperatingSystem.IsWindows())
		{
			Console.CancelKeyPress += HandleConsoleCancel;
		}
		else
		{
			Register(PosixSignal.SIGINT);
			Register(PosixSignal.SIGTERM);
			Register(PosixSignal.SIGHUP);
		}
	}

	public CancellationToken Token => _cancellationSource.Token;

	public static TerminalCancellationCoordinator Register() => new();

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		if (OperatingSystem.IsWindows())
			Console.CancelKeyPress -= HandleConsoleCancel;
		foreach (var registration in _signalRegistrations)
			registration.Dispose();
		_signalRegistrations.Clear();
		_cancellationSource.Dispose();
	}

	private void HandleConsoleCancel(object? sender, ConsoleCancelEventArgs eventArgs)
	{
		eventArgs.Cancel = RequestCancellation();
	}

	private void Register(PosixSignal signal)
	{
		_signalRegistrations.Add(PosixSignalRegistration.Create(
			signal,
			context => context.Cancel = RequestCancellation()));
	}

	private bool RequestCancellation()
	{
		if (!_interruptPolicy.TryBeginCancellation())
			return false;
		try
		{
			_cancellationSource.Cancel();
		}
		catch (ObjectDisposedException)
		{
			return false;
		}
		return true;
	}
}

internal sealed class InterruptPolicy
{
	private int _interruptCount;

	public bool TryBeginCancellation() =>
		Interlocked.Increment(ref _interruptCount) == 1;
}
