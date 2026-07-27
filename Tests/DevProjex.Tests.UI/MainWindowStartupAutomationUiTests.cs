using System.Reflection;
using System.Text.Json;
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
			var options = DesktopStartupOptions.Default;
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

		var options = new DesktopStartupOptions(
			new DesktopOpenRequest(UseLastProject: true, Language: AppLanguage.En));
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

		var options = new DesktopStartupOptions(
			new DesktopOpenRequest(UseLastProject: true, Language: AppLanguage.En));
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
		var options = new DesktopStartupOptions(
			new DesktopOpenRequest(
				ProjectPath: project.RootPath,
				OpenPreview: true,
				PreviewView: DesktopPreviewView.TreeContent,
				TreeFormat: TreeTextFormat.Markdown,
				Language: AppLanguage.En));
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
		var options = new DesktopStartupOptions(
			new DesktopOpenRequest(
				ProjectPath: project.RootPath,
				OpenPreview: true,
				PreviewView: DesktopPreviewView.TreeContent,
				TreeFormat: TreeTextFormat.Xml,
				Language: AppLanguage.En));
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
		var options = new DesktopStartupOptions(
			new DesktopOpenRequest(
				ProjectPath: project.RootPath,
				Filter: "Services",
				Language: AppLanguage.En));
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
		var options = new DesktopStartupOptions(
			new DesktopOpenRequest(
				ProjectPath: project.RootPath,
				Filter: "Services",
				Language: AppLanguage.En));
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
		var options = new DesktopStartupOptions(
			new DesktopOpenRequest(
				ProjectPath: project.RootPath,
				OpenPreview: true,
				Search: "Preview",
				Language: AppLanguage.En));
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
	public async Task StartupSessionMetrics_LoadsProjectAndWritesPrivateReportOnClose()
	{
		using var project = UiTestProject.CreateDefault();
		const string privateSearchQuery = "PrivateSearchNeedle";
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, $"{privateSearchQuery}.txt"),
			"private search fixture",
			TestContext.Current.CancellationToken);
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var outputPath = Path.Combine(project.AppDataPath, "session-metrics", "session.json");
		var options = new DesktopStartupOptions(
			new DesktopOpenRequest(
				ProjectPath: project.RootPath,
				OpenPreview: true,
				Search: privateSearchQuery,
				Language: AppLanguage.En),
			new SessionMetricsOptions(true, project.RootPath, outputPath));
		var window = CreateStartupWindow(options, appDataPath);

		window.Show();
		await UiTestDriver.WaitForPreviewReadyAsync(window);
		await UiTestDriver.WaitForSearchAppliedAsync(window, privateSearchQuery);

		await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);

		Assert.True(File.Exists(outputPath));
		var json = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
		Assert.DoesNotContain(
			privateSearchQuery,
			json,
			StringComparison.Ordinal);
		using var document = JsonDocument.Parse(json);
		var root = document.RootElement;
		Assert.Equal("interactive-session", root.GetProperty("kind").GetString());
		Assert.Equal(GetComparablePath(project.RootPath).Replace('\\', '/'), root.GetProperty("targetPath").GetString());
		var events = root.GetProperty("events").EnumerateArray().ToArray();
		Assert.Contains(events, static item => item.GetProperty("name").GetString() == "session.started");
		Assert.Contains(events, static item => item.GetProperty("name").GetString() == "project.load");
		Assert.Contains(events, static item =>
			item.GetProperty("name").GetString() == "preview.mode.changed" &&
			item.GetProperty("previewVisible").GetBoolean());
		Assert.Contains(events, static item => item.GetProperty("name").GetString() == "tree.search");
	}

	[AvaloniaFact]
	public async Task StartupUiBenchmarkScript_RunsStandardScenarioAndWritesStepMetrics()
	{
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var outputPath = Path.Combine(project.AppDataPath, "session-metrics", "ui-benchmark-session.json");
		var options = new DesktopStartupOptions(
			new DesktopOpenRequest(ProjectPath: project.RootPath, Language: AppLanguage.En),
			new SessionMetricsOptions(true, project.RootPath, outputPath),
			DesktopDiagnosticScenario.Standard);
		var window = CreateStartupWindow(options, appDataPath);

		try
		{
			window.Show();
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !window.IsVisible && File.Exists(outputPath),
				"scripted UI benchmark to close the window and write the session report",
				TimeSpan.FromSeconds(45));

			Assert.True(File.Exists(outputPath), "Expected the scripted UI benchmark session report to be written.");
			var json = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
			using var document = JsonDocument.Parse(json);
			var events = document.RootElement.GetProperty("events").EnumerateArray().ToArray();
			Assert.Contains(events, static item => HasSuccessfulBenchmarkStep(item, "preview.open"));
			Assert.Contains(events, static item => HasSuccessfulBenchmarkStep(item, "tree-format.json"));
			Assert.Contains(events, static item => HasSuccessfulBenchmarkStep(item, "tree-format.xml"));
			Assert.Contains(events, static item => HasSuccessfulBenchmarkStep(item, "tree-format.md"));
			Assert.Contains(events, static item => HasSuccessfulBenchmarkStep(item, "search.apply"));
			Assert.Contains(events, static item => HasSuccessfulBenchmarkStep(item, "filter.apply"));
			Assert.Contains(events, static item => HasSuccessfulBenchmarkStep(item, "preview.close"));
			Assert.Contains(events, static item => item.GetProperty("name").GetString() == "tree.search");
			Assert.Contains(events, static item => item.GetProperty("name").GetString() == "tree.filter");

			var filterEvents = events
				.Where(static item => item.GetProperty("name").GetString() == "tree.filter")
				.ToArray();
			Assert.Equal(2, filterEvents.Length);
			Assert.Single(filterEvents, static item => item.GetProperty("queryLength").GetInt32() > 0);
			Assert.Single(filterEvents, static item => item.GetProperty("queryLength").GetInt32() == 0);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}
	}

	[AvaloniaFact]
	public async Task StartupUiBenchmarkScript_RepeatsPreviewSearchCloseCycle()
	{
		using var idleOverride = TemporaryEnvironmentVariable.Set(
			"DEVPROJEX_UI_BENCHMARK_IDLE_SECONDS",
			"1");
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var outputPath = Path.Combine(project.AppDataPath, "session-metrics", "search-retention-session.json");
		var options = new DesktopStartupOptions(
			new DesktopOpenRequest(ProjectPath: project.RootPath, Language: AppLanguage.En),
			new SessionMetricsOptions(true, project.RootPath, outputPath),
			DesktopDiagnosticScenario.PreviewSearchRetention);
		var window = CreateStartupWindow(options, appDataPath);

		try
		{
			window.Show();
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !window.IsVisible && File.Exists(outputPath),
				"repeated search-retention benchmark to close the window and write its report",
				TimeSpan.FromSeconds(45));

			var json = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
			using var document = JsonDocument.Parse(json);
			var events = document.RootElement.GetProperty("events").EnumerateArray().ToArray();
			Assert.Contains(events, static item => HasSuccessfulBenchmarkStep(item, "search.apply"));
			Assert.Contains(events, static item => HasSuccessfulBenchmarkStep(item, "search.close"));
			Assert.Contains(events, static item => HasSuccessfulBenchmarkStep(item, "search-cycle.idle-settle"));
			Assert.Contains(events, static item => HasSuccessfulBenchmarkStep(item, "search.apply.repeat"));
			Assert.Contains(events, static item => HasSuccessfulBenchmarkStep(item, "search.close.repeat"));
			Assert.Contains(events, static item => HasSuccessfulBenchmarkStep(item, "search-cycle.repeat.idle-settle"));

			var searchEvents = events
				.Where(static item => item.GetProperty("name").GetString() == "tree.search")
				.ToArray();
			Assert.Equal(4, searchEvents.Length);
			Assert.Equal(2, searchEvents.Count(static item => item.GetProperty("queryLength").GetInt32() > 0));
			Assert.Equal(2, searchEvents.Count(static item => item.GetProperty("queryLength").GetInt32() == 0));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}
	}

	private static MainWindow CreateStartupWindow(DesktopStartupOptions options, string appDataPath)
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

	private static bool HasSuccessfulBenchmarkStep(JsonElement item, string stepName)
	{
		return item.GetProperty("name").GetString() == "ui.benchmark.step" &&
		       item.GetProperty("stepName").GetString() == stepName &&
		       item.GetProperty("success").GetBoolean() &&
		       item.GetProperty("durationMilliseconds").GetDouble() > 0;
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

	private sealed class TemporaryEnvironmentVariable : IDisposable
	{
		private readonly string _name;
		private readonly string? _previousValue;

		private TemporaryEnvironmentVariable(string name, string value)
		{
			_name = name;
			_previousValue = Environment.GetEnvironmentVariable(name);
			Environment.SetEnvironmentVariable(name, value);
		}

		public static TemporaryEnvironmentVariable Set(string name, string value) => new(name, value);

		public void Dispose() => Environment.SetEnvironmentVariable(_name, _previousValue);
	}
}
