using DevProjex.Application.Models;

namespace DevProjex.Tests.Unit;

public sealed class SelectionSyncCoordinatorDynamicIgnoreNegativeMatrixTests
{
	private const string ProjectPath = @"C:\Workspace\ProjectA";
	private const string NextProjectPath = @"C:\Workspace\ProjectB";

	[Theory]
	[MemberData(nameof(DynamicOptionIds))]
	public void DynamicIgnoreNegativeMatrix_UnavailableOption_DoesNotAppearAfterVisibleOptionChanges(
		IgnoreOptionId dynamicOptionId)
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel);
		coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);

		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(HiddenFolders: 1, DotFiles: 1));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);
		GetIgnoreOption(viewModel, IgnoreOptionId.HiddenFolders).IsChecked = false;
		GetIgnoreOption(viewModel, IgnoreOptionId.HiddenFolders).IsChecked = true;
		GetIgnoreOption(viewModel, IgnoreOptionId.DotFiles).IsChecked = false;

		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(HiddenFolders: 1, DotFiles: 1));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		Assert.DoesNotContain(viewModel.IgnoreOptions, option => option.Id == dynamicOptionId);
		Assert.False(viewModel.AllIgnoreChecked);
	}

	[Theory]
	[MemberData(nameof(DynamicOptionIds))]
	public void DynamicIgnoreNegativeMatrix_HiddenUncheckedOption_DoesNotFlipToCheckedWhenVisibleOptionsAreAllChecked(
		IgnoreOptionId dynamicOptionId)
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel);
		coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);

		ApplyIgnoreCounts(coordinator, BuildCounts(dynamicOptionId, dynamicVisible: true));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);
		GetIgnoreOption(viewModel, dynamicOptionId).IsChecked = false;

		ApplyIgnoreCounts(coordinator, IgnoreOptionCounts.Empty);
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		Assert.DoesNotContain(viewModel.IgnoreOptions, option => option.Id == dynamicOptionId);
		Assert.True(viewModel.AllIgnoreChecked);

		ApplyIgnoreCounts(coordinator, BuildCounts(dynamicOptionId, dynamicVisible: true));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		Assert.False(GetIgnoreOption(viewModel, dynamicOptionId).IsChecked);
		Assert.False(viewModel.AllIgnoreChecked);
	}

	[Theory]
	[MemberData(nameof(DynamicOptionIds))]
	public void DynamicIgnoreNegativeMatrix_EmptyProfile_DoesNotAutoCheckDynamicOptionWhenItAppears(
		IgnoreOptionId dynamicOptionId)
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel);

		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: []);

		coordinator.ApplyProjectProfileSelections(ProjectPath, profile);
		ApplyIgnoreCounts(coordinator, IgnoreOptionCounts.Empty);
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);
		Assert.DoesNotContain(viewModel.IgnoreOptions, option => option.Id == dynamicOptionId);

		ApplyIgnoreCounts(coordinator, BuildCounts(dynamicOptionId, dynamicVisible: true));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		Assert.False(GetIgnoreOption(viewModel, dynamicOptionId).IsChecked);
		Assert.False(viewModel.AllIgnoreChecked);
	}

	[Theory]
	[MemberData(nameof(DynamicOptionIds))]
	public void DynamicIgnoreNegativeMatrix_UnavailableOnlyProfile_DoesNotPromoteDynamicOptionToChecked(
		IgnoreOptionId dynamicOptionId)
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel);

		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: [IgnoreOptionId.UseGitIgnore]);

		coordinator.ApplyProjectProfileSelections(ProjectPath, profile);
		ApplyIgnoreCounts(coordinator, IgnoreOptionCounts.Empty);
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);
		Assert.DoesNotContain(viewModel.IgnoreOptions, option => option.Id == dynamicOptionId);

		ApplyIgnoreCounts(coordinator, BuildCounts(dynamicOptionId, dynamicVisible: true));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		Assert.False(GetIgnoreOption(viewModel, dynamicOptionId).IsChecked);
		Assert.False(viewModel.AllIgnoreChecked);
		Assert.Contains(IgnoreOptionId.UseGitIgnore, coordinator.GetSelectedIgnoreOptionIds());
	}

	[Theory]
	[MemberData(nameof(DynamicOptionIds))]
	public void DynamicIgnoreNegativeMatrix_NewProjectWithoutProfile_DoesNotReusePreviousProjectUncheckedDynamicState(
		IgnoreOptionId dynamicOptionId)
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel);
		coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);

		ApplyIgnoreCounts(coordinator, BuildCounts(dynamicOptionId, dynamicVisible: true));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);
		GetIgnoreOption(viewModel, dynamicOptionId).IsChecked = false;

		coordinator.ResetProjectProfileSelections(NextProjectPath);
		ApplyIgnoreCounts(coordinator, BuildCounts(dynamicOptionId, dynamicVisible: true));
		coordinator.PopulateIgnoreOptionsForRootSelection([], NextProjectPath);

		Assert.True(GetIgnoreOption(viewModel, dynamicOptionId).IsChecked);
		Assert.Contains(dynamicOptionId, coordinator.GetSelectedIgnoreOptionIds());
		Assert.True(viewModel.AllIgnoreChecked);
	}

	public static IEnumerable<object[]> DynamicOptionIds()
	{
		yield return [IgnoreOptionId.EmptyFolders];
		yield return [IgnoreOptionId.EmptyFiles];
		yield return [IgnoreOptionId.ExtensionlessFiles];
	}

	private static IgnoreOptionViewModel GetIgnoreOption(MainWindowViewModel viewModel, IgnoreOptionId id)
	{
		return Assert.Single(viewModel.IgnoreOptions.Where(option => option.Id == id));
	}

	private static IgnoreOptionCounts BuildCounts(IgnoreOptionId dynamicOptionId, bool dynamicVisible)
	{
		if (!dynamicVisible)
			return IgnoreOptionCounts.Empty;

		return dynamicOptionId switch
		{
			IgnoreOptionId.EmptyFolders => new IgnoreOptionCounts(EmptyFolders: 2),
			IgnoreOptionId.EmptyFiles => new IgnoreOptionCounts(EmptyFiles: 3),
			IgnoreOptionId.ExtensionlessFiles => new IgnoreOptionCounts(ExtensionlessFiles: 4),
			_ => throw new ArgumentOutOfRangeException(nameof(dynamicOptionId), dynamicOptionId, null)
		};
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
				SmartIgnoredFiles: new HashSet<string>())
			{
				UseGitIgnore = true
			},
			(_, _) => new IgnoreOptionsAvailability(
				IncludeGitIgnore: true,
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
				["Settings.Ignore.EmptyFolders"] = "Empty folders",
				["Settings.Ignore.EmptyFiles"] = "Empty files",
				["Settings.Ignore.ExtensionlessFiles"] = "Files without extension"
			}
		};

		return new StubLocalizationCatalog(data);
	}
}
