namespace DevProjex.Avalonia.Services;

internal static class TreeZoomWheelHandler
{
    public static bool TryGetZoomStep(
        KeyModifiers modifiers,
        Vector delta,
        bool pointerOverTree,
        out double step,
        DesktopShortcutModifiers? shortcutModifiers = null)
    {
        step = 0;

        if (!pointerOverTree)
            return false;

        if (!(shortcutModifiers ?? DesktopShortcutModifiers.Current).IsPrimary(modifiers))
            return false;

        if (delta.Y > 0)
        {
            step = 1;
            return true;
        }

        if (delta.Y < 0)
        {
            step = -1;
            return true;
        }

        return false;
    }
}
