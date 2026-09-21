using System.Globalization;

namespace DevProjex.Mcp;

/// <param name="HasRootOnlyPattern">
/// Whether an include pattern carries no <c>/</c>, so it can only match a file that sits directly
/// in the project root. It is the shape a caller reaches for when all they know is a file name,
/// and the one that silently returns nothing.
/// </param>
internal readonly record struct McpSelectionNoticeContext(
	bool HasPaths,
	bool HasPatterns,
	bool HasRootOnlyPattern = false);

/// <summary>
/// The effective-filter footer and, when nothing survived, the explanation of the empty result.
/// </summary>
internal readonly record struct McpSelectionNotices(
	string? Filters,
	string? EmptySelection);

/// <summary>
/// Trusted, path-free descriptions of the filters that shaped a selection. They let an agent
/// tell "this file does not exist" from "this server hides it" without naming a hidden path.
/// </summary>
internal static class McpEffectiveFilters
{
	public const string StartupFlags = "--exclude, --unrestricted, --allow-agent-exclusions";

	// Each notice opens with the stage that emptied the selection, as a constant token, so a caller
	// can tell "narrow your pattern" from "this server hides it" without a second call. The stage is
	// the proximate one the server can name; overlapping narrowing is reported as the request
	// argument that was applied, not as a claim about which one removed the last file.
	private const string PatternSelectionEmptyNotice =
		"[Empty selection] stage=patterns. No file passed the effective filters and the request patterns. " +
		"Patterns match the whole project-relative path: '*' stays inside one segment, '**/' spans any depth; " +
		"paths the filters hide never match.";
	private const string RootOnlyPatternSelectionEmptyNotice =
		"[Empty selection] stage=patterns. A pattern with no '/' and no '**' matches only an entry directly " +
		"in the project root; prefix it with '**/' to match that name at any depth, or append '/**' to select " +
		"a directory's files. Paths the filters hide never match.";
	private const string PathSelectionEmptyNotice =
		"[Empty selection] stage=paths. None of the requested paths is in the effective selection; paths the filters hide never match.";
	private const string ProjectSelectionEmptyNotice =
		"[Empty selection] stage=filters. The effective filters leave no file in this project.";
	private const string IndeterminateEmptySelectionNotice =
		"[Empty selection] No files survived the effective filters and request selection.";

	public static string Describe(ProjectContextPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);
		var description =
			$"git: {DescribeGitMode(plan.Selection)}; exclusions: {DescribeExclusions(plan.Selection.Exclusions)}";
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
	public static string Notice(ProjectContextPlan plan, bool agentExclusions, bool live = false)
	{
		var trusted = $"[Effective filters] {Describe(plan)}. " + WideningHint(agentExclusions, live);
		return DescribeUntrustedGitScope(plan.Selection) is { } scope
			? McpServiceNoticeMemo.DeferUntrustedData(trusted, scope)
			: trusted;
	}

	public static string WideningHint(bool agentExclusions, bool live = false) =>
		live
			? "Window and startup filters remain enforced. Per-call exclusions can only add filters."
			: agentExclusions
				? "Per-call exclusions can replace startup exclusions; paths and patterns only narrow the resulting selection."
				: "An explicit profile can replace startup filters; paths and patterns only narrow the resulting selection.";

	/// <summary>
	/// The footer and the empty-selection explanation as separate lines, so a caller can decide
	/// which of them a given response repeats. Both are <see langword="null"/> when no requested
	/// diagnostic applies and the selection is not empty.
	/// </summary>
	public static McpSelectionNotices SelectionNoticeParts(
		ProjectContextPlan plan,
		bool agentExclusions,
		bool includeFilters,
		McpSelectionNoticeContext request,
		bool live = false)
	{
		ArgumentNullException.ThrowIfNull(plan);
		var isEmpty = plan.IncludedFiles.Count == 0;
		if (!includeFilters && !isEmpty && plan.FileSizeFilter is null)
			return new McpSelectionNotices(null, null);

		return new McpSelectionNotices(
			Notice(plan, agentExclusions, live),
			isEmpty ? EmptySelectionNotice(plan, request) : null);
	}

	private static string EmptySelectionNotice(
		ProjectContextPlan plan,
		McpSelectionNoticeContext request)
	{
		if (request.HasPatterns)
		{
			return request.HasRootOnlyPattern
				? RootOnlyPatternSelectionEmptyNotice
				: PatternSelectionEmptyNotice;
		}

		var gitMode = plan.Selection.GitMode ?? GitFilteringMode.None;
		var isGitNarrowing = gitMode is GitFilteringMode.TrackedFilesOnly or
			GitFilteringMode.Staged or
			GitFilteringMode.Changes or
			GitFilteringMode.Diff;
		if (request.HasPaths && !isGitNarrowing)
			return PathSelectionEmptyNotice;
		if (isGitNarrowing)
			return IndeterminateEmptySelectionNotice;

		return ProjectSelectionEmptyNotice;
	}

	private static string DescribeGitMode(ProjectSelectionSpec selection) =>
		selection.GitMode == GitFilteringMode.Diff
			? "diff"
			: ProjectSelectionTokens.ToToken(selection);

	private static string? DescribeUntrustedGitScope(ProjectSelectionSpec selection) =>
		selection.GitMode == GitFilteringMode.Diff
			? $"[Effective git scope] {ProjectSelectionTokens.ToToken(selection)}"
			: null;
}
