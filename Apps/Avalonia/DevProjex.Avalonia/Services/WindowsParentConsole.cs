using System.Runtime.InteropServices;

namespace DevProjex.Avalonia.Services;

internal static class WindowsParentConsole
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    public static bool AttachForCommandLine()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            if (Console.IsOutputRedirected || Console.IsErrorRedirected)
                return false;

            if (!AttachConsole(AttachParentProcess))
                return false;

            ResetStandardWriters();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ResetStandardWriters()
    {
        // WinExe processes start without Console.Out/Console.Error bound to the
        // invoking terminal. Reopening the streams after AttachConsole makes
        // utility modes behave like a normal command-line tool.
        var output = new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding)
        {
            AutoFlush = true
        };
        var error = new StreamWriter(Console.OpenStandardError(), Console.OutputEncoding)
        {
            AutoFlush = true
        };

        Console.SetOut(output);
        Console.SetError(error);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);
}
