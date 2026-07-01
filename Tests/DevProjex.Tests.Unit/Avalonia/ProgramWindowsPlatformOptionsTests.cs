using CompositionBackdropCornerRadiusCoordinator = DevProjex.Avalonia.Services.CompositionBackdropCornerRadiusCoordinator;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class ProgramWindowsPlatformOptionsTests
{
    [Fact]
    public void CreateWin32PlatformOptions_UsesSharpMainWindowBackdropCorners()
    {
        var method = typeof(Program).GetMethod(
            "CreateWin32PlatformOptions",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var options = method!.Invoke(null, null);
        Assert.NotNull(options);

        var radiusProperty = options!.GetType().GetProperty("WinUICompositionBackdropCornerRadius");
        Assert.NotNull(radiusProperty);

        Assert.Null(radiusProperty!.GetValue(options));
    }

    [Fact]
    public void CompositionBackdropCornerRadiusCoordinator_UsesSeparateSurfaceProfilesOnlyOnWindows()
    {
        var options = new Win32PlatformOptions
        {
            WinUICompositionBackdropCornerRadius = 4f
        };

        CompositionBackdropCornerRadiusCoordinator.Attach(options);
        CompositionBackdropCornerRadiusCoordinator.UseSharpCornersForDecoratedWindow();

        if (OperatingSystem.IsWindows())
        {
            Assert.Null(options.WinUICompositionBackdropCornerRadius);

            CompositionBackdropCornerRadiusCoordinator.UseRoundedCornersForPopupSurface();
            Assert.Equal(
                CompositionBackdropCornerRadiusCoordinator.PopupBackdropCornerRadius,
                options.WinUICompositionBackdropCornerRadius);

            CompositionBackdropCornerRadiusCoordinator.UseRoundedCornersForBorderlessDialogSurface();
            Assert.Equal(
                CompositionBackdropCornerRadiusCoordinator.BorderlessDialogBackdropCornerRadius,
                options.WinUICompositionBackdropCornerRadius);
        }
        else
        {
            Assert.Equal(4f, options.WinUICompositionBackdropCornerRadius);
        }
    }
}
