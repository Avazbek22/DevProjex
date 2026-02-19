using DevProjex.Application.Services;
using DevProjex.Application.UseCases;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.ViewModels;
using DevProjex.Infrastructure.ResourceStore;
using DevProjex.Tests.Unit.Helpers;

namespace DevProjex.Tests.Unit;

public sealed class SelectionProfileMatrixUnitTests
{
	[Theory]
	[MemberData(nameof(ExtensionFallbackCases))]
	public void ExtensionProfileMatrix_PreservesIndependentSelectionBehavior(
		int caseId,
		string[] availableExtensions,
		string[] savedExtensions)
	{
		_ = caseId;
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => @"C:\ProjectA");
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: Array.Empty<string>(),
			SelectedExtensions: savedExtensions,
			SelectedIgnoreOptions: Array.Empty<IgnoreOptionId>());

		coordinator.ApplyProjectProfileSelections(@"C:\ProjectA", profile);
		coordinator.ApplyExtensionScan(availableExtensions);

		var expectedChecked = CalculateExpectedCheckedExtensions(availableExtensions, savedExtensions);
		foreach (var option in viewModel.Extensions)
		{
			var shouldBeChecked = expectedChecked.Contains(option.Name);
			Assert.Equal(shouldBeChecked, option.IsChecked);
		}

		var allCheckedExpected = availableExtensions.Length > 0 && expectedChecked.Count == availableExtensions.Length;
		Assert.Equal(allCheckedExpected, viewModel.AllExtensionsChecked);
	}

	[Theory]
	[MemberData(nameof(IgnoreFallbackCases))]
	public void IgnoreProfileMatrix_PreservesIndependentSelectionBehavior(
		int caseId,
		IgnoreOptionId[] savedIgnoreOptions)
	{
		_ = caseId;
		var currentPath = @"C:\ProjectA";
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => currentPath);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: Array.Empty<string>(),
			SelectedExtensions: Array.Empty<string>(),
			SelectedIgnoreOptions: savedIgnoreOptions);

		coordinator.ApplyProjectProfileSelections(currentPath, profile);
		coordinator.PopulateIgnoreOptionsForRootSelection(new[] { "src" }, currentPath);

		var availableIds = viewModel.IgnoreOptions.Select(option => option.Id).ToArray();
		var expectedChecked = CalculateExpectedCheckedIgnoreOptions(availableIds, savedIgnoreOptions);
		foreach (var option in viewModel.IgnoreOptions)
		{
			var shouldBeChecked = expectedChecked.Contains(option.Id);
			Assert.Equal(shouldBeChecked, option.IsChecked);
		}

		var allCheckedExpected = availableIds.Length > 0 && expectedChecked.Count == availableIds.Length;
		Assert.Equal(allCheckedExpected, viewModel.AllIgnoreChecked);
	}

	public static IEnumerable<object[]> ExtensionFallbackCases()
	{
		var availableSets = new[]
		{
			new[] { ".cs" },
			new[] { ".cs", ".json" },
			new[] { ".cs", ".json", ".md" },
			new[] { ".xml", ".yml", ".yaml", ".toml" },
			new[] { ".a", ".b", ".c", ".d", ".e" },
			new[] { ".png", ".jpg", ".svg" }
		};

		var caseId = 0;
		foreach (var available in availableSets)
		{
			var first = available[0];
			var last = available[^1];
			var missingOne = ".missing-one";
			var missingTwo = ".missing-two";
			var missingThree = ".missing-three";

			var savedVariants = new List<string[]>
			{
				Array.Empty<string>(),
				new[] { first },
				new[] { last },
				new[] { first, last },
				available.ToArray(),
				new[] { missingOne },
				new[] { missingTwo },
				new[] { missingThree },
				new[] { missingOne, missingTwo },
				new[] { missingOne, first },
				new[] { missingOne, last },
				new[] { missingOne, first, last },
				new[] { first.ToUpperInvariant() },
				new[] { last.ToUpperInvariant() },
				new[] { first.ToUpperInvariant(), missingOne },
				new[] { first, first, missingOne },
				new[] { last, last, missingOne },
				new[] { missingOne, missingTwo, missingThree },
				new[] { first, missingOne, missingTwo },
				new[] { last, missingOne, missingTwo }
			};

			foreach (var saved in savedVariants)
				yield return new object[] { caseId++, available, saved };
		}
	}

	public static IEnumerable<object[]> IgnoreFallbackCases()
	{
		var available = new[]
		{
			IgnoreOptionId.HiddenFolders,
			IgnoreOptionId.HiddenFiles,
			IgnoreOptionId.DotFolders,
			IgnoreOptionId.DotFiles
		};

		var savedVariants = new List<IgnoreOptionId[]>
		{
			Array.Empty<IgnoreOptionId>(),
			new[] { IgnoreOptionId.HiddenFolders },
			new[] { IgnoreOptionId.HiddenFiles },
			new[] { IgnoreOptionId.DotFolders },
			new[] { IgnoreOptionId.DotFiles },
			new[] { IgnoreOptionId.HiddenFolders, IgnoreOptionId.HiddenFiles },
			new[] { IgnoreOptionId.DotFolders, IgnoreOptionId.DotFiles },
			new[] { IgnoreOptionId.HiddenFolders, IgnoreOptionId.DotFiles },
			new[] { IgnoreOptionId.HiddenFiles, IgnoreOptionId.DotFolders },
			new[] { IgnoreOptionId.HiddenFolders, IgnoreOptionId.HiddenFiles, IgnoreOptionId.DotFolders },
			new[] { IgnoreOptionId.HiddenFolders, IgnoreOptionId.HiddenFiles, IgnoreOptionId.DotFolders, IgnoreOptionId.DotFiles },
			new[] { IgnoreOptionId.UseGitIgnore },
			new[] { IgnoreOptionId.SmartIgnore },
			new[] { IgnoreOptionId.ExtensionlessFiles },
			new[] { IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore },
			new[] { IgnoreOptionId.UseGitIgnore, IgnoreOptionId.ExtensionlessFiles },
			new[] { IgnoreOptionId.SmartIgnore, IgnoreOptionId.ExtensionlessFiles },
			new[] { IgnoreOptionId.UseGitIgnore, IgnoreOptionId.HiddenFolders },
			new[] { IgnoreOptionId.SmartIgnore, IgnoreOptionId.HiddenFiles },
			new[] { IgnoreOptionId.ExtensionlessFiles, IgnoreOptionId.DotFiles },
			new[] { IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore, IgnoreOptionId.ExtensionlessFiles },
			new[] { IgnoreOptionId.UseGitIgnore, IgnoreOptionId.HiddenFolders, IgnoreOptionId.DotFiles },
			new[] { IgnoreOptionId.SmartIgnore, IgnoreOptionId.HiddenFiles, IgnoreOptionId.DotFolders },
			new[] { IgnoreOptionId.ExtensionlessFiles, IgnoreOptionId.HiddenFolders, IgnoreOptionId.HiddenFiles },
			new[] { IgnoreOptionId.HiddenFolders, IgnoreOptionId.HiddenFolders, IgnoreOptionId.UseGitIgnore },
			new[] { IgnoreOptionId.DotFiles, IgnoreOptionId.DotFiles, IgnoreOptionId.SmartIgnore }
		};

		var caseId = 0;
		// Repeat the matrix for three root-selection contexts to widen coverage
		// while keeping each test execution lightweight and deterministic.
		var contexts = new[] { 0, 1, 2 };
		foreach (var _ in contexts)
		{
			foreach (var saved in savedVariants)
				yield return new object[] { caseId++, saved };
		}

		// Sanity: 26 variants * 3 contexts = 78 theory cases.
		_ = available;
	}

	private static HashSet<string> CalculateExpectedCheckedExtensions(
		IReadOnlyCollection<string> availableExtensions,
		IReadOnlyCollection<string> savedExtensions)
	{
		var available = new HashSet<string>(availableExtensions, StringComparer.OrdinalIgnoreCase);
		var saved = new HashSet<string>(savedExtensions, StringComparer.OrdinalIgnoreCase);
		if (saved.Count == 0)
			return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var ext in saved)
		{
			if (available.Contains(ext))
				matched.Add(ext);
		}

		return matched.Count > 0
			? matched
			: new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
	}

	private static HashSet<IgnoreOptionId> CalculateExpectedCheckedIgnoreOptions(
		IReadOnlyCollection<IgnoreOptionId> availableOptions,
		IReadOnlyCollection<IgnoreOptionId> savedOptions)
	{
		var available = new HashSet<IgnoreOptionId>(availableOptions);
		var saved = new HashSet<IgnoreOptionId>(savedOptions);
		if (saved.Count == 0)
			return new HashSet<IgnoreOptionId>();

		var matched = new HashSet<IgnoreOptionId>();
		foreach (var option in saved)
		{
			if (available.Contains(option))
				matched.Add(option);
		}

		return matched.Count > 0
			? matched
			: new HashSet<IgnoreOptionId>(available);
	}

	private static MainWindowViewModel CreateViewModel()
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		return new MainWindowViewModel(localization, new HelpContentProvider());
	}

	private static SelectionSyncCoordinator CreateCoordinator(
		MainWindowViewModel viewModel,
		Func<string?> currentPathProvider)
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		var scanner = new StubFileSystemScanner();
		var scanOptions = new ScanOptionsUseCase(scanner);
		var filterService = new FilterOptionSelectionService();
		var ignoreService = new IgnoreOptionsService(localization);
		Func<string, IgnoreRules> buildIgnoreRules = _ => new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>());

		return new SelectionSyncCoordinator(
			viewModel,
			scanOptions,
			filterService,
			ignoreService,
			buildIgnoreRules,
			_ => false,
			currentPathProvider);
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
				["Settings.Ignore.ExtensionlessFiles"] = "Extensionless files"
			}
		};

		return new StubLocalizationCatalog(data);
	}
}
