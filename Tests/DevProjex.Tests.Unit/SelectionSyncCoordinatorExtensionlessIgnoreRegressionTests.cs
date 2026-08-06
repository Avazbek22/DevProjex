namespace DevProjex.Tests.Unit;

public sealed class SelectionSyncCoordinatorExtensionlessIgnoreRegressionTests
{
	[Theory]
	[MemberData(nameof(ExtensionAvailabilityScanCases))]
	public async Task PopulateExtensionsForRootSelectionAsync_ExtensionAvailabilityScan_DoesNotApplySelfIgnoreFlag(
		int caseId,
		IgnoreOptionId[] selectedIgnoreOptions,
		string[] selectedRoots)
	{
		_ = caseId;
		const string projectPath = @"C:\Workspace\ProjectA";
		var observedIgnoreExtensionlessValues = new List<bool>();
		var scanner = new StubFileSystemScanner
		{
			GetRootFileExtensionsHandler = (_, rules) =>
			{
				observedIgnoreExtensionlessValues.Add(rules.IgnoreExtensionlessFiles);
				// Cancel before Dispatcher.UIThread.InvokeAsync to keep this test deterministic.
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
			coordinator.PopulateExtensionsForRootSelectionAsync(projectPath, selectedRoots, cancellationToken: TestContext.Current.CancellationToken));

		Assert.NotEmpty(observedIgnoreExtensionlessValues);
		Assert.All(observedIgnoreExtensionlessValues, value => Assert.False(value));
	}

	[Theory]
	[MemberData(nameof(ExtensionlessCountSequenceCases))]
	public void ApplyExtensionScan_ExtensionlessOptionState_FollowsScanCountAndProfileSelection(
		int caseId,
		bool extensionlessSelectedInProfile,
		string[][] scanSequence,
		int[] expectedCounts)
	{
		_ = caseId;
		const string projectPath = @"C:\Workspace\ProjectA";
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel, new StubFileSystemScanner(), projectPath);
		var selectedIgnoreOptions = extensionlessSelectedInProfile
			? new[] { IgnoreOptionId.ExtensionlessFiles }
			: Array.Empty<IgnoreOptionId>();
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: selectedIgnoreOptions);

		coordinator.ApplyProjectProfileSelections(projectPath, profile);

		for (var i = 0; i < scanSequence.Length; i++)
		{
			coordinator.ApplyExtensionScan(scanSequence[i]);
			coordinator.PopulateIgnoreOptionsForRootSelection([], projectPath);

			var expectedChecked = extensionlessSelectedInProfile && expectedCounts[i] > 0;
			AssertExtensionlessOption(viewModel, expectedCounts[i], expectedChecked);
		}
	}

	public static IEnumerable<object[]> ExtensionAvailabilityScanCases()
	{
		var rootCases = new[]
		{
			Array.Empty<string>(),
			["src"],
			["tests"],
			new[] { "src", "tests" },
			new[] { "docs" },
			new[] { "missing-root" }
		};

		var ignoreCases = new[]
		{
			Array.Empty<IgnoreOptionId>(),
			[IgnoreOptionId.ExtensionlessFiles],
			new[] { IgnoreOptionId.ExtensionlessFiles, IgnoreOptionId.HiddenFiles },
			new[] { IgnoreOptionId.ExtensionlessFiles, IgnoreOptionId.DotFiles, IgnoreOptionId.DotFolders },
			new[] { IgnoreOptionId.HiddenFolders, IgnoreOptionId.HiddenFiles },
			new[] { IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore, IgnoreOptionId.ExtensionlessFiles }
		};

		var caseId = 0;
		foreach (var selectedRoots in rootCases)
		{
			foreach (var selectedIgnoreOptions in ignoreCases)
			{
				yield return [ caseId++, selectedIgnoreOptions, selectedRoots ];
			}
		}
	}

	public static IEnumerable<object[]> ExtensionlessCountSequenceCases()
	{
		var patterns = new Dictionary<int, (string[] Entries, int Count)>
		{
			[0] = ([".cs", ".json"], 0),
			[1] = (["Dockerfile", ".cs"], 1),
			[2] = (["Dockerfile", "Makefile", ".cs"], 2),
			[3] = ([".env", ".gitignore", ".editorconfig"], 0),
			[4] = (["LICENSE", ".md", ".txt"], 1),
			[5] = (["Taskfile", "WORKSPACE", ".yml"], 2),
			[6] = (["README", "file.", ".props"], 2)
		};

		var sequences = new[]
		{
			[0, 1, 0],
			new[] { 1, 2, 1 },
			new[] { 2, 0, 2 },
			new[] { 3, 4, 3 },
			new[] { 4, 5, 0 },
			new[] { 5, 6, 1 },
			new[] { 6, 0, 6 },
			new[] { 1, 3, 5 },
			new[] { 2, 4, 2 },
			new[] { 0, 0, 0 }
		};

		var caseId = 0;
		foreach (var extensionlessSelectedInProfile in new[] { false, true })
		{
			foreach (var sequence in sequences)
			{
				var scanSequence = sequence.Select(index => patterns[index].Entries).ToArray();
				var expectedCounts = sequence.Select(index => patterns[index].Count).ToArray();
				yield return [ caseId++, extensionlessSelectedInProfile, scanSequence, expectedCounts ];
			}
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
				IgnoreHiddenFiles: false,
				IgnoreDotFolders: false,
				IgnoreDotFiles: false,
				SmartIgnoredFolders: new HashSet<string>(),
				SmartIgnoredFiles: new HashSet<string>())
			{
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
				["Settings.Ignore.HideSecrets"] = "Hide secrets",
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

	private static void AssertExtensionlessOption(MainWindowViewModel viewModel, int expectedCount, bool expectedChecked)
	{
		var option = viewModel.IgnoreOptions.FirstOrDefault(item => item.Id == IgnoreOptionId.ExtensionlessFiles);
		if (expectedCount <= 0)
		{
			Assert.Null(option);
			return;
		}

		Assert.NotNull(option);
		Assert.Equal($"Files without extension ({expectedCount})", option!.Label);
		Assert.Equal(expectedChecked, option.IsChecked);
	}
}
