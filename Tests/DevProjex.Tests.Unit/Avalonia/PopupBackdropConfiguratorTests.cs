using Avalonia.Controls;
using Avalonia.Media;
using DevProjex.Avalonia.Services;
using ThemeEffectMode = DevProjex.Infrastructure.ThemePresets.ThemeEffectMode;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class PopupBackdropConfiguratorTests
{
    [AvaloniaFact]
    public void TryApplyToTopLevel_WithTransparentFallback_UsesMenuPopupHintOrder()
    {
        var popupLevel = new Window();
        var host = new Window();

        var applied = PopupBackdropConfigurator.TryApplyToTopLevel(
            popupLevel,
            host,
            ThemeEffectMode.Acrylic,
            PopupBackdropTransparencyFallback.Transparent);

        Assert.True(applied);
        AssertTransparencyHints(
            popupLevel,
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.Blur,
            WindowTransparencyLevel.Transparent,
            WindowTransparencyLevel.None);
        Assert.Same(Brushes.Transparent, popupLevel.Background);
    }

    [AvaloniaFact]
    public void TryApplyToTopLevel_WithoutTransparentFallback_UsesPopoverHintOrder()
    {
        var popupLevel = new Window();
        var host = new Window();

        var applied = PopupBackdropConfigurator.TryApplyToTopLevel(
            popupLevel,
            host,
            ThemeEffectMode.Acrylic,
            PopupBackdropTransparencyFallback.None);

        Assert.True(applied);
        AssertTransparencyHints(
            popupLevel,
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.Blur,
            WindowTransparencyLevel.None);
        Assert.Same(Brushes.Transparent, popupLevel.Background);
    }

    [AvaloniaFact]
    public void TryApplyToTopLevel_WithEffect_UsesPopupBackdropRadiusProfile()
    {
        var options = new Win32PlatformOptions
        {
            WinUICompositionBackdropCornerRadius = CompositionBackdropCornerRadiusCoordinator.BorderlessDialogBackdropCornerRadius
        };
        var popupLevel = new Window();
        var host = new Window();

        CompositionBackdropCornerRadiusCoordinator.Attach(options);

        var applied = PopupBackdropConfigurator.TryApplyToTopLevel(
            popupLevel,
            host,
            ThemeEffectMode.Mica,
            PopupBackdropTransparencyFallback.None);

        Assert.True(applied);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(
                CompositionBackdropCornerRadiusCoordinator.PopupBackdropCornerRadius,
                options.WinUICompositionBackdropCornerRadius);
        }
        else
        {
            Assert.Equal(
                CompositionBackdropCornerRadiusCoordinator.BorderlessDialogBackdropCornerRadius,
                options.WinUICompositionBackdropCornerRadius);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryApplyToTopLevel_Solid_UsesTransparentHostWithoutRequestingBackdrop(
        bool useTransparentFallback)
    {
        var popupLevel = new Window
        {
            Background = Brushes.Blue
        };
        var host = new Window();
        var fallback = useTransparentFallback
            ? PopupBackdropTransparencyFallback.Transparent
            : PopupBackdropTransparencyFallback.None;

        var applied = PopupBackdropConfigurator.TryApplyToTopLevel(
            popupLevel,
            host,
            ThemeEffectMode.Solid,
            fallback);

        Assert.True(applied);
        AssertTransparencyHints(
            popupLevel,
            WindowTransparencyLevel.Transparent,
            WindowTransparencyLevel.None);
        Assert.DoesNotContain(WindowTransparencyLevel.Blur, popupLevel.TransparencyLevelHint);
        Assert.DoesNotContain(WindowTransparencyLevel.AcrylicBlur, popupLevel.TransparencyLevelHint);
        Assert.Same(Brushes.Transparent, popupLevel.Background);
    }

    [AvaloniaFact]
    public void TryApplyToTopLevel_Solid_DoesNotEnableNativeBackdropRadius()
    {
        var expectedRadius = CompositionBackdropCornerRadiusCoordinator.BorderlessDialogBackdropCornerRadius;
        var options = new Win32PlatformOptions
        {
            WinUICompositionBackdropCornerRadius = expectedRadius
        };
        CompositionBackdropCornerRadiusCoordinator.Attach(options);

        var applied = PopupBackdropConfigurator.TryApplyToTopLevel(
            new Window(),
            new Window(),
            ThemeEffectMode.Solid,
            PopupBackdropTransparencyFallback.None);

        Assert.True(applied);
        Assert.Equal(expectedRadius, options.WinUICompositionBackdropCornerRadius);
    }

    [AvaloniaFact]
    public void TryApplyToTopLevel_WhenPopupIsHost_DoesNothing()
    {
        var host = new Window();

        var applied = PopupBackdropConfigurator.TryApplyToTopLevel(
            host,
            host,
            ThemeEffectMode.Acrylic,
            PopupBackdropTransparencyFallback.Transparent);

        Assert.False(applied);
        Assert.Empty(host.TransparencyLevelHint);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryApplyToTopLevel_TransparentWindow_UsesBlurredPopupSurface(bool useTransparentFallback)
    {
        var popupLevel = new Window();
        var fallback = useTransparentFallback
            ? PopupBackdropTransparencyFallback.Transparent
            : PopupBackdropTransparencyFallback.None;

        var applied = PopupBackdropConfigurator.TryApplyToTopLevel(
            popupLevel,
            new Window(),
            ThemeEffectMode.Transparent,
            fallback);

        Assert.True(applied);
        WindowTransparencyLevel[] expected = useTransparentFallback
            ?
            [
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Blur,
                WindowTransparencyLevel.Transparent,
                WindowTransparencyLevel.None
            ]
            :
            [
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Blur,
                WindowTransparencyLevel.None
            ];
        AssertTransparencyHints(popupLevel, expected);
        Assert.Same(Brushes.Transparent, popupLevel.Background);
    }

    private static void AssertTransparencyHints(Window window, params WindowTransparencyLevel[] expected)
    {
        Assert.Equal(expected, window.TransparencyLevelHint.ToArray());
    }
}
