using DevProjex.Application.Models;

namespace DevProjex.Tests.Unit;

public sealed class SelectionSyncCoordinatorIgnoreStateRegressionTests
{
	private const string ProjectPath = @"C:\Workspace\ProjectA";
	private const string NextProjectPath = @"C:\Workspace\ProjectB";

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_NewlyVisibleOption_UsesDefaultCheckedAfterManualSelectionChange()
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel);
		coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);

		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(HiddenFolders: 1, HiddenFiles: 1));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		GetIgnoreOption(viewModel, IgnoreOptionId.HiddenFolders).IsChecked = false;

		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(HiddenFolders: 1, HiddenFiles: 1, ExtensionlessFiles: 2));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		Assert.False(GetIgnoreOption(viewModel, IgnoreOptionId.HiddenFolders).IsChecked);
		Assert.True(GetIgnoreOption(viewModel, IgnoreOptionId.HiddenFiles).IsChecked);
		Assert.True(GetIgnoreOption(viewModel, IgnoreOptionId.ExtensionlessFiles).IsChecked);
		Assert.False(viewModel.AllIgnoreChecked);
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_TransientlyHiddenUncheckedOption_RestoresUncheckedState()
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel);
		coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);

		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(HiddenFiles: 1, ExtensionlessFiles: 2));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		GetIgnoreOption(viewModel, IgnoreOptionId.ExtensionlessFiles).IsChecked = false;

		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(HiddenFiles: 1));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);
		Assert.DoesNotContain(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.ExtensionlessFiles);

		GetIgnoreOption(viewModel, IgnoreOptionId.HiddenFiles).IsChecked = false;

		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(HiddenFiles: 1, ExtensionlessFiles: 2));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		Assert.False(GetIgnoreOption(viewModel, IgnoreOptionId.ExtensionlessFiles).IsChecked);
		Assert.False(GetIgnoreOption(viewModel, IgnoreOptionId.HiddenFiles).IsChecked);
		Assert.False(viewModel.AllIgnoreChecked);
	}

	[Fact]
	public void ResetProjectProfileSelections_NewProject_RestoresExtensionlessDefaultCheckedState()
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel);
		coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);

		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(HiddenFolders: 1, ExtensionlessFiles: 2));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		GetIgnoreOption(viewModel, IgnoreOptionId.ExtensionlessFiles).IsChecked = false;

		coordinator.ResetProjectProfileSelections(NextProjectPath);
		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(HiddenFolders: 1, ExtensionlessFiles: 2));
		coordinator.PopulateIgnoreOptionsForRootSelection([], NextProjectPath);

		Assert.True(GetIgnoreOption(viewModel, IgnoreOptionId.ExtensionlessFiles).IsChecked);
		Assert.True(GetIgnoreOption(viewModel, IgnoreOptionId.HiddenFolders).IsChecked);
		Assert.True(viewModel.AllIgnoreChecked);
	}

	[Fact]
	public void HandleIgnoreAllChanged_NewlyVisibleOptionRespectsAllOffIntent()
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel);
		coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);

		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(HiddenFolders: 1));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		coordinator.HandleIgnoreAllChanged(false, currentPath: null);

		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(HiddenFolders: 1, ExtensionlessFiles: 2));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		Assert.False(GetIgnoreOption(viewModel, IgnoreOptionId.HiddenFolders).IsChecked);
		Assert.False(GetIgnoreOption(viewModel, IgnoreOptionId.ExtensionlessFiles).IsChecked);
		Assert.False(viewModel.AllIgnoreChecked);
	}

	[Fact]
	public void HandleIgnoreAllChanged_NewlyVisibleOptionRespectsAllOffIntentWhenNoKnownOptionsExist()
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel);
		coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);

		coordinator.HandleIgnoreAllChanged(false, currentPath: null);

		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(ExtensionlessFiles: 2));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		Assert.False(GetIgnoreOption(viewModel, IgnoreOptionId.ExtensionlessFiles).IsChecked);
		Assert.False(viewModel.AllIgnoreChecked);
	}

	[Fact]
	public void GetSelectedIgnoreOptionIds_HiddenCachedOptionsDoNotAffectRuntimeRules()
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel);

		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: [],
			IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.DotFolders] = true,
				[IgnoreOptionId.EmptyFolders] = true
			});
		coordinator.ApplyProjectProfileSelections(ProjectPath, profile);

		ApplyIgnoreCounts(coordinator, IgnoreOptionCounts.Empty);
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		var selected = coordinator.GetSelectedIgnoreOptionIds();
		Assert.DoesNotContain(IgnoreOptionId.DotFolders, selected);
		Assert.DoesNotContain(IgnoreOptionId.EmptyFolders, selected);
		Assert.Empty(viewModel.IgnoreOptions);
	}

	[Fact]
	public void GetSelectedIgnoreOptionIds_MixedVisibleAndHiddenCachedStatesReturnsOnlyVisibleCheckedOptions()
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel);

		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions:
			[
				IgnoreOptionId.DotFiles,
				IgnoreOptionId.DotFolders,
				IgnoreOptionId.EmptyFolders,
				IgnoreOptionId.ExtensionlessFiles
			],
			IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.DotFiles] = true,
				[IgnoreOptionId.DotFolders] = true,
				[IgnoreOptionId.EmptyFolders] = true,
				[IgnoreOptionId.ExtensionlessFiles] = true
			});
		coordinator.ApplyProjectProfileSelections(ProjectPath, profile);

		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(DotFiles: 1, ExtensionlessFiles: 2));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		var selected = coordinator.GetSelectedIgnoreOptionIds();
		Assert.Contains(IgnoreOptionId.DotFiles, selected);
		Assert.Contains(IgnoreOptionId.ExtensionlessFiles, selected);
		Assert.DoesNotContain(IgnoreOptionId.DotFolders, selected);
		Assert.DoesNotContain(IgnoreOptionId.EmptyFolders, selected);

		GetIgnoreOption(viewModel, IgnoreOptionId.DotFiles).IsChecked = false;
		GetIgnoreOption(viewModel, IgnoreOptionId.ExtensionlessFiles).IsChecked = false;

		selected = coordinator.GetSelectedIgnoreOptionIds();
		Assert.Empty(selected);
		Assert.True(coordinator.SnapshotIgnoreOptionStatesForPersistence()![IgnoreOptionId.DotFolders]);
		Assert.True(coordinator.SnapshotIgnoreOptionStatesForPersistence()![IgnoreOptionId.EmptyFolders]);
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_ExplicitUncheckedGitController_RemainsVisibleWithZeroImpact()
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, includeGitIgnore: true);
		coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);

		ApplyIgnoreCounts(
			coordinator,
			IgnoreOptionCounts.Empty,
			new IgnoreControllerImpactCounts(GitIgnore: 1));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		var gitIgnore = GetIgnoreOption(viewModel, IgnoreOptionId.UseGitIgnore);
		Assert.True(gitIgnore.IsChecked);

		gitIgnore.IsChecked = false;

		ApplyIgnoreCounts(coordinator, IgnoreOptionCounts.Empty, IgnoreControllerImpactCounts.Empty);
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		Assert.False(GetIgnoreOption(viewModel, IgnoreOptionId.UseGitIgnore).IsChecked);
		Assert.False(viewModel.AllIgnoreChecked);
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_CheckedGitController_HidesWhenImpactDropsToZero()
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, includeGitIgnore: true);
		coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);

		ApplyIgnoreCounts(
			coordinator,
			IgnoreOptionCounts.Empty,
			new IgnoreControllerImpactCounts(GitIgnore: 1));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		Assert.True(GetIgnoreOption(viewModel, IgnoreOptionId.UseGitIgnore).IsChecked);

		ApplyIgnoreCounts(coordinator, IgnoreOptionCounts.Empty, IgnoreControllerImpactCounts.Empty);
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		Assert.DoesNotContain(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.UseGitIgnore);
		Assert.DoesNotContain(IgnoreOptionId.UseGitIgnore, coordinator.GetSelectedIgnoreOptionIds());
		Assert.True(coordinator.SnapshotIgnoreOptionStatesForPersistence()![IgnoreOptionId.UseGitIgnore]);
	}

	private static IgnoreOptionViewModel GetIgnoreOption(MainWindowViewModel viewModel, IgnoreOptionId id)
	{
		return Assert.Single(viewModel.IgnoreOptions, option => option.Id == id);
	}

	private static void ApplyIgnoreCounts(
		SelectionSyncCoordinator coordinator,
		IgnoreOptionCounts ignoreCounts,
		IgnoreControllerImpactCounts controllerImpactCounts = default)
	{
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"ApplyExtensionOptions",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method!.Invoke(coordinator, [Array.Empty<SelectionOption>(), 0, ignoreCounts, controllerImpactCounts, true]);
	}

	private static SelectionSyncCoordinator CreateCoordinator(
		MainWindowViewModel viewModel,
		bool includeGitIgnore = false,
		bool includeSmartIgnore = false)
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		var scanner = new StubFileSystemScanner();
		var scanOptions = new ScanOptionsUseCase(scanner);
		var filterSelectionService = new FilterOptionSelectionService();
		var ignoreOptionsService = new IgnoreOptionsService(localization);

		return new SelectionSyncCoordinator(
			viewModel,
			scanOptions,
			filterSelectionService,
			ignoreOptionsService,
			(_, _, _) => new IgnoreRules(
				IgnoreHiddenFolders: false,
				IgnoreHiddenFiles: false,
				IgnoreDotFolders: false,
				IgnoreDotFiles: false,
				SmartIgnoredFolders: new HashSet<string>(),
				SmartIgnoredFiles: new HashSet<string>()),
			(_, _) => new IgnoreOptionsAvailability(
				IncludeGitIgnore: includeGitIgnore,
				IncludeSmartIgnore: includeSmartIgnore,
				ShowAdvancedCounts: true),
			_ => false,
			() => null);
	}

	private static MainWindowViewModel CreateViewModel()
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		return new MainWindowViewModel(localization, new HelpContentProvider());
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
				["Settings.Ignore.DotFiles"] = "dot files",
				["Settings.Ignore.EmptyFolders"] = "Empty folders",
				["Settings.Ignore.EmptyFiles"] = "Empty files",
				["Settings.Ignore.ExtensionlessFiles"] = "Files without extension"
			}
		};

		return new StubLocalizationCatalog(data);
	}
}
