namespace DevProjex.Tests.Unit;

public sealed class SelectionRefreshEngineTests
{
	[Fact]
	public void ComputeFullRefreshSnapshot_UsesSecondPassRootRefreshForDynamicDotFolderAvailability()
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
					HasExtensionlessEntries: false,
					ExtensionlessEntriesCount: 0)),
			CancellationToken.None);

		Assert.DoesNotContain(snapshot.RootOptions!, option => string.Equals(option.Name, ".cache", StringComparison.Ordinal));
		Assert.Contains(snapshot.RootOptions!, option => string.Equals(option.Name, "src", StringComparison.Ordinal));
		Assert.DoesNotContain(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.DotFolders);
		Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.ExtensionlessFiles && option.IsChecked);
	}

	[Fact]
	public void ComputeFullRefreshSnapshot_DirectoryLevelDynamicChange_RebuildsRootFoldersExactlyOnce()
	{
		var scanner = new CountingDirectoryLevelScanner();
		var engine = CreateEngine(scanner);

		_ = engine.ComputeFullRefreshSnapshot(CreateDefaultsContext(), CancellationToken.None);

		Assert.Equal(2, scanner.RootFolderScanCount);
		Assert.True(scanner.IgnoreSnapshotCallCount >= 3);
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
	public void ComputeFullRefreshSnapshot_PropagatesCancellationFromDynamicFollowUpPass()
	{
		var scanner = new CancelOnSecondDynamicPassScanner();
		var engine = CreateEngine(scanner);

		var exception = Assert.Throws<OperationCanceledException>(() =>
			engine.ComputeFullRefreshSnapshot(CreateDefaultsContext(), CancellationToken.None));

		Assert.True(scanner.IgnoreSnapshotCallCount >= 2);
		Assert.NotNull(exception);
	}

	[Fact]
	public void ComputeFullRefreshSnapshot_ProfileFallback_PreservesUnavailableIgnoreSelectionWithoutLeakingVisibleOption()
	{
		var scanner = new ProfileFallbackVisibilityScanner();
		var engine = CreateEngine(scanner);

		var snapshot = engine.ComputeFullRefreshSnapshot(
			CreateProfileContext([IgnoreOptionId.DotFolders]),
			CancellationToken.None);

		Assert.DoesNotContain(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.DotFolders);
		Assert.True(snapshot.IgnoreOptionStateCache.TryGetValue(IgnoreOptionId.DotFolders, out var isChecked) && isChecked);
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
				HasExtensionlessEntries: false,
				ExtensionlessEntriesCount: 0));

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

	private sealed class DotFolderNoiseScanner
		: IFileSystemScanner, IFileSystemScannerIgnoreSectionSnapshotProvider
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
	}

	private sealed class CountingDirectoryLevelScanner
		: IFileSystemScanner, IFileSystemScannerIgnoreSectionSnapshotProvider
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
	}

	private sealed class CountingFileLevelScanner
		: IFileSystemScanner, IFileSystemScannerIgnoreSectionSnapshotProvider
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
	}

	private sealed class CancelOnSecondDynamicPassScanner
		: IFileSystemScanner, IFileSystemScannerIgnoreSectionSnapshotProvider
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
	}

	private sealed class ProfileFallbackVisibilityScanner
		: IFileSystemScanner, IFileSystemScannerIgnoreSectionSnapshotProvider
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
					IgnoreOptionCounts.Empty),
				false,
				false);
		}
	}
}
