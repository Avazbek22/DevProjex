namespace DevProjex.Tests.Unit;

public sealed class SelectionSyncCoordinatorFileIgnoreAvailabilityScanTests
{
	[Theory]
	[MemberData(nameof(AvailabilityCases))]
	public async Task PopulateExtensionsForRootSelectionAsync_ExtensionAvailabilityScan_DoesNotApplyFileLevelSelfIgnoreFlags(
		int caseId,
		IgnoreOptionId[] selectedIgnoreOptions,
		string[] selectedRoots)
	{
		_ = caseId;
		const string projectPath = @"C:\Workspace\ProjectA";
		var observedRules = new List<IgnoreRules>();
		var scanner = new StubFileSystemScanner
		{
			GetRootFileExtensionsHandler = (_, rules) =>
			{
				observedRules.Add(rules);
				throw new OperationCanceledException("Synthetic stop after rule capture.");
			},
			GetExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				RootAccessDenied: false,
				HadAccessDenied: false)
		};
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel, scanner, projectPath);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: selectedRoots,
			SelectedExtensions: [],
			SelectedIgnoreOptions: selectedIgnoreOptions);

		coordinator.ApplyProjectProfileSelections(projectPath, profile);

		await Assert.ThrowsAsync<OperationCanceledException>(() =>
			coordinator.PopulateExtensionsForRootSelectionAsync(projectPath, selectedRoots));

		Assert.NotEmpty(observedRules);
		Assert.All(observedRules, rules =>
		{
			Assert.False(rules.IgnoreHiddenFiles);
			Assert.False(rules.IgnoreDotFiles);
			Assert.False(rules.IgnoreEmptyFiles);
			Assert.False(rules.IgnoreExtensionlessFiles);
		});
	}

	public static IEnumerable<object[]> AvailabilityCases()
	{
		var rootCases = new[]
		{
			Array.Empty<string>(),
			["src"],
			["tests"],
			new[] { "src", "tests" }
		};

		var ignoreCases = new[]
		{
			Array.Empty<IgnoreOptionId>(),
			[IgnoreOptionId.HiddenFiles],
			[IgnoreOptionId.DotFiles],
			[IgnoreOptionId.EmptyFiles],
			[IgnoreOptionId.ExtensionlessFiles],
			new[] { IgnoreOptionId.HiddenFiles, IgnoreOptionId.DotFiles },
			new[] { IgnoreOptionId.EmptyFiles, IgnoreOptionId.ExtensionlessFiles },
			new[] { IgnoreOptionId.HiddenFiles, IgnoreOptionId.EmptyFiles, IgnoreOptionId.ExtensionlessFiles }
		};

		var caseId = 0;
		foreach (var selectedRoots in rootCases)
		{
			foreach (var selectedIgnoreOptions in ignoreCases)
				yield return [caseId++, selectedIgnoreOptions, selectedRoots];
		}
	}

	private static SelectionSyncCoordinator CreateCoordinator(
		MainWindowViewModel viewModel,
		StubFileSystemScanner scanner,
		string currentPath)
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		var scanOptions = new ScanOptionsUseCase(scanner);
		var filterSelectionService = new FilterOptionSelectionService();
		var ignoreOptionsService = new IgnoreOptionsService(localization);

		return new SelectionSyncCoordinator(
			viewModel,
			scanOptions,
			filterSelectionService,
			ignoreOptionsService,
			(_, selectedOptions, _) => new IgnoreRules(
				IgnoreHiddenFolders: false,
				IgnoreHiddenFiles: selectedOptions.Contains(IgnoreOptionId.HiddenFiles),
				IgnoreDotFolders: false,
				IgnoreDotFiles: selectedOptions.Contains(IgnoreOptionId.DotFiles),
				SmartIgnoredFolders: new HashSet<string>(),
				SmartIgnoredFiles: new HashSet<string>())
			{
				IgnoreEmptyFiles = selectedOptions.Contains(IgnoreOptionId.EmptyFiles),
				IgnoreExtensionlessFiles = selectedOptions.Contains(IgnoreOptionId.ExtensionlessFiles)
			},
			(_, _) => new IgnoreOptionsAvailability(
				IncludeGitIgnore: false,
				IncludeSmartIgnore: false,
				ShowAdvancedCounts: true),
			_ => false,
			() => currentPath);
	}

	private static MainWindowViewModel CreateViewModel()
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		return new MainWindowViewModel(localization, new HelpContentProvider())
		{
			AllIgnoreChecked = false
		};
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
				["Settings.Ignore.EmptyFiles"] = "Empty files",
				["Settings.Ignore.ExtensionlessFiles"] = "Files without extension"
			}
		};

		return new StubLocalizationCatalog(data);
	}
}
