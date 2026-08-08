using System.Reflection;
using System.Runtime.InteropServices;

namespace DevProjex.Terminal.Tui;

internal sealed class WindowsTerminalInputShutdownGuard : IDisposable
{
	private const int StandardInputHandle = -10;
	private static readonly nint InvalidHandle = new(-1);
	private static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromMilliseconds(10);

	private readonly Func<Action?> createCancellation;
	private readonly TimeSpan retryInterval;
	private readonly bool enabled;
	private CancellationTokenSource? cancellationSource;
	private Task? wakeTask;
	private int armed;

	public WindowsTerminalInputShutdownGuard()
		: this(CreateNativeCancellation, DefaultRetryInterval, OperatingSystem.IsWindows())
	{
	}

	internal WindowsTerminalInputShutdownGuard(
		Action cancelPendingRead,
		TimeSpan retryInterval,
		bool enabled = true)
		: this(() => cancelPendingRead, retryInterval, enabled)
	{
		ArgumentNullException.ThrowIfNull(cancelPendingRead);
	}

	private WindowsTerminalInputShutdownGuard(
		Func<Action?> createCancellation,
		TimeSpan retryInterval,
		bool enabled)
	{
		this.createCancellation = createCancellation;
		if (retryInterval <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(retryInterval));
		this.retryInterval = retryInterval;
		this.enabled = enabled;
	}

	public void Arm()
	{
		if (!enabled || Interlocked.Exchange(ref armed, 1) != 0)
			return;
		var cancelPendingRead = createCancellation();
		if (cancelPendingRead is null)
			return;

		var source = new CancellationTokenSource();
		cancellationSource = source;
		wakeTask = Task.Factory.StartNew(
			() => WakeUntilDisposed(cancelPendingRead, source.Token),
			CancellationToken.None,
			TaskCreationOptions.LongRunning,
			TaskScheduler.Default);
	}

	public void Dispose()
	{
		var source = Interlocked.Exchange(ref cancellationSource, null);
		if (source is null)
			return;

		source.Cancel();
		try
		{
			wakeTask?.GetAwaiter().GetResult();
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

	private static Action? CreateNativeCancellation()
	{
		var inputHandle = ResolveTerminalGuiInputHandle();
		if (inputHandle == nint.Zero || inputHandle == InvalidHandle)
			inputHandle = GetStdHandle(StandardInputHandle);
		if (inputHandle == nint.Zero || inputHandle == InvalidHandle)
			return null;

		return () => _ = CancelIoEx(inputHandle, nint.Zero);
	}

	private static nint ResolveTerminalGuiInputHandle()
	{
		try
		{
			// Redirected Windows-subsystem hosts make Terminal.Gui open CONIN$;
			// canceling STDIN would target a different handle and leave ReadFile blocked.
			var terminalDevice = typeof(global::Terminal.Gui.App.IApplication)
				.Assembly
				.GetType("Terminal.Gui.Drivers.TerminalDevice", throwOnError: false);
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
