namespace DevProjex.Tests.Unit;

public sealed class SelectionSyncCoordinatorIgnoreCountsRuntimeTests
{
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void PopulateIgnoreOptionsForRootSelection_CountDrivenAvailability_UpdatesIgnoreOptions(bool showAdvancedCounts)
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, showAdvancedCounts, currentPath: @"C:\Workspace\Project");

		ApplyScanState(
			coordinator,
			extensions: [".cs", "Dockerfile"],
			ignoreCounts: new IgnoreOptionCounts(
				HiddenFolders: 2,
				HiddenFiles: 0,
				DotFolders: 1,
				DotFiles: 0,
				EmptyFolders: 3,
				ExtensionlessFiles: 5,
				EmptyFiles: 4),
			hasIgnoreCounts: true);

		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], @"C:\Workspace\Project");

		Assert.Contains(viewModel.IgnoreOptions, o => o.Id == IgnoreOptionId.HiddenFolders);
		Assert.DoesNotContain(viewModel.IgnoreOptions, o => o.Id == IgnoreOptionId.HiddenFiles);
		Assert.Contains(viewModel.IgnoreOptions, o => o.Id == IgnoreOptionId.DotFolders);
		Assert.DoesNotContain(viewModel.IgnoreOptions, o => o.Id == IgnoreOptionId.DotFiles);
		Assert.Contains(viewModel.IgnoreOptions, o => o.Id == IgnoreOptionId.EmptyFolders);
		Assert.Contains(viewModel.IgnoreOptions, o => o.Id == IgnoreOptionId.EmptyFiles);
		Assert.Contains(viewModel.IgnoreOptions, o => o.Id == IgnoreOptionId.ExtensionlessFiles);

		AssertLabel(viewModel, IgnoreOptionId.HiddenFolders, "Hidden folders", 2, showAdvancedCounts);
		AssertLabel(viewModel, IgnoreOptionId.DotFolders, "dot folders", 1, showAdvancedCounts);
		AssertLabel(viewModel, IgnoreOptionId.EmptyFolders, "Empty folders", 3, showAdvancedCounts);
		AssertLabel(viewModel, IgnoreOptionId.EmptyFiles, "Empty files", 4, showAdvancedCounts);
		AssertLabel(viewModel, IgnoreOptionId.ExtensionlessFiles, "Files without extension", 5, showAdvancedCounts);
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_WhenCountBecomesZero_OptionIsTemporarilyHidden()
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, showAdvancedCounts: true, currentPath: @"C:\Workspace\Project");

		ApplyScanState(
			coordinator,
			extensions: [".cs", "Dockerfile"],
			ignoreCounts: new IgnoreOptionCounts(HiddenFolders: 2),
			hasIgnoreCounts: true);
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], @"C:\Workspace\Project");

		var hidden = viewModel.IgnoreOptions.Single(o => o.Id == IgnoreOptionId.HiddenFolders);
		hidden.IsChecked = true;
		coordinator.UpdateIgnoreSelectionCache();

		ApplyScanState(
			coordinator,
			extensions: [".cs", "Dockerfile"],
			ignoreCounts: IgnoreOptionCounts.Empty,
			hasIgnoreCounts: true);
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], @"C:\Workspace\Project");

		Assert.DoesNotContain(viewModel.IgnoreOptions, o => o.Id == IgnoreOptionId.HiddenFolders);
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_WhenCountReturns_OptionRestoresSelection()
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, showAdvancedCounts: true, currentPath: @"C:\Workspace\Project");

		ApplyScanState(
			coordinator,
			extensions: [".cs", "Dockerfile"],
			ignoreCounts: new IgnoreOptionCounts(HiddenFolders: 2),
			hasIgnoreCounts: true);
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], @"C:\Workspace\Project");

		viewModel.IgnoreOptions.Single(o => o.Id == IgnoreOptionId.HiddenFolders).IsChecked = true;
		coordinator.UpdateIgnoreSelectionCache();

		ApplyScanState(
			coordinator,
			extensions: [".cs", "Dockerfile"],
			ignoreCounts: IgnoreOptionCounts.Empty,
			hasIgnoreCounts: true);
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], @"C:\Workspace\Project");
		Assert.DoesNotContain(viewModel.IgnoreOptions, o => o.Id == IgnoreOptionId.HiddenFolders);

		ApplyScanState(
			coordinator,
			extensions: [".cs", "Dockerfile"],
			ignoreCounts: new IgnoreOptionCounts(HiddenFolders: 1),
			hasIgnoreCounts: true);
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], @"C:\Workspace\Project");

		var restored = viewModel.IgnoreOptions.Single(o => o.Id == IgnoreOptionId.HiddenFolders);
		Assert.True(restored.IsChecked);
		Assert.Equal("Hidden folders (1)", restored.Label);
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_EmptyFilesCountLifecycle_RestoresSelectionAndUpdatedCount()
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, showAdvancedCounts: true, currentPath: @"C:\Workspace\Project");

		ApplyScanState(
			coordinator,
			extensions: [".cs"],
			ignoreCounts: new IgnoreOptionCounts(EmptyFiles: 2),
			hasIgnoreCounts: true);
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], @"C:\Workspace\Project");

		var option = viewModel.IgnoreOptions.Single(o => o.Id == IgnoreOptionId.EmptyFiles);
		Assert.Equal("Empty files (2)", option.Label);
		option.IsChecked = true;
		coordinator.UpdateIgnoreSelectionCache();

		ApplyScanState(
			coordinator,
			extensions: [".cs"],
			ignoreCounts: IgnoreOptionCounts.Empty,
			hasIgnoreCounts: true);
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], @"C:\Workspace\Project");

		Assert.DoesNotContain(viewModel.IgnoreOptions, o => o.Id == IgnoreOptionId.EmptyFiles);

		ApplyScanState(
			coordinator,
			extensions: [".cs"],
			ignoreCounts: new IgnoreOptionCounts(EmptyFiles: 1),
			hasIgnoreCounts: true);
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], @"C:\Workspace\Project");

		var restored = viewModel.IgnoreOptions.Single(o => o.Id == IgnoreOptionId.EmptyFiles);
		Assert.True(restored.IsChecked);
		Assert.Equal("Empty files (1)", restored.Label);
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_HasIgnoreCountsFalse_UsesExtensionlessFallback()
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, showAdvancedCounts: true, currentPath: @"C:\Workspace\Project");

		ApplyScanState(
			coordinator,
			extensions: [".cs", "Dockerfile", "README"],
			ignoreCounts: IgnoreOptionCounts.Empty,
			hasIgnoreCounts: false);

		coordinator.PopulateIgnoreOptionsForRootSelection([], @"C:\Workspace\Project");

		var extensionless = viewModel.IgnoreOptions.Single(o => o.Id == IgnoreOptionId.ExtensionlessFiles);
		Assert.Equal("Files without extension (2)", extensionless.Label);
	}

	private static void ApplyScanState(
		SelectionSyncCoordinator coordinator,
		IReadOnlyCollection<string> extensions,
		IgnoreOptionCounts ignoreCounts,
		bool hasIgnoreCounts)
	{
		var filterService = new FilterOptionSelectionService();
		var options = filterService.BuildExtensionOptions(extensions, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
		var extensionlessCount = CountExtensionless(extensions);

		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"ApplyExtensionOptions",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method!.Invoke(coordinator, [options, extensionlessCount, ignoreCounts, IgnoreControllerImpactCounts.Empty, hasIgnoreCounts]);
	}

	private static int CountExtensionless(IEnumerable<string> extensions)
	{
		var count = 0;
		foreach (var extension in extensions)
		{
			if (string.IsNullOrWhiteSpace(extension))
				continue;

			if (string.IsNullOrWhiteSpace(Path.GetExtension(extension)))
				count++;
		}

		return count;
	}

	private static void AssertLabel(
		MainWindowViewModel viewModel,
		IgnoreOptionId optionId,
		string baseLabel,
		int count,
		bool showCount)
	{
		var option = viewModel.IgnoreOptions.Single(o => o.Id == optionId);
		var expected = showCount ? $"{baseLabel} ({count})" : baseLabel;
		Assert.Equal(expected, option.Label);
	}

	private static MainWindowViewModel CreateViewModel()
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		return new MainWindowViewModel(localization, new HelpContentProvider());
	}

	private static SelectionSyncCoordinator CreateCoordinator(
		MainWindowViewModel viewModel,
		bool showAdvancedCounts,
		string currentPath)
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		var scanOptions = new ScanOptionsUseCase(new StubFileSystemScanner());
		var filterService = new FilterOptionSelectionService();
		var ignoreService = new IgnoreOptionsService(localization);

		return new SelectionSyncCoordinator(
			viewModel,
			scanOptions,
			filterService,
			ignoreService,
			(rootPath, selectedOptions, _) => new IgnoreRules(
				IgnoreHiddenFolders: selectedOptions.Contains(IgnoreOptionId.HiddenFolders),
				IgnoreHiddenFiles: selectedOptions.Contains(IgnoreOptionId.HiddenFiles),
				IgnoreDotFolders: selectedOptions.Contains(IgnoreOptionId.DotFolders),
				IgnoreDotFiles: selectedOptions.Contains(IgnoreOptionId.DotFiles),
				SmartIgnoredFolders: new HashSet<string>(),
				SmartIgnoredFiles: new HashSet<string>())
			{
				IgnoreEmptyFolders = selectedOptions.Contains(IgnoreOptionId.EmptyFolders),
				IgnoreEmptyFiles = selectedOptions.Contains(IgnoreOptionId.EmptyFiles),
				IgnoreExtensionlessFiles = selectedOptions.Contains(IgnoreOptionId.ExtensionlessFiles)
			},
			(_, _) => new IgnoreOptionsAvailability(
				IncludeGitIgnore: false,
				IncludeSmartIgnore: false,
				IncludeHiddenFolders: true,
				IncludeHiddenFiles: true,
				IncludeDotFolders: true,
				IncludeDotFiles: true,
				IncludeEmptyFolders: true,
				IncludeEmptyFiles: true,
				IncludeExtensionlessFiles: true,
				ShowAdvancedCounts: showAdvancedCounts),
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
				["Settings.Ignore.HideSecrets"] = "Hide secrets",
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
}
