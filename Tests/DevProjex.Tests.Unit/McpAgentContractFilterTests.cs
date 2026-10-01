using DevProjex.Application.Context;
using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpAgentContractFilterTests
{
	[Fact]
	public void StandardFilterAdviceExplainsProfilesAndNarrowingWithoutClaimingStartupIsTheOnlyControl()
	{
		var notice = McpEffectiveFilters.Notice(Plan(ProjectSelectionSpec.Standard), agentExclusions: false);

		Assert.Contains(
			"An explicit profile can replace startup filters; paths and patterns only narrow the resulting selection.",
			notice,
			StringComparison.Ordinal);
		Assert.DoesNotContain("only the server startup line", notice, StringComparison.Ordinal);
	}

	[Fact]
	public void LiveFilterAdviceSaysThatCallsOnlyAddToWindowAndStartupFilters()
	{
		var notice = McpEffectiveFilters.Notice(
			Plan(ProjectSelectionSpec.Standard),
			agentExclusions: true,
			live: true);

		Assert.Contains(
			"Window and startup filters remain enforced. Per-call exclusions can only add filters.",
			notice,
			StringComparison.Ordinal);
		Assert.DoesNotContain("replace startup exclusions", notice, StringComparison.Ordinal);
	}

	[Fact]
	public void ActiveGitModeWithoutStageEvidenceUsesTheGenericEmptySelectionExplanation()
	{
		var selection = ProjectSelectionSpec.Standard with { GitMode = GitFilteringMode.Changes };
		var notices = McpEffectiveFilters.SelectionNoticeParts(
			Plan(selection),
			agentExclusions: false,
			includeFilters: true,
			new McpSelectionNoticeContext(HasPaths: false, HasPatterns: false));

		Assert.Equal(
			"[Empty selection] No files survived the effective filters and request selection.",
			notices.EmptySelection);
		Assert.DoesNotContain("Git reports no files", notices.EmptySelection, StringComparison.Ordinal);
	}

	[Fact]
	public void RepeatedDiffScopeUsesItsCanonicalStateInsteadOfTheRandomBoundary()
	{
		var selection = ProjectSelectionSpec.Standard with
		{
			GitMode = GitFilteringMode.Diff,
			GitDiffRange = "main..topic"
		};
		var plan = Plan(selection, fileCount: 1);
		var memo = new McpServiceNoticeMemo();
		var firstFilters = McpEffectiveFilters.Notice(plan, agentExclusions: false);
		var first = memo.Prepare("project", firstFilters, protection: null, alwaysSend: false);
		Assert.DoesNotContain("<untrusted-data-", firstFilters, StringComparison.Ordinal);
		Assert.Contains("<untrusted-data-", first.Filters, StringComparison.Ordinal);
		Assert.Contains("[Effective git scope] diff:main..topic", first.Filters, StringComparison.Ordinal);
		memo.CommitDelivered(first.Filters!);

		var secondFilters = McpEffectiveFilters.Notice(plan, agentExclusions: false);
		var second = memo.Prepare("project", secondFilters, protection: null, alwaysSend: false);

		Assert.Null(second.Filters);
		Assert.Equal(McpServiceNoticeMemo.FiltersContinuationNotice, second.Continuation);
	}

	[Fact]
	public void ContinuationNoticesReferToTheEffectivePolicyRatherThanStartupDefaults()
	{
		Assert.Equal(
			"[Unchanged] effective filters, protection.",
			McpServiceNoticeMemo.ContinuationNotice);
		Assert.Equal("[Unchanged] effective filters.", McpServiceNoticeMemo.FiltersContinuationNotice);
		Assert.Equal("[Unchanged] protection.", McpServiceNoticeMemo.ProtectionContinuationNotice);
	}

	private static ProjectContextPlan Plan(ProjectSelectionSpec selection, int fileCount = 0)
	{
		var root = Path.GetPathRoot(Environment.CurrentDirectory) ?? Environment.CurrentDirectory;
		var tree = new TreeNodeDescriptor("project", root, true, false, "folder", []);
		var analysis = new ProjectAnalysisReport(
			ProjectAnalysisReport.CurrentSchemaVersion,
			DateTimeOffset.UnixEpoch,
			root,
			new ProjectAnalysisSelectionReport([], [], []),
			new ProjectAnalysisInventoryReport([], [], new ProjectTreeSummaryReport(1, fileCount, 0)),
			new ProjectAnalysisOutputMetricsReport(ProjectOutputMetricsReport.Empty, ProjectOutputMetricsReport.Empty),
			new ProjectAnalysisTimingReport(0, 0, 0),
			new ProjectAnalysisDiagnosticsReport(false, false, []));
		return new ProjectContextPlan(
			root,
			selection,
			[],
			[],
			[],
			[],
			tree,
			tree,
			new HashSet<string>(PathComparer.Default),
			Enumerable.Range(0, fileCount).Select(index => Path.Combine(root, $"File{index}.cs")).ToArray(),
			[root],
			analysis,
			[],
			new ProjectContextGitReadiness(selection.GitMode ?? GitFilteringMode.None, 0, true),
			"filter-contract");
	}
}
