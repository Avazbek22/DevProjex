namespace DevProjex.Avalonia.Services;

internal static class CompositionBackdropCornerRadiusCoordinator
{
    // Do not collapse these values back into one generic "rounded" radius.
    // WinUICompositionBackdropCornerRadius controls the native WinUI blur brush, not an
    // Avalonia visual. XAML overlays, clipped Borders, and background fillers can hide one
    // surface while breaking another, because the real artifact is produced by the native
    // top-level backdrop layer. Each caller below selects the radius that matches the
    // top-level surface Avalonia is about to materialize.
    public const float PopupBackdropCornerRadius = 8f;
    public const float BorderlessDialogBackdropCornerRadius = 12f;
    private static Win32PlatformOptions? s_options;

    public static void Attach(Win32PlatformOptions options)
    {
        s_options = options;
    }

    public static void UseSharpCornersForDecoratedWindow()
        => TrySetWin32BackdropCornerRadius(null);

    public static void UseRoundedCornersForPopupSurface()
        => TrySetWin32BackdropCornerRadius(PopupBackdropCornerRadius);

    public static void UseRoundedCornersForBorderlessDialogSurface()
        => TrySetWin32BackdropCornerRadius(BorderlessDialogBackdropCornerRadius);

    private static void TrySetWin32BackdropCornerRadius(float? radius)
    {
        // This Win32 option is intentionally coordinated in one place. Avalonia uses it for
        // native WinUI composition blur brushes, and that native layer is not clipped by XAML
        // Border.CornerRadius. Mixing the profiles causes two different historical artifacts:
        // sharp main menu holes when decorated windows inherit rounded popup corners, and
        // a second weaker rounded rectangle behind borderless dialogs when their native
        // backdrop radius is smaller than the visible XAML card radius.
        if (!OperatingSystem.IsWindows())
            return;

        if (s_options is not null)
            s_options.WinUICompositionBackdropCornerRadius = radius;
    }
}
