namespace DevProjex.Application.UseCases;

public sealed class ScanOptionsUseCase(IFileSystemScanner scanner)
{
	// Optimal parallelism for modern multi-core CPUs (targeting developers with NVMe SSDs)
	private static readonly int MaxParallelism = Math.Max(4, Environment.ProcessorCount);

	public ScanOptionsResult Execute(ScanOptionsRequest request, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		ScanResult<HashSet<string>>? extensions = null;
		ScanResult<List<string>>? rootFolders = null;

		Parallel.Invoke(
			new ParallelOptions
			{
				MaxDegreeOfParallelism = 2,
				CancellationToken = cancellationToken
			},
			() => extensions = scanner.GetExtensions(request.RootPath, request.IgnoreRules, cancellationToken),
			() => rootFolders = scanner.GetRootFolderNames(request.RootPath, request.IgnoreRules, cancellationToken));

		if (extensions is null || rootFolders is null)
			throw new InvalidOperationException("Scan results were not produced.");

		// Convert to List and sort in-place - avoids LINQ intermediate allocations
		var extensionsList = new List<string>(extensions.Value);
		extensionsList.Sort(StringComparer.OrdinalIgnoreCase);

		var rootFoldersList = new List<string>(rootFolders.Value);
		rootFoldersList.Sort(PathComparer.Default);

		return new ScanOptionsResult(
			Extensions: extensionsList,
			RootFolders: rootFoldersList,
			RootAccessDenied: extensions.RootAccessDenied || rootFolders.RootAccessDenied,
			HadAccessDenied: extensions.HadAccessDenied || rootFolders.HadAccessDenied);
	}

	public ScanResult<List<string>> GetRootFolders(
		string rootPath,
		IgnoreRules ignoreRules,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var scan = scanner.GetRootFolderNames(rootPath, ignoreRules, cancellationToken);
		var rootFolders = new List<string>(scan.Value);
		rootFolders.Sort(PathComparer.Default);
		return new ScanResult<List<string>>(rootFolders, scan.RootAccessDenied, scan.HadAccessDenied);
	}

	public ScanResult<HashSet<string>> GetExtensionsForRootFolders(
		string rootPath,
		IReadOnlyCollection<string> rootFolders,
		IgnoreRules ignoreRules,
		CancellationToken cancellationToken = default)
	{
		var scan = GetExtensionsAndIgnoreCountsForRootFolders(
			rootPath,
			rootFolders,
			ignoreRules,
			cancellationToken);

		return new ScanResult<HashSet<string>>(
			new HashSet<string>(scan.Value.Extensions, StringComparer.OrdinalIgnoreCase),
			scan.RootAccessDenied,
			scan.HadAccessDenied);
	}

	public ScanResult<ExtensionsScanData> GetExtensionsAndIgnoreCountsForRootFolders(
		string rootPath,
		IReadOnlyCollection<string> rootFolders,
		IgnoreRules ignoreRules,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var ignoreCounts = IgnoreOptionCounts.Empty;
		var mergeLock = new object();
		var rootAccessDenied = 0;
		var hadAccessDenied = 0;

		// Always scan root-level files, even when no subfolders are selected.
		// This ensures folders containing only files (no subdirectories) work correctly.
		if (scanner is IFileSystemScannerAdvanced advancedScanner)
		{
			var rootFiles = advancedScanner.GetRootFileExtensionsWithIgnoreOptionCounts(rootPath, ignoreRules, cancellationToken);
			foreach (var ext in rootFiles.Value.Extensions)
				extensions.Add(ext);
			ignoreCounts = ignoreCounts.Add(rootFiles.Value.IgnoreOptionCounts);

			if (rootFiles.RootAccessDenied) Interlocked.Exchange(ref rootAccessDenied, 1);
			if (rootFiles.HadAccessDenied) Interlocked.Exchange(ref hadAccessDenied, 1);

			// For very small selections, sequential scan is faster than spinning thread-pool work.
			if (rootFolders.Count > 0)
			{
				if (rootFolders.Count <= 2)
				{
					foreach (var folder in rootFolders)
					{
						cancellationToken.ThrowIfCancellationRequested();

						var folderPath = Path.Combine(rootPath, folder);
						var result = advancedScanner.GetExtensionsWithIgnoreOptionCounts(folderPath, ignoreRules, cancellationToken);

						foreach (var ext in result.Value.Extensions)
							extensions.Add(ext);
						ignoreCounts = ignoreCounts.Add(result.Value.IgnoreOptionCounts);

						if (result.RootAccessDenied) Interlocked.Exchange(ref rootAccessDenied, 1);
						if (result.HadAccessDenied) Interlocked.Exchange(ref hadAccessDenied, 1);
					}
				}
				else
				{
					var parallelOptions = new ParallelOptions
					{
						MaxDegreeOfParallelism = Math.Min(MaxParallelism, rootFolders.Count),
						CancellationToken = cancellationToken
					};

					Parallel.ForEach(
						rootFolders,
						parallelOptions,
						() => new LocalRootSelectionScanAccumulator(),
						(folder, _, localAccumulator) =>
						{
							cancellationToken.ThrowIfCancellationRequested();

							var folderPath = Path.Combine(rootPath, folder);
							var result = advancedScanner.GetExtensionsWithIgnoreOptionCounts(folderPath, ignoreRules, cancellationToken);

							foreach (var ext in result.Value.Extensions)
								localAccumulator.Extensions.Add(ext);
							localAccumulator.IgnoreOptionCounts =
								localAccumulator.IgnoreOptionCounts.Add(result.Value.IgnoreOptionCounts);

							if (result.RootAccessDenied) Interlocked.Exchange(ref rootAccessDenied, 1);
							if (result.HadAccessDenied) Interlocked.Exchange(ref hadAccessDenied, 1);

							return localAccumulator;
						},
						localAccumulator =>
						{
							if (localAccumulator.Extensions.Count == 0 &&
							    localAccumulator.IgnoreOptionCounts == IgnoreOptionCounts.Empty)
								return;

							lock (mergeLock)
							{
								extensions.UnionWith(localAccumulator.Extensions);
								ignoreCounts = ignoreCounts.Add(localAccumulator.IgnoreOptionCounts);
							}
						});
				}
			}
		}
		else
		{
			var rootFiles = scanner.GetRootFileExtensions(rootPath, ignoreRules, cancellationToken);
			foreach (var ext in rootFiles.Value)
				extensions.Add(ext);

			if (rootFiles.RootAccessDenied) Interlocked.Exchange(ref rootAccessDenied, 1);
			if (rootFiles.HadAccessDenied) Interlocked.Exchange(ref hadAccessDenied, 1);

			if (rootFolders.Count > 0)
			{
				if (rootFolders.Count <= 2)
				{
					foreach (var folder in rootFolders)
					{
						cancellationToken.ThrowIfCancellationRequested();

						var folderPath = Path.Combine(rootPath, folder);
						var result = scanner.GetExtensions(folderPath, ignoreRules, cancellationToken);

						foreach (var ext in result.Value)
							extensions.Add(ext);

						if (result.RootAccessDenied) Interlocked.Exchange(ref rootAccessDenied, 1);
						if (result.HadAccessDenied) Interlocked.Exchange(ref hadAccessDenied, 1);
					}
				}
				else
				{
					var parallelOptions = new ParallelOptions
					{
						MaxDegreeOfParallelism = Math.Min(MaxParallelism, rootFolders.Count),
						CancellationToken = cancellationToken
					};

					Parallel.ForEach(
						rootFolders,
						parallelOptions,
						() => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
						(folder, _, localExtensions) =>
						{
							cancellationToken.ThrowIfCancellationRequested();

							var folderPath = Path.Combine(rootPath, folder);
							var result = scanner.GetExtensions(folderPath, ignoreRules, cancellationToken);

							foreach (var ext in result.Value)
								localExtensions.Add(ext);

							if (result.RootAccessDenied) Interlocked.Exchange(ref rootAccessDenied, 1);
							if (result.HadAccessDenied) Interlocked.Exchange(ref hadAccessDenied, 1);

							return localExtensions;
						},
						localExtensions =>
						{
							if (localExtensions.Count == 0)
								return;

							lock (mergeLock)
							{
								extensions.UnionWith(localExtensions);
							}
						});
				}
			}
		}

		return new ScanResult<ExtensionsScanData>(
			new ExtensionsScanData(extensions, ignoreCounts),
			rootAccessDenied == 1,
			hadAccessDenied == 1);
	}

	public ScanResult<int> GetEffectiveEmptyFolderCountForRootFolders(
		string rootPath,
		IReadOnlyCollection<string> rootFolders,
		IReadOnlySet<string> allowedExtensions,
		IgnoreRules ignoreRules,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (rootFolders.Count == 0 || string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
			return new ScanResult<int>(0, RootAccessDenied: false, HadAccessDenied: false);

		if (scanner is not IFileSystemScannerEffectiveEmptyFolderCounter counter)
		{
			var fallback = GetExtensionsAndIgnoreCountsForRootFolders(rootPath, rootFolders, ignoreRules, cancellationToken);
			return new ScanResult<int>(
				fallback.Value.IgnoreOptionCounts.EmptyFolders,
				fallback.RootAccessDenied,
				fallback.HadAccessDenied);
		}

		var emptyFolderCount = 0;
		var rootAccessDenied = 0;
		var hadAccessDenied = 0;
		var mergeLock = new object();

		if (rootFolders.Count <= 2)
		{
			foreach (var folder in rootFolders)
			{
				cancellationToken.ThrowIfCancellationRequested();

				var folderPath = Path.Combine(rootPath, folder);
				var result = counter.GetEffectiveEmptyFolderCount(folderPath, allowedExtensions, ignoreRules, cancellationToken);
				emptyFolderCount += result.Value;

				if (result.RootAccessDenied)
					Interlocked.Exchange(ref rootAccessDenied, 1);
				if (result.HadAccessDenied)
					Interlocked.Exchange(ref hadAccessDenied, 1);
			}
		}
		else
		{
			var parallelOptions = new ParallelOptions
			{
				MaxDegreeOfParallelism = Math.Min(MaxParallelism, rootFolders.Count),
				CancellationToken = cancellationToken
			};

			Parallel.ForEach(
				rootFolders,
				parallelOptions,
				() => 0,
				(folder, _, localCount) =>
				{
					cancellationToken.ThrowIfCancellationRequested();

					var folderPath = Path.Combine(rootPath, folder);
					var result = counter.GetEffectiveEmptyFolderCount(folderPath, allowedExtensions, ignoreRules, cancellationToken);
					if (result.RootAccessDenied)
						Interlocked.Exchange(ref rootAccessDenied, 1);
					if (result.HadAccessDenied)
						Interlocked.Exchange(ref hadAccessDenied, 1);

					return localCount + result.Value;
				},
				localCount =>
				{
					if (localCount == 0)
						return;

					lock (mergeLock)
					{
						emptyFolderCount += localCount;
					}
				});
		}

		return new ScanResult<int>(emptyFolderCount, rootAccessDenied == 1, hadAccessDenied == 1);
	}

	public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshotForRootFolders(
		string rootPath,
		IReadOnlyCollection<string> rootFolders,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IReadOnlySet<string>? effectiveAllowedExtensions,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (scanner is not IFileSystemScannerIgnoreSectionSnapshotProvider provider)
		{
			// Keep the legacy fallback behavior exact. The optimized snapshot path must stay
			// semantically interchangeable with the older raw-scan + effective-scan pipeline.
			var rawScan = GetExtensionsAndIgnoreCountsForRootFolders(
				rootPath,
				rootFolders,
				extensionDiscoveryRules,
				cancellationToken);
			var resolvedAllowedExtensions = effectiveAllowedExtensions ??
			                                BuildAllDiscoveredExtensionsSet(rawScan.Value.Extensions);
			var effectiveScan = GetEffectiveIgnoreOptionCountsForRootFolders(
				rootPath,
				rootFolders,
				resolvedAllowedExtensions,
				effectiveRules,
				rawScan.Value.IgnoreOptionCounts,
				cancellationToken);

			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					rawScan.Value.Extensions,
					rawScan.Value.IgnoreOptionCounts,
					effectiveScan.Value),
				rawScan.RootAccessDenied || effectiveScan.RootAccessDenied,
				rawScan.HadAccessDenied || effectiveScan.HadAccessDenied);
		}

		var aggregatedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var rawCounts = IgnoreOptionCounts.Empty;
		var effectiveCounts = IgnoreOptionCounts.Empty;
		var rootAccessDenied = 0;
		var hadAccessDenied = 0;
		var mergeLock = new object();

		// Root-level files participate in ignore availability even when no subfolders are selected.
		// Keeping them in the same snapshot guarantees that extension availability and live counts
		// come from one coherent filesystem view.
		var rootFileSnapshot = provider.GetRootFileIgnoreSectionSnapshot(
			rootPath,
			extensionDiscoveryRules,
			effectiveRules,
			effectiveAllowedExtensions,
			cancellationToken);
		aggregatedExtensions.UnionWith(rootFileSnapshot.Value.Extensions);
		rawCounts = rawCounts.Add(rootFileSnapshot.Value.RawIgnoreOptionCounts);
		effectiveCounts = effectiveCounts.Add(rootFileSnapshot.Value.EffectiveIgnoreOptionCounts);
		if (rootFileSnapshot.RootAccessDenied)
			Interlocked.Exchange(ref rootAccessDenied, 1);
		if (rootFileSnapshot.HadAccessDenied)
			Interlocked.Exchange(ref hadAccessDenied, 1);

		if (rootFolders.Count > 0)
		{
			if (rootFolders.Count <= 2)
			{
				foreach (var folder in rootFolders)
				{
					cancellationToken.ThrowIfCancellationRequested();

					var folderPath = Path.Combine(rootPath, folder);
					var snapshot = provider.GetIgnoreSectionSnapshot(
						folderPath,
						extensionDiscoveryRules,
						effectiveRules,
						effectiveAllowedExtensions,
						cancellationToken);

					aggregatedExtensions.UnionWith(snapshot.Value.Extensions);
					rawCounts = rawCounts.Add(snapshot.Value.RawIgnoreOptionCounts);
					effectiveCounts = effectiveCounts.Add(snapshot.Value.EffectiveIgnoreOptionCounts);

					if (snapshot.RootAccessDenied)
						Interlocked.Exchange(ref rootAccessDenied, 1);
					if (snapshot.HadAccessDenied)
						Interlocked.Exchange(ref hadAccessDenied, 1);
				}
			}
			else
			{
				var parallelOptions = new ParallelOptions
				{
					MaxDegreeOfParallelism = Math.Min(MaxParallelism, rootFolders.Count),
					CancellationToken = cancellationToken
				};

				Parallel.ForEach(
					rootFolders,
					parallelOptions,
					() => new LocalIgnoreSectionSnapshotAccumulator(),
					(folder, _, localAccumulator) =>
					{
						cancellationToken.ThrowIfCancellationRequested();

						var folderPath = Path.Combine(rootPath, folder);
						var snapshot = provider.GetIgnoreSectionSnapshot(
							folderPath,
							extensionDiscoveryRules,
							effectiveRules,
							effectiveAllowedExtensions,
							cancellationToken);

						localAccumulator.Extensions.UnionWith(snapshot.Value.Extensions);
						localAccumulator.RawIgnoreOptionCounts =
							localAccumulator.RawIgnoreOptionCounts.Add(snapshot.Value.RawIgnoreOptionCounts);
						localAccumulator.EffectiveIgnoreOptionCounts =
							localAccumulator.EffectiveIgnoreOptionCounts.Add(snapshot.Value.EffectiveIgnoreOptionCounts);

						if (snapshot.RootAccessDenied)
							Interlocked.Exchange(ref rootAccessDenied, 1);
						if (snapshot.HadAccessDenied)
							Interlocked.Exchange(ref hadAccessDenied, 1);

						return localAccumulator;
					},
					localAccumulator =>
					{
						if (localAccumulator.Extensions.Count == 0 &&
						    localAccumulator.RawIgnoreOptionCounts == IgnoreOptionCounts.Empty &&
						    localAccumulator.EffectiveIgnoreOptionCounts == IgnoreOptionCounts.Empty)
						{
							return;
						}

						lock (mergeLock)
						{
							aggregatedExtensions.UnionWith(localAccumulator.Extensions);
							rawCounts = rawCounts.Add(localAccumulator.RawIgnoreOptionCounts);
							effectiveCounts = effectiveCounts.Add(localAccumulator.EffectiveIgnoreOptionCounts);
						}
					});
			}
		}

		return new ScanResult<IgnoreSectionScanData>(
			new IgnoreSectionScanData(
				aggregatedExtensions,
				rawCounts,
				effectiveCounts),
			rootAccessDenied == 1,
			hadAccessDenied == 1);
	}

	public ScanResult<IgnoreOptionCounts> GetEffectiveIgnoreOptionCountsForRootFolders(
		string rootPath,
		IReadOnlyCollection<string> rootFolders,
		IReadOnlySet<string> allowedExtensions,
		IgnoreRules ignoreRules,
		IgnoreOptionCounts rawCounts,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (scanner is not IFileSystemScannerEffectiveIgnoreCountsProvider counter)
		{
			var effectiveEmptyFolderCount = GetEffectiveEmptyFolderCountForRootFolders(
				rootPath,
				rootFolders,
				allowedExtensions,
				ignoreRules,
				cancellationToken);

			return new ScanResult<IgnoreOptionCounts>(
				rawCounts with { EmptyFolders = Math.Max(0, effectiveEmptyFolderCount.Value) },
				effectiveEmptyFolderCount.RootAccessDenied,
				effectiveEmptyFolderCount.HadAccessDenied);
		}

		var effectiveCounts = IgnoreOptionCounts.Empty;
		var rootAccessDenied = 0;
		var hadAccessDenied = 0;
		var mergeLock = new object();

		var rootFileCounts = counter.GetEffectiveRootFileIgnoreOptionCounts(
			rootPath,
			allowedExtensions,
			ignoreRules,
			cancellationToken);
		effectiveCounts = effectiveCounts.Add(rootFileCounts.Value);
		if (rootFileCounts.RootAccessDenied)
			Interlocked.Exchange(ref rootAccessDenied, 1);
		if (rootFileCounts.HadAccessDenied)
			Interlocked.Exchange(ref hadAccessDenied, 1);

		if (rootFolders.Count > 0)
		{
			if (rootFolders.Count <= 2)
			{
				foreach (var folder in rootFolders)
				{
					cancellationToken.ThrowIfCancellationRequested();

					var folderPath = Path.Combine(rootPath, folder);
					var result = counter.GetEffectiveIgnoreOptionCounts(
						folderPath,
						allowedExtensions,
						ignoreRules,
						cancellationToken);
					effectiveCounts = effectiveCounts.Add(result.Value);

					if (result.RootAccessDenied)
						Interlocked.Exchange(ref rootAccessDenied, 1);
					if (result.HadAccessDenied)
						Interlocked.Exchange(ref hadAccessDenied, 1);
				}
			}
			else
			{
				var parallelOptions = new ParallelOptions
				{
					MaxDegreeOfParallelism = Math.Min(MaxParallelism, rootFolders.Count),
					CancellationToken = cancellationToken
				};

				Parallel.ForEach(
					rootFolders,
					parallelOptions,
					() => IgnoreOptionCounts.Empty,
					(folder, _, localCounts) =>
					{
						cancellationToken.ThrowIfCancellationRequested();

						var folderPath = Path.Combine(rootPath, folder);
						var result = counter.GetEffectiveIgnoreOptionCounts(
							folderPath,
							allowedExtensions,
							ignoreRules,
							cancellationToken);
						if (result.RootAccessDenied)
							Interlocked.Exchange(ref rootAccessDenied, 1);
						if (result.HadAccessDenied)
							Interlocked.Exchange(ref hadAccessDenied, 1);

						return localCounts.Add(result.Value);
					},
					localCounts =>
					{
						if (localCounts == IgnoreOptionCounts.Empty)
							return;

						lock (mergeLock)
						{
							effectiveCounts = effectiveCounts.Add(localCounts);
						}
					});
			}
		}

		return new ScanResult<IgnoreOptionCounts>(
			rawCounts with
			{
				HiddenFolders = effectiveCounts.HiddenFolders,
				HiddenFiles = effectiveCounts.HiddenFiles,
				DotFolders = effectiveCounts.DotFolders,
				DotFiles = effectiveCounts.DotFiles,
				EmptyFolders = Math.Max(0, effectiveCounts.EmptyFolders),
				ExtensionlessFiles = effectiveCounts.ExtensionlessFiles,
				EmptyFiles = effectiveCounts.EmptyFiles
			},
			rootAccessDenied == 1,
			hadAccessDenied == 1);
	}

	public bool CanReadRoot(string rootPath) => scanner.CanReadRoot(rootPath);

	private sealed class LocalRootSelectionScanAccumulator
	{
		public HashSet<string> Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);
		public IgnoreOptionCounts IgnoreOptionCounts { get; set; } = IgnoreOptionCounts.Empty;
	}

	private sealed class LocalIgnoreSectionSnapshotAccumulator
	{
		public HashSet<string> Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);
		public IgnoreOptionCounts RawIgnoreOptionCounts { get; set; } = IgnoreOptionCounts.Empty;
		public IgnoreOptionCounts EffectiveIgnoreOptionCounts { get; set; } = IgnoreOptionCounts.Empty;
	}

	private static HashSet<string> BuildAllDiscoveredExtensionsSet(IReadOnlyCollection<string> discoveredEntries)
	{
		var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in discoveredEntries)
		{
			var extension = Path.GetExtension(entry);
			if (!string.IsNullOrWhiteSpace(extension))
				extensions.Add(extension);
		}

		return extensions;
	}

}
