namespace DevProjex.Tests.Unit;

public sealed class ScanOptionsUseCasePathSemanticsTests
{
	private static IgnoreRules CreateRules() => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(),
		SmartIgnoredFiles: new HashSet<string>());

	[Fact]
	public void GetRootFolders_SortsUsingPathComparerDefault()
	{
		var returnedFolders = new[] { "a-src", "B-src", "C-src" };
		var scanner = new StubFileSystemScanner
		{
			GetRootFolderNamesHandler = (_, _) => new ScanResult<List<string>>(
				[..returnedFolders],
				RootAccessDenied: false,
				HadAccessDenied: false)
		};

		var useCase = new ScanOptionsUseCase(scanner);
		var result = useCase.GetRootFolders(CreateRootPath(), CreateRules());

		var expected = returnedFolders.ToList();
		expected.Sort(PathComparer.Default);

		Assert.Equal(expected, result.Value);
	}

	[Fact]
	public void GetExtensionsForRootFolders_PassesOriginalFolderPathsToScanner()
	{
		var rootPath = CreateRootPath();
		var folderCalls = new List<string>();
		var scanner = new StubFileSystemScanner
		{
			GetRootFileExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				[],
				RootAccessDenied: false,
				HadAccessDenied: false),
			GetExtensionsHandler = (path, _) =>
			{
				folderCalls.Add(path);
				return new ScanResult<HashSet<string>>(
					[],
					RootAccessDenied: false,
					HadAccessDenied: false);
			}
		};

		var useCase = new ScanOptionsUseCase(scanner);
		_ = useCase.GetExtensionsForRootFolders(rootPath, ["Src", "docs"], CreateRules());

		Assert.Equal(
			[
				Path.Combine(rootPath, "Src"),
				Path.Combine(rootPath, "docs")
			],
			folderCalls);
	}

	[Fact]
	public void GetExtensionsAndIgnoreCountsForRootFolders_AdvancedScanner_PassesOriginalFolderPathsToScanner()
	{
		var rootPath = CreateRootPath();
		var scanner = new RecordingAdvancedScanner();
		var useCase = new ScanOptionsUseCase(scanner);

		_ = useCase.GetExtensionsAndIgnoreCountsForRootFolders(rootPath, ["Src", "docs"], CreateRules());

		Assert.Equal(rootPath, scanner.RootFilePath);
		Assert.Equal(
			[
				Path.Combine(rootPath, "Src"),
				Path.Combine(rootPath, "docs")
			],
			scanner.FolderPaths);
	}

	[Fact]
	public void GetExtensionsForRootFolders_WhenRootExists_SkipsMissingRootedAndEscapingSelections()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFolder("src");
		var rootedSelection = Path.Combine(temp.Path, "absolute");
		var folderCalls = new List<string>();
		var scanner = new StubFileSystemScanner
		{
			GetRootFileExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				[],
				RootAccessDenied: false,
				HadAccessDenied: false),
			GetExtensionsHandler = (path, _) =>
			{
				folderCalls.Add(path);
				return new ScanResult<HashSet<string>>(
					[],
					RootAccessDenied: false,
					HadAccessDenied: false);
			}
		};

		var useCase = new ScanOptionsUseCase(scanner);
		_ = useCase.GetExtensionsForRootFolders(
			temp.Path,
			["src", "missing", ".", "..", rootedSelection],
			CreateRules());

		Assert.Equal([Path.Combine(temp.Path, "src")], folderCalls);
	}

	private static string CreateRootPath() => SyntheticTestPaths.CreateMissingRoot();

	private sealed class RecordingAdvancedScanner : IFileSystemScanner, IFileSystemScannerAdvanced
	{
		public string? RootFilePath { get; private set; }
		public List<string> FolderPaths { get; } = [];

		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) => new([], false, false);

		public ScanResult<HashSet<string>> GetRootFileExtensions(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) => new([], false, false);

		public ScanResult<List<string>> GetRootFolderNames(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) => new([], false, false);

		public ScanResult<ExtensionsScanData> GetExtensionsWithIgnoreOptionCounts(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			FolderPaths.Add(rootPath);
			return new ScanResult<ExtensionsScanData>(
				new ExtensionsScanData(new HashSet<string>(StringComparer.OrdinalIgnoreCase), IgnoreOptionCounts.Empty),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		public ScanResult<ExtensionsScanData> GetRootFileExtensionsWithIgnoreOptionCounts(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			RootFilePath = rootPath;
			return new ScanResult<ExtensionsScanData>(
				new ExtensionsScanData(new HashSet<string>(StringComparer.OrdinalIgnoreCase), IgnoreOptionCounts.Empty),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}
	}
}
