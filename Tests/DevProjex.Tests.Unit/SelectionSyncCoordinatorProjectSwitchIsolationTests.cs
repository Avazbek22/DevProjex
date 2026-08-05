namespace DevProjex.Tests.Unit;

public sealed class SelectionSyncCoordinatorProjectSwitchIsolationTests
{
	[Theory]
	[MemberData(nameof(IgnoreProjectSwitchCases))]
	public void ProjectSwitch_IgnoreSelections_RestorePerProjectWithoutCrossBleed(
		int caseId,
		IgnoreOptionId[] projectASavedIgnore,
		bool projectAIncludeGit,
		bool projectAIncludeTrackedGitFiles,
		bool projectAIncludeSmart,
		string[] projectAExtensions)
	{
		_ = caseId;
		const string projectA = @"C:\Workspace\ProjectA";
		const string projectB = @"C:\Workspace\ProjectB";
		var currentPath = projectA;

		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(
			viewModel,
			currentPathProvider: () => currentPath,
			availabilityProvider: (path, _) =>
			{
				if (PathComparer.Default.Equals(path, projectA))
				{
					return new IgnoreOptionsAvailability(
						IncludeGitIgnore: projectAIncludeGit,
						IncludeSmartIgnore: projectAIncludeSmart,
						IncludeTrackedGitFilesOnly: projectAIncludeTrackedGitFiles);
				}

				return new IgnoreOptionsAvailability(
					IncludeGitIgnore: false,
					IncludeSmartIgnore: false);
			});

		var profileA = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: projectASavedIgnore);

		coordinator.ApplyProjectProfileSelections(projectA, profileA);
		coordinator.ApplyExtensionScan(projectAExtensions);
		coordinator.PopulateIgnoreOptionsForRootSelection([], projectA);
		var initialProjectAState = SnapshotIgnoreState(viewModel.IgnoreOptions);
		AssertMutuallyExclusiveGitFilteringModes(initialProjectAState);

		currentPath = projectB;
		coordinator.ResetProjectProfileSelections(projectB);
		coordinator.ApplyExtensionScan([".cs", ".json"]);
		coordinator.PopulateIgnoreOptionsForRootSelection([], projectB);

		currentPath = projectA;
		coordinator.ApplyProjectProfileSelections(projectA, profileA);
		coordinator.ApplyExtensionScan(projectAExtensions);
		coordinator.PopulateIgnoreOptionsForRootSelection([], projectA);
		var restoredProjectAState = SnapshotIgnoreState(viewModel.IgnoreOptions);

		AssertIgnoreState(restoredProjectAState, initialProjectAState);
		AssertMutuallyExclusiveGitFilteringModes(restoredProjectAState);
	}

	[Theory]
	[MemberData(nameof(ExtensionProjectSwitchCases))]
	public void ProjectSwitch_ExtensionSelections_AreRestoredPerProject(
		int caseId,
		string[] projectASelectedExtensions,
		string[] firstScan,
		string[] secondScan)
	{
		_ = caseId;
		const string projectA = @"C:\Workspace\ProjectA";
		const string projectB = @"C:\Workspace\ProjectB";
		var currentPath = projectA;

		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(
			viewModel,
			currentPathProvider: () => currentPath,
			availabilityProvider: (_, _) => new IgnoreOptionsAvailability(false, false));

		var profileA = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: projectASelectedExtensions,
			SelectedIgnoreOptions: []);

		coordinator.ApplyProjectProfileSelections(projectA, profileA);
		coordinator.ApplyExtensionScan(firstScan);
		var initialProjectAState = SnapshotSelectionState(viewModel.Extensions);

		currentPath = projectB;
		coordinator.ResetProjectProfileSelections(projectB);
		coordinator.ApplyExtensionScan(secondScan);

		currentPath = projectA;
		coordinator.ApplyProjectProfileSelections(projectA, profileA);
		coordinator.ApplyExtensionScan(firstScan);
		var restoredProjectAState = SnapshotSelectionState(viewModel.Extensions);

		AssertSelectionState(restoredProjectAState, initialProjectAState);
	}

	public static IEnumerable<object[]> IgnoreProjectSwitchCases()
	{
		var caseId = 0;
		var savedVariants = new[]
		{
			new[] { IgnoreOptionId.HiddenFolders },
			new[] { IgnoreOptionId.HiddenFiles, IgnoreOptionId.DotFiles },
			new[] { IgnoreOptionId.UseGitIgnore },
			new[] { IgnoreOptionId.TrackedGitFilesOnly },
			new[] { IgnoreOptionId.SmartIgnore },
			new[] { IgnoreOptionId.ExtensionlessFiles },
			new[] { IgnoreOptionId.UseGitIgnore, IgnoreOptionId.HiddenFolders },
			new[] { IgnoreOptionId.TrackedGitFilesOnly, IgnoreOptionId.SmartIgnore, IgnoreOptionId.DotFiles },
			new[] { IgnoreOptionId.SmartIgnore, IgnoreOptionId.DotFiles },
			new[] { IgnoreOptionId.ExtensionlessFiles, IgnoreOptionId.HiddenFiles },
			new[] { IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore, IgnoreOptionId.HiddenFolders },
			Array.Empty<IgnoreOptionId>()
		};

		var availabilityVariants = new[]
		{
			(IncludeGit: false, IncludeTracked: false, IncludeSmart: false),
			(IncludeGit: true, IncludeTracked: true, IncludeSmart: false),
			(IncludeGit: false, IncludeTracked: false, IncludeSmart: true),
			(IncludeGit: true, IncludeTracked: true, IncludeSmart: true)
		};

		var extensionScanVariants = new[]
		{
			new[] { ".cs", ".json" },
			new[] { ".cs", "Dockerfile" }
		};

		foreach (var saved in savedVariants)
		{
			foreach (var availability in availabilityVariants)
			{
				foreach (var scan in extensionScanVariants)
				{
					yield return
					[
						caseId++,
						saved,
						availability.IncludeGit,
						availability.IncludeTracked,
						availability.IncludeSmart,
						scan
					];
				}
			}
		}
	}

	public static IEnumerable<object[]> ExtensionProjectSwitchCases()
	{
		var caseId = 0;
		var savedVariants = new[]
		{
			new[] { ".cs" },
			new[] { ".json", ".md" },
			new[] { ".missing" },
			new[] { ".cs", ".missing" },
			Array.Empty<string>()
		};

		var scans = new[]
		{
			new[] { ".cs", ".json", ".md" },
			new[] { ".ts", ".tsx", ".json" },
			new[] { ".xml", ".yml" }
		};

		foreach (var saved in savedVariants)
		{
			foreach (var first in scans)
			{
				foreach (var second in scans)
				{
					yield return
					[
						caseId++,
						saved,
						first,
						second
					];
				}
			}
		}
	}

	private static SelectionSyncCoordinator CreateCoordinator(
		MainWindowViewModel viewModel,
		Func<string?> currentPathProvider,
		Func<string, IReadOnlyCollection<string>, IgnoreOptionsAvailability> availabilityProvider)
	{
		return CreateCoordinator(viewModel, new StubFileSystemScanner(), currentPathProvider, availabilityProvider);
	}

	private static SelectionSyncCoordinator CreateCoordinator(
		MainWindowViewModel viewModel,
		StubFileSystemScanner scanner,
		Func<string?> currentPathProvider,
		Func<string, IReadOnlyCollection<string>, IgnoreOptionsAvailability> availabilityProvider)
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		var scanOptions = new ScanOptionsUseCase(scanner);
		var filterService = new FilterOptionSelectionService();
		var ignoreService = new IgnoreOptionsService(localization);

		return new SelectionSyncCoordinator(
			viewModel,
			scanOptions,
			filterService,
			ignoreService,
			(_, _, _) => new IgnoreRules(
				IgnoreHiddenFolders: false,
				IgnoreHiddenFiles: false,
				IgnoreDotFolders: false,
				IgnoreDotFiles: false,
				SmartIgnoredFolders: new HashSet<string>(),
				SmartIgnoredFiles: new HashSet<string>()),
			availabilityProvider,
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
				["Settings.Ignore.HideSecrets"] = "Hide secrets",
				["Settings.Ignore.UseGitIgnore"] = "Use .gitignore",
				["Settings.Ignore.TrackedGitFilesOnly"] = "Tracked Git files only",
				["Settings.Ignore.HiddenFolders"] = "Hidden folders",
				["Settings.Ignore.HiddenFiles"] = "Hidden files",
				["Settings.Ignore.DotFolders"] = "dot folders",
				["Settings.Ignore.DotFiles"] = "dot files",
				["Settings.Ignore.ExtensionlessFiles"] = "Files without extension"
			}
		};

		return new StubLocalizationCatalog(data);
	}

	private static IReadOnlyDictionary<IgnoreOptionId, bool> SnapshotIgnoreState(IEnumerable<IgnoreOptionViewModel> options)
	{
		return options.ToDictionary(option => option.Id, option => option.IsChecked);
	}

	private static IReadOnlyDictionary<string, bool> SnapshotSelectionState(IEnumerable<SelectionOptionViewModel> options)
	{
		return options.ToDictionary(option => option.Name, option => option.IsChecked, StringComparer.OrdinalIgnoreCase);
	}

	private static void AssertIgnoreState(
		IReadOnlyDictionary<IgnoreOptionId, bool> actualState,
		IReadOnlyDictionary<IgnoreOptionId, bool> expectedState)
	{
		Assert.Equal(expectedState.Count, actualState.Count);

		foreach (var (id, expectedChecked) in expectedState)
		{
			Assert.True(actualState.ContainsKey(id), $"Expected ignore option is missing: {id}");
			Assert.Equal(expectedChecked, actualState[id]);
		}
	}

	private static void AssertMutuallyExclusiveGitFilteringModes(
		IReadOnlyDictionary<IgnoreOptionId, bool> state)
	{
		var useGitIgnore = state.TryGetValue(IgnoreOptionId.UseGitIgnore, out var gitIgnoreState) &&
		                   gitIgnoreState;
		var trackedOnly = state.TryGetValue(IgnoreOptionId.TrackedGitFilesOnly, out var trackedState) &&
		                  trackedState;
		Assert.False(useGitIgnore && trackedOnly);
	}

	private static void AssertSelectionState(
		IReadOnlyDictionary<string, bool> actualState,
		IReadOnlyDictionary<string, bool> expectedState)
	{
		Assert.Equal(expectedState.Count, actualState.Count);
		foreach (var (name, expectedChecked) in expectedState)
		{
			Assert.True(actualState.ContainsKey(name), $"Expected option is missing: {name}");
			Assert.Equal(expectedChecked, actualState[name]);
		}
	}

	private static bool IsExtensionlessEntry(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return false;

		var extension = Path.GetExtension(value);
		return string.IsNullOrEmpty(extension) || extension == ".";
	}
}
