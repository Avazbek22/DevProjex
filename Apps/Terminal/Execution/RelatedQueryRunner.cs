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
		return ResolveSelectedFile(plan, seedPath, fullPath);
	}

	private static string ResolveSelectedFile(ProjectContextPlan plan, string seedPath, string fullPath)
	{
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

	public static RelatedSeedSelection ResolveSeedSelection(ProjectContextPlan plan, string seedPath)
	{
		SelectedPathExistenceValidator.Validate(plan.SourceRoot, [seedPath]);
		var relative = ProjectSelectionPath.NormalizeRelative(seedPath);
		var directory = Path.GetFullPath(Path.Combine(plan.SourceRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
		var direct = DependencySeedFolders.DirectFiles(directory, plan.IncludedFiles);
		return direct.Count is > 0 and <= 16
			? new(direct.Select(path => PathUtility.GetPortableRelativePath(plan.SourceRoot, path)).ToArray(), ExpandedFolder: true)
			: new([ResolveSelectedFile(plan, seedPath, directory)], ExpandedFolder: false);
	}

	public static IReadOnlyList<string> ResolveSeeds(ProjectContextPlan plan, string seedPath) =>
		ResolveSeedSelection(plan, seedPath).Files;

	public static string FormatFolderNotice(int fileCount) =>
		$"[Read as] 1 folder(s) were replaced by the {fileCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} file(s) directly inside them as seeds.";

	internal sealed record RelatedSeedSelection(IReadOnlyList<string> Files, bool ExpandedFolder);

	public static Task<DependencyRelatedResult> FindAsync(
		DependencyFactsEngine engine,
		ProjectContextPlan plan,
		string seed,
		DependencyDirection direction,
		int depth,
		IReadOnlyCollection<string> readableProjectFiles,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(engine);
		if (depth is < MinimumDepth or > MaximumDepth)
			throw new ArgumentOutOfRangeException(nameof(depth));
		return FindAsync(engine, plan, ResolveSeeds(plan, seed), direction, depth, readableProjectFiles, cancellationToken);
	}

	public static async Task<DependencyRelatedResult> FindAsync(
		DependencyFactsEngine engine,
		ProjectContextPlan plan,
		IReadOnlyList<string> seeds,
		DependencyDirection direction,
		int depth,
		IReadOnlyCollection<string> readableProjectFiles,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(engine);
		if (depth is < MinimumDepth or > MaximumDepth)
			throw new ArgumentOutOfRangeException(nameof(depth));

		return await engine.FindRelatedAsync(
				plan.SourceRoot,
				plan.IncludedFiles,
				seeds,
				direction,
				depth,
				cancellationToken: cancellationToken,
				readableProjectFiles: readableProjectFiles)
			.ConfigureAwait(false);
	}
}
