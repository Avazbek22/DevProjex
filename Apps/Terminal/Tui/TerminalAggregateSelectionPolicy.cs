namespace DevProjex.Terminal.Tui;

internal static class TerminalAggregateSelectionPolicy
{
	public static IReadOnlyCollection<ProjectExclusion> ResolveExclusions(bool enabled)
	{
		if (!enabled)
			return [];

		return ProjectPresentationCatalog.Exclusions
			.Select(static descriptor => descriptor.RequireId())
			.ToArray();
	}
}
