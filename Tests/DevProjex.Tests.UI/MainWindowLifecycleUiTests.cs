using System.Reflection;
using Avalonia.Media;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Application.Compression;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Git;
using DevProjex.Infrastructure.ThemePresets;
using DevProjex.Kernel.Abstractions;
using DevProjex.Terminal.DesktopControl;

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
	public async Task ClosingWindow_WithPublishedDesktopServer_CompletesTeardownBeforeClosedReturns()
	{
		var appDataPath = Path.Combine(Path.GetTempPath(), "DevProjexTests", Guid.NewGuid().ToString("N"));
		var paths = new DesktopControlPaths(() => appDataPath);
		Directory.CreateDirectory(appDataPath);
		var services = AvaloniaCompositionRoot.CreateDefault(
			DesktopStartupOptions.Default,
			() => appDataPath) with
		{
			DesktopControlServerFactory = (handler, projectPath, cancellationToken) =>
				DesktopControlServer.StartAsync(handler, projectPath, paths, cancellationToken)
		};
		var window = new MainWindow(DesktopStartupOptions.Default, services);
		UiTestDriver.TrackTopLevelWindow(window);
		bool? shutdownCompletedAtClosed = null;
		window.Closed += (_, _) =>
			shutdownCompletedAtClosed = window.ShutdownCompletion.IsCompletedSuccessfully;

		try
		{
			window.Show();
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => Directory.Exists(paths.RegistryDirectory) &&
				      Directory.EnumerateFiles(paths.RegistryDirectory, "*.json").Any(),
				"desktop control server publication",
				TimeSpan.FromSeconds(2));

			window.Close();
			await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(2));

			Assert.True(shutdownCompletedAtClosed);
			Assert.Null(GetPrivateFieldValue(window, new OwnedField(null, "_desktopControlServer")));
			Assert.False(Directory.Exists(paths.RegistryDirectory) &&
			             Directory.EnumerateFiles(paths.RegistryDirectory, "*.json").Any());
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
	public async Task ClosingWindow_BeforeDesktopServerPublication_DisposesLateServer()
	{
		var appDataPath = Path.Combine(Path.GetTempPath(), "DevProjexTests", Guid.NewGuid().ToString("N"));
		var paths = new DesktopControlPaths(() => appDataPath);
		var serverStarted = new TaskCompletionSource<DesktopControlServer>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var releasePublication = new TaskCompletionSource(
			TaskCreationOptions.RunContinuationsAsynchronously);
		DesktopControlServer? startedServer = null;
		Directory.CreateDirectory(appDataPath);

		var options = DesktopStartupOptions.Default;
		var services = AvaloniaCompositionRoot.CreateDefault(options, () => appDataPath) with
		{
			DesktopControlServerFactory = async (handler, projectPath, _) =>
			{
				var server = await DesktopControlServer.StartAsync(
					handler,
					projectPath,
					paths,
					CancellationToken.None);
				startedServer = server;
				serverStarted.TrySetResult(server);
				await releasePublication.Task;
				return server;
			}
		};
		var window = new MainWindow(options, services);
		UiTestDriver.TrackTopLevelWindow(window);

		try
		{
			window.Show();
			_ = await serverStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
			Assert.Single(Directory.EnumerateFiles(paths.RegistryDirectory, "*.json"));

			window.Close();
			await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(2));
			releasePublication.TrySetResult();

			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !Directory.Exists(paths.RegistryDirectory) ||
				      !Directory.EnumerateFiles(paths.RegistryDirectory, "*.json").Any(),
				"late desktop control server to be disposed",
				TimeSpan.FromSeconds(2));
			Assert.Null(GetPrivateFieldValue(window, new OwnedField(null, "_desktopControlServer")));
		}
		finally
		{
			releasePublication.TrySetResult();
			if (window.IsVisible)
				await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
			if (startedServer is not null)
				await startedServer.DisposeAsync();

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
	public async Task ClosingWindow_WaitsForGitCloneCancellationCleanupBeforeTeardown()
	{
		using var project = UiTestProject.CreateDefault();
		var git = new BlockingGitRepositoryService(BlockingGitOperation.Clone);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			configureServices: services => services with { GitRepositoryService = git });
		GitCloneWindow? cloneWindow = null;
		bool? operationExitedAtClosed = null;
		window.Closed += (_, _) => operationExitedAtClosed = git.Exited.Task.IsCompleted;

		try
		{
			cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			UiTestDriver.GetViewModel(window).GitCloneUrl = "https://example.test/repository.git";
			await UiTestDriver.RaiseButtonClickAsync(
				Assert.IsType<Button>(cloneWindow.FindControl<Button>("StartCloneButton")));
			await git.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

			window.Close();
			await git.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

			Assert.False(window.ShutdownCompletion.IsCompleted);
			Assert.True(window.IsVisible);

			git.ReleaseCleanup.TrySetResult();
			await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(2));

			Assert.True(git.Exited.Task.IsCompletedSuccessfully);
			Assert.True(operationExitedAtClosed);
			Assert.False(UiTestDriver.GetViewModel(window).GitCloneInProgress);
		}
		finally
		{
			git.ReleaseCleanup.TrySetResult();
			if (cloneWindow is not null)
				await UiTestDriver.CloseTopLevelWindowAsync(cloneWindow);
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task ClosingWindow_WaitsForGitCloneCacheCatalogBeforeTeardown()
	{
		using var project = UiTestProject.CreateDefault();
		BlockingRepoCacheService? cache = null;
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			configureServices: services => services with
			{
				RepoCacheService = cache = new BlockingRepoCacheService(services.RepoCacheService)
			});
		GitCloneWindow? cloneWindow = null;
		bool? catalogExitedAtClosed = null;
		window.Closed += (_, _) => catalogExitedAtClosed = cache!.Exited.Task.IsCompleted;

		try
		{
			cache!.Arm();
			cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			await cache.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
			var catalogCts = Assert.IsType<CancellationTokenSource>(GetPrivateFieldValue(
				window,
				new OwnedField(null, "_gitCloneCatalogCts")));

			window.Close();

			Assert.False(window.ShutdownCompletion.IsCompleted);
			Assert.True(window.IsVisible);
			Assert.True(catalogCts.IsCancellationRequested);
			Assert.Null(Record.Exception(catalogCts.Cancel));

			cache.Release();
			await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(2));

			Assert.True(cache.Exited.Task.IsCompletedSuccessfully);
			Assert.True(catalogExitedAtClosed);
		}
		finally
		{
			cache?.Release();
			if (cloneWindow is not null)
				await UiTestDriver.CloseTopLevelWindowAsync(cloneWindow);
			await UiTestDriver.CloseWindowAsync(window);
			cache?.Dispose();
		}
	}

	[AvaloniaTheory]
	[InlineData(BlockingGitOperation.Update)]
	[InlineData(BlockingGitOperation.Branch)]
	public async Task ClosingWindow_WaitsForRepositoryGitCancellationCleanupBeforeTeardown(
		BlockingGitOperation operation)
	{
		using var project = UiTestProject.CreateDefault();
		var git = new BlockingGitRepositoryService(operation);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			configureServices: services => services with { GitRepositoryService = git },
			projectSourceType: ProjectSourceType.GitClone,
			managedClonePath: project.RootPath,
			repositoryUrl: "https://example.test/repository.git");
		bool? operationExitedAtClosed = null;
		window.Closed += (_, _) => operationExitedAtClosed = git.Exited.Task.IsCompleted;

		try
		{
			StartRepositoryGitOperation(window, operation);
			await git.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

			window.Close();
			await git.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

			Assert.False(window.ShutdownCompletion.IsCompleted);
			Assert.True(window.IsVisible);

			git.ReleaseCleanup.TrySetResult();
			await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(2));

			Assert.True(git.Exited.Task.IsCompletedSuccessfully);
			Assert.True(operationExitedAtClosed);
		}
		finally
		{
			git.ReleaseCleanup.TrySetResult();
			await UiTestDriver.CloseWindowAsync(window);
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

	private static void StartRepositoryGitOperation(MainWindow window, BlockingGitOperation operation)
	{
		if (operation == BlockingGitOperation.Update)
		{
			var updateMethod = typeof(MainWindow).GetMethod(
				"GetGitUpdatesAsync",
				BindingFlags.Instance | BindingFlags.NonPublic);
			Assert.NotNull(updateMethod);
			Assert.IsAssignableFrom<Task>(updateMethod!.Invoke(window, null));
			return;
		}

		var branchMethod = typeof(MainWindow).GetMethod(
			"OnGitBranchSwitch",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(branchMethod);
		branchMethod!.Invoke(window, [window, "feature"]);
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

	private sealed class BlockingRepoCacheService(IRepoCacheService inner) : IRepoCacheService, IDisposable
	{
		private readonly ManualResetEventSlim _release = new(initialState: false);
		private int _armed;

		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public string CacheRootPath => inner.CacheRootPath;
		public IReadOnlyList<string> CacheSearchRootPaths => inner.CacheSearchRootPaths;

		public void Arm() => Volatile.Write(ref _armed, 1);

		public void Release() => _release.Set();

		public string CreateRepositoryDirectory(string repositoryUrl) =>
			inner.CreateRepositoryDirectory(repositoryUrl);

		public string CreateRepositoryStagingDirectory(string repositoryUrl) =>
			inner.CreateRepositoryStagingDirectory(repositoryUrl);

		public string PublishRepositoryDirectory(string stagingPath, string repositoryUrl) =>
			inner.PublishRepositoryDirectory(stagingPath, repositoryUrl);

		public RepositoryCacheIndexEntry? FindIndexedRepository(string repositoryUrl) =>
			inner.FindIndexedRepository(repositoryUrl);

		public IReadOnlyList<RepositoryCacheCatalogEntry> ListIndexedRepositories()
		{
			if (Interlocked.Exchange(ref _armed, 0) != 1)
				return inner.ListIndexedRepositories();

			Started.TrySetResult();
			try
			{
				_release.Wait();
				return inner.ListIndexedRepositories();
			}
			finally
			{
				Exited.TrySetResult();
			}
		}

		public RepositoryCacheManagementListResult ListCacheEntriesForManagement() =>
			inner.ListCacheEntriesForManagement();

		public Task<IRepositoryCacheSession?> TryAcquireRepositorySessionAsync(
			string repositoryUrl,
			string? branch = null,
			CancellationToken cancellationToken = default) =>
			inner.TryAcquireRepositorySessionAsync(repositoryUrl, branch, cancellationToken);

		public Task<IRepositoryCacheSession?> TryAcquireRepositorySessionByPathAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) =>
			inner.TryAcquireRepositorySessionByPathAsync(repositoryPath, cancellationToken);

		public Task<IAsyncDisposable> AcquireRepositoryOperationAsync(
			string repositoryUrl,
			CancellationToken cancellationToken = default) =>
			inner.AcquireRepositoryOperationAsync(repositoryUrl, cancellationToken);

		public void RecordIndexedRepository(
			string repositoryUrl,
			string localPath,
			string? branch = null,
			string? commitHash = null,
			RepositoryCacheEntryState state = RepositoryCacheEntryState.Ready) =>
			inner.RecordIndexedRepository(repositoryUrl, localPath, branch, commitHash, state);

		public void RemoveIndexedRepository(string localPath) =>
			inner.RemoveIndexedRepository(localPath);

		public void DeleteRepositoryDirectory(string path) =>
			inner.DeleteRepositoryDirectory(path);

		public void ClearAllCache() => inner.ClearAllCache();

		public CacheClearResult ClearAllCacheWithResult() => inner.ClearAllCacheWithResult();

		public CacheClearResult RemoveCachedRepositoryWithResult(string repositoryUrl) =>
			inner.RemoveCachedRepositoryWithResult(repositoryUrl);

		public void CleanupStaleCacheOnStartup() => inner.CleanupStaleCacheOnStartup();

		public void CollectGarbage() => inner.CollectGarbage();

		public void RequestGarbageCollection() => inner.RequestGarbageCollection();

		public void RefreshIndexedRepositorySize(string localPath) =>
			inner.RefreshIndexedRepositorySize(localPath);

		public bool IsInCache(string path) => inner.IsInCache(path);

		public bool PathsBelongToSameRepository(string left, string right) =>
			inner.PathsBelongToSameRepository(left, right);

		public void Dispose()
		{
			_release.Set();
			_release.Dispose();
		}
	}

	public enum BlockingGitOperation
	{
		Clone,
		Update,
		Branch
	}

	private sealed class BlockingGitRepositoryService(BlockingGitOperation operation) : IGitRepositoryService
	{
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) =>
			Task.FromResult(true);

		public async Task<GitCloneResult> CloneAsync(
			string url,
			string targetDirectory,
			IProgress<string>? progress = null,
			CancellationToken cancellationToken = default)
		{
			Assert.Equal(BlockingGitOperation.Clone, operation);
			await WaitForCancellationAndCleanupAsync(cancellationToken);
			throw new UnreachableException();
		}

		public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyList<GitBranch>>(
			[
				new("main", IsActive: true, IsRemote: false),
				new("feature", IsActive: false, IsRemote: false)
			]);

		public Task<string?> GetDefaultBranchAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) =>
			Task.FromResult<string?>("main");

		public async Task<bool> SwitchBranchAsync(
			string repositoryPath,
			string branchName,
			IProgress<string>? progress = null,
			CancellationToken cancellationToken = default)
		{
			Assert.Equal(BlockingGitOperation.Branch, operation);
			await WaitForCancellationAndCleanupAsync(cancellationToken);
			throw new UnreachableException();
		}

		public async Task<bool> PullUpdatesAsync(
			string repositoryPath,
			IProgress<string>? progress = null,
			CancellationToken cancellationToken = default)
		{
			Assert.Equal(BlockingGitOperation.Update, operation);
			await WaitForCancellationAndCleanupAsync(cancellationToken);
			throw new UnreachableException();
		}

		public Task<string?> GetHeadCommitAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) =>
			Task.FromResult<string?>("head");

		public Task<string?> GetCurrentBranchAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) =>
			Task.FromResult<string?>("main");

		public Task<string?> GetRemoteUrlAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) =>
			Task.FromResult<string?>("https://example.test/repository.git");

		private async Task WaitForCancellationAndCleanupAsync(CancellationToken cancellationToken)
		{
			Started.TrySetResult();
			try
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				CancellationObserved.TrySetResult();
				await ReleaseCleanup.Task;
				throw;
			}
			finally
			{
				Exited.TrySetResult();
			}
		}
	}
}
