namespace DevProjex.Tests.Unit;

public sealed class ScanOptionsUseCaseTests
{
	// Verifies scan results are sorted and access-denied flags are combined.
	[Fact]
	public void Execute_SortsResultsAndCombinesAccessFlags()
	{
		var scanner = new StubFileSystemScanner
		{
			GetExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".b", ".a" },
				RootAccessDenied: true,
				HadAccessDenied: false),
			GetRootFolderNamesHandler = (_, _) => new ScanResult<List<string>>(
				new List<string> { "z", "a" },
				RootAccessDenied: false,
				HadAccessDenied: true)
		};

		var useCase = new ScanOptionsUseCase(scanner);
		var result = useCase.Execute(new ScanOptionsRequest("/root", new IgnoreRules(IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>())));

		Assert.Equal(new[] { ".a", ".b" }, result.Extensions);
		Assert.Equal(new[] { "a", "z" }, result.RootFolders);
		Assert.True(result.RootAccessDenied);
		Assert.True(result.HadAccessDenied);
	}

	// Verifies root file extensions are scanned even when no subfolders exist.
	// This is the fix for Problem 1: folders with only root-level files.
	[Fact]
	public void GetExtensionsForRootFolders_ScansRootFilesEvenWhenNoSubfolders()
	{
		var scanner = new StubFileSystemScanner
		{
			GetRootFileExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt", ".md" },
				RootAccessDenied: false,
				HadAccessDenied: false)
		};
		var useCase = new ScanOptionsUseCase(scanner);

		var result = useCase.GetExtensionsForRootFolders("/root", new List<string>(), new IgnoreRules(IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>()));

		// Root files should be scanned even with empty folder selection
		Assert.Equal(2, result.Value.Count);
		Assert.Contains(".txt", result.Value);
		Assert.Contains(".md", result.Value);
		Assert.False(result.RootAccessDenied);
		Assert.False(result.HadAccessDenied);
	}

	// Verifies empty result when scanner returns no root files and no folders selected.
	[Fact]
	public void GetExtensionsForRootFolders_ReturnsEmptyWhenNoRootFilesAndNoFolders()
	{
		var scanner = new StubFileSystemScanner();
		var useCase = new ScanOptionsUseCase(scanner);

		var result = useCase.GetExtensionsForRootFolders("/root", new List<string>(), new IgnoreRules(IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>()));

		Assert.Empty(result.Value);
		Assert.False(result.RootAccessDenied);
		Assert.False(result.HadAccessDenied);
	}

	// Verifies extensions are aggregated from root files and selected folders.
	[Fact]
	public void GetExtensionsForRootFolders_AggregatesRootAndFolderExtensions()
	{
		var scanner = new StubFileSystemScanner
		{
			GetRootFileExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".root" },
				RootAccessDenied: false,
				HadAccessDenied: false),
			GetExtensionsHandler = (path, _) => path.EndsWith("src", StringComparison.Ordinal)
				? new ScanResult<HashSet<string>>(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".root" },
					RootAccessDenied: false,
					HadAccessDenied: false)
				: new ScanResult<HashSet<string>>(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".md" },
					RootAccessDenied: false,
					HadAccessDenied: false)
		};

		var useCase = new ScanOptionsUseCase(scanner);

		var result = useCase.GetExtensionsForRootFolders(
			"/root",
			new List<string> { "src", "docs" },
			new IgnoreRules(IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
				IgnoreDotFiles: false,
				SmartIgnoredFolders: new HashSet<string>(),
				SmartIgnoredFiles: new HashSet<string>()));

		Assert.Equal(3, result.Value.Count);
		Assert.Contains(".root", result.Value);
		Assert.Contains(".cs", result.Value);
		Assert.Contains(".md", result.Value);
	}

	// Verifies CanReadRoot delegates to the scanner.
	[Fact]
	public void CanReadRoot_DelegatesToScanner()
	{
		var scanner = new StubFileSystemScanner
		{
			CanReadRootHandler = _ => false
		};
		var useCase = new ScanOptionsUseCase(scanner);

		Assert.False(useCase.CanReadRoot("/root"));
	}

	// Verifies execute returns empty lists when scanner results are empty.
	[Fact]
	public void Execute_ReturnsEmptyListsWhenScannerEmpty()
	{
		var scanner = new StubFileSystemScanner
		{
			GetExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				new HashSet<string>(),
				RootAccessDenied: false,
				HadAccessDenied: false),
			GetRootFolderNamesHandler = (_, _) => new ScanResult<List<string>>(
				new List<string>(),
				RootAccessDenied: false,
				HadAccessDenied: false)
		};

		var useCase = new ScanOptionsUseCase(scanner);
		var result = useCase.Execute(new ScanOptionsRequest("/root", new IgnoreRules(IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>())));

		Assert.Empty(result.Extensions);
		Assert.Empty(result.RootFolders);
		Assert.False(result.RootAccessDenied);
		Assert.False(result.HadAccessDenied);
	}

	// Verifies access-denied flags are aggregated across root files and folders.
	[Fact]
	public void GetExtensionsForRootFolders_CombinesAccessDeniedFlags()
	{
		var scanner = new StubFileSystemScanner
		{
			GetRootFileExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				new HashSet<string> { ".root" },
				RootAccessDenied: true,
				HadAccessDenied: false),
			GetExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				new HashSet<string> { ".cs" },
				RootAccessDenied: false,
				HadAccessDenied: true)
		};

		var useCase = new ScanOptionsUseCase(scanner);
		var result = useCase.GetExtensionsForRootFolders(
			"/root",
			new List<string> { "src" },
			new IgnoreRules(IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
				IgnoreDotFiles: false,
				SmartIgnoredFolders: new HashSet<string>(),
				SmartIgnoredFiles: new HashSet<string>()));

		Assert.True(result.RootAccessDenied);
		Assert.True(result.HadAccessDenied);
	}

	// Verifies extension aggregation de-duplicates case-insensitively.
	[Fact]
	public void GetExtensionsForRootFolders_DeduplicatesExtensions()
	{
		var scanner = new StubFileSystemScanner
		{
			GetRootFileExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".CS" },
				RootAccessDenied: false,
				HadAccessDenied: false),
			GetExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
				RootAccessDenied: false,
				HadAccessDenied: false)
		};

		var useCase = new ScanOptionsUseCase(scanner);
		var result = useCase.GetExtensionsForRootFolders(
			"/root",
			new List<string> { "src" },
			new IgnoreRules(IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
				IgnoreDotFiles: false,
				SmartIgnoredFolders: new HashSet<string>(),
				SmartIgnoredFiles: new HashSet<string>()));

		Assert.Single(result.Value);
		Assert.Contains(".cs", result.Value);
	}
}




