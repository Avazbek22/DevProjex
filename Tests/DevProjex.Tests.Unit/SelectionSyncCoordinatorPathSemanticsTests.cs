namespace DevProjex.Tests.Unit;

public sealed class SelectionSyncCoordinatorPathSemanticsTests
{
	[Fact]
	public void ApplyProjectProfileSelections_RootSelectionCache_BehaviorMatchesPlatform()
	{
		const string projectPath = @"C:\Workspace\ProjectA";
		var coordinator = CreateCoordinator();
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: ["src", "Src"],
			SelectedExtensions: [],
			SelectedIgnoreOptions: []);

		coordinator.ApplyProjectProfileSelections(projectPath, profile);

		var cache = GetPrivateSession(coordinator).RootFolders.SelectedNames;
		var expectedCount = OperatingSystem.IsWindows() ? 1 : 2;
		Assert.Equal(expectedCount, cache.Count);
		Assert.Contains("src", cache);
		if (!OperatingSystem.IsWindows())
			Assert.Contains("Src", cache);
	}

	[Fact]
	public void BuildIgnoreRulesCacheKey_RootSelectionCaseVariants_FollowPathComparer()
	{
		var first = IgnoreRulesBuildCacheKeyBuilder.Build(
			@"C:\Workspace\ProjectA",
			Array.Empty<IgnoreOptionId>(),
			new[] { "src" });
		var second = IgnoreRulesBuildCacheKeyBuilder.Build(
			@"C:\Workspace\ProjectA",
			Array.Empty<IgnoreOptionId>(),
			new[] { "Src" });

		Assert.Equal(OperatingSystem.IsWindows(), string.Equals(first, second, StringComparison.Ordinal));
	}

	private static SelectionSyncCoordinator CreateCoordinator()
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		var viewModel = new MainWindowViewModel(localization, new HelpContentProvider());
		var scanner = new StubFileSystemScanner();
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
			(_, _) => new IgnoreOptionsAvailability(false, false),
			_ => false,
			() => @"C:\Workspace\ProjectA");
	}

	private static ProjectSelectionSessionState GetPrivateSession(SelectionSyncCoordinator coordinator)
	{
		var field = typeof(SelectionSyncCoordinator).GetField(
			"_session",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		return (ProjectSelectionSessionState)field!.GetValue(coordinator)!;
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
