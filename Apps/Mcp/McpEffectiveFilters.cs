using System.Globalization;

namespace DevProjex.Mcp;

internal readonly record struct McpSelectionNoticeContext(
	bool HasPaths,
	bool HasPatterns);

/// <summary>
/// Trusted, path-free descriptions of the filters that shaped a selection. They let an agent
/// tell "this file does not exist" from "this server hides it" without naming a hidden path.
/// </summary>
internal static class McpEffectiveFilters
{
	public const string StartupFlags = "--exclude, --unrestricted, --allow-agent-exclusions";

	private const string PatternSelectionEmptyNotice =
		"[Empty selection] No file passed the effective filters and the request arguments. " +
		"Patterns match the whole project-relative path: '*' stays inside one segment, '**/' spans any depth; " +
		"paths the filters hide never match.";
	private const string PathSelectionEmptyNotice =
		"[Empty selection] None of the requested paths is in the effective selection; paths the filters hide never match.";
	private const string ProjectSelectionEmptyNotice =
		"[Empty selection] The effective filters leave no file in this project.";

	public static string Describe(ProjectContextPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);
		var description =
			$"git: {ProjectSelectionTokens.ToToken(plan.Selection)}; exclusions: {DescribeExclusions(plan.Selection.Exclusions)}";
		return plan.FileSizeFilter is null
			? description
			: $"{description}; max_file_bytes: {plan.FileSizeFilter.MaximumFileBytes.ToString(CultureInfo.InvariantCulture)}";
	}

	public static string DescribeExclusions(IEnumerable<ProjectExclusion>? exclusions)
	{
		var tokens = ProjectSelectionTokens
			.OrderExclusions(exclusions ?? [])
			.Select(ProjectSelectionTokens.ToToken)
			.ToArray();
		return tokens.Length == 0 ? "none" : string.Join(", ", tokens);
	}

	/// <summary>
	/// Footer for tree-bearing responses: the agent reads which filters were active next to
	/// the tree they shaped, and learns who can change them.
	/// </summary>
	public static string Notice(ProjectContextPlan plan, bool agentExclusions) =>
		$"[Effective filters] {Describe(plan)}. " + WideningHint(agentExclusions);

	public static string WideningHint(bool agentExclusions) =>
		agentExclusions
			? "Paths the exclusions hide stay absent until a call passes exclusions; Git filtering is set on the server startup line."
			: $"Paths they hide are absent from every tool; only the server startup line widens them ({StartupFlags}).";

	/// <summary>
	/// The footer plus the empty-selection explanation when nothing survived the filters;
	/// <see langword="null"/> when no requested diagnostic applies and the selection is not empty.
	/// </summary>
	public static string? SelectionNotices(
		ProjectContextPlan plan,
		bool agentExclusions,
		bool includeFilters,
		McpSelectionNoticeContext request)
	{
		ArgumentNullException.ThrowIfNull(plan);
		var isEmpty = plan.IncludedFiles.Count == 0;
		if (!includeFilters && !isEmpty && plan.FileSizeFilter is null)
			return null;

		var notice = Notice(plan, agentExclusions);
		return isEmpty ? notice + "\n" + EmptySelectionNotice(plan, request) : notice;
	}

	private static string EmptySelectionNotice(
		ProjectContextPlan plan,
		McpSelectionNoticeContext request)
	{
		if (request.HasPatterns)
			return PatternSelectionEmptyNotice;

		var gitMode = plan.Selection.GitMode ?? GitFilteringMode.None;
		var isGitNarrowing = gitMode is GitFilteringMode.TrackedFilesOnly or
			GitFilteringMode.Staged or
			GitFilteringMode.Changes or
			GitFilteringMode.Diff;
		if (request.HasPaths && !isGitNarrowing)
			return PathSelectionEmptyNotice;
		if (isGitNarrowing)
		{
			return $"[Empty selection] Git reports no files for this scope (git: {ProjectSelectionTokens.ToToken(plan.Selection)}).";
		}

		return ProjectSelectionEmptyNotice;
	}
}
