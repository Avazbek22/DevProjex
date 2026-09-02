namespace DevProjex.Tests.Unit;

public sealed class SelectionSyncCoordinatorIgnoreLifecycleMatrixTests
{
	[Theory]
	[MemberData(nameof(ExtensionlessPreservationCases))]
	public void ProfileLifecycle_ExtensionlessSelection_SurvivesTransientUnavailability(
		int caseId,
		bool includeGitIgnore,
		bool includeSmartIgnore,
		string[] scanEntries,
		bool useEmptyRoots)
	{
		_ = caseId;
		var availability = new IgnoreOptionsAvailability(includeGitIgnore, includeSmartIgnore);
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(
			viewModel,
			currentPathProvider: () => @"C:\ProjectA",
			availabilityProvider: () => availability);

		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: [IgnoreOptionId.ExtensionlessFiles]);

		coordinator.ApplyProjectProfileSelections(@"C:\ProjectA", profile);

		var roots = useEmptyRoots ? Array.Empty<string>() : new[] { "src" };

		// First pass before extension scan: extensionless option is unavailable.
		coordinator.PopulateIgnoreOptionsForRootSelection(roots, @"C:\ProjectA");
		Assert.DoesNotContain(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.ExtensionlessFiles);

		// Second pass after extension scan: option should reappear and keep saved state.
		coordinator.ApplyExtensionScan(scanEntries);
		coordinator.PopulateIgnoreOptionsForRootSelection(roots, @"C:\ProjectA");

		var hasExtensionlessEntries = scanEntries.Any(IsExtensionlessEntry);
		var extensionlessOption = viewModel.IgnoreOptions.FirstOrDefault(option => option.Id == IgnoreOptionId.ExtensionlessFiles);
		if (!hasExtensionlessEntries)
		{
			Assert.Null(extensionlessOption);
			return;
		}

		Assert.NotNull(extensionlessOption);
		Assert.True(extensionlessOption!.IsChecked);
	}

	[Theory]
	[MemberData(nameof(TransientIgnoreOptionPreservationCases))]
	public void ProfileLifecycle_TransientlyUnavailableIgnoreSelections_AreRestored(
		int caseId,
		IgnoreOptionId[] savedSelections,
		bool secondIncludeGitIgnore,
		bool secondIncludeSmartIgnore,
		bool includeExtensionlessOnSecondPass)
	{
		_ = caseId;
		var availability = new IgnoreOptionsAvailability(IncludeGitIgnore: false, IncludeSmartIgnore: false);
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(
			viewModel,
			currentPathProvider: () => @"C:\ProjectA",
			availabilityProvider: () => availability);

		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: savedSelections);

		coordinator.ApplyProjectProfileSelections(@"C:\ProjectA", profile);

		// First pass: git/smart unavailable, extensionless unavailable as scan is not applied yet.
		coordinator.PopulateIgnoreOptionsForRootSelection([], @"C:\ProjectA");

		availability = new IgnoreOptionsAvailability(
			IncludeGitIgnore: secondIncludeGitIgnore,
			IncludeSmartIgnore: secondIncludeSmartIgnore);

		coordinator.ApplyExtensionScan(includeExtensionlessOnSecondPass
			? new[] { ".cs", "Dockerfile" }
			: new[] { ".cs", ".json" });
		coordinator.PopulateIgnoreOptionsForRootSelection([], @"C:\ProjectA");

		foreach (var id in savedSelections.Distinct())
		{
			var option = viewModel.IgnoreOptions.FirstOrDefault(item => item.Id == id);
			switch (id)
			{
				case IgnoreOptionId.UseGitIgnore when secondIncludeGitIgnore:
					Assert.NotNull(option);
					Assert.True(option!.IsChecked);
					break;

				case IgnoreOptionId.SmartIgnore when secondIncludeSmartIgnore:
					Assert.NotNull(option);
					Assert.True(option!.IsChecked);
					break;

				case IgnoreOptionId.ExtensionlessFiles when includeExtensionlessOnSecondPass:
					Assert.NotNull(option);
					Assert.True(option!.IsChecked);
					break;

				default:
					Assert.Null(option);
					break;
			}
		}
	}

	public static IEnumerable<object[]> ExtensionlessPreservationCases()
	{
		var scanEntriesVariants = new[]
		{
			new[] { ".cs", ".json" },
			new[] { ".cs", "Dockerfile" },
			new[] { ".json", "Makefile", "LICENSE" },
			new[] { ".md", ".txt" },
			new[] { ".axaml", "Taskfile" },
			new[] { ".xml", ".yml", "Procfile" },
			new[] { ".props", ".targets" },
			new[] { ".config", "Jenkinsfile" },
			new[] { ".sln", "WORKSPACE" }
		};

		var caseId = 0;
		foreach (var includeGitIgnore in new[] { false, true })
		{
			foreach (var includeSmartIgnore in new[] { false, true })
			{
				foreach (var scanEntries in scanEntriesVariants)
				{
					foreach (var useEmptyRoots in new[] { false, true })
					{
						yield return
						[
							caseId++,
							includeGitIgnore,
							includeSmartIgnore,
							scanEntries,
							useEmptyRoots
						];
					}
				}
			}
		}
	}

	public static IEnumerable<object[]> TransientIgnoreOptionPreservationCases()
	{
		var savedVariants = new[]
		{
			new[] { IgnoreOptionId.UseGitIgnore },
			new[] { IgnoreOptionId.SmartIgnore },
			new[] { IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore },
			new[] { IgnoreOptionId.ExtensionlessFiles },
			new[] { IgnoreOptionId.UseGitIgnore, IgnoreOptionId.ExtensionlessFiles },
			new[] { IgnoreOptionId.SmartIgnore, IgnoreOptionId.ExtensionlessFiles }
		};

		var availabilityVariants = new[]
		{
			(IncludeGit: true, IncludeSmart: false),
			(IncludeGit: false, IncludeSmart: true),
			(IncludeGit: true, IncludeSmart: true)
		};

		var caseId = 0;
		foreach (var saved in savedVariants)
		{
			foreach (var availability in availabilityVariants)
			{
				foreach (var includeExtensionlessOnSecondPass in new[] { false, true })
				{
					yield return
					[
						caseId++,
						saved,
						availability.IncludeGit,
						availability.IncludeSmart,
						includeExtensionlessOnSecondPass
					];
				}
			}
		}
	}

	private static SelectionSyncCoordinator CreateCoordinator(
		MainWindowViewModel viewModel,
		Func<string?> currentPathProvider,
		Func<IgnoreOptionsAvailability> availabilityProvider)
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
			(_, _, _) => new IgnoreRules(
				IgnoreHiddenFolders: false,
				IgnoreHiddenFiles: false,
				IgnoreDotFolders: false,
				IgnoreDotFiles: false,
				SmartIgnoredFolders: new HashSet<string>(),
				SmartIgnoredFiles: new HashSet<string>()),
			(_, _) => availabilityProvider(),
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
