namespace DevProjex.Tests.Unit;

public sealed class SelectionSyncCoordinatorIgnoreAvailabilityFallbackMatrixTests
{
	private const string ProjectPath = @"C:\Workspace\Project";

	public static IEnumerable<object[]> LeakedAvailabilityCases()
	{
		foreach (var optionId in AdvancedOptionIds())
		foreach (var includeGitIgnore in new[] { false, true })
		foreach (var includeSmartIgnore in new[] { false, true })
			yield return [optionId, includeGitIgnore, includeSmartIgnore];
	}

	[Theory]
	[MemberData(nameof(LeakedAvailabilityCases))]
	public void PopulateIgnoreOptionsForRootSelection_WhenEffectiveCountsUnavailable_SuppressesLeakedCountDrivenOption(
		IgnoreOptionId optionId,
		bool includeGitIgnore,
		bool includeSmartIgnore)
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(
			viewModel,
			includeGitIgnore,
			includeSmartIgnore,
			CreateLeakedAvailability(optionId, includeGitIgnore, includeSmartIgnore));

		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], ProjectPath);

		AssertBaseOnlyOptions(viewModel.IgnoreOptions, includeGitIgnore, includeSmartIgnore);
		Assert.DoesNotContain(viewModel.IgnoreOptions, option => option.Id == optionId);
	}

	[Theory]
	[MemberData(nameof(LeakedAvailabilityCases))]
	public void GetSelectedIgnoreOptionIds_WhenEffectiveCountsUnavailable_DoesNotCacheLeakedCountDrivenOption(
		IgnoreOptionId optionId,
		bool includeGitIgnore,
		bool includeSmartIgnore)
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(
			viewModel,
			includeGitIgnore,
			includeSmartIgnore,
			CreateLeakedAvailability(optionId, includeGitIgnore, includeSmartIgnore));

		var selected = coordinator.GetSelectedIgnoreOptionIds();

		AssertBaseOnlySelection(selected, includeGitIgnore, includeSmartIgnore);
		Assert.DoesNotContain(optionId, selected);
	}

	public static IEnumerable<object[]> ExtensionlessFallbackCases()
	{
		foreach (var includeGitIgnore in new[] { false, true })
		foreach (var includeSmartIgnore in new[] { false, true })
		foreach (var extensionlessCount in new[] { 1, 3 })
			yield return [includeGitIgnore, includeSmartIgnore, extensionlessCount];
	}

	[Theory]
	[MemberData(nameof(ExtensionlessFallbackCases))]
	public void PopulateIgnoreOptionsForRootSelection_WhenOnlyExtensionlessFallbackExists_ShowsOnlyBaseOptionsAndExtensionless(
		bool includeGitIgnore,
		bool includeSmartIgnore,
		int extensionlessCount)
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(
			viewModel,
			includeGitIgnore,
			includeSmartIgnore,
			new IgnoreOptionsAvailability(
				IncludeGitIgnore: includeGitIgnore,
				IncludeSmartIgnore: includeSmartIgnore,
				IncludeHiddenFolders: true,
				HiddenFoldersCount: 9,
				IncludeHiddenFiles: true,
				HiddenFilesCount: 9,
				IncludeDotFolders: true,
				DotFoldersCount: 9,
				IncludeDotFiles: true,
				DotFilesCount: 9,
				IncludeEmptyFolders: true,
				EmptyFoldersCount: 9,
				IncludeEmptyFiles: true,
				EmptyFilesCount: 9,
				IncludeExtensionlessFiles: true,
				ExtensionlessFilesCount: 9,
				ShowAdvancedCounts: true));

		ApplyExtensionlessFallbackState(coordinator, extensionlessCount);
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], ProjectPath);

		var ids = viewModel.IgnoreOptions.Select(option => option.Id).ToArray();
		var expectedIds = BuildBaseIds(includeGitIgnore, includeSmartIgnore)
			.Append(IgnoreOptionId.ExtensionlessFiles)
			.ToArray();

		Assert.Equal(expectedIds, ids);
		Assert.Equal(
			$"Files without extension ({extensionlessCount})",
			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.ExtensionlessFiles).Label);
	}

	private static IgnoreOptionsAvailability CreateLeakedAvailability(
		IgnoreOptionId optionId,
		bool includeGitIgnore,
		bool includeSmartIgnore)
	{
		return optionId switch
		{
			IgnoreOptionId.HiddenFolders => new IgnoreOptionsAvailability(
				IncludeGitIgnore: includeGitIgnore,
				IncludeSmartIgnore: includeSmartIgnore,
				IncludeHiddenFolders: true,
				HiddenFoldersCount: 9,
				ShowAdvancedCounts: true),
			IgnoreOptionId.HiddenFiles => new IgnoreOptionsAvailability(
				IncludeGitIgnore: includeGitIgnore,
				IncludeSmartIgnore: includeSmartIgnore,
				IncludeHiddenFiles: true,
				HiddenFilesCount: 9,
				ShowAdvancedCounts: true),
			IgnoreOptionId.DotFolders => new IgnoreOptionsAvailability(
				IncludeGitIgnore: includeGitIgnore,
				IncludeSmartIgnore: includeSmartIgnore,
				IncludeDotFolders: true,
				DotFoldersCount: 9,
				ShowAdvancedCounts: true),
			IgnoreOptionId.DotFiles => new IgnoreOptionsAvailability(
				IncludeGitIgnore: includeGitIgnore,
				IncludeSmartIgnore: includeSmartIgnore,
				IncludeDotFiles: true,
				DotFilesCount: 9,
				ShowAdvancedCounts: true),
			IgnoreOptionId.EmptyFolders => new IgnoreOptionsAvailability(
				IncludeGitIgnore: includeGitIgnore,
				IncludeSmartIgnore: includeSmartIgnore,
				IncludeEmptyFolders: true,
				EmptyFoldersCount: 9,
				ShowAdvancedCounts: true),
			IgnoreOptionId.EmptyFiles => new IgnoreOptionsAvailability(
				IncludeGitIgnore: includeGitIgnore,
				IncludeSmartIgnore: includeSmartIgnore,
				IncludeEmptyFiles: true,
				EmptyFilesCount: 9,
				ShowAdvancedCounts: true),
			IgnoreOptionId.ExtensionlessFiles => new IgnoreOptionsAvailability(
				IncludeGitIgnore: includeGitIgnore,
				IncludeSmartIgnore: includeSmartIgnore,
				IncludeExtensionlessFiles: true,
				ExtensionlessFilesCount: 9,
				ShowAdvancedCounts: true),
			_ => throw new ArgumentOutOfRangeException(nameof(optionId), optionId, null)
		};
	}

	private static void ApplyExtensionlessFallbackState(
		SelectionSyncCoordinator coordinator,
		int extensionlessCount)
	{
		var extensions = new List<string> { ".cs" };
		for (var index = 0; index < extensionlessCount; index++)
			extensions.Add($"Dockerfile{index}");

		var filterService = new FilterOptionSelectionService();
		var options = filterService.BuildExtensionOptions(
			extensions,
			new HashSet<string>(StringComparer.OrdinalIgnoreCase));

		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"ApplyExtensionOptions",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method!.Invoke(coordinator, [options, extensionlessCount, IgnoreOptionCounts.Empty, false]);
	}

	private static void AssertBaseOnlyOptions(
		IEnumerable<IgnoreOptionViewModel> options,
		bool includeGitIgnore,
		bool includeSmartIgnore)
	{
		var ids = options.Select(option => option.Id).ToArray();
		Assert.Equal(BuildBaseIds(includeGitIgnore, includeSmartIgnore), ids);
	}

	private static void AssertBaseOnlySelection(
		IReadOnlyCollection<IgnoreOptionId> selected,
		bool includeGitIgnore,
		bool includeSmartIgnore)
	{
		Assert.Equal(
			BuildBaseIds(includeGitIgnore, includeSmartIgnore).OrderBy(static id => id),
			selected.OrderBy(static id => id));
	}

	private static IgnoreOptionId[] BuildBaseIds(bool includeGitIgnore, bool includeSmartIgnore)
	{
		var ids = new List<IgnoreOptionId>(2);
		if (includeSmartIgnore)
			ids.Add(IgnoreOptionId.SmartIgnore);
		if (includeGitIgnore)
			ids.Add(IgnoreOptionId.UseGitIgnore);

		return ids.ToArray();
	}

	private static IEnumerable<IgnoreOptionId> AdvancedOptionIds()
	{
		yield return IgnoreOptionId.HiddenFolders;
		yield return IgnoreOptionId.HiddenFiles;
		yield return IgnoreOptionId.DotFolders;
		yield return IgnoreOptionId.DotFiles;
		yield return IgnoreOptionId.EmptyFolders;
		yield return IgnoreOptionId.EmptyFiles;
		yield return IgnoreOptionId.ExtensionlessFiles;
	}

	private static SelectionSyncCoordinator CreateCoordinator(
		MainWindowViewModel viewModel,
		bool includeGitIgnore,
		bool includeSmartIgnore,
		IgnoreOptionsAvailability availability)
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
			(_, _) => availability with
			{
				IncludeGitIgnore = includeGitIgnore,
				IncludeSmartIgnore = includeSmartIgnore
			},
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
}
