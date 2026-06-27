namespace DevProjex.Avalonia.Services;

internal enum PopupBackdropTransparencyFallback
{
    None,
    Transparent
}

internal static class PopupBackdropConfigurator
{
    private static readonly WindowTransparencyLevel[] NoEffectHints =
    [
        WindowTransparencyLevel.None
    ];

    private static readonly WindowTransparencyLevel[] EffectHints =
    [
        WindowTransparencyLevel.AcrylicBlur,
        WindowTransparencyLevel.Blur,
        WindowTransparencyLevel.None
    ];

    private static readonly WindowTransparencyLevel[] EffectHintsWithTransparentFallback =
    [
        WindowTransparencyLevel.AcrylicBlur,
        WindowTransparencyLevel.Blur,
        WindowTransparencyLevel.Transparent,
        WindowTransparencyLevel.None
    ];

    public static bool TryApply(
        Control? hostedControl,
        TopLevel? host,
        bool enableBackdrop,
        PopupBackdropTransparencyFallback fallback)
    {
        if (hostedControl is null)
            return false;

        return TopLevel.GetTopLevel(hostedControl) is TopLevel popupLevel &&
               TryApplyToTopLevel(popupLevel, host, enableBackdrop, fallback);
    }

    public static bool TryApplyToTopLevel(
        TopLevel popupLevel,
        TopLevel? host,
        bool enableBackdrop,
        PopupBackdropTransparencyFallback fallback)
    {
        if (host is not null && ReferenceEquals(popupLevel, host))
            return false;

        try
        {
            popupLevel.TransparencyLevelHint = enableBackdrop
                ? ResolveEffectHints(fallback)
                : NoEffectHints;

            if (enableBackdrop)
                popupLevel.Background = Brushes.Transparent;

            return true;
        }
        catch
        {
            // Popup/tooltip hosts can close while their opened event is still being processed.
            return false;
        }
    }

    private static IReadOnlyList<WindowTransparencyLevel> ResolveEffectHints(
        PopupBackdropTransparencyFallback fallback)
    {
        return fallback == PopupBackdropTransparencyFallback.Transparent
            ? EffectHintsWithTransparentFallback
            : EffectHints;
    }
}
