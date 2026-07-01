namespace DevProjex.Avalonia.Services;

internal static class CompositionBackdropCornerRadiusCoordinator
{
    public const float RoundedBackdropCornerRadius = 8f;
    private static Win32PlatformOptions? s_options;

    public static void Attach(Win32PlatformOptions options)
    {
        s_options = options;
    }

    public static void UseSharpCornersForDecoratedWindow()
        => TrySetWin32BackdropCornerRadius(null);

    public static void UseRoundedCornersForPopupSurface()
        => TrySetWin32BackdropCornerRadius(RoundedBackdropCornerRadius);

    private static void TrySetWin32BackdropCornerRadius(float? radius)
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (s_options is not null)
            s_options.WinUICompositionBackdropCornerRadius = radius;
    }
}
