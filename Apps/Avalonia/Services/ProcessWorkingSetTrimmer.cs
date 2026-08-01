using System.Runtime.InteropServices;

namespace DevProjex.Avalonia.Services;

internal static class ProcessWorkingSetTrimmer
{
    public static void TrimCurrentProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            using var process = Process.GetCurrentProcess();
            _ = SetProcessWorkingSetSize(
                process.Handle,
                new IntPtr(-1),
                new IntPtr(-1));
        }
        catch (Exception)
        {
            // Working-set trimming is an optional optimization and may be denied in a sandbox.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(
        IntPtr process,
        IntPtr minimumWorkingSetSize,
        IntPtr maximumWorkingSetSize);
}
