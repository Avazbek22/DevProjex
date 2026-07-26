using DevProjex.Application.Models;
using DevProjex.Avalonia.Collections;

namespace DevProjex.Tests.Unit;

[Collection("AvaloniaUI")]
public sealed class SelectionSyncCoordinatorAdditionalTests
{
	[Fact]
	public void PendingApplyState_TracksEverySettingsSectionAndStopsAfterRoundTrip()
	{
		const string projectPath = @"C:\Project";
		var viewModel = CreateViewModel();
		var root = new SelectionOptionViewModel("src", true);
		var extension = new SelectionOptionViewModel(".cs", true);
		var ignore = new IgnoreOptionViewModel(IgnoreOptionId.DotFolders, "dot folders", true);
		viewModel.RootFolders.Add(root);
		viewModel.Extensions.Add(extension);
		viewModel.IgnoreOptions.Add(ignore);

		using var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => projectPath);
		HookAllOptionListeners(coordinator, viewModel);
		coordinator.AcceptCurrentSelectionsAsApplied(projectPath);

		Assert.False(viewModel.HasPendingFilterSettingsChanges);

		root.IsChecked = false;
		Assert.True(viewModel.HasPendingFilterSettingsChanges);
		root.IsChecked = true;
		Assert.False(viewModel.HasPendingFilterSettingsChanges);

		extension.IsChecked = false;
		Assert.True(viewModel.HasPendingFilterSettingsChanges);
		extension.IsChecked = true;
		Assert.False(viewModel.HasPendingFilterSettingsChanges);

		ignore.IsChecked = false;
		Assert.True(viewModel.HasPendingFilterSettingsChanges);
		ignore.IsChecked = true;
		Assert.False(viewModel.HasPendingFilterSettingsChanges);
	}

	[Fact]
	public void PendingApplyState_IgnoresMasterCheckboxChangesWithoutAnEffectiveSelectionChange()
	{
		const string projectPath = @"C:\Project";
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => projectPath);
		coordinator.AcceptCurrentSelectionsAsApplied(projectPath);

		coordinator.HandleRootAllChanged(isChecked: false, currentPath: null);
		coordinator.HandleIgnoreAllChanged(isChecked: false, currentPath: null);

		Assert.False(viewModel.HasPendingFilterSettingsChanges);
	}

	[Fact]
	public void PendingApplyState_AcceptAndProjectResetCannotLeakAcrossProjects()
	{
		const string firstProjectPath = @"C:\ProjectA";
		const string secondProjectPath = @"C:\ProjectB";
		var currentPath = firstProjectPath;
		var viewModel = CreateViewModel();
		viewModel.RootFolders.Add(new SelectionOptionViewModel("src", true));
		using var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => currentPath);
		coordinator.AcceptCurrentSelectionsAsApplied(firstProjectPath);

		viewModel.RootFolders[0].IsChecked = false;
		coordinator.ReevaluatePendingApplyChanges();
		Assert.True(viewModel.HasPendingFilterSettingsChanges);

		coordinator.AcceptCurrentSelectionsAsApplied(firstProjectPath);
		Assert.False(viewModel.HasPendingFilterSettingsChanges);

		currentPath = secondProjectPath;
		coordinator.ReevaluatePendingApplyChanges();
		Assert.True(viewModel.HasPendingFilterSettingsChanges);

		coordinator.ClearAppliedSelectionState();
		Assert.False(viewModel.HasPendingFilterSettingsChanges);
	}

	[Fact]
	public void PendingApplyState_DynamicOptionProjectionReturningToBaselineStopsAttention()
	{
		const string projectPath = @"C:\Project";
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".md", true));
		using var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => projectPath);
		coordinator.UpdateExtensionsSelectionCache();
		coordinator.AcceptCurrentSelectionsAsApplied(projectPath);

		coordinator.ApplyExtensionScan([".cs"]);
		Assert.True(viewModel.HasPendingFilterSettingsChanges);

		coordinator.ApplyExtensionScan([".cs", ".md"]);
		Assert.False(viewModel.HasPendingFilterSettingsChanges);
	}

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
	public void HandleIgnoreAllChanged_OffOnCyclePreservesTrackedGitFilteringMode()
	{
		const string projectPath = @"C:\Project";
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(
			viewModel,
			currentPathProvider: () => projectPath,
			availabilityProvider: (_, _) => new IgnoreOptionsAvailability(
				IncludeGitIgnore: true,
				IncludeSmartIgnore: true,
				IncludeTrackedGitFilesOnly: true));
		coordinator.ApplyProjectProfileSelections(
			projectPath,
			new ProjectSelectionProfile(
				SelectedRootFolders: [],
				SelectedExtensions: [],
				SelectedIgnoreOptions:
				[
					IgnoreOptionId.TrackedGitFilesOnly,
					IgnoreOptionId.SmartIgnore
				],
				IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.UseGitIgnore] = false,
					[IgnoreOptionId.TrackedGitFilesOnly] = true,
					[IgnoreOptionId.SmartIgnore] = true
				}));
		coordinator.PopulateIgnoreOptionsForRootSelection([], projectPath);

		Assert.False(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.UseGitIgnore).IsChecked);
		Assert.True(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.TrackedGitFilesOnly).IsChecked);

		coordinator.HandleIgnoreAllChanged(isChecked: false, currentPath: null);

		Assert.All(viewModel.IgnoreOptions, static option => Assert.False(option.IsChecked));

		coordinator.HandleIgnoreAllChanged(isChecked: true, currentPath: null);

		Assert.False(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.UseGitIgnore).IsChecked);
		Assert.True(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.TrackedGitFilesOnly).IsChecked);
		Assert.True(viewModel.AllIgnoreChecked);
	}

	[Fact]
	public void RootFolderReset_UnsubscribesRemovedItemsAndDoesNotDuplicateRetainedSubscriptions()
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel);
		coordinator.HookOptionListeners(viewModel.RootFolders);
		var options = Assert.IsType<ResettableObservableCollection<SelectionOptionViewModel>>(viewModel.RootFolders);
		var removed = new SelectionOptionViewModel("removed", true);
		var retained = new SelectionOptionViewModel("retained", true);

		options.ReplaceAll([removed]);
		options.ReplaceAll([retained]);
		options.ReplaceAll([retained]);
		options.ReplaceAll([retained]);

		Assert.Equal(0, GetEventSubscriberCount(removed, nameof(SelectionOptionViewModel.CheckedChanged)));
		Assert.Equal(1, GetEventSubscriberCount(retained, nameof(SelectionOptionViewModel.CheckedChanged)));
	}

	[Fact]
	public void IgnoreReset_UnsubscribesRemovedItemsAndDoesNotDuplicateRetainedSubscriptions()
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel);
		coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);
		var options = Assert.IsType<ResettableObservableCollection<IgnoreOptionViewModel>>(viewModel.IgnoreOptions);
		var removed = new IgnoreOptionViewModel(IgnoreOptionId.DotFolders, "removed", true);
		var retained = new IgnoreOptionViewModel(IgnoreOptionId.SmartIgnore, "retained", true);

		options.ReplaceAll([removed]);
		options.ReplaceAll([retained]);
		options.ReplaceAll([retained]);
		options.ReplaceAll([retained]);

		Assert.Equal(0, GetEventSubscriberCount(removed, nameof(IgnoreOptionViewModel.CheckedChanged)));
		Assert.Equal(1, GetEventSubscriberCount(retained, nameof(IgnoreOptionViewModel.CheckedChanged)));
	}

	[Fact]
	public void RelabelIgnoreOptions_UpdatesOnlyPresentationWithoutAvailabilityScanOrStateMutation()
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		var viewModel = new MainWindowViewModel(localization, new HelpContentProvider());
		viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(
			IgnoreOptionId.SmartIgnore,
			"Smart ignore",
			isChecked: false));
		var availabilityCalls = 0;
		using var coordinator = new SelectionSyncCoordinator(
			viewModel,
			new ScanOptionsUseCase(new StubFileSystemScanner()),
			new FilterOptionSelectionService(),
			new IgnoreOptionsService(localization),
			(_, _, _) => new IgnoreRules(
				false,
				false,
				false,
				false,
				new HashSet<string>(),
				new HashSet<string>()),
			(_, _) =>
			{
				availabilityCalls++;
				return new IgnoreOptionsAvailability(
					IncludeGitIgnore: false,
					IncludeSmartIgnore: true);
			},
			_ => false,
			() => @"C:\Project");
		var revisionBefore = coordinator.CurrentSelectionRevision;

		localization.SetLanguage(AppLanguage.Ru);
		coordinator.RelabelIgnoreOptions(showAdvancedCounts: true);

		var option = Assert.Single(viewModel.IgnoreOptions);
		Assert.Equal("Умное исключение", option.Label);
		Assert.False(option.IsChecked);
		Assert.Equal(revisionBefore, coordinator.CurrentSelectionRevision);
		Assert.Equal(0, availabilityCalls);
	}

	[Fact]
	public void SelectionRevision_AdvancesForEveryTreeAffectingSettingsMutation()
	{
		var viewModel = CreateViewModel();
		viewModel.RootFolders.Add(new SelectionOptionViewModel("src", true));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
		viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(
			IgnoreOptionId.DotFolders,
			"dot folders",
			isChecked: true));
		using var coordinator = CreateCoordinator(viewModel);
		HookAllOptionListeners(coordinator, viewModel);
		var expectedRevision = coordinator.CurrentSelectionRevision;

		viewModel.RootFolders[0].IsChecked = false;
		Assert.Equal(++expectedRevision, coordinator.CurrentSelectionRevision);

		viewModel.Extensions[0].IsChecked = false;
		Assert.Equal(++expectedRevision, coordinator.CurrentSelectionRevision);

		viewModel.IgnoreOptions[0].IsChecked = false;
		Assert.Equal(++expectedRevision, coordinator.CurrentSelectionRevision);

		coordinator.HandleRootAllChanged(true, currentPath: null);
		Assert.Equal(++expectedRevision, coordinator.CurrentSelectionRevision);

		coordinator.HandleExtensionsAllChanged(true);
		Assert.Equal(++expectedRevision, coordinator.CurrentSelectionRevision);

		coordinator.HandleIgnoreAllChanged(true, currentPath: null);
		Assert.Equal(++expectedRevision, coordinator.CurrentSelectionRevision);

		coordinator.ApplyProjectProfileSelections(
			@"C:\Project",
			new ProjectSelectionProfile([], [], []));
		Assert.Equal(++expectedRevision, coordinator.CurrentSelectionRevision);

		coordinator.ResetProjectProfileSelections(@"C:\Other");
		Assert.Equal(++expectedRevision, coordinator.CurrentSelectionRevision);
	}

	[Fact]
	public async Task UpdateLiveOptionsFromRootSelectionIfDirtyAsync_AfterSnapshotApply_DoesNotRunRedundantSnapshot()
	{
		var viewModel = CreateViewModel();
		var path = @"C:\Project";
		var scanner = new CountingRootSelectionSnapshotScanner();
		var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		MarkSelectionRefreshDirty(coordinator);

		ApplySelectionRefreshSnapshot(
			coordinator,
			new SelectionRefreshSnapshot(
				RootOptions: [new SelectionOption("src", true)],
				ExtensionOptions: [new SelectionOption(".cs", true)],
				IgnoreOptions: [],
				ExtensionlessEntriesCount: 0,
				HasIgnoreOptionCounts: true,
				IgnoreOptionCounts: IgnoreOptionCounts.Empty,
				ControllerImpactCounts: IgnoreControllerImpactCounts.Empty,
				IgnoreOptionStateCache: new Dictionary<IgnoreOptionId, bool>(),
				RootAccessDenied: false,
				HadAccessDenied: false));

		await coordinator.UpdateLiveOptionsFromRootSelectionIfDirtyAsync(path, cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(0, scanner.RootSelectionSnapshotCount);
	}

	[Fact]
	public void ApplySelectionRefreshSnapshot_InvalidatesOlderStandaloneIgnoreAvailabilityRefreshes()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		var beforeVersion = GetPrivateIgnoreOptionsVersion(coordinator);

		ApplySelectionRefreshSnapshot(
			coordinator,
			new SelectionRefreshSnapshot(
				RootOptions: [new SelectionOption("src", true)],
				ExtensionOptions: [new SelectionOption(".cs", true)],
				IgnoreOptions:
				[
					new ResolvedIgnoreOptionState(IgnoreOptionId.UseGitIgnore, "Use .gitignore", true, true),
					new ResolvedIgnoreOptionState(IgnoreOptionId.DotFolders, "dot folders (100)", true, true)
				],
				ExtensionlessEntriesCount: 0,
				HasIgnoreOptionCounts: true,
				IgnoreOptionCounts: new IgnoreOptionCounts(DotFolders: 100),
				ControllerImpactCounts: new IgnoreControllerImpactCounts(GitIgnore: 150),
				IgnoreOptionStateCache: new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.UseGitIgnore] = true,
					[IgnoreOptionId.DotFolders] = true
				},
				RootAccessDenied: false,
				HadAccessDenied: false));

		var afterVersion = GetPrivateIgnoreOptionsVersion(coordinator);

		// Standalone async availability refreshes compare this version before mutating
		// the UI. Count-driven snapshots must advance it to preserve one authoritative
		// ignore state after live/full refreshes.
		Assert.True(afterVersion > beforeVersion);
		Assert.Contains(viewModel.IgnoreOptions, option =>
			option.Id == IgnoreOptionId.DotFolders &&
			option.Label == "dot folders (100)" &&
			option.IsChecked);
	}

	[Fact]
	public void PopulateExtensionsForRootSelectionAsync_DoesNotDropCachedSelections()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", false));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".md", true));

		var coordinator = CreateCoordinator(viewModel);
		coordinator.UpdateExtensionsSelectionCache();

		coordinator.ApplyExtensionScan([".cs"]);
		coordinator.ApplyExtensionScan([".cs", ".md"]);

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

		coordinator.ApplyExtensionScan([]);
		coordinator.ApplyExtensionScan([".cs", ".md"]);

		var md = viewModel.Extensions.Single(option => option.Name == ".md");
		Assert.True(md.IsChecked);
	}

	[Fact]
	public void SnapshotExtensionOptionStatesForPersistence_KeepsHiddenManualStates()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".log", false));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));

		var coordinator = CreateCoordinator(viewModel);
		coordinator.UpdateExtensionsSelectionCache();

		coordinator.ApplyExtensionScan([".cs"]);

		var states = coordinator.SnapshotExtensionOptionStatesForPersistence();

		Assert.NotNull(states);
		Assert.True(states!.TryGetValue(".log", out var logChecked));
		Assert.False(logChecked);
		Assert.True(states.TryGetValue(".cs", out var csChecked));
		Assert.True(csChecked);
	}

	[Fact]
	public void ApplyExtensionScan_UpdatesExtensionsFromScanResults()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".old", true));

		var coordinator = CreateCoordinator(viewModel);

		coordinator.ApplyExtensionScan([".cs", ".md", ".root"]);

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

		coordinator.ApplyExtensionScan([".md", ".txt"]);

		var md = viewModel.Extensions.Single(option => option.Name == ".md");
		var txt = viewModel.Extensions.Single(option => option.Name == ".txt");
		Assert.True(md.IsChecked);
		Assert.False(txt.IsChecked);
	}

	[Fact]
	public void ApplyExtensionScan_NewExtensionDefaultsCheckedWhileKnownUncheckedStaysUnchecked()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", false));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".md", true));
		viewModel.AllExtensionsChecked = false;

		var coordinator = CreateCoordinator(viewModel);
		coordinator.UpdateExtensionsSelectionCache();

		coordinator.ApplyExtensionScan([".cs", ".md", ".json"]);

		Assert.False(viewModel.Extensions.Single(option => option.Name == ".cs").IsChecked);
		Assert.True(viewModel.Extensions.Single(option => option.Name == ".md").IsChecked);
		Assert.True(viewModel.Extensions.Single(option => option.Name == ".json").IsChecked);
		Assert.False(viewModel.AllExtensionsChecked);
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

		coordinator.ApplyExtensionScan([".cs", ".json"]);

		Assert.All(viewModel.Extensions, option => Assert.True(option.IsChecked));
	}

	[Fact]
	public void ApplyExtensionScan_EmptyScan_ClearsExtensionsAndAllFlag()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
		viewModel.AllExtensionsChecked = true;

		var coordinator = CreateCoordinator(viewModel);

		coordinator.ApplyExtensionScan([]);

		Assert.Empty(viewModel.Extensions);
		Assert.False(viewModel.AllExtensionsChecked);
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_EmptyRoots_StillPopulatesIgnoreOptions()
	{
		// Emulate a root-level extensionless entry so the coordinator has a real
		// count-driven reason to keep one advanced option visible.
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(ExtensionlessFiles: 1));

		coordinator.PopulateIgnoreOptionsForRootSelection([], "C:\\ProjectA");

		Assert.Contains(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.ExtensionlessFiles);
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_PreservesIgnoreSelections()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(HiddenFolders: 1, HiddenFiles: 1));
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], "C:\\ProjectA");
		coordinator.HandleIgnoreAllChanged(false, currentPath: null);
		viewModel.IgnoreOptions[0].IsChecked = true;
		viewModel.IgnoreOptions[1].IsChecked = false;
		coordinator.UpdateIgnoreSelectionCache();

		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(HiddenFolders: 1, HiddenFiles: 1));
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], "C:\\ProjectA");

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
			var scanOptions = new ScanOptionsUseCase(LegacyWorkspaceScannerTestAdapter.Adapt(scanner));
			var coordinator = new SelectionSyncCoordinator(
				viewModel,
				scanOptions,
				new FilterOptionSelectionService(),
				new IgnoreOptionsService(localization),
				_ => new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
				_ => false,
				() => tempRoot);

			coordinator.PopulateIgnoreOptionsForRootSelection(["src"], tempRoot);

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
			var scanOptions = new ScanOptionsUseCase(LegacyWorkspaceScannerTestAdapter.Adapt(scanner));
			var coordinator = new SelectionSyncCoordinator(
				viewModel,
				scanOptions,
				new FilterOptionSelectionService(),
				new IgnoreOptionsService(localization),
				_ => new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
				_ => false,
				() => tempRoot);

			coordinator.PopulateIgnoreOptionsForRootSelection(["src"], tempRoot);

			Assert.DoesNotContain(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.UseGitIgnore);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void ApplyProjectProfileSelections_StoresUnavailableIgnoreSelectionsWithoutActivatingHiddenRules()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: [IgnoreOptionId.DotFiles, IgnoreOptionId.HiddenFiles]);

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		var selected = coordinator.GetSelectedIgnoreOptionIds();
		var persistedStates = coordinator.SnapshotIgnoreOptionStatesForPersistence();

		Assert.Empty(selected);
		Assert.NotNull(persistedStates);
		Assert.True(persistedStates![IgnoreOptionId.DotFiles]);
		Assert.True(persistedStates[IgnoreOptionId.HiddenFiles]);
	}

	[Fact]
	public void ApplyProjectProfileSelections_PreservesExtensionSelectionInNextScan()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [".md"],
			SelectedIgnoreOptions: []);

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		coordinator.ApplyExtensionScan([".cs", ".md"]);

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
			SelectedRootFolders: [],
			SelectedExtensions: [".md"],
			SelectedIgnoreOptions: []);

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		coordinator.ApplyExtensionScan([".cs", ".md", ".json"]);

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
			SelectedRootFolders: [],
			SelectedExtensions: [".removed-ext"],
			SelectedIgnoreOptions: []);

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		coordinator.ApplyExtensionScan([".cs", ".json"]);

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
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: []);

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		coordinator.ApplyExtensionScan([".cs", ".json"]);

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
			SelectedRootFolders: ["src"],
			SelectedExtensions: [".cs"],
			SelectedIgnoreOptions: [IgnoreOptionId.DotFiles]);

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
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: [IgnoreOptionId.UseGitIgnore]);

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(HiddenFolders: 1, HiddenFiles: 1, DotFolders: 1, DotFiles: 1));
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], "C:\\ProjectA");

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
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: []);

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], "C:\\ProjectA");

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
			SelectedRootFolders: [],
			SelectedExtensions: [".md"],
			SelectedIgnoreOptions: []);

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		coordinator.ResetProjectProfileSelections("C:\\ProjectB");
		coordinator.ApplyExtensionScan([".cs", ".md"]);

		Assert.True(viewModel.Extensions.Single(option => option.Name == ".cs").IsChecked);
		Assert.True(viewModel.Extensions.Single(option => option.Name == ".md").IsChecked);
	}

	[Fact]
	public void ApplyRootOptions_WhenOptionsAreUnchanged_KeepsExistingViewModels()
	{
		var viewModel = CreateViewModel();
		viewModel.AllRootFoldersChecked = false;
		var coordinator = CreateCoordinator(viewModel);
		var options = new[]
		{
			new SelectionOption("src", true),
			new SelectionOption("tests", false)
		};

		ApplyRootOptions(coordinator, options);
		var firstRoot = viewModel.RootFolders[0];
		var collectionEvents = 0;
		viewModel.RootFolders.CollectionChanged += (_, _) => collectionEvents++;

		ApplyRootOptions(coordinator, options);

		Assert.Same(firstRoot, viewModel.RootFolders[0]);
		Assert.Equal(0, collectionEvents);
	}

	[Fact]
	public void ApplyRootOptions_WhenFilteredRootDisappears_UpdatesNamesCountAndMasterState()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		ApplyRootOptions(
			coordinator,
			[
				new SelectionOption("app", true),
				new SelectionOption("scripts", true),
				new SelectionOption("tests", true)
			]);

		ApplyRootOptions(
			coordinator,
			[
				new SelectionOption("app", true),
				new SelectionOption("tests", true)
			]);

		Assert.Equal(["app", "tests"], viewModel.RootFolders.Select(static option => option.Name));
		Assert.All(viewModel.RootFolders, static option => Assert.True(option.IsChecked));
		Assert.True(viewModel.AllRootFoldersChecked);
		Assert.EndsWith(" (2)", viewModel.SettingsAllRootFolders, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(IgnoreOptionId.SmartIgnore)]
	[InlineData(IgnoreOptionId.UseGitIgnore)]
	[InlineData(IgnoreOptionId.HiddenFolders)]
	[InlineData(IgnoreOptionId.HiddenFiles)]
	[InlineData(IgnoreOptionId.DotFolders)]
	[InlineData(IgnoreOptionId.DotFiles)]
	[InlineData(IgnoreOptionId.EmptyFolders)]
	[InlineData(IgnoreOptionId.EmptyFiles)]
	[InlineData(IgnoreOptionId.ExtensionlessFiles)]
	public void ReversibleRefresh_IgnoreOptionToggleCycle_RestoresCountsWithoutScanning(
		IgnoreOptionId optionId)
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		var scanner = new CountingRootSelectionSnapshotScanner();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		var enabledSnapshot = CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433);
		var disabledSnapshot = CreateReversibleSelectionRefreshSnapshot(
			optionId,
			emptyFolderCount: 410) with
		{
			RootOptions =
			[
				new SelectionOption("src", true),
				new SelectionOption("empty-file-root", true)
			],
			ExtensionOptions =
			[
				new SelectionOption(".cs", true),
				new SelectionOption(".txt", true)
			]
		};
		ApplySelectionRefreshSnapshot(
			coordinator,
			enabledSnapshot);
		ApplyCurrentSelectionState(
			coordinator,
			viewModel,
			disabledSnapshot);
		ApplySelectionRefreshSnapshot(
			coordinator,
			disabledSnapshot,
			retainPreviousSnapshot: true);

		ApplyCurrentSelectionState(
			coordinator,
			viewModel,
			CreateIgnoreReversalCurrentSnapshot(disabledSnapshot, enabledSnapshot, optionId));

		Assert.True(TryRestoreKnownSelectionSnapshot(
			coordinator,
			path,
			SelectionRefreshOrigin.IgnoreOption,
			optionId));
		Assert.Equal(0, scanner.TotalScanCount);
		Assert.True(viewModel.IgnoreOptions.Single(option => option.Id == optionId).IsChecked);
		Assert.Equal(
			"EmptyFolders (433)",
			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).Label);
		Assert.Equal(["src"], viewModel.RootFolders.Select(static option => option.Name));
		Assert.Equal([".cs"], viewModel.Extensions.Select(static option => option.Name));

		ApplyCurrentSelectionState(
			coordinator,
			viewModel,
			CreateIgnoreReversalCurrentSnapshot(enabledSnapshot, disabledSnapshot, optionId));

		Assert.True(TryRestoreKnownSelectionSnapshot(
			coordinator,
			path,
			SelectionRefreshOrigin.IgnoreOption,
			optionId));
		Assert.Equal(0, scanner.TotalScanCount);
		Assert.False(viewModel.IgnoreOptions.Single(option => option.Id == optionId).IsChecked);
		Assert.Equal(
			"EmptyFolders (410)",
			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).Label);
		Assert.Equal(
			["src", "empty-file-root"],
			viewModel.RootFolders.Select(static option => option.Name));
		Assert.Equal(
			[".cs", ".txt"],
			viewModel.Extensions.Select(static option => option.Name));
	}

	[AvaloniaTheory]
	[InlineData(IgnoreOptionId.HiddenFiles)]
	[InlineData(IgnoreOptionId.DotFiles)]
	[InlineData(IgnoreOptionId.EmptyFiles)]
	[InlineData(IgnoreOptionId.ExtensionlessFiles)]
	public async Task PublicRefreshQueue_FileVisibilityToggleCycle_RestoresOriginalEmptyFoldersCounter(
		IgnoreOptionId optionId)
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		var scanner = new CountingRootSelectionSnapshotScanner
		{
			RootSelectionSnapshot = CreateDriftedRootSelectionScanData()
		};
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));
		HookAllOptionListeners(coordinator, viewModel);

		viewModel.IgnoreOptions.Single(option => option.Id == optionId).IsChecked = false;
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

		Assert.Equal(1, scanner.RootSelectionSnapshotCount);
		Assert.Equal(410, GetPrivateIgnoreOptionCounts(coordinator).EmptyFolders);
		Assert.NotEqual(
			"EmptyFolders (433)",
			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).Label);
		Assert.Contains(viewModel.Extensions, option => option.Name == ".txt");

		viewModel.IgnoreOptions.Single(option => option.Id == optionId).IsChecked = true;
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

		Assert.Equal(1, scanner.RootSelectionSnapshotCount);
		Assert.Equal(433, GetPrivateIgnoreOptionCounts(coordinator).EmptyFolders);
		Assert.Equal(
			"EmptyFolders (433)",
			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).Label);
		Assert.DoesNotContain(viewModel.Extensions, option => option.Name == ".txt");

		viewModel.IgnoreOptions.Single(option => option.Id == optionId).IsChecked = false;
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

		Assert.Equal(1, scanner.RootSelectionSnapshotCount);
		Assert.Equal(410, GetPrivateIgnoreOptionCounts(coordinator).EmptyFolders);
		Assert.Contains(viewModel.Extensions, option => option.Name == ".txt");
	}

	[AvaloniaTheory]
	[InlineData(IgnoreOptionId.HiddenFiles)]
	[InlineData(IgnoreOptionId.DotFolders)]
	public async Task PublicRefreshQueue_CurrentFault_RestoresStableSelectionPresentation(
		IgnoreOptionId optionId)
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		var scanner = new CountingRootSelectionSnapshotScanner
		{
			BeforeRootSelectionSnapshot = _ => throw new IOException("Synthetic refresh failure.")
		};
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));
		HookAllOptionListeners(coordinator, viewModel);

		viewModel.IgnoreOptions.Single(option => option.Id == optionId).IsChecked = false;

		await Assert.ThrowsAsync<IOException>(() =>
			coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken));

		Assert.True(viewModel.IgnoreOptions.Single(option => option.Id == optionId).IsChecked);
		Assert.True(viewModel.AllIgnoreChecked);
		Assert.Equal(
			"EmptyFolders (433)",
			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).Label);
		Assert.Equal(["src"], viewModel.RootFolders.Select(static option => option.Name));
		Assert.Equal([".cs"], viewModel.Extensions.Select(static option => option.Name));
	}

	[AvaloniaTheory]
	[InlineData(IgnoreOptionId.HiddenFiles)]
	[InlineData(IgnoreOptionId.DotFolders)]
	public async Task CancelPendingRefreshes_LiveAndFullLateResults_CannotOverwriteStablePresentation(
		IgnoreOptionId optionId)
	{
		const string path = @"C:\Project";
		using var scanStarted = new ManualResetEventSlim();
		using var releaseScan = new ManualResetEventSlim();
		var scanner = new CountingRootSelectionSnapshotScanner
		{
			RootSelectionSnapshot = CreateDriftedRootSelectionScanData(),
			BeforeRootSelectionSnapshot = _ =>
			{
				scanStarted.Set();
				if (!releaseScan.Wait(TimeSpan.FromSeconds(3)))
					throw new TimeoutException("The controlled selection scan was not released.");
			}
		};
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));
		HookAllOptionListeners(coordinator, viewModel);

		try
		{
			viewModel.IgnoreOptions.Single(option => option.Id == optionId).IsChecked = false;
			Assert.True(await Task.Run(
				() => scanStarted.Wait(TimeSpan.FromSeconds(2)),
				TestContext.Current.CancellationToken));

			Assert.True(coordinator.CancelPendingRefreshes());
			Assert.False(coordinator.CancelPendingRefreshes());
			Assert.True(viewModel.IgnoreOptions.Single(option => option.Id == optionId).IsChecked);
			Assert.Equal(
				"EmptyFolders (433)",
				viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).Label);

			releaseScan.Set();
			await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

			Assert.True(viewModel.IgnoreOptions.Single(option => option.Id == optionId).IsChecked);
			Assert.True(viewModel.AllIgnoreChecked);
			Assert.Equal(["src"], viewModel.RootFolders.Select(static option => option.Name));
			Assert.Equal([".cs"], viewModel.Extensions.Select(static option => option.Name));
		}
		finally
		{
			releaseScan.Set();
		}
	}

	[AvaloniaFact]
	public async Task PublicRefreshQueue_RapidIgnoreReversal_CancelsStaleScanAndKeepsStableSnapshot()
	{
		const string path = @"C:\Project";
		using var scanStarted = new ManualResetEventSlim();
		using var releaseScan = new ManualResetEventSlim();
		var cancellationObserved = 0;
		var scanner = new CountingRootSelectionSnapshotScanner
		{
			RootSelectionSnapshot = CreateDriftedRootSelectionScanData(),
			BeforeRootSelectionSnapshot = cancellationToken =>
			{
				scanStarted.Set();
				var signal = WaitHandle.WaitAny(
					[cancellationToken.WaitHandle, releaseScan.WaitHandle],
					TimeSpan.FromSeconds(3));
				if (signal == 0)
				{
					Interlocked.Exchange(ref cancellationObserved, 1);
					cancellationToken.ThrowIfCancellationRequested();
				}

				if (signal == WaitHandle.WaitTimeout)
					throw new TimeoutException("The controlled selection scan was not released.");
			}
		};
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));
		HookAllOptionListeners(coordinator, viewModel);

		try
		{
			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFiles).IsChecked = false;
			Assert.True(await Task.Run(
				() => scanStarted.Wait(TimeSpan.FromSeconds(2)),
				TestContext.Current.CancellationToken));

			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFiles).IsChecked = true;
			await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

			Assert.Equal(1, Volatile.Read(ref cancellationObserved));
			Assert.Equal(1, scanner.RootSelectionSnapshotCount);
			Assert.Equal(433, GetPrivateIgnoreOptionCounts(coordinator).EmptyFolders);
			Assert.Equal(
				"EmptyFolders (433)",
				viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).Label);
			Assert.DoesNotContain(viewModel.Extensions, option => option.Name == ".txt");
		}
		finally
		{
			releaseScan.Set();
		}
	}

	[AvaloniaFact]
	public async Task PublicRefreshQueue_AdditionalExtensionChange_InvalidatesEarlierIgnoreRollback()
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		var scanner = new CountingRootSelectionSnapshotScanner
		{
			RootSelectionSnapshot = CreateDriftedRootSelectionScanData()
		};
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));
		HookAllOptionListeners(coordinator, viewModel);

		viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFiles).IsChecked = false;
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);
		Assert.Equal(1, scanner.RootSelectionSnapshotCount);

		viewModel.Extensions.Single(option => option.Name == ".txt").IsChecked = false;
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);
		Assert.Equal(2, scanner.RootSelectionSnapshotCount);

		viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFiles).IsChecked = true;
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

		Assert.Equal(3, scanner.RootSelectionSnapshotCount);
		Assert.Equal(410, GetPrivateIgnoreOptionCounts(coordinator).EmptyFolders);
	}

	[AvaloniaTheory]
	[InlineData(IgnoreOptionId.SmartIgnore)]
	[InlineData(IgnoreOptionId.UseGitIgnore)]
	[InlineData(IgnoreOptionId.HiddenFolders)]
	[InlineData(IgnoreOptionId.DotFolders)]
	[InlineData(IgnoreOptionId.EmptyFolders)]
	public async Task PublicFullRefreshQueue_StructuralIgnoreToggleCycle_RestoresKnownSnapshotsWithoutScanning(
		IgnoreOptionId optionId)
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		var scanner = new CountingRootSelectionSnapshotScanner();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		var enabledSnapshot = CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433);
		var disabledSnapshot = CreateReversibleSelectionRefreshSnapshot(
			optionId,
			emptyFolderCount: 410) with
		{
			RootOptions =
			[
				new SelectionOption("src", true),
				new SelectionOption("generated", true)
			]
		};
		ApplySelectionRefreshSnapshot(coordinator, enabledSnapshot);
		ApplyCurrentSelectionState(coordinator, viewModel, disabledSnapshot);
		ApplySelectionRefreshSnapshot(
			coordinator,
			disabledSnapshot,
			retainPreviousSnapshot: true);
		HookAllOptionListeners(coordinator, viewModel);

		viewModel.IgnoreOptions.Single(option => option.Id == optionId).IsChecked = true;
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

		Assert.Equal(0, scanner.TotalScanCount);
		Assert.Equal(433, GetPrivateIgnoreOptionCounts(coordinator).EmptyFolders);
		Assert.Equal(["src"], viewModel.RootFolders.Select(static option => option.Name));

		viewModel.IgnoreOptions.Single(option => option.Id == optionId).IsChecked = false;
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

		Assert.Equal(0, scanner.TotalScanCount);
		Assert.Equal(410, GetPrivateIgnoreOptionCounts(coordinator).EmptyFolders);
		Assert.Equal(
			["src", "generated"],
			viewModel.RootFolders.Select(static option => option.Name));
	}

	[AvaloniaTheory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task PublicLiveRefreshQueue_RootOrExtensionToggleCycle_RestoresKnownSnapshotsWithoutScanning(
		bool changeRootSelection)
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		var scanner = new CountingRootSelectionSnapshotScanner();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		var enabledSnapshot = CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433);
		var disabledSnapshot = CreateReversibleSelectionRefreshSnapshot(
			rootChecked: !changeRootSelection,
			extensionChecked: changeRootSelection,
			emptyFolderCount: 410);
		ApplySelectionRefreshSnapshot(coordinator, enabledSnapshot);
		ApplyCurrentSelectionState(coordinator, viewModel, disabledSnapshot);
		ApplySelectionRefreshSnapshot(
			coordinator,
			disabledSnapshot,
			retainPreviousSnapshot: true);
		HookAllOptionListeners(coordinator, viewModel);

		if (changeRootSelection)
			viewModel.RootFolders.Single().IsChecked = true;
		else
			viewModel.Extensions.Single().IsChecked = true;
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

		Assert.Equal(0, scanner.TotalScanCount);
		Assert.Equal(433, GetPrivateIgnoreOptionCounts(coordinator).EmptyFolders);

		if (changeRootSelection)
			viewModel.RootFolders.Single().IsChecked = false;
		else
			viewModel.Extensions.Single().IsChecked = false;
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

		Assert.Equal(0, scanner.TotalScanCount);
		Assert.Equal(410, GetPrivateIgnoreOptionCounts(coordinator).EmptyFolders);
	}

	[AvaloniaTheory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task PublicBulkSelectionToggleCycle_RestoresKnownSnapshotsWithoutScanning(
		bool changeRootSelection)
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		var scanner = new CountingRootSelectionSnapshotScanner();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		var enabledSnapshot = CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433);
		var disabledSnapshot = CreateReversibleSelectionRefreshSnapshot(
			rootChecked: !changeRootSelection,
			extensionChecked: changeRootSelection,
			emptyFolderCount: 410);
		ApplySelectionRefreshSnapshot(coordinator, enabledSnapshot);
		ApplyCurrentSelectionState(coordinator, viewModel, disabledSnapshot);
		ApplySelectionRefreshSnapshot(
			coordinator,
			disabledSnapshot,
			retainPreviousSnapshot: true);

		if (changeRootSelection)
			coordinator.HandleRootAllChanged(true, path);
		else
			coordinator.HandleExtensionsAllChanged(true);
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

		Assert.Equal(0, scanner.TotalScanCount);
		Assert.Equal(433, GetPrivateIgnoreOptionCounts(coordinator).EmptyFolders);
		Assert.Equal(
			"EmptyFolders (433)",
			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).Label);
		Assert.True(changeRootSelection
			? viewModel.AllRootFoldersChecked
			: viewModel.AllExtensionsChecked);
		Assert.True(changeRootSelection
			? viewModel.RootFolders.Single().IsChecked
			: viewModel.Extensions.Single().IsChecked);

		if (changeRootSelection)
			coordinator.HandleRootAllChanged(false, path);
		else
			coordinator.HandleExtensionsAllChanged(false);
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

		Assert.Equal(0, scanner.TotalScanCount);
		Assert.Equal(410, GetPrivateIgnoreOptionCounts(coordinator).EmptyFolders);
		Assert.Equal(
			"EmptyFolders (410)",
			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).Label);
		Assert.False(changeRootSelection
			? viewModel.AllRootFoldersChecked
			: viewModel.AllExtensionsChecked);
		Assert.False(changeRootSelection
			? viewModel.RootFolders.Single().IsChecked
			: viewModel.Extensions.Single().IsChecked);
	}

	[AvaloniaFact]
	public async Task PublicRefreshQueue_PathChangesDuringScan_DoesNotApplyStaleSnapshot()
	{
		const string originalPath = @"C:\ProjectA";
		const string newPath = @"C:\ProjectB";
		var currentPath = originalPath;
		using var scanStarted = new ManualResetEventSlim();
		using var releaseScan = new ManualResetEventSlim();
		var scanner = new CountingRootSelectionSnapshotScanner
		{
			RootSelectionSnapshot = CreateDriftedRootSelectionScanData(),
			BeforeRootSelectionSnapshot = cancellationToken =>
			{
				scanStarted.Set();
				if (!releaseScan.Wait(TimeSpan.FromSeconds(3), cancellationToken))
					throw new TimeoutException("The controlled selection scan was not released.");
			}
		};
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => currentPath);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));
		HookAllOptionListeners(coordinator, viewModel);

		try
		{
			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFiles).IsChecked = false;
			Assert.True(await Task.Run(
				() => scanStarted.Wait(TimeSpan.FromSeconds(2)),
				TestContext.Current.CancellationToken));

			currentPath = newPath;
			releaseScan.Set();
			await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

			Assert.Equal(1, scanner.RootSelectionSnapshotCount);
			Assert.Equal(433, GetPrivateIgnoreOptionCounts(coordinator).EmptyFolders);
			Assert.Equal(
				"EmptyFolders (433)",
				viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).Label);
			Assert.DoesNotContain(viewModel.Extensions, option => option.Name == ".txt");
		}
		finally
		{
			releaseScan.Set();
		}
	}

	[AvaloniaFact]
	public async Task PublicIgnoreAllChange_ExplicitPreferenceRejectsSemanticallyDifferentRollback()
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		var scanner = new CountingRootSelectionSnapshotScanner
		{
			RootSelectionSnapshot = CreateDriftedRootSelectionScanData()
		};
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));
		HookAllOptionListeners(coordinator, viewModel);

		viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFiles).IsChecked = false;
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);
		Assert.Equal(1, scanner.TotalScanCount);
		var scanCountBeforeBulkChange = scanner.TotalScanCount;

		coordinator.HandleIgnoreAllChanged(true, path);
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

		Assert.True(scanner.TotalScanCount > scanCountBeforeBulkChange);
		Assert.True(viewModel.AllIgnoreChecked);
		Assert.All(viewModel.IgnoreOptions, static option => Assert.True(option.IsChecked));
	}

	[Fact]
	public void ReversibleRefresh_ExtensionToggleCycle_RestoresCountsWithoutScanning()
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		var scanner = new CountingRootSelectionSnapshotScanner();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));
		ApplyCurrentSelectionState(
			coordinator,
			viewModel,
			CreateReversibleSelectionRefreshSnapshot(
				extensionChecked: false,
				emptyFolderCount: 410));
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(
				extensionChecked: false,
				emptyFolderCount: 410),
			retainPreviousSnapshot: true);

		ApplyCurrentSelectionState(
			coordinator,
			viewModel,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));

		Assert.True(TryRestoreKnownSelectionSnapshot(
			coordinator,
			path,
			SelectionRefreshOrigin.ExtensionSelection));
		Assert.Equal(0, scanner.TotalScanCount);
		Assert.True(viewModel.Extensions.Single().IsChecked);
		Assert.Equal(
			"EmptyFolders (433)",
			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).Label);

		ApplyCurrentSelectionState(
			coordinator,
			viewModel,
			CreateReversibleSelectionRefreshSnapshot(
				extensionChecked: false,
				emptyFolderCount: 410));

		Assert.True(TryRestoreKnownSelectionSnapshot(
			coordinator,
			path,
			SelectionRefreshOrigin.ExtensionSelection));
		Assert.Equal(0, scanner.TotalScanCount);
		Assert.False(viewModel.Extensions.Single().IsChecked);
		Assert.Equal(
			"EmptyFolders (410)",
			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).Label);
	}

	[Fact]
	public void ReversibleRefresh_RootToggleCycle_RestoresCountsWithoutScanning()
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		var scanner = new CountingRootSelectionSnapshotScanner();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));
		ApplyCurrentSelectionState(
			coordinator,
			viewModel,
			CreateReversibleSelectionRefreshSnapshot(
				rootChecked: false,
				emptyFolderCount: 410));
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(
				rootChecked: false,
				emptyFolderCount: 410),
			retainPreviousSnapshot: true);

		ApplyCurrentSelectionState(
			coordinator,
			viewModel,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));

		Assert.True(TryRestoreKnownSelectionSnapshot(
			coordinator,
			path,
			SelectionRefreshOrigin.RootSelection));
		Assert.Equal(0, scanner.TotalScanCount);
		Assert.True(viewModel.RootFolders.Single().IsChecked);
		Assert.Equal(
			"EmptyFolders (433)",
			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).Label);

		ApplyCurrentSelectionState(
			coordinator,
			viewModel,
			CreateReversibleSelectionRefreshSnapshot(
				rootChecked: false,
				emptyFolderCount: 410));

		Assert.True(TryRestoreKnownSelectionSnapshot(
			coordinator,
			path,
			SelectionRefreshOrigin.RootSelection));
		Assert.Equal(0, scanner.TotalScanCount);
		Assert.False(viewModel.RootFolders.Single().IsChecked);
		Assert.Equal(
			"EmptyFolders (410)",
			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).Label);
	}

	[Fact]
	public void ReversibleRefresh_HiddenCacheStateDiffers_RejectsCachedSnapshot()
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		var scanner = new CountingRootSelectionSnapshotScanner();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));
		GetPrivateSession(coordinator).Extensions.OptionStates[".hidden"] = true;

		Assert.False(TryRestoreKnownSelectionSnapshot(coordinator, path));
		Assert.Equal(0, scanner.TotalScanCount);
	}

	[Fact]
	public void ReversibleRefresh_IgnoreReversalAfterAdditionalExtensionChange_RejectsCachedSnapshot()
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => path);
		var enabledSnapshot = CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433);
		var disabledSnapshot = CreateReversibleSelectionRefreshSnapshot(
			IgnoreOptionId.EmptyFiles,
			emptyFolderCount: 410) with
		{
			ExtensionOptions =
			[
				new SelectionOption(".cs", true),
				new SelectionOption(".txt", true)
			]
		};
		ApplySelectionRefreshSnapshot(coordinator, enabledSnapshot);
		ApplyCurrentSelectionState(coordinator, viewModel, disabledSnapshot);
		ApplySelectionRefreshSnapshot(
			coordinator,
			disabledSnapshot,
			retainPreviousSnapshot: true);
		var currentSnapshot = CreateIgnoreReversalCurrentSnapshot(
			disabledSnapshot,
			enabledSnapshot,
			IgnoreOptionId.EmptyFiles) with
		{
			ExtensionOptions =
			[
				new SelectionOption(".cs", true),
				new SelectionOption(".txt", false)
			]
		};
		ApplyCurrentSelectionState(coordinator, viewModel, currentSnapshot);

		Assert.False(TryRestoreKnownSelectionSnapshot(
			coordinator,
			path,
			SelectionRefreshOrigin.IgnoreOption,
			IgnoreOptionId.EmptyFiles));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void ReversibleRefresh_SectionReversalWithConflictingCrossSectionPreference_RejectsCachedSnapshot(
		bool reverseRootSelection)
	{
		const string path = @"C:\Project";
		var origin = reverseRootSelection
			? SelectionRefreshOrigin.RootSelection
			: SelectionRefreshOrigin.ExtensionSelection;
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => path);
		var originalSnapshot = CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433);
		var changedSnapshot = CreateReversibleSelectionRefreshSnapshot(
			rootChecked: false,
			extensionChecked: false,
			emptyFolderCount: 410);
		ApplySelectionRefreshSnapshot(coordinator, originalSnapshot);
		ApplyCurrentSelectionState(coordinator, viewModel, changedSnapshot);
		ApplySelectionRefreshSnapshot(
			coordinator,
			changedSnapshot,
			retainPreviousSnapshot: true);

		var hybridSnapshot = origin == SelectionRefreshOrigin.RootSelection
			? CreateReversibleSelectionRefreshSnapshot(
				rootChecked: true,
				extensionChecked: false,
				emptyFolderCount: 410)
			: CreateReversibleSelectionRefreshSnapshot(
				rootChecked: false,
				extensionChecked: true,
				emptyFolderCount: 410);
		ApplyCurrentSelectionState(coordinator, viewModel, hybridSnapshot);

		Assert.False(TryRestoreKnownSelectionSnapshot(coordinator, path, origin));
		Assert.Equal(origin == SelectionRefreshOrigin.RootSelection, viewModel.RootFolders.Single().IsChecked);
		Assert.Equal(origin == SelectionRefreshOrigin.ExtensionSelection, viewModel.Extensions.Single().IsChecked);
	}

	[Fact]
	public void StableRefresh_CheckboxStateMatchesButCountLabelDrifted_RestoresStablePresentation()
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		var scanner = new CountingRootSelectionSnapshotScanner();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));
		ApplyCurrentSelectionState(
			coordinator,
			viewModel,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 410));

		Assert.True(TryRestoreKnownSelectionSnapshot(coordinator, path));

		Assert.Equal(0, scanner.TotalScanCount);
		Assert.Equal(
			"EmptyFolders (433)",
			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).Label);
	}

	[Fact]
	public void ReversibleRefresh_DifferentPath_RejectsCachedSnapshot()
	{
		const string path = @"C:\ProjectA";
		var viewModel = CreateViewModel();
		var scanner = new CountingRootSelectionSnapshotScanner();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));

		Assert.False(TryRestoreKnownSelectionSnapshot(coordinator, @"C:\ProjectB"));
		Assert.Equal(0, scanner.TotalScanCount);
	}

	[Fact]
	public void ApplyRootOptions_WhenOnlyCheckedStateChanges_UpdatesExistingViewModels()
	{
		var viewModel = CreateViewModel();
		viewModel.AllRootFoldersChecked = false;
		var coordinator = CreateCoordinator(viewModel);
		ApplyRootOptions(coordinator, [new SelectionOption("src", true), new SelectionOption("tests", false)]);
		var firstRoot = viewModel.RootFolders[0];
		var collectionEvents = 0;
		viewModel.RootFolders.CollectionChanged += (_, _) => collectionEvents++;

		ApplyRootOptions(coordinator, [new SelectionOption("src", false), new SelectionOption("tests", true)]);

		Assert.Same(firstRoot, viewModel.RootFolders[0]);
		Assert.False(viewModel.RootFolders[0].IsChecked);
		Assert.True(viewModel.RootFolders[1].IsChecked);
		Assert.Equal(0, collectionEvents);
	}

	[Fact]
	public void ApplyExtensionOptions_WhenOptionsAreUnchanged_KeepsExistingViewModels()
	{
		var viewModel = CreateViewModel();
		viewModel.AllExtensionsChecked = false;
		var coordinator = CreateCoordinator(viewModel);
		var options = new[]
		{
			new SelectionOption(".cs", true),
			new SelectionOption(".md", false)
		};

		ApplyExtensionOptions(coordinator, options);
		var firstExtension = viewModel.Extensions[0];
		var collectionEvents = 0;
		viewModel.Extensions.CollectionChanged += (_, _) => collectionEvents++;

		ApplyExtensionOptions(coordinator, options);

		Assert.Same(firstExtension, viewModel.Extensions[0]);
		Assert.Equal(0, collectionEvents);
	}

	[Fact]
	public void ApplyExtensionOptions_WhenOnlyCheckedStateChanges_UpdatesExistingViewModels()
	{
		var viewModel = CreateViewModel();
		viewModel.AllExtensionsChecked = false;
		var coordinator = CreateCoordinator(viewModel);
		ApplyExtensionOptions(coordinator, [new SelectionOption(".cs", true), new SelectionOption(".md", false)]);
		var firstExtension = viewModel.Extensions[0];
		var collectionEvents = 0;
		viewModel.Extensions.CollectionChanged += (_, _) => collectionEvents++;

		ApplyExtensionOptions(coordinator, [new SelectionOption(".cs", false), new SelectionOption(".md", true)]);

		Assert.Same(firstExtension, viewModel.Extensions[0]);
		Assert.False(viewModel.Extensions[0].IsChecked);
		Assert.True(viewModel.Extensions[1].IsChecked);
		Assert.Equal(0, collectionEvents);
	}

	[Fact]
	public void ApplyResolvedIgnoreOptions_WhenOptionsAreUnchanged_KeepsExistingViewModels()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		var options = new[]
		{
			new ResolvedIgnoreOptionState(IgnoreOptionId.DotFolders, "dot folders", true, true),
			new ResolvedIgnoreOptionState(IgnoreOptionId.EmptyFiles, "empty files", true, false)
		};
		var stateCache = new Dictionary<IgnoreOptionId, bool>
		{
			[IgnoreOptionId.DotFolders] = true,
			[IgnoreOptionId.EmptyFiles] = false
		};

		ApplyResolvedIgnoreOptions(coordinator, options, stateCache);
		var firstIgnoreOption = viewModel.IgnoreOptions[0];
		var collectionEvents = 0;
		viewModel.IgnoreOptions.CollectionChanged += (_, _) => collectionEvents++;

		ApplyResolvedIgnoreOptions(coordinator, options, stateCache);

		Assert.Same(firstIgnoreOption, viewModel.IgnoreOptions[0]);
		Assert.Equal(0, collectionEvents);
	}

	[Fact]
	public void ApplyResolvedIgnoreOptions_WhenStateAndLabelChange_UpdatesExistingViewModels()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		ApplyResolvedIgnoreOptions(
			coordinator,
			[new ResolvedIgnoreOptionState(IgnoreOptionId.DotFolders, "dot folders (1)", true, true)],
			new Dictionary<IgnoreOptionId, bool> { [IgnoreOptionId.DotFolders] = true });
		var firstIgnoreOption = viewModel.IgnoreOptions[0];
		var collectionEvents = 0;
		viewModel.IgnoreOptions.CollectionChanged += (_, _) => collectionEvents++;

		ApplyResolvedIgnoreOptions(
			coordinator,
			[new ResolvedIgnoreOptionState(IgnoreOptionId.DotFolders, "dot folders (2)", true, false)],
			new Dictionary<IgnoreOptionId, bool> { [IgnoreOptionId.DotFolders] = false });

		Assert.Same(firstIgnoreOption, viewModel.IgnoreOptions[0]);
		Assert.Equal("dot folders (2)", firstIgnoreOption.Label);
		Assert.False(firstIgnoreOption.IsChecked);
		Assert.Equal(0, collectionEvents);
	}

	private static MainWindowViewModel CreateViewModel()
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		return new MainWindowViewModel(localization, new HelpContentProvider());
	}

	private static int GetEventSubscriberCount(object instance, string eventName)
	{
		var eventField = instance.GetType().GetField(
			eventName,
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(eventField);
		var handlers = eventField.GetValue(instance) as Delegate;
		return handlers?.GetInvocationList().Length ?? 0;
	}

	private static void HookAllOptionListeners(
		SelectionSyncCoordinator coordinator,
		MainWindowViewModel viewModel)
	{
		coordinator.HookOptionListeners(viewModel.RootFolders);
		coordinator.HookOptionListeners(viewModel.Extensions);
		coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);
	}

	private static IgnoreSectionScanData CreateDriftedRootSelectionScanData()
	{
		var counts = new IgnoreOptionCounts(
			HiddenFolders: 1,
			HiddenFiles: 1,
			DotFolders: 1,
			DotFiles: 1,
			EmptyFolders: 410,
			ExtensionlessFiles: 1,
			EmptyFiles: 1);

		return new IgnoreSectionScanData(
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".txt" },
			counts,
			counts,
			new IgnoreControllerImpactCounts(GitIgnore: 1, SmartIgnore: 1));
	}

	private static SelectionSyncCoordinator CreateCoordinator(
		MainWindowViewModel viewModel,
		IFileSystemScanner? scanner = null,
		Func<string?>? currentPathProvider = null,
		Func<string, IReadOnlyCollection<string>, IgnoreOptionsAvailability>? availabilityProvider = null)
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		scanner ??= new StubFileSystemScanner();
		var scanOptions = new ScanOptionsUseCase(LegacyWorkspaceScannerTestAdapter.Adapt(scanner));
		var filterService = new FilterOptionSelectionService();
		var ignoreService = new IgnoreOptionsService(localization);
		Func<string, IgnoreRules> buildIgnoreRules = _ => new IgnoreRules(false,
			false,
			false,
			false,
			new HashSet<string>(),
			new HashSet<string>());

		if (availabilityProvider is null)
		{
			return new SelectionSyncCoordinator(
				viewModel,
				scanOptions,
				filterService,
				ignoreService,
				buildIgnoreRules,
				_ => false,
				currentPathProvider ?? (() => null));
		}

		return new SelectionSyncCoordinator(
			viewModel,
			scanOptions,
			filterService,
			ignoreService,
			(path, _, _) => buildIgnoreRules(path),
			availabilityProvider,
			_ => false,
			currentPathProvider ?? (() => null));
	}

	[Fact]
	public void ShouldClearCachesForCurrentPath_WithPreparedTargetProfile_ReturnsFalse()
	{
		var result = SelectionRefreshPolicy.ShouldClearCachesForCurrentPath(
			"C:\\ProjectA",
			"C:\\ProjectB",
			"C:\\ProjectB");

		Assert.False(result);
	}

	[Fact]
	public void ShouldClearCachesForCurrentPath_PathSwitchWithoutPreparedProfile_ReturnsTrue()
	{
		var result = SelectionRefreshPolicy.ShouldClearCachesForCurrentPath(
			"C:\\ProjectA",
			null,
			"C:\\ProjectB");

		Assert.True(result);
	}

	[Fact]
	public void ShouldClearCachesForCurrentPath_NoLastLoadedPath_ReturnsFalse()
	{
		var result = SelectionRefreshPolicy.ShouldClearCachesForCurrentPath(
			null,
			null,
			"C:\\ProjectA");

		Assert.False(result);
	}

	[Fact]
	public void ShouldClearCachesForCurrentPath_SamePath_ReturnsFalse()
	{
		var result = SelectionRefreshPolicy.ShouldClearCachesForCurrentPath(
			"C:\\ProjectA",
			null,
			"C:\\ProjectA");

		Assert.False(result);
	}

	[Fact]
	public void ShouldClearCachesForCurrentPath_PreparedPathForAnotherProject_ReturnsTrue()
	{
		var result = SelectionRefreshPolicy.ShouldClearCachesForCurrentPath(
			"C:\\ProjectA",
			"C:\\ProjectC",
			"C:\\ProjectB");

		Assert.True(result);
	}

	[Fact]
	public void ShouldClearCachesForCurrentPath_PreparedPathCaseDifference_UsesPlatformComparer()
	{
		var result = SelectionRefreshPolicy.ShouldClearCachesForCurrentPath(
			"C:\\ProjectA",
			"c:\\projectb",
			"C:\\ProjectB");

		Assert.Equal(!OperatingSystem.IsWindows(), result);
	}

	[Fact]
	public void ShouldSkipRefreshForPreparedPath_PreparedForAnotherProject_ReturnsTrue()
	{
		var shouldSkip = SelectionRefreshPolicy.ShouldSkipRefreshForPreparedPath(
			"C:\\TargetProject",
			"C:\\AnotherProject");

		Assert.True(shouldSkip);
	}

	[Fact]
	public void ShouldSkipRefreshForPreparedPath_PreparedForCurrentProject_ReturnsFalse()
	{
		var shouldSkip = SelectionRefreshPolicy.ShouldSkipRefreshForPreparedPath(
			"C:\\TargetProject",
			"C:\\TargetProject");

		Assert.False(shouldSkip);
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_WhenPathIsStale_DoesNotMutateOptions()
	{
		var viewModel = CreateViewModel();
		var currentPath = "C:\\ProjectB";
		var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => currentPath);

		coordinator.PopulateIgnoreOptionsForRootSelection([], "C:\\ProjectA");

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
				["src", "tests"],
				false,
				false)
		};
		var coordinator = CreateCoordinator(viewModel, scanner, () => currentPath);

		await coordinator.PopulateRootFoldersAsync("C:\\ProjectA", cancellationToken: TestContext.Current.CancellationToken);

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

		await coordinator.PopulateExtensionsForRootSelectionAsync("C:\\ProjectA", [], cancellationToken: TestContext.Current.CancellationToken);

		Assert.Single(viewModel.Extensions);
		Assert.Equal(".keep", viewModel.Extensions[0].Name);
		Assert.True(viewModel.Extensions[0].IsChecked);
	}

	[Fact]
	public void ApplyRootAndDependentsSnapshot_WhenPathIsStale_DoesNotMutateOptions()
	{
		var viewModel = CreateViewModel();
		viewModel.RootFolders.Add(new SelectionOptionViewModel("keep", true));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".keep", true));
		viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.DotFolders, "dot folders", false));
		var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => "C:\\ProjectB");

		var applied = coordinator.ApplyRootAndDependentsSnapshot(
			"C:\\ProjectA",
			CreateSelectionRefreshSnapshot());

		Assert.False(applied);
		Assert.Single(viewModel.RootFolders);
		Assert.Equal("keep", viewModel.RootFolders[0].Name);
		Assert.Single(viewModel.Extensions);
		Assert.Equal(".keep", viewModel.Extensions[0].Name);
		Assert.Single(viewModel.IgnoreOptions);
		Assert.False(viewModel.IgnoreOptions[0].IsChecked);
	}

	[Fact]
	public void ApplyRootAndDependentsSnapshot_WhenPreparedForTargetPath_AppliesAndConsumesPreparedState()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => "C:\\ProjectB");
		coordinator.ResetProjectProfileSelections("C:\\ProjectA");

		var applied = coordinator.ApplyRootAndDependentsSnapshot(
			"C:\\ProjectA",
			CreateSelectionRefreshSnapshot());

		var session = GetPrivateSession(coordinator);
		Assert.True(applied);
		Assert.Contains(viewModel.RootFolders, option => option.Name == "src" && option.IsChecked);
		Assert.Contains(viewModel.Extensions, option => option.Name == ".cs" && option.IsChecked);
		Assert.Contains(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.DotFolders && option.IsChecked);
		Assert.False(session.HasPreparedSelectionForPath("C:\\ProjectA"));
	}

	[Fact]
	public void ApplyProjectProfileSelections_EmptyExtensions_StillInitializesExtensionCache()
	{
		var coordinator = CreateCoordinator(CreateViewModel());
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: []);

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);

		var session = GetPrivateSession(coordinator);
		Assert.True(session.Extensions.IsInitialized);
		Assert.Empty(session.Extensions.SelectedNames);
	}

	[Fact]
	public void ResetProjectProfileSelections_StoresPreparedPathForTargetProject()
	{
		var coordinator = CreateCoordinator(CreateViewModel());

		coordinator.ResetProjectProfileSelections("C:\\ProjectB");

		var session = GetPrivateSession(coordinator);
		Assert.True(session.HasPreparedSelectionForPath("C:\\ProjectB"));
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
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: [IgnoreOptionId.DotFiles]);

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(DotFiles: 1, HiddenFolders: 1));
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], "C:\\ProjectA");

		Assert.False(viewModel.AllIgnoreChecked);
		Assert.Contains(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.DotFiles && option.IsChecked);
		Assert.Contains(viewModel.IgnoreOptions, option => option.Id != IgnoreOptionId.DotFiles && !option.IsChecked);
	}

	private sealed class CountingRootSelectionSnapshotScanner
		: IFileSystemScanner, IFileSystemScannerRootSelectionSnapshotProvider
	{
		public int RootSelectionSnapshotCount { get; private set; }
		public int TotalScanCount { get; private set; }
		public Action<CancellationToken>? BeforeRootSelectionSnapshot { get; init; }
		public IgnoreSectionScanData RootSelectionSnapshot { get; init; } = new(
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
			new IgnoreOptionCounts(HiddenFolders: 1, HiddenFiles: 1),
			new IgnoreOptionCounts(HiddenFolders: 1, HiddenFiles: 1));

		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			TotalScanCount++;
			return new ScanResult<HashSet<string>>(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
				false,
				false);
		}

		public ScanResult<HashSet<string>> GetRootFileExtensions(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			TotalScanCount++;
			return new ScanResult<HashSet<string>>(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
				false,
				false);
		}

		public ScanResult<List<string>> GetRootFolderNames(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			TotalScanCount++;
			return new ScanResult<List<string>>(["src"], false, false);
		}

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshotForRootSelection(
			string rootPath,
			IReadOnlyCollection<string> selectedRootFolders,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			bool includeDirectoryToggleProbeRoots = false,
			CancellationToken cancellationToken = default,
			bool includeControllerImpactProbeRoots = false)
		{
			RootSelectionSnapshotCount++;
			TotalScanCount++;
			BeforeRootSelectionSnapshot?.Invoke(cancellationToken);
			return new ScanResult<IgnoreSectionScanData>(RootSelectionSnapshot, false, false);
		}
	}

	private static StubLocalizationCatalog CreateCatalog()
	{
		var data = new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Settings.Ignore.SmartIgnore"] = "Smart ignore",
				["Settings.Ignore.UseGitIgnore"] = "Use .gitignore",
				["Settings.Ignore.TrackedGitFilesOnly"] = "Tracked Git files only",
				["Settings.Ignore.HiddenFolders"] = "Hidden folders",
				["Settings.Ignore.HiddenFiles"] = "Hidden files",
				["Settings.Ignore.DotFolders"] = "dot folders",
				["Settings.Ignore.DotFiles"] = "dot files",
				["Settings.Ignore.EmptyFolders"] = "Empty folders",
				["Settings.Ignore.EmptyFiles"] = "Empty files",
				["Settings.Ignore.ExtensionlessFiles"] = "Extensionless files"
			},
			[AppLanguage.Ru] = new Dictionary<string, string>
			{
				["Settings.Ignore.SmartIgnore"] = "Умное исключение",
				["Settings.Ignore.UseGitIgnore"] = "Использовать .gitignore",
				["Settings.Ignore.TrackedGitFilesOnly"] = "Только файлы под контролем Git",
				["Settings.Ignore.HiddenFolders"] = "Скрытые папки",
				["Settings.Ignore.HiddenFiles"] = "Скрытые файлы",
				["Settings.Ignore.DotFolders"] = "Папки с точкой",
				["Settings.Ignore.DotFiles"] = "Файлы с точкой",
				["Settings.Ignore.EmptyFolders"] = "Пустые папки",
				["Settings.Ignore.EmptyFiles"] = "Пустые файлы",
				["Settings.Ignore.ExtensionlessFiles"] = "Файлы без расширения"
			}
		};

		return new StubLocalizationCatalog(data);
	}

	private static void ApplyIgnoreCounts(SelectionSyncCoordinator coordinator, IgnoreOptionCounts ignoreCounts)
	{
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"ApplyExtensionOptions",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method!.Invoke(coordinator, [Array.Empty<SelectionOption>(), 0, ignoreCounts, IgnoreControllerImpactCounts.Empty, true]);
	}

	private static void ApplyRootOptions(
		SelectionSyncCoordinator coordinator,
		IReadOnlyList<SelectionOption> options)
	{
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"ApplyRootOptions",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method!.Invoke(coordinator, [options]);
	}

	private static void ApplyExtensionOptions(
		SelectionSyncCoordinator coordinator,
		IReadOnlyList<SelectionOption> options)
	{
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"ApplyExtensionOptions",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method!.Invoke(
			coordinator,
			[options, 0, IgnoreOptionCounts.Empty, IgnoreControllerImpactCounts.Empty, true]);
	}

	private static void ApplyResolvedIgnoreOptions(
		SelectionSyncCoordinator coordinator,
		IReadOnlyList<ResolvedIgnoreOptionState> options,
		IReadOnlyDictionary<IgnoreOptionId, bool> stateCache)
	{
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"ApplyResolvedIgnoreOptions",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method!.Invoke(coordinator, [options, stateCache]);
	}

	private static void ApplySelectionRefreshSnapshot(
		SelectionSyncCoordinator coordinator,
		SelectionRefreshSnapshot snapshot,
		bool retainPreviousSnapshot = false)
	{
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"ApplySelectionRefreshSnapshot",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method!.Invoke(coordinator, [snapshot, retainPreviousSnapshot]);
	}

	private static void ApplyCurrentSelectionState(
		SelectionSyncCoordinator coordinator,
		MainWindowViewModel viewModel,
		SelectionRefreshSnapshot snapshot)
	{
		viewModel.AllRootFoldersChecked = snapshot.RootOptions!.All(static option => option.IsChecked);
		viewModel.AllExtensionsChecked = snapshot.EffectiveExtensionOptions.All(static option => option.IsChecked);
		ApplyRootOptions(coordinator, snapshot.RootOptions!);
		ApplyExtensionOptions(coordinator, snapshot.EffectiveExtensionOptions);
		coordinator.UpdateExtensionsSelectionCache();
		ApplyResolvedIgnoreOptions(
			coordinator,
			snapshot.IgnoreOptions,
			snapshot.IgnoreOptionStateCache);
	}

	private static bool TryRestoreKnownSelectionSnapshot(
		SelectionSyncCoordinator coordinator,
		string path,
		SelectionRefreshOrigin origin = SelectionRefreshOrigin.Unknown,
		IgnoreOptionId? changedIgnoreOptionId = null)
	{
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"TryRestoreKnownSelectionSnapshot",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		return Assert.IsType<bool>(method!.Invoke(
			coordinator,
			[path, origin, changedIgnoreOptionId]));
	}

	private static SelectionRefreshSnapshot CreateSelectionRefreshSnapshot()
	{
		return new SelectionRefreshSnapshot(
			RootOptions:
			[
				new SelectionOption("src", true),
				new SelectionOption("docs", false)
			],
			ExtensionOptions:
			[
				new SelectionOption(".cs", true),
				new SelectionOption(".md", false)
			],
			IgnoreOptions:
			[
				new ResolvedIgnoreOptionState(IgnoreOptionId.DotFolders, "dot folders", true, true),
				new ResolvedIgnoreOptionState(IgnoreOptionId.EmptyFiles, "empty files", true, false)
			],
			ExtensionlessEntriesCount: 0,
			HasIgnoreOptionCounts: true,
			IgnoreOptionCounts: new IgnoreOptionCounts(DotFolders: 1),
			ControllerImpactCounts: IgnoreControllerImpactCounts.Empty,
			IgnoreOptionStateCache: new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.DotFolders] = true,
				[IgnoreOptionId.EmptyFiles] = false
			},
			RootAccessDenied: false,
			HadAccessDenied: false);
	}

	private static SelectionRefreshSnapshot CreateReversibleSelectionRefreshSnapshot(
		IgnoreOptionId? uncheckedIgnoreOption = null,
		bool rootChecked = true,
		bool extensionChecked = true,
		int emptyFolderCount = 433)
	{
		var ignoreOptions = Enum.GetValues<IgnoreOptionId>()
			.Where(static optionId => optionId != IgnoreOptionId.TrackedGitFilesOnly)
			.Select(optionId => new ResolvedIgnoreOptionState(
				optionId,
				$"{optionId} ({(optionId == IgnoreOptionId.EmptyFolders ? emptyFolderCount : 1)})",
				DefaultChecked: true,
				IsChecked: optionId != uncheckedIgnoreOption))
			.ToArray();
		var ignoreStateCache = ignoreOptions.ToDictionary(
			static option => option.Id,
			static option => option.IsChecked);

		return new SelectionRefreshSnapshot(
			RootOptions: [new SelectionOption("src", rootChecked)],
			ExtensionOptions: [new SelectionOption(".cs", extensionChecked)],
			IgnoreOptions: ignoreOptions,
			ExtensionlessEntriesCount: 1,
			HasIgnoreOptionCounts: true,
			IgnoreOptionCounts: new IgnoreOptionCounts(
				HiddenFolders: 1,
				HiddenFiles: 1,
				DotFolders: 1,
				DotFiles: 1,
				EmptyFolders: emptyFolderCount,
				ExtensionlessFiles: 1,
				EmptyFiles: 1),
			ControllerImpactCounts: new IgnoreControllerImpactCounts(
				GitIgnore: 1,
				SmartIgnore: 1),
			IgnoreOptionStateCache: ignoreStateCache,
			RootAccessDenied: false,
			HadAccessDenied: false);
	}

	private static SelectionRefreshSnapshot CreateIgnoreReversalCurrentSnapshot(
		SelectionRefreshSnapshot stableSnapshot,
		SelectionRefreshSnapshot reversibleSnapshot,
		IgnoreOptionId changedOptionId)
	{
		var reversedState = reversibleSnapshot.IgnoreOptionStateCache[changedOptionId];
		var ignoreOptions = stableSnapshot.IgnoreOptions
			.Select(option => option.Id == changedOptionId
				? option with { IsChecked = reversedState }
				: option)
			.ToArray();
		var stateCache = new Dictionary<IgnoreOptionId, bool>(stableSnapshot.IgnoreOptionStateCache)
		{
			[changedOptionId] = reversedState
		};

		return stableSnapshot with
		{
			IgnoreOptions = ignoreOptions,
			IgnoreOptionStateCache = stateCache
		};
	}

	private static void MarkSelectionRefreshDirty(SelectionSyncCoordinator coordinator)
	{
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"MarkSelectionRefreshDirty",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method!.Invoke(coordinator, []);
	}

	private static ProjectSelectionSessionState GetPrivateSession(SelectionSyncCoordinator coordinator)
	{
		var field = typeof(SelectionSyncCoordinator).GetField(
			"_session",
			BindingFlags.NonPublic | BindingFlags.Instance);
		Assert.NotNull(field);
		return (ProjectSelectionSessionState)field.GetValue(coordinator)!;
	}

	private static int GetPrivateIgnoreOptionsVersion(SelectionSyncCoordinator coordinator)
	{
		var field = typeof(SelectionSyncCoordinator).GetField(
			"_ignoreOptionsVersion",
			BindingFlags.NonPublic | BindingFlags.Instance);
		Assert.NotNull(field);
		return (int)field.GetValue(coordinator)!;
	}

	private static IgnoreOptionCounts GetPrivateIgnoreOptionCounts(SelectionSyncCoordinator coordinator)
	{
		var field = typeof(SelectionSyncCoordinator).GetField(
			"_ignoreOptionCounts",
			BindingFlags.NonPublic | BindingFlags.Instance);
		Assert.NotNull(field);
		return (IgnoreOptionCounts)field.GetValue(coordinator)!;
	}
}


