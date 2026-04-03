using DevProjex.Application.Models;

namespace DevProjex.Tests.Unit;

public sealed class SelectionSyncCoordinatorEffectiveEmptyFolderCountsTests
{
	[Theory]
	[MemberData(nameof(ExtensionSelectionCases))]
	public void BuildEffectiveAllowedExtensionsForLiveCounts_UsesSelectionAwareAllowedExtensions(
		string scenario,
		IReadOnlyList<SelectionOption> extensionOptions,
		int expectedEmptyFolders)
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, currentPath: "/workspace");
		Assert.True(expectedEmptyFolders >= 0);

		ApplyPreviousExtensionSelection(viewModel, coordinator, extensionOptions);
		var allowedExtensions = ResolveAllowedExtensions(coordinator, forceAllExtensionsChecked: false);

		var expectedAllowedExtensions = scenario switch
		{
			"all-visible" => [".cs", ".md"],
			"markdown-hidden" => [".cs"],
			"all-unchecked" => Array.Empty<string>(),
			_ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
		};

		Assert.NotNull(allowedExtensions);
		Assert.True(allowedExtensions!.SequenceEqual(expectedAllowedExtensions));
	}

	[Fact]
	public void BuildEffectiveAllowedExtensionsForLiveCounts_WhenAllExtensionsToggleIsForced_ReturnsNull()
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, currentPath: "/workspace");
		ApplyPreviousExtensionSelection(
			viewModel,
			coordinator,
			[
				new SelectionOption(".cs", true),
				new SelectionOption(".md", false)
			]);

		var allowedExtensions = ResolveAllowedExtensions(coordinator, forceAllExtensionsChecked: true);

		Assert.Null(allowedExtensions);
	}

	[Theory]
	[MemberData(nameof(ExtensionSelectionCases))]
	public void PopulateIgnoreOptionsForRootSelection_UsesEffectiveEmptyFoldersCountResolvedFromSelection(
		string scenario,
		IReadOnlyList<SelectionOption> extensionOptions,
		int expectedEmptyFolders)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFolder("src");

		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, temp.Path);
		Assert.False(string.IsNullOrWhiteSpace(scenario));

		ApplyPreviousExtensionSelection(viewModel, coordinator, extensionOptions);
		ApplyExtensionScanState(
			coordinator,
			extensionOptions,
			new IgnoreOptionCounts(HiddenFolders: 99, EmptyFolders: expectedEmptyFolders),
			hasIgnoreCounts: true);
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], temp.Path);

		var hiddenFolders = viewModel.IgnoreOptions.Single(item => item.Id == IgnoreOptionId.HiddenFolders);
		Assert.Equal("Hidden folders (99)", hiddenFolders.Label);

		var option = viewModel.IgnoreOptions.SingleOrDefault(item => item.Id == IgnoreOptionId.EmptyFolders);
		if (expectedEmptyFolders == 0)
		{
			Assert.Null(option);
			return;
		}

		Assert.NotNull(option);
		Assert.Equal($"Empty folders ({expectedEmptyFolders})", option!.Label);
		Assert.True(option.IsChecked);
	}

	public static IEnumerable<object[]> ExtensionSelectionCases()
	{
		yield return
		[
			"all-visible",
			new List<SelectionOption>
			{
				new(".cs", true),
				new(".md", true)
			},
			0
		];

		yield return
		[
			"markdown-hidden",
			new List<SelectionOption>
			{
				new(".cs", true),
				new(".md", false)
			},
			2
		];

		yield return
		[
			"all-unchecked",
			new List<SelectionOption>
			{
				new(".cs", false),
				new(".md", false)
			},
			3
		];
	}

	private static void ApplyPreviousExtensionSelection(
		MainWindowViewModel viewModel,
		SelectionSyncCoordinator coordinator,
		IReadOnlyCollection<SelectionOption> options)
	{
		viewModel.Extensions.Clear();
		foreach (var option in options)
			viewModel.Extensions.Add(new SelectionOptionViewModel(option.Name, option.IsChecked));

		coordinator.UpdateExtensionsSelectionCache();
		viewModel.AllExtensionsChecked = options.All(option => option.IsChecked);
	}

	private static string[]? ResolveAllowedExtensions(
		SelectionSyncCoordinator coordinator,
		bool forceAllExtensionsChecked)
	{
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"BuildEffectiveAllowedExtensionsForLiveCounts",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);

		var result = method!.Invoke(coordinator, [forceAllExtensionsChecked]);
		if (result is null)
			return null;

		return Assert.IsType<HashSet<string>>(result)
			.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static void ApplyExtensionScanState(
		SelectionSyncCoordinator coordinator,
		IReadOnlyCollection<SelectionOption> options,
		IgnoreOptionCounts ignoreCounts,
		bool hasIgnoreCounts)
	{
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"ApplyExtensionOptions",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method!.Invoke(coordinator, [options, 0, ignoreCounts, hasIgnoreCounts]);
	}

	private static IgnoreRules CreateRules() => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(),
		SmartIgnoredFiles: new HashSet<string>());

	private static MainWindowViewModel CreateViewModel()
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		return new MainWindowViewModel(localization, new HelpContentProvider());
	}

	private static SelectionSyncCoordinator CreateCoordinator(
		MainWindowViewModel viewModel,
		string currentPath)
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		var filterSelectionService = new FilterOptionSelectionService();
		var ignoreOptionsService = new IgnoreOptionsService(localization);

		return new SelectionSyncCoordinator(
			viewModel,
			new ScanOptionsUseCase(new MinimalScanner()),
			filterSelectionService,
			ignoreOptionsService,
			(_, _, _) => CreateRules(),
			(_, _) => new IgnoreOptionsAvailability(
				IncludeGitIgnore: false,
				IncludeSmartIgnore: false,
				ShowAdvancedCounts: true),
			_ => false,
			() => currentPath);
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
				["Settings.Ignore.EmptyFolders"] = "Empty folders",
				["Settings.Ignore.EmptyFiles"] = "Empty files",
				["Settings.Ignore.ExtensionlessFiles"] = "Files without extension"
			}
		};

		return new StubLocalizationCatalog(data);
	}

	private sealed class MinimalScanner : IFileSystemScanner
	{
		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);

		public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);

		public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new(["src"], false, false);
	}
}
