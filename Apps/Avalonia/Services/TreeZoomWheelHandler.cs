namespace DevProjex.Avalonia.Services;

internal static class TreeZoomWheelHandler
{
    public static bool TryGetZoomStep(KeyModifiers modifiers, Vector delta, bool pointerOverTree, out double step)
    {
        step = 0;

        if (!pointerOverTree)
            return false;

        if (!modifiers.HasFlag(KeyModifiers.Control) && !modifiers.HasFlag(KeyModifiers.Meta))
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
