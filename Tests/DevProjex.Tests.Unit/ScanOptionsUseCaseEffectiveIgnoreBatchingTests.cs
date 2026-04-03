using System.Collections.Concurrent;

namespace DevProjex.Tests.Unit;

public sealed class ScanOptionsUseCaseEffectiveIgnoreBatchingTests
{
	[Theory]
	[MemberData(nameof(AggregationCases))]
	public void GetEffectiveIgnoreOptionCountsForRootFolders_UsesBatchProviderAndAggregatesCounts(
		int folderCount,
		bool rootRootDenied,
		bool rootHadDenied,
		bool folderRootDenied,
		bool folderHadDenied)
	{
		using var temp = new TemporaryDirectory();
		var selectedRoots = CreateFolders(temp, folderCount);
		var scanner = new EffectiveIgnoreBatchScanner(
			rootRootDenied,
			rootHadDenied,
			folderRootDenied,
			folderHadDenied);
		var useCase = new ScanOptionsUseCase(scanner);

		var result = useCase.GetEffectiveIgnoreOptionCountsForRootFolders(
			temp.Path,
			selectedRoots,
			CreateAllowedExtensions(),
			CreateRules(),
			new IgnoreOptionCounts(
				HiddenFolders: 99,
				HiddenFiles: 98,
				DotFolders: 97,
				DotFiles: 96,
				EmptyFolders: 95,
				ExtensionlessFiles: 94,
				EmptyFiles: 93));

		var expectedFolderCounts = Enumerable.Range(1, folderCount)
			.Select(EffectiveIgnoreBatchScanner.BuildFolderCounts)
			.Aggregate(IgnoreOptionCounts.Empty, static (acc, current) => acc.Add(current));
		var expectedCounts = EffectiveIgnoreBatchScanner.RootCounts.Add(expectedFolderCounts);

		Assert.Equal(expectedCounts, result.Value);
		Assert.Equal(rootRootDenied || (folderCount > 0 && folderRootDenied), result.RootAccessDenied);
		Assert.Equal(rootHadDenied || (folderCount > 0 && folderHadDenied), result.HadAccessDenied);
		Assert.Equal(1, scanner.RootFileCalls);
		Assert.Equal(folderCount, scanner.FolderCalls);
	}

	[Fact]
	public void GetEffectiveIgnoreOptionCountsForRootFolders_ForwardsAllowedExtensionsToEveryBatchCall()
	{
		using var temp = new TemporaryDirectory();
		var selectedRoots = CreateFolders(temp, folderCount: 3);
		var scanner = new EffectiveIgnoreBatchScanner(false, false, false, false);
		var useCase = new ScanOptionsUseCase(scanner);
		var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			".cs",
			".md",
			".json"
		};

		_ = useCase.GetEffectiveIgnoreOptionCountsForRootFolders(
			temp.Path,
			selectedRoots,
			allowedExtensions,
			CreateRules(),
			IgnoreOptionCounts.Empty);

		Assert.Single(scanner.RootFileAllowedExtensionsSnapshots);
		Assert.Equal(3, scanner.FolderAllowedExtensionsSnapshots.ToArray().Length);
		Assert.All(scanner.RootFileAllowedExtensionsSnapshots, snapshot => AssertExtensions(snapshot, allowedExtensions));
		Assert.All(scanner.FolderAllowedExtensionsSnapshots, snapshot => AssertExtensions(snapshot, allowedExtensions));
	}

	[Fact]
	public void GetEffectiveIgnoreOptionCountsForRootFolders_CancellationDuringRootFileBatch_Throws()
	{
		using var temp = new TemporaryDirectory();
		using var cts = new CancellationTokenSource();
		var scanner = new CancelingEffectiveIgnoreBatchScanner(cancelRootFiles: true, cancelOnFolderCallNumber: null, cts);
		var useCase = new ScanOptionsUseCase(scanner);

		Assert.Throws<OperationCanceledException>(() =>
			useCase.GetEffectiveIgnoreOptionCountsForRootFolders(
				temp.Path,
				CreateFolders(temp, folderCount: 1),
				CreateAllowedExtensions(),
				CreateRules(),
				IgnoreOptionCounts.Empty,
				cts.Token));
	}

	[Fact]
	public void GetEffectiveIgnoreOptionCountsForRootFolders_CancellationDuringParallelFolderBatches_Throws()
	{
		using var temp = new TemporaryDirectory();
		using var cts = new CancellationTokenSource();
		var scanner = new CancelingEffectiveIgnoreBatchScanner(cancelRootFiles: false, cancelOnFolderCallNumber: 2, cts);
		var useCase = new ScanOptionsUseCase(scanner);

		Assert.Throws<OperationCanceledException>(() =>
			useCase.GetEffectiveIgnoreOptionCountsForRootFolders(
				temp.Path,
				CreateFolders(temp, folderCount: 4),
				CreateAllowedExtensions(),
				CreateRules(),
				IgnoreOptionCounts.Empty,
				cts.Token));
	}

	public static IEnumerable<object[]> AggregationCases()
	{
		foreach (var folderCount in new[] { 0, 1, 4 })
		{
			foreach (var rootRootDenied in new[] { false, true })
			{
				foreach (var rootHadDenied in new[] { false, true })
				{
					foreach (var folderRootDenied in new[] { false, true })
					{
						foreach (var folderHadDenied in new[] { false, true })
							yield return [folderCount, rootRootDenied, rootHadDenied, folderRootDenied, folderHadDenied];
					}
				}
			}
		}
	}

	private static void AssertExtensions(IReadOnlyCollection<string> actual, IReadOnlySet<string> expected)
	{
		Assert.True(
			actual.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
				.SequenceEqual(expected.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
	}

	private static HashSet<string> CreateAllowedExtensions() => new(StringComparer.OrdinalIgnoreCase)
	{
		".cs",
		".md"
	};

	private static IgnoreRules CreateRules() => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(),
		SmartIgnoredFiles: new HashSet<string>())
	{
		IgnoreEmptyFolders = true,
		IgnoreEmptyFiles = true,
		IgnoreExtensionlessFiles = true
	};

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

	private sealed class EffectiveIgnoreBatchScanner(
		bool rootRootDenied,
		bool rootHadDenied,
		bool folderRootDenied,
		bool folderHadDenied)
		: IFileSystemScanner, IFileSystemScannerEffectiveIgnoreCountsProvider
	{
		private int _rootFileCalls;
		private int _folderCalls;

		public static readonly IgnoreOptionCounts RootCounts = new(
			HiddenFolders: 100,
			HiddenFiles: 200,
			DotFolders: 300,
			DotFiles: 400,
			EmptyFolders: 500,
			ExtensionlessFiles: 600,
			EmptyFiles: 700);

		public int RootFileCalls => Volatile.Read(ref _rootFileCalls);
		public int FolderCalls => Volatile.Read(ref _folderCalls);
		public ConcurrentBag<IReadOnlyCollection<string>> RootFileAllowedExtensionsSnapshots { get; } = [];
		public ConcurrentBag<IReadOnlyCollection<string>> FolderAllowedExtensionsSnapshots { get; } = [];

		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<IgnoreOptionCounts> GetEffectiveIgnoreOptionCounts(
			string rootPath,
			IReadOnlySet<string> allowedExtensions,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Interlocked.Increment(ref _folderCalls);
			FolderAllowedExtensionsSnapshots.Add([.. allowedExtensions]);

			var folderName = Path.GetFileName(rootPath);
			var folderIndex = int.Parse(folderName["folder".Length..], CultureInfo.InvariantCulture);
			return new ScanResult<IgnoreOptionCounts>(
				BuildFolderCounts(folderIndex),
				folderRootDenied,
				folderHadDenied);
		}

		public ScanResult<IgnoreOptionCounts> GetEffectiveRootFileIgnoreOptionCounts(
			string rootPath,
			IReadOnlySet<string> allowedExtensions,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Interlocked.Increment(ref _rootFileCalls);
			RootFileAllowedExtensionsSnapshots.Add([.. allowedExtensions]);
			return new ScanResult<IgnoreOptionCounts>(RootCounts, rootRootDenied, rootHadDenied);
		}

		public static IgnoreOptionCounts BuildFolderCounts(int folderIndex)
		{
			return new IgnoreOptionCounts(
				HiddenFolders: folderIndex,
				HiddenFiles: folderIndex * 10,
				DotFolders: folderIndex * 100,
				DotFiles: folderIndex * 1_000,
				EmptyFolders: folderIndex * 10_000,
				ExtensionlessFiles: folderIndex * 100_000,
				EmptyFiles: folderIndex * 1_000_000);
		}
	}

	private sealed class CancelingEffectiveIgnoreBatchScanner(
		bool cancelRootFiles,
		int? cancelOnFolderCallNumber,
		CancellationTokenSource cts)
		: IFileSystemScanner, IFileSystemScannerEffectiveIgnoreCountsProvider
	{
		private int _folderCallNumber;

		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<IgnoreOptionCounts> GetEffectiveIgnoreOptionCounts(
			string rootPath,
			IReadOnlySet<string> allowedExtensions,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			if (cancelOnFolderCallNumber is int callNumber &&
			    Interlocked.Increment(ref _folderCallNumber) == callNumber)
			{
				cts.Cancel();
			}

			cancellationToken.ThrowIfCancellationRequested();
			return new ScanResult<IgnoreOptionCounts>(IgnoreOptionCounts.Empty, false, false);
		}

		public ScanResult<IgnoreOptionCounts> GetEffectiveRootFileIgnoreOptionCounts(
			string rootPath,
			IReadOnlySet<string> allowedExtensions,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			if (cancelRootFiles)
				cts.Cancel();

			cancellationToken.ThrowIfCancellationRequested();
			return new ScanResult<IgnoreOptionCounts>(IgnoreOptionCounts.Empty, false, false);
		}
	}
}
