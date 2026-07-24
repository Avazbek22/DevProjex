using DevProjex.Application.Models;

namespace DevProjex.Tests.Unit;

public sealed class SelectionRefreshEngineTests
{
	[Fact]
	public void ComputeFullRefreshSnapshot_DefaultDirectoryTogglesUseSinglePassRootProjection()
	{
		var scanner = new DotFolderNoiseScanner();
		var useCase = new ScanOptionsUseCase(scanner);
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		var engine = new SelectionRefreshEngine(
			useCase,
			new FilterOptionSelectionService(),
			new IgnoreOptionsService(localization),
			BuildIgnoreRules,
			GetIgnoreAvailability);

		var snapshot = engine.ComputeFullRefreshSnapshot(
			new SelectionRefreshContext(
				Path: @"C:\Workspace\Project",
				PreparedSelectionMode: PreparedSelectionMode.Defaults,
				AllRootFoldersChecked: true,
				AllExtensionsChecked: true,
				RootSelectionInitialized: false,
				RootSelectionCache: new HashSet<string>(PathComparer.Default),
				ExtensionsSelectionInitialized: false,
				ExtensionsSelectionCache: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				IgnoreSelectionInitialized: false,
				IgnoreSelectionCache: new HashSet<IgnoreOptionId>(),
				IgnoreOptionStateCache: new Dictionary<IgnoreOptionId, bool>(),
				IgnoreAllPreference: null,
				CurrentSnapshotState: new IgnoreSectionSnapshotState(
					HasIgnoreOptionCounts: false,
					IgnoreOptionCounts: IgnoreOptionCounts.Empty,
					ControllerImpactCounts: IgnoreControllerImpactCounts.Empty,
					HasExtensionlessEntries: false,
					ExtensionlessEntriesCount: 0)),
			CancellationToken.None);

		Assert.DoesNotContain(snapshot.RootOptions!, option => string.Equals(option.Name, ".cache", StringComparison.Ordinal));
		Assert.Contains(snapshot.RootOptions!, option => string.Equals(option.Name, "src", StringComparison.Ordinal));
		Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.DotFolders && option.IsChecked);
		Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.ExtensionlessFiles && option.IsChecked);
	}

	[Fact]
	public void ComputeFullRefreshSnapshot_DefaultDirectoryLevelDynamicChange_UsesSingleRootRefresh()
	{
		var scanner = new CountingDirectoryLevelScanner();
		var engine = CreateEngine(scanner);

		_ = engine.ComputeFullRefreshSnapshot(CreateDefaultsContext(), CancellationToken.None);

		Assert.Equal(1, scanner.RootFolderScanCount);
		Assert.Equal(2, scanner.IgnoreSnapshotCallCount);
	}

	[Fact]
	public void ComputeFullRefreshSnapshot_SelfHiddenRuntimeOptions_DoNotOscillateAcrossMaximumPasses()
	{
		var scanner = new SelfHiddenRuntimeOptionsScanner();
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		var engine = new SelectionRefreshEngine(
			new ScanOptionsUseCase(scanner),
			new FilterOptionSelectionService(),
			new IgnoreOptionsService(localization),
			BuildIgnoreRules,
			(_, _) => new IgnoreOptionsAvailability(
				IncludeGitIgnore: true,
				IncludeSmartIgnore: true,
				ShowAdvancedCounts: true));

		var snapshot = engine.ComputeFullRefreshSnapshot(CreateDefaultsContext(), CancellationToken.None);

		Assert.Equal(1, scanner.RootFolderScanCount);
		Assert.Equal(1, scanner.RootSelectionSnapshotCount);
		Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.SmartIgnore && option.IsChecked);
		Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.UseGitIgnore && option.IsChecked);
		Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.DotFolders && option.IsChecked);
		Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.EmptyFolders && option.IsChecked);
		Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.EmptyFiles && option.IsChecked);
		Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.ExtensionlessFiles && option.IsChecked);
	}

	[Fact]
	public void ComputeFullRefreshSnapshot_FileLevelDynamicChange_DoesNotRebuildRootFolders()
	{
		var scanner = new CountingFileLevelScanner();
		var engine = CreateEngine(scanner);

		_ = engine.ComputeFullRefreshSnapshot(CreateDefaultsContext(), CancellationToken.None);

		Assert.Equal(1, scanner.RootFolderScanCount);
		Assert.True(scanner.IgnoreSnapshotCallCount >= 2);
	}

	[Fact]
	public void ComputeFullRefreshSnapshot_NewCheckedExtensionUsesSinglePolicyAwareSnapshotPass()
	{
		var scanner = new NewExtensionPolicyScanner();
		var engine = CreateEngine(scanner);

		var snapshot = engine.ComputeFullRefreshSnapshot(
			CreateNewExtensionPolicyContext(),
			CancellationToken.None);

		Assert.Contains(snapshot.ExtensionOptions, option => option.Name == ".cs" && option.IsChecked);
		Assert.Contains(snapshot.ExtensionOptions, option => option.Name == ".md" && option.IsChecked);
		Assert.Equal(1, snapshot.IgnoreOptionCounts.EmptyFiles);
		Assert.Equal(2, scanner.PolicySnapshotCallCount);
	}

	[Fact]
	public void ComputeFullRefreshSnapshot_PropagatesCancellationFromDynamicFollowUpPass()
	{
		var scanner = new CancelOnSecondDynamicPassScanner();
		var engine = CreateEngine(scanner);

		var exception = Assert.Throws<OperationCanceledException>(() =>
			engine.ComputeFullRefreshSnapshot(CreateInitializedEmptyIgnoreContext(), CancellationToken.None));

		Assert.True(scanner.IgnoreSnapshotCallCount >= 2);
		Assert.NotNull(exception);
	}

	[Fact]
	public void ComputeFullRefreshSnapshot_ExpandingDependencyChain_DoesNotPublishPartialState()
	{
		var scanner = new ExpandingIgnoreImpactScanner();
		var engine = CreateEngine(scanner);

		var exception = Assert.Throws<SelectionRefreshConvergenceException>(() =>
			engine.ComputeFullRefreshSnapshot(CreateInitializedEmptyIgnoreContext(), CancellationToken.None));

		Assert.Equal(SelectionRefreshConvergenceFailure.PassLimitExceeded, exception.Failure);
		Assert.Equal(6, exception.CompletedPasses);
		Assert.Equal(6, scanner.DynamicPassCount);
	}

	[Fact]
	public void ComputeFullRefreshSnapshot_ProfileFallback_PreservesDirectoryToggleWhenItAffectsRootList()
	{
		var scanner = new ProfileFallbackVisibilityScanner();
		var engine = CreateEngine(scanner);

		var snapshot = engine.ComputeFullRefreshSnapshot(
			CreateProfileContext([IgnoreOptionId.DotFolders]),
			CancellationToken.None);

		Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.DotFolders && option.IsChecked);
		Assert.True(snapshot.IgnoreOptionStateCache.TryGetValue(IgnoreOptionId.DotFolders, out var isChecked) && isChecked);
	}

	[Fact]
	public void ComputeFullRefreshSnapshot_ProfileFallback_DoesNotDefaultCheckUnselectedControllers()
	{
		var scanner = new ProfileFallbackVisibilityScanner(new IgnoreControllerImpactCounts(
			GitIgnore: 1,
			SmartIgnore: 1));
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		var engine = new SelectionRefreshEngine(
			new ScanOptionsUseCase(scanner),
			new FilterOptionSelectionService(),
			new IgnoreOptionsService(localization),
			BuildIgnoreRules,
			(_, _) => new IgnoreOptionsAvailability(
				IncludeGitIgnore: true,
				IncludeSmartIgnore: true,
				ShowAdvancedCounts: true));

		var snapshot = engine.ComputeFullRefreshSnapshot(
			CreateProfileContext([IgnoreOptionId.DotFolders]),
			CancellationToken.None);

		Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.DotFolders && option.IsChecked);
		Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.UseGitIgnore && !option.IsChecked);
		Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.SmartIgnore && !option.IsChecked);
		Assert.True(snapshot.IgnoreOptionStateCache.TryGetValue(IgnoreOptionId.DotFolders, out var isChecked) && isChecked);
	}

	[Fact]
	public void ComputeFullRefreshSnapshot_ControllerMetadataWithoutImpact_HidesControllers()
	{
		var scanner = new ProfileFallbackVisibilityScanner();
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		var engine = new SelectionRefreshEngine(
			new ScanOptionsUseCase(scanner),
			new FilterOptionSelectionService(),
			new IgnoreOptionsService(localization),
			BuildIgnoreRules,
			(_, _) => new IgnoreOptionsAvailability(
				IncludeGitIgnore: true,
				IncludeSmartIgnore: true,
				ShowAdvancedCounts: true));

		var snapshot = engine.ComputeFullRefreshSnapshot(
			CreateDefaultsContext(),
			CancellationToken.None);

		Assert.DoesNotContain(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.UseGitIgnore);
		Assert.DoesNotContain(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.SmartIgnore);
	}

	[Fact]
	public void ComputeFullRefreshSnapshot_ExplicitUncheckedControllersStayVisibleWhenImpactDropsToZero()
	{
		var scanner = new ProfileFallbackVisibilityScanner();
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		var engine = new SelectionRefreshEngine(
			new ScanOptionsUseCase(scanner),
			new FilterOptionSelectionService(),
			new IgnoreOptionsService(localization),
			BuildIgnoreRules,
			(_, _) => new IgnoreOptionsAvailability(
				IncludeGitIgnore: true,
				IncludeSmartIgnore: true,
				ShowAdvancedCounts: true));
		var context = CreateDefaultsContext() with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = new HashSet<IgnoreOptionId>(),
			IgnoreOptionStateCache = new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.UseGitIgnore] = false,
				[IgnoreOptionId.SmartIgnore] = false
			},
			IgnoreOptionStateCacheIsComplete = true,
			CurrentSnapshotState = new IgnoreSectionSnapshotState(
				HasIgnoreOptionCounts: true,
				IgnoreOptionCounts: IgnoreOptionCounts.Empty,
				ControllerImpactCounts: new IgnoreControllerImpactCounts(GitIgnore: 1, SmartIgnore: 1),
				HasExtensionlessEntries: false,
				ExtensionlessEntriesCount: 0)
		};

		var snapshot = engine.ComputeFullRefreshSnapshot(context, CancellationToken.None);

		Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.UseGitIgnore && !option.IsChecked);
		Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.SmartIgnore && !option.IsChecked);
		Assert.True(snapshot.IgnoreOptionStateCache.TryGetValue(IgnoreOptionId.UseGitIgnore, out var gitState));
		Assert.False(gitState);
		Assert.True(snapshot.IgnoreOptionStateCache.TryGetValue(IgnoreOptionId.SmartIgnore, out var smartState));
		Assert.False(smartState);
	}

	[Fact]
	public void ComputeFullRefreshSnapshot_CheckedControllersWithNoImpactStayHidden()
	{
		var scanner = new ProfileFallbackVisibilityScanner();
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		var engine = new SelectionRefreshEngine(
			new ScanOptionsUseCase(scanner),
			new FilterOptionSelectionService(),
			new IgnoreOptionsService(localization),
			BuildIgnoreRules,
			(_, _) => new IgnoreOptionsAvailability(
				IncludeGitIgnore: true,
				IncludeSmartIgnore: true,
				ShowAdvancedCounts: true));
		var context = CreateDefaultsContext() with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = new HashSet<IgnoreOptionId>
			{
				IgnoreOptionId.UseGitIgnore,
				IgnoreOptionId.SmartIgnore
			},
			IgnoreOptionStateCache = new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.UseGitIgnore] = true,
				[IgnoreOptionId.SmartIgnore] = true
			},
			IgnoreOptionStateCacheIsComplete = true
		};

		var snapshot = engine.ComputeFullRefreshSnapshot(context, CancellationToken.None);

		Assert.DoesNotContain(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.UseGitIgnore);
		Assert.DoesNotContain(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.SmartIgnore);
		Assert.True(snapshot.IgnoreOptionStateCache.TryGetValue(IgnoreOptionId.UseGitIgnore, out var gitState));
		Assert.True(gitState);
		Assert.True(snapshot.IgnoreOptionStateCache.TryGetValue(IgnoreOptionId.SmartIgnore, out var smartState));
		Assert.True(smartState);
	}

	[Fact]
	public void ComputeLiveRefreshSnapshot_ReusesIgnoreRulesForIdenticalInputs()
	{
		var scanner = new StableSnapshotScanner();
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		var buildCount = 0;
		var engine = new SelectionRefreshEngine(
			new ScanOptionsUseCase(scanner),
			new FilterOptionSelectionService(),
			new IgnoreOptionsService(localization),
			(path, selectedIgnoreOptions, selectedRootFolders) =>
			{
				buildCount++;
				return BuildIgnoreRules(path, selectedIgnoreOptions, selectedRootFolders);
			},
			GetIgnoreAvailability);
		var context = CreateDefaultsContext();

		_ = engine.ComputeLiveRefreshSnapshot(context, ["src"], CancellationToken.None);
		_ = engine.ComputeLiveRefreshSnapshot(context, ["src"], CancellationToken.None);

		Assert.Equal(1, buildCount);
	}

	[Fact]
	public void ExtensionSnapshotReusePolicy_NullPolicyMatchesAllCheckedFallbackOptions()
	{
		var options = new[]
		{
			new SelectionOption(".cs", true),
			new SelectionOption(".md", true)
		};

		Assert.True(ExtensionSnapshotReusePolicy.CanReuseSnapshot(null, options));
	}

	[Fact]
	public void ExtensionSnapshotReusePolicy_NullPolicyDoesNotMatchUncheckedOptions()
	{
		var options = new[]
		{
			new SelectionOption(".cs", true),
			new SelectionOption(".md", false)
		};

		Assert.False(ExtensionSnapshotReusePolicy.CanReuseSnapshot(null, options));
	}

	private static SelectionRefreshEngine CreateEngine(
		IFileSystemScanner scanner)
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		return new SelectionRefreshEngine(
			new ScanOptionsUseCase(scanner),
			new FilterOptionSelectionService(),
			new IgnoreOptionsService(localization),
			BuildIgnoreRules,
			GetIgnoreAvailability);
	}

	private static SelectionRefreshContext CreateDefaultsContext() =>
		new(
			Path: @"C:\Workspace\Project",
			PreparedSelectionMode: PreparedSelectionMode.Defaults,
			AllRootFoldersChecked: true,
			AllExtensionsChecked: true,
			RootSelectionInitialized: false,
			RootSelectionCache: new HashSet<string>(PathComparer.Default),
			ExtensionsSelectionInitialized: false,
			ExtensionsSelectionCache: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			IgnoreSelectionInitialized: false,
			IgnoreSelectionCache: new HashSet<IgnoreOptionId>(),
			IgnoreOptionStateCache: new Dictionary<IgnoreOptionId, bool>(),
			IgnoreAllPreference: null,
			CurrentSnapshotState: new IgnoreSectionSnapshotState(
				HasIgnoreOptionCounts: false,
				IgnoreOptionCounts: IgnoreOptionCounts.Empty,
				ControllerImpactCounts: IgnoreControllerImpactCounts.Empty,
				HasExtensionlessEntries: false,
				ExtensionlessEntriesCount: 0));

	private static SelectionRefreshContext CreateInitializedEmptyIgnoreContext() =>
		CreateDefaultsContext() with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = new HashSet<IgnoreOptionId>(),
			IgnoreOptionStateCache = new Dictionary<IgnoreOptionId, bool>()
		};

	private static SelectionRefreshContext CreateNewExtensionPolicyContext() =>
		CreateDefaultsContext() with
		{
			AllExtensionsChecked = false,
			ExtensionsSelectionInitialized = true,
			ExtensionsSelectionCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
			ExtensionOptionStateCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
			{
				[".cs"] = true
			},
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = new HashSet<IgnoreOptionId> { IgnoreOptionId.EmptyFiles },
			IgnoreOptionStateCache = new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.EmptyFiles] = true
			},
			CurrentSnapshotState = new IgnoreSectionSnapshotState(
				HasIgnoreOptionCounts: true,
				IgnoreOptionCounts: new IgnoreOptionCounts(EmptyFiles: 1),
				ControllerImpactCounts: IgnoreControllerImpactCounts.Empty,
				HasExtensionlessEntries: false,
				ExtensionlessEntriesCount: 0)
		};

	private static SelectionRefreshContext CreateProfileContext(
		IReadOnlyCollection<IgnoreOptionId> selectedIgnoreOptions) =>
		new(
			Path: @"C:\Workspace\Project",
			PreparedSelectionMode: PreparedSelectionMode.Profile,
			AllRootFoldersChecked: true,
			AllExtensionsChecked: true,
			RootSelectionInitialized: false,
			RootSelectionCache: new HashSet<string>(PathComparer.Default),
			ExtensionsSelectionInitialized: false,
			ExtensionsSelectionCache: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			IgnoreSelectionInitialized: true,
			IgnoreSelectionCache: new HashSet<IgnoreOptionId>(selectedIgnoreOptions),
			IgnoreOptionStateCache: new Dictionary<IgnoreOptionId, bool>(),
			IgnoreAllPreference: null,
			CurrentSnapshotState: new IgnoreSectionSnapshotState(
				HasIgnoreOptionCounts: false,
				IgnoreOptionCounts: IgnoreOptionCounts.Empty,
				ControllerImpactCounts: IgnoreControllerImpactCounts.Empty,
				HasExtensionlessEntries: false,
				ExtensionlessEntriesCount: 0));

	private static IgnoreRules BuildIgnoreRules(
		string _,
		IReadOnlyCollection<IgnoreOptionId> selectedIgnoreOptions,
		IReadOnlyCollection<string>? __)
	{
		var selected = new HashSet<IgnoreOptionId>(selectedIgnoreOptions);
		return new IgnoreRules(
			IgnoreHiddenFolders: selected.Contains(IgnoreOptionId.HiddenFolders),
			IgnoreHiddenFiles: selected.Contains(IgnoreOptionId.HiddenFiles),
			IgnoreDotFolders: selected.Contains(IgnoreOptionId.DotFolders),
			IgnoreDotFiles: selected.Contains(IgnoreOptionId.DotFiles),
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>())
		{
			IgnoreEmptyFolders = selected.Contains(IgnoreOptionId.EmptyFolders),
			IgnoreEmptyFiles = selected.Contains(IgnoreOptionId.EmptyFiles),
			IgnoreExtensionlessFiles = selected.Contains(IgnoreOptionId.ExtensionlessFiles),
			UseGitIgnore = selected.Contains(IgnoreOptionId.UseGitIgnore),
			UseSmartIgnore = selected.Contains(IgnoreOptionId.SmartIgnore)
		};
	}

	private static IgnoreOptionsAvailability GetIgnoreAvailability(
		string _,
		IReadOnlyCollection<string> __) =>
		new(
			IncludeGitIgnore: false,
			IncludeSmartIgnore: false,
			ShowAdvancedCounts: true);

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
				["Settings.Ignore.DotFolders"] = "Dot folders",
				["Settings.Ignore.DotFiles"] = "Dot files",
				["Settings.Ignore.EmptyFolders"] = "Empty folders",
				["Settings.Ignore.EmptyFiles"] = "Empty files",
				["Settings.Ignore.ExtensionlessFiles"] = "Files without extension"
			}
		};

		return new StubLocalizationCatalog(data);
	}

	private static ScanResult<IgnoreSectionScanData> AggregateRootSelectionSnapshot(
		string rootPath,
		IReadOnlyCollection<string> selectedRootFolders,
		ScanResult<IgnoreSectionScanData> rootFileSnapshot,
		Func<string, ScanResult<IgnoreSectionScanData>> getFolderSnapshot,
		IgnoreOptionCounts extraEffectiveCounts = default)
	{
		var extensions = new HashSet<string>(rootFileSnapshot.Value.Extensions, StringComparer.OrdinalIgnoreCase);
		var rawCounts = rootFileSnapshot.Value.RawIgnoreOptionCounts;
		var effectiveCounts = rootFileSnapshot.Value.EffectiveIgnoreOptionCounts.Add(extraEffectiveCounts);
		var controllerImpactCounts = rootFileSnapshot.Value.ControllerImpactCounts;
		var rootAccessDenied = rootFileSnapshot.RootAccessDenied;
		var hadAccessDenied = rootFileSnapshot.HadAccessDenied;

		foreach (var selectedRootFolder in selectedRootFolders)
		{
			var snapshot = getFolderSnapshot(Path.Combine(rootPath, selectedRootFolder));
			extensions.UnionWith(snapshot.Value.Extensions);
			rawCounts = rawCounts.Add(snapshot.Value.RawIgnoreOptionCounts);
			effectiveCounts = effectiveCounts.Add(snapshot.Value.EffectiveIgnoreOptionCounts);
			controllerImpactCounts = controllerImpactCounts.Add(snapshot.Value.ControllerImpactCounts);
			rootAccessDenied |= snapshot.RootAccessDenied;
			hadAccessDenied |= snapshot.HadAccessDenied;
		}

		return new ScanResult<IgnoreSectionScanData>(
			new IgnoreSectionScanData(extensions, rawCounts, effectiveCounts, controllerImpactCounts),
			rootAccessDenied,
			hadAccessDenied);
	}

	private static bool ContainsRootFolderName(
		IEnumerable<string> rootFolders,
		string name)
	{
		foreach (var rootFolder in rootFolders)
		{
			if (PathComparer.Default.Equals(rootFolder, name))
				return true;
		}

		return false;
	}

	private sealed class DotFolderNoiseScanner
		: IFileSystemScanner, IFileSystemScannerIgnoreSectionSnapshotProvider, IFileSystemScannerExtensionPolicySnapshotProvider, IFileSystemScannerRootSelectionSnapshotProvider
	{
		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);

		public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);

		public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
		{
			var names = rules.IgnoreDotFolders
				? new List<string> { "src" }
				: new List<string> { ".cache", "src" };
			return new ScanResult<List<string>>(names, false, false);
		}

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IReadOnlySet<string>? effectiveAllowedExtensions,
			CancellationToken cancellationToken = default)
		{
			var name = Path.GetFileName(rootPath);
			return name switch
			{
				".cache" => new ScanResult<IgnoreSectionScanData>(
					new IgnoreSectionScanData(
						new HashSet<string>(StringComparer.OrdinalIgnoreCase),
						IgnoreOptionCounts.Empty,
						new IgnoreOptionCounts(DotFolders: 1)),
					false,
					false),
				"src" => new ScanResult<IgnoreSectionScanData>(
					new IgnoreSectionScanData(
						new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
						IgnoreOptionCounts.Empty,
						IgnoreOptionCounts.Empty),
					false,
					false),
				_ => new ScanResult<IgnoreSectionScanData>(
					new IgnoreSectionScanData(
						new HashSet<string>(StringComparer.OrdinalIgnoreCase),
						IgnoreOptionCounts.Empty,
						IgnoreOptionCounts.Empty),
					false,
					false)
			};
		}

		public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IReadOnlySet<string>? effectiveAllowedExtensions,
			CancellationToken cancellationToken = default)
		{
			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "README" },
					IgnoreOptionCounts.Empty,
					new IgnoreOptionCounts(ExtensionlessFiles: 1)),
				false,
				false);
		}

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			CancellationToken cancellationToken = default) =>
			GetIgnoreSectionSnapshot(rootPath, extensionDiscoveryRules, effectiveRules, (IReadOnlySet<string>?)null, cancellationToken);

		public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			CancellationToken cancellationToken = default) =>
			GetRootFileIgnoreSectionSnapshot(rootPath, extensionDiscoveryRules, effectiveRules, (IReadOnlySet<string>?)null, cancellationToken);

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshotForRootSelection(
			string rootPath,
			IReadOnlyCollection<string> selectedRootFolders,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			bool includeDirectoryToggleProbeRoots = false,
			CancellationToken cancellationToken = default,
			bool includeControllerImpactProbeRoots = false)
		{
			var extraEffectiveCounts =
				includeDirectoryToggleProbeRoots &&
				effectiveRules.IgnoreDotFolders &&
				!ContainsRootFolderName(selectedRootFolders, ".cache")
					? new IgnoreOptionCounts(DotFolders: 1)
					: IgnoreOptionCounts.Empty;

			return AggregateRootSelectionSnapshot(
				rootPath,
				selectedRootFolders,
				GetRootFileIgnoreSectionSnapshot(rootPath, extensionDiscoveryRules, effectiveRules, effectiveExtensionPolicy, cancellationToken),
				folderPath => GetIgnoreSectionSnapshot(folderPath, extensionDiscoveryRules, effectiveRules, effectiveExtensionPolicy, cancellationToken),
				extraEffectiveCounts);
		}
	}

	private sealed class CountingDirectoryLevelScanner
		: IFileSystemScanner, IFileSystemScannerIgnoreSectionSnapshotProvider, IFileSystemScannerExtensionPolicySnapshotProvider, IFileSystemScannerRootSelectionSnapshotProvider
	{
		public int RootFolderScanCount { get; private set; }
		public int IgnoreSnapshotCallCount { get; private set; }

		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);

		public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);

		public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
		{
			RootFolderScanCount++;
			var names = rules.IgnoreDotFolders
				? new List<string> { "src" }
				: new List<string> { ".cache", "src" };
			return new ScanResult<List<string>>(names, false, false);
		}

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IReadOnlySet<string>? effectiveAllowedExtensions,
			CancellationToken cancellationToken = default)
		{
			IgnoreSnapshotCallCount++;
			var name = Path.GetFileName(rootPath);
			return name switch
			{
				".cache" when !effectiveRules.IgnoreDotFolders => new ScanResult<IgnoreSectionScanData>(
					new IgnoreSectionScanData(
						new HashSet<string>(StringComparer.OrdinalIgnoreCase),
						IgnoreOptionCounts.Empty,
						new IgnoreOptionCounts(DotFolders: 1)),
					false,
					false),
				_ => new ScanResult<IgnoreSectionScanData>(
					new IgnoreSectionScanData(
						new HashSet<string>(StringComparer.OrdinalIgnoreCase),
						IgnoreOptionCounts.Empty,
						IgnoreOptionCounts.Empty),
					false,
					false)
			};
		}

		public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IReadOnlySet<string>? effectiveAllowedExtensions,
			CancellationToken cancellationToken = default)
		{
			IgnoreSnapshotCallCount++;
			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "README" },
					IgnoreOptionCounts.Empty,
					new IgnoreOptionCounts(ExtensionlessFiles: 1)),
				false,
				false);
		}

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			CancellationToken cancellationToken = default) =>
			GetIgnoreSectionSnapshot(rootPath, extensionDiscoveryRules, effectiveRules, (IReadOnlySet<string>?)null, cancellationToken);

		public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			CancellationToken cancellationToken = default) =>
			GetRootFileIgnoreSectionSnapshot(rootPath, extensionDiscoveryRules, effectiveRules, (IReadOnlySet<string>?)null, cancellationToken);

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshotForRootSelection(
			string rootPath,
			IReadOnlyCollection<string> selectedRootFolders,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			bool includeDirectoryToggleProbeRoots = false,
			CancellationToken cancellationToken = default,
			bool includeControllerImpactProbeRoots = false)
		{
			var extraEffectiveCounts =
				includeDirectoryToggleProbeRoots &&
				effectiveRules.IgnoreDotFolders &&
				!ContainsRootFolderName(selectedRootFolders, ".cache")
					? new IgnoreOptionCounts(DotFolders: 1)
					: IgnoreOptionCounts.Empty;

			return AggregateRootSelectionSnapshot(
				rootPath,
				selectedRootFolders,
				GetRootFileIgnoreSectionSnapshot(rootPath, extensionDiscoveryRules, effectiveRules, effectiveExtensionPolicy, cancellationToken),
				folderPath => GetIgnoreSectionSnapshot(folderPath, extensionDiscoveryRules, effectiveRules, effectiveExtensionPolicy, cancellationToken),
				extraEffectiveCounts);
		}
	}

	private sealed class CountingFileLevelScanner
		: IFileSystemScanner, IFileSystemScannerIgnoreSectionSnapshotProvider, IFileSystemScannerExtensionPolicySnapshotProvider
	{
		public int RootFolderScanCount { get; private set; }
		public int IgnoreSnapshotCallCount { get; private set; }

		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);

		public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);

		public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
		{
			RootFolderScanCount++;
			return new ScanResult<List<string>>(new List<string> { "src" }, false, false);
		}

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IReadOnlySet<string>? effectiveAllowedExtensions,
			CancellationToken cancellationToken = default)
		{
			IgnoreSnapshotCallCount++;
			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase),
					IgnoreOptionCounts.Empty,
					IgnoreOptionCounts.Empty),
				false,
				false);
		}

		public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IReadOnlySet<string>? effectiveAllowedExtensions,
			CancellationToken cancellationToken = default)
		{
			IgnoreSnapshotCallCount++;
			var effectiveCounts = effectiveRules.IgnoreExtensionlessFiles
				? IgnoreOptionCounts.Empty
				: new IgnoreOptionCounts(ExtensionlessFiles: 1);
			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "README" },
					IgnoreOptionCounts.Empty,
					effectiveCounts),
				false,
				false);
		}

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			CancellationToken cancellationToken = default) =>
			GetIgnoreSectionSnapshot(rootPath, extensionDiscoveryRules, effectiveRules, (IReadOnlySet<string>?)null, cancellationToken);

		public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			CancellationToken cancellationToken = default) =>
			GetRootFileIgnoreSectionSnapshot(rootPath, extensionDiscoveryRules, effectiveRules, (IReadOnlySet<string>?)null, cancellationToken);
	}

	private sealed class SelfHiddenRuntimeOptionsScanner
		: IFileSystemScanner, IFileSystemScannerExtensionPolicySnapshotProvider, IFileSystemScannerRootSelectionSnapshotProvider
	{
		public int RootFolderScanCount { get; private set; }
		public int RootSelectionSnapshotCount { get; private set; }

		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);

		public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);

		public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
		{
			RootFolderScanCount++;
			var hasRootSuppressingRule =
				rules.UseGitIgnore ||
				rules.UseSmartIgnore ||
				rules.IgnoreDotFolders ||
				rules.IgnoreEmptyFolders;
			var names = hasRootSuppressingRule
				? new List<string> { "src" }
				: new List<string> { ".idea", "bin", "empty", "src" };
			return new ScanResult<List<string>>(names, false, false);
		}

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			CancellationToken cancellationToken = default) =>
			CreateSnapshot(effectiveRules);

		public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			CancellationToken cancellationToken = default) =>
			CreateSnapshot(effectiveRules);

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshotForRootSelection(
			string rootPath,
			IReadOnlyCollection<string> selectedRootFolders,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			bool includeDirectoryToggleProbeRoots = false,
			CancellationToken cancellationToken = default,
			bool includeControllerImpactProbeRoots = false)
		{
			RootSelectionSnapshotCount++;
			return CreateSnapshot(effectiveRules);
		}

		private static ScanResult<IgnoreSectionScanData> CreateSnapshot(IgnoreRules effectiveRules)
		{
			var extensions = effectiveRules.IgnoreExtensionlessFiles
				? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" }
				: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", "README" };
			// The scanner snapshot reports option impact for the current scope, not just
			// currently visible leftovers. This keeps active self-hidden toggles stable
			// without letting the refresh engine reuse stale counts from an old scope.
			var counts = new IgnoreOptionCounts(
				DotFolders: 1,
				EmptyFolders: 1,
				ExtensionlessFiles: 1,
				EmptyFiles: 1);
			var controllerImpactCounts = new IgnoreControllerImpactCounts(
				GitIgnore: 1,
				SmartIgnore: 1);

			// This deliberately models options that hide their own evidence in the tree.
			// The fresh snapshot still owns the impact truth; the engine only preserves
			// the user's checked/unchecked preference separately in the option state cache.
			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					extensions,
					IgnoreOptionCounts.Empty,
					counts,
					controllerImpactCounts),
				false,
				false);
		}
	}

	private sealed class NewExtensionPolicyScanner
		: IFileSystemScanner, IFileSystemScannerExtensionPolicySnapshotProvider
	{
		public int PolicySnapshotCallCount { get; private set; }

		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);

		public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);

		public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new(new List<string> { "src" }, false, false);

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			CancellationToken cancellationToken = default)
		{
			PolicySnapshotCallCount++;
			var counts = effectiveExtensionPolicy?.AllowsExtension(".md") == true
				? new IgnoreOptionCounts(EmptyFiles: 1)
				: IgnoreOptionCounts.Empty;
			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".md" },
					IgnoreOptionCounts.Empty,
					counts),
				false,
				false);
		}

		public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			CancellationToken cancellationToken = default)
		{
			PolicySnapshotCallCount++;
			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase),
					IgnoreOptionCounts.Empty,
					IgnoreOptionCounts.Empty),
				false,
				false);
		}

	}

	private sealed class CancelOnSecondDynamicPassScanner
		: IFileSystemScanner, IFileSystemScannerIgnoreSectionSnapshotProvider, IFileSystemScannerExtensionPolicySnapshotProvider
	{
		public int IgnoreSnapshotCallCount { get; private set; }

		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);

		public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);

		public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new(new List<string> { "src" }, false, false);

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IReadOnlySet<string>? effectiveAllowedExtensions,
			CancellationToken cancellationToken = default)
		{
			IgnoreSnapshotCallCount++;
			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase),
					IgnoreOptionCounts.Empty,
					IgnoreOptionCounts.Empty),
				false,
				false);
		}

		public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IReadOnlySet<string>? effectiveAllowedExtensions,
			CancellationToken cancellationToken = default)
		{
			IgnoreSnapshotCallCount++;
			if (IgnoreSnapshotCallCount >= 2)
				throw new OperationCanceledException(cancellationToken);

			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "README" },
					IgnoreOptionCounts.Empty,
					new IgnoreOptionCounts(ExtensionlessFiles: 1)),
				false,
				false);
		}

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			CancellationToken cancellationToken = default) =>
			GetIgnoreSectionSnapshot(rootPath, extensionDiscoveryRules, effectiveRules, (IReadOnlySet<string>?)null, cancellationToken);

		public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			CancellationToken cancellationToken = default) =>
			GetRootFileIgnoreSectionSnapshot(rootPath, extensionDiscoveryRules, effectiveRules, (IReadOnlySet<string>?)null, cancellationToken);
	}

	private sealed class ProfileFallbackVisibilityScanner(IgnoreControllerImpactCounts controllerImpactCounts = default)
		: IFileSystemScanner, IFileSystemScannerIgnoreSectionSnapshotProvider,
			IFileSystemScannerExtensionPolicySnapshotProvider, IFileSystemScannerRootSelectionSnapshotProvider
	{
		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);

		public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);

		public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
		{
			var names = rules.IgnoreDotFolders
				? new List<string> { "docs" }
				: new List<string> { ".cache", "docs" };
			return new ScanResult<List<string>>(names, false, false);
		}

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IReadOnlySet<string>? effectiveAllowedExtensions,
			CancellationToken cancellationToken = default)
		{
			var name = Path.GetFileName(rootPath);
			return name switch
			{
				".cache" when !effectiveRules.IgnoreDotFolders => new ScanResult<IgnoreSectionScanData>(
					new IgnoreSectionScanData(
						new HashSet<string>(StringComparer.OrdinalIgnoreCase),
						IgnoreOptionCounts.Empty,
						new IgnoreOptionCounts(DotFolders: 1)),
					false,
					false),
				"docs" when !effectiveRules.IgnoreExtensionlessFiles => new ScanResult<IgnoreSectionScanData>(
					new IgnoreSectionScanData(
						new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "README" },
						IgnoreOptionCounts.Empty,
						new IgnoreOptionCounts(ExtensionlessFiles: 1)),
					false,
					false),
				_ => new ScanResult<IgnoreSectionScanData>(
					new IgnoreSectionScanData(
						new HashSet<string>(StringComparer.OrdinalIgnoreCase),
						IgnoreOptionCounts.Empty,
						IgnoreOptionCounts.Empty),
					false,
					false)
			};
		}

		public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IReadOnlySet<string>? effectiveAllowedExtensions,
			CancellationToken cancellationToken = default)
		{
			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase),
					IgnoreOptionCounts.Empty,
					IgnoreOptionCounts.Empty,
					controllerImpactCounts),
				false,
				false);
		}

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			CancellationToken cancellationToken = default) =>
			GetIgnoreSectionSnapshot(rootPath, extensionDiscoveryRules, effectiveRules, (IReadOnlySet<string>?)null, cancellationToken);

		public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			CancellationToken cancellationToken = default) =>
			GetRootFileIgnoreSectionSnapshot(rootPath, extensionDiscoveryRules, effectiveRules, (IReadOnlySet<string>?)null, cancellationToken);

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshotForRootSelection(
			string rootPath,
			IReadOnlyCollection<string> selectedRootFolders,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			bool includeDirectoryToggleProbeRoots = false,
			CancellationToken cancellationToken = default,
			bool includeControllerImpactProbeRoots = false)
		{
			var extraEffectiveCounts =
				includeDirectoryToggleProbeRoots &&
				effectiveRules.IgnoreDotFolders &&
				!ContainsRootFolderName(selectedRootFolders, ".cache")
					? new IgnoreOptionCounts(DotFolders: 1)
					: IgnoreOptionCounts.Empty;

			return AggregateRootSelectionSnapshot(
				rootPath,
				selectedRootFolders,
				GetRootFileIgnoreSectionSnapshot(rootPath, extensionDiscoveryRules, effectiveRules, effectiveExtensionPolicy, cancellationToken),
				folderPath => GetIgnoreSectionSnapshot(folderPath, extensionDiscoveryRules, effectiveRules, effectiveExtensionPolicy, cancellationToken),
				extraEffectiveCounts);
		}
	}

	private sealed class StableSnapshotScanner
		: IFileSystemScanner, IFileSystemScannerExtensionPolicySnapshotProvider
	{
		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);

		public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);

		public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new(new List<string> { "src" }, false, false);

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			CancellationToken cancellationToken = default) =>
			CreateEmptySnapshot();

		public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			CancellationToken cancellationToken = default) =>
			CreateEmptySnapshot();

		private static ScanResult<IgnoreSectionScanData> CreateEmptySnapshot() =>
			new(
				new IgnoreSectionScanData(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase),
					IgnoreOptionCounts.Empty,
					IgnoreOptionCounts.Empty),
				false,
				false);
	}

	private sealed class ExpandingIgnoreImpactScanner
		: IFileSystemScanner, IFileSystemScannerExtensionPolicySnapshotProvider
	{
		public int DynamicPassCount { get; private set; }

		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) =>
			new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);

		public ScanResult<HashSet<string>> GetRootFileExtensions(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) =>
			new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);

		public ScanResult<List<string>> GetRootFolderNames(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) =>
			new(new List<string> { "src" }, false, false);

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			CancellationToken cancellationToken = default)
		{
			DynamicPassCount++;
			var step = DynamicPassCount;
			var counts = new IgnoreOptionCounts(
				HiddenFolders: step >= 5 ? 1 : 0,
				HiddenFiles: step >= 1 ? 1 : 0,
				DotFolders: step >= 6 ? 1 : 0,
				DotFiles: step >= 2 ? 1 : 0,
				EmptyFolders: step >= 7 ? 1 : 0,
				EmptyFiles: step >= 3 ? 1 : 0,
				ExtensionlessFiles: step >= 4 ? 1 : 0);
			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase),
					IgnoreOptionCounts.Empty,
					counts),
				false,
				false);
		}

		public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			CancellationToken cancellationToken = default) =>
			new(
				new IgnoreSectionScanData(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase),
					IgnoreOptionCounts.Empty,
					IgnoreOptionCounts.Empty),
				false,
				false);
	}

}
