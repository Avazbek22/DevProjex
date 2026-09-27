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

	// Terminal.Gui keeps dialog buttons on one row and clips whatever does not fit.
	// Narrow terminals drop shadows and padding first, and brackets only when still needed.
	internal static void FitOverlayButtonRow(IReadOnlyList<Button> buttons, int availableColumns)
	{
		if (MeasureButtonRow(buttons) <= availableColumns)
			return;
		foreach (var button in buttons)
		{
			button.ShadowStyle = ShadowStyles.None;
			button.NoPadding = true;
		}
		if (MeasureButtonRow(buttons) <= availableColumns)
			return;
		foreach (var button in buttons)
			button.NoDecorations = true;
	}

	internal static int MeasureButtonRow(IReadOnlyList<Button> buttons) =>
		buttons.Sum(MeasureButton) + Math.Max(0, buttons.Count - 1);

	private static int MeasureButton(Button button)
	{
		var textColumns = TerminalCellWidth.Measure(button.Text);
		if (button.NoDecorations)
			return textColumns;
		var interiorColumns = button.IsDefault
			? textColumns + 4
			: button.NoPadding ? textColumns : textColumns + 2;
		var shadowColumns = button.ShadowStyle == ShadowStyles.None ? 0 : 1;
		return interiorColumns + 2 + shadowColumns;
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
