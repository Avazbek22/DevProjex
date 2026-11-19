using Terminal.Gui.Text;

namespace DevProjex.Terminal.Tui;

/// <summary>
/// Shares a mini-panel's top row between its frame title and the "All" aggregate control.
/// The title has priority: the aggregate first drops its count, then its word, keeping only
/// the marker, before the title would be shortened.
/// </summary>
internal static class TerminalAggregateFrameLayout
{
	/// <summary>Border columns kept to the right of an aggregate drawn on the frame.</summary>
	internal const int TrailingBorderColumns = 3;

	// The controls panel border on both sides of a mini-panel.
	private const int PanelBorderColumns = 2;

	// The corner and both title caps ("┌┤" and "├") left of the aggregate.
	private const int TitleChromeColumns = 3;

	public static int ResolveAvailableColumns(int panelWidth, bool isOnBorder) =>
		isOnBorder
			? panelWidth - PanelBorderColumns - TitleChromeColumns - TrailingBorderColumns
			: panelWidth;

	public static string FitAggregateLabel(
		string title,
		IReadOnlyList<string> labelsByPreference,
		int panelWidth,
		bool isOnBorder)
	{
		ArgumentNullException.ThrowIfNull(title);
		ArgumentNullException.ThrowIfNull(labelsByPreference);
		if (labelsByPreference.Count == 0)
			throw new ArgumentException("At least one label is required.", nameof(labelsByPreference));
		var available = ResolveAvailableColumns(panelWidth, isOnBorder) -
			TerminalFrameTitle.Normalize(title).GetColumns();
		foreach (var label in labelsByPreference)
		{
			if (TerminalAggregateControl.MeasureText(isOnBorder, label) <= available)
				return label;
		}
		return labelsByPreference[^1];
	}
}
