namespace DevProjex.Application.Context;

public static class ProjectSelectionTokens
{
	public static IReadOnlyList<string> GitModes { get; } =
		ProjectPresentationCatalog.GitFiltering
			.OrderBy(static descriptor => descriptor.Order)
			.Select(static descriptor => descriptor.Token)
			.ToArray();

	public static IReadOnlyList<string> Exclusions { get; } =
		ProjectPresentationCatalog.Exclusions
			.OrderBy(static descriptor => descriptor.Order)
			.Select(static descriptor => descriptor.Token)
			.ToArray();

	public static bool TryParseGitMode(string? value, out GitFilteringMode mode)
	{
		var descriptor = ProjectPresentationCatalog.GitFiltering.FirstOrDefault(
			item => string.Equals(item.Token, value, StringComparison.OrdinalIgnoreCase));
		mode = descriptor?.Id ?? (GitFilteringMode)(-1);
		return descriptor is not null;
	}

	public static string ToToken(GitFilteringMode mode) =>
		ProjectPresentationCatalog.Get(mode).Token;

	public static bool TryParseExclusion(string? value, out ProjectExclusion exclusion)
	{
		var descriptor = ProjectPresentationCatalog.LegacyExclusionChoices.FirstOrDefault(
			item => string.Equals(item.Token, value, StringComparison.OrdinalIgnoreCase));
		exclusion = descriptor?.Id ?? (ProjectExclusion)(-1);
		return descriptor is not null;
	}

	public static string ToToken(ProjectExclusion exclusion) =>
		ProjectPresentationCatalog.Get(exclusion).Token;

	public static IReadOnlyList<ProjectExclusion> OrderExclusions(
		IEnumerable<ProjectExclusion> exclusions)
	{
		ArgumentNullException.ThrowIfNull(exclusions);
		return exclusions
			.Where(static exclusion => exclusion != ProjectExclusion.HideSecrets)
			.Distinct()
			.OrderBy(static exclusion => ProjectPresentationCatalog.Get(exclusion).Order)
			.ToArray();
	}
}
