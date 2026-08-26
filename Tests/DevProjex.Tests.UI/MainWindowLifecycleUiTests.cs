using System.Reflection;
using Avalonia.Media;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Application.Compression;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Tests.UI;

public sealed class MainWindowLifecycleUiTests
{
	private static readonly OwnedField[] OwnedCancellationSourceFields =
	[
		new("_previewSurfaceController", "_selectionMetricsCts"),
		new("_memoryCleanup", "_previewCleanupCts"),
		new("_memoryCleanup", "_backgroundCleanupCts"),
		new("_previewWorkspaceController", "_modeSwitchCts"),
		new(null, "_windowLifetimeCts"),
		new(null, "_projectOperationCts"),
		new(null, "_applySettingsCts"),
		new(null, "_gitCloneCts"),
		new(null, "_gitOperationCts")
	];

	[AvaloniaFact]
	public async Task StartupRevealGate_KeepsContentVisibleAndRevealsNativeBackdropBehindIt()
	{
		var appDataPath = Path.Combine(Path.GetTempPath(), "DevProjexTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(appDataPath);

		var options = DesktopStartupOptions.Default;
		var services = AvaloniaCompositionRoot.CreateDefault(options, () => appDataPath);
		var window = new MainWindow(options, services)
		{
			Width = 900,
			Height = 620
		};
		UiTestDriver.TrackTopLevelWindow(window);

		try
		{
			var cover = Assert.IsType<Border>(window.FindControl<Border>("StartupBackdropCover"));
			var revealGateActive = Assert.IsType<bool>(GetPrivateFieldValue(
				window,
				new OwnedField(null, "_startupRevealGateActive")));

			Assert.Equal(1.0, window.Opacity);
			Assert.Equal(revealGateActive, cover.IsVisible);
			Assert.Equal(revealGateActive ? 1.0 : 0.0, cover.Opacity);

			window.Show();

			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.Opacity >= 0.99 && cover.Opacity <= 0.01,
				"startup backdrop cover to reveal the native material");
			Assert.Equal(1.0, window.Opacity);
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

	[Theory]
	[InlineData(true, ThemeEffectMode.Acrylic, true)]
	[InlineData(true, ThemeEffectMode.Mica, true)]
	[InlineData(true, ThemeEffectMode.Solid, false)]
	[InlineData(true, ThemeEffectMode.Transparent, false)]
	[InlineData(false, ThemeEffectMode.Acrylic, false)]
	[InlineData(false, ThemeEffectMode.Mica, false)]
	public void StartupRevealGate_IsReservedForWindowsNativeBackdrops(
		bool isWindows,
		ThemeEffectMode effect,
		bool expected)
	{
		Assert.Equal(expected, MainWindow.ShouldUseStartupRevealGate(isWindows, effect));
	}

	[AvaloniaFact]
	public async Task Startup_LoadsOptionalFontCatalogAfterTheWindowBecomesVisible()
	{
		var appDataPath = Path.Combine(Path.GetTempPath(), "DevProjexTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(appDataPath);
		var options = DesktopStartupOptions.Default;
		var services = AvaloniaCompositionRoot.CreateDefault(options, () => appDataPath);
		var window = new MainWindow(options, services);
		UiTestDriver.TrackTopLevelWindow(window);

		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			Assert.Single(viewModel.FontFamilies);
			Assert.Equal(FontFamily.Default, viewModel.SelectedFontFamily);
			Assert.False(Assert.IsType<bool>(GetPrivateFieldValue(
				window,
				new OwnedField(null, "_fontCatalogLoaded"))));

			window.Show();
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => Assert.IsType<bool>(GetPrivateFieldValue(
					window,
					new OwnedField(null, "_fontCatalogLoaded"))),
				"optional font catalog to load at application idle");

			Assert.Equal(FontFamily.Default, viewModel.SelectedFontFamily);
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
		var redactionSession = Assert.IsType<SecretRedactionSession>(
			GetPrivateFieldValue(window, new OwnedField(null, "_secretRedactionSession")));
		var compressionSession = Assert.IsType<CodeCompressionSession>(
			GetPrivateFieldValue(window, new OwnedField(null, "_codeCompressionSession")));
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
			Assert.True(window.ShutdownCompletion.IsCompletedSuccessfully);

			foreach (var (fieldName, token) in tokensByField)
			{
				Assert.True(token.IsCancellationRequested, $"{fieldName} must be canceled during window shutdown.");
				var ownedField = OwnedCancellationSourceFields.Single(candidate =>
					candidate.DisplayName == fieldName);
				Assert.Null(GetPrivateFieldValue(window, ownedField));
			}

			Assert.False(debounceTimer.IsEnabled);
			Assert.True(GetPrivateBooleanField(redactionSession, "_disposed"));
			Assert.True(GetPrivateBooleanField(compressionSession, "_disposed"));
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

	[AvaloniaFact]
	public async Task ClosingWindow_CancelsPendingSearchAnimationAndFocusContinuation()
	{
		using var project = UiTestProject.CreateDefault();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		var controller = Assert.IsType<SearchFilterInteractionController>(
			GetPrivateFieldValue(
				window,
				new OwnedField(null, "_searchFilterController")));
		var searchBar = UiTestDriver.GetRequiredControl<SearchBarView>(
			window,
			"SearchBar");
		var searchBox = Assert.IsType<TextBox>(searchBar.SearchBoxControl);

		controller.ShowSearch();
		var closeTask = controller.CloseSearchAsync();
		await UiTestDriver.CloseWindowAsync(window);

		await closeTask.WaitAsync(TimeSpan.FromSeconds(2));
		await Task.Delay(
			UiTimingProfile.Scale(TimeSpan.FromMilliseconds(400)));
		Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);

		Assert.True(closeTask.IsCompletedSuccessfully);
		Assert.False(searchBox.IsFocused);
	}

	[AvaloniaFact]
	public async Task DisposedSearchController_IgnoresAlreadyPostedHotkeyToggle()
	{
		using var project = UiTestProject.CreateDefault();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		var controller = Assert.IsType<SearchFilterInteractionController>(
			GetPrivateFieldValue(
				window,
				new OwnedField(null, "_searchFilterController")));

		try
		{
			var scheduleMethod = typeof(SearchFilterInteractionController)
				.GetMethod(
					"ScheduleHotkeyToggle",
					BindingFlags.Instance | BindingFlags.NonPublic);
			Assert.NotNull(scheduleMethod);
			var toolKind = Enum.ToObject(
				scheduleMethod!.GetParameters()[0].ParameterType,
				0);
			scheduleMethod.Invoke(controller, [toolKind]);

			controller.Dispose();
			Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);

			Assert.False(UiTestDriver.GetViewModel(window).SearchVisible);
		}
		finally
		{
			if (window.IsVisible)
				await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task PointerWheel_CancelsPendingMemoryCleanup()
	{
		using var project = UiTestProject.CreateDefault();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		var coordinator = Assert.IsType<
			DevProjex.Avalonia.Coordinators.MemoryCleanupCoordinator>(
			GetPrivateFieldValue(
				window,
				new OwnedField(null, "_memoryCleanup")));

		try
		{
			coordinator.SchedulePreview(
				MemoryCleanupReason.PreviewClose);
			Assert.True(coordinator.IsCleanupPendingOrRunning);

			using var pointer = new global::Avalonia.Input.Pointer(
				global::Avalonia.Input.Pointer.GetNextFreeId(),
				PointerType.Mouse,
				isPrimary: true);
			window.RaiseEvent(new PointerWheelEventArgs(
				window,
				pointer,
				window,
				default,
				timestamp: 0,
				new PointerPointProperties(),
				KeyModifiers.None,
				new Vector(0, -1)));

			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !coordinator.IsCleanupPendingOrRunning,
				"pointer wheel interaction to cancel pending memory cleanup");
		}
		finally
		{
			if (window.IsVisible)
				await UiTestDriver.CloseWindowAsync(window);
		}
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

	private static bool GetPrivateBooleanField(object owner, string fieldName)
	{
		var field = owner.GetType().GetField(
			fieldName,
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		return Assert.IsType<bool>(field!.GetValue(owner));
	}

	private readonly record struct OwnedField(string? OwnerFieldName, string FieldName)
	{
		public string DisplayName =>
			OwnerFieldName is null ? FieldName : $"{OwnerFieldName}.{FieldName}";
	}
}
