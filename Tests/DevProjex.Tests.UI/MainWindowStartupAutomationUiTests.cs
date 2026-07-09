using System.Reflection;
using System.Text.Json;
using DevProjex.Infrastructure.Reports;
using DevProjex.Infrastructure.RecentProjects;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowStartupAutomationUiTests
{
	[AvaloniaFact]
	public async Task OpenFolder_RelativePath_NormalizesCurrentPathAndTitle()
	{
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(appDataPath);
		var originalCurrentDirectory = Environment.CurrentDirectory;
		var projectParentPath = Directory.GetParent(project.RootPath)!.FullName;
		Environment.CurrentDirectory = projectParentPath;
		var relativeProjectPath = Path.GetRelativePath(projectParentPath, project.RootPath);
		MainWindow? window = null;
		try
		{
			var options = CommandLineOptions.Empty;
			var services = AvaloniaCompositionRoot.CreateDefault(options, () => appDataPath);
			window = new MainWindow(options, services)
			{
				Width = 1500,
				Height = 920
			};
			UiTestDriver.TrackTopLevelWindow(window);

			window.Show();
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.IsVisible,
				"main window to become visible before opening a relative folder");

			await UiTestDriver.OpenFolderAsync(window, relativeProjectPath);

			var viewModel = UiTestDriver.GetViewModel(window);
			var expectedPath = GetComparablePath(project.RootPath);
			var actualCurrentPath = GetComparablePath(GetCurrentPath(window));
			var actualTitle = NormalizeMacOsPrivateVarAlias(viewModel.Title);

			Assert.Equal(expectedPath, actualCurrentPath);
			Assert.Contains(expectedPath, actualTitle, StringComparison.Ordinal);
			Assert.StartsWith(
				$"{MainWindowViewModel.BaseTitle} - {expectedPath}",
				actualTitle,
				StringComparison.Ordinal);
		}
		finally
		{
			if (window is not null)
				await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
			Environment.CurrentDirectory = originalCurrentDirectory;
		}
	}

	[AvaloniaFact]
	public async Task StartupUi_LastOpensMostRecentLocalFolder()
	{
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var firstPath = Path.Combine(project.RootPath, "history", "first");
		var secondPath = Path.Combine(project.RootPath, "history", "second");
		Directory.CreateDirectory(firstPath);
		Directory.CreateDirectory(secondPath);

		var recentStore = new RecentProjectsStore(() => appDataPath);
		var db = recentStore.Load();
		db = recentStore.AddFolder(db, firstPath);
		db = recentStore.AddFolder(db, secondPath);

		var options = CommandLineOptions.Empty with
		{
			Ui = StartupUiOptions.Default with { OpenLastProject = true }
		};
		var window = CreateStartupWindow(options, appDataPath);

		try
		{
			window.Show();

			await UiTestDriver.WaitForConditionAsync(
				window,
				() => UiTestDriver.GetViewModel(window).IsProjectLoaded,
				"last recent project to load at startup");

			Assert.Equal(GetComparablePath(secondPath), GetComparablePath(GetCurrentPath(window)));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}
	}

	[AvaloniaFact]
	public async Task StartupUi_LastSkipsMissingRecentFolderAndOpensFirstExistingFolder()
	{
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var existingPath = Path.Combine(project.RootPath, "history", "existing");
		var missingPath = Path.Combine(project.RootPath, "history", "missing");
		Directory.CreateDirectory(existingPath);

		var recentStore = new RecentProjectsStore(() => appDataPath);
		var db = recentStore.Load();
		db = recentStore.AddFolder(db, existingPath);
		db = recentStore.AddFolder(db, missingPath);

		var options = ParseValidOptions(CommandLineOptionTokens.Last);
		var window = CreateStartupWindow(options, appDataPath);

		try
		{
			window.Show();

			await UiTestDriver.WaitForConditionAsync(
				window,
				() => UiTestDriver.GetViewModel(window).IsProjectLoaded,
				"first existing recent project to load at startup");

			Assert.Equal(GetComparablePath(existingPath), GetComparablePath(GetCurrentPath(window)));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}
	}

	[AvaloniaFact]
	public async Task StartupUi_PreviewModeAndTreeFormatOpenPreparedPreview()
	{
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var options = new CommandLineOptions(project.RootPath, AppLanguage.En, false)
		{
			Ui = StartupUiOptions.Default with
			{
				OpenPreview = true,
				PreviewMode = StartupPreviewMode.TreeContent,
				TreeFormat = TreeTextFormat.Markdown
			}
		};
		var window = CreateStartupWindow(options, appDataPath);

		try
		{
			window.Show();
			await UiTestDriver.WaitForPreviewReadyAsync(window);

			var viewModel = UiTestDriver.GetViewModel(window);
			Assert.True(viewModel.IsPreviewMode);
			Assert.Equal(PreviewContentMode.TreeAndContent, viewModel.SelectedPreviewContentMode);
			Assert.Equal(ExportFormat.Markdown, viewModel.SelectedExportFormat);

			await UiTestDriver.WaitForConditionAsync(
				window,
				() =>
				{
					var payload = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
					return payload.StartsWith("Root: ", StringComparison.Ordinal) &&
					       payload.Contains("\u00A0", StringComparison.Ordinal);
				},
				"startup Markdown tree-content preview to render");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}
	}

	[AvaloniaFact]
	public async Task StartupUi_ParsedInlinePreviewModeAndTreeFormatOpenXmlTreeContentPreview()
	{
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var options = ParseValidOptions(
			$"{CommandLineOptionTokens.Path}={project.RootPath}",
			$"{CommandLineOptionTokens.PreviewMode}=tree-and-content",
			$"{CommandLineOptionTokens.TreeFormat}=xml");
		var window = CreateStartupWindow(options, appDataPath);

		try
		{
			window.Show();
			await UiTestDriver.WaitForPreviewReadyAsync(window);

			var viewModel = UiTestDriver.GetViewModel(window);
			Assert.True(viewModel.IsPreviewMode);
			Assert.Equal(PreviewContentMode.TreeAndContent, viewModel.SelectedPreviewContentMode);
			Assert.Equal(ExportFormat.Xml, viewModel.SelectedExportFormat);

			await UiTestDriver.WaitForConditionAsync(
				window,
				() =>
				{
					var payload = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
					return payload.StartsWith("<t ", StringComparison.Ordinal) &&
					       payload.Contains("\u00A0", StringComparison.Ordinal);
				},
				"startup XML tree-content preview to render from parsed inline arguments");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}
	}

	[AvaloniaFact]
	public async Task StartupUi_TreeFilterAppliesAfterProjectLoad()
	{
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var options = new CommandLineOptions(project.RootPath, AppLanguage.En, false)
		{
			Ui = StartupUiOptions.Default with { TreeFilter = "Services" }
		};
		var window = CreateStartupWindow(options, appDataPath);

		try
		{
			window.Show();
			await UiTestDriver.WaitForFilterAppliedAsync(window, "Services");

			var viewModel = UiTestDriver.GetViewModel(window);
			Assert.True(viewModel.FilterVisible);
			Assert.False(viewModel.SearchVisible);
			Assert.False(viewModel.IsPreviewMode);
			Assert.True(viewModel.FilterMatchCount > 0);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}
	}

	[AvaloniaFact]
	public async Task StartupUi_ParsedInlineTreeFilterKeepsPreviewClosedAndAppliesFilter()
	{
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var options = ParseValidOptions(
			$"{CommandLineOptionTokens.Path}={project.RootPath}",
			$"{CommandLineOptionTokens.TreeFilter}=Services");
		var window = CreateStartupWindow(options, appDataPath);

		try
		{
			window.Show();
			await UiTestDriver.WaitForFilterAppliedAsync(window, "Services");

			var viewModel = UiTestDriver.GetViewModel(window);
			Assert.False(viewModel.IsPreviewMode);
			Assert.True(viewModel.FilterVisible);
			Assert.False(viewModel.SearchVisible);
			Assert.True(viewModel.FilterMatchCount > 0);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}
	}

	[AvaloniaFact]
	public async Task StartupUi_PreviewSearchOpensPreviewAndSearch()
	{
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var options = new CommandLineOptions(project.RootPath, AppLanguage.En, false)
		{
			Ui = StartupUiOptions.Default with { PreviewSearch = "Preview" }
		};
		var window = CreateStartupWindow(options, appDataPath);

		try
		{
			window.Show();
			await UiTestDriver.WaitForPreviewReadyAsync(window);
			await UiTestDriver.WaitForSearchAppliedAsync(window, "Preview");

			var viewModel = UiTestDriver.GetViewModel(window);
			Assert.True(viewModel.IsPreviewMode);
			Assert.True(viewModel.SearchVisible);
			Assert.False(viewModel.FilterVisible);
			Assert.True(viewModel.SearchTotalMatches > 0);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}
	}

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
			"devprojex-report-2026-06-19_11-12-13-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.json");
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
				utcNowProvider: () => new DateTimeOffset(2026, 6, 19, 11, 12, 13, TimeSpan.Zero),
				reportIdProvider: () => Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))
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

	private static MainWindow CreateStartupWindow(CommandLineOptions options, string appDataPath)
	{
		Directory.CreateDirectory(appDataPath);
		var services = AvaloniaCompositionRoot.CreateDefault(options, () => appDataPath);
		var window = new MainWindow(options, services)
		{
			Width = 1500,
			Height = 920
		};
		UiTestDriver.TrackTopLevelWindow(window);
		return window;
	}

	private static CommandLineOptions ParseValidOptions(params string[] args)
	{
		var result = CommandLineOptions.Parse(args);
		Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors.Select(static error => error.Message)));
		return result.Options;
	}

	private static string? GetCurrentPath(MainWindow window)
	{
		var field = typeof(MainWindow).GetField("_currentPath", BindingFlags.Instance | BindingFlags.NonPublic);
		return Assert.IsType<string>(field?.GetValue(window));
	}

	private static string GetComparablePath(string? path)
	{
		Assert.False(string.IsNullOrWhiteSpace(path));
		var fullPath = Path.GetFullPath(path);
		return NormalizeMacOsPrivateVarAlias(fullPath);
	}

	private static string NormalizeMacOsPrivateVarAlias(string value)
	{
		const string privateVarPrefix = "/private/var/";
		const string varPrefix = "/var/";

		// macOS can surface the same temp directory as either /var/... or /private/var/... in UI/runtime paths.
		// Titles include the path after the app prefix, so normalize every path occurrence, not only the start.
		return OperatingSystem.IsMacOS()
			? value.Replace(privateVarPrefix, varPrefix, StringComparison.Ordinal)
			: value;
	}
}
