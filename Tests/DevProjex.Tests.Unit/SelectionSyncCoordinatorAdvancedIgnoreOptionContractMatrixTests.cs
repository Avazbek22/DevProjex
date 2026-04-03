namespace DevProjex.Tests.Unit;

public sealed class SelectionSyncCoordinatorAdvancedIgnoreOptionContractMatrixTests
{
	private const string ProjectPath = @"C:\Workspace\Project";

	public static IEnumerable<object[]> EffectiveCountCases()
	{
		foreach (var optionCase in AdvancedOptionCases())
		{
			foreach (var effectiveCount in new[] { 0, 1, 2, 5 })
				yield return [optionCase, effectiveCount];
		}
	}

	[Theory]
	[MemberData(nameof(EffectiveCountCases))]
	public void PopulateIgnoreOptionsForRootSelection_EffectiveCountControlsVisibilityAndLabel(
		IgnoreOptionCase optionCase,
		int effectiveCount)
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, includeGitIgnore: false);

		ApplyScanState(
			coordinator,
			BuildSingleCount(optionCase.Id, effectiveCount),
			hasIgnoreCounts: true);

		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], ProjectPath);

		var option = viewModel.IgnoreOptions.SingleOrDefault(item => item.Id == optionCase.Id);
		if (effectiveCount == 0)
		{
			Assert.Null(option);
			Assert.Empty(viewModel.IgnoreOptions);
			Assert.False(viewModel.AllIgnoreChecked);
			return;
		}

		Assert.NotNull(option);
		Assert.Single(viewModel.IgnoreOptions);
		Assert.Equal($"{optionCase.BaseLabel} ({effectiveCount})", option!.Label);
		Assert.True(option.IsChecked);
		Assert.True(viewModel.AllIgnoreChecked);
	}

	public static IEnumerable<object[]> ReappearingUncheckedCases()
	{
		foreach (var optionCase in AdvancedOptionCases())
		{
			foreach (var reappearingCount in new[] { 1, 4 })
				yield return [optionCase, reappearingCount];
		}
	}

	[Theory]
	[MemberData(nameof(ReappearingUncheckedCases))]
	public void PopulateIgnoreOptionsForRootSelection_HideAndReappear_PreservesUncheckedStateAndUpdatedLabel(
		IgnoreOptionCase optionCase,
		int reappearingCount)
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, includeGitIgnore: false);

		ApplyScanState(
			coordinator,
			BuildSingleCount(optionCase.Id, 3),
			hasIgnoreCounts: true);
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], ProjectPath);

		var option = Assert.Single(viewModel.IgnoreOptions);
		option.IsChecked = false;
		coordinator.UpdateIgnoreSelectionCache();
		coordinator.SyncIgnoreAllCheckbox();

		ApplyScanState(
			coordinator,
			IgnoreOptionCounts.Empty,
			hasIgnoreCounts: true);
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], ProjectPath);
		Assert.DoesNotContain(viewModel.IgnoreOptions, item => item.Id == optionCase.Id);

		ApplyScanState(
			coordinator,
			BuildSingleCount(optionCase.Id, reappearingCount),
			hasIgnoreCounts: true);
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], ProjectPath);

		option = Assert.Single(viewModel.IgnoreOptions);
		Assert.Equal(optionCase.Id, option.Id);
		Assert.False(option.IsChecked);
		Assert.Equal($"{optionCase.BaseLabel} ({reappearingCount})", option.Label);
		Assert.False(viewModel.AllIgnoreChecked);
	}

	public static IEnumerable<object[]> AllIgnoreCases()
	{
		foreach (var optionCase in AdvancedOptionCases())
			yield return [optionCase];
	}

	[Theory]
	[MemberData(nameof(AllIgnoreCases))]
	public void PopulateIgnoreOptionsForRootSelection_WhenUncheckedAdvancedOptionDisappears_AllIgnoreTracksRemainingVisibleOptions(
		IgnoreOptionCase optionCase)
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, includeGitIgnore: true);

		ApplyScanState(
			coordinator,
			BuildSingleCount(optionCase.Id, 2),
			hasIgnoreCounts: true);
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], ProjectPath);

		var target = Assert.Single(viewModel.IgnoreOptions.Where(item => item.Id == optionCase.Id));
		target.IsChecked = false;
		coordinator.UpdateIgnoreSelectionCache();
		coordinator.SyncIgnoreAllCheckbox();
		Assert.False(viewModel.AllIgnoreChecked);

		ApplyScanState(
			coordinator,
			IgnoreOptionCounts.Empty,
			hasIgnoreCounts: true);
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], ProjectPath);

		Assert.DoesNotContain(viewModel.IgnoreOptions, item => item.Id == optionCase.Id);
		Assert.Single(viewModel.IgnoreOptions);
		Assert.Equal(IgnoreOptionId.UseGitIgnore, viewModel.IgnoreOptions[0].Id);
		Assert.True(viewModel.IgnoreOptions[0].IsChecked);
		Assert.True(viewModel.AllIgnoreChecked);
	}

	[Theory]
	[MemberData(nameof(AllIgnoreCases))]
	public void PopulateIgnoreOptionsForRootSelection_WhenUncheckedAdvancedOptionReappears_AllIgnoreBecomesFalseAgain(
		IgnoreOptionCase optionCase)
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, includeGitIgnore: true);

		ApplyScanState(
			coordinator,
			BuildSingleCount(optionCase.Id, 2),
			hasIgnoreCounts: true);
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], ProjectPath);

		var target = Assert.Single(viewModel.IgnoreOptions.Where(item => item.Id == optionCase.Id));
		target.IsChecked = false;
		coordinator.UpdateIgnoreSelectionCache();
		coordinator.SyncIgnoreAllCheckbox();

		ApplyScanState(
			coordinator,
			IgnoreOptionCounts.Empty,
			hasIgnoreCounts: true);
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], ProjectPath);
		Assert.True(viewModel.AllIgnoreChecked);

		ApplyScanState(
			coordinator,
			BuildSingleCount(optionCase.Id, 1),
			hasIgnoreCounts: true);
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], ProjectPath);

		target = Assert.Single(viewModel.IgnoreOptions.Where(item => item.Id == optionCase.Id));
		Assert.False(target.IsChecked);
		Assert.False(viewModel.AllIgnoreChecked);
	}

	private static IEnumerable<IgnoreOptionCase> AdvancedOptionCases()
	{
		yield return new IgnoreOptionCase(IgnoreOptionId.HiddenFolders, "Hidden folders");
		yield return new IgnoreOptionCase(IgnoreOptionId.HiddenFiles, "Hidden files");
		yield return new IgnoreOptionCase(IgnoreOptionId.DotFolders, "Dot folders");
		yield return new IgnoreOptionCase(IgnoreOptionId.DotFiles, "Dot files");
		yield return new IgnoreOptionCase(IgnoreOptionId.EmptyFolders, "Empty folders");
		yield return new IgnoreOptionCase(IgnoreOptionId.EmptyFiles, "Empty files");
		yield return new IgnoreOptionCase(IgnoreOptionId.ExtensionlessFiles, "Files without extension");
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

	private static void ApplyScanState(
		SelectionSyncCoordinator coordinator,
		IgnoreOptionCounts ignoreCounts,
		bool hasIgnoreCounts)
	{
		var filterService = new FilterOptionSelectionService();
		var options = filterService.BuildExtensionOptions([".cs"], new HashSet<string>(StringComparer.OrdinalIgnoreCase));

		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"ApplyExtensionOptions",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method!.Invoke(coordinator, [options, 0, ignoreCounts, hasIgnoreCounts]);
	}

	private static SelectionSyncCoordinator CreateCoordinator(
		MainWindowViewModel viewModel,
		bool includeGitIgnore)
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		var scanOptions = new ScanOptionsUseCase(new StubFileSystemScanner());
		var filterService = new FilterOptionSelectionService();
		var ignoreService = new IgnoreOptionsService(localization);

		return new SelectionSyncCoordinator(
			viewModel,
			scanOptions,
			filterService,
			ignoreService,
			(_, selectedOptions, _) => new IgnoreRules(
				IgnoreHiddenFolders: selectedOptions.Contains(IgnoreOptionId.HiddenFolders),
				IgnoreHiddenFiles: selectedOptions.Contains(IgnoreOptionId.HiddenFiles),
				IgnoreDotFolders: selectedOptions.Contains(IgnoreOptionId.DotFolders),
				IgnoreDotFiles: selectedOptions.Contains(IgnoreOptionId.DotFiles),
				SmartIgnoredFolders: new HashSet<string>(),
				SmartIgnoredFiles: new HashSet<string>())
			{
				UseGitIgnore = selectedOptions.Contains(IgnoreOptionId.UseGitIgnore),
				IgnoreEmptyFolders = selectedOptions.Contains(IgnoreOptionId.EmptyFolders),
				IgnoreEmptyFiles = selectedOptions.Contains(IgnoreOptionId.EmptyFiles),
				IgnoreExtensionlessFiles = selectedOptions.Contains(IgnoreOptionId.ExtensionlessFiles)
			},
			(_, _) => new IgnoreOptionsAvailability(
				IncludeGitIgnore: includeGitIgnore,
				IncludeSmartIgnore: false,
				ShowAdvancedCounts: true),
			_ => false,
			() => ProjectPath);
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
				["Settings.Ignore.DotFolders"] = "Dot folders",
				["Settings.Ignore.DotFiles"] = "Dot files",
				["Settings.Ignore.EmptyFolders"] = "Empty folders",
				["Settings.Ignore.EmptyFiles"] = "Empty files",
				["Settings.Ignore.ExtensionlessFiles"] = "Files without extension"
			}
		};

		return new StubLocalizationCatalog(data);
	}

	public sealed record IgnoreOptionCase(IgnoreOptionId Id, string BaseLabel)
	{
		public override string ToString() => Id.ToString();
	}
}
