using System.Runtime.InteropServices;
using DevProjex.Terminal.CommandLine;

CaptureDpiAwareness.EnablePerMonitorV2();

using var cancellation = TerminalCancellationCoordinator.Register();
var environment = new InvocationEnvironment(hasAttachedConsole: true);
return await new TerminalApplication(environment)
    .RunAsync(args, cancellation.Token);

internal static class CaptureDpiAwareness
{
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    internal static void EnablePerMonitorV2()
    {
        if (OperatingSystem.IsWindows())
            SetProcessDpiAwarenessContext(PerMonitorAwareV2);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
