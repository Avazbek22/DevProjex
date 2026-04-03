using DevProjex.Application.Models;

namespace DevProjex.Tests.Unit;

public sealed class SelectionSyncCoordinatorDynamicIgnoreOptionMatrixTests
{
	private const string ProjectPath = @"C:\Workspace\ProjectA";
	private const string NextProjectPath = @"C:\Workspace\ProjectB";

	[Theory]
	[MemberData(nameof(NewlyVisibleOptionCases))]
	public void DynamicIgnoreOptionMatrix_NewlyVisibleOption_UsesDefaultCheckedAfterOtherManualChange(
		IgnoreOptionId dynamicOptionId,
		IgnoreOptionId manuallyUncheckedOptionId)
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel);
		coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);

		ApplyIgnoreCounts(coordinator, BuildCounts(dynamicOptionId, dynamicVisible: false, manuallyUncheckedOptionId));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);
		GetIgnoreOption(viewModel, manuallyUncheckedOptionId).IsChecked = false;

		ApplyIgnoreCounts(coordinator, BuildCounts(dynamicOptionId, dynamicVisible: true, manuallyUncheckedOptionId));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		Assert.False(GetIgnoreOption(viewModel, manuallyUncheckedOptionId).IsChecked);
		Assert.True(GetIgnoreOption(viewModel, dynamicOptionId).IsChecked);
		Assert.False(viewModel.AllIgnoreChecked);
	}

	[Theory]
	[MemberData(nameof(HiddenOptionRestoreCases))]
	public void DynamicIgnoreOptionMatrix_HiddenUncheckedOption_RestoresUncheckedStateAfterOtherChanges(
		IgnoreOptionId dynamicOptionId,
		IgnoreOptionId otherOptionId)
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel);
		coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);

		ApplyIgnoreCounts(coordinator, BuildCounts(dynamicOptionId, dynamicVisible: true, otherOptionId));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);
		GetIgnoreOption(viewModel, dynamicOptionId).IsChecked = false;

		ApplyIgnoreCounts(coordinator, BuildCounts(dynamicOptionId, dynamicVisible: false, otherOptionId));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);
		Assert.DoesNotContain(viewModel.IgnoreOptions, option => option.Id == dynamicOptionId);

		GetIgnoreOption(viewModel, otherOptionId).IsChecked = false;

		ApplyIgnoreCounts(coordinator, BuildCounts(dynamicOptionId, dynamicVisible: true, otherOptionId));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		Assert.False(GetIgnoreOption(viewModel, dynamicOptionId).IsChecked);
		Assert.False(GetIgnoreOption(viewModel, otherOptionId).IsChecked);
		Assert.False(viewModel.AllIgnoreChecked);
	}

	[Theory]
	[MemberData(nameof(DynamicOptionIds))]
	public void DynamicIgnoreOptionMatrix_ResetProjectSelections_RestoresDefaultsForNewProject(
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
		Assert.True(viewModel.AllIgnoreChecked);
	}

	[Theory]
	[MemberData(nameof(DynamicOptionIds))]
	public void DynamicIgnoreOptionMatrix_AllIgnoreIntent_AppliesToOptionsThatAppearLater(
		IgnoreOptionId dynamicOptionId)
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel);
		coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);

		ApplyIgnoreCounts(coordinator, BuildCounts(dynamicOptionId, dynamicVisible: false, IgnoreOptionId.HiddenFolders));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);
		coordinator.HandleIgnoreAllChanged(false, currentPath: null);

		ApplyIgnoreCounts(coordinator, BuildCounts(dynamicOptionId, dynamicVisible: true, IgnoreOptionId.HiddenFolders));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		Assert.False(GetIgnoreOption(viewModel, dynamicOptionId).IsChecked);
		Assert.False(GetIgnoreOption(viewModel, IgnoreOptionId.HiddenFolders).IsChecked);
		Assert.False(viewModel.AllIgnoreChecked);
	}

	[Theory]
	[MemberData(nameof(DynamicOptionIds))]
	public void DynamicIgnoreOptionMatrix_ProfileSelection_RestoresDynamicOptionWhenItBecomesAvailable(
		IgnoreOptionId dynamicOptionId)
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel);

		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: [dynamicOptionId]);

		coordinator.ApplyProjectProfileSelections(ProjectPath, profile);
		ApplyIgnoreCounts(coordinator, BuildCounts(dynamicOptionId, dynamicVisible: false));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);
		Assert.DoesNotContain(viewModel.IgnoreOptions, option => option.Id == dynamicOptionId);

		ApplyIgnoreCounts(coordinator, BuildCounts(dynamicOptionId, dynamicVisible: true));
		coordinator.PopulateIgnoreOptionsForRootSelection([], ProjectPath);

		Assert.True(GetIgnoreOption(viewModel, dynamicOptionId).IsChecked);
	}

	public static IEnumerable<object[]> NewlyVisibleOptionCases()
	{
		foreach (var dynamicOptionId in DynamicOptionIds().Select(item => (IgnoreOptionId)item[0]))
		{
			yield return [dynamicOptionId, IgnoreOptionId.HiddenFolders];
			yield return [dynamicOptionId, IgnoreOptionId.HiddenFiles];
			yield return [dynamicOptionId, IgnoreOptionId.DotFolders];
			yield return [dynamicOptionId, IgnoreOptionId.DotFiles];
		}
	}

	public static IEnumerable<object[]> HiddenOptionRestoreCases()
	{
		foreach (var dynamicOptionId in DynamicOptionIds().Select(item => (IgnoreOptionId)item[0]))
		{
			yield return [dynamicOptionId, IgnoreOptionId.HiddenFolders];
			yield return [dynamicOptionId, IgnoreOptionId.HiddenFiles];
			yield return [dynamicOptionId, IgnoreOptionId.DotFolders];
			yield return [dynamicOptionId, IgnoreOptionId.DotFiles];
		}
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

	private static IgnoreOptionCounts BuildCounts(
		IgnoreOptionId dynamicOptionId,
		bool dynamicVisible,
		IgnoreOptionId? additionalVisibleOptionId = null)
	{
		var counts = IgnoreOptionCounts.Empty;
		if (additionalVisibleOptionId is not null)
			counts = counts.Add(BuildSingleCount(additionalVisibleOptionId.Value, 1));

		if (!dynamicVisible)
			return counts;

		return counts.Add(dynamicOptionId switch
		{
			IgnoreOptionId.EmptyFolders => new IgnoreOptionCounts(EmptyFolders: 2),
			IgnoreOptionId.EmptyFiles => new IgnoreOptionCounts(EmptyFiles: 3),
			IgnoreOptionId.ExtensionlessFiles => new IgnoreOptionCounts(ExtensionlessFiles: 4),
			_ => throw new ArgumentOutOfRangeException(nameof(dynamicOptionId), dynamicOptionId, null)
		});
	}

	private static IgnoreOptionCounts BuildSingleCount(IgnoreOptionId optionId, int count)
	{
		return optionId switch
		{
			IgnoreOptionId.HiddenFolders => new IgnoreOptionCounts(HiddenFolders: count),
			IgnoreOptionId.HiddenFiles => new IgnoreOptionCounts(HiddenFiles: count),
			IgnoreOptionId.DotFolders => new IgnoreOptionCounts(DotFolders: count),
			IgnoreOptionId.DotFiles => new IgnoreOptionCounts(DotFiles: count),
			IgnoreOptionId.EmptyFolders => new IgnoreOptionCounts(EmptyFolders: count),
			IgnoreOptionId.EmptyFiles => new IgnoreOptionCounts(EmptyFiles: count),
			IgnoreOptionId.ExtensionlessFiles => new IgnoreOptionCounts(ExtensionlessFiles: count),
			_ => throw new ArgumentOutOfRangeException(nameof(optionId), optionId, null)
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
				["Settings.Ignore.EmptyFolders"] = "Empty folders",
				["Settings.Ignore.EmptyFiles"] = "Empty files",
				["Settings.Ignore.ExtensionlessFiles"] = "Files without extension"
			}
		};

		return new StubLocalizationCatalog(data);
	}
}
