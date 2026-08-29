namespace DevProjex.Application.Services;

public static class ProjectFileSizeFilter
{
	public static async Task<ProjectContextPlan> ApplyAsync(
		ProjectContextPlanner planner,
		ProjectContextPlan plan,
		long? maximumFileBytes,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(planner);
		ArgumentNullException.ThrowIfNull(plan);
		if (maximumFileBytes is null)
			return plan;
		if (maximumFileBytes <= 0)
			throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));

		var selected = new List<string>(plan.IncludedFiles.Count);
		var excludedFiles = 0;
		long excludedBytes = 0;
		foreach (var path in plan.IncludedFiles)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var size = ResolveSize(plan.EffectiveFileSizes, path);
			if (size <= maximumFileBytes.Value)
			{
				selected.Add(PathUtility.GetPortableRelativePath(plan.SourceRoot, path));
				continue;
			}

			excludedFiles++;
			excludedBytes = SaturatingAdd(excludedBytes, size);
		}

		var summary = new FileSizeFilterSummary(
			maximumFileBytes.Value,
			excludedFiles,
			excludedBytes);
		if (excludedFiles == 0)
			return plan with { FileSizeFilter = summary };

		var narrowed = selected.Count > 0
			? await planner.ReprojectSelectionAsync(plan, selected, cancellationToken).ConfigureAwait(false)
			: await planner.ReprojectEmptySelectionAsync(plan, cancellationToken).ConfigureAwait(false);
		return narrowed with
		{
			Selection = plan.Selection,
			FileSizeFilter = summary
		};
	}

	private static long ResolveSize(
		IReadOnlyDictionary<string, long>? effectiveFileSizes,
		string path)
	{
		if (effectiveFileSizes is not null &&
		    effectiveFileSizes.TryGetValue(path, out var knownSize))
		{
			return Math.Max(0, knownSize);
		}

		try
		{
			return Math.Max(0, new FileInfo(path).Length);
		}
		catch
		{
			// An unreadable size cannot safely prove that the file exceeds the cap.
			return 0;
		}
	}

	private static long SaturatingAdd(long left, long right) =>
		left > long.MaxValue - right ? long.MaxValue : left + right;
}

public sealed record FileSizeFilterSummary(
	long MaximumFileBytes,
	int ExcludedFiles,
	long ExcludedBytes);
