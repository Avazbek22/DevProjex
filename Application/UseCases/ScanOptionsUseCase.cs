namespace DevProjex.Application.UseCases;

public sealed class ScanOptionsUseCase(IFileSystemScanner scanner)
{
	public ScanOptionsResult Execute(ScanOptionsRequest request, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		ScanResult<HashSet<string>>? extensions = null;
		ScanResult<List<string>>? rootFolders = null;

		Parallel.Invoke(
			new ParallelOptions
			{
				MaxDegreeOfParallelism = ScanParallelismPolicy.MaxDegreeOfParallelism,
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
		var selectedRootPaths = ResolveSelectedRootFolderPaths(rootPath, rootFolders);

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

			if (selectedRootPaths.Count > 0)
			{
				var parallelOptions = ScanParallelismPolicy.CreateOptions(cancellationToken);

				Parallel.ForEach(
					selectedRootPaths,
					parallelOptions,
					() => new LocalRootSelectionScanAccumulator(),
					(folderPath, _, localAccumulator) =>
					{
						parallelOptions.CancellationToken.ThrowIfCancellationRequested();

						var result = advancedScanner.GetExtensionsWithIgnoreOptionCounts(
							folderPath,
							ignoreRules,
							parallelOptions.CancellationToken);

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
		else
		{
			var rootFiles = scanner.GetRootFileExtensions(rootPath, ignoreRules, cancellationToken);
			foreach (var ext in rootFiles.Value)
				extensions.Add(ext);

			if (rootFiles.RootAccessDenied) Interlocked.Exchange(ref rootAccessDenied, 1);
			if (rootFiles.HadAccessDenied) Interlocked.Exchange(ref hadAccessDenied, 1);

			if (selectedRootPaths.Count > 0)
			{
				var parallelOptions = ScanParallelismPolicy.CreateOptions(cancellationToken);

				Parallel.ForEach(
					selectedRootPaths,
					parallelOptions,
					() => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
					(folderPath, _, localExtensions) =>
					{
						parallelOptions.CancellationToken.ThrowIfCancellationRequested();

						var result = scanner.GetExtensions(
							folderPath,
							ignoreRules,
							parallelOptions.CancellationToken);

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

		var selectedRootPaths = ResolveSelectedRootFolderPaths(rootPath, rootFolders);
		if (selectedRootPaths.Count == 0)
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

		var parallelOptions = ScanParallelismPolicy.CreateOptions(cancellationToken);

		Parallel.ForEach(
			selectedRootPaths,
			parallelOptions,
			() => 0,
			(folderPath, _, localCount) =>
			{
				parallelOptions.CancellationToken.ThrowIfCancellationRequested();

				var result = counter.GetEffectiveEmptyFolderCount(
					folderPath,
					allowedExtensions,
					ignoreRules,
					parallelOptions.CancellationToken);
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

		return new ScanResult<int>(emptyFolderCount, rootAccessDenied == 1, hadAccessDenied == 1);
	}

	public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshotForRootFolders(
		string rootPath,
		IReadOnlyCollection<string> rootFolders,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IReadOnlySet<string>? effectiveAllowedExtensions,
		bool includeDirectoryToggleProbeRoots = false,
		CancellationToken cancellationToken = default,
		bool includeControllerImpactProbeRoots = false)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (scanner is IFileSystemScannerRootSelectionSnapshotProvider rootSelectionProvider)
		{
			var effectiveExtensionPolicy = effectiveAllowedExtensions is null
				? null
				: new ExtensionSetInclusionPolicy(effectiveAllowedExtensions);
			return rootSelectionProvider.GetIgnoreSectionSnapshotForRootSelection(
				rootPath,
				rootFolders,
				extensionDiscoveryRules,
				effectiveRules,
				effectiveExtensionPolicy,
				includeDirectoryToggleProbeRoots,
				cancellationToken,
				includeControllerImpactProbeRoots);
		}

		if (scanner is not IFileSystemScannerIgnoreSectionSnapshotProvider provider)
		{
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

		return GetIgnoreSectionSnapshotForRootFoldersCore(
			rootPath,
			rootFolders,
			extensionDiscoveryRules,
			effectiveRules,
			effectiveAllowedExtensions,
			includeDirectoryToggleProbeRoots,
			cancellationToken,
			provider.GetRootFileIgnoreSectionSnapshot,
			provider.GetIgnoreSectionSnapshot);
	}

	public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshotForRootFolders(
		string rootPath,
		IReadOnlyCollection<string> rootFolders,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		bool includeDirectoryToggleProbeRoots = false,
		CancellationToken cancellationToken = default,
		bool includeControllerImpactProbeRoots = false)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (scanner is IFileSystemScannerRootSelectionSnapshotProvider rootSelectionProvider)
		{
			return rootSelectionProvider.GetIgnoreSectionSnapshotForRootSelection(
				rootPath,
				rootFolders,
				extensionDiscoveryRules,
				effectiveRules,
				effectiveExtensionPolicy,
				includeDirectoryToggleProbeRoots,
				cancellationToken,
				includeControllerImpactProbeRoots);
		}

		if (scanner is not IFileSystemScannerExtensionPolicySnapshotProvider policyProvider)
		{
			// Keep the legacy fallback behavior exact. The optimized snapshot path must stay
			// semantically interchangeable with the older raw-scan + effective-scan pipeline.
			var rawScan = GetExtensionsAndIgnoreCountsForRootFolders(
				rootPath,
				rootFolders,
				extensionDiscoveryRules,
				cancellationToken);
			var resolvedAllowedExtensions = BuildAllowedExtensionsSet(
				rawScan.Value.Extensions,
				effectiveExtensionPolicy);
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

		return GetIgnoreSectionSnapshotForRootFoldersCore(
			rootPath,
			rootFolders,
			extensionDiscoveryRules,
			effectiveRules,
			effectiveExtensionPolicy,
			includeDirectoryToggleProbeRoots,
			cancellationToken,
			policyProvider.GetRootFileIgnoreSectionSnapshot,
			policyProvider.GetIgnoreSectionSnapshot);
	}

	public ScanResult<ProjectWorkspaceScanSnapshot> GetProjectWorkspaceSnapshotForRootFolders(
		string rootPath,
		IReadOnlyCollection<string> rootFolders,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		bool includeDirectoryToggleProbeRoots = false,
		CancellationToken cancellationToken = default,
		bool includeControllerImpactProbeRoots = false)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (scanner is IFileSystemScannerProjectWorkspaceSnapshotProvider provider)
		{
			return provider.GetProjectWorkspaceSnapshotForRootSelection(
				rootPath,
				rootFolders,
				extensionDiscoveryRules,
				effectiveRules,
				effectiveExtensionPolicy,
				includeDirectoryToggleProbeRoots,
				cancellationToken,
				includeControllerImpactProbeRoots);
		}

		// Older scanner implementations can still participate through the stable
		// ignore-section contract. They simply do not provide a reusable tree inventory.
		var ignoreSection = GetIgnoreSectionSnapshotForRootFolders(
			rootPath,
			rootFolders,
			extensionDiscoveryRules,
			effectiveRules,
			effectiveExtensionPolicy,
			includeDirectoryToggleProbeRoots,
			cancellationToken,
			includeControllerImpactProbeRoots);
		return new ScanResult<ProjectWorkspaceScanSnapshot>(
			new ProjectWorkspaceScanSnapshot(ignoreSection.Value, TreeInventory: null),
			ignoreSection.RootAccessDenied,
			ignoreSection.HadAccessDenied);
	}

	private ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshotForRootFoldersCore<TPolicy>(
		string rootPath,
		IReadOnlyCollection<string> rootFolders,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		TPolicy effectiveExtensionPolicy,
		bool includeDirectoryToggleProbeRoots,
		CancellationToken cancellationToken,
		Func<string, IgnoreRules, IgnoreRules, TPolicy, CancellationToken, ScanResult<IgnoreSectionScanData>> getRootFileSnapshot,
		Func<string, IgnoreRules, IgnoreRules, TPolicy, CancellationToken, ScanResult<IgnoreSectionScanData>> getFolderSnapshot)
	{
		var aggregatedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var rawCounts = IgnoreOptionCounts.Empty;
		var effectiveCounts = IgnoreOptionCounts.Empty;
		var controllerImpactCounts = IgnoreControllerImpactCounts.Empty;
		var rootAccessDenied = 0;
		var hadAccessDenied = 0;
		var mergeLock = new object();
		var selectedRootPaths = ResolveSelectedRootFolderPaths(rootPath, rootFolders);

		// Root-level files participate in ignore availability even when no subfolders are selected.
		// Keeping them in the same snapshot guarantees that extension availability and live counts
		// come from one coherent filesystem view.
		var rootFileSnapshot = getRootFileSnapshot(
			rootPath,
			extensionDiscoveryRules,
			effectiveRules,
			effectiveExtensionPolicy,
			cancellationToken);
		aggregatedExtensions.UnionWith(rootFileSnapshot.Value.Extensions);
		rawCounts = rawCounts.Add(rootFileSnapshot.Value.RawIgnoreOptionCounts);
		effectiveCounts = effectiveCounts.Add(rootFileSnapshot.Value.EffectiveIgnoreOptionCounts);
		controllerImpactCounts = controllerImpactCounts.Add(rootFileSnapshot.Value.ControllerImpactCounts);
		if (rootFileSnapshot.RootAccessDenied)
			Interlocked.Exchange(ref rootAccessDenied, 1);
		if (rootFileSnapshot.HadAccessDenied)
			Interlocked.Exchange(ref hadAccessDenied, 1);

		if (selectedRootPaths.Count > 0)
		{
			var parallelOptions = ScanParallelismPolicy.CreateOptions(cancellationToken);

			Parallel.ForEach(
				selectedRootPaths,
				parallelOptions,
				() => new LocalIgnoreSectionSnapshotAccumulator(),
				(folderPath, _, localAccumulator) =>
				{
					parallelOptions.CancellationToken.ThrowIfCancellationRequested();

					var snapshot = getFolderSnapshot(
						folderPath,
						extensionDiscoveryRules,
						effectiveRules,
						effectiveExtensionPolicy,
						parallelOptions.CancellationToken);

					localAccumulator.Extensions.UnionWith(snapshot.Value.Extensions);
					localAccumulator.RawIgnoreOptionCounts =
						localAccumulator.RawIgnoreOptionCounts.Add(snapshot.Value.RawIgnoreOptionCounts);
					localAccumulator.EffectiveIgnoreOptionCounts =
						localAccumulator.EffectiveIgnoreOptionCounts.Add(snapshot.Value.EffectiveIgnoreOptionCounts);
					localAccumulator.ControllerImpactCounts =
						localAccumulator.ControllerImpactCounts.Add(snapshot.Value.ControllerImpactCounts);

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
					    localAccumulator.EffectiveIgnoreOptionCounts == IgnoreOptionCounts.Empty &&
					    localAccumulator.ControllerImpactCounts == IgnoreControllerImpactCounts.Empty)
					{
						return;
					}

					lock (mergeLock)
					{
						aggregatedExtensions.UnionWith(localAccumulator.Extensions);
						rawCounts = rawCounts.Add(localAccumulator.RawIgnoreOptionCounts);
						effectiveCounts = effectiveCounts.Add(localAccumulator.EffectiveIgnoreOptionCounts);
						controllerImpactCounts = controllerImpactCounts.Add(localAccumulator.ControllerImpactCounts);
					}
				});
		}

		if (includeDirectoryToggleProbeRoots)
		{
			var rootCandidateCounts = GetRootDirectoryToggleCandidateCounts(
				rootPath,
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
				effectiveCounts,
				controllerImpactCounts),
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
		var selectedRootPaths = ResolveSelectedRootFolderPaths(rootPath, rootFolders);

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

		if (selectedRootPaths.Count > 0)
		{
			var parallelOptions = ScanParallelismPolicy.CreateOptions(cancellationToken);

			Parallel.ForEach(
				selectedRootPaths,
				parallelOptions,
				() => IgnoreOptionCounts.Empty,
				(folderPath, _, localCounts) =>
				{
					parallelOptions.CancellationToken.ThrowIfCancellationRequested();

					var result = counter.GetEffectiveIgnoreOptionCounts(
						folderPath,
						allowedExtensions,
						ignoreRules,
						parallelOptions.CancellationToken);
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

		if (includeDirectoryToggleProbeRoots)
		{
			var rootCandidateCounts = GetRootDirectoryToggleCandidateCounts(
				rootPath,
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

	private static List<string> ResolveSelectedRootFolderPaths(
		string rootPath,
		IReadOnlyCollection<string> selectedRootFolders)
	{
		var paths = new List<string>(selectedRootFolders.Count);
		if (selectedRootFolders.Count == 0 || string.IsNullOrWhiteSpace(rootPath))
			return paths;

		if (!Directory.Exists(rootPath))
			return BuildUncheckedSelectedRootFolderPaths(rootPath, selectedRootFolders);

		string normalizedRootPath;
		try
		{
			normalizedRootPath = PathUtility.Normalize(rootPath);
		}
		catch
		{
			return paths;
		}

		foreach (var selectedRootFolder in selectedRootFolders)
		{
			if (string.IsNullOrWhiteSpace(selectedRootFolder) || Path.IsPathRooted(selectedRootFolder))
				continue;

			string fullPath;
			try
			{
				fullPath = PathUtility.Normalize(Path.Combine(rootPath, selectedRootFolder));
			}
			catch
			{
				continue;
			}

			// Stale profile/UI selections must not escape the opened root or follow symlink roots.
			if (PathComparer.Default.Equals(fullPath, normalizedRootPath) ||
			    !PathUtility.IsPathInside(fullPath, normalizedRootPath) ||
			    !Directory.Exists(fullPath) ||
			    IsReparsePointDirectory(fullPath))
			{
				continue;
			}

			paths.Add(fullPath);
		}

		return paths;
	}

	private static List<string> BuildUncheckedSelectedRootFolderPaths(
		string rootPath,
		IReadOnlyCollection<string> selectedRootFolders)
	{
		var paths = new List<string>(selectedRootFolders.Count);
		foreach (var selectedRootFolder in selectedRootFolders)
		{
			if (string.IsNullOrWhiteSpace(selectedRootFolder) || Path.IsPathRooted(selectedRootFolder))
				continue;

			try
			{
				paths.Add(Path.Combine(rootPath, selectedRootFolder));
			}
			catch
			{
				// Invalid stale selections are ignored even when the root itself is not available.
			}
		}

		return paths;
	}

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
		public IgnoreControllerImpactCounts ControllerImpactCounts { get; set; } = IgnoreControllerImpactCounts.Empty;
	}

	private sealed class RootDirectoryToggleCandidateAccumulator
	{
		public int HiddenFolders { get; set; }
		public int DotFolders { get; set; }
		public bool IsEmpty => HiddenFolders == 0 && DotFolders == 0;
	}

	private static HashSet<string> BuildAllDiscoveredExtensionsSet(IReadOnlyCollection<string> discoveredEntries)
	{
		return BuildAllowedExtensionsSet(discoveredEntries, effectiveExtensionPolicy: null);
	}

	private static HashSet<string> BuildAllowedExtensionsSet(
		IReadOnlyCollection<string> discoveredEntries,
		IExtensionInclusionPolicy? effectiveExtensionPolicy)
	{
		var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in discoveredEntries)
		{
			var extension = Path.GetExtension(entry);
			if (!string.IsNullOrWhiteSpace(extension) &&
			    (effectiveExtensionPolicy is null || effectiveExtensionPolicy.AllowsExtension(extension)))
			{
				extensions.Add(extension);
			}
		}

		return extensions;
	}

	private static ScanResult<IgnoreOptionCounts> GetRootDirectoryToggleCandidateCounts(
		string rootPath,
		IgnoreRules effectiveRules,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
			return new ScanResult<IgnoreOptionCounts>(IgnoreOptionCounts.Empty, RootAccessDenied: false, HadAccessDenied: false);

		var hiddenFolders = 0;
		var dotFolders = 0;
		var rootAccessDenied = 0;
		var hadAccessDenied = 0;
		var directoryPaths = new List<string>();

		try
		{
			foreach (var directoryPath in Directory.EnumerateDirectories(rootPath, "*", SearchOption.TopDirectoryOnly))
			{
				cancellationToken.ThrowIfCancellationRequested();
				directoryPaths.Add(directoryPath);
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

		var parallelOptions = ScanParallelismPolicy.CreateOptions(cancellationToken);
		Parallel.ForEach(
			directoryPaths,
			parallelOptions,
			() => new RootDirectoryToggleCandidateAccumulator(),
			(directoryPath, _, localCounts) =>
			{
				parallelOptions.CancellationToken.ThrowIfCancellationRequested();
				var name = Path.GetFileName(directoryPath);
				if (string.IsNullOrWhiteSpace(name))
					return localCounts;
				if (IsReparsePointDirectory(directoryPath))
					return localCounts;

				if (IsSuppressedByNonDirectoryToggleRule(directoryPath, name, effectiveRules))
					return localCounts;

				var isHiddenFolder = HasHiddenAttribute(directoryPath);
				var isDotFolder = IgnoreRuleSemantics.IsDotName(name);
				var isHiddenByCurrentHiddenFolderRule =
					IgnoreRuleSemantics.ShouldIgnoreHiddenDirectory(
						effectiveRules.IgnoreHiddenFolders,
						isHiddenFolder,
						isDotFolder,
						effectiveRules.IgnoreDotFolders);
				var shouldCountDotFolder =
					isDotFolder &&
					(effectiveRules.IgnoreDotFolders || !isHiddenByCurrentHiddenFolderRule);
				var shouldCountHiddenFolder =
					isHiddenFolder &&
					!IgnoreRuleSemantics.ShouldIgnoreDotDirectory(effectiveRules.IgnoreDotFolders, isDotFolder);

				if (shouldCountDotFolder)
				{
					// Help 11.9 defines basic counters as rule-specific impact in the
					// current tree configuration. Controller-owned roots are filtered above;
					// EmptyFolders must not mask direct root-level directory-toggle evidence.
					localCounts.DotFolders++;
				}

				if (shouldCountHiddenFolder)
				{
					// Keep the fallback scanner aligned with FileSystemScanner's
					// controller-aware root-level directory-toggle contract.
					localCounts.HiddenFolders++;
				}

				return localCounts;
			},
			localCounts =>
			{
				if (localCounts.IsEmpty)
					return;

				Interlocked.Add(ref hiddenFolders, localCounts.HiddenFolders);
				Interlocked.Add(ref dotFolders, localCounts.DotFolders);
			});

		return new ScanResult<IgnoreOptionCounts>(
			new IgnoreOptionCounts(HiddenFolders: hiddenFolders, DotFolders: dotFolders),
			RootAccessDenied: rootAccessDenied == 1,
			HadAccessDenied: hadAccessDenied == 1);
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

	private static bool IsExtensionlessFileName(string name) =>
		IgnoreRuleSemantics.IsExtensionlessFileName(name);

}
