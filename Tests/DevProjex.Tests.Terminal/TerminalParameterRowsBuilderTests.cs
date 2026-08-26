using DevProjex.Application.Secrets;
using DevProjex.Application.Presentation;
using Terminal.Gui.Text;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalParameterRowsBuilderTests
{
	[Theory]
	[InlineData(true, "[x] .cs")]
	[InlineData(false, "[ ] .cs")]
	public void RowsStartAtTheMiniPanelContentEdge(bool isSelected, string expected)
	{
		var row = new TerminalParameterRow(
			"row",
			TerminalParameterRowKind.Extension,
			".cs",
			isSelected);

		Assert.Equal(expected, row.ToString());
		Assert.False(row.ToString().StartsWith(' '));
	}

	[Fact]
	public void ContentRowsExposeAllTransformationsAndSeparateRedactionCounters()
	{
		var builder = CreateBuilder();
		var selection = ProjectSelectionSpec.Standard with
		{
			HideSecrets = true,
			HidePrivateData = true,
			CompressCode = true,
			StripComments = true,
			StripBlankLines = true
		};
		var snapshot = new SecretRedactionSnapshot(
			"selection",
			DetectedCount: 8,
			RedactedCount: 6,
			PrivateDataDetectedCount: 3,
			PrivateDataRedactedCount: 2);

		var rows = builder.BuildContent(CreatePlan(selection), snapshot);

		Assert.Equal(5, rows.Count);
		Assert.All(rows, static row =>
		{
			Assert.Equal(TerminalParameterRowKind.ContentTransformation, row.Kind);
			Assert.True(row.IsSelected);
		});
		Assert.Contains(rows, static row =>
			row.ContentTransformation == IgnoreOptionId.HideSecrets &&
			row.Label == "HideSecrets:5/4");
		Assert.Contains(rows, static row =>
			row.ContentTransformation == IgnoreOptionId.HidePrivateData &&
			row.Label == "HidePrivateData:3/2");
	}

	[Fact]
	public void ExclusionRowsKeepMutuallyExclusiveGitModesWithoutAggregateRow()
	{
		var builder = CreateBuilder();
		var rows = builder.BuildExclusions(CreatePlan(ProjectSelectionSpec.Standard));

		Assert.DoesNotContain(rows, static row => row.Kind == TerminalParameterRowKind.ToggleAllExclusions);
		Assert.Equal(GitFilteringMode.RespectGitIgnore, rows[0].GitMode);
		Assert.True(rows[0].IsSelected);
		Assert.Equal(GitFilteringMode.TrackedFilesOnly, rows[1].GitMode);
		Assert.False(rows[1].IsSelected);
		Assert.DoesNotContain(
			rows,
			static row => row.GitMode == GitFilteringMode.None);
	}

	[Fact]
	public void ExclusionRowsUseThePlansGuiEquivalentImpactCounts()
	{
		var counts = new IgnoreOptionCounts(
			HiddenFolders: 2,
			HiddenFiles: 3,
			DotFolders: 4,
			DotFiles: 5,
			EmptyFolders: 6,
			ExtensionlessFiles: 7,
			EmptyFiles: 8);
		var rows = CreateBuilder().BuildExclusions(CreatePlan(
			ProjectSelectionSpec.Standard,
			hasIgnoreOptionCounts: true,
			ignoreOptionCounts: counts));

		Assert.Equal(
			"Settings.Ignore.HiddenFolders (2)",
			rows.Single(static row => row.Exclusion == ProjectExclusion.HiddenFolders).Label);
		Assert.Equal(
			"Settings.Ignore.DotFiles (5)",
			rows.Single(static row => row.Exclusion == ProjectExclusion.DotFiles).Label);
		Assert.Equal(
			"Settings.Ignore.EmptyFiles (8)",
			rows.Single(static row => row.Exclusion == ProjectExclusion.EmptyFiles).Label);
		Assert.DoesNotContain(
			"(",
			rows.Single(static row => row.Exclusion == ProjectExclusion.SmartIgnore).Label,
			StringComparison.Ordinal);
	}

	[Fact]
	public void ContentAggregateCountsAndTogglesAllFiveTransformations()
	{
		var builder = CreateBuilder();
		var allOn = ProjectSelectionSpec.Standard with
		{
			HideSecrets = true,
			HidePrivateData = true,
			CompressCode = true,
			StripComments = true,
			StripBlankLines = true
		};

		var selected = builder.BuildContentAggregate(allOn);
		var cleared = builder.BuildContentAggregate(ProjectSelectionSpec.Standard);

		Assert.Equal(TerminalParameterRowKind.ToggleAllContent, selected.Kind);
		Assert.Equal("Settings.All (5)", selected.Label);
		Assert.True(selected.IsSelected);
		Assert.False(cleared.IsSelected);
	}

	[Fact]
	public void ExclusionAggregateRequiresPreferredGitModeAndEveryRule()
	{
		var builder = CreateBuilder();

		var selected = builder.BuildExclusionAggregate(CreatePlan(ProjectSelectionSpec.Standard));
		var cleared = builder.BuildExclusionAggregate(CreatePlan(ProjectSelectionSpec.Standard with
		{
			GitMode = GitFilteringMode.None,
			Exclusions = []
		}));

		Assert.Equal(TerminalParameterRowKind.ToggleAllExclusions, selected.Kind);
		Assert.Equal(
			$"Settings.All ({ProjectPresentationCatalog.GitFiltering.Count - 1 + ProjectPresentationCatalog.Exclusions.Count})",
			selected.Label);
		Assert.True(selected.IsSelected);
		Assert.False(cleared.IsSelected);
	}

	[Fact]
	public void ExtensionRowsSilentlyOmitUnavailableRememberedValues()
	{
		var builder = CreateBuilder();
		var selection = ProjectSelectionSpec.Standard with
		{
			Extensions = [".cs", ".removed"]
		};
		var rows = builder.BuildExtensions(CreatePlan(
			selection,
			availableExtensions: [".cs", ".json"],
			selectedExtensions: [".cs"]));

		Assert.DoesNotContain(rows, static row => row.Kind == TerminalParameterRowKind.ToggleAllExtensions);
		Assert.Contains(rows, static row =>
			row.Kind == TerminalParameterRowKind.Extension &&
			row.Value == ".cs" && row.IsSelected == true);
		Assert.DoesNotContain(rows, static row =>
			string.Equals(row.Value, ".removed", StringComparison.OrdinalIgnoreCase) ||
			row.Label.Contains(".removed", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void ExtensionAggregateIgnoresUnavailableRememberedSelections()
	{
		var row = CreateBuilder().BuildExtensionAggregate(CreatePlan(
			ProjectSelectionSpec.Standard,
			availableExtensions: [".cs", ".json"],
			selectedExtensions: [".cs", ".json", ".removed"]));

		Assert.Equal(TerminalParameterRowKind.ToggleAllExtensions, row.Kind);
		Assert.Equal("Settings.All (2)", row.Label);
		Assert.True(row.IsSelected);
	}

	[Fact]
	public void ParameterIslandsNeverEmitInformationalPlaceholderRows()
	{
		var builder = CreateBuilder();
		var plan = CreatePlan(
			ProjectSelectionSpec.Standard with { Extensions = [".missing"] },
			availableExtensions: [".cs"],
			selectedExtensions: []);

		var rows = builder.BuildContent(plan, snapshot: null)
			.Concat(builder.BuildExclusions(plan))
			.Concat(builder.BuildExtensions(plan));

		Assert.All(rows, static row => Assert.NotNull(row.IsSelected));
	}

	[Theory]
	[InlineData(0, 0, false)]
	[InlineData(1, 1, true)]
	[InlineData(1, 0, false)]
	public void ExtensionAggregateHandlesEmptyAndSingleItemSets(
		int availableCount,
		int selectedCount,
		bool expected)
	{
		var available = availableCount == 0 ? Array.Empty<string>() : [".cs"];
		var selected = selectedCount == 0 ? Array.Empty<string>() : [".cs"];

		var row = CreateBuilder().BuildExtensionAggregate(CreatePlan(
			ProjectSelectionSpec.Standard,
			availableExtensions: available,
			selectedExtensions: selected));

		Assert.Equal(expected, row.IsSelected);
		Assert.Equal(
			availableCount == 0 ? "Settings.All" : $"Settings.All ({availableCount})",
			row.Label);
	}

	[Fact]
	public void ExtensionAggregateTreatsUnavailableSelectionsAsOutsideTheAvailableSet()
	{
		var row = CreateBuilder().BuildExtensionAggregate(CreatePlan(
			ProjectSelectionSpec.Standard with { Extensions = [".removed"] },
			availableExtensions: [],
			selectedExtensions: []));

		Assert.False(row.IsSelected);
	}

	[Theory]
	[InlineData(0, 0, 2, true, 0)]
	[InlineData(4, 1, 8, true, 5)]
	[InlineData(0, 2, 2, false, 2)]
	[InlineData(4, 4, 8, false, 8)]
	[InlineData(0, -1, 2, false, -1)]
	public void ParameterListPointerSelectionRejectsRowsOutsideTheSource(
		int viewportTop,
		int pointerRow,
		int itemCount,
		bool expected,
		int expectedIndex)
	{
		var resolved = TerminalParameterListView.TryResolveSelectionIndex(
			viewportTop,
			pointerRow,
			itemCount,
			out var selectionIndex);

		Assert.Equal(expected, resolved);
		Assert.Equal(expectedIndex, selectionIndex);
	}

	[Theory]
	[InlineData("abcdef", 4, true, "abc…")]
	[InlineData("界界界", 5, true, "界界…")]
	[InlineData("abcdef", 5, false, "ab...")]
	public void FitLabelHonorsTerminalColumns(
		string value,
		int width,
		bool useUnicode,
		string expected)
	{
		var result = TerminalParameterRow.FitLabel(value, width, useUnicode);

		Assert.Equal(expected, result);
		Assert.True(result.GetColumns() <= width);
	}

	[Theory]
	[InlineData("Очень длинная подпись личных данных (12/11)", 24, true, " (12/11)")]
	[InlineData("Maxfiy ma'lumotlarning juda uzun nomi (7)", 22, false, " (7)")]
	[InlineData("界界界界界界界界 (3/2)", 14, true, " (3/2)")]
	public void FitLabelPreservesTrailingCounters(
		string value,
		int width,
		bool useUnicode,
		string counter)
	{
		var result = TerminalParameterRow.FitLabel(value, width, useUnicode);

		Assert.EndsWith(counter, result, StringComparison.Ordinal);
		Assert.Contains(useUnicode ? "…" : "...", result, StringComparison.Ordinal);
		Assert.True(result.GetColumns() <= width);
	}

	private static TerminalParameterRowsBuilder CreateBuilder() =>
		new(
			static key => key,
			static value => value,
			static (option, _, matched, redacted) =>
				$"{option}:{matched}/{redacted}");

	private static ProjectContextPlan CreatePlan(
		ProjectSelectionSpec selection,
		IReadOnlyList<string>? availableExtensions = null,
		IReadOnlyList<string>? selectedExtensions = null,
		bool hasIgnoreOptionCounts = false,
		IgnoreOptionCounts ignoreOptionCounts = default)
	{
		var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DevProjexTerminalRows"));
		var tree = new TreeNodeDescriptor("project", root, true, false, "folder", []);
		var analysis = new ProjectAnalysisReport(
			1,
			DateTimeOffset.UnixEpoch,
			root,
			new ProjectAnalysisSelectionReport([], [], []),
			new ProjectAnalysisInventoryReport(
				[],
				availableExtensions ?? [".cs"],
				new ProjectTreeSummaryReport(1, 0, 0)),
			new ProjectAnalysisOutputMetricsReport(
				ProjectOutputMetricsReport.Empty,
				ProjectOutputMetricsReport.Empty),
			new ProjectAnalysisTimingReport(0, 0, 0),
			new ProjectAnalysisDiagnosticsReport(false, false, []));
		return new ProjectContextPlan(
			root,
			selection,
			[],
			[],
			availableExtensions ?? [".cs"],
			selectedExtensions ?? [".cs"],
			tree,
			tree,
			new HashSet<string>(PathComparer.Default),
			[],
			[root],
			analysis,
			[],
			new ProjectContextGitReadiness(
				selection.GitMode ?? GitFilteringMode.None,
				1,
				true),
			"rows",
			HasIgnoreOptionCounts: hasIgnoreOptionCounts,
			IgnoreOptionCounts: ignoreOptionCounts);
	}
}
