using System.Reflection;
using System.Security;
using System.Text.Json;
using DevProjex.Application.Context;
using DevProjex.Application.UseCases;
using DevProjex.Infrastructure.FileSystem;
using DevProjex.Infrastructure.RecentProjects;
using DevProjex.Infrastructure.ThemePresets;
using DevProjex.Kernel.Abstractions;
using DevProjex.Terminal.DesktopControl;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowStartupAutomationUiTests
{
	[AvaloniaFact]
	public async Task OpenFolder_OlderBlockedRootProbeCannotReplaceNewerOpenedProject()
	{
		using var olderProject = UiTestProject.CreateDefault();
		using var newerProject = UiTestProject.CreateDefault();
		using var scanner = new BlockingRootProbeScanner(olderProject.RootPath);
		var appDataPath = Path.Combine(olderProject.AppDataPath, Guid.NewGuid().ToString("N"));
		var options = DesktopStartupOptions.Default;
		var services = AvaloniaCompositionRoot.CreateDefault(options, () => appDataPath) with
		{
			ScanOptionsUseCase = new ScanOptionsUseCase(scanner)
		};
		var window = new MainWindow(options, services);
		UiTestDriver.TrackTopLevelWindow(window);

		try
		{
			window.Show();
			var olderOpen = Assert.IsAssignableFrom<Task<bool>>(
				await UiTestDriver.BeginOpenFolderAsync(window, olderProject.RootPath));
			await scanner.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

			await UiTestDriver.OpenFolderAsync(window, newerProject.RootPath);
			Assert.Equal(GetComparablePath(newerProject.RootPath), GetComparablePath(GetCurrentPath(window)));

			scanner.Release();
			Assert.False(await olderOpen.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
			Assert.Equal(GetComparablePath(newerProject.RootPath), GetComparablePath(GetCurrentPath(window)));
		}
		finally
		{
			scanner.Release();
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}
	}

	[AvaloniaFact]
	public async Task OpenFolder_NewerFailedPreflightDoesNotSupersedeOlderEligibleProject()
	{
		using var olderProject = UiTestProject.CreateDefault();
		using var scanner = new BlockingRootProbeScanner(olderProject.RootPath);
		var appDataPath = Path.Combine(olderProject.AppDataPath, Guid.NewGuid().ToString("N"));
		var options = DesktopStartupOptions.Default;
		var services = AvaloniaCompositionRoot.CreateDefault(options, () => appDataPath) with
		{
			ScanOptionsUseCase = new ScanOptionsUseCase(scanner)
		};
		var window = new MainWindow(options, services);
		UiTestDriver.TrackTopLevelWindow(window);
		Window? dialog = null;

		try
		{
			window.Show();
			var olderOpen = Assert.IsAssignableFrom<Task<bool>>(
				await UiTestDriver.BeginOpenFolderAsync(window, olderProject.RootPath));
			await scanner.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

			var missingPath = Path.Combine(olderProject.RootPath, "missing-project");
			var newerOpen = Assert.IsAssignableFrom<Task<bool>>(
				await UiTestDriver.BeginOpenFolderAsync(window, missingPath));
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.OwnedWindows.Count == 1,
				"missing-project error dialog to open");
			dialog = Assert.Single(window.OwnedWindows);
			await UiTestDriver.CloseTopLevelWindowAsync(dialog);
			dialog = null;
			Assert.False(await newerOpen.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

			scanner.Release();
			Assert.True(await olderOpen.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
			Assert.Equal(GetComparablePath(olderProject.RootPath), GetComparablePath(GetCurrentPath(window)));
		}
		finally
		{
			scanner.Release();
			if (dialog is not null)
				await UiTestDriver.CloseTopLevelWindowAsync(dialog);
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}
	}

	[AvaloniaFact]
	public async Task OpenFolder_OlderAccessFailureCannotPromptOrElevateAfterNewerProjectOpens()
	{
		using var olderProject = UiTestProject.CreateDefault();
		using var newerProject = UiTestProject.CreateDefault();
		using var scanner = new BlockingRootProbeScanner(olderProject.RootPath, canReadBlockedRoot: false);
		var elevation = new RecordingElevationService();
		var appDataPath = Path.Combine(olderProject.AppDataPath, Guid.NewGuid().ToString("N"));
		var options = DesktopStartupOptions.Default;
		var services = AvaloniaCompositionRoot.CreateDefault(options, () => appDataPath) with
		{
			ScanOptionsUseCase = new ScanOptionsUseCase(scanner),
			Elevation = elevation
		};
		var window = new MainWindow(options, services);
		UiTestDriver.TrackTopLevelWindow(window);

		try
		{
			window.Show();
			var olderOpen = Assert.IsAssignableFrom<Task<bool>>(
				await UiTestDriver.BeginOpenFolderAsync(window, olderProject.RootPath));
			await scanner.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

			await UiTestDriver.OpenFolderAsync(window, newerProject.RootPath);
			scanner.Release();
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => olderOpen.IsCompleted || elevation.RelaunchCount != 0,
				"older open to finish or request elevation",
				TimeSpan.FromSeconds(5));
			Assert.Equal(0, elevation.RelaunchCount);
			Assert.False(await olderOpen.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
			Assert.Equal(GetComparablePath(newerProject.RootPath), GetComparablePath(GetCurrentPath(window)));
			Assert.Empty(window.OwnedWindows);
		}
		finally
		{
			scanner.Release();
			foreach (var dialog in window.OwnedWindows.ToArray())
				await UiTestDriver.CloseTopLevelWindowAsync(dialog);
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}
	}

	[AvaloniaFact]
	public async Task OpenFolder_ClosedWindowRejectsLateRootAccessFailureWithoutElevation()
	{
		using var project = UiTestProject.CreateDefault();
		using var scanner = new BlockingRootProbeScanner(project.RootPath, canReadBlockedRoot: false);
		var elevation = new RecordingElevationService();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var options = DesktopStartupOptions.Default;
		var services = AvaloniaCompositionRoot.CreateDefault(options, () => appDataPath) with
		{
			ScanOptionsUseCase = new ScanOptionsUseCase(scanner),
			Elevation = elevation
		};
		var window = new MainWindow(options, services);
		UiTestDriver.TrackTopLevelWindow(window);

		try
		{
			window.Show();
			var openTask = Assert.IsAssignableFrom<Task<bool>>(
				await UiTestDriver.BeginOpenFolderAsync(window, project.RootPath));
			await scanner.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

			window.Close();
			await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
			scanner.Release();
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => openTask.IsCompleted || elevation.RelaunchCount != 0,
				"closed-window open to finish or request elevation",
				TimeSpan.FromSeconds(5));
			Assert.Equal(0, elevation.RelaunchCount);
			Assert.False(await openTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
			Assert.Empty(window.OwnedWindows);
		}
		finally
		{
			scanner.Release();
			foreach (var dialog in window.OwnedWindows.ToArray())
				await UiTestDriver.CloseTopLevelWindowAsync(dialog);
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}
	}

	[AvaloniaFact]
	public async Task StartupUi_AgentActivityPreferenceStorageFailureDoesNotBlockRequestedProject()
	{
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var options = new DesktopStartupOptions(
			new DesktopOpenRequest(ProjectPath: project.RootPath, Language: AppLanguage.En));
		var services = AvaloniaCompositionRoot.CreateDefault(options, () => appDataPath) with
		{
			AgentActivityPreferenceStore = new AgentActivityPreferenceStore(
				() => throw new SecurityException("Agent activity preference storage is unavailable."))
		};
		var window = new MainWindow(options, services);
		UiTestDriver.TrackTopLevelWindow(window);

		try
		{
			window.Show();
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => UiTestDriver.GetViewModel(window).IsProjectLoaded,
				"requested project to load despite agent activity preference storage failure");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}
	}

	[AvaloniaFact]
	public async Task StartupUi_DesktopControlStorageFailureDoesNotBlockRequestedProject()
	{
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(appDataPath);
		var options = new DesktopStartupOptions(
			new DesktopOpenRequest(ProjectPath: project.RootPath, Language: AppLanguage.En));
		var services = AvaloniaCompositionRoot.CreateDefault(options, () => appDataPath) with
		{
			DesktopControlServerFactory = (_, _, _) =>
				Task.FromException<DevProjex.Terminal.DesktopControl.DesktopControlServer>(
					new IOException("Desktop control storage is unavailable."))
		};
		var window = new MainWindow(options, services);
		UiTestDriver.TrackTopLevelWindow(window);

		try
		{
			window.Show();
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => UiTestDriver.GetViewModel(window).IsProjectLoaded,
				"requested project to load despite desktop control storage failure");
			Assert.Equal(GetComparablePath(project.RootPath), GetComparablePath(GetCurrentPath(window)));
			var startupError = typeof(MainWindow).GetField(
				"_desktopStartupErrorCode",
				BindingFlags.Instance | BindingFlags.NonPublic);
			Assert.Equal("DPX-DESKTOP-STARTUP-FAILED", startupError?.GetValue(window));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}
	}

	[AvaloniaTheory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DesktopOpen_RegistryWriteFailureDoesNotFailAppliedRequest(bool alreadyLoaded)
	{
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var unavailableRoot = Path.Combine(project.AppDataPath, "unavailable-registry-root");
		File.WriteAllText(unavailableRoot, string.Empty);
		var registryRoot = appDataPath;
		var paths = new DesktopControlPaths(() => registryRoot);
		var options = DesktopStartupOptions.Default;
		var services = AvaloniaCompositionRoot.CreateDefault(options, () => appDataPath) with
		{
			DesktopControlServerFactory = (handler, projectPath, cancellationToken) =>
				DesktopControlServer.StartAsync(handler, projectPath, paths, cancellationToken)
		};
		var window = new MainWindow(options, services);
		UiTestDriver.TrackTopLevelWindow(window);

		try
		{
			window.Show();
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => GetDesktopControlServer(window) is not null,
				"Desktop control server to publish before the registry becomes unavailable");
			if (alreadyLoaded)
			{
				var initialOpen = await InvokeDesktopInteractionAsync(
					window,
					new DesktopOpenProjectRequest(new DesktopOpenRequest(project.RootPath)));
				Assert.True(initialOpen.Success, initialOpen.ErrorCode ?? "Initial project open failed.");
			}

			registryRoot = unavailableRoot;
			var result = await InvokeDesktopInteractionAsync(
				window,
				new DesktopOpenProjectRequest(
					new DesktopOpenRequest(project.RootPath, Language: AppLanguage.Ru)));

			Assert.True(result.Success, result.ErrorCode ?? "Desktop open request failed.");
			Assert.NotNull(result.State);
			Assert.True(Assert.IsType<bool>(result.State["projectLoaded"]));
			Assert.Equal(
				GetComparablePath(project.RootPath),
				GetComparablePath(Assert.IsType<string>(result.State["projectPath"])));
			Assert.True(UiTestDriver.GetViewModel(window).IsProjectLoaded);
			Assert.Equal(GetComparablePath(project.RootPath), GetComparablePath(GetCurrentPath(window)));
			Assert.Equal(AppLanguage.Ru, services.Localization.CurrentLanguage);
		}
		finally
		{
			registryRoot = appDataPath;
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}
	}

	[AvaloniaFact]
	public async Task DesktopOpen_MissingProjectStillReturnsOpenFailure()
	{
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var window = CreateStartupWindow(DesktopStartupOptions.Default, appDataPath);
		Window? dialog = null;

		try
		{
			window.Show();
			var missingPath = Path.Combine(project.RootPath, "missing-project");
			var requestTask = InvokeDesktopInteractionAsync(
				window,
				new DesktopOpenProjectRequest(new DesktopOpenRequest(missingPath)));
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.OwnedWindows.Count == 1,
				"missing-project error dialog to open");
			dialog = Assert.Single(window.OwnedWindows);
			await UiTestDriver.CloseTopLevelWindowAsync(dialog);
			dialog = null;

			var result = await requestTask;
			Assert.False(result.Success);
			Assert.Equal("DPX-DESKTOP-PROJECT-OPEN-FAILED", result.ErrorCode);
			Assert.False(UiTestDriver.GetViewModel(window).IsProjectLoaded);
		}
		finally
		{
			if (dialog?.IsVisible == true)
				await UiTestDriver.CloseTopLevelWindowAsync(dialog);
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}
	}

	[AvaloniaFact]
	public async Task StartupUi_ExplicitEmptySelectionUnchecksEveryTreeNode()
	{
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var options = new DesktopStartupOptions(
			new DesktopOpenRequest(
				ProjectPath: project.RootPath,
				Selection: new ProjectSelectionSpec(SelectedPaths: []),
				Language: AppLanguage.En));
		var window = CreateStartupWindow(options, appDataPath);

		try
		{
			window.Show();
			await UiTestDriver.WaitForConditionAsync(
				window,
				() =>
				{
					var viewModel = UiTestDriver.GetViewModel(window);
					return viewModel.IsProjectLoaded &&
					       viewModel.TreeNodes.Count > 0 &&
					       viewModel.TreeNodes.All(static node => node.IsChecked == false);
				},
				"explicit empty Desktop selection to uncheck the project tree");

			Assert.All(
				UiTestDriver.GetViewModel(window).TreeNodes,
				static node => Assert.False(node.IsChecked));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}
	}

	[AvaloniaFact]
	public async Task DesktopOpenLanguage_IsSessionScopedWhileGuiLanguageActionPersistsPreference()
	{
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var settingsStore = new UserSettingsStore(() => appDataPath);
		Assert.True(settingsStore.TrySave(new UserSettingsDb
		{
			ViewSettings = new AppViewSettings
			{
				PreferredLanguage = AppLanguage.En
			}
		}));

		var startupOptions = new DesktopStartupOptions(
			new DesktopOpenRequest(project.RootPath, Language: AppLanguage.En));
		var services = AvaloniaCompositionRoot.CreateDefault(startupOptions, () => appDataPath);
		var window = new MainWindow(startupOptions, services)
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
				() => UiTestDriver.GetViewModel(window).IsProjectLoaded,
				"startup project to load before applying a Desktop open request");

			var result = await InvokeDesktopInteractionAsync(
				window,
				new DesktopOpenProjectRequest(
					new DesktopOpenRequest(project.RootPath, Language: AppLanguage.Ru)));

			Assert.True(result.Success, result.ErrorCode ?? "Desktop open request failed.");
			Assert.Equal(AppLanguage.Ru, services.Localization.CurrentLanguage);
			Assert.Equal(
				AppLanguage.En,
				new UserSettingsStore(() => appDataPath).Load().ViewSettings.PreferredLanguage);

			InvokeGuiLanguageAction(window, "OnLangDe");

			Assert.Equal(AppLanguage.De, services.Localization.CurrentLanguage);
			Assert.Equal(
				AppLanguage.De,
				new UserSettingsStore(() => appDataPath).Load().ViewSettings.PreferredLanguage);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}
	}

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
	public async Task StartupUi_LastRetriesRecentHistoryAfterConstructorLockContention()
	{
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var recentFolder = Path.Combine(project.RootPath, "history", "recent");
		Directory.CreateDirectory(recentFolder);
		var recentStore = new RecentProjectsStore(() => appDataPath);
		var db = recentStore.Load();
		Assert.Single(recentStore.AddFolder(db, recentFolder).RecentFolders);

		var options = new DesktopStartupOptions(
			new DesktopOpenRequest(UseLastProject: true, Language: AppLanguage.En));
		var lockPath = recentStore.GetPath() + ".lock";
		MainWindow window;
		using (var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
		{
			window = CreateStartupWindow(options, appDataPath);
		}

		try
		{
			window.Show();
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => UiTestDriver.GetViewModel(window).IsProjectLoaded,
				"saved recent project to load after the constructor lock is released");
			Assert.Equal(GetComparablePath(recentFolder), GetComparablePath(GetCurrentPath(window)));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}
	}

	[AvaloniaFact]
	public async Task DesktopOpen_LastRetriesRecentHistoryAfterDeferredLockContention()
	{
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var recentFolder = Path.Combine(project.RootPath, "history", "recent");
		Directory.CreateDirectory(recentFolder);
		var recentStore = new RecentProjectsStore(() => appDataPath);
		var db = recentStore.Load();
		Assert.Single(recentStore.AddFolder(db, recentFolder).RecentFolders);

		var window = CreateStartupWindow(DesktopStartupOptions.Default, appDataPath);
		try
		{
			var request = new DesktopOpenProjectRequest(new DesktopOpenRequest(UseLastProject: true));
			var lockPath = recentStore.GetPath() + ".lock";
			using (var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
			{
				window.Show();
				var unavailable = await InvokeDesktopInteractionAsync(window, request);
				Assert.False(unavailable.Success);
				Assert.Equal("DPX-DESKTOP-NO-RECENT-PROJECT", unavailable.ErrorCode);
			}

			var retried = await InvokeDesktopInteractionAsync(window, request);
			Assert.True(retried.Success, retried.ErrorCode ?? "Recent project did not open after lock release.");
			Assert.Equal(GetComparablePath(recentFolder), GetComparablePath(GetCurrentPath(window)));
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
	public async Task StartupTrackedOverrideWithoutRepository_RemainsVisibleAndGuardsEveryPreviewCopyPath()
	{
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var options = new DesktopStartupOptions(
			new DesktopOpenRequest(
				ProjectPath: project.RootPath,
				OpenPreview: true,
				Selection: new ProjectSelectionSpec(
					GitMode: GitFilteringMode.TrackedFilesOnly,
					Exclusions: []),
				Language: AppLanguage.En));
		var window = CreateStartupWindow(options, appDataPath);

		try
		{
			window.Show();
			await UiTestDriver.WaitForPreviewReadyAsync(window);

			var viewModel = UiTestDriver.GetViewModel(window);
			var trackedOption = Assert.Single(
				viewModel.IgnoreOptions,
				static option => option.Id == IgnoreOptionId.TrackedGitFilesOnly);
			Assert.True(trackedOption.IsChecked);
			Assert.Contains(
				IgnoreOptionId.TrackedGitFilesOnly,
				UiTestDriver.GetSelectedIgnoreOptionIds(window));

			var diagnostic = Assert.IsType<ContextDiagnostic>(
				UiTestDriver.GetAppliedGitReadinessDiagnostic(window, project.RootPath));
			Assert.Equal(ProjectContextGitReadiness.UnavailableDiagnosticCode, diagnostic.Code);
			Assert.Equal(ContextDiagnosticSeverity.Error, diagnostic.Severity);

			var clipboardSentinel = $"tracked-preview-guard-{Guid.NewGuid():N}";
			await UiTestDriver.SetClipboardTextAsync(window, clipboardSentinel);
			await UiTestDriver.ClickPreviewCopyButtonAsync(window);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.Equal(clipboardSentinel, await UiTestDriver.GetClipboardTextAsync(window));

			var previewTextControl =
				UiTestDriver.GetRequiredControl<DevProjex.Avalonia.Controls.VirtualizedPreviewTextControl>(
					window,
					"PreviewTextControl");
			previewTextControl.SelectAll();
			Assert.True(previewTextControl.HasSelection);
			var copySelectionMethod = typeof(DevProjex.Avalonia.Controls.VirtualizedPreviewTextControl)
				.GetMethod("CopySelectionToClipboardAsync", BindingFlags.Instance | BindingFlags.NonPublic);
			Assert.NotNull(copySelectionMethod);
			var copyTask = Assert.IsAssignableFrom<Task>(copySelectionMethod.Invoke(previewTextControl, null));
			await copyTask;

			Assert.Equal(clipboardSentinel, await UiTestDriver.GetClipboardTextAsync(window));
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

	private static async Task<DesktopInteractionResult> InvokeDesktopInteractionAsync(
		MainWindow window,
		DesktopInteractionRequest request)
	{
		var method = typeof(MainWindow).GetMethod(
			"HandleDesktopInteractionAsync",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		var invocation = method.Invoke(window, [request, CancellationToken.None]);
		return await Assert.IsAssignableFrom<Task<DesktopInteractionResult>>(invocation);
	}

	private static void InvokeGuiLanguageAction(MainWindow window, string methodName)
	{
		var method = typeof(MainWindow).GetMethod(
			methodName,
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method.Invoke(window, [null, new global::Avalonia.Interactivity.RoutedEventArgs()]);
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

	private static DesktopControlServer? GetDesktopControlServer(MainWindow window)
	{
		var field = typeof(MainWindow).GetField("_desktopControlServer", BindingFlags.Instance | BindingFlags.NonPublic);
		return field?.GetValue(window) as DesktopControlServer;
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

	private sealed class BlockingRootProbeScanner(string blockedPath, bool canReadBlockedRoot = true)
		: IFileSystemScannerProjectWorkspaceScanner, IDisposable
	{
		private readonly FileSystemScanner _inner = new();
		private readonly ManualResetEventSlim _release = new(initialState: false);
		private readonly TaskCompletionSource _started =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task Started => _started.Task;

		public void Release() => _release.Set();

		public bool CanReadRoot(string rootPath)
		{
			if (PathComparer.Default.Equals(rootPath, blockedPath))
			{
				_started.TrySetResult();
				if (!_release.Wait(TimeSpan.FromSeconds(15)))
					throw new TimeoutException("The controlled root probe was not released.");
			}

			return (!PathComparer.Default.Equals(rootPath, blockedPath) || canReadBlockedRoot) &&
			       _inner.CanReadRoot(rootPath);
		}

		public ScanResult<HashSet<string>> GetExtensions(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) =>
			_inner.GetExtensions(rootPath, rules, cancellationToken);

		public ScanResult<HashSet<string>> GetRootFileExtensions(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) =>
			_inner.GetRootFileExtensions(rootPath, rules, cancellationToken);

		public ScanResult<List<string>> GetRootFolderNames(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) =>
			_inner.GetRootFolderNames(rootPath, rules, cancellationToken);

		public ScanResult<ProjectWorkspaceScanSnapshot> ScanProjectWorkspace(
			ProjectWorkspaceScanRequest request,
			CancellationToken cancellationToken = default) =>
			_inner.ScanProjectWorkspace(request, cancellationToken);

		public void Dispose() => _release.Dispose();
	}

	private sealed class RecordingElevationService : IElevationService
	{
		public bool IsAdministrator => false;
		public int RelaunchCount { get; private set; }

		public bool TryRelaunchAsAdministrator(IReadOnlyList<string> arguments)
		{
			RelaunchCount++;
			return false;
		}
	}
}
