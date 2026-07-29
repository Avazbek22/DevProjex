using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Tui;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspacePresentationPolicyTests
{
	[Theory]
	[InlineData(TerminalColorMode.Auto, false, false, false)]
	[InlineData(TerminalColorMode.Auto, false, true, true)]
	[InlineData(TerminalColorMode.Always, false, true, false)]
	[InlineData(TerminalColorMode.Never, false, false, true)]
	[InlineData(TerminalColorMode.Always, true, false, true)]
	public void Resolve_UsesExpectedMonochromePolicy(
		TerminalColorMode color,
		bool plain,
		bool noColor,
		bool expectedMonochrome)
	{
		var environment = new TestTerminalEnvironment
		{
			IsNoColor = noColor
		};

		var result = TerminalWorkspacePresentationPolicy.Resolve(
			color,
			plain,
			environment);

		Assert.Equal(expectedMonochrome, result.UseMonochromeScheme);
		Assert.Equal(
			expectedMonochrome
				? TerminalWorkspacePresentationPolicy.MonochromeSchemeName
				: null,
			result.SchemeName);
		Assert.Equal(plain ? LineStyle.None : LineStyle.Single, result.BorderStyle);
		Assert.Equal(!plain, result.AllowMotion);
	}

	[Fact]
	public void PlainTextNormalizationRemovesTerminalDecorations()
	{
		var value = TerminalPlainText.Normalize("↑↓ ←/→ Action · Value… — ready");

		Assert.Equal("j/k h/l Action | Value... - ready", value);
		Assert.DoesNotContain(value, static character =>
			"↑↓←→·…—".Contains(character));
	}

	[Fact]
	public void PlainOverlayButtonsHaveNoTerminalDecorations()
	{
		var button = new Button { Text = "Apply" };

		TerminalWorkspacePresentationPolicy.ConfigureOverlayButton(
			button,
			plain: true);

		Assert.True(button.NoDecorations);
		Assert.True(button.NoPadding);
		Assert.Equal(ShadowStyles.None, button.ShadowStyle);
	}

	[Fact]
	public void PlainPreviewDoesNotCreateUnicodeScrollBars()
	{
		using var view = new TerminalVirtualizedPreviewView(
			useUnicode: false,
			showScrollBars: false);

		Assert.False(view.ViewportSettings.HasFlag(
			ViewportSettingsFlags.HasScrollBars));
	}
}
