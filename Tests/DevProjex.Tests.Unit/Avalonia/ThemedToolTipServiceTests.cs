using Avalonia.Controls;
using Avalonia.Media;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class ThemedToolTipServiceTests
{
	[AvaloniaFact]
	public void ApplyBackdrop_WithoutSeparatePopupHost_DoesNotMutateWindowSurface()
	{
		var originalHints = new[] { WindowTransparencyLevel.None };
		var toolTip = new ToolTip { Content = "Embedded" };
		var window = new Window
		{
			Content = toolTip,
			Background = Brushes.Red,
			TransparencyLevelHint = originalHints
		};

		try
		{
			window.Show();
			AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);

			Assert.False(ThemedToolTipService.ApplyBackdrop(toolTip));
			Assert.Equal(originalHints, window.TransparencyLevelHint);
			Assert.Same(Brushes.Red, window.Background);
		}
		finally
		{
			window.Close();
		}
	}
}
