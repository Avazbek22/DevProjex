using DevProjex.Application.Secrets;
using Terminal.Gui.Text;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalParameterRowsBuilderTests
{
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
	public void ExclusionRowsKeepAllAndMutuallyExclusiveGitModesInOneList()
	{
		var builder = CreateBuilder();
		var rows = builder.BuildExclusions(CreatePlan(ProjectSelectionSpec.Standard));

		Assert.Equal(TerminalParameterRowKind.ToggleAllExclusions, rows[0].Kind);
		Assert.True(rows[0].IsSelected);
		Assert.Equal(GitFilteringMode.RespectGitIgnore, rows[1].GitMode);
		Assert.True(rows[1].IsSelected);
		Assert.Equal(GitFilteringMode.TrackedFilesOnly, rows[2].GitMode);
		Assert.False(rows[2].IsSelected);
		Assert.DoesNotContain(
			rows,
			static row => row.GitMode == GitFilteringMode.None);
	}

	[Fact]
	public void ExtensionRowsAppendUnavailableProfileValuesAsInformation()
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

		Assert.Equal(TerminalParameterRowKind.ToggleAllExtensions, rows[0].Kind);
		Assert.False(rows[0].IsSelected);
		Assert.Contains(rows, static row =>
			row.Kind == TerminalParameterRowKind.Extension &&
			row.Value == ".cs" && row.IsSelected == true);
		Assert.Contains(rows, static row =>
			row.Kind == TerminalParameterRowKind.Information &&
			row.Label.Contains(".removed", StringComparison.Ordinal));
	}

	[Fact]
	public void ExtensionAllRequiresTheExactAvailableSet()
	{
		var rows = CreateBuilder().BuildExtensions(CreatePlan(
			ProjectSelectionSpec.Standard,
			availableExtensions: [".cs", ".json"],
			selectedExtensions: [".cs", ".removed"]));

		Assert.False(rows[0].IsSelected);
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

	private static TerminalParameterRowsBuilder CreateBuilder() =>
		new(
			static key => key,
			static value => value,
			static value => value,
			static (option, _, matched, redacted) =>
				$"{option}:{matched}/{redacted}");

	private static ProjectContextPlan CreatePlan(
		ProjectSelectionSpec selection,
		IReadOnlyList<string>? availableExtensions = null,
		IReadOnlyList<string>? selectedExtensions = null)
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
			"rows");
	}
}
