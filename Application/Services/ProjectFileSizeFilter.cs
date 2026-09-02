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
		AppendPreexistingEmptyDirectories(plan, selected, cancellationToken);

		ProjectContextPlan narrowed;
		if (selected.Count == 0)
		{
			narrowed = await planner
				.ReprojectEmptySelectionAsync(plan, cancellationToken)
				.ConfigureAwait(false);
		}
		else if (GitScopeSelection.IsMomentary(plan.Selection.GitMode ?? GitFilteringMode.None))
		{
			narrowed = await planner
				.ReprojectSelectionAsync(
					plan,
					selected,
					StringComparer.Ordinal,
					cancellationToken)
				.ConfigureAwait(false);
		}
		else
		{
			narrowed = await planner
				.ReprojectSelectionAsync(plan, selected, cancellationToken)
				.ConfigureAwait(false);
		}
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

	private static void AppendPreexistingEmptyDirectories(
		ProjectContextPlan plan,
		List<string> selected,
		CancellationToken cancellationToken)
	{
		var emptyDirectories = new List<string>();
		var stack = new Stack<TreeNodeDescriptor>();
		for (var index = plan.ProjectedTree.Children.Count - 1; index >= 0; index--)
			stack.Push(plan.ProjectedTree.Children[index]);

		while (stack.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var node = stack.Pop();
			if (!node.IsDirectory)
				continue;
			if (node.Children.Count == 0)
			{
				emptyDirectories.Add(node.FullPath);
				continue;
			}

			for (var index = node.Children.Count - 1; index >= 0; index--)
				stack.Push(node.Children[index]);
		}

		CancellationAwareSort.Sort(
			emptyDirectories,
			ProjectTreePathIdentity.CanonicalComparer,
			cancellationToken);
		foreach (var path in emptyDirectories)
		{
			cancellationToken.ThrowIfCancellationRequested();
			selected.Add(PathUtility.GetPortableRelativePath(plan.SourceRoot, path));
		}
	}

	private static long SaturatingAdd(long left, long right) =>
		left > long.MaxValue - right ? long.MaxValue : left + right;
}

public sealed record FileSizeFilterSummary(
	long MaximumFileBytes,
	int ExcludedFiles,
	long ExcludedBytes);
