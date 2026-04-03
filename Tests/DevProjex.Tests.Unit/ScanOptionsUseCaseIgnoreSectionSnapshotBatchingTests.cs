using System.Collections.Concurrent;

namespace DevProjex.Tests.Unit;

public sealed class ScanOptionsUseCaseIgnoreSectionSnapshotBatchingTests
{
	[Theory]
	[MemberData(nameof(AggregationCases))]
	public void GetIgnoreSectionSnapshotForRootFolders_UsesBatchProviderAndAggregatesSnapshot(
		int folderCount,
		bool rootRootDenied,
		bool rootHadDenied,
		bool folderRootDenied,
		bool folderHadDenied)
	{
		using var temp = new TemporaryDirectory();
		var selectedRoots = CreateFolders(temp, folderCount);
		var scanner = new IgnoreSectionSnapshotBatchScanner(
			rootRootDenied,
			rootHadDenied,
			folderRootDenied,
			folderHadDenied);
		var useCase = new ScanOptionsUseCase(scanner);

		var result = useCase.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			selectedRoots,
			CreateDiscoveryRules(),
			CreateEffectiveRules(),
			CreateAllowedExtensions());

		var expectedFolderRawCounts = Enumerable.Range(1, folderCount)
			.Select(IgnoreSectionSnapshotBatchScanner.BuildFolderRawCounts)
			.Aggregate(IgnoreOptionCounts.Empty, static (acc, current) => acc.Add(current));
		var expectedFolderEffectiveCounts = Enumerable.Range(1, folderCount)
			.Select(IgnoreSectionSnapshotBatchScanner.BuildFolderEffectiveCounts)
			.Aggregate(IgnoreOptionCounts.Empty, static (acc, current) => acc.Add(current));
		var expectedExtensions = new HashSet<string>(IgnoreSectionSnapshotBatchScanner.RootSnapshot.Extensions, StringComparer.OrdinalIgnoreCase);
		for (var index = 1; index <= folderCount; index++)
			expectedExtensions.UnionWith(IgnoreSectionSnapshotBatchScanner.BuildFolderExtensions(index));

		AssertSetEquals(expectedExtensions, result.Value.Extensions);
		Assert.Equal(
			IgnoreSectionSnapshotBatchScanner.RootSnapshot.RawIgnoreOptionCounts.Add(expectedFolderRawCounts),
			result.Value.RawIgnoreOptionCounts);
		Assert.Equal(
			IgnoreSectionSnapshotBatchScanner.RootSnapshot.EffectiveIgnoreOptionCounts.Add(expectedFolderEffectiveCounts),
			result.Value.EffectiveIgnoreOptionCounts);
		Assert.Equal(rootRootDenied || (folderCount > 0 && folderRootDenied), result.RootAccessDenied);
		Assert.Equal(rootHadDenied || (folderCount > 0 && folderHadDenied), result.HadAccessDenied);
		Assert.Equal(1, scanner.RootFileCalls);
		Assert.Equal(folderCount, scanner.FolderCalls);
	}

	[Fact]
	public void GetIgnoreSectionSnapshotForRootFolders_ForwardsNullAllowedExtensionsToEveryBatchCall()
	{
		using var temp = new TemporaryDirectory();
		var selectedRoots = CreateFolders(temp, folderCount: 3);
		var scanner = new IgnoreSectionSnapshotBatchScanner(false, false, false, false);
		var useCase = new ScanOptionsUseCase(scanner);

		_ = useCase.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			selectedRoots,
			CreateDiscoveryRules(),
			CreateEffectiveRules(),
			effectiveAllowedExtensions: null);

		Assert.Single(scanner.RootFileAllowedExtensionKeys);
		Assert.All(scanner.RootFileAllowedExtensionKeys, key => Assert.Equal("<null>", key));
		Assert.Equal(3, scanner.FolderAllowedExtensionKeys.Count);
		Assert.All(scanner.FolderAllowedExtensionKeys, key => Assert.Equal("<null>", key));
	}

	[Fact]
	public void GetIgnoreSectionSnapshotForRootFolders_ForwardsAllowedExtensionsToEveryBatchCall()
	{
		using var temp = new TemporaryDirectory();
		var selectedRoots = CreateFolders(temp, folderCount: 3);
		var scanner = new IgnoreSectionSnapshotBatchScanner(false, false, false, false);
		var useCase = new ScanOptionsUseCase(scanner);
		var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			".cs",
			".md",
			".json"
		};

		_ = useCase.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			selectedRoots,
			CreateDiscoveryRules(),
			CreateEffectiveRules(),
			allowedExtensions);

		var expectedKey = SerializeAllowedExtensions(allowedExtensions);
		Assert.Single(scanner.RootFileAllowedExtensionKeys);
		Assert.All(scanner.RootFileAllowedExtensionKeys, key => Assert.Equal(expectedKey, key));
		Assert.Equal(3, scanner.FolderAllowedExtensionKeys.Count);
		Assert.All(scanner.FolderAllowedExtensionKeys, key => Assert.Equal(expectedKey, key));
	}

	[Fact]
	public void GetIgnoreSectionSnapshotForRootFolders_CancellationDuringRootFileBatch_Throws()
	{
		using var temp = new TemporaryDirectory();
		using var cts = new CancellationTokenSource();
		var scanner = new CancelingIgnoreSectionSnapshotBatchScanner(cancelRootFiles: true, cancelOnFolderCallNumber: null, cts);
		var useCase = new ScanOptionsUseCase(scanner);

		Assert.Throws<OperationCanceledException>(() =>
			useCase.GetIgnoreSectionSnapshotForRootFolders(
				temp.Path,
				CreateFolders(temp, folderCount: 1),
				CreateDiscoveryRules(),
				CreateEffectiveRules(),
				CreateAllowedExtensions(),
				cts.Token));
	}

	[Fact]
	public void GetIgnoreSectionSnapshotForRootFolders_CancellationDuringParallelFolderBatches_Throws()
	{
		using var temp = new TemporaryDirectory();
		using var cts = new CancellationTokenSource();
		var scanner = new CancelingIgnoreSectionSnapshotBatchScanner(cancelRootFiles: false, cancelOnFolderCallNumber: 2, cts);
		var useCase = new ScanOptionsUseCase(scanner);

		Assert.Throws<OperationCanceledException>(() =>
			useCase.GetIgnoreSectionSnapshotForRootFolders(
				temp.Path,
				CreateFolders(temp, folderCount: 4),
				CreateDiscoveryRules(),
				CreateEffectiveRules(),
				CreateAllowedExtensions(),
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

	private static void AssertSetEquals(
		IReadOnlySet<string> expected,
		IReadOnlySet<string> actual)
	{
		Assert.True(
			expected.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
				.SequenceEqual(actual.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
	}

	private static IgnoreRules CreateDiscoveryRules() => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(),
		SmartIgnoredFiles: new HashSet<string>());

	private static IgnoreRules CreateEffectiveRules() => CreateDiscoveryRules() with
	{
		IgnoreEmptyFolders = true,
		IgnoreEmptyFiles = true,
		IgnoreExtensionlessFiles = true
	};

	private static HashSet<string> CreateAllowedExtensions() => new(StringComparer.OrdinalIgnoreCase)
	{
		".cs",
		".md"
	};

	private static string SerializeAllowedExtensions(IReadOnlySet<string>? allowedExtensions)
	{
		if (allowedExtensions is null)
			return "<null>";

		return string.Join(
			"|",
			allowedExtensions.OrderBy(static entry => entry, StringComparer.OrdinalIgnoreCase));
	}

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

	private sealed class IgnoreSectionSnapshotBatchScanner(
		bool rootRootDenied,
		bool rootHadDenied,
		bool folderRootDenied,
		bool folderHadDenied)
		: IFileSystemScanner, IFileSystemScannerIgnoreSectionSnapshotProvider
	{
		private int _rootFileCalls;
		private int _folderCalls;

		public static readonly IgnoreSectionScanData RootSnapshot = new(
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".json", "README" },
			new IgnoreOptionCounts(
				HiddenFolders: 100,
				HiddenFiles: 200,
				DotFolders: 300,
				DotFiles: 400,
				EmptyFolders: 500,
				ExtensionlessFiles: 600,
				EmptyFiles: 700),
			new IgnoreOptionCounts(
				HiddenFolders: 10,
				HiddenFiles: 20,
				DotFolders: 30,
				DotFiles: 40,
				EmptyFolders: 50,
				ExtensionlessFiles: 60,
				EmptyFiles: 70));

		public int RootFileCalls => Volatile.Read(ref _rootFileCalls);
		public int FolderCalls => Volatile.Read(ref _folderCalls);
		public ConcurrentBag<string?> RootFileAllowedExtensionKeys { get; } = [];
		public ConcurrentBag<string?> FolderAllowedExtensionKeys { get; } = [];

		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IReadOnlySet<string>? effectiveAllowedExtensions,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Interlocked.Increment(ref _folderCalls);
			FolderAllowedExtensionKeys.Add(SerializeAllowedExtensions(effectiveAllowedExtensions));

			var folderName = Path.GetFileName(rootPath);
			var folderIndex = int.Parse(folderName["folder".Length..], CultureInfo.InvariantCulture);
			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					BuildFolderExtensions(folderIndex),
					BuildFolderRawCounts(folderIndex),
					BuildFolderEffectiveCounts(folderIndex)),
				folderRootDenied,
				folderHadDenied);
		}

		public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IReadOnlySet<string>? effectiveAllowedExtensions,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Interlocked.Increment(ref _rootFileCalls);
			RootFileAllowedExtensionKeys.Add(SerializeAllowedExtensions(effectiveAllowedExtensions));
			return new ScanResult<IgnoreSectionScanData>(RootSnapshot, rootRootDenied, rootHadDenied);
		}

		public static HashSet<string> BuildFolderExtensions(int folderIndex) => new(StringComparer.OrdinalIgnoreCase)
		{
			$".ext{folderIndex}",
			$"FILE{folderIndex}"
		};

		public static IgnoreOptionCounts BuildFolderRawCounts(int folderIndex)
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

		public static IgnoreOptionCounts BuildFolderEffectiveCounts(int folderIndex)
		{
			return new IgnoreOptionCounts(
				HiddenFolders: folderIndex + 1,
				HiddenFiles: folderIndex + 2,
				DotFolders: folderIndex + 3,
				DotFiles: folderIndex + 4,
				EmptyFolders: folderIndex + 5,
				ExtensionlessFiles: folderIndex + 6,
				EmptyFiles: folderIndex + 7);
		}
	}

	private sealed class CancelingIgnoreSectionSnapshotBatchScanner(
		bool cancelRootFiles,
		int? cancelOnFolderCallNumber,
		CancellationTokenSource cts)
		: IFileSystemScanner, IFileSystemScannerIgnoreSectionSnapshotProvider
	{
		private int _folderCallNumber;

		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
			=> new([], false, false);

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IReadOnlySet<string>? effectiveAllowedExtensions,
			CancellationToken cancellationToken = default)
		{
			if (cancelOnFolderCallNumber is int callNumber &&
			    Interlocked.Increment(ref _folderCallNumber) == callNumber)
			{
				cts.Cancel();
			}

			cancellationToken.ThrowIfCancellationRequested();
			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(new HashSet<string>(StringComparer.OrdinalIgnoreCase), IgnoreOptionCounts.Empty, IgnoreOptionCounts.Empty),
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
			if (cancelRootFiles)
				cts.Cancel();

			cancellationToken.ThrowIfCancellationRequested();
			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(new HashSet<string>(StringComparer.OrdinalIgnoreCase), IgnoreOptionCounts.Empty, IgnoreOptionCounts.Empty),
				false,
				false);
		}
	}
}
