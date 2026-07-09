using Avalonia.Controls;
using Avalonia.Media;
using DevProjex.Avalonia.Services;

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
            enableBackdrop: true,
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
            enableBackdrop: true,
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
            enableBackdrop: true,
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

    [AvaloniaFact]
    public void TryApplyToTopLevel_WithoutEffect_KeepsHostBackgroundUntouched()
    {
        var popupLevel = new Window
        {
            Background = Brushes.Blue
        };
        var host = new Window();

        var applied = PopupBackdropConfigurator.TryApplyToTopLevel(
            popupLevel,
            host,
            enableBackdrop: false,
            PopupBackdropTransparencyFallback.Transparent);

        Assert.True(applied);
        AssertTransparencyHints(popupLevel, WindowTransparencyLevel.None);
        Assert.Same(Brushes.Blue, popupLevel.Background);
    }

    [AvaloniaFact]
    public void TryApplyToTopLevel_WhenPopupIsHost_DoesNothing()
    {
        var host = new Window();

        var applied = PopupBackdropConfigurator.TryApplyToTopLevel(
            host,
            host,
            enableBackdrop: true,
            PopupBackdropTransparencyFallback.Transparent);

        Assert.False(applied);
        Assert.Empty(host.TransparencyLevelHint);
    }

    private static void AssertTransparencyHints(Window window, params WindowTransparencyLevel[] expected)
    {
        Assert.Equal(expected, window.TransparencyLevelHint.ToArray());
    }
}
