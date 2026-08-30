using DevProjex.Terminal.Rendering;
using Terminal.Gui.Text;

namespace DevProjex.Terminal.Tui;

internal enum TerminalParameterRowKind
{
	GitMode,
	ContentTransformation,
	ToggleAllContent,
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
	bool IsEnabled = true,
	bool UseUnicodeRadioMarker = true,
	GitFilteringMode? GitMode = null,
	ProjectExclusion? Exclusion = null,
	IgnoreOptionId? ContentTransformation = null,
	string? Value = null)
{
	private string? _displayText;

	private TerminalParameterRow(TerminalParameterRow original)
		: base()
	{
		Key = original.Key;
		Kind = original.Kind;
		Label = original.Label;
		IsSelected = original.IsSelected;
		IsEnabled = original.IsEnabled;
		UseUnicodeRadioMarker = original.UseUnicodeRadioMarker;
		GitMode = original.GitMode;
		Exclusion = original.Exclusion;
		ContentTransformation = original.ContentTransformation;
		Value = original.Value;
	}

	public override string ToString()
	{
		if (_displayText is not null)
			return _displayText;
		var marker = Kind == TerminalParameterRowKind.GitMode
			? IsSelected == true
				? UseUnicodeRadioMarker ? "(•)" : "(*)"
				: "( )"
			: IsSelected == true ? "[x]" : "[ ]";
		return _displayText = $"{marker} {Label}";
	}

	public bool Equals(TerminalParameterRow? other) =>
		ReferenceEquals(this, other) ||
		other is not null &&
		Kind == other.Kind &&
		IsSelected == other.IsSelected &&
		IsEnabled == other.IsEnabled &&
		UseUnicodeRadioMarker == other.UseUnicodeRadioMarker &&
		GitMode == other.GitMode &&
		Exclusion == other.Exclusion &&
		ContentTransformation == other.ContentTransformation &&
		string.Equals(Key, other.Key, StringComparison.Ordinal) &&
		string.Equals(Label, other.Label, StringComparison.Ordinal) &&
		string.Equals(Value, other.Value, StringComparison.Ordinal);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(Key, StringComparer.Ordinal);
		hash.Add(Kind);
		hash.Add(Label, StringComparer.Ordinal);
		hash.Add(IsSelected);
		hash.Add(IsEnabled);
		hash.Add(UseUnicodeRadioMarker);
		hash.Add(GitMode);
		hash.Add(Exclusion);
		hash.Add(ContentTransformation);
		hash.Add(Value, StringComparer.Ordinal);
		return hash.ToHashCode();
	}

	internal static string FitLabel(string value, int width, bool useUnicode)
	{
		value = TerminalTextEscaping.EscapeSingleLine(value);
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
