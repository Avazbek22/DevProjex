using System.Reflection;

namespace DevProjex.Tests.UI;

public sealed class MainWindowLifecycleUiTests
{
	private static readonly OwnedField[] OwnedCancellationSourceFields =
	[
		new("_previewSurfaceController", "_selectionMetricsCts"),
		new("_memoryCleanup", "_previewCleanupCts"),
		new("_memoryCleanup", "_searchCleanupCts"),
		new("_memoryCleanup", "_backgroundCleanupCts"),
		new("_previewWorkspaceController", "_modeSwitchCts"),
		new(null, "_windowLifetimeCts"),
		new(null, "_projectOperationCts"),
		new(null, "_applySettingsCts"),
		new(null, "_gitCloneCts"),
		new(null, "_gitOperationCts")
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
			foreach (var ownedField in OwnedCancellationSourceFields)
			{
				var source = new CancellationTokenSource();
				sources.Add(source);
				tokensByField[ownedField.DisplayName] = source.Token;
				SetPrivateField(window, ownedField, source);
			}

			SetPrivateField(
				window,
				new OwnedField("_previewSurfaceController", "_selectionMetricsDebounceTimer"),
				debounceTimer);

			await UiTestDriver.CloseWindowAsync(window);

			foreach (var (fieldName, token) in tokensByField)
			{
				Assert.True(token.IsCancellationRequested, $"{fieldName} must be canceled during window shutdown.");
				var ownedField = OwnedCancellationSourceFields.Single(candidate =>
					candidate.DisplayName == fieldName);
				Assert.Null(GetPrivateFieldValue(window, ownedField));
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
			viewModel.BackgroundTransparency = 85;
			viewModel.PanelContrast = 80;
			viewModel.BorderVisibility = 75;
			viewModel.MenuTransparency = 70;
			viewModel.StatusBusy = true;
			viewModel.StatusProgressValue = 42;
			viewModel.SelectedExportFormat = ExportFormat.Json;
		});

		Assert.Null(exception);
	}

	private static void SetPrivateField(MainWindow window, OwnedField ownedField, object? value)
	{
		var owner = GetOwner(window, ownedField);
		var field = owner.GetType().GetField(
			ownedField.FieldName,
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		field!.SetValue(owner, value);
	}

	private static object? GetPrivateFieldValue(MainWindow window, OwnedField ownedField)
	{
		var owner = GetOwner(window, ownedField);
		var field = owner.GetType().GetField(
			ownedField.FieldName,
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		return field!.GetValue(owner);
	}

	private static object GetOwner(MainWindow window, OwnedField ownedField)
	{
		if (ownedField.OwnerFieldName is null)
			return window;

		var ownerField = typeof(MainWindow).GetField(
			ownedField.OwnerFieldName,
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(ownerField);
		return Assert.IsAssignableFrom<object>(ownerField!.GetValue(window));
	}

	private readonly record struct OwnedField(string? OwnerFieldName, string FieldName)
	{
		public string DisplayName =>
			OwnerFieldName is null ? FieldName : $"{OwnerFieldName}.{FieldName}";
	}
}
