using DevProjex.Application.Models;

namespace DevProjex.Tests.Unit;

public sealed class SelectionSyncCoordinatorProjectSwitchIsolationTests
{
	public enum PreparedEventKind
	{
		ExtensionItem,
		IgnoreItem,
		ExtensionsAll,
		IgnoreAll,
		ContentProcessingAll
	}

	private static readonly IgnoreOptionId[] CompletePublishedSectionIds =
	[
		IgnoreOptionId.SmartIgnore,
		IgnoreOptionId.HiddenFolders,
		IgnoreOptionId.HiddenFiles,
		IgnoreOptionId.DotFolders,
		IgnoreOptionId.DotFiles,
		IgnoreOptionId.EmptyFolders,
		IgnoreOptionId.EmptyFiles,
		IgnoreOptionId.ExtensionlessFiles,
		IgnoreOptionId.HideSecrets,
		IgnoreOptionId.HidePrivateData,
		IgnoreOptionId.CompressCode,
		IgnoreOptionId.StripComments,
		IgnoreOptionId.StripBlankLines,
		IgnoreOptionId.UseGitIgnore,
		IgnoreOptionId.TrackedGitFilesOnly
	];

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
						IncludeEmptyFolders: true,
						IncludeEmptyFiles: true,
						IncludeExtensionlessFiles: true,
						IncludeTrackedGitFilesOnly: projectAIncludeTrackedGitFiles);
				}

				return new IgnoreOptionsAvailability(
					IncludeGitIgnore: false,
					IncludeSmartIgnore: false,
					IncludeEmptyFolders: true,
					IncludeEmptyFiles: true,
					IncludeExtensionlessFiles: true);
			});

		var profileA = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: projectASavedIgnore);

		coordinator.ApplyProjectProfileSelections(projectA, profileA);
		coordinator.ApplyExtensionScan(projectAExtensions);
		ApplyCompleteIgnoreCounts(coordinator);
		coordinator.PopulateIgnoreOptionsForRootSelection([], projectA);
		var initialProjectAState = SnapshotIgnoreState(viewModel.IgnoreOptions);
		AssertMutuallyExclusiveGitFilteringModes(initialProjectAState);

		currentPath = projectB;
		coordinator.ResetProjectProfileSelections(projectB);
		coordinator.ApplyExtensionScan([".cs", ".json"]);
		ApplyCompleteIgnoreCounts(coordinator);
		coordinator.PopulateIgnoreOptionsForRootSelection([], projectB);

		currentPath = projectA;
		coordinator.ApplyProjectProfileSelections(projectA, profileA);
		coordinator.ApplyExtensionScan(projectAExtensions);
		ApplyCompleteIgnoreCounts(coordinator);
		coordinator.PopulateIgnoreOptionsForRootSelection([], projectA);
		var restoredProjectAState = SnapshotIgnoreState(viewModel.IgnoreOptions);

		AssertIgnoreState(restoredProjectAState, initialProjectAState);
		AssertMutuallyExclusiveGitFilteringModes(restoredProjectAState);
	}

	[Fact]
	public void ProjectSwitch_TargetProjectProfileReplacesTheCompletePreviousSectionState()
	{
		const string projectA = @"C:\Workspace\ProjectA";
		const string projectB = @"C:\Workspace\ProjectB";
		var currentPath = projectA;
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(
			viewModel,
			currentPathProvider: () => currentPath,
			availabilityProvider: static (_, _) => CreateCompleteAvailability());
		var projectAState = CreateCompleteIgnoreState(
			IgnoreOptionId.SmartIgnore,
			IgnoreOptionId.UseGitIgnore,
			IgnoreOptionId.HiddenFiles,
			IgnoreOptionId.HideSecrets,
			IgnoreOptionId.StripComments);
		var projectBState = CreateCompleteIgnoreState(
			IgnoreOptionId.TrackedGitFilesOnly,
			IgnoreOptionId.DotFolders,
			IgnoreOptionId.EmptyFiles,
			IgnoreOptionId.HidePrivateData,
			IgnoreOptionId.CompressCode,
			IgnoreOptionId.StripBlankLines);

		coordinator.ApplyProjectProfileSelections(projectA, CreateProfile(projectAState));
		ApplyCompleteIgnoreCounts(coordinator);
		coordinator.PopulateIgnoreOptionsForRootSelection([], projectA);
		AssertIgnoreState(SnapshotIgnoreState(viewModel.IgnoreOptions), projectAState);

		currentPath = projectB;
		coordinator.ApplyProjectProfileSelections(projectB, CreateProfile(projectBState));
		ApplyCompleteIgnoreCounts(coordinator);
		coordinator.PopulateIgnoreOptionsForRootSelection([], projectB);

		AssertIgnoreState(SnapshotIgnoreState(viewModel.IgnoreOptions), projectBState);
		AssertMutuallyExclusiveGitFilteringModes(SnapshotIgnoreState(viewModel.IgnoreOptions));
	}

	[Fact]
	public void ProjectSwitch_PreparedProfileReadDoesNotImportPreviousProjectCheckboxes()
	{
		const string projectA = @"C:\Workspace\ProjectA";
		const string projectB = @"C:\Workspace\ProjectB";
		var currentPath = projectA;
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(
			viewModel,
			currentPathProvider: () => currentPath,
			availabilityProvider: static (_, _) => CreateCompleteAvailability());
		var projectAState = CreateCompleteIgnoreState(
			IgnoreOptionId.UseGitIgnore,
			IgnoreOptionId.SmartIgnore,
			IgnoreOptionId.HideSecrets);
		var projectBState = CreateCompleteIgnoreState(
			IgnoreOptionId.TrackedGitFilesOnly,
			IgnoreOptionId.HidePrivateData);

		coordinator.ApplyProjectProfileSelections(projectA, CreateProfile(projectAState));
		ApplyCompleteIgnoreCounts(coordinator);
		coordinator.PopulateIgnoreOptionsForRootSelection([], projectA);
		Assert.True(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.UseGitIgnore).IsChecked);
		Assert.False(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.TrackedGitFilesOnly).IsChecked);

		currentPath = projectB;
		coordinator.ApplyProjectProfileSelections(projectB, CreateProfile(projectBState));
		var selectedBeforePublication = coordinator.GetSelectedIgnoreOptionIds();

		Assert.Equal(
			projectBState.Where(static pair => pair.Value).Select(static pair => pair.Key).Order(),
			selectedBeforePublication.Order());

		ApplyCompleteIgnoreCounts(coordinator);
		coordinator.PopulateIgnoreOptionsForRootSelection([], projectB);
		AssertIgnoreState(SnapshotIgnoreState(viewModel.IgnoreOptions), projectBState);
	}

	[Theory]
	[InlineData(PreparedEventKind.ExtensionItem)]
	[InlineData(PreparedEventKind.IgnoreItem)]
	[InlineData(PreparedEventKind.ExtensionsAll)]
	[InlineData(PreparedEventKind.IgnoreAll)]
	[InlineData(PreparedEventKind.ContentProcessingAll)]
	public void ProjectSwitch_PreparedProfileIgnoresStaleViewModelEvents(PreparedEventKind eventKind)
	{
		const string projectA = @"C:\Workspace\ProjectA";
		const string projectB = @"C:\Workspace\ProjectB";
		var currentPath = projectA;
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(
			viewModel,
			() => currentPath,
			static (_, _) => CreateCompleteAvailability());
		coordinator.ApplyProjectProfileSelections(
			projectA,
			CreateProfile(CreateCompleteIgnoreState(IgnoreOptionId.HiddenFiles, IgnoreOptionId.HideSecrets)));
		coordinator.ApplyExtensionScan([".cs", ".md"]);
		coordinator.PopulateIgnoreOptionsForRootSelection([], projectA);
		coordinator.ConsumePreparedSelectionForPath(projectA);
		coordinator.HookOptionListeners(viewModel.Extensions);
		coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);

		currentPath = projectB;
		var projectBState = CreateCompleteIgnoreState(
			IgnoreOptionId.DotFolders,
			IgnoreOptionId.HidePrivateData,
			IgnoreOptionId.CompressCode);
		coordinator.ApplyProjectProfileSelections(projectB, CreateProfile(projectBState));
		var before = coordinator.CaptureProjectCheckpoint().Session;
		var revisionBefore = coordinator.CurrentSelectionRevision;
		var extensionVisualStateBefore = viewModel.Extensions.ToDictionary(
			static option => option.Name,
			static option => option.IsChecked,
			StringComparer.OrdinalIgnoreCase);
		var ignoreVisualStateBefore = SnapshotIgnoreState(viewModel.IgnoreOptions);
		var allExtensionsBefore = viewModel.AllExtensionsChecked;
		var allIgnoreBefore = viewModel.AllIgnoreChecked;
		var allContentProcessingBefore = viewModel.AllContentProcessingChecked;

		switch (eventKind)
		{
			case PreparedEventKind.ExtensionItem:
				viewModel.Extensions[0].IsChecked = !viewModel.Extensions[0].IsChecked;
				break;
			case PreparedEventKind.IgnoreItem:
				var hideSecrets = viewModel.IgnoreOptions.Single(
					static option => option.Id == IgnoreOptionId.HideSecrets);
				hideSecrets.IsChecked = !hideSecrets.IsChecked;
				break;
			case PreparedEventKind.ExtensionsAll:
				viewModel.AllExtensionsChecked = !allExtensionsBefore;
				coordinator.HandleExtensionsAllChanged(!allExtensionsBefore);
				break;
			case PreparedEventKind.IgnoreAll:
				viewModel.AllIgnoreChecked = !allIgnoreBefore;
				coordinator.HandleIgnoreAllChanged(!allIgnoreBefore, projectB);
				break;
			case PreparedEventKind.ContentProcessingAll:
				viewModel.AllContentProcessingChecked = !allContentProcessingBefore;
				coordinator.HandleContentProcessingAllChanged(!allContentProcessingBefore);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(eventKind), eventKind, null);
		}

		var after = coordinator.CaptureProjectCheckpoint().Session;
		Assert.Equal(revisionBefore, coordinator.CurrentSelectionRevision);
		Assert.Equal(
			before.Extensions.OptionStates.OrderBy(static pair => pair.Key),
			after.Extensions.OptionStates.OrderBy(static pair => pair.Key));
		Assert.Equal(
			before.IgnoreOptions.OptionStateCache.OrderBy(static pair => pair.Key),
			after.IgnoreOptions.OptionStateCache.OrderBy(static pair => pair.Key));
		Assert.Equal(
			projectBState.Where(static pair => pair.Value).Select(static pair => pair.Key).Order(),
			coordinator.GetSelectedIgnoreOptionIds().Order());
		Assert.Equal(allExtensionsBefore, viewModel.AllExtensionsChecked);
		Assert.Equal(allIgnoreBefore, viewModel.AllIgnoreChecked);
		Assert.Equal(allContentProcessingBefore, viewModel.AllContentProcessingChecked);
		Assert.All(
			viewModel.Extensions,
			option => Assert.Equal(extensionVisualStateBefore[option.Name], option.IsChecked));
		AssertIgnoreState(SnapshotIgnoreState(viewModel.IgnoreOptions), ignoreVisualStateBefore);
	}

	[Fact]
	public void ProjectSwitch_PreparedProfileDoesNotUsePreviousProjectsAvailability()
	{
		const string projectA = @"C:\Workspace\ProjectA";
		const string projectB = @"C:\Workspace\ProjectB";
		var currentPath = projectA;
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(
			viewModel,
			() => currentPath,
			(path, _) => PathComparer.Default.Equals(path, projectB)
				? new IgnoreOptionsAvailability(IncludeGitIgnore: true, IncludeSmartIgnore: false)
				: new IgnoreOptionsAvailability(IncludeGitIgnore: false, IncludeSmartIgnore: false));
		coordinator.ResetProjectProfileSelections(projectA);
		coordinator.PopulateIgnoreOptionsForRootSelection([], projectA);
		coordinator.ConsumePreparedSelectionForPath(projectA);
		Assert.DoesNotContain(viewModel.IgnoreOptions, static option => option.Id == IgnoreOptionId.UseGitIgnore);

		currentPath = projectB;
		coordinator.ApplyProjectProfileSelections(
			projectB,
			new ProjectSelectionProfile([], [], [IgnoreOptionId.UseGitIgnore]));

		Assert.Contains(IgnoreOptionId.UseGitIgnore, coordinator.GetSelectedIgnoreOptionIds());
		coordinator.PopulateIgnoreOptionsForRootSelection([], projectB);
		Assert.True(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.UseGitIgnore).IsChecked);
	}

	[Fact]
	public void ProjectSwitch_PreparedProfileUsesTargetDescriptorsOnceTheyArePublished()
	{
		const string projectA = @"C:\Workspace\ProjectA";
		const string projectB = @"C:\Workspace\ProjectB";
		var currentPath = projectA;
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(
			viewModel,
			() => currentPath,
			(path, _) => new IgnoreOptionsAvailability(
				IncludeGitIgnore: PathComparer.Default.Equals(path, projectA),
				IncludeSmartIgnore: false));
		coordinator.ResetProjectProfileSelections(projectA);
		coordinator.PopulateIgnoreOptionsForRootSelection([], projectA);
		coordinator.ConsumePreparedSelectionForPath(projectA);
		Assert.Contains(viewModel.IgnoreOptions, static option => option.Id == IgnoreOptionId.UseGitIgnore);

		currentPath = projectB;
		coordinator.ApplyProjectProfileSelections(
			projectB,
			new ProjectSelectionProfile([], [], [IgnoreOptionId.UseGitIgnore]));
		Assert.Contains(IgnoreOptionId.UseGitIgnore, coordinator.GetSelectedIgnoreOptionIds());

		coordinator.PopulateIgnoreOptionsForRootSelection([], projectB);

		Assert.DoesNotContain(viewModel.IgnoreOptions, static option => option.Id == IgnoreOptionId.UseGitIgnore);
		Assert.DoesNotContain(IgnoreOptionId.UseGitIgnore, coordinator.GetSelectedIgnoreOptionIds());
		Assert.True(coordinator.SnapshotIgnoreOptionStatesForPersistence()![IgnoreOptionId.UseGitIgnore]);
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
			new[] { IgnoreOptionId.HiddenFiles, IgnoreOptionId.DotFolders, IgnoreOptionId.EmptyFiles },
			new[] { IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore, IgnoreOptionId.HiddenFolders },
			new[] { IgnoreOptionId.HideSecrets },
			new[] { IgnoreOptionId.HidePrivateData },
			new[] { IgnoreOptionId.CompressCode },
			new[] { IgnoreOptionId.StripComments, IgnoreOptionId.StripBlankLines },
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
				["Settings.Ignore.HidePrivateData"] = "Hide private data",
				["Settings.Ignore.CompressCode"] = "Compress code",
				["Settings.Ignore.StripComments"] = "Strip comments",
				["Settings.Ignore.StripBlankLines"] = "Strip blank lines",
				["Settings.Ignore.UseGitIgnore"] = "Use .gitignore",
				["Settings.Ignore.TrackedGitFilesOnly"] = "Tracked Git files only",
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

	private static ProjectSelectionProfile CreateProfile(
		IReadOnlyDictionary<IgnoreOptionId, bool> state)
	{
		return new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: state
				.Where(static pair => pair.Value)
				.Select(static pair => pair.Key)
				.ToArray(),
			IgnoreOptionStates: state);
	}

	private static IReadOnlyDictionary<IgnoreOptionId, bool> CreateCompleteIgnoreState(
		params IgnoreOptionId[] selectedOptions)
	{
		var selected = selectedOptions.ToHashSet();
		return CompletePublishedSectionIds.ToDictionary(
			static optionId => optionId,
			optionId => selected.Contains(optionId));
	}

	private static IgnoreOptionsAvailability CreateCompleteAvailability() =>
		new(
			IncludeGitIgnore: true,
			IncludeSmartIgnore: true,
			IncludeEmptyFolders: true,
			IncludeEmptyFiles: true,
			IncludeExtensionlessFiles: true,
			IncludeTrackedGitFilesOnly: true);

	private static void ApplyCompleteIgnoreCounts(SelectionSyncCoordinator coordinator)
	{
		var apply = typeof(SelectionSyncCoordinator).GetMethod(
			"ApplyExtensionOptions",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(apply);
		apply.Invoke(
			coordinator,
			[
				Array.Empty<SelectionOption>(),
				0,
				new IgnoreOptionCounts(
					HiddenFolders: 1,
					HiddenFiles: 1,
					DotFolders: 1,
					DotFiles: 1,
					EmptyFolders: 1,
					EmptyFiles: 1,
					ExtensionlessFiles: 1),
				new IgnoreControllerImpactCounts(GitIgnore: 1, SmartIgnore: 1),
				true
			]);
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
