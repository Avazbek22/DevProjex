using DevProjex.Application.Models;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.ViewModels;

namespace DevProjex.Tests.Integration;

public sealed class ProjectProfileDynamicIgnoreNegativeMutationIntegrationTests
{
	[Theory]
	[MemberData(nameof(NegativeCases))]
	public void ProjectProfileNegativeMutationMatrix_DoesNotPromoteDynamicOptionsUnexpectedly(
		IgnoreOptionId dynamicOptionId,
		NegativeMutationMode mutationMode)
	{
		using var temp = new TemporaryDirectory();
		var store = new ProjectProfileStore(() => temp.Path);
		var canonicalPath = Path.Combine(temp.Path, "workspace", "RepoA");
		Directory.CreateDirectory(canonicalPath);

		ApplyMutation(store, canonicalPath, dynamicOptionId, mutationMode);

		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(
			viewModel,
			includeGitIgnore: mutationMode == NegativeMutationMode.UnavailableOnly);

		if (store.TryLoadProfile(canonicalPath, out var profile))
			coordinator.ApplyProjectProfileSelections(canonicalPath, profile);
		else
			coordinator.ResetProjectProfileSelections(canonicalPath);

		ApplyIgnoreCounts(coordinator, BuildCounts(dynamicOptionId, dynamicVisible: false));
		coordinator.PopulateIgnoreOptionsForRootSelection([], canonicalPath);
		Assert.DoesNotContain(viewModel.IgnoreOptions, option => option.Id == dynamicOptionId);

		ApplyIgnoreCounts(coordinator, BuildCounts(dynamicOptionId, dynamicVisible: true));
		coordinator.PopulateIgnoreOptionsForRootSelection([], canonicalPath);

		switch (mutationMode)
		{
			case NegativeMutationMode.EmptySelection:
				Assert.False(GetIgnoreOption(viewModel, dynamicOptionId).IsChecked);
				Assert.False(viewModel.AllIgnoreChecked);
				break;

			case NegativeMutationMode.UnavailableOnly:
				Assert.False(GetIgnoreOption(viewModel, dynamicOptionId).IsChecked);
				Assert.False(viewModel.AllIgnoreChecked);
				Assert.Contains(IgnoreOptionId.UseGitIgnore, coordinator.GetSelectedIgnoreOptionIds());
				break;

			case NegativeMutationMode.ManualFileRemoval:
			case NegativeMutationMode.CorruptedStorage:
				Assert.True(GetIgnoreOption(viewModel, dynamicOptionId).IsChecked);
				Assert.True(viewModel.AllIgnoreChecked);
				break;

			default:
				throw new ArgumentOutOfRangeException(nameof(mutationMode), mutationMode, null);
		}
	}

	public static IEnumerable<object[]> NegativeCases()
	{
		var dynamicOptionIds = new[]
		{
			IgnoreOptionId.EmptyFolders,
			IgnoreOptionId.EmptyFiles,
			IgnoreOptionId.ExtensionlessFiles
		};

		foreach (var dynamicOptionId in dynamicOptionIds)
		foreach (var mutationMode in Enum.GetValues<NegativeMutationMode>())
			yield return [dynamicOptionId, mutationMode];
	}

	private static void ApplyMutation(
		ProjectProfileStore store,
		string canonicalPath,
		IgnoreOptionId dynamicOptionId,
		NegativeMutationMode mutationMode)
	{
		switch (mutationMode)
		{
			case NegativeMutationMode.EmptySelection:
				store.SaveProfile(canonicalPath, CreateProfile([]));
				break;
			case NegativeMutationMode.UnavailableOnly:
				store.SaveProfile(canonicalPath, CreateProfile([IgnoreOptionId.UseGitIgnore]));
				break;
			case NegativeMutationMode.ManualFileRemoval:
				store.SaveProfile(canonicalPath, CreateProfile([dynamicOptionId]));
				File.Delete(store.GetPath());
				break;
			case NegativeMutationMode.CorruptedStorage:
				store.SaveProfile(canonicalPath, CreateProfile([dynamicOptionId]));
				File.WriteAllText(store.GetPath(), "{ broken-json");
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(mutationMode), mutationMode, null);
		}
	}

	private static IgnoreOptionViewModel GetIgnoreOption(MainWindowViewModel viewModel, IgnoreOptionId id)
	{
		return Assert.Single(viewModel.IgnoreOptions.Where(option => option.Id == id));
	}

	private static IgnoreOptionCounts BuildCounts(IgnoreOptionId dynamicOptionId, bool dynamicVisible)
	{
		if (!dynamicVisible)
			return IgnoreOptionCounts.Empty;

		return dynamicOptionId switch
		{
			IgnoreOptionId.EmptyFolders => new IgnoreOptionCounts(EmptyFolders: 2),
			IgnoreOptionId.EmptyFiles => new IgnoreOptionCounts(EmptyFiles: 3),
			IgnoreOptionId.ExtensionlessFiles => new IgnoreOptionCounts(ExtensionlessFiles: 4),
			_ => throw new ArgumentOutOfRangeException(nameof(dynamicOptionId), dynamicOptionId, null)
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

	public enum NegativeMutationMode
	{
		EmptySelection = 0,
		UnavailableOnly = 1,
		ManualFileRemoval = 2,
		CorruptedStorage = 3
	}
}
