namespace DevProjex.Tests.Unit.Avalonia;

public sealed class ProgramWindowsPlatformOptionsTests
{
    [Fact]
    public void CreateWin32PlatformOptions_ConfiguresCompositionBackdropCornerRadius()
    {
        var method = typeof(Program).GetMethod(
            "CreateWin32PlatformOptions",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var options = method!.Invoke(null, null);
        Assert.NotNull(options);

        var radiusProperty = options!.GetType().GetProperty("WinUICompositionBackdropCornerRadius");
        Assert.NotNull(radiusProperty);

        var radius = Assert.IsType<float>(radiusProperty!.GetValue(options));
        Assert.Equal(8f, radius);
    }
}
