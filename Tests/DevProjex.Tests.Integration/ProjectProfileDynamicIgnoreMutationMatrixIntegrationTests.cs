using DevProjex.Application.Models;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.ViewModels;

namespace DevProjex.Tests.Integration;

public sealed class ProjectProfileDynamicIgnoreMutationMatrixIntegrationTests
{
	[Theory]
	[MemberData(nameof(MutationCases))]
	public void ProjectProfileMutationMatrix_DynamicIgnoreState_AppliesExpectedPreparedMode(
		int pathMode,
		IgnoreOptionId dynamicOptionId,
		ProfileMutationMode mutationMode)
	{
		using var temp = new TemporaryDirectory();
		var store = new ProjectProfileStore(() => temp.Path);
		var canonicalPath = Path.Combine(temp.Path, "workspace", "RepoA");
		Directory.CreateDirectory(canonicalPath);
		var savePath = BuildPathByMode(canonicalPath, pathMode);

		ApplyMutation(store, savePath, dynamicOptionId, mutationMode);

		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(
			viewModel,
			includeGitIgnore: mutationMode == ProfileMutationMode.UnavailableOnly);

		ApplyPreparedSelections(store, coordinator, canonicalPath);

		ApplyIgnoreCounts(
			coordinator,
			BuildCounts(
				dynamicOptionId,
				dynamicVisible: false,
				GetVisibleStaticOptions(mutationMode)));
		coordinator.PopulateIgnoreOptionsForRootSelection([], canonicalPath);
		Assert.DoesNotContain(viewModel.IgnoreOptions, option => option.Id == dynamicOptionId);

		ApplyIgnoreCounts(
			coordinator,
			BuildCounts(
				dynamicOptionId,
				dynamicVisible: true,
				GetVisibleStaticOptions(mutationMode)));
		coordinator.PopulateIgnoreOptionsForRootSelection([], canonicalPath);

		AssertExpectedPreparedState(viewModel, coordinator, dynamicOptionId, mutationMode);
	}

	public static IEnumerable<object[]> MutationCases()
	{
		var pathModes = new[] { 0, 1, 2, 3 };
		var dynamicOptionIds = new[]
		{
			IgnoreOptionId.EmptyFolders,
			IgnoreOptionId.EmptyFiles,
			IgnoreOptionId.ExtensionlessFiles
		};

		foreach (var pathMode in pathModes)
		foreach (var dynamicOptionId in dynamicOptionIds)
		foreach (var mutationMode in Enum.GetValues<ProfileMutationMode>())
			yield return [pathMode, dynamicOptionId, mutationMode];
	}

	private static void ApplyPreparedSelections(
		ProjectProfileStore store,
		SelectionSyncCoordinator coordinator,
		string canonicalPath)
	{
		if (store.TryLoadProfile(canonicalPath, out var profile))
			coordinator.ApplyProjectProfileSelections(canonicalPath, profile);
		else
			coordinator.ResetProjectProfileSelections(canonicalPath);
	}

	private static void ApplyMutation(
		ProjectProfileStore store,
		string savePath,
		IgnoreOptionId dynamicOptionId,
		ProfileMutationMode mutationMode)
	{
		switch (mutationMode)
		{
			case ProfileMutationMode.SelectedDynamicOnly:
				store.SaveProfile(savePath, CreateProfile([dynamicOptionId]));
				break;
			case ProfileMutationMode.EmptySelection:
				store.SaveProfile(savePath, CreateProfile([]));
				break;
			case ProfileMutationMode.UnavailableOnly:
				store.SaveProfile(savePath, CreateProfile([IgnoreOptionId.UseGitIgnore]));
				break;
			case ProfileMutationMode.MixedVisibleSelection:
				store.SaveProfile(savePath, CreateProfile([dynamicOptionId, IgnoreOptionId.HiddenFiles]));
				break;
			case ProfileMutationMode.ClearedAfterSave:
				store.SaveProfile(savePath, CreateProfile([dynamicOptionId]));
				store.ClearAllProfiles();
				break;
			case ProfileMutationMode.CorruptedAfterSave:
				store.SaveProfile(savePath, CreateProfile([dynamicOptionId]));
				File.WriteAllText(store.GetPath(), "{ definitely-not-valid-json");
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(mutationMode), mutationMode, null);
		}
	}

	private static void AssertExpectedPreparedState(
		MainWindowViewModel viewModel,
		SelectionSyncCoordinator coordinator,
		IgnoreOptionId dynamicOptionId,
		ProfileMutationMode mutationMode)
	{
		var selectedIds = coordinator.GetSelectedIgnoreOptionIds().ToHashSet();

		switch (mutationMode)
		{
			case ProfileMutationMode.SelectedDynamicOnly:
				Assert.True(GetIgnoreOption(viewModel, dynamicOptionId).IsChecked);
				Assert.True(GetIgnoreOption(viewModel, IgnoreOptionId.HiddenFiles).IsChecked);
				Assert.True(viewModel.AllIgnoreChecked);
				AssertIgnoreSetEqual(selectedIds, [dynamicOptionId, IgnoreOptionId.HiddenFiles]);
				break;

			case ProfileMutationMode.EmptySelection:
				Assert.False(GetIgnoreOption(viewModel, dynamicOptionId).IsChecked);
				Assert.False(GetIgnoreOption(viewModel, IgnoreOptionId.HiddenFolders).IsChecked);
				Assert.False(viewModel.AllIgnoreChecked);
				Assert.Empty(selectedIds);
				break;

			case ProfileMutationMode.UnavailableOnly:
				Assert.False(GetIgnoreOption(viewModel, dynamicOptionId).IsChecked);
				Assert.False(GetIgnoreOption(viewModel, IgnoreOptionId.HiddenFolders).IsChecked);
				Assert.False(viewModel.AllIgnoreChecked);
				AssertIgnoreSetEqual(selectedIds, [IgnoreOptionId.UseGitIgnore]);
				break;

			case ProfileMutationMode.ClearedAfterSave:
			case ProfileMutationMode.CorruptedAfterSave:
				Assert.True(GetIgnoreOption(viewModel, dynamicOptionId).IsChecked);
				Assert.True(GetIgnoreOption(viewModel, IgnoreOptionId.HiddenFolders).IsChecked);
				Assert.True(viewModel.AllIgnoreChecked);
				AssertIgnoreSetEqual(selectedIds, [dynamicOptionId, IgnoreOptionId.HiddenFolders]);
				break;

			case ProfileMutationMode.MixedVisibleSelection:
				Assert.True(GetIgnoreOption(viewModel, dynamicOptionId).IsChecked);
				Assert.True(GetIgnoreOption(viewModel, IgnoreOptionId.HiddenFiles).IsChecked);
				Assert.False(GetIgnoreOption(viewModel, IgnoreOptionId.HiddenFolders).IsChecked);
				Assert.False(viewModel.AllIgnoreChecked);
				AssertIgnoreSetEqual(selectedIds, [dynamicOptionId, IgnoreOptionId.HiddenFiles]);
				break;

			default:
				throw new ArgumentOutOfRangeException(nameof(mutationMode), mutationMode, null);
		}
	}

	private static IgnoreOptionViewModel GetIgnoreOption(MainWindowViewModel viewModel, IgnoreOptionId id)
	{
		return Assert.Single(viewModel.IgnoreOptions.Where(option => option.Id == id));
	}

	private static void AssertIgnoreSetEqual(
		IReadOnlyCollection<IgnoreOptionId> actual,
		IEnumerable<IgnoreOptionId> expected)
	{
		var actualSet = actual.ToHashSet();
		var expectedSet = expected.ToHashSet();
		Assert.Equal(expectedSet.Count, actualSet.Count);
		foreach (var optionId in expectedSet)
			Assert.Contains(optionId, actualSet);
	}

	private static IgnoreOptionCounts BuildCounts(
		IgnoreOptionId dynamicOptionId,
		bool dynamicVisible,
		IReadOnlyCollection<IgnoreOptionId> visibleStaticOptions)
	{
		var counts = IgnoreOptionCounts.Empty;
		foreach (var optionId in visibleStaticOptions)
			counts = counts.Add(BuildSingleCount(optionId, 1));

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

	private static IReadOnlyCollection<IgnoreOptionId> GetVisibleStaticOptions(ProfileMutationMode mutationMode)
	{
		return mutationMode switch
		{
			ProfileMutationMode.SelectedDynamicOnly => [IgnoreOptionId.HiddenFiles],
			ProfileMutationMode.EmptySelection => [IgnoreOptionId.HiddenFolders],
			ProfileMutationMode.UnavailableOnly => [IgnoreOptionId.HiddenFolders],
			ProfileMutationMode.ClearedAfterSave => [IgnoreOptionId.HiddenFolders],
			ProfileMutationMode.CorruptedAfterSave => [IgnoreOptionId.HiddenFolders],
			ProfileMutationMode.MixedVisibleSelection => [IgnoreOptionId.HiddenFolders, IgnoreOptionId.HiddenFiles],
			_ => Array.Empty<IgnoreOptionId>()
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

	private static ProjectSelectionProfile CreateProfile(IReadOnlyCollection<IgnoreOptionId> selectedIgnoreOptions)
	{
		return new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: selectedIgnoreOptions);
	}

	private static SelectionSyncCoordinator CreateCoordinator(
		MainWindowViewModel viewModel,
		bool includeGitIgnore)
	{
		var localization = new LocalizationService(new TestLocalizationCatalog(), AppLanguage.En);
		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
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
				UseGitIgnore = includeGitIgnore
			},
			(_, _) => new IgnoreOptionsAvailability(
				IncludeGitIgnore: includeGitIgnore,
				IncludeSmartIgnore: false,
				ShowAdvancedCounts: true),
			_ => false,
			() => null);
	}

	private static MainWindowViewModel CreateViewModel()
	{
		var localization = new LocalizationService(new TestLocalizationCatalog(), AppLanguage.En);
		return new MainWindowViewModel(localization, new HelpContentProvider());
	}

	private static string BuildPathByMode(string canonicalPath, int mode)
	{
		return mode switch
		{
			0 => canonicalPath,
			1 => $"{canonicalPath}{Path.DirectorySeparatorChar}",
			2 => canonicalPath.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			3 => Path.GetRelativePath(Environment.CurrentDirectory, canonicalPath),
			_ => canonicalPath
		};
	}

	private sealed class TestLocalizationCatalog : ILocalizationCatalog
	{
		private static readonly IReadOnlyDictionary<string, string> EnglishCatalog =
			new Dictionary<string, string>
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
			};

		public IReadOnlyDictionary<string, string> Get(AppLanguage language)
		{
			return EnglishCatalog;
		}
	}

	public enum ProfileMutationMode
	{
		SelectedDynamicOnly = 0,
		EmptySelection = 1,
		UnavailableOnly = 2,
		MixedVisibleSelection = 3,
		ClearedAfterSave = 4,
		CorruptedAfterSave = 5
	}
}
