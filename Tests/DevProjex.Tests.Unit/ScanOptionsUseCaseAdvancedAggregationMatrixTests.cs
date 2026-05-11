namespace DevProjex.Tests.Unit;

public sealed class ScanOptionsUseCaseAdvancedAggregationMatrixTests
{
	private static IgnoreRules CreateRules() => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(),
		SmartIgnoredFiles: new HashSet<string>());

	public static IEnumerable<object[]> AggregationMatrix()
	{
		foreach (var rootRootDenied in new[] { false, true })
		foreach (var rootHadDenied in new[] { false, true })
		foreach (var folderRootDenied in new[] { false, true })
		foreach (var folderHadDenied in new[] { false, true })
		foreach (var folderCount in new[] { 0, 1, 3 })
			yield return [rootRootDenied, rootHadDenied, folderRootDenied, folderHadDenied, folderCount];
	}

	[Theory]
	[MemberData(nameof(AggregationMatrix))]
	public void GetExtensionsAndIgnoreCountsForRootFolders_AggregatesFlagsAndCounts(
		bool rootRootDenied,
		bool rootHadDenied,
		bool folderRootDenied,
		bool folderHadDenied,
		int folderCount)
	{
		var folders = Enumerable.Range(1, folderCount)
			.Select(i => $"folder{i}")
			.ToArray();

		var scanner = new MatrixAdvancedScanner(
			rootRootDenied,
			rootHadDenied,
			folderRootDenied,
			folderHadDenied);

		var useCase = new ScanOptionsUseCase(scanner);
		var result = useCase.GetExtensionsAndIgnoreCountsForRootFolders(
			SyntheticTestPaths.CreateMissingRoot(),
			folders,
			CreateRules());

		Assert.Equal(rootRootDenied || (folderCount > 0 && folderRootDenied), result.RootAccessDenied);
		Assert.Equal(rootHadDenied || (folderCount > 0 && folderHadDenied), result.HadAccessDenied);

		var expectedExtensionCount = 1 + folderCount;
		Assert.Equal(expectedExtensionCount, result.Value.Extensions.Count);
		Assert.Contains(".root", result.Value.Extensions);
		foreach (var folder in folders)
			Assert.Contains($".{folder}", result.Value.Extensions);

		var expectedCounts = new IgnoreOptionCounts(
			HiddenFolders: 1 + (10 * folderCount),
			HiddenFiles: 2 + (20 * folderCount),
			DotFolders: 3 + (30 * folderCount),
			DotFiles: 4 + (40 * folderCount),
			EmptyFolders: 5 + (50 * folderCount),
			ExtensionlessFiles: 6 + (60 * folderCount));

		Assert.Equal(expectedCounts, result.Value.IgnoreOptionCounts);
	}

	[Fact]
	public void GetExtensionsAndIgnoreCountsForRootFolders_FallbackScanner_ReturnsEmptyCounts()
	{
		var scanner = new StubFileSystemScanner
		{
			GetRootFileExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".root" },
				RootAccessDenied: false,
				HadAccessDenied: false),
			GetExtensionsHandler = (path, _) =>
			{
				var folderName = Path.GetFileName(path);
				return new ScanResult<HashSet<string>>(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $".{folderName}" },
					RootAccessDenied: false,
					HadAccessDenied: false);
			}
		};

		var useCase = new ScanOptionsUseCase(scanner);
		var result = useCase.GetExtensionsAndIgnoreCountsForRootFolders(
			SyntheticTestPaths.CreateMissingRoot(),
			["a", "b"],
			CreateRules());

		Assert.Equal(3, result.Value.Extensions.Count);
		Assert.Equal(IgnoreOptionCounts.Empty, result.Value.IgnoreOptionCounts);
	}

	[Fact]
	public void GetExtensionsAndIgnoreCountsForRootFolders_PreCanceled_Throws()
	{
		var scanner = new MatrixAdvancedScanner(false, false, false, false);
		var useCase = new ScanOptionsUseCase(scanner);
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		Assert.Throws<OperationCanceledException>(() =>
			useCase.GetExtensionsAndIgnoreCountsForRootFolders(
				SyntheticTestPaths.CreateMissingRoot(),
				["a", "b"],
				CreateRules(),
				cts.Token));
	}

	[Theory]
	[InlineData(CancelStage.RootFiles)]
	[InlineData(CancelStage.FirstFolder)]
	public void GetExtensionsAndIgnoreCountsForRootFolders_MidFlightCancellation_Throws(CancelStage stage)
	{
		using var cts = new CancellationTokenSource();
		var scanner = new CancelingAdvancedScanner(stage, cts);
		var useCase = new ScanOptionsUseCase(scanner);

		Assert.Throws<OperationCanceledException>(() =>
			useCase.GetExtensionsAndIgnoreCountsForRootFolders(
				SyntheticTestPaths.CreateMissingRoot(),
				["a", "b", "c"],
				CreateRules(),
				cts.Token));
	}

	private sealed class MatrixAdvancedScanner(
		bool rootRootDenied,
		bool rootHadDenied,
		bool folderRootDenied,
		bool folderHadDenied)
		: IFileSystemScanner, IFileSystemScannerAdvanced
	{
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
			var folderName = Path.GetFileName(rootPath);
			return new ScanResult<ExtensionsScanData>(
				new ExtensionsScanData(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $".{folderName}" },
					new IgnoreOptionCounts(10, 20, 30, 40, 50, 60)),
				RootAccessDenied: folderRootDenied,
				HadAccessDenied: folderHadDenied);
		}

		public ScanResult<ExtensionsScanData> GetRootFileExtensionsWithIgnoreOptionCounts(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			return new ScanResult<ExtensionsScanData>(
				new ExtensionsScanData(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".root" },
					new IgnoreOptionCounts(1, 2, 3, 4, 5, 6)),
				RootAccessDenied: rootRootDenied,
				HadAccessDenied: rootHadDenied);
		}
	}

	public enum CancelStage
	{
		RootFiles = 0,
		FirstFolder = 1
	}

	private sealed class CancelingAdvancedScanner(CancelStage stage, CancellationTokenSource cts)
		: IFileSystemScanner, IFileSystemScannerAdvanced
	{
		private int _folderCalls;

		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<ExtensionsScanData> GetRootFileExtensionsWithIgnoreOptionCounts(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			if (stage == CancelStage.RootFiles)
				cts.Cancel();

			cancellationToken.ThrowIfCancellationRequested();

			return new ScanResult<ExtensionsScanData>(
				new ExtensionsScanData(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".root" }, IgnoreOptionCounts.Empty),
				false,
				false);
		}

		public ScanResult<ExtensionsScanData> GetExtensionsWithIgnoreOptionCounts(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			if (stage == CancelStage.FirstFolder && Interlocked.Increment(ref _folderCalls) == 1)
				cts.Cancel();

			cancellationToken.ThrowIfCancellationRequested();

			return new ScanResult<ExtensionsScanData>(
				new ExtensionsScanData(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".f" }, IgnoreOptionCounts.Empty),
				false,
				false);
		}
	}
}
