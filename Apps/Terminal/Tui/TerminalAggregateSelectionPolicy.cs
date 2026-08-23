namespace DevProjex.Terminal.Tui;

internal static class TerminalAggregateSelectionPolicy
{
	public static (GitFilteringMode Mode, IReadOnlyCollection<ProjectExclusion> Exclusions)
		ResolveExclusions(
			bool enabled,
			GitFilteringMode currentMode,
			GitFilteringMode preferredMode)
	{
		if (!enabled)
			return (GitFilteringMode.None, []);

		var mode = currentMode == GitFilteringMode.None ? preferredMode : currentMode;
		var exclusions = ProjectPresentationCatalog.Exclusions
			.Select(static descriptor => descriptor.RequireId())
			.ToArray();
		return (mode, exclusions);
	}
}
