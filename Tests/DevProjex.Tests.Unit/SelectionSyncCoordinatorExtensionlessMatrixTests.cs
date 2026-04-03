namespace DevProjex.Tests.Unit;

public sealed class SelectionSyncCoordinatorExtensionlessMatrixTests
{
	[Theory]
	[MemberData(nameof(ExtensionScanCases))]
	public void ApplyExtensionScan_FiltersExtensionlessFromUiAndControlsIgnoreOption(
		string[] scanEntries,
		string[] expectedVisibleEntries,
		bool expectExtensionlessIgnoreOption,
		int expectedExtensionlessCount)
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel, @"C:\Temp\Project");

		coordinator.ApplyExtensionScan(scanEntries);
		coordinator.PopulateIgnoreOptionsForRootSelection([], @"C:\Temp\Project");

		var visible = viewModel.Extensions.Select(option => option.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
		Assert.Equal(expectedVisibleEntries.Length, visible.Count);
		foreach (var entry in expectedVisibleEntries)
			Assert.Contains(entry, visible);

		Assert.DoesNotContain(viewModel.Extensions, option => IsExtensionlessEntry(option.Name));

		var hasExtensionlessOption = viewModel.IgnoreOptions.Any(option => option.Id == IgnoreOptionId.ExtensionlessFiles);
		Assert.Equal(expectExtensionlessIgnoreOption, hasExtensionlessOption);
		if (!expectExtensionlessIgnoreOption)
			return;

		var extensionlessOption = viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.ExtensionlessFiles);
		Assert.Equal($"Files without extension ({expectedExtensionlessCount})", extensionlessOption.Label);
		Assert.True(extensionlessOption.IsChecked);
	}

	[Fact]
	public void ApplyExtensionScan_TransitionFromPresentToAbsent_HidesExtensionlessOption()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel, @"C:\Temp\Project");

		coordinator.ApplyExtensionScan(["Dockerfile", ".cs"]);
		coordinator.PopulateIgnoreOptionsForRootSelection([], @"C:\Temp\Project");
		Assert.Contains(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.ExtensionlessFiles);

		coordinator.ApplyExtensionScan([".cs", ".json", ".md"]);
		coordinator.PopulateIgnoreOptionsForRootSelection([], @"C:\Temp\Project");

		Assert.DoesNotContain(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.ExtensionlessFiles);
	}

	[Fact]
	public void ApplyExtensionScan_TransitionFromAbsentToPresent_UpdatesCountInLabel()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel, @"C:\Temp\Project");

		coordinator.ApplyExtensionScan([".cs", ".json"]);
		coordinator.PopulateIgnoreOptionsForRootSelection([], @"C:\Temp\Project");
		Assert.DoesNotContain(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.ExtensionlessFiles);

		coordinator.ApplyExtensionScan(["Dockerfile", "Makefile", ".cs"]);
		coordinator.PopulateIgnoreOptionsForRootSelection([], @"C:\Temp\Project");

		var extensionlessOption = viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.ExtensionlessFiles);
		Assert.Equal("Files without extension (2)", extensionlessOption.Label);
		Assert.True(extensionlessOption.IsChecked);
	}

	[Fact]
	public void ProfiledExtensionlessSelection_ReappearsCheckedWithNewCountAfterTemporaryAbsence()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel, @"C:\Temp\Project");

		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: [IgnoreOptionId.ExtensionlessFiles]);
		coordinator.ApplyProjectProfileSelections(@"C:\Temp\Project", profile);

		coordinator.ApplyExtensionScan([".cs", ".json"]);
		coordinator.PopulateIgnoreOptionsForRootSelection([], @"C:\Temp\Project");
		Assert.DoesNotContain(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.ExtensionlessFiles);

		coordinator.ApplyExtensionScan(["Dockerfile", "Makefile", "LICENSE", ".cs"]);
		coordinator.PopulateIgnoreOptionsForRootSelection([], @"C:\Temp\Project");

		var extensionlessOption = viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.ExtensionlessFiles);
		Assert.True(extensionlessOption.IsChecked);
		Assert.Equal("Files without extension (3)", extensionlessOption.Label);
	}

	public static IEnumerable<object[]> ExtensionScanCases()
	{
		yield return [ new[] { ".cs", ".md" }, new[] { ".cs", ".md" }, false, 0 ];
		yield return [ new[] { "Dockerfile", ".cs" }, new[] { ".cs" }, true, 1 ];
		yield return [ new[] { "Dockerfile", "Makefile" }, Array.Empty<string>(), true, 2 ];
		yield return [ new[] { ".env", ".cs" }, new[] { ".env", ".cs" }, false, 0 ];
		yield return [ new[] { ".gitignore", "README", ".txt" }, new[] { ".gitignore", ".txt" }, true, 1 ];
		yield return [ new[] { "LICENSE", ".json", ".yml" }, new[] { ".json", ".yml" }, true, 1 ];
		yield return [ new[] { ".axaml", ".cs", ".json" }, new[] { ".axaml", ".cs", ".json" }, false, 0 ];
		yield return [ new[] { "WORKSPACE", ".csproj", ".sln" }, new[] { ".csproj", ".sln" }, true, 1 ];
		yield return [ new[] { ".dockerignore", "Jenkinsfile", ".yaml" }, new[] { ".dockerignore", ".yaml" }, true, 1 ];
		yield return [ new[] { ".rules", ".props", ".targets" }, new[] { ".rules", ".props", ".targets" }, false, 0 ];
		yield return [ new[] { "Taskfile", ".txt", ".log", ".md" }, new[] { ".txt", ".log", ".md" }, true, 1 ];
		yield return [ new[] { ".env", ".gitignore", ".editorconfig" }, new[] { ".env", ".gitignore", ".editorconfig" }, false, 0 ];
	}

	private static SelectionSyncCoordinator CreateCoordinator(MainWindowViewModel viewModel, string currentPath)
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
			(rootPath, _, _) => new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
			(rootPath, _) => new IgnoreOptionsAvailability(
				IncludeGitIgnore: false,
				IncludeSmartIgnore: false,
				ShowAdvancedCounts: true),
			_ => false,
			() => currentPath);
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

	private static bool IsExtensionlessEntry(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return false;

		var extension = Path.GetExtension(value);
		return string.IsNullOrEmpty(extension) || extension == ".";
	}
}
