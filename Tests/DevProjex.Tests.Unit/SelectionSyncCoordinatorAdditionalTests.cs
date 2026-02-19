namespace DevProjex.Tests.Unit;

public sealed class SelectionSyncCoordinatorAdditionalTests
{
	[Fact]
	public void HandleRootAllChanged_ChecksAllRootFolderOptions()
	{
		var viewModel = CreateViewModel();
		viewModel.RootFolders.Add(new SelectionOptionViewModel("src", false));
		viewModel.RootFolders.Add(new SelectionOptionViewModel("tests", false));

		var coordinator = CreateCoordinator(viewModel);

		coordinator.HandleRootAllChanged(true, currentPath: null);

		Assert.True(viewModel.AllRootFoldersChecked);
		Assert.All(viewModel.RootFolders, option => Assert.True(option.IsChecked));
	}

	[Fact]
	public void HandleExtensionsAllChanged_ChecksAllExtensionOptions()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", false));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".md", false));

		var coordinator = CreateCoordinator(viewModel);

		coordinator.HandleExtensionsAllChanged(true);

		Assert.True(viewModel.AllExtensionsChecked);
		Assert.All(viewModel.Extensions, option => Assert.True(option.IsChecked));
	}

	[Fact]
	public void HandleIgnoreAllChanged_ChecksAllIgnoreOptions()
	{
		var viewModel = CreateViewModel();
		viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.HiddenFolders, "hidden folders", false));
		viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.DotFolders, "dot folders", false));

		var coordinator = CreateCoordinator(viewModel);

		coordinator.HandleIgnoreAllChanged(true, currentPath: null);

		Assert.True(viewModel.AllIgnoreChecked);
		Assert.All(viewModel.IgnoreOptions, option => Assert.True(option.IsChecked));
	}

	[Fact]
	public async Task PopulateExtensionsForRootSelectionAsync_EmptyPath_DoesNotChangeExtensions()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));

		var coordinator = CreateCoordinator(viewModel);

		await coordinator.PopulateExtensionsForRootSelectionAsync(string.Empty, new List<string> { "src" });

		Assert.Single(viewModel.Extensions);
		Assert.Equal(".cs", viewModel.Extensions[0].Name);
	}

	[Fact]
	public async Task PopulateRootFoldersAsync_EmptyPath_DoesNotChangeRootFolders()
	{
		var viewModel = CreateViewModel();
		viewModel.RootFolders.Add(new SelectionOptionViewModel("src", true));

		var coordinator = CreateCoordinator(viewModel);

		await coordinator.PopulateRootFoldersAsync(string.Empty);

		Assert.Single(viewModel.RootFolders);
		Assert.Equal("src", viewModel.RootFolders[0].Name);
	}

	[Fact]
	public async Task UpdateLiveOptionsFromRootSelectionAsync_EmptyPath_DoesNotChangeOptions()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
		viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.HiddenFolders, "hidden folders", true));

		var coordinator = CreateCoordinator(viewModel);

		await coordinator.UpdateLiveOptionsFromRootSelectionAsync(null);

		Assert.Single(viewModel.Extensions);
		Assert.Single(viewModel.IgnoreOptions);
	}

	[Fact]
	public void PopulateExtensionsForRootSelectionAsync_DoesNotDropCachedSelections()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", false));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".md", true));

		var coordinator = CreateCoordinator(viewModel);
		coordinator.UpdateExtensionsSelectionCache();

		coordinator.ApplyExtensionScan(new[] { ".cs" });
		coordinator.ApplyExtensionScan(new[] { ".cs", ".md" });

		var md = viewModel.Extensions.Single(option => option.Name == ".md");
		Assert.True(md.IsChecked);
	}

	[Fact]
	public void PopulateExtensionsForRootSelectionAsync_EmptyRoots_DoesNotClearCachedSelections()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", false));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".md", true));

		var coordinator = CreateCoordinator(viewModel);
		coordinator.UpdateExtensionsSelectionCache();

		coordinator.ApplyExtensionScan(Array.Empty<string>());
		coordinator.ApplyExtensionScan(new[] { ".cs", ".md" });

		var md = viewModel.Extensions.Single(option => option.Name == ".md");
		Assert.True(md.IsChecked);
	}

	[Fact]
	public void ApplyExtensionScan_UpdatesExtensionsFromScanResults()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".old", true));

		var coordinator = CreateCoordinator(viewModel);

		coordinator.ApplyExtensionScan(new[] { ".cs", ".md", ".root" });

		var names = viewModel.Extensions.Select(option => option.Name).ToList();
		Assert.Contains(".root", names);
		Assert.Contains(".cs", names);
		Assert.Contains(".md", names);
		Assert.DoesNotContain(".old", names);
	}

	[Fact]
	public void ApplyExtensionScan_PreservesCachedExtensionSelections()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".md", true));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".txt", false));
		viewModel.AllExtensionsChecked = false;

		var coordinator = CreateCoordinator(viewModel);
		coordinator.UpdateExtensionsSelectionCache();

		coordinator.ApplyExtensionScan(new[] { ".md", ".txt" });

		var md = viewModel.Extensions.Single(option => option.Name == ".md");
		var txt = viewModel.Extensions.Single(option => option.Name == ".txt");
		Assert.True(md.IsChecked);
		Assert.False(txt.IsChecked);
	}

	[Fact]
	public void ApplyExtensionScan_WhenCacheNotInitialized_RestoresDefaultCheckedState()
	{
		var viewModel = CreateViewModel();
		viewModel.AllExtensionsChecked = false;
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".md", false));

		var coordinator = CreateCoordinator(viewModel);
		coordinator.ResetProjectProfileSelections("C:\\ProjectB");

		coordinator.ApplyExtensionScan(new[] { ".cs", ".json" });

		Assert.All(viewModel.Extensions, option => Assert.True(option.IsChecked));
	}

	[Fact]
	public void ApplyExtensionScan_EmptyScan_ClearsExtensionsAndAllFlag()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
		viewModel.AllExtensionsChecked = true;

		var coordinator = CreateCoordinator(viewModel);

		coordinator.ApplyExtensionScan(Array.Empty<string>());

		Assert.Empty(viewModel.Extensions);
		Assert.False(viewModel.AllExtensionsChecked);
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_EmptyRoots_StillPopulatesIgnoreOptions()
	{
		// After Problem 1 fix: even with empty folder selection, we still scan root files
		// so ignore options should be populated, not cleared
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);

		coordinator.PopulateIgnoreOptionsForRootSelection(Array.Empty<string>());

		// Ignore options are populated for root-level files
		Assert.NotEmpty(viewModel.IgnoreOptions);
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_PreservesIgnoreSelections()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		coordinator.PopulateIgnoreOptionsForRootSelection(new[] { "src" });
		coordinator.HandleIgnoreAllChanged(false, currentPath: null);
		viewModel.IgnoreOptions[0].IsChecked = true;
		viewModel.IgnoreOptions[1].IsChecked = false;
		coordinator.UpdateIgnoreSelectionCache();

		coordinator.PopulateIgnoreOptionsForRootSelection(new[] { "src" });

		var hiddenFolders = viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.HiddenFolders);
		var hiddenFiles = viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.HiddenFiles);
		Assert.True(hiddenFolders.IsChecked);
		Assert.False(hiddenFiles.IsChecked);
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_WhenGitIgnoreExists_AddsUseGitIgnoreOption()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), $"devprojex-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempRoot);
		File.WriteAllText(Path.Combine(tempRoot, ".gitignore"), "bin/");
		try
		{
			var viewModel = CreateViewModel();
			var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
			var scanner = new StubFileSystemScanner();
			var scanOptions = new ScanOptionsUseCase(scanner);
			var coordinator = new SelectionSyncCoordinator(
				viewModel,
				scanOptions,
				new FilterOptionSelectionService(),
				new IgnoreOptionsService(localization),
				_ => new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
				_ => false,
				() => tempRoot);

			coordinator.PopulateIgnoreOptionsForRootSelection(new[] { "src" }, tempRoot);

			Assert.Contains(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.UseGitIgnore);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_WhenGitIgnoreMissing_DoesNotAddUseGitIgnoreOption()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), $"devprojex-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempRoot);
		try
		{
			var viewModel = CreateViewModel();
			var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
			var scanner = new StubFileSystemScanner();
			var scanOptions = new ScanOptionsUseCase(scanner);
			var coordinator = new SelectionSyncCoordinator(
				viewModel,
				scanOptions,
				new FilterOptionSelectionService(),
				new IgnoreOptionsService(localization),
				_ => new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
				_ => false,
				() => tempRoot);

			coordinator.PopulateIgnoreOptionsForRootSelection(new[] { "src" }, tempRoot);

			Assert.DoesNotContain(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.UseGitIgnore);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void ApplyProjectProfileSelections_SetsIgnoreSelectionCache()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: Array.Empty<string>(),
			SelectedExtensions: Array.Empty<string>(),
			SelectedIgnoreOptions: new[] { IgnoreOptionId.DotFiles, IgnoreOptionId.HiddenFiles });

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		var selected = coordinator.GetSelectedIgnoreOptionIds();

		Assert.Equal(2, selected.Count);
		Assert.Contains(IgnoreOptionId.DotFiles, selected);
		Assert.Contains(IgnoreOptionId.HiddenFiles, selected);
	}

	[Fact]
	public void ApplyProjectProfileSelections_PreservesExtensionSelectionInNextScan()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: Array.Empty<string>(),
			SelectedExtensions: new[] { ".md" },
			SelectedIgnoreOptions: Array.Empty<IgnoreOptionId>());

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		coordinator.ApplyExtensionScan(new[] { ".cs", ".md" });

		Assert.False(viewModel.Extensions.Single(option => option.Name == ".cs").IsChecked);
		Assert.True(viewModel.Extensions.Single(option => option.Name == ".md").IsChecked);
	}

	[Fact]
	public void ApplyProjectProfileSelections_PreventsAllExtensionsOverride()
	{
		var viewModel = CreateViewModel();
		// Intentionally keep default AllExtensionsChecked=true to verify fix.
		var coordinator = CreateCoordinator(viewModel);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: Array.Empty<string>(),
			SelectedExtensions: new[] { ".md" },
			SelectedIgnoreOptions: Array.Empty<IgnoreOptionId>());

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		coordinator.ApplyExtensionScan(new[] { ".cs", ".md", ".json" });

		Assert.False(viewModel.AllExtensionsChecked);
		Assert.False(viewModel.Extensions.Single(option => option.Name == ".cs").IsChecked);
		Assert.True(viewModel.Extensions.Single(option => option.Name == ".md").IsChecked);
		Assert.False(viewModel.Extensions.Single(option => option.Name == ".json").IsChecked);
	}

	[Fact]
	public void ApplyProjectProfileSelections_MissingSavedExtensions_FallsBackToDefaultsForAvailable()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: Array.Empty<string>(),
			SelectedExtensions: new[] { ".removed-ext" },
			SelectedIgnoreOptions: Array.Empty<IgnoreOptionId>());

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		coordinator.ApplyExtensionScan(new[] { ".cs", ".json" });

		Assert.True(viewModel.Extensions.Single(option => option.Name == ".cs").IsChecked);
		Assert.True(viewModel.Extensions.Single(option => option.Name == ".json").IsChecked);
		Assert.True(viewModel.AllExtensionsChecked);
	}

	[Fact]
	public void ApplyProjectProfileSelections_EmptySavedExtensions_StillKeepsAllUnchecked()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: Array.Empty<string>(),
			SelectedExtensions: Array.Empty<string>(),
			SelectedIgnoreOptions: Array.Empty<IgnoreOptionId>());

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		coordinator.ApplyExtensionScan(new[] { ".cs", ".json" });

		Assert.False(viewModel.Extensions.Single(option => option.Name == ".cs").IsChecked);
		Assert.False(viewModel.Extensions.Single(option => option.Name == ".json").IsChecked);
		Assert.False(viewModel.AllExtensionsChecked);
	}

	[Fact]
	public void ApplyProjectProfileSelections_DoesNotForceAllTogglesToFalse()
	{
		var viewModel = CreateViewModel();
		viewModel.AllRootFoldersChecked = true;
		viewModel.AllExtensionsChecked = true;
		viewModel.AllIgnoreChecked = true;
		var coordinator = CreateCoordinator(viewModel);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: new[] { "src" },
			SelectedExtensions: new[] { ".cs" },
			SelectedIgnoreOptions: new[] { IgnoreOptionId.DotFiles });

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);

		Assert.True(viewModel.AllRootFoldersChecked);
		Assert.True(viewModel.AllExtensionsChecked);
		Assert.True(viewModel.AllIgnoreChecked);
	}

	[Fact]
	public void ApplyProjectProfileSelections_MissingSavedIgnoreOptions_FallsBackToVisibleDefaults()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: Array.Empty<string>(),
			SelectedExtensions: Array.Empty<string>(),
			SelectedIgnoreOptions: new[] { IgnoreOptionId.UseGitIgnore });

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		coordinator.PopulateIgnoreOptionsForRootSelection(new[] { "src" }, "C:\\ProjectA");

		var hiddenFolders = viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.HiddenFolders);
		var hiddenFiles = viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.HiddenFiles);
		var dotFolders = viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.DotFolders);
		var dotFiles = viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.DotFiles);
		Assert.True(hiddenFolders.IsChecked);
		Assert.True(hiddenFiles.IsChecked);
		Assert.True(dotFolders.IsChecked);
		Assert.True(dotFiles.IsChecked);
		Assert.True(viewModel.AllIgnoreChecked);
	}

	[Fact]
	public void ApplyProjectProfileSelections_EmptySavedIgnoreOptions_StillKeepsAllUnchecked()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: Array.Empty<string>(),
			SelectedExtensions: Array.Empty<string>(),
			SelectedIgnoreOptions: Array.Empty<IgnoreOptionId>());

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		coordinator.PopulateIgnoreOptionsForRootSelection(new[] { "src" }, "C:\\ProjectA");

		Assert.All(viewModel.IgnoreOptions, option => Assert.False(option.IsChecked));
		Assert.False(viewModel.AllIgnoreChecked);
	}

	[Fact]
	public void ResetProjectProfileSelections_ClearsAppliedExtensionCache_AndRestoresDefaults()
	{
		var viewModel = CreateViewModel();
		viewModel.AllExtensionsChecked = false;
		var coordinator = CreateCoordinator(viewModel);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: Array.Empty<string>(),
			SelectedExtensions: new[] { ".md" },
			SelectedIgnoreOptions: Array.Empty<IgnoreOptionId>());

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		coordinator.ResetProjectProfileSelections("C:\\ProjectB");
		coordinator.ApplyExtensionScan(new[] { ".cs", ".md" });

		Assert.True(viewModel.Extensions.Single(option => option.Name == ".cs").IsChecked);
		Assert.True(viewModel.Extensions.Single(option => option.Name == ".md").IsChecked);
	}

	private static MainWindowViewModel CreateViewModel()
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		return new MainWindowViewModel(localization, new HelpContentProvider());
	}

private static SelectionSyncCoordinator CreateCoordinator(
	MainWindowViewModel viewModel,
	StubFileSystemScanner? scanner = null,
	Func<string?>? currentPathProvider = null)
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		scanner ??= new StubFileSystemScanner();
		var scanOptions = new ScanOptionsUseCase(scanner);
		var filterService = new FilterOptionSelectionService();
		var ignoreService = new IgnoreOptionsService(localization);
		Func<string, IgnoreRules> buildIgnoreRules = _ => new IgnoreRules(false,
			false,
			false,
			false,
			new HashSet<string>(),
			new HashSet<string>());

		return new SelectionSyncCoordinator(
			viewModel,
			scanOptions,
			filterService,
			ignoreService,
			buildIgnoreRules,
			_ => false,
			currentPathProvider ?? (() => null));
	}

	[Fact]
	public void ShouldClearCachesForCurrentPath_WithPreparedTargetProfile_ReturnsFalse()
	{
		var coordinator = CreateCoordinator(CreateViewModel());
		SetPrivateField(coordinator, "_lastLoadedPath", "C:\\ProjectA");
		SetPrivateField(coordinator, "_preparedSelectionPath", "C:\\ProjectB");

		var result = InvokeShouldClearCachesForCurrentPath(coordinator, "C:\\ProjectB");

		Assert.False(result);
	}

	[Fact]
	public void ShouldClearCachesForCurrentPath_PathSwitchWithoutPreparedProfile_ReturnsTrue()
	{
		var coordinator = CreateCoordinator(CreateViewModel());
		SetPrivateField(coordinator, "_lastLoadedPath", "C:\\ProjectA");
		SetPrivateField(coordinator, "_preparedSelectionPath", null);

		var result = InvokeShouldClearCachesForCurrentPath(coordinator, "C:\\ProjectB");

		Assert.True(result);
	}

	[Fact]
	public void ShouldClearCachesForCurrentPath_NoLastLoadedPath_ReturnsFalse()
	{
		var coordinator = CreateCoordinator(CreateViewModel());
		SetPrivateField(coordinator, "_lastLoadedPath", null);
		SetPrivateField(coordinator, "_preparedSelectionPath", null);

		var result = InvokeShouldClearCachesForCurrentPath(coordinator, "C:\\ProjectA");

		Assert.False(result);
	}

	[Fact]
	public void ShouldClearCachesForCurrentPath_SamePath_ReturnsFalse()
	{
		var coordinator = CreateCoordinator(CreateViewModel());
		SetPrivateField(coordinator, "_lastLoadedPath", "C:\\ProjectA");
		SetPrivateField(coordinator, "_preparedSelectionPath", null);

		var result = InvokeShouldClearCachesForCurrentPath(coordinator, "C:\\ProjectA");

		Assert.False(result);
	}

	[Fact]
	public void ShouldClearCachesForCurrentPath_PreparedPathForAnotherProject_ReturnsTrue()
	{
		var coordinator = CreateCoordinator(CreateViewModel());
		SetPrivateField(coordinator, "_lastLoadedPath", "C:\\ProjectA");
		SetPrivateField(coordinator, "_preparedSelectionPath", "C:\\ProjectC");

		var result = InvokeShouldClearCachesForCurrentPath(coordinator, "C:\\ProjectB");

		Assert.True(result);
	}

	[Fact]
	public void ShouldClearCachesForCurrentPath_PreparedPathCaseDifference_UsesPlatformComparer()
	{
		var coordinator = CreateCoordinator(CreateViewModel());
		SetPrivateField(coordinator, "_lastLoadedPath", "C:\\ProjectA");
		SetPrivateField(coordinator, "_preparedSelectionPath", "c:\\projectb");

		var result = InvokeShouldClearCachesForCurrentPath(coordinator, "C:\\ProjectB");

		Assert.Equal(!OperatingSystem.IsWindows(), result);
	}

	[Fact]
	public void ShouldSkipRefreshForPreparedPath_PreparedForAnotherProject_ReturnsTrue()
	{
		var coordinator = CreateCoordinator(CreateViewModel());
		SetPrivateField(coordinator, "_preparedSelectionPath", "C:\\TargetProject");

		var shouldSkip = InvokeShouldSkipRefreshForPreparedPath(coordinator, "C:\\AnotherProject");

		Assert.True(shouldSkip);
	}

	[Fact]
	public void ShouldSkipRefreshForPreparedPath_PreparedForCurrentProject_ReturnsFalse()
	{
		var coordinator = CreateCoordinator(CreateViewModel());
		SetPrivateField(coordinator, "_preparedSelectionPath", "C:\\TargetProject");

		var shouldSkip = InvokeShouldSkipRefreshForPreparedPath(coordinator, "C:\\TargetProject");

		Assert.False(shouldSkip);
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_WhenPathIsStale_DoesNotMutateOptions()
	{
		var viewModel = CreateViewModel();
		var currentPath = "C:\\ProjectB";
		var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => currentPath);

		coordinator.PopulateIgnoreOptionsForRootSelection(Array.Empty<string>(), "C:\\ProjectA");

		Assert.Empty(viewModel.IgnoreOptions);
	}

	[Fact]
	public async Task PopulateRootFoldersAsync_WhenPathIsStale_DoesNotMutateRootOptions()
	{
		var viewModel = CreateViewModel();
		var currentPath = "C:\\ProjectB";
		var scanner = new StubFileSystemScanner
		{
			GetRootFolderNamesHandler = (_, _) => new ScanResult<List<string>>(
				new List<string> { "src", "tests" },
				false,
				false)
		};
		var coordinator = CreateCoordinator(viewModel, scanner, () => currentPath);

		await coordinator.PopulateRootFoldersAsync("C:\\ProjectA");

		Assert.Empty(viewModel.RootFolders);
	}

	[Fact]
	public async Task PopulateExtensionsForRootSelectionAsync_WhenPathIsStale_DoesNotMutateExtensionOptions()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".keep", true));
		var currentPath = "C:\\ProjectB";
		var scanner = new StubFileSystemScanner
		{
			GetRootFileExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".json" },
				false,
				false)
		};
		var coordinator = CreateCoordinator(viewModel, scanner, () => currentPath);

		await coordinator.PopulateExtensionsForRootSelectionAsync("C:\\ProjectA", Array.Empty<string>());

		Assert.Single(viewModel.Extensions);
		Assert.Equal(".keep", viewModel.Extensions[0].Name);
		Assert.True(viewModel.Extensions[0].IsChecked);
	}

	[Fact]
	public void ApplyProjectProfileSelections_EmptyExtensions_StillInitializesExtensionCache()
	{
		var coordinator = CreateCoordinator(CreateViewModel());
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: Array.Empty<string>(),
			SelectedExtensions: Array.Empty<string>(),
			SelectedIgnoreOptions: Array.Empty<IgnoreOptionId>());

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);

		var initialized = GetPrivateBoolField(coordinator, "_extensionsSelectionInitialized");
		var cached = GetPrivateHashSetField(coordinator, "_extensionsSelectionCache");
		Assert.True(initialized);
		Assert.Empty(cached);
	}

	[Fact]
	public void ResetProjectProfileSelections_StoresPreparedPathForTargetProject()
	{
		var coordinator = CreateCoordinator(CreateViewModel());

		coordinator.ResetProjectProfileSelections("C:\\ProjectB");

		var prepared = GetPrivateStringField(coordinator, "_preparedSelectionPath");
		Assert.Equal("C:\\ProjectB", prepared);
	}

	[Fact]
	public void ResetProjectProfileSelections_RestoresAllTogglesToDefaults()
	{
		var viewModel = CreateViewModel();
		viewModel.AllRootFoldersChecked = false;
		viewModel.AllExtensionsChecked = false;
		viewModel.AllIgnoreChecked = false;
		var coordinator = CreateCoordinator(viewModel);

		coordinator.ResetProjectProfileSelections("C:\\ProjectB");

		Assert.True(viewModel.AllRootFoldersChecked);
		Assert.True(viewModel.AllExtensionsChecked);
		Assert.True(viewModel.AllIgnoreChecked);
	}

	[Fact]
	public void ApplyProjectProfileSelections_PreventsAllIgnoreOverride()
	{
		var viewModel = CreateViewModel();
		// Intentionally keep default AllIgnoreChecked=true to verify fix.
		var coordinator = CreateCoordinator(viewModel);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: Array.Empty<string>(),
			SelectedExtensions: Array.Empty<string>(),
			SelectedIgnoreOptions: new[] { IgnoreOptionId.DotFiles });

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		coordinator.PopulateIgnoreOptionsForRootSelection(new[] { "src" });

		Assert.False(viewModel.AllIgnoreChecked);
		Assert.Contains(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.DotFiles && option.IsChecked);
		Assert.Contains(viewModel.IgnoreOptions, option => option.Id != IgnoreOptionId.DotFiles && !option.IsChecked);
	}

	private static StubLocalizationCatalog CreateCatalog()
	{
		var data = new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Settings.Ignore.SmartIgnore"] = "Smart ignore",
				["Settings.Ignore.UseGitIgnore"] = "Use .gitignore",
				["Settings.Ignore.HiddenFolders"] = "Hidden folders",
				["Settings.Ignore.HiddenFiles"] = "Hidden files",
				["Settings.Ignore.DotFolders"] = "dot folders",
				["Settings.Ignore.DotFiles"] = "dot files"
			}
		};

		return new StubLocalizationCatalog(data);
	}

	private static bool InvokeShouldClearCachesForCurrentPath(SelectionSyncCoordinator coordinator, string path)
	{
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"ShouldClearCachesForCurrentPath",
			BindingFlags.NonPublic | BindingFlags.Instance);
		Assert.NotNull(method);
		return (bool)method.Invoke(coordinator, new object[] { path })!;
	}

	private static bool InvokeShouldSkipRefreshForPreparedPath(SelectionSyncCoordinator coordinator, string path)
	{
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"ShouldSkipRefreshForPreparedPath",
			BindingFlags.NonPublic | BindingFlags.Instance);
		Assert.NotNull(method);
		return (bool)method.Invoke(coordinator, new object[] { path })!;
	}

	private static void SetPrivateField(SelectionSyncCoordinator coordinator, string fieldName, string? value)
	{
		var field = typeof(SelectionSyncCoordinator).GetField(
			fieldName,
			BindingFlags.NonPublic | BindingFlags.Instance);
		Assert.NotNull(field);
		field.SetValue(coordinator, value);
	}

	private static bool GetPrivateBoolField(SelectionSyncCoordinator coordinator, string fieldName)
	{
		var field = typeof(SelectionSyncCoordinator).GetField(
			fieldName,
			BindingFlags.NonPublic | BindingFlags.Instance);
		Assert.NotNull(field);
		return (bool)field.GetValue(coordinator)!;
	}

	private static string? GetPrivateStringField(SelectionSyncCoordinator coordinator, string fieldName)
	{
		var field = typeof(SelectionSyncCoordinator).GetField(
			fieldName,
			BindingFlags.NonPublic | BindingFlags.Instance);
		Assert.NotNull(field);
		return (string?)field.GetValue(coordinator);
	}

	private static HashSet<string> GetPrivateHashSetField(SelectionSyncCoordinator coordinator, string fieldName)
	{
		var field = typeof(SelectionSyncCoordinator).GetField(
			fieldName,
			BindingFlags.NonPublic | BindingFlags.Instance);
		Assert.NotNull(field);
		return (HashSet<string>)field.GetValue(coordinator)!;
	}
}

