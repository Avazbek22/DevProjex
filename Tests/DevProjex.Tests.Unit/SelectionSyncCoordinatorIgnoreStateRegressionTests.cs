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
	public void HandleIgnoreAllChanged_UncheckedIntent_AppliesToOptionsThatAppearLater()
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

	private static IgnoreOptionViewModel GetIgnoreOption(MainWindowViewModel viewModel, IgnoreOptionId id)
	{
		return Assert.Single(viewModel.IgnoreOptions.Where(option => option.Id == id));
	}

	private static void ApplyIgnoreCounts(SelectionSyncCoordinator coordinator, IgnoreOptionCounts ignoreCounts)
	{
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"ApplyExtensionOptions",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method!.Invoke(coordinator, [Array.Empty<SelectionOption>(), 0, ignoreCounts, true]);
	}

	private static SelectionSyncCoordinator CreateCoordinator(MainWindowViewModel viewModel)
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
				IncludeGitIgnore: false,
				IncludeSmartIgnore: false,
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
				["Settings.Ignore.ExtensionlessFiles"] = "Files without extension"
			}
		};

		return new StubLocalizationCatalog(data);
	}
}
