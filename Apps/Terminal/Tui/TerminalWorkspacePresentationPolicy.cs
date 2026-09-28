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

// Terminal.Gui draws every frame from process-wide line glyphs. A terminal without Unicode
// gets ASCII frames while the workspace runs; the previous glyphs are restored afterwards.
internal sealed class TerminalAsciiLineGlyphs : IDisposable
{
	private static readonly (Func<Rune> Get, Action<Rune> Set, char Ascii)[] LineGlyphs =
	[
		(static () => Glyphs.HLine, static value => Glyphs.HLine = value, '-'),
		(static () => Glyphs.VLine, static value => Glyphs.VLine = value, '|'),
		(static () => Glyphs.ULCorner, static value => Glyphs.ULCorner = value, '+'),
		(static () => Glyphs.URCorner, static value => Glyphs.URCorner = value, '+'),
		(static () => Glyphs.LLCorner, static value => Glyphs.LLCorner = value, '+'),
		(static () => Glyphs.LRCorner, static value => Glyphs.LRCorner = value, '+'),
		(static () => Glyphs.LeftTee, static value => Glyphs.LeftTee = value, '+'),
		(static () => Glyphs.RightTee, static value => Glyphs.RightTee = value, '+'),
		(static () => Glyphs.TopTee, static value => Glyphs.TopTee = value, '+'),
		(static () => Glyphs.BottomTee, static value => Glyphs.BottomTee = value, '+'),
		(static () => Glyphs.Cross, static value => Glyphs.Cross = value, '+')
	];

	private readonly Rune[] _previous;

	public TerminalAsciiLineGlyphs()
	{
		_previous = LineGlyphs.Select(static glyph => glyph.Get()).ToArray();
		foreach (var glyph in LineGlyphs)
			glyph.Set(new Rune(glyph.Ascii));
	}

	public void Dispose()
	{
		for (var index = 0; index < LineGlyphs.Length; index++)
			LineGlyphs[index].Set(_previous[index]);
	}
}

internal static class TerminalPlainText
{
	// Arrow glyphs name the arrow keys themselves. Spelling the key out keeps every hint
	// truthful; substituting letter keys would advertise bindings that do not exist everywhere.
	public static string Normalize(string value) =>
		value
			.Replace("↑↓", "Up/Down", StringComparison.Ordinal)
			.Replace("↑", "Up", StringComparison.Ordinal)
			.Replace("↓", "Down", StringComparison.Ordinal)
			.Replace("←", "Left", StringComparison.Ordinal)
			.Replace("→", "Right", StringComparison.Ordinal)
			.Replace(" · ", " | ", StringComparison.Ordinal)
			.Replace("…", "...", StringComparison.Ordinal)
			.Replace('—', '-')
			.Replace('–', '-');
}
