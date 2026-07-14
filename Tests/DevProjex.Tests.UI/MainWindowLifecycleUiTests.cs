using System.Reflection;

namespace DevProjex.Tests.UI;

public sealed class MainWindowLifecycleUiTests
{
	private static readonly string[] OwnedCancellationSourceFields =
	[
		"_previewSelectionMetricsCts",
		"_previewMemoryCleanupCts",
		"_searchMemoryCleanupCts",
		"_backgroundMemoryCleanupCts",
		"_previewModeSwitchCts",
		"_windowLifetimeCts",
		"_projectOperationCts",
		"_gitCloneCts",
		"_gitOperationCts"
	];

	[AvaloniaFact]
	public async Task StartupRevealGate_RestoresWindowOpacityAfterInitialRenderFrames()
	{
		var appDataPath = Path.Combine(Path.GetTempPath(), "DevProjexTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(appDataPath);

		var options = CommandLineOptions.Empty;
		var services = AvaloniaCompositionRoot.CreateDefault(options, () => appDataPath);
		var window = new MainWindow(options, services)
		{
			Width = 900,
			Height = 620
		};
		UiTestDriver.TrackTopLevelWindow(window);

		try
		{
			var expectedInitialOpacity = MainWindow.ShouldUseStartupRevealGate() ? 0.0 : 1.0;
			Assert.Equal(expectedInitialOpacity, window.Opacity);

			window.Show();

			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.Opacity >= 0.99,
				"startup reveal gate to restore the window opacity");
		}
		finally
		{
			if (window.IsVisible)
				await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);

			try
			{
				Directory.Delete(appDataPath, recursive: true);
			}
			catch
			{
				// Best effort test cleanup only.
			}
		}
	}

	[AvaloniaFact]
	public async Task ClosingWindow_CancelsAndClearsOwnedOperationsAndStopsDebounceTimer()
	{
		using var project = UiTestProject.CreateDefault();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		var tokensByField = new Dictionary<string, CancellationToken>();
		var sources = new List<CancellationTokenSource>();
		var debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
		debounceTimer.Start();

		try
		{
			foreach (var fieldName in OwnedCancellationSourceFields)
			{
				var source = new CancellationTokenSource();
				sources.Add(source);
				tokensByField[fieldName] = source.Token;
				SetPrivateField(window, fieldName, source);
			}

			SetPrivateField(window, "_previewSelectionMetricsDebounceTimer", debounceTimer);

			await UiTestDriver.CloseWindowAsync(window);

			foreach (var (fieldName, token) in tokensByField)
			{
				Assert.True(token.IsCancellationRequested, $"{fieldName} must be canceled during window shutdown.");
				Assert.Null(GetPrivateFieldValue(window, fieldName));
			}

			Assert.False(debounceTimer.IsEnabled);
		}
		finally
		{
			if (window.IsVisible)
				await UiTestDriver.CloseWindowAsync(window);

			foreach (var source in sources)
				source.Dispose();
		}
	}

	[AvaloniaFact]
	public async Task ClosingWindow_DetachesViewModelHandlersSoLateChangesDoNotReachDisposedCoordinators()
	{
		using var project = UiTestProject.CreateDefault();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		var viewModel = UiTestDriver.GetViewModel(window);

		await UiTestDriver.CloseWindowAsync(window);

		var exception = Record.Exception(() =>
		{
			viewModel.SearchQuery = "late search";
			viewModel.NameFilter = "late filter";
			viewModel.MaterialIntensity = 85;
			viewModel.PanelContrast = 80;
			viewModel.BorderStrength = 75;
			viewModel.MenuChildIntensity = 70;
			viewModel.BlurRadius = 65;
			viewModel.StatusBusy = true;
			viewModel.StatusProgressValue = 42;
			viewModel.SelectedExportFormat = ExportFormat.Json;
		});

		Assert.Null(exception);
	}

	private static void SetPrivateField(MainWindow window, string fieldName, object? value)
	{
		var field = typeof(MainWindow).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		field!.SetValue(window, value);
	}

	private static object? GetPrivateFieldValue(MainWindow window, string fieldName)
	{
		var field = typeof(MainWindow).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		return field!.GetValue(window);
	}
}
