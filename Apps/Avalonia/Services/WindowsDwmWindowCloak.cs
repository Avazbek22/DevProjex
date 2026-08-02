using System.Runtime.InteropServices;

namespace DevProjex.Avalonia.Services;

internal static class WindowsDwmWindowCloak
{
    private const int DwmWindowAttributeCloak = 13;

    public static bool TrySet(Window window, bool cloaked)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var platformHandle = window.TryGetPlatformHandle();
        if (platformHandle is null ||
            platformHandle.Handle == IntPtr.Zero ||
            !string.Equals(platformHandle.HandleDescriptor, "HWND", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var value = cloaked ? 1 : 0;
            return DwmSetWindowAttribute(
                       platformHandle.Handle,
                       DwmWindowAttributeCloak,
                       ref value,
                       sizeof(int)) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    // DWMWA_CLOAK keeps the HWND composed while withholding it from presentation. This is
    // the Windows primitive that lets Avalonia finish its first DirectComposition/Skia frame
    // without exposing an uninitialized transparent client surface.
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
