using Avalonia.Controls;
using DevProjex.Avalonia.Views;
using CompositionBackdropCornerRadiusCoordinator = DevProjex.Avalonia.Services.CompositionBackdropCornerRadiusCoordinator;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class GitCloneWindowBackdropTests
{
    [AvaloniaFact]
    public void GitCloneWindow_AlignsNativeBackdropRadiusWithVisibleCardRadius()
    {
        var options = new Win32PlatformOptions
        {
            WinUICompositionBackdropCornerRadius = 4f
        };

        CompositionBackdropCornerRadiusCoordinator.Attach(options);
        var window = new GitCloneWindow();

        try
        {
            var card = Assert.IsType<Border>(window.FindControl<Border>("CloneWindowCard"));
            var expectedRadius = new CornerRadius(
                CompositionBackdropCornerRadiusCoordinator.BorderlessDialogBackdropCornerRadius);

            Assert.Equal(WindowDecorations.None, window.WindowDecorations);
            Assert.Equal(expectedRadius, card.CornerRadius);
            Assert.True(card.ClipToBounds);
            AssertTransparencyHints(
                window,
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Blur,
                WindowTransparencyLevel.None);

            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    CompositionBackdropCornerRadiusCoordinator.BorderlessDialogBackdropCornerRadius,
                    options.WinUICompositionBackdropCornerRadius);
            }
            else
            {
                Assert.Equal(4f, options.WinUICompositionBackdropCornerRadius);
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertTransparencyHints(Window window, params WindowTransparencyLevel[] expected)
    {
        Assert.Equal(expected, window.TransparencyLevelHint.ToArray());
    }
}
