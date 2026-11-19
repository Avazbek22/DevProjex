using DevProjex.Application.Context;
using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed partial class McpLiveContextStateTests
{
	[Theory]
	[InlineData(3, 1, " (2 files no longer readable)", true)]
	[InlineData(1, 3, " (2 now readable)", true)]
	[InlineData(1, 1, "", true)]
	[InlineData(3, 1, "", false)]
	public void WindowFilterCountsUseTheTreeBeforeFocus(int before, int after, string counts, bool capture)
	{
		using var temporary = new TemporaryDirectory();
		var first = new ProjectSelectionProfile([], [".md", ".py"], [], SelectedPaths: ["src"]);
		var second = first with { SelectedExtensions = [".py"] };
		var store = new SequenceProfileStore(Found(first), Found(second));
		var state = new McpLiveContextState(new McpRootRegistry([temporary.Path]), () => store);
		using (state.BeginInvocation())
		{
			state.ReadProfile(temporary.Path);
			state.RecordPlan(temporary.Path, BoundaryPlan(before));
			Assert.DoesNotContain("changed since", Text(state.AppendNotices(McpToolResults.TextSuccess("ok"))), StringComparison.Ordinal);
		}
		using (state.BeginInvocation())
		{
			state.ReadProfile(temporary.Path);
			state.RecordPlan(temporary.Path, BoundaryPlan(after), captureReadableBoundary: capture);
			var text = Text(state.AppendNotices(McpToolResults.TextSuccess("ok")));
			Assert.Contains("changed since revision 1: window filters changed" + counts, text, StringComparison.Ordinal);
			Assert.DoesNotContain("selection settings changed", text, StringComparison.Ordinal);
			AssertMarkerIsInsideUntrustedData(text, "-extension .md");
		}

		ProjectContextPlan BoundaryPlan(int count) => Plan(temporary.Path, 1, Tree(temporary.Path, count)) with
		{
			GitReadiness = new ProjectContextGitReadiness(GitFilteringMode.None, 0, true)
		};
	}

	[Fact]
	public void SeveralUnreportedChangesUseTheOriginalRevisionAndProfile()
	{
		using var temporary = new TemporaryDirectory();
		var first = new ProjectSelectionProfile([], [".md", ".py"], [], SelectedPaths: ["src"]);
		var second = first with { SelectedExtensions = [".py"], SelectedPaths = ["intermediate"] };
		var third = first with { SelectedExtensions = [".ts"], SelectedPaths = ["tests"] };
		var store = new SequenceProfileStore(Found(first), Found(second), Found(third));
		var state = new McpLiveContextState(new McpRootRegistry([temporary.Path]), () => store);
		using (state.BeginInvocation())
		{
			state.ReadProfile(temporary.Path);
			state.AppendNotices(McpToolResults.TextSuccess("ok"));
		}
		state.RefreshProfile(temporary.Path);
		using (state.BeginInvocation())
		{
			Assert.Equal(3, state.ReadProfile(temporary.Path).Revision);
			var text = Text(state.AppendNotices(McpToolResults.TextSuccess("ok")));
			Assert.Contains("changed since revision 1: window filters changed · +1 path, -1 path", text, StringComparison.Ordinal);
			Assert.Contains("-extension .md", text, StringComparison.Ordinal);
			Assert.Contains("+extension .ts", text, StringComparison.Ordinal);
			Assert.DoesNotContain("intermediate", text, StringComparison.Ordinal);
			Assert.DoesNotContain("no longer readable", text, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void FilterComparisonExcludesFocusTransformationsAndExtensionCase()
	{
		var first = new ProjectSelectionProfile([], [".MD"], [], SelectedPaths: ["src"]);
		var after = first with { SelectedExtensions = [".md"], SelectedPaths = [], SelectedIgnoreOptions = [IgnoreOptionId.StripComments, IgnoreOptionId.HidePrivateData] };
		Assert.Empty(McpWindowFilters.Changes(first, after));
	}

	[Fact]
	public void FilterValuesAreBoundedAndControlCharactersAreEscaped()
	{
		var changes = Enumerable.Range(0, 15).Select(index => "+extension .value" + index + "\r\nnext").ToArray();
		var text = McpWindowFilters.FormatValues(changes);
		Assert.Equal(11, text.Split(Environment.NewLine).Length);
		Assert.EndsWith("· 5 more", text, StringComparison.Ordinal);
		Assert.Contains("\\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain("\r\nnext", text, StringComparison.Ordinal);
	}

	[Fact]
	public void CountsCanNarrowAndWidenTogetherAndUnknownSnapshotsStayUncounted()
	{
		Assert.Equal(" (1 files no longer readable, 1 now readable)", McpWindowFilters.FormatCounts(
			new HashSet<string> { "a.py", "b.md" }, new HashSet<string> { "a.py", "c.ts" }));
		Assert.Equal("", McpWindowFilters.FormatCounts(null, new HashSet<string>()));
		Assert.Equal("", McpWindowFilters.FormatCounts(new HashSet<string>(), null));
	}

	[Fact]
	public void FilterStateMapsDistinguishAllNoneAndExplicitSelections()
	{
		var all = new ProjectSelectionProfile([], [], []);
		Assert.Empty(McpWindowFilters.Changes(all, null));
		var none = all with { ExtensionStates = new Dictionary<string, bool> { [".md"] = false } };
		var selected = all with { ExtensionStates = new Dictionary<string, bool> { [".md"] = true }, RootFolderStates = new Dictionary<string, bool> { ["src\\nested"] = true } };
		Assert.Contains("-extension selection all", McpWindowFilters.Changes(all, none));
		Assert.Contains("+extension selection none", McpWindowFilters.Changes(all, none));
		Assert.Contains("+extension .md", McpWindowFilters.Changes(none, selected));
		Assert.Contains("+root folder \"src/nested\"", McpWindowFilters.Changes(none, selected));
		var newExclusion = selected with { ExtensionStates = new Dictionary<string, bool> { [".md"] = true, [".py"] = false } };
		Assert.Contains("+exclusion extension .py", McpWindowFilters.Changes(selected, newExclusion));
		Assert.NotEqual(McpWindowFilters.Identity(selected), McpWindowFilters.Identity(newExclusion));
		Assert.NotEqual(McpWindowFilters.Identity(all), McpWindowFilters.Identity(all with { SelectedRootFolders = ["selection all"] }));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void IncompleteReadableBoundariesDoNotProduceCounts(bool denied)
	{
		using var temporary = new TemporaryDirectory();
		var plan = Plan(temporary.Path, 1, Tree(temporary.Path, 1));
		plan = plan with
		{
			GitReadiness = new ProjectContextGitReadiness(GitFilteringMode.TrackedFilesOnly, 0, !denied),
			Analysis = plan.Analysis with { Diagnostics = new ProjectAnalysisDiagnosticsReport(false, !denied, []) }
		};
		Assert.Null(McpWindowFilters.CaptureReadableFiles(plan));
	}

	[Fact]
	public void ReadableBoundariesBeyondTheLimitStayUnknown()
	{
		using var temporary = new TemporaryDirectory();
		var plan = Plan(temporary.Path, 1, Tree(temporary.Path, McpWindowFilters.MaximumReadableFiles + 1)) with
		{
			GitReadiness = new ProjectContextGitReadiness(GitFilteringMode.None, 0, true)
		};
		Assert.Null(McpWindowFilters.CaptureReadableFiles(plan));
	}

	[Fact]
	public void WindowFilterNoticesCommitAfterTransportNormalizesCrLf()
	{
		var memo = new McpServiceNoticeMemo();
		var filters = McpServiceNoticeMemo.DeferUntrustedData("[Effective filters] windows\r\n", "extension .md\r\nroot folder src");
		var first = memo.Prepare("root", filters, null, false, "window-policy");
		memo.CommitDelivered(McpToolResults.NormalizeLineEndings(first.Filters!));
		Assert.Equal(McpServiceNoticeMemo.FiltersContinuationNotice, memo.Prepare("root", filters, null, false, "window-policy").Continuation);
	}

	[Fact]
	public void WindowFilterIdentityInvalidatesNoticeEvenBeyondTheDisplayedRows()
	{
		var memo = new McpServiceNoticeMemo();
		const string filters = "[Effective filters] Saved window filters apply.";
		var profile = new ProjectSelectionProfile([], Enumerable.Range(0, 15).Select(index => $".extension{index:00}").ToArray(), []);
		var changed = profile with { SelectedExtensions = [.. profile.SelectedExtensions.Take(14), ".z"] };
		Assert.Equal(McpWindowFilters.FormatValues(McpWindowFilters.Values(profile)), McpWindowFilters.FormatValues(McpWindowFilters.Values(changed)));
		var initial = memo.Prepare("root", filters, null, false, McpWindowFilters.Identity(profile));
		memo.CommitDelivered(initial.Filters!);
		Assert.NotNull(memo.Prepare("root", filters, null, false, McpWindowFilters.Identity(changed)).Filters);
		memo.CommitDelivered(filters);
		Assert.Equal(McpServiceNoticeMemo.FiltersContinuationNotice, memo.Prepare("root", filters, null, false, McpWindowFilters.Identity(changed)).Continuation);
	}
}
