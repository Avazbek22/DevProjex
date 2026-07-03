using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DevProjex.Avalonia.Services;

[SupportedOSPlatform("windows")]
internal sealed class WindowsTaskbarProgressNative : IWindowsTaskbarProgressNative
{
    private ITaskbarList3? _taskbarList;

    public WindowsTaskbarProgressNative()
    {
        _taskbarList = (ITaskbarList3)(object)new CTaskbarList();
        var hr = _taskbarList.HrInit();
        if (hr < 0)
        {
            Dispose();
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    public bool TrySetProgressState(nint windowHandle, WindowsTaskbarProgressState state)
        => TryInvoke(taskbar => taskbar.SetProgressState(windowHandle, (TaskbarProgressBarState)state));

    public bool TrySetProgressValue(nint windowHandle, ulong completed, ulong total)
        => TryInvoke(taskbar => taskbar.SetProgressValue(windowHandle, completed, total));

    private bool TryInvoke(Func<ITaskbarList3, int> operation)
    {
        var taskbar = _taskbarList;
        if (taskbar is null)
            return false;

        try
        {
            return operation(taskbar) >= 0;
        }
        catch (COMException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        var taskbar = _taskbarList;
        _taskbarList = null;

        if (taskbar is not null && Marshal.IsComObject(taskbar))
            Marshal.FinalReleaseComObject(taskbar);
    }

    private enum TaskbarProgressBarState
    {
        NoProgress = 0,
        Indeterminate = 1,
        Normal = 2,
        Error = 4,
        Paused = 8
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class CTaskbarList
    {
    }

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig]
        int HrInit();

        [PreserveSig]
        int AddTab(nint hwnd);

        [PreserveSig]
        int DeleteTab(nint hwnd);

        [PreserveSig]
        int ActivateTab(nint hwnd);

        [PreserveSig]
        int SetActiveAlt(nint hwnd);

        [PreserveSig]
        int MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        [PreserveSig]
        int SetProgressValue(nint hwnd, ulong completed, ulong total);

        [PreserveSig]
        int SetProgressState(nint hwnd, TaskbarProgressBarState flags);

        // Methods below keep the COM vtable layout correct. DevProjex does not use thumbnail
        // buttons or tab previews, so pointer parameters are intentionally opaque.
        [PreserveSig]
        int RegisterTab(nint tabHwnd, nint mdiHwnd);

        [PreserveSig]
        int UnregisterTab(nint tabHwnd);

        [PreserveSig]
        int SetTabOrder(nint tabHwnd, nint insertBeforeHwnd);

        [PreserveSig]
        int SetTabActive(nint tabHwnd, nint mdiHwnd, uint reserved);

        [PreserveSig]
        int ThumbBarAddButtons(nint hwnd, uint buttonCount, nint buttons);

        [PreserveSig]
        int ThumbBarUpdateButtons(nint hwnd, uint buttonCount, nint buttons);

        [PreserveSig]
        int ThumbBarSetImageList(nint hwnd, nint imageList);

        [PreserveSig]
        int SetOverlayIcon(nint hwnd, nint icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);

        [PreserveSig]
        int SetThumbnailTooltip(nint hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? tooltip);

        [PreserveSig]
        int SetThumbnailClip(nint hwnd, nint rect);
    }
}
