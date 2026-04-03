namespace DevProjex.Tests.Unit;

public sealed class SelectionSyncCoordinatorExtensionlessCountSequenceTests
{
	[Theory]
	[MemberData(nameof(CountSequenceCases))]
	public void ApplyExtensionScan_CountSequence_UpdatesVisibilityAndLabel(
		int caseId,
		string[][] scans,
		int[] expectedCounts)
	{
		_ = caseId;
		const string projectPath = @"C:\Workspace\ProjectA";
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel, () => projectPath);

		for (var i = 0; i < scans.Length; i++)
		{
			coordinator.ApplyExtensionScan(scans[i]);
			coordinator.PopulateIgnoreOptionsForRootSelection([], projectPath);

			AssertExtensionlessOptionState(viewModel, expectedCounts[i], expectChecked: expectedCounts[i] > 0);
		}
	}

	[Theory]
	[MemberData(nameof(CountSequenceCases))]
	public void ApplyExtensionScan_CountSequence_WithSavedProfile_RestoresCheckedStateWhenOptionReappears(
		int caseId,
		string[][] scans,
		int[] expectedCounts)
	{
		_ = caseId;
		const string projectPath = @"C:\Workspace\ProjectA";
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel, () => projectPath);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: [IgnoreOptionId.ExtensionlessFiles]);

		coordinator.ApplyProjectProfileSelections(projectPath, profile);

		for (var i = 0; i < scans.Length; i++)
		{
			coordinator.ApplyExtensionScan(scans[i]);
			coordinator.PopulateIgnoreOptionsForRootSelection([], projectPath);

			AssertExtensionlessOptionState(viewModel, expectedCounts[i], expectChecked: expectedCounts[i] > 0);
		}
	}

	[Fact]
	public void ApplyExtensionScan_FromRootSelectionScan_CountTracksSelectedRootFolders()
	{
		const string projectPath = @"C:\Workspace\ProjectA";
		var srcPath = Path.Combine(projectPath, "src");
		var testsPath = Path.Combine(projectPath, "tests");
		var ignoreRules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>());
		var scanner = new StubFileSystemScanner
		{
			GetRootFileExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Dockerfile", ".cs" },
				false,
				false),
			GetExtensionsHandler = (path, _) => path switch
			{
				_ when path == srcPath => new ScanResult<HashSet<string>>(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Makefile", ".ts" },
					false,
					false),
				_ when path == testsPath => new ScanResult<HashSet<string>>(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LICENSE", ".cs" },
					false,
					false),
				_ => new ScanResult<HashSet<string>>([], false, false)
			}
		};

		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel, () => projectPath, scanner);
		var scanOptions = new ScanOptionsUseCase(scanner);

		var rootOnlyScan = scanOptions.GetExtensionsForRootFolders(projectPath, [], ignoreRules);
		coordinator.ApplyExtensionScan(rootOnlyScan.Value);
		coordinator.PopulateIgnoreOptionsForRootSelection([], projectPath);
		AssertExtensionlessOptionState(viewModel, expectedCount: 1, expectChecked: true);

		var srcScan = scanOptions.GetExtensionsForRootFolders(projectPath, ["src"], ignoreRules);
		coordinator.ApplyExtensionScan(srcScan.Value);
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], projectPath);
		AssertExtensionlessOptionState(viewModel, expectedCount: 2, expectChecked: true);

		var srcAndTestsScan = scanOptions.GetExtensionsForRootFolders(projectPath, ["src", "tests"], ignoreRules);
		coordinator.ApplyExtensionScan(srcAndTestsScan.Value);
		coordinator.PopulateIgnoreOptionsForRootSelection(["src", "tests"], projectPath);
		AssertExtensionlessOptionState(viewModel, expectedCount: 3, expectChecked: true);
	}

	public static IEnumerable<object[]> CountSequenceCases()
	{
		var patterns = new Dictionary<int, (string[] Scan, int Count)>
		{
			[0] = ([".cs", ".json", ".md"], 0),
			[1] = (["Dockerfile", ".cs"], 1),
			[2] = (["Dockerfile", "Makefile", ".cs"], 2),
			[3] = ([".env", ".gitignore", ".dockerignore"], 0),
			[4] = (["file.", ".txt"], 1),
			[5] = (["Dockerfile", "LICENSE", "WORKSPACE"], 3),
			[6] = (["archive.tar.gz", ".yaml"], 0),
			[7] = (["Taskfile", ".log"], 1)
		};

		var sequences = new[]
		{
			new[] { 1, 0, 1 },
			new[] { 2, 0, 5 },
			new[] { 0, 0, 0 },
			new[] { 3, 0, 3 },
			new[] { 4, 6, 4 },
			new[] { 1, 2, 5 },
			new[] { 5, 2, 1 },
			new[] { 0, 7, 0 },
			new[] { 7, 0, 2 },
			new[] { 2, 6, 0 },
			new[] { 1, 3, 6 },
			new[] { 5, 0, 4 },
			new[] { 4, 0, 2 },
			new[] { 7, 3, 7 },
			new[] { 2, 1, 0 },
			new[] { 6, 1, 6 },
			new[] { 0, 5, 0 },
			new[] { 3, 7, 3 },
			new[] { 5, 5, 5 },
			new[] { 0, 2, 0 }
		};

		var caseId = 0;
		foreach (var sequence in sequences)
		{
			var scans = sequence.Select(id => patterns[id].Scan).ToArray();
			var counts = sequence.Select(id => patterns[id].Count).ToArray();
			yield return [ caseId++, scans, counts ];
		}
	}

	private static void AssertExtensionlessOptionState(
		MainWindowViewModel viewModel,
		int expectedCount,
		bool expectChecked)
	{
		var option = viewModel.IgnoreOptions.FirstOrDefault(item => item.Id == IgnoreOptionId.ExtensionlessFiles);
		if (expectedCount <= 0)
		{
			Assert.Null(option);
			return;
		}

		Assert.NotNull(option);
		Assert.Equal($"Files without extension ({expectedCount})", option!.Label);
		Assert.Equal(expectChecked, option.IsChecked);
	}

	private static SelectionSyncCoordinator CreateCoordinator(
		MainWindowViewModel viewModel,
		Func<string?> currentPathProvider,
		StubFileSystemScanner? scanner = null)
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		scanner ??= new StubFileSystemScanner();
		var scanOptions = new ScanOptionsUseCase(scanner);
		var filterSelection = new FilterOptionSelectionService();
		var ignoreOptions = new IgnoreOptionsService(localization);

		return new SelectionSyncCoordinator(
			viewModel,
			scanOptions,
			filterSelection,
			ignoreOptions,
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
			currentPathProvider);
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
