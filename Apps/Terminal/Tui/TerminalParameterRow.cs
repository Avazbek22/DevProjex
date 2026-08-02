namespace DevProjex.Terminal.Tui;

internal enum TerminalParameterRowKind
{
	Section,
	Information,
	GitMode,
	ToggleAllExclusions,
	Exclusion,
	ToggleAllExtensions,
	Extension,
	ToggleAllRoots,
	Root
}

internal sealed record TerminalParameterRow(
	string Key,
	TerminalParameterRowKind Kind,
	string Label,
	bool? IsSelected = null,
	GitFilteringMode? GitMode = null,
	ProjectExclusion? Exclusion = null,
	string? Value = null)
{
	public override string ToString()
	{
		if (Kind == TerminalParameterRowKind.Section)
			return $"  {Label.ToUpperInvariant()}";
		if (Kind == TerminalParameterRowKind.Information)
			return $"  {Label}";
		var marker = Kind == TerminalParameterRowKind.GitMode
			? IsSelected == true ? "(*)" : "( )"
			: IsSelected == true ? "[x]" : "[ ]";
		return $"  {marker} {Label}";
	}
}
