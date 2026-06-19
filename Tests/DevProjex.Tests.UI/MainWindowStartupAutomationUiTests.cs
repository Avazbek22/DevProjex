using System.Text.Json;
using DevProjex.Infrastructure.Reports;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowStartupAutomationUiTests
{
	[AvaloniaFact]
	public async Task StartupReport_WritesReportAfterCommandLineProjectLoad()
	{
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(appDataPath);
		var reportPath = Path.Combine(project.AppDataPath, "startup-report.json");
		var options = new CommandLineOptions(project.RootPath, AppLanguage.En, false)
		{
			Report = new StartupReportOptions(true, reportPath, StartupReportFormat.Json),
			IncludeRootFolders = ["src"],
			IncludeExtensions = [".cs"],
			IgnoreOptionsSpecified = true,
			IgnoreOptions = []
		};
		var services = AvaloniaCompositionRoot.CreateDefault(options, () => appDataPath);
		var window = new MainWindow(options, services)
		{
			Width = 1500,
			Height = 920
		};
		UiTestDriver.TrackTopLevelWindow(window);

		try
		{
			window.Show();

			await UiTestDriver.WaitForConditionAsync(
				window,
				() => UiTestDriver.GetViewModel(window).IsProjectLoaded && File.Exists(reportPath),
				"startup report to be written after project load");

			var json = await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken);
			using var document = JsonDocument.Parse(json);
			var root = document.RootElement;
			Assert.Equal(ProjectAnalysisReport.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
			Assert.Equal(project.RootPath, root.GetProperty("rootPath").GetString());
			Assert.Equal("src", root.GetProperty("selection").GetProperty("selectedRootFolders")[0].GetString());
			Assert.Equal(".cs", root.GetProperty("selection").GetProperty("selectedExtensions")[0].GetString());
			Assert.Empty(root.GetProperty("selection").GetProperty("selectedIgnoreOptions").EnumerateArray());
			Assert.True(root.GetProperty("inventory").GetProperty("tree").GetProperty("fileCount").GetInt32() > 0);
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaFact]
	public async Task StartupReport_WithoutExplicitPath_UsesDefaultDocumentsReportFolder()
	{
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var documentsPath = Path.Combine(project.AppDataPath, "Documents");
		Directory.CreateDirectory(appDataPath);
		var expectedReportPath = Path.Combine(
			documentsPath,
			"DevProjex",
			"reports",
			"devprojex-report-2026-06-19_11-12-13.json");
		var options = new CommandLineOptions(project.RootPath, AppLanguage.En, false)
		{
			Report = new StartupReportOptions(true, null, StartupReportFormat.Json),
			IncludeRootFolders = ["src"],
			IncludeExtensions = [".cs"],
			IgnoreOptionsSpecified = true,
			IgnoreOptions = []
		};
		var services = AvaloniaCompositionRoot.CreateDefault(options, () => appDataPath) with
		{
			ReportPathResolver = new ReportPathResolver(
				specialFolderPathProvider: folder => folder == Environment.SpecialFolder.MyDocuments ? documentsPath : string.Empty,
				utcNowProvider: () => new DateTimeOffset(2026, 6, 19, 11, 12, 13, TimeSpan.Zero))
		};
		var window = new MainWindow(options, services)
		{
			Width = 1500,
			Height = 920
		};
		UiTestDriver.TrackTopLevelWindow(window);

		try
		{
			window.Show();

			await UiTestDriver.WaitForConditionAsync(
				window,
				() => UiTestDriver.GetViewModel(window).IsProjectLoaded && File.Exists(expectedReportPath),
				"startup report to be written to the default report folder");

			var json = await File.ReadAllTextAsync(expectedReportPath, TestContext.Current.CancellationToken);
			using var document = JsonDocument.Parse(json);
			var root = document.RootElement;
			Assert.Equal(ProjectAnalysisReport.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
			Assert.Equal(project.RootPath, root.GetProperty("rootPath").GetString());
			Assert.Equal("src", root.GetProperty("selection").GetProperty("selectedRootFolders")[0].GetString());
			Assert.Equal(".cs", root.GetProperty("selection").GetProperty("selectedExtensions")[0].GetString());
		}
		finally
		{
			window.Close();
		}
	}
}
