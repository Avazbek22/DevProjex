using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Execution;

internal static class RelatedQueryRunner
{
	public const int MinimumDepth = 1;
	public const int MaximumDepth = 10;

	public static string ResolveSeed(ProjectContextPlan plan, string seedPath)
	{
		SelectedPathExistenceValidator.Validate(plan.SourceRoot, [seedPath]);
		var relative = ProjectSelectionPath.NormalizeRelative(seedPath);
		var fullPath = Path.GetFullPath(Path.Combine(
			plan.SourceRoot,
			relative.Replace('/', Path.DirectorySeparatorChar)));
		var exact = plan.IncludedFiles.FirstOrDefault(candidate => PathComparer.Default.Equals(candidate, fullPath));
		if (exact is null)
		{
			throw new ProjectContextValidationException(
				"DPX-SELECTION-PATH-MISSING",
				"The related seed is outside the effective selection.",
				seedPath);
		}

		return PathUtility.GetPortableRelativePath(plan.SourceRoot, exact);
	}

	public static async Task<DependencyRelatedResult> FindAsync(
		DependencyFactsEngine engine,
		ProjectContextPlan plan,
		string seed,
		DependencyDirection direction,
		int depth,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(engine);
		if (depth is < MinimumDepth or > MaximumDepth)
			throw new ArgumentOutOfRangeException(nameof(depth));

		return await engine.FindRelatedAsync(
				plan.SourceRoot,
				plan.IncludedFiles,
				[seed],
				direction,
				depth,
				cancellationToken: cancellationToken)
			.ConfigureAwait(false);
	}
}
