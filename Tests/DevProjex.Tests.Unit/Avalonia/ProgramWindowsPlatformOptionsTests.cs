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
    public void CompositionBackdropCornerRadiusCoordinator_SwitchesOnlyOnWindows()
    {
        var options = new Win32PlatformOptions
        {
            WinUICompositionBackdropCornerRadius = 4f
        };

        DevProjex.Avalonia.Services.CompositionBackdropCornerRadiusCoordinator.Attach(options);
        DevProjex.Avalonia.Services.CompositionBackdropCornerRadiusCoordinator.UseSharpCornersForDecoratedWindow();

        if (OperatingSystem.IsWindows())
        {
            Assert.Null(options.WinUICompositionBackdropCornerRadius);

            DevProjex.Avalonia.Services.CompositionBackdropCornerRadiusCoordinator.UseRoundedCornersForPopupSurface();
            Assert.Equal(
                DevProjex.Avalonia.Services.CompositionBackdropCornerRadiusCoordinator.RoundedBackdropCornerRadius,
                options.WinUICompositionBackdropCornerRadius);
        }
        else
        {
            Assert.Equal(4f, options.WinUICompositionBackdropCornerRadius);
        }
    }
}
