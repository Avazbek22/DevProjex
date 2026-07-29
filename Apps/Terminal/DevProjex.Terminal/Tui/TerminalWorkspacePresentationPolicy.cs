using DevProjex.Terminal.CommandLine;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

public sealed record TerminalWorkspacePresentation(
	bool UseMonochromeScheme,
	string? SchemeName,
	LineStyle BorderStyle,
	bool AllowMotion);

public static class TerminalWorkspacePresentationPolicy
{
	public const string MonochromeSchemeName = "DevProjexMonochrome";

	public static TerminalWorkspacePresentation Resolve(
		TerminalColorMode requestedColor,
		bool plain,
		ITerminalEnvironment environment)
	{
		var useMonochrome = plain ||
		                    requestedColor == TerminalColorMode.Never ||
		                    requestedColor == TerminalColorMode.Auto && environment.IsNoColor;
		return new TerminalWorkspacePresentation(
			useMonochrome,
			useMonochrome ? MonochromeSchemeName : null,
			plain ? LineStyle.None : LineStyle.Single,
			AllowMotion: !plain);
	}

	internal static void ConfigureOverlayButton(Button button, bool plain)
	{
		if (!plain)
			return;

		button.NoDecorations = true;
		button.NoPadding = true;
		button.ShadowStyle = ShadowStyles.None;
	}
}

internal static class TerminalPlainText
{
	public static string Normalize(string value) =>
		value
			.Replace("↑↓", "j/k", StringComparison.Ordinal)
			.Replace("←/→", "h/l", StringComparison.Ordinal)
			.Replace('↑', 'k')
			.Replace('↓', 'j')
			.Replace('←', 'h')
			.Replace('→', 'l')
			.Replace(" · ", " | ", StringComparison.Ordinal)
			.Replace("…", "...", StringComparison.Ordinal)
			.Replace('—', '-')
			.Replace('–', '-');
}
