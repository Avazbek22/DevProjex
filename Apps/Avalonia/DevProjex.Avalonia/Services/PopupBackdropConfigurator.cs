namespace DevProjex.Avalonia.Services;

using ThemeEffectMode = DevProjex.Infrastructure.ThemePresets.ThemeEffectMode;

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

    private static readonly WindowTransparencyLevel[] TransparentHints =
    [
        WindowTransparencyLevel.Transparent,
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
        ThemeEffectMode effect,
        PopupBackdropTransparencyFallback fallback)
    {
        if (hostedControl is null)
            return false;

        return TopLevel.GetTopLevel(hostedControl) is TopLevel popupLevel &&
               TryApplyToTopLevel(popupLevel, host, effect, fallback);
    }

    public static bool TryApplyToTopLevel(
        TopLevel popupLevel,
        TopLevel? host,
        ThemeEffectMode effect,
        PopupBackdropTransparencyFallback fallback)
    {
        if (host is not null && ReferenceEquals(popupLevel, host))
            return false;

        try
        {
            if (effect != ThemeEffectMode.Solid)
            {
                // Menu/popover surfaces intentionally use the smaller popup radius.
                // Borderless dialogs have a separate profile because their outer card is 12px.
                CompositionBackdropCornerRadiusCoordinator.UseRoundedCornersForPopupSurface();
            }

            popupLevel.TransparencyLevelHint = ResolveEffectHints(effect, fallback);

            if (effect != ThemeEffectMode.Solid)
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
        ThemeEffectMode effect,
        PopupBackdropTransparencyFallback fallback)
    {
        return effect switch
        {
            ThemeEffectMode.Solid => NoEffectHints,
            ThemeEffectMode.Transparent => TransparentHints,
            _ => fallback == PopupBackdropTransparencyFallback.Transparent
                ? EffectHintsWithTransparentFallback
                : EffectHints
        };
    }
}
