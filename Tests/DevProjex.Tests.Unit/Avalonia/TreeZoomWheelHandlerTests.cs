using Avalonia.Input;
using DevProjex.Avalonia.Services;
using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class TreeZoomWheelHandlerTests
{
    [Fact]
    public void TryGetZoomStep_ReturnsFalse_WhenPointerNotOverTree()
    {
        var handled = TreeZoomWheelHandler.TryGetZoomStep(
            KeyModifiers.Control,
            new Vector(0, 1),
            pointerOverTree: false,
            out var step);

        Assert.False(handled);
        Assert.Equal(0, step);
    }

    [Fact]
    public void TryGetZoomStep_ReturnsFalse_WhenNoModifiers()
    {
        var handled = TreeZoomWheelHandler.TryGetZoomStep(
            KeyModifiers.None,
            new Vector(0, 1),
            pointerOverTree: true,
            out var step);

        Assert.False(handled);
        Assert.Equal(0, step);
    }

    [Fact]
    public void TryGetZoomStep_ReturnsPositiveStep_ForCtrlWheelUp()
    {
        var handled = TreeZoomWheelHandler.TryGetZoomStep(
            KeyModifiers.Control,
            new Vector(0, 1),
            pointerOverTree: true,
            out var step);

        Assert.True(handled);
        Assert.Equal(1, step);
    }

    [Fact]
	public void TryGetZoomStep_ReturnsNegativeStep_ForMacOSMetaWheelDown()
    {
        var handled = TreeZoomWheelHandler.TryGetZoomStep(
            KeyModifiers.Meta,
            new Vector(0, -1),
            pointerOverTree: true,
			out var step,
			new DesktopShortcutModifiers(DesktopPlatform.MacOS));

        Assert.True(handled);
        Assert.Equal(-1, step);
    }

	[Theory]
	[InlineData(DesktopPlatform.Windows)]
	[InlineData(DesktopPlatform.Linux)]
	public void TryGetZoomStep_RejectsMetaOutsideMacOS(DesktopPlatform platform)
	{
		var handled = TreeZoomWheelHandler.TryGetZoomStep(
			KeyModifiers.Meta,
			new Vector(0, -1),
			pointerOverTree: true,
			out var step,
			new DesktopShortcutModifiers(platform));

		Assert.False(handled);
		Assert.Equal(0, step);
	}

    [Fact]
    public void TryGetZoomStep_ReturnsFalse_WhenDeltaIsZero()
    {
        var handled = TreeZoomWheelHandler.TryGetZoomStep(
            KeyModifiers.Control,
            new Vector(0, 0),
            pointerOverTree: true,
            out var step);

        Assert.False(handled);
        Assert.Equal(0, step);
    }
}
