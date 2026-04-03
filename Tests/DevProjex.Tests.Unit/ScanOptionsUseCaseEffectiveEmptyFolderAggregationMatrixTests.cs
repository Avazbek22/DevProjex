namespace DevProjex.Tests.Unit;

public sealed class ScanOptionsUseCaseEffectiveEmptyFolderAggregationMatrixTests
{
	[Theory]
	[MemberData(nameof(AggregationCases))]
	public void GetEffectiveEmptyFolderCountForRootFolders_AggregatesCountsAndFlagsFromEffectiveCounter(
		int folderCount,
		int perFolderCount,
		bool folderRootDenied,
		bool folderHadDenied)
	{
		using var temp = new TemporaryDirectory();
		var selectedRoots = CreateFolders(temp, folderCount);
		var scanner = new EffectiveCountScanner(perFolderCount, folderRootDenied, folderHadDenied);
		var useCase = new ScanOptionsUseCase(scanner);

		var result = useCase.GetEffectiveEmptyFolderCountForRootFolders(
			temp.Path,
			selectedRoots,
			CreateAllowedExtensions(),
			CreateRules());

		Assert.Equal(perFolderCount * folderCount, result.Value);
		Assert.Equal(folderRootDenied, result.RootAccessDenied);
		Assert.Equal(folderHadDenied, result.HadAccessDenied);
		Assert.Equal(folderCount, scanner.CounterCalls);
	}

	[Fact]
	public void GetEffectiveEmptyFolderCountForRootFolders_FallbackScanner_UsesIgnoreCountsAggregation()
	{
		using var temp = new TemporaryDirectory();
		var selectedRoots = CreateFolders(temp, folderCount: 3);
		var scanner = new FallbackAdvancedScanner(perFolderCount: 4, folderRootDenied: true, folderHadDenied: true);
		var useCase = new ScanOptionsUseCase(scanner);

		var result = useCase.GetEffectiveEmptyFolderCountForRootFolders(
			temp.Path,
			selectedRoots,
			CreateAllowedExtensions(),
			CreateRules());

		Assert.Equal(12, result.Value);
		Assert.True(result.RootAccessDenied);
		Assert.True(result.HadAccessDenied);
		Assert.Equal(3, scanner.FolderCalls);
	}

	[Fact]
	public void GetEffectiveEmptyFolderCountForRootFolders_EmptyRootSelection_ReturnsZeroWithoutCallingScanner()
	{
		using var temp = new TemporaryDirectory();
		var scanner = new EffectiveCountScanner(perFolderCount: 5, folderRootDenied: false, folderHadDenied: false);
		var useCase = new ScanOptionsUseCase(scanner);

		var result = useCase.GetEffectiveEmptyFolderCountForRootFolders(
			temp.Path,
			[],
			CreateAllowedExtensions(),
			CreateRules());

		Assert.Equal(0, result.Value);
		Assert.False(result.RootAccessDenied);
		Assert.False(result.HadAccessDenied);
		Assert.Equal(0, scanner.CounterCalls);
	}

	[Fact]
	public void GetEffectiveEmptyFolderCountForRootFolders_MissingRootPath_ReturnsZeroWithoutCallingScanner()
	{
		var missingRootPath = Path.Combine(Path.GetTempPath(), "DevProjex", "Tests", "Missing", Guid.NewGuid().ToString("N"));
		var scanner = new EffectiveCountScanner(perFolderCount: 5, folderRootDenied: false, folderHadDenied: false);
		var useCase = new ScanOptionsUseCase(scanner);

		var result = useCase.GetEffectiveEmptyFolderCountForRootFolders(
			missingRootPath,
			["src"],
			CreateAllowedExtensions(),
			CreateRules());

		Assert.Equal(0, result.Value);
		Assert.False(result.RootAccessDenied);
		Assert.False(result.HadAccessDenied);
		Assert.Equal(0, scanner.CounterCalls);
	}

	[Fact]
	public void GetEffectiveEmptyFolderCountForRootFolders_PreCanceled_Throws()
	{
		using var temp = new TemporaryDirectory();
		var selectedRoots = CreateFolders(temp, folderCount: 1);
		var useCase = new ScanOptionsUseCase(new EffectiveCountScanner(3, false, false));
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		Assert.Throws<OperationCanceledException>(() =>
			useCase.GetEffectiveEmptyFolderCountForRootFolders(
				temp.Path,
				selectedRoots,
				CreateAllowedExtensions(),
				CreateRules(),
				cts.Token));
	}

	[Fact]
	public void GetEffectiveEmptyFolderCountForRootFolders_SequentialCancellation_Throws()
	{
		using var temp = new TemporaryDirectory();
		var selectedRoots = CreateFolders(temp, folderCount: 1);
		using var cts = new CancellationTokenSource();
		var scanner = new CancelingEffectiveCountScanner(cancelOnCallNumber: 1, cts);
		var useCase = new ScanOptionsUseCase(scanner);

		Assert.Throws<OperationCanceledException>(() =>
			useCase.GetEffectiveEmptyFolderCountForRootFolders(
				temp.Path,
				selectedRoots,
				CreateAllowedExtensions(),
				CreateRules(),
				cts.Token));
	}

	[Fact]
	public void GetEffectiveEmptyFolderCountForRootFolders_ParallelCancellation_Throws()
	{
		using var temp = new TemporaryDirectory();
		var selectedRoots = CreateFolders(temp, folderCount: 3);
		using var cts = new CancellationTokenSource();
		var scanner = new CancelingEffectiveCountScanner(cancelOnCallNumber: 2, cts);
		var useCase = new ScanOptionsUseCase(scanner);

		Assert.Throws<OperationCanceledException>(() =>
			useCase.GetEffectiveEmptyFolderCountForRootFolders(
				temp.Path,
				selectedRoots,
				CreateAllowedExtensions(),
				CreateRules(),
				cts.Token));
	}

	[Fact]
	public void GetEffectiveEmptyFolderCountForRootFolders_ForwardsAllowedExtensionsToCounter()
	{
		using var temp = new TemporaryDirectory();
		var selectedRoots = CreateFolders(temp, folderCount: 1);
		var scanner = new EffectiveCountScanner(perFolderCount: 2, folderRootDenied: false, folderHadDenied: false);
		var useCase = new ScanOptionsUseCase(scanner);
		var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			".cs",
			".md"
		};

		_ = useCase.GetEffectiveEmptyFolderCountForRootFolders(
			temp.Path,
			selectedRoots,
			allowedExtensions,
			CreateRules());

		Assert.True(
			scanner.LastAllowedExtensions
				.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
				.SequenceEqual([".cs", ".md"]));
	}

	public static IEnumerable<object[]> AggregationCases()
	{
		foreach (var folderCount in new[] { 1, 3 })
		{
			foreach (var perFolderCount in new[] { 1, 4, 7 })
			{
				foreach (var folderRootDenied in new[] { false, true })
				{
					foreach (var folderHadDenied in new[] { false, true })
						yield return [folderCount, perFolderCount, folderRootDenied, folderHadDenied];
				}
			}
		}
	}

	private static HashSet<string> CreateAllowedExtensions() => new(StringComparer.OrdinalIgnoreCase)
	{
		".cs"
	};

	private static IgnoreRules CreateRules() => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(),
		SmartIgnoredFiles: new HashSet<string>());

	private static string[] CreateFolders(TemporaryDirectory temp, int folderCount)
	{
		var selectedRoots = new string[folderCount];
		for (var index = 0; index < folderCount; index++)
		{
			selectedRoots[index] = $"folder{index + 1}";
			temp.CreateFolder(selectedRoots[index]);
		}

		return selectedRoots;
	}

	private sealed class EffectiveCountScanner(
		int perFolderCount,
		bool folderRootDenied,
		bool folderHadDenied)
		: IFileSystemScanner, IFileSystemScannerEffectiveEmptyFolderCounter
	{
		private int _counterCalls;

		public int CounterCalls => Volatile.Read(ref _counterCalls);
		public HashSet<string> LastAllowedExtensions { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<int> GetEffectiveEmptyFolderCount(
			string rootPath,
			IReadOnlySet<string> allowedExtensions,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Interlocked.Increment(ref _counterCalls);
			LastAllowedExtensions = new HashSet<string>(allowedExtensions, StringComparer.OrdinalIgnoreCase);
			return new ScanResult<int>(perFolderCount, folderRootDenied, folderHadDenied);
		}
	}

	private sealed class FallbackAdvancedScanner(
		int perFolderCount,
		bool folderRootDenied,
		bool folderHadDenied)
		: IFileSystemScanner, IFileSystemScannerAdvanced
	{
		private int _folderCalls;

		public int FolderCalls => Volatile.Read(ref _folderCalls);

		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<ExtensionsScanData> GetExtensionsWithIgnoreOptionCounts(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Interlocked.Increment(ref _folderCalls);
			return new ScanResult<ExtensionsScanData>(
				new ExtensionsScanData(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase),
					new IgnoreOptionCounts(EmptyFolders: perFolderCount)),
				folderRootDenied,
				folderHadDenied);
		}

		public ScanResult<ExtensionsScanData> GetRootFileExtensionsWithIgnoreOptionCounts(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return new ScanResult<ExtensionsScanData>(
				new ExtensionsScanData(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase),
					IgnoreOptionCounts.Empty),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}
	}

	private sealed class CancelingEffectiveCountScanner(
		int cancelOnCallNumber,
		CancellationTokenSource cts)
		: IFileSystemScanner, IFileSystemScannerEffectiveEmptyFolderCounter
	{
		private int _callNumber;

		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<int> GetEffectiveEmptyFolderCount(
			string rootPath,
			IReadOnlySet<string> allowedExtensions,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			if (Interlocked.Increment(ref _callNumber) == cancelOnCallNumber)
				cts.Cancel();

			cancellationToken.ThrowIfCancellationRequested();
			return new ScanResult<int>(1, false, false);
		}
	}
}
