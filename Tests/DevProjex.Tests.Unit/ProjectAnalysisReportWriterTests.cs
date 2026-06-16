using DevProjex.Infrastructure.Reports;

namespace DevProjex.Tests.Unit;

public sealed class ProjectAnalysisReportWriterTests
{
	[Fact]
	public async Task WriteAsync_CreatesDirectoryAndWritesCamelCaseJson()
	{
		using var temp = new TemporaryDirectory();
		var path = Path.Combine(temp.Path, "nested", "report.json");
		var writer = new ProjectAnalysisReportWriter();

		await writer.WriteAsync(CreateReport("first"), path, TestContext.Current.CancellationToken);

		var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
		using var document = JsonDocument.Parse(json);
		var root = document.RootElement;
		Assert.Equal(ProjectAnalysisReport.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
		Assert.Equal("first", root.GetProperty("rootPath").GetString());
		Assert.Equal("dotFolders", root.GetProperty("selection").GetProperty("selectedIgnoreOptions")[0].GetString());
		Assert.False(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any());
	}

	[Fact]
	public async Task WriteAsync_ReplacesExistingReport()
	{
		using var temp = new TemporaryDirectory();
		var path = Path.Combine(temp.Path, "report.json");
		var writer = new ProjectAnalysisReportWriter();

		await writer.WriteAsync(CreateReport("first"), path, TestContext.Current.CancellationToken);
		await writer.WriteAsync(CreateReport("second"), path, TestContext.Current.CancellationToken);

		var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
		using var document = JsonDocument.Parse(json);
		Assert.Equal("second", document.RootElement.GetProperty("rootPath").GetString());
	}

	private static ProjectAnalysisReport CreateReport(string rootPath) =>
		new(
			SchemaVersion: ProjectAnalysisReport.CurrentSchemaVersion,
			GeneratedUtc: new DateTimeOffset(2026, 6, 16, 10, 11, 12, TimeSpan.Zero),
			RootPath: rootPath,
			Selection: new ProjectAnalysisSelectionReport(
				SelectedRootFolders: ["src"],
				SelectedExtensions: [".cs"],
				SelectedIgnoreOptions: [IgnoreOptionId.DotFolders]),
			Inventory: new ProjectAnalysisInventoryReport(
				AvailableRootFolders: ["src"],
				AvailableExtensions: [".cs"],
				Tree: new ProjectTreeSummaryReport(DirectoryCount: 1, FileCount: 1, AccessDeniedDirectoryCount: 0)),
			Metrics: new ProjectAnalysisOutputMetricsReport(
				Tree: new ProjectOutputMetricsReport(1, 10, 3),
				Content: new ProjectOutputMetricsReport(1, 10, 3)),
			Timing: new ProjectAnalysisTimingReport(1.234, 2.345, 3.579),
			Diagnostics: new ProjectAnalysisDiagnosticsReport(
				RootAccessDenied: false,
				HadAccessDenied: false,
				Warnings: []));
}
