using Terminal.Gui.Text;

namespace DevProjex.Terminal.Tui;

internal static class TerminalFrameTitle
{
	public static string Normalize(string value) =>
		value.TrimEnd().TrimEnd(':').TrimEnd();

	public static string Fit(string value, int maxColumns, bool useUnicode)
	{
		var normalized = Normalize(value);
		return normalized.GetColumns() <= maxColumns
			? normalized
			: TerminalParameterRow.FitLabel(normalized, maxColumns, useUnicode);
	}
}
