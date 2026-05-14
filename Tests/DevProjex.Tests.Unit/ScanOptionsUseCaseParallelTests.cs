using System.Collections.Concurrent;

namespace DevProjex.Tests.Unit;

/// <summary>
/// Tests for parallel execution behavior in ScanOptionsUseCase.
/// Verifies that scanning operations run concurrently for better performance.
/// </summary>
public sealed class ScanOptionsUseCaseParallelTests
{
	private static IgnoreRules CreateDefaultRules() => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(),
		SmartIgnoredFiles: new HashSet<string>());

	/// <summary>
	/// Verifies Execute calls GetExtensions and GetRootFolderNames in parallel.
	/// Both methods should be invoked, and the result should aggregate their outputs.
	/// </summary>
	[Fact]
	public void Execute_CallsBothScannersInParallel()
	{
		var extensionsCalled = 0;
		var foldersCalled = 0;

		var scanner = new StubFileSystemScanner
		{
			GetExtensionsHandler = (_, _) =>
			{
				Interlocked.Increment(ref extensionsCalled);
				Thread.Sleep(50); // Simulate I/O
				return new ScanResult<HashSet<string>>(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
					RootAccessDenied: false,
					HadAccessDenied: false);
			},
			GetRootFolderNamesHandler = (_, _) =>
			{
				Interlocked.Increment(ref foldersCalled);
				Thread.Sleep(50); // Simulate I/O
				return new ScanResult<List<string>>(
					["src"],
					RootAccessDenied: false,
					HadAccessDenied: false);
			}
		};

		var useCase = new ScanOptionsUseCase(scanner);
		var result = useCase.Execute(new ScanOptionsRequest("/root", CreateDefaultRules()));

		Assert.Equal(1, extensionsCalled);
		Assert.Equal(1, foldersCalled);
		Assert.Contains(".cs", result.Extensions);
		Assert.Contains("src", result.RootFolders);
	}

	/// <summary>
	/// Verifies GetExtensionsForRootFolders scans multiple folders in parallel.
	/// The call count should match the number of folders, and results should be aggregated.
	/// </summary>
	[Fact]
	public void GetExtensionsForRootFolders_ScansMultipleFoldersInParallel()
	{
		var callCount = 0;
		var calledPaths = new ConcurrentBag<string>();

		var scanner = new StubFileSystemScanner
		{
			GetRootFileExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				[], false, false),
			GetExtensionsHandler = (path, _) =>
			{
				Interlocked.Increment(ref callCount);
				calledPaths.Add(path);
				Thread.Sleep(20); // Simulate I/O
				return new ScanResult<HashSet<string>>(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
					RootAccessDenied: false,
					HadAccessDenied: false);
			}
		};

		var useCase = new ScanOptionsUseCase(scanner);
		var folders = new List<string> { "src", "lib", "tests", "docs" };

		var result = useCase.GetExtensionsForRootFolders(
			SyntheticTestPaths.CreateMissingRoot(),
			folders,
			CreateDefaultRules());

		Assert.Equal(4, callCount);
		Assert.Contains(".cs", result.Value);
	}

	/// <summary>
	/// Verifies parallel folder scanning aggregates extensions correctly.
	/// Different folders returning different extensions should all appear in result.
	/// </summary>
	[Fact]
	public void GetExtensionsForRootFolders_AggregatesExtensionsFromAllFolders()
	{
		var extensionsByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			{ "src", ".cs" },
			{ "web", ".ts" },
			{ "docs", ".md" }
		};

		var scanner = new StubFileSystemScanner
		{
			GetRootFileExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				[], false, false),
			GetExtensionsHandler = (path, _) =>
			{
				var folderName = Path.GetFileName(path);
				var ext = extensionsByPath.GetValueOrDefault(folderName, ".txt");
				return new ScanResult<HashSet<string>>(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ext },
					RootAccessDenied: false,
					HadAccessDenied: false);
			}
		};

		var useCase = new ScanOptionsUseCase(scanner);
		var folders = new List<string> { "src", "web", "docs" };

		var result = useCase.GetExtensionsForRootFolders(
			SyntheticTestPaths.CreateMissingRoot(),
			folders,
			CreateDefaultRules());

		Assert.Equal(3, result.Value.Count);
		Assert.Contains(".cs", result.Value);
		Assert.Contains(".ts", result.Value);
		Assert.Contains(".md", result.Value);
	}

	/// <summary>
	/// Verifies thread-safe aggregation of access denied flags across parallel scans.
	/// If any folder scan reports access denied, the result should reflect it.
	/// </summary>
	[Fact]
	public void GetExtensionsForRootFolders_ThreadSafeAccessDeniedAggregation()
	{
		var callIndex = 0;

		var scanner = new StubFileSystemScanner
		{
			GetRootFileExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				[], false, false),
			GetExtensionsHandler = (_, _) =>
			{
				var idx = Interlocked.Increment(ref callIndex);
				// Third call returns access denied
				var rootDenied = idx == 3;
				var hadDenied = idx == 2;
				return new ScanResult<HashSet<string>>(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
					RootAccessDenied: rootDenied,
					HadAccessDenied: hadDenied);
			}
		};

		var useCase = new ScanOptionsUseCase(scanner);
		var folders = new List<string> { "a", "b", "c", "d" };

		var result = useCase.GetExtensionsForRootFolders(
			SyntheticTestPaths.CreateMissingRoot(),
			folders,
			CreateDefaultRules());

		Assert.True(result.RootAccessDenied);
		Assert.True(result.HadAccessDenied);
	}

	/// <summary>
	/// Verifies concurrent execution handles exceptions gracefully.
	/// If one folder fails, others should still be processed.
	/// </summary>
	[Fact]
	public void GetExtensionsForRootFolders_HandlesExceptionsInParallelScans()
	{
		var processedCount = 0;

		var scanner = new StubFileSystemScanner
		{
			GetRootFileExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				[], false, false),
			GetExtensionsHandler = (path, _) =>
			{
				if (path.Contains("failing"))
				{
					// Return empty result (simulating handled exception)
					return new ScanResult<HashSet<string>>(
						[], false, true);
				}

				Interlocked.Increment(ref processedCount);
				return new ScanResult<HashSet<string>>(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
					RootAccessDenied: false,
					HadAccessDenied: false);
			}
		};

		var useCase = new ScanOptionsUseCase(scanner);
		var folders = new List<string> { "good1", "failing", "good2" };

		var result = useCase.GetExtensionsForRootFolders(
			SyntheticTestPaths.CreateMissingRoot(),
			folders,
			CreateDefaultRules());

		Assert.Equal(2, processedCount);
		Assert.Contains(".cs", result.Value);
		Assert.True(result.HadAccessDenied);
	}

	/// <summary>
	/// Verifies deduplication works correctly across parallel folder scans.
	/// Same extension from multiple folders should appear only once.
	/// </summary>
	[Fact]
	public void GetExtensionsForRootFolders_DeduplicatesAcrossParallelScans()
	{
		var scanner = new StubFileSystemScanner
		{
			GetRootFileExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".CS" },
				false, false),
			GetExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".Cs" },
				RootAccessDenied: false,
				HadAccessDenied: false)
		};

		var useCase = new ScanOptionsUseCase(scanner);
		var folders = new List<string> { "src1", "src2", "src3" };

		var result = useCase.GetExtensionsForRootFolders(
			SyntheticTestPaths.CreateMissingRoot(),
			folders,
			CreateDefaultRules());

		// All variations of .cs should be deduplicated to one entry
		Assert.Single(result.Value);
	}

	/// <summary>
	/// Verifies root files are always scanned regardless of parallel folder scanning.
	/// </summary>
	[Fact]
	public void GetExtensionsForRootFolders_AlwaysIncludesRootFiles()
	{
		var rootFilesCalled = false;

		var scanner = new StubFileSystemScanner
		{
			GetRootFileExtensionsHandler = (_, _) =>
			{
				rootFilesCalled = true;
				return new ScanResult<HashSet<string>>(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".root" },
					false, false);
			},
			GetExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".folder" },
				RootAccessDenied: false,
				HadAccessDenied: false)
		};

		var useCase = new ScanOptionsUseCase(scanner);
		var folders = new List<string> { "src" };

		var result = useCase.GetExtensionsForRootFolders(
			SyntheticTestPaths.CreateMissingRoot(),
			folders,
			CreateDefaultRules());

		Assert.True(rootFilesCalled);
		Assert.Contains(".root", result.Value);
		Assert.Contains(".folder", result.Value);
	}

	/// <summary>
	/// Verifies Execute correctly combines access denied flags from parallel scans.
	/// </summary>
	[Fact]
	public void Execute_CombinesAccessDeniedFromParallelScans()
	{
		var scanner = new StubFileSystemScanner
		{
			GetExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				[".cs"],
				RootAccessDenied: true,
				HadAccessDenied: false),
			GetRootFolderNamesHandler = (_, _) => new ScanResult<List<string>>(
				["src"],
				RootAccessDenied: false,
				HadAccessDenied: true)
		};

		var useCase = new ScanOptionsUseCase(scanner);
		var result = useCase.Execute(new ScanOptionsRequest("/root", CreateDefaultRules()));

		Assert.True(result.RootAccessDenied);
		Assert.True(result.HadAccessDenied);
	}

	/// <summary>
	/// Verifies empty folder list still scans root files.
	/// </summary>
	[Fact]
	public void GetExtensionsForRootFolders_EmptyFolderListStillScansRootFiles()
	{
		var scanner = new StubFileSystemScanner
		{
			GetRootFileExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".readme" },
				false, false),
			GetExtensionsHandler = (_, _) =>
				throw new InvalidOperationException("Should not be called for empty folder list")
		};

		var useCase = new ScanOptionsUseCase(scanner);

		var result = useCase.GetExtensionsForRootFolders("/root", new List<string>(), CreateDefaultRules());

		Assert.Single(result.Value);
		Assert.Contains(".readme", result.Value);
	}

	/// <summary>
	/// Verifies large number of folders are processed in parallel without issues.
	/// </summary>
	[Fact]
	public void GetExtensionsForRootFolders_HandlesLargeFolderCount()
	{
		var processedCount = 0;

		var scanner = new StubFileSystemScanner
		{
			GetRootFileExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				[], false, false),
			GetExtensionsHandler = (_, _) =>
			{
				Interlocked.Increment(ref processedCount);
				return new ScanResult<HashSet<string>>(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
					RootAccessDenied: false,
					HadAccessDenied: false);
			}
		};

		var useCase = new ScanOptionsUseCase(scanner);
		var folders = new List<string>();
		for (int i = 0; i < 100; i++)
			folders.Add($"folder{i}");

		var result = useCase.GetExtensionsForRootFolders(
			SyntheticTestPaths.CreateMissingRoot(),
			folders,
			CreateDefaultRules());

		Assert.Equal(100, processedCount);
		Assert.Contains(".cs", result.Value);
	}

	/// <summary>
	/// Verifies concurrent access to the same UseCase instance is thread-safe.
	/// </summary>
	[Fact]
	public async Task Execute_ThreadSafeForConcurrentCalls()
	{
		var callCount = 0;

		var scanner = new StubFileSystemScanner
		{
			GetExtensionsHandler = (_, _) =>
			{
				Interlocked.Increment(ref callCount);
				Thread.Sleep(10);
				return new ScanResult<HashSet<string>>(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
					RootAccessDenied: false,
					HadAccessDenied: false);
			},
			GetRootFolderNamesHandler = (_, _) =>
			{
				Interlocked.Increment(ref callCount);
				return new ScanResult<List<string>>(
					["src"],
					RootAccessDenied: false,
					HadAccessDenied: false);
			}
		};

		var useCase = new ScanOptionsUseCase(scanner);
		var tasks = new Task<ScanOptionsResult>[10];

		for (int i = 0; i < 10; i++)
		{
			var idx = i;
			tasks[i] = Task.Run(() => useCase.Execute(
				new ScanOptionsRequest($"/root{idx}", CreateDefaultRules())));
		}

		var results = await Task.WhenAll(tasks);

		// Each Execute call invokes both handlers
		Assert.Equal(20, callCount);

		foreach (var result in results)
		{
			Assert.Contains(".cs", result.Extensions);
			Assert.Contains("src", result.RootFolders);
		}
	}

	/// <summary>
	/// Verifies results are sorted correctly after parallel aggregation.
	/// </summary>
	[Fact]
	public void Execute_ResultsSortedAfterParallelExecution()
	{
		var scanner = new StubFileSystemScanner
		{
			GetExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".z", ".A", ".m" },
				RootAccessDenied: false,
				HadAccessDenied: false),
			GetRootFolderNamesHandler = (_, _) => new ScanResult<List<string>>(
				["z", "A", "m"],
				RootAccessDenied: false,
				HadAccessDenied: false)
		};

		var useCase = new ScanOptionsUseCase(scanner);
		var result = useCase.Execute(new ScanOptionsRequest("/root", CreateDefaultRules()));

		// Verify case-insensitive alphabetical sorting
		Assert.Equal([".A", ".m", ".z"], result.Extensions);
		Assert.Equal(["A", "m", "z"], result.RootFolders);
	}

	[Fact]
	public async Task GetExtensionsForRootFolders_LegacyScanner_TwoFoldersStartBeforeFirstCompletes()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFolder("src");
		temp.CreateFolder("tests");
		using var scanner = new BlockingLegacyRootScanner(expectedFolderCalls: 2);
		var useCase = new ScanOptionsUseCase(scanner);

		var scanTask = Task.Run(() => useCase.GetExtensionsForRootFolders(
			temp.Path,
			["src", "tests"],
			CreateDefaultRules()));

		var allFoldersStarted = scanner.WaitForAllFolderCalls(TimeSpan.FromSeconds(2));
		scanner.Release();
		var result = await scanTask.WaitAsync(TimeSpan.FromSeconds(2));

		Assert.True(allFoldersStarted);
		Assert.Equal(2, scanner.FolderCallCount);
		Assert.Contains(".folder", result.Value);
	}

	[Theory]
	[InlineData(BlockingScanOperation.ExtensionsWithCounts)]
	[InlineData(BlockingScanOperation.EffectiveEmptyFolders)]
	[InlineData(BlockingScanOperation.IgnoreSectionSnapshot)]
	[InlineData(BlockingScanOperation.EffectiveIgnoreCounts)]
	public async Task RootFolderScanOperations_TwoFoldersStartBeforeFirstCompletes(
		BlockingScanOperation operation)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFolder("src");
		temp.CreateFolder("tests");
		using var scanner = new BlockingAdvancedRootScanner(operation, expectedFolderCalls: 2);
		var useCase = new ScanOptionsUseCase(scanner);

		var scanTask = Task.Run(() => ExecuteBlockingOperation(useCase, operation, temp.Path));

		var allFoldersStarted = scanner.WaitForAllFolderCalls(TimeSpan.FromSeconds(2));
		scanner.Release();
		await scanTask.WaitAsync(TimeSpan.FromSeconds(2));

		Assert.True(allFoldersStarted);
		Assert.Equal(2, scanner.FolderCallCount);
	}

	[Fact]
	public void ScanParallelismPolicy_UsesOneCpuWideMaximumEverywhere()
	{
		var options = ScanParallelismPolicy.CreateOptions();

		Assert.True(ScanParallelismPolicy.MaxDegreeOfParallelism >= Math.Max(1, Environment.ProcessorCount));
		Assert.Equal(ScanParallelismPolicy.MaxDegreeOfParallelism, options.MaxDegreeOfParallelism);
	}

	private static object ExecuteBlockingOperation(
		ScanOptionsUseCase useCase,
		BlockingScanOperation operation,
		string rootPath)
	{
		return operation switch
		{
			BlockingScanOperation.ExtensionsWithCounts => useCase.GetExtensionsAndIgnoreCountsForRootFolders(
				rootPath,
				["src", "tests"],
				CreateDefaultRules()),
			BlockingScanOperation.EffectiveEmptyFolders => useCase.GetEffectiveEmptyFolderCountForRootFolders(
				rootPath,
				["src", "tests"],
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
				CreateDefaultRules()),
			BlockingScanOperation.IgnoreSectionSnapshot => useCase.GetIgnoreSectionSnapshotForRootFolders(
				rootPath,
				["src", "tests"],
				CreateDefaultRules(),
				CreateDefaultRules(),
				effectiveExtensionPolicy: null),
			BlockingScanOperation.EffectiveIgnoreCounts => useCase.GetEffectiveIgnoreOptionCountsForRootFolders(
				rootPath,
				["src", "tests"],
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
				CreateDefaultRules(),
				IgnoreOptionCounts.Empty),
			_ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
		};
	}

	public enum BlockingScanOperation
	{
		ExtensionsWithCounts,
		EffectiveEmptyFolders,
		IgnoreSectionSnapshot,
		EffectiveIgnoreCounts
	}

	private sealed class BlockingLegacyRootScanner(int expectedFolderCalls) : IFileSystemScanner, IDisposable
	{
		private readonly CountdownEvent _allFolderCallsStarted = new(expectedFolderCalls);
		private readonly ManualResetEventSlim _release = new();
		private int _folderCallCount;

		public int FolderCallCount => Volatile.Read(ref _folderCallCount);

		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			SignalAndWait(cancellationToken);
			return new ScanResult<HashSet<string>>(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".folder" },
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		public ScanResult<HashSet<string>> GetRootFileExtensions(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			return new ScanResult<HashSet<string>>(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".root" },
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		public ScanResult<List<string>> GetRootFolderNames(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			return new ScanResult<List<string>>(["src", "tests"], false, false);
		}

		public bool WaitForAllFolderCalls(TimeSpan timeout) => _allFolderCallsStarted.Wait(timeout);

		public void Release() => _release.Set();

		public void Dispose()
		{
			_release.Dispose();
			_allFolderCallsStarted.Dispose();
		}

		private void SignalAndWait(CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref _folderCallCount);
			_allFolderCallsStarted.Signal();
			_release.Wait(cancellationToken);
		}
	}

	private sealed class BlockingAdvancedRootScanner(
		BlockingScanOperation operation,
		int expectedFolderCalls)
		: IFileSystemScanner,
			IFileSystemScannerAdvanced,
			IFileSystemScannerEffectiveEmptyFolderCounter,
			IFileSystemScannerEffectiveIgnoreCountsProvider,
			IFileSystemScannerExtensionPolicySnapshotProvider,
			IDisposable
	{
		private readonly CountdownEvent _allFolderCallsStarted = new(expectedFolderCalls);
		private readonly ManualResetEventSlim _release = new();
		private int _folderCallCount;

		public int FolderCallCount => Volatile.Read(ref _folderCallCount);

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
			CancellationToken cancellationToken = default) => new(["src", "tests"], false, false);

		public ScanResult<ExtensionsScanData> GetExtensionsWithIgnoreOptionCounts(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			BlockWhen(BlockingScanOperation.ExtensionsWithCounts, cancellationToken);
			return new ScanResult<ExtensionsScanData>(
				new ExtensionsScanData(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $".{Path.GetFileName(rootPath)}" },
					new IgnoreOptionCounts(EmptyFolders: 1)),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		public ScanResult<ExtensionsScanData> GetRootFileExtensionsWithIgnoreOptionCounts(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			return new ScanResult<ExtensionsScanData>(
				new ExtensionsScanData(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".root" },
					IgnoreOptionCounts.Empty),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		public ScanResult<int> GetEffectiveEmptyFolderCount(
			string rootPath,
			IReadOnlySet<string> allowedExtensions,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			BlockWhen(BlockingScanOperation.EffectiveEmptyFolders, cancellationToken);
			return new ScanResult<int>(1, RootAccessDenied: false, HadAccessDenied: false);
		}

		public ScanResult<IgnoreOptionCounts> GetEffectiveIgnoreOptionCounts(
			string rootPath,
			IReadOnlySet<string> allowedExtensions,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			BlockWhen(BlockingScanOperation.EffectiveIgnoreCounts, cancellationToken);
			return new ScanResult<IgnoreOptionCounts>(
				new IgnoreOptionCounts(EmptyFolders: 1),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		public ScanResult<IgnoreOptionCounts> GetEffectiveRootFileIgnoreOptionCounts(
			string rootPath,
			IReadOnlySet<string> allowedExtensions,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			return new ScanResult<IgnoreOptionCounts>(IgnoreOptionCounts.Empty, false, false);
		}

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			CancellationToken cancellationToken = default)
		{
			BlockWhen(BlockingScanOperation.IgnoreSectionSnapshot, cancellationToken);
			return CreateSnapshot($".{Path.GetFileName(rootPath)}");
		}

		public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			CancellationToken cancellationToken = default)
		{
			return CreateSnapshot(".root");
		}

		public bool WaitForAllFolderCalls(TimeSpan timeout) => _allFolderCallsStarted.Wait(timeout);

		public void Release() => _release.Set();

		public void Dispose()
		{
			_release.Dispose();
			_allFolderCallsStarted.Dispose();
		}

		private void BlockWhen(BlockingScanOperation expectedOperation, CancellationToken cancellationToken)
		{
			if (operation != expectedOperation)
				return;

			Interlocked.Increment(ref _folderCallCount);
			_allFolderCallsStarted.Signal();
			_release.Wait(cancellationToken);
		}

		private static ScanResult<IgnoreSectionScanData> CreateSnapshot(string extension)
		{
			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { extension },
					new IgnoreOptionCounts(EmptyFolders: 1),
					new IgnoreOptionCounts(EmptyFolders: 1)),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}
	}
}
