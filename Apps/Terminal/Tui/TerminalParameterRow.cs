using Terminal.Gui.Text;

namespace DevProjex.Terminal.Tui;

internal enum TerminalParameterRowKind
{
	Information,
	GitMode,
	ContentTransformation,
	ToggleAllExclusions,
	Exclusion,
	ToggleAllExtensions,
	Extension
}

internal sealed record TerminalParameterRow(
	string Key,
	TerminalParameterRowKind Kind,
	string Label,
	bool? IsSelected = null,
	GitFilteringMode? GitMode = null,
	ProjectExclusion? Exclusion = null,
	IgnoreOptionId? ContentTransformation = null,
	string? Value = null)
{
	public override string ToString()
	{
		if (Kind == TerminalParameterRowKind.Information)
			return Label;
		var marker = IsSelected == true ? "[x]" : "[ ]";
		return $"{marker} {Label}";
	}

	internal static string FitLabel(string value, int width, bool useUnicode)
	{
		if (string.IsNullOrEmpty(value) || width <= 0)
			return string.Empty;
		if (value.GetColumns() <= width)
			return value;

		var suffix = useUnicode ? "…" : "...";
		var counterStart = value.LastIndexOf(" (", StringComparison.Ordinal);
		if (counterStart > 0 && value.EndsWith(')'))
		{
			var counter = value[counterStart..];
			var prefixWidth = width - counter.GetColumns();
			if (prefixWidth > suffix.GetColumns())
				return FitLabel(value[..counterStart], prefixWidth, useUnicode) + counter;
		}
		if (width <= suffix.GetColumns())
			return new string('.', width);
		var remaining = width - suffix.GetColumns();
		var builder = new StringBuilder();
		foreach (var rune in value.EnumerateRunes())
		{
			var columns = Math.Max(0, rune.GetColumns());
			if (columns > remaining)
				break;
			builder.Append(rune);
			remaining -= columns;
		}
		return builder.Append(suffix).ToString();
	}
}
