using System.Reflection;
using System.Runtime.InteropServices;

namespace DevProjex.Terminal.Tui;

internal sealed class WindowsTerminalInputShutdownGuard : IDisposable
{
	private const int StandardInputHandle = -10;
	private static readonly nint InvalidHandle = new(-1);
	private static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromMilliseconds(10);

	private readonly Action cancelPendingRead;
	private readonly TimeSpan retryInterval;
	private readonly bool enabled;
	private readonly object lifetimeGate = new();
	private CancellationTokenSource? cancellationSource;
	private Task? wakeTask;
	private bool armed;
	private bool disposed;

	public WindowsTerminalInputShutdownGuard()
		: this(CancelPendingRead, DefaultRetryInterval, OperatingSystem.IsWindows())
	{
	}

	internal WindowsTerminalInputShutdownGuard(
		Action cancelPendingRead,
		TimeSpan retryInterval,
		bool enabled = true)
	{
		this.cancelPendingRead = cancelPendingRead ??
			throw new ArgumentNullException(nameof(cancelPendingRead));
		if (retryInterval <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(retryInterval));
		this.retryInterval = retryInterval;
		this.enabled = enabled;
	}

	public void Arm()
	{
		if (!enabled)
			return;

		lock (lifetimeGate)
		{
			if (disposed || armed)
				return;

			var source = new CancellationTokenSource();
			try
			{
				wakeTask = Task.Factory.StartNew(
					() => WakeUntilDisposed(cancelPendingRead, source.Token),
					CancellationToken.None,
					TaskCreationOptions.LongRunning,
					TaskScheduler.Default);
				cancellationSource = source;
				armed = true;
			}
			catch
			{
				source.Dispose();
				throw;
			}
		}
	}

	public void Dispose()
	{
		CancellationTokenSource? source;
		Task? worker;
		lock (lifetimeGate)
		{
			if (disposed)
				return;

			disposed = true;
			source = cancellationSource;
			worker = wakeTask;
			cancellationSource = null;
			wakeTask = null;
		}

		if (source is null)
			return;

		source.Cancel();
		try
		{
			worker?.GetAwaiter().GetResult();
		}
		finally
		{
			source.Dispose();
		}
	}

	private void WakeUntilDisposed(
		Action cancelPendingRead,
		CancellationToken cancellationToken)
	{
		do
		{
			try
			{
				cancelPendingRead();
			}
			catch
			{
				// Terminal restoration must continue even if a console handle disappears.
			}
		}
		while (!cancellationToken.WaitHandle.WaitOne(retryInterval));
	}

	private static void CancelPendingRead()
	{
		var inputHandle = ResolveTerminalGuiInputHandle();
		if (inputHandle == nint.Zero || inputHandle == InvalidHandle)
			inputHandle = GetStdHandle(StandardInputHandle);
		if (inputHandle == nint.Zero || inputHandle == InvalidHandle)
			return;

		_ = CancelIoEx(inputHandle, nint.Zero);
	}

	private static nint ResolveTerminalGuiInputHandle()
	{
		try
		{
			// Redirected Windows-subsystem hosts make Terminal.Gui open CONIN$;
			// canceling STDIN would target a different handle and leave ReadFile blocked.
			var terminalDeviceTypeName = string.Join(
				'.',
				"Terminal",
				"Gui",
				"Drivers",
				"TerminalDevice");
			var terminalDevice = typeof(global::Terminal.Gui.App.IApplication)
				.Assembly
				.GetType(terminalDeviceTypeName, throwOnError: false);
			var inputHandle = terminalDevice?.GetProperty(
				"InputHandle",
				BindingFlags.Public | BindingFlags.Static);
			return inputHandle?.GetValue(null) is nint handle
				? handle
				: nint.Zero;
		}
		catch
		{
			return nint.Zero;
		}
	}

	[DllImport("kernel32.dll")]
	private static extern nint GetStdHandle(int standardHandle);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CancelIoEx(nint handle, nint overlapped);
}
