namespace DevProjex.Application.UseCases;

public sealed class ScanOptionsUseCase(IFileSystemScanner scanner)
{
	// Keep local SSD scans fast while avoiding unbounded fan-out on very wide projects.
	private static readonly int LocalStorageMaxParallelism = Math.Clamp(Environment.ProcessorCount, 4, 16);

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
						MaxDegreeOfParallelism = ResolveMaxDegreeOfParallelism(rootPath, rootFolders.Count),
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
						MaxDegreeOfParallelism = ResolveMaxDegreeOfParallelism(rootPath, rootFolders.Count),
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
				MaxDegreeOfParallelism = ResolveMaxDegreeOfParallelism(rootPath, rootFolders.Count),
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
		bool includeDirectoryToggleProbeRoots = false,
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
				includeDirectoryToggleProbeRoots,
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
					MaxDegreeOfParallelism = ResolveMaxDegreeOfParallelism(rootPath, rootFolders.Count),
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

		if (includeDirectoryToggleProbeRoots)
		{
			var rootCandidateCounts = GetRootDirectoryToggleCandidateCounts(
				rootPath,
				rootFolders,
				effectiveRules,
				cancellationToken);
			effectiveCounts = effectiveCounts.Add(rootCandidateCounts.Value);
			if (rootCandidateCounts.RootAccessDenied)
				Interlocked.Exchange(ref rootAccessDenied, 1);
			if (rootCandidateCounts.HadAccessDenied)
				Interlocked.Exchange(ref hadAccessDenied, 1);
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
		bool includeDirectoryToggleProbeRoots = false,
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
					MaxDegreeOfParallelism = ResolveMaxDegreeOfParallelism(rootPath, rootFolders.Count),
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

		if (includeDirectoryToggleProbeRoots)
		{
			var rootCandidateCounts = GetRootDirectoryToggleCandidateCounts(
				rootPath,
				rootFolders,
				ignoreRules,
				cancellationToken);
			effectiveCounts = effectiveCounts.Add(rootCandidateCounts.Value);
			if (rootCandidateCounts.RootAccessDenied)
				Interlocked.Exchange(ref rootAccessDenied, 1);
			if (rootCandidateCounts.HadAccessDenied)
				Interlocked.Exchange(ref hadAccessDenied, 1);
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

	private static ScanResult<IgnoreOptionCounts> GetRootDirectoryToggleCandidateCounts(
		string rootPath,
		IReadOnlyCollection<string> selectedRootFolders,
		IgnoreRules effectiveRules,
		CancellationToken cancellationToken)
	{
		if (!effectiveRules.IgnoreDotFolders && !effectiveRules.IgnoreHiddenFolders)
			return new ScanResult<IgnoreOptionCounts>(IgnoreOptionCounts.Empty, RootAccessDenied: false, HadAccessDenied: false);

		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
			return new ScanResult<IgnoreOptionCounts>(IgnoreOptionCounts.Empty, RootAccessDenied: false, HadAccessDenied: false);

		var selected = new HashSet<string>(selectedRootFolders, PathComparer.Default);
		var hiddenFolders = 0;
		var dotFolders = 0;

		try
		{
			foreach (var directoryPath in Directory.EnumerateDirectories(rootPath, "*", SearchOption.TopDirectoryOnly))
			{
				cancellationToken.ThrowIfCancellationRequested();

				var name = Path.GetFileName(directoryPath);
				if (string.IsNullOrWhiteSpace(name) || selected.Contains(name))
					continue;
				if (IsReparsePointDirectory(directoryPath))
					continue;

				if (IsSuppressedByNonDirectoryToggleRule(directoryPath, name, effectiveRules))
					continue;

				if (effectiveRules.IgnoreDotFolders && name.StartsWith(".", StringComparison.Ordinal))
				{
					var visible = HasVisibleContentForDirectoryToggleCandidate(
						directoryPath,
						effectiveRules with
						{
							IgnoreDotFolders = false,
							IgnoreEmptyFolders = true
						},
						cancellationToken);
					if (visible.RootAccessDenied)
						return new ScanResult<IgnoreOptionCounts>(
							new IgnoreOptionCounts(HiddenFolders: hiddenFolders, DotFolders: dotFolders),
							RootAccessDenied: true,
							HadAccessDenied: true);
					if (visible.HadAccessDenied || visible.Value)
						dotFolders++;
				}

				if (effectiveRules.IgnoreHiddenFolders && HasHiddenAttribute(directoryPath))
				{
					var visible = HasVisibleContentForDirectoryToggleCandidate(
						directoryPath,
						effectiveRules with
						{
							IgnoreHiddenFolders = false,
							IgnoreEmptyFolders = true
						},
						cancellationToken);
					if (visible.RootAccessDenied)
						return new ScanResult<IgnoreOptionCounts>(
							new IgnoreOptionCounts(HiddenFolders: hiddenFolders, DotFolders: dotFolders),
							RootAccessDenied: true,
							HadAccessDenied: true);
					if (visible.HadAccessDenied || visible.Value)
						hiddenFolders++;
				}
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (UnauthorizedAccessException)
		{
			return new ScanResult<IgnoreOptionCounts>(
				new IgnoreOptionCounts(HiddenFolders: hiddenFolders, DotFolders: dotFolders),
				RootAccessDenied: true,
				HadAccessDenied: true);
		}
		catch
		{
			return new ScanResult<IgnoreOptionCounts>(
				new IgnoreOptionCounts(HiddenFolders: hiddenFolders, DotFolders: dotFolders),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		return new ScanResult<IgnoreOptionCounts>(
			new IgnoreOptionCounts(HiddenFolders: hiddenFolders, DotFolders: dotFolders),
			RootAccessDenied: false,
			HadAccessDenied: false);
	}

	private static bool IsSuppressedByNonDirectoryToggleRule(
		string directoryPath,
		string name,
		IgnoreRules rules)
	{
		if (rules.IsSmartIgnoredDirectory(directoryPath, name))
			return true;

		if (!rules.UseGitIgnore)
			return false;

		var gitIgnore = rules.EvaluateGitIgnore(directoryPath, isDirectory: true, name);
		return gitIgnore.IsIgnored && !gitIgnore.ShouldTraverseIgnoredDirectory;
	}

	private static ScanResult<bool> HasVisibleContentForDirectoryToggleCandidate(
		string rootPath,
		IgnoreRules rules,
		CancellationToken cancellationToken)
	{
		if (IsDirectorySuppressedByRules(rootPath, Path.GetFileName(rootPath), rules))
			return new ScanResult<bool>(false, RootAccessDenied: false, HadAccessDenied: false);

		var hadAccessDenied = false;
		var pending = new Stack<string>();
		pending.Push(rootPath);

		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var currentPath = pending.Pop();

			try
			{
				foreach (var filePath in Directory.EnumerateFiles(currentPath, "*", SearchOption.TopDirectoryOnly))
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (IsFileVisibleForDirectoryToggleCandidate(filePath, rules))
						return new ScanResult<bool>(true, RootAccessDenied: false, HadAccessDenied: hadAccessDenied);
				}

				foreach (var directoryPath in Directory.EnumerateDirectories(currentPath, "*", SearchOption.TopDirectoryOnly))
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (IsReparsePointDirectory(directoryPath))
						continue;

					var name = Path.GetFileName(directoryPath);
					if (string.IsNullOrWhiteSpace(name))
						continue;
					if (IsDirectorySuppressedByRules(directoryPath, name, rules))
						continue;

					pending.Push(directoryPath);
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (UnauthorizedAccessException)
			{
				hadAccessDenied = true;
				return new ScanResult<bool>(true, RootAccessDenied: currentPath == rootPath, HadAccessDenied: true);
			}
			catch
			{
				// Best-effort: unreadable children should not destabilize ignore availability.
			}
		}

		return new ScanResult<bool>(false, RootAccessDenied: false, HadAccessDenied: hadAccessDenied);
	}

	private static bool IsDirectorySuppressedByRules(
		string directoryPath,
		string name,
		IgnoreRules rules)
	{
		if (rules.IsSmartIgnoredDirectory(directoryPath, name))
			return true;

		if (rules.UseGitIgnore)
		{
			var gitIgnore = rules.EvaluateGitIgnore(directoryPath, isDirectory: true, name);
			if (gitIgnore.IsIgnored && !gitIgnore.ShouldTraverseIgnoredDirectory)
				return true;
		}

		if (rules.IgnoreDotFolders && name.StartsWith(".", StringComparison.Ordinal))
			return true;

		if (rules.IgnoreHiddenFolders && HasHiddenAttribute(directoryPath))
			return true;

		return false;
	}

	private static bool IsFileVisibleForDirectoryToggleCandidate(string filePath, IgnoreRules rules)
	{
		var name = Path.GetFileName(filePath);
		if (string.IsNullOrWhiteSpace(name))
			return false;

		if (rules.UseGitIgnore && rules.EvaluateGitIgnore(filePath, isDirectory: false, name).IsIgnored)
			return false;

		if (rules.IsSmartIgnoredFile(filePath, name, rules.ShouldApplySmartIgnore(filePath, isDirectory: false)))
			return false;

		if (rules.IgnoreDotFiles && name.StartsWith(".", StringComparison.Ordinal))
			return false;

		if (rules.IgnoreHiddenFiles && HasHiddenAttribute(filePath))
			return false;

		if (rules.IgnoreEmptyFiles && GetFileLength(filePath) == 0)
			return false;

		if (rules.IgnoreExtensionlessFiles && IsExtensionlessFileName(name))
			return false;

		return true;
	}

	private static bool IsReparsePointDirectory(string path)
	{
		try
		{
			return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
		}
		catch
		{
			return true;
		}
	}

	private static int ResolveMaxDegreeOfParallelism(string rootPath, int workItemCount)
	{
		if (workItemCount <= 0)
			return 1;

		var storageCap = IsLikelySlowStorageRoot(rootPath)
			? 2
			: LocalStorageMaxParallelism;
		return Math.Max(1, Math.Min(storageCap, workItemCount));
	}

	private static bool IsLikelySlowStorageRoot(string rootPath)
	{
		try
		{
			var pathRoot = Path.GetPathRoot(rootPath);
			if (string.IsNullOrWhiteSpace(pathRoot))
				return false;

			if (OperatingSystem.IsWindows() &&
			    pathRoot.StartsWith(@"\\", StringComparison.Ordinal))
			{
				return true;
			}

			return new DriveInfo(pathRoot).DriveType == DriveType.Network;
		}
		catch
		{
			return false;
		}
	}

	private static bool HasHiddenAttribute(string path)
	{
		try
		{
			return File.GetAttributes(path).HasFlag(FileAttributes.Hidden);
		}
		catch
		{
			return false;
		}
	}

	private static long GetFileLength(string path)
	{
		try
		{
			return new FileInfo(path).Length;
		}
		catch
		{
			return 0;
		}
	}

	private static bool IsExtensionlessFileName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return false;

		var dotIndex = name.AsSpan().LastIndexOf('.');
		if (dotIndex <= 0)
			return dotIndex != 0;

		return dotIndex == name.Length - 1;
	}

}
