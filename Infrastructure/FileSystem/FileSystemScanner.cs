using System.Buffers;

namespace DevProjex.Infrastructure.FileSystem;

public sealed partial class FileSystemScanner : IFileSystemScanner, IFileSystemScannerAdvanced, IFileSystemScannerEffectiveEmptyFolderCounter, IFileSystemScannerEffectiveIgnoreCountsProvider, IFileSystemScannerIgnoreSectionSnapshotProvider, IFileSystemScannerExtensionPolicySnapshotProvider, IFileSystemScannerRootSelectionSnapshotProvider, IFileSystemScannerProjectWorkspaceSnapshotProvider, IFileSystemScannerProjectWorkspaceScanner
{
	public bool CanReadRoot(string rootPath)
	{
		try
		{
			using var enumerator = Directory.EnumerateFileSystemEntries(rootPath).GetEnumerator();
			_ = enumerator.MoveNext();
			return true;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
		catch
		{
			return true;
		}
	}

	public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
	{
		var scan = ScanExtensionsCore(
			rootPath,
			rules,
			collectIgnoreOptionCounts: false,
			includeRootDirectoryInCounts: false,
			cancellationToken);
		return new ScanResult<HashSet<string>>(scan.Value.Extensions, scan.RootAccessDenied, scan.HadAccessDenied);
	}

	public ScanResult<ExtensionsScanData> GetExtensionsWithIgnoreOptionCounts(
		string rootPath,
		IgnoreRules rules,
		CancellationToken cancellationToken = default)
	{
		return ScanExtensionsCore(
			rootPath,
			rules,
			collectIgnoreOptionCounts: true,
			includeRootDirectoryInCounts: true,
			cancellationToken);
	}

	public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
	{
		var scan = ScanRootFilesCore(rootPath, rules, collectIgnoreOptionCounts: false, cancellationToken);
		return new ScanResult<HashSet<string>>(scan.Value.Extensions, scan.RootAccessDenied, scan.HadAccessDenied);
	}

	public ScanResult<ExtensionsScanData> GetRootFileExtensionsWithIgnoreOptionCounts(
		string rootPath,
		IgnoreRules rules,
		CancellationToken cancellationToken = default)
	{
		return ScanRootFilesCore(rootPath, rules, collectIgnoreOptionCounts: true, cancellationToken);
	}

	public ScanResult<int> GetEffectiveEmptyFolderCount(
		string rootPath,
		IReadOnlySet<string> allowedExtensions,
		IgnoreRules rules,
		CancellationToken cancellationToken = default)
	{
		return ScanEffectiveEmptyFolderCountCore(rootPath, allowedExtensions, rules, cancellationToken);
	}

	public ScanResult<IgnoreOptionCounts> GetEffectiveIgnoreOptionCounts(
		string rootPath,
		IReadOnlySet<string> allowedExtensions,
		IgnoreRules rules,
		CancellationToken cancellationToken = default)
	{
		return ScanEffectiveIgnoreOptionCountsCore(rootPath, allowedExtensions, rules, cancellationToken);
	}

	public ScanResult<IgnoreOptionCounts> GetEffectiveRootFileIgnoreOptionCounts(
		string rootPath,
		IReadOnlySet<string> allowedExtensions,
		IgnoreRules rules,
		CancellationToken cancellationToken = default)
	{
		return ScanEffectiveRootFileIgnoreOptionCountsCore(rootPath, allowedExtensions, rules, cancellationToken);
	}

	public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
		string rootPath,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IReadOnlySet<string>? effectiveAllowedExtensions,
		CancellationToken cancellationToken = default)
	{
		return GetIgnoreSectionSnapshot(
			rootPath,
			extensionDiscoveryRules,
			effectiveRules,
			CreateExtensionPolicy(effectiveAllowedExtensions),
			cancellationToken);
	}

	public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
		string rootPath,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		CancellationToken cancellationToken = default)
	{
		return ScanIgnoreSectionSnapshotCore(
			rootPath,
			extensionDiscoveryRules,
			effectiveRules,
			effectiveExtensionPolicy,
			includeRootDirectoryInRawCounts: true,
			cancellationToken);
	}

	public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
		string rootPath,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IReadOnlySet<string>? effectiveAllowedExtensions,
		CancellationToken cancellationToken = default)
	{
		return GetRootFileIgnoreSectionSnapshot(
			rootPath,
			extensionDiscoveryRules,
			effectiveRules,
			CreateExtensionPolicy(effectiveAllowedExtensions),
			cancellationToken);
	}

	public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
		string rootPath,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		CancellationToken cancellationToken = default)
	{
		return ScanRootFileIgnoreSectionSnapshotCore(
			rootPath,
			extensionDiscoveryRules,
			effectiveRules,
			effectiveExtensionPolicy,
			cancellationToken);
	}

	public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshotForRootSelection(
		string rootPath,
		IReadOnlyCollection<string> selectedRootFolders,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		bool includeDirectoryToggleProbeRoots = false,
		CancellationToken cancellationToken = default,
		bool includeControllerImpactProbeRoots = false)
	{
		var scan = ScanProjectWorkspace(
			new ProjectWorkspaceScanRequest(
				rootPath,
				selectedRootFolders,
				extensionDiscoveryRules,
				effectiveRules,
				effectiveExtensionPolicy,
				CaptureTreeInventory: false,
				IncludeDirectoryToggleProbeRoots: includeDirectoryToggleProbeRoots,
				IncludeControllerImpactProbeRoots: includeControllerImpactProbeRoots),
			cancellationToken);
		return new ScanResult<IgnoreSectionScanData>(
			scan.Value.IgnoreSection,
			scan.RootAccessDenied,
			scan.HadAccessDenied);
	}

	public ScanResult<ProjectWorkspaceScanSnapshot> GetProjectWorkspaceSnapshotForRootSelection(
		string rootPath,
		IReadOnlyCollection<string> selectedRootFolders,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		bool includeDirectoryToggleProbeRoots = false,
		CancellationToken cancellationToken = default,
		bool includeControllerImpactProbeRoots = false)
	{
		return ScanProjectWorkspace(
			new ProjectWorkspaceScanRequest(
				rootPath,
				selectedRootFolders,
				extensionDiscoveryRules,
				effectiveRules,
				effectiveExtensionPolicy,
				CaptureTreeInventory: true,
				IncludeDirectoryToggleProbeRoots: includeDirectoryToggleProbeRoots,
				IncludeControllerImpactProbeRoots: includeControllerImpactProbeRoots),
			cancellationToken);
	}

	public ScanResult<ProjectWorkspaceScanSnapshot> ScanProjectWorkspace(
		ProjectWorkspaceScanRequest request,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ValidateProjectWorkspaceScanRuleContract(request.ExtensionDiscoveryRules, request.EffectiveRules);
		var rootPath = request.RootPath;
		var selectedRootFolders = request.SelectedRootFolders;
		var extensionDiscoveryRules = request.ExtensionDiscoveryRules;
		var effectiveRules = request.EffectiveRules;
		var effectiveExtensionPolicy = request.EffectiveExtensionPolicy;
		var includeDirectoryToggleProbeRoots = request.IncludeDirectoryToggleProbeRoots;
		var includeControllerImpactProbeRoots = request.IncludeControllerImpactProbeRoots;
		var captureTreeInventory = request.CaptureTreeInventory;
		var captureRootScanBreakdown = request.CaptureRootScanBreakdown;

		var scanPlan = BuildRootSelectionScanPlan(
			rootPath,
			selectedRootFolders,
			effectiveRules,
			includeDirectoryToggleProbeRoots,
			includeControllerImpactProbeRoots,
			cancellationToken);

		var aggregatedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var aggregatedEffectiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var rawCounts = IgnoreOptionCounts.Empty;
		var effectiveCounts = IgnoreOptionCounts.Empty;
		var controllerImpactCounts = IgnoreControllerImpactCounts.Empty;
		var gitEvidence = GitWorkspaceEvidence.Empty;
		var rootAccessDenied = scanPlan.RootAccessDenied ? 1 : 0;
		var hadAccessDenied = scanPlan.HadAccessDenied ? 1 : 0;
		var mergeLock = new object();
		var rootFileInventoryEntries = captureTreeInventory
			? new List<ProjectTreeInventoryEntry>()
			: null;

		var rootFileSnapshot = ScanRootFileIgnoreSectionSnapshotCore(
			rootPath,
			extensionDiscoveryRules,
			effectiveRules,
			effectiveExtensionPolicy,
			cancellationToken,
			rootFileInventoryEntries);
		aggregatedExtensions.UnionWith(rootFileSnapshot.Value.Extensions);
		aggregatedEffectiveExtensions.UnionWith(rootFileSnapshot.Value.VisibleExtensions);
		rawCounts = rawCounts.Add(rootFileSnapshot.Value.RawIgnoreOptionCounts);
		effectiveCounts = effectiveCounts.Add(rootFileSnapshot.Value.EffectiveIgnoreOptionCounts);
		controllerImpactCounts = controllerImpactCounts.Add(rootFileSnapshot.Value.ControllerImpactCounts);
		gitEvidence = gitEvidence.Add(rootFileSnapshot.Value.GitEvidence);
		if (rootFileSnapshot.RootAccessDenied)
			Interlocked.Exchange(ref rootAccessDenied, 1);
		if (rootFileSnapshot.HadAccessDenied)
			Interlocked.Exchange(ref hadAccessDenied, 1);

		var subtreeInventories = captureTreeInventory
			? new List<ProjectTreeInventorySnapshot>()
			: null;
		var rootScanSnapshots = captureRootScanBreakdown
			? new Dictionary<string, ProjectWorkspaceRootScanSnapshot>(scanPlan.SelectedRoots.Count, PathComparer.Default)
			: null;
		var projectGitIgnoreContext = captureRootScanBreakdown
			? effectiveRules.CreateGitIgnoreScanContext(rootPath)
			: IgnoreRules.GitIgnoreScanContext.Disabled(effectiveRules);
		var projectGitIgnoreCandidateContext = captureRootScanBreakdown
			? effectiveRules.CreateGitIgnoreCandidateScanContext(rootPath)
			: IgnoreRules.GitIgnoreScanContext.Disabled(effectiveRules);
		if (captureRootScanBreakdown && effectiveRules.IsGitIgnoreTraversalEnabled)
		{
			(projectGitIgnoreContext, projectGitIgnoreCandidateContext, _) =
				EnterNearestGitTrackedPathIndex(
					rootPath,
					projectGitIgnoreContext,
					projectGitIgnoreCandidateContext,
					cancellationToken);
		}

		if (scanPlan.SelectedRoots.Count > 0)
		{
			var parallelOptions = ScanParallelismPolicy.CreateOptions(cancellationToken);
			Parallel.ForEach(
				scanPlan.SelectedRoots,
				parallelOptions,
				() => new ProjectWorkspaceScanLocalState(captureTreeInventory, captureRootScanBreakdown),
				(folder, _, localState) =>
				{
					parallelOptions.CancellationToken.ThrowIfCancellationRequested();

					var inventoryCapture = captureTreeInventory
						? new ProjectTreeInventoryCapture()
						: null;
					var snapshot = ScanIgnoreSectionSnapshotCore(
						folder.FullPath,
						extensionDiscoveryRules,
						effectiveRules,
						effectiveExtensionPolicy,
						includeRootDirectoryInRawCounts: true,
						parallelOptions.CancellationToken,
						inventoryCapture);

					localState.Extensions.UnionWith(snapshot.Value.Extensions);
					localState.EffectiveExtensions.UnionWith(snapshot.Value.VisibleExtensions);
					localState.RawCounts.Add(snapshot.Value.RawIgnoreOptionCounts);
					localState.EffectiveCounts = localState.EffectiveCounts.Add(snapshot.Value.EffectiveIgnoreOptionCounts);
					localState.ControllerImpactCounts =
						localState.ControllerImpactCounts.Add(snapshot.Value.ControllerImpactCounts);
					localState.GitEvidence = localState.GitEvidence.Add(snapshot.Value.GitEvidence);
					if (localState.TreeInventories is not null &&
					    inventoryCapture?.Inventory is not null)
					{
						localState.TreeInventories.Add(inventoryCapture.Inventory);
					}
					if (localState.RootSnapshots is not null)
					{
						var directoryToggleProbeCounts = includeDirectoryToggleProbeRoots
							? CountRootDirectoryToggleCandidate(
								folder,
								effectiveRules,
								parallelOptions.CancellationToken)
							: IgnoreOptionCounts.Empty;
						var controllerImpactProbeCounts = includeControllerImpactProbeRoots
							? CountRootDirectoryControllerImpactCandidate(
								folder,
								effectiveRules,
								effectiveExtensionPolicy,
								projectGitIgnoreContext,
								projectGitIgnoreCandidateContext,
								parallelOptions.CancellationToken)
							: IgnoreControllerImpactCounts.Empty;
						localState.RootSnapshots.Add(new KeyValuePair<string, ProjectWorkspaceRootScanSnapshot>(
							folder.Name,
							new ProjectWorkspaceRootScanSnapshot(
								snapshot.Value,
								directoryToggleProbeCounts,
								controllerImpactProbeCounts,
								snapshot.RootAccessDenied,
								snapshot.HadAccessDenied)));
					}

					if (snapshot.RootAccessDenied)
						Interlocked.Exchange(ref rootAccessDenied, 1);
					if (snapshot.HadAccessDenied)
						Interlocked.Exchange(ref hadAccessDenied, 1);

					return localState;
				},
				localState =>
				{
					if (localState.IsEmpty)
						return;

					lock (mergeLock)
					{
						if (localState.Extensions.Count > 0)
							aggregatedExtensions.UnionWith(localState.Extensions);
						if (localState.EffectiveExtensions.Count > 0)
							aggregatedEffectiveExtensions.UnionWith(localState.EffectiveExtensions);
						rawCounts = rawCounts.Add(localState.RawCounts.ToImmutable());
						effectiveCounts = effectiveCounts.Add(localState.EffectiveCounts);
						controllerImpactCounts = controllerImpactCounts.Add(localState.ControllerImpactCounts);
						gitEvidence = gitEvidence.Add(localState.GitEvidence);
						if (subtreeInventories is not null &&
						    localState.TreeInventories is not null &&
						    localState.TreeInventories.Count > 0)
						{
							subtreeInventories.AddRange(localState.TreeInventories);
						}
						if (rootScanSnapshots is not null &&
						    localState.RootSnapshots is not null)
						{
							foreach (var pair in localState.RootSnapshots)
								rootScanSnapshots[pair.Key] = pair.Value;
						}
					}
				});
		}

		var unselectedDirectoryToggleProbeCounts = IgnoreOptionCounts.Empty;
		if (includeDirectoryToggleProbeRoots && scanPlan.DirectoryToggleCandidates.Count > 0)
		{
			// Keep initial project-load inventory focused on the currently selected roots.
			// Root-level toggle candidates still affect counts, but reading their full
			// subtrees here would delay first paint for folders that are invisible now.
			// This is the core performance trade-off of controller-aware ignore scanning: root probes
			// keep checkboxes reversible, while selected-root scans do the expensive content
			// work only for roots the user can currently see.
			var rootCandidateCounts = CountRootDirectoryToggleCandidates(
				scanPlan.DirectoryToggleCandidates,
				effectiveRules,
				cancellationToken);
			effectiveCounts = effectiveCounts.Add(rootCandidateCounts.Value);
			unselectedDirectoryToggleProbeCounts = rootCandidateCounts.Value;
			if (rootCandidateCounts.RootAccessDenied)
				Interlocked.Exchange(ref rootAccessDenied, 1);
			if (rootCandidateCounts.HadAccessDenied)
				Interlocked.Exchange(ref hadAccessDenied, 1);
		}

		var unselectedControllerImpactProbeCounts = IgnoreControllerImpactCounts.Empty;
		if (scanPlan.ControllerImpactCandidates.Count > 0)
		{
			var rootControllerImpactCounts = CountRootDirectoryControllerImpactCandidates(
				scanPlan.ControllerImpactCandidates,
				effectiveRules,
				effectiveExtensionPolicy,
				cancellationToken);
			controllerImpactCounts = controllerImpactCounts.Add(rootControllerImpactCounts.Value);
			unselectedControllerImpactProbeCounts = rootControllerImpactCounts.Value;
			if (rootControllerImpactCounts.RootAccessDenied)
				Interlocked.Exchange(ref rootAccessDenied, 1);
			if (rootControllerImpactCounts.HadAccessDenied)
				Interlocked.Exchange(ref hadAccessDenied, 1);
		}

		var ignoreSection = new IgnoreSectionScanData(
			aggregatedExtensions,
			rawCounts,
			effectiveCounts,
			controllerImpactCounts,
			aggregatedEffectiveExtensions,
			GitEvidence: gitEvidence);
		var treeInventory = captureTreeInventory
			? BuildRootSelectionInventory(
				rootPath,
				rootFileInventoryEntries!,
				subtreeInventories!,
				rootAccessDenied == 1,
				hadAccessDenied == 1,
				TryLoadRootTrackedPathIndex(
					rootPath,
					effectiveRules,
					cancellationToken))
			: null;
		var breakdown = captureRootScanBreakdown
			? new ProjectWorkspaceScanBreakdown(
				rootFileSnapshot.Value,
				rootScanSnapshots!,
				unselectedDirectoryToggleProbeCounts,
				unselectedControllerImpactProbeCounts,
				includeDirectoryToggleProbeRoots,
				includeControllerImpactProbeRoots,
				scanPlan.RootAccessDenied,
				scanPlan.HadAccessDenied,
				rootFileSnapshot.RootAccessDenied,
				rootFileSnapshot.HadAccessDenied)
			: null;
		return new ScanResult<ProjectWorkspaceScanSnapshot>(
			new ProjectWorkspaceScanSnapshot(ignoreSection, treeInventory, breakdown),
			rootAccessDenied == 1,
			hadAccessDenied == 1);
	}

	private static void ValidateProjectWorkspaceScanRuleContract(
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules)
	{
		// Extension discovery may differ for file-level rules and EmptyFolders only. The
		// latter is a final tree-pruning rule, not a traversal rule for finding extensions.
		// Directory/controller rules define reachability and must stay shared.
		if (extensionDiscoveryRules.IsGitIgnoreTraversalEnabled != effectiveRules.IsGitIgnoreTraversalEnabled ||
		    extensionDiscoveryRules.GitFilteringMode != effectiveRules.GitFilteringMode ||
		    extensionDiscoveryRules.UseSmartIgnore != effectiveRules.UseSmartIgnore ||
		    extensionDiscoveryRules.IgnoreHiddenFolders != effectiveRules.IgnoreHiddenFolders ||
		    extensionDiscoveryRules.IgnoreDotFolders != effectiveRules.IgnoreDotFolders ||
		    !PathComparer.Default.Equals(
			    extensionDiscoveryRules.ExcludedRootFolderName,
			    effectiveRules.ExcludedRootFolderName))
		{
			throw new ArgumentException(
				"Extension discovery rules may differ from effective rules only by file-level ignore options and EmptyFolders.",
				nameof(ProjectWorkspaceScanRequest.ExtensionDiscoveryRules));
		}
	}

	public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var names = new List<string>();
		var useGitIgnore = rules.IsGitIgnoreTraversalEnabled;
		var gitIgnoreContext = rules.CreateGitIgnoreScanContext(rootPath);

		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
			return new ScanResult<List<string>>(names, false, false);

		try
		{
			if (useGitIgnore &&
			    gitIgnoreContext.RequiresTrackedPathIndex &&
			    GitTrackedPathIndexCache.TryLoadNearest(
				    rootPath,
				    cancellationToken,
				    out var trackedPathIndex))
			{
				gitIgnoreContext = gitIgnoreContext.WithTrackedPathIndex(trackedPathIndex);
			}

			foreach (var dir in FileSystemEntryEnumerator.EnumerateDirectories(rootPath))
			{
				cancellationToken.ThrowIfCancellationRequested();

				var dirName = dir.Name;
				if (PathComparer.Default.Equals(dirName, rules.ExcludedRootFolderName))
					continue;

				var directoryGitIgnore = useGitIgnore
					? gitIgnoreContext.Evaluate(dir.FullPath, dir.RelativePath, isDirectory: true, dirName)
					: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
				if (ShouldSkipDirectoryByName(dirName, dir.FullPath, dir.IsHidden, rules, directoryGitIgnore))
					continue;

				names.Add(dirName);
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (UnauthorizedAccessException)
		{
			return new ScanResult<List<string>>(names, true, true);
		}
		catch
		{
			return new ScanResult<List<string>>(names, false, false);
		}

		names.Sort(StringComparer.OrdinalIgnoreCase);
		return new ScanResult<List<string>>(names, false, false);
	}

	/// <summary>
	/// Optimized version that avoids DirectoryInfo allocation when possible.
	/// Only creates DirectoryInfo when checking Hidden attribute.
	/// </summary>
	private static bool ShouldSkipDirectoryByName(
		string name,
		string fullPath,
		IgnoreRules rules,
		in IgnoreRules.GitIgnoreEvaluation gitIgnoreEvaluation)
	{
		return ShouldSkipDirectoryByName(
			name,
			fullPath,
			HasHiddenAttribute(fullPath),
			rules,
			gitIgnoreEvaluation);
	}

	/// <summary>
	/// Entry-based scans already know whether a directory is hidden.
	/// Reusing that metadata avoids an extra stat call for every candidate folder.
	/// </summary>
	private static bool ShouldSkipDirectoryByName(
		string name,
		string fullPath,
		bool isHidden,
		IgnoreRules rules,
		in IgnoreRules.GitIgnoreEvaluation gitIgnoreEvaluation)
	{
		return IgnoreDecisionEngine
			.EvaluateDirectory(fullPath, name, isHidden, rules, gitIgnoreEvaluation)
			.IsIgnored;
	}

	/// <summary>
	/// Optimized version that avoids FileInfo allocation when possible.
	/// Only checks attributes when necessary.
	/// </summary>
	private static bool ShouldSkipFileByName(
		string name,
		string fullPath,
		IgnoreRules rules,
		bool shouldApplySmartIgnore,
		in IgnoreRules.GitIgnoreEvaluation gitIgnoreEvaluation)
	{
		return ShouldSkipFileByName(
			name,
			fullPath,
			HasHiddenAttribute(fullPath),
			GetFileLength(fullPath),
			rules,
			shouldApplySmartIgnore,
			gitIgnoreEvaluation);
	}

	/// <summary>
	/// File scans reuse the metadata captured during enumeration so hidden/empty checks
	/// stay purely in-memory instead of bouncing back to the filesystem for every file.
	/// </summary>
	private static bool ShouldSkipFileByName(
		string name,
		string fullPath,
		bool isHidden,
		long length,
		IgnoreRules rules,
		bool shouldApplySmartIgnore,
		in IgnoreRules.GitIgnoreEvaluation gitIgnoreEvaluation)
	{
		return IgnoreDecisionEngine
			.EvaluateFile(
				fullPath,
				name,
				isHidden,
				length,
				rules,
				shouldApplySmartIgnore,
				gitIgnoreEvaluation)
			.IsIgnored;
	}

	private static DirectoryScanFacts AnalyzeDirectory(
		string fullPath,
		string relativePath,
		string name,
		IgnoreRules rules,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext,
		IgnoreRules.GitIgnoreScanContext gitIgnoreCandidateContext)
	{
		return AnalyzeDirectory(
			fullPath,
			relativePath,
			name,
			HasHiddenAttribute(fullPath),
			rules,
			gitIgnoreContext,
			gitIgnoreCandidateContext);
	}

	private static DirectoryScanFacts AnalyzeDirectory(
		string fullPath,
		string relativePath,
		string name,
		bool isHidden,
		IgnoreRules rules,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext,
		IgnoreRules.GitIgnoreScanContext gitIgnoreCandidateContext)
	{
		var gitIgnoreEvaluation = rules.IsGitIgnoreTraversalEnabled
			? gitIgnoreContext.Evaluate(fullPath, relativePath, isDirectory: true, name)
			: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
		var gitIgnoreCandidateEvaluation = rules.GitIgnoreCandidateMatchesActiveRules
			? gitIgnoreEvaluation
			: gitIgnoreCandidateContext.Evaluate(fullPath, relativePath, isDirectory: true, name);
		var isSmartIgnored = rules.IsSmartIgnoredDirectory(fullPath, name);

		return new DirectoryScanFacts(
			Name: name,
			FullPath: fullPath,
			RelativePath: relativePath,
			IsHidden: isHidden,
			IsDot: IgnoreRuleSemantics.IsDotName(name),
			IsSmartIgnored: isSmartIgnored,
			IsSmartIgnoredCandidate: rules.SmartIgnoreCandidateMatchesActiveRules
				? isSmartIgnored
				: rules.IsSmartIgnoredDirectoryCandidate(fullPath, name),
			GitIgnoreEvaluation: gitIgnoreEvaluation,
			GitIgnoreCandidateEvaluation: gitIgnoreCandidateEvaluation);
	}

	private static FileScanFacts AnalyzeFile(
		string fullPath,
		string relativePath,
		string name,
		bool shouldApplySmartIgnoreForFiles,
		bool shouldApplySmartIgnoreCandidateForFiles,
		IgnoreRules rules,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext,
		IgnoreRules.GitIgnoreScanContext gitIgnoreCandidateContext)
	{
		return AnalyzeFile(
			fullPath,
			relativePath,
			name,
			HasHiddenAttribute(fullPath),
			GetFileLength(fullPath),
			shouldApplySmartIgnoreForFiles,
			shouldApplySmartIgnoreCandidateForFiles,
			rules,
			gitIgnoreContext,
			gitIgnoreCandidateContext);
	}

	private static FileScanFacts AnalyzeFile(
		string fullPath,
		string relativePath,
		string name,
		bool isHidden,
		long length,
		bool shouldApplySmartIgnoreForFiles,
		bool shouldApplySmartIgnoreCandidateForFiles,
		IgnoreRules rules,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext,
		IgnoreRules.GitIgnoreScanContext gitIgnoreCandidateContext)
	{
		var isExtensionless = IsExtensionlessFileName(name);
		var extensionStart = GetExtensionStart(name);
		var gitIgnored = rules.IsGitIgnoreTraversalEnabled &&
		                 gitIgnoreContext.Evaluate(fullPath, relativePath, isDirectory: false, name).IsIgnored;
		var gitIgnoredCandidate = rules.GitIgnoreCandidateMatchesActiveRules
			? gitIgnored
			: gitIgnoreCandidateContext
				.Evaluate(fullPath, relativePath, isDirectory: false, name)
				.IsIgnored;
		var isSmartIgnored = rules.IsSmartIgnoredFile(fullPath, name, shouldApplySmartIgnoreForFiles);

		return new FileScanFacts(
			Name: name,
			RelativePath: relativePath,
			ExtensionStart: extensionStart,
			IsHidden: isHidden,
			IsDot: IgnoreRuleSemantics.IsDotName(name),
			IsEmpty: length == 0,
			IsExtensionless: isExtensionless,
			IsSmartIgnored: isSmartIgnored,
			IsSmartIgnoredCandidate: rules.SmartIgnoreCandidateMatchesActiveRules
				? isSmartIgnored
				: rules.IsSmartIgnoredFileCandidate(
					fullPath,
					name,
					shouldApplySmartIgnoreCandidateForFiles),
			IsGitIgnored: gitIgnored,
			IsGitIgnoredCandidate: gitIgnoredCandidate);
	}

	private static DirectoryToggleRuleState EvaluateDirectoryRuleState(
		in DirectoryScanFacts facts,
		bool ignoreHiddenFolders,
		bool ignoreDotFolders)
	{
		if (facts.GitIgnoreEvaluation.IsIgnored && !facts.GitIgnoreEvaluation.ShouldTraverseIgnoredDirectory)
			return new DirectoryToggleRuleState(CanTraverseChildren: false, IsSelfIgnoredButTraversed: false);

		if (facts.IsSmartIgnored)
			return new DirectoryToggleRuleState(CanTraverseChildren: false, IsSelfIgnoredButTraversed: false);

		if (IgnoreRuleSemantics.ShouldIgnoreDotDirectory(ignoreDotFolders, facts.IsDot))
			return new DirectoryToggleRuleState(CanTraverseChildren: false, IsSelfIgnoredButTraversed: false);

		if (IgnoreRuleSemantics.ShouldIgnoreHiddenDirectory(
			    ignoreHiddenFolders,
			    facts.IsHidden,
			    facts.IsDot,
			    ignoreDotFolders))
			return new DirectoryToggleRuleState(CanTraverseChildren: false, IsSelfIgnoredButTraversed: false);

		return new DirectoryToggleRuleState(
			CanTraverseChildren: true,
			IsSelfIgnoredButTraversed: facts.GitIgnoreEvaluation.IsIgnored &&
			                           facts.GitIgnoreEvaluation.ShouldTraverseIgnoredDirectory);
	}

	private static IgnoreControllerImpactCounts CountDirectDirectoryControllerImpact(
		in DirectoryScanFacts facts,
		IgnoreRules rules,
		IExtensionInclusionPolicy? extensionPolicy,
		CancellationToken cancellationToken,
		bool requireVisibleContentWhenEmptyFoldersIgnored = true)
	{
		// Controller ownership is evaluated before general dot/hidden/empty rules.
		// Otherwise overlapping candidates oscillate between controllers and lower-priority
		// toggles instead of leaving the highest-priority applicable option reversible.
		var gitDirectImpact =
			facts.GitIgnoreCandidateEvaluation.IsIgnored &&
			!facts.GitIgnoreCandidateEvaluation.ShouldTraverseIgnoredDirectory;
		var activeGitOwnsDirectory =
			rules.IsGitIgnoreTraversalEnabled &&
			facts.GitIgnoreEvaluation.IsIgnored &&
			!facts.GitIgnoreEvaluation.ShouldTraverseIgnoredDirectory;
		var smartDirectImpact =
			facts.IsSmartIgnoredCandidate &&
			(!rules.IsGitIgnoreTraversalEnabled || (!gitDirectImpact && !activeGitOwnsDirectory));
		if (!gitDirectImpact && !smartDirectImpact)
			return IgnoreControllerImpactCounts.Empty;

		// Selected-scope directories need a content probe because EmptyFolders can already
		// remove an empty folder. Root-list candidates are different: hiding the top-level
		// checkbox itself is user-visible even before extension/content filters are applied.
		// Keep those concepts separate: controller impact answers "would this controller
		// remove a visible choice or visible content?", not "does this folder have a raw
		// suspicious name?".
		if (requireVisibleContentWhenEmptyFoldersIgnored &&
		    rules.IgnoreEmptyFolders &&
		    !HasVisibleContentForControllerImpactCandidate(
			    facts.FullPath,
			    facts.RelativePath,
			    rules,
			    extensionPolicy,
			    cancellationToken))
		{
			return IgnoreControllerImpactCounts.Empty;
		}

		return new IgnoreControllerImpactCounts(
			GitIgnore: gitDirectImpact ? 1 : 0,
			SmartIgnore: smartDirectImpact ? 1 : 0);
	}

	private static bool HasVisibleContentForControllerImpactCandidate(
		string rootPath,
		string rootRelativePath,
		IgnoreRules rules,
		IExtensionInclusionPolicy? extensionPolicy,
		CancellationToken cancellationToken)
	{
		var pending = new Stack<(string Path, string RelativePath)>();
		pending.Push((rootPath, rootRelativePath));

		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var (currentPath, currentRelativePath) = pending.Pop();

			try
			{
				foreach (var file in FileSystemEntryEnumerator.EnumerateFiles(currentPath, currentRelativePath))
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (IsFileVisibleWithoutControllerRules(file, rules, extensionPolicy))
						return true;
				}

				foreach (var directory in FileSystemEntryEnumerator.EnumerateDirectories(currentPath, currentRelativePath))
				{
					cancellationToken.ThrowIfCancellationRequested();
					var childFacts = new DirectoryScanFacts(
						Name: directory.Name,
						FullPath: directory.FullPath,
						RelativePath: directory.RelativePath,
						IsHidden: directory.IsHidden,
						IsDot: IgnoreRuleSemantics.IsDotName(directory.Name),
						IsSmartIgnored: false,
						IsSmartIgnoredCandidate: false,
						GitIgnoreEvaluation: IgnoreRules.GitIgnoreEvaluation.NotIgnored,
						GitIgnoreCandidateEvaluation: IgnoreRules.GitIgnoreEvaluation.NotIgnored);
					pending.Push((directory.FullPath, directory.RelativePath));
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (UnauthorizedAccessException)
			{
				// An unreadable directory is still visible to the user as an access-denied node.
				return true;
			}
			catch
			{
				// Best-effort impact probing must not destabilize project loading.
			}
		}

		return false;
	}

	private static bool IsFileVisibleWithoutControllerRules(
		FileSystemFileEntry file,
		IgnoreRules rules,
		IExtensionInclusionPolicy? extensionPolicy)
	{
		var facts = new FileScanFacts(
			Name: file.Name,
			RelativePath: file.RelativePath,
			ExtensionStart: GetExtensionStart(file.Name),
			IsHidden: file.IsHidden,
			IsDot: IgnoreRuleSemantics.IsDotName(file.Name),
			IsEmpty: file.Length == 0,
			IsExtensionless: IsExtensionlessFileName(file.Name),
			IsSmartIgnored: false,
			IsSmartIgnoredCandidate: false,
			IsGitIgnored: false,
			IsGitIgnoredCandidate: false);

		return PassesControllerImpactExtensionFilter(facts, extensionPolicy);
	}

	private static bool IsDirectoryLocallyVisible(
		DirectoryToggleRuleState ruleState,
		bool ignoreEmptyFolders,
		bool hasVisibleContent)
	{
		if (!ruleState.CanTraverseChildren)
			return false;

		if (!hasVisibleContent)
		{
			if (ruleState.IsSelfIgnoredButTraversed)
				return false;
			if (ignoreEmptyFolders)
				return false;
		}

		return true;
	}

	private static EffectiveFileVisibilityProfile EvaluateFileVisibilityProfile(
		in FileScanFacts facts,
		IExtensionInclusionPolicy? extensionPolicy,
		IgnoreRules rules,
		bool allowWhenExtensionsAreDiscovered)
	{
		var controllerBaselineVisible = PassesControllerImpactExtensionFilter(facts, extensionPolicy);
		var gitIgnoreVisible = controllerBaselineVisible && !facts.IsGitIgnoredCandidate;
		var smartIgnoreBaselineVisible = rules.IsGitIgnoreTraversalEnabled
			? gitIgnoreVisible
			: controllerBaselineVisible;
		var smartIgnoreVisible = smartIgnoreBaselineVisible && !facts.IsSmartIgnoredCandidate;

		if (facts.IsGitIgnored ||
		    facts.IsSmartIgnored ||
		    !PassesExtensionFilter(facts, extensionPolicy, allowWhenExtensionsAreDiscovered))
		{
			return new EffectiveFileVisibilityProfile(
				BaseVisible: false,
				HiddenFilesVisible: false,
				DotFilesVisible: false,
				EmptyFilesVisible: false,
				ExtensionlessFilesVisible: false,
				ControllerBaselineVisible: controllerBaselineVisible,
				GitIgnoreVisible: gitIgnoreVisible,
				SmartIgnoreVisible: smartIgnoreVisible);
		}

		return new EffectiveFileVisibilityProfile(
			BaseVisible: PassesFileIgnoreRules(
				facts,
				rules.IgnoreHiddenFiles,
				rules.IgnoreDotFiles,
				rules.IgnoreEmptyFiles,
				rules.IgnoreExtensionlessFiles),
			HiddenFilesVisible: PassesFileIgnoreRules(
				facts,
				!rules.IgnoreHiddenFiles,
				rules.IgnoreDotFiles,
				rules.IgnoreEmptyFiles,
				rules.IgnoreExtensionlessFiles),
			DotFilesVisible: PassesFileIgnoreRules(
				facts,
				rules.IgnoreHiddenFiles,
				!rules.IgnoreDotFiles,
				rules.IgnoreEmptyFiles,
				rules.IgnoreExtensionlessFiles),
			EmptyFilesVisible: PassesFileIgnoreRules(
				facts,
				rules.IgnoreHiddenFiles,
				rules.IgnoreDotFiles,
				!rules.IgnoreEmptyFiles,
				rules.IgnoreExtensionlessFiles),
			ExtensionlessFilesVisible: PassesFileIgnoreRules(
				facts,
				rules.IgnoreHiddenFiles,
				rules.IgnoreDotFiles,
				rules.IgnoreEmptyFiles,
				!rules.IgnoreExtensionlessFiles),
			ControllerBaselineVisible: controllerBaselineVisible,
			GitIgnoreVisible: gitIgnoreVisible,
			SmartIgnoreVisible: smartIgnoreVisible);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool PassesControllerImpactExtensionFilter(
		in FileScanFacts facts,
		IExtensionInclusionPolicy? extensionPolicy)
	{
		if (facts.IsExtensionless)
			return true;

		var extension = GetExtensionSpan(facts);
		if (extension.IsEmpty)
			return false;

		return extensionPolicy is null || extensionPolicy.AllowsExtension(extension);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool PassesExtensionFilter(
		in FileScanFacts facts,
		IExtensionInclusionPolicy? extensionPolicy,
		bool allowWhenExtensionsAreDiscovered)
	{
		if (facts.IsExtensionless)
			return true;

		if (!allowWhenExtensionsAreDiscovered)
			return false;

		var extension = GetExtensionSpan(facts);
		if (extensionPolicy is null)
			return !extension.IsEmpty;

		return !extension.IsEmpty && extensionPolicy.AllowsExtension(extension);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool PassesFileIgnoreRules(
		in FileScanFacts facts,
		bool ignoreHiddenFiles,
		bool ignoreDotFiles,
		bool ignoreEmptyFiles,
		bool ignoreExtensionlessFiles)
	{
		return !IgnoreDecisionEngine.EvaluateFileWithoutControllers(
			facts.Name,
			facts.IsHidden,
			facts.IsEmpty,
			facts.IsExtensionless,
			ignoreHiddenFiles,
			ignoreDotFiles,
			ignoreEmptyFiles,
			ignoreExtensionlessFiles).IsIgnored;
	}

	private static IExtensionInclusionPolicy? CreateExtensionPolicy(
		IReadOnlySet<string>? allowedExtensions)
	{
		if (allowedExtensions is null)
			return null;

		return new ExtensionSetInclusionPolicy(allowedExtensions);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool PassesExtensionDiscoveryRules(in FileScanFacts facts, IgnoreRules rules)
	{
		return !facts.IsGitIgnored &&
		       !facts.IsSmartIgnored &&
		       PassesFileIgnoreRules(
			       facts,
			       rules.IgnoreHiddenFiles,
			       rules.IgnoreDotFiles,
			       rules.IgnoreEmptyFiles,
			       rules.IgnoreExtensionlessFiles);
	}

	private static IgnoreRules BuildEffectiveCountDiscoveryRules(IgnoreRules rules)
	{
		// Effective counters measure what each basic file rule changes from the current tree.
		// Keep Git/Smart exclusions, but leave file-level toggles open so the rule that hides
		// a file can still be counted as the active cause.
		return rules with
		{
			IgnoreHiddenFiles = false,
			IgnoreDotFiles = false,
			IgnoreEmptyFiles = false,
			IgnoreExtensionlessFiles = false
		};
	}

	private ScanResult<ExtensionsScanData> ScanExtensionsCore(
		string rootPath,
		IgnoreRules rules,
		bool collectIgnoreOptionCounts,
		bool includeRootDirectoryInCounts,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var uniqueExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var useGitIgnore = rules.IsGitIgnoreTraversalEnabled;
		var gitIgnoreContext = rules.CreateGitIgnoreScanContext(rootPath);
		var gitIgnoreCandidateContext = rules.CreateGitIgnoreCandidateScanContext(rootPath);
		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
		{
			return new ScanResult<ExtensionsScanData>(
				new ExtensionsScanData(uniqueExtensions, IgnoreOptionCounts.Empty),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		if (useGitIgnore)
		{
			(gitIgnoreContext, gitIgnoreCandidateContext, _) = EnterNearestGitTrackedPathIndex(
				rootPath,
				gitIgnoreContext,
				gitIgnoreCandidateContext,
				cancellationToken);
		}

		var normalizedRootPath = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var rootName = Path.GetFileName(normalizedRootPath);
		var rootGitIgnore = useGitIgnore
			? gitIgnoreContext.Evaluate(rootPath, string.Empty, isDirectory: true, rootName)
			: IgnoreRules.GitIgnoreEvaluation.NotIgnored;

		// Selected root folders must obey the same directory-level rules as the tree itself.
		// Otherwise a stale root selection (for example a dot-folder discovered before the
		// dynamic DotFolders toggle appeared) would still leak its subtree into counts.
		if (ShouldSkipDirectoryByName(rootName, rootPath, HasHiddenAttribute(rootPath), rules, rootGitIgnore))
		{
			return new ScanResult<ExtensionsScanData>(
				new ExtensionsScanData(uniqueExtensions, IgnoreOptionCounts.Empty),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		// Directory discovery is single-threaded; keep it allocation-light and parent-indexed.
		var directories = new List<DirectoryScanNode>(capacity: 256);
		var rootAccessDenied = 0;
		var hadAccessDenied = 0;
		var directoryCounts = default(MutableIgnoreOptionCounts);
		var gitIgnoreImpactCount = 0;

		// First pass: collect all traversable directories and parent links.
		var pending = new Stack<(
			string Path,
			string RelativePath,
			int ParentIndex,
			bool IsRootDirectory,
			IgnoreRules.GitIgnoreScanContext GitIgnoreContext,
			IgnoreRules.GitIgnoreScanContext GitIgnoreCandidateContext)>();
		pending.Push((
			rootPath,
			RelativePath: string.Empty,
			ParentIndex: -1,
			IsRootDirectory: true,
			gitIgnoreContext,
			gitIgnoreCandidateContext));

		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var (
				dir,
				relativePath,
				parentIndex,
				isRootDirectory,
				parentGitIgnoreContext,
				parentGitIgnoreCandidateContext) = pending.Pop();
			var currentDirectoryIndex = directories.Count;
			var directoryGitIgnoreContext = parentGitIgnoreContext;
			var directoryGitIgnoreCandidateContext = parentGitIgnoreCandidateContext;

			if (collectIgnoreOptionCounts && isRootDirectory && includeRootDirectoryInCounts)
			{
				AccumulateDirectoryIgnoreOptionCounts(
					new FileSystemDirectoryEntry(Path.GetFileName(dir), dir, relativePath, HasHiddenAttribute(dir)),
					ref directoryCounts);
			}

			try
			{
				var directoryBatch = FileSystemEntryEnumerator.ReadDirectoriesAndGitIgnore(
					dir,
					relativePath,
					cancellationToken);
				(directoryGitIgnoreContext, directoryGitIgnoreCandidateContext) = EnterGitIgnoreScope(
					dir,
					relativePath,
					directoryBatch.GitIgnorePath,
					directoryBatch.GitMetadataPath,
					directoryGitIgnoreContext,
					directoryGitIgnoreCandidateContext,
					cancellationToken);
				directories.Add(new DirectoryScanNode(
					dir,
					relativePath,
					parentIndex,
					isAccessDenied: false,
					directoryGitIgnoreContext,
					directoryGitIgnoreCandidateContext));

				foreach (var sd in directoryBatch.Directories)
				{
					cancellationToken.ThrowIfCancellationRequested();

					if (collectIgnoreOptionCounts)
						AccumulateDirectoryIgnoreOptionCounts(sd, ref directoryCounts);

					var directoryGitIgnore = useGitIgnore
						? directoryGitIgnoreContext.Evaluate(sd.FullPath, sd.RelativePath, isDirectory: true, sd.Name)
						: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
					var directoryGitIgnoreCandidate = rules.GitIgnoreCandidateMatchesActiveRules
						? directoryGitIgnore
						: directoryGitIgnoreCandidateContext.Evaluate(
							sd.FullPath,
							sd.RelativePath,
							isDirectory: true,
							sd.Name);
					if (directoryGitIgnoreCandidate.IsIgnored)
						gitIgnoreImpactCount++;
					if (ShouldSkipDirectoryByName(sd.Name, sd.FullPath, sd.IsHidden, rules, directoryGitIgnore))
						continue;

					pending.Push((
						sd.FullPath,
						sd.RelativePath,
						currentDirectoryIndex,
						IsRootDirectory: false,
						directoryGitIgnoreContext,
						directoryGitIgnoreCandidateContext));
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (UnauthorizedAccessException)
			{
				directories.Add(new DirectoryScanNode(
					dir,
					relativePath,
					parentIndex,
					isAccessDenied: true,
					directoryGitIgnoreContext,
					directoryGitIgnoreCandidateContext));
				Interlocked.Exchange(ref hadAccessDenied, 1);
				if (isRootDirectory) Interlocked.Exchange(ref rootAccessDenied, 1);
				continue;
			}
			catch
			{
				directories.Add(new DirectoryScanNode(
					dir,
					relativePath,
					parentIndex,
					isAccessDenied: false,
					directoryGitIgnoreContext,
					directoryGitIgnoreCandidateContext));
				continue;
			}
		}

		var mergeLock = new object();
		var fileCounts = default(MutableIgnoreOptionCounts);
		var hasVisibleFilesByDirectory = ArrayPool<bool>.Shared.Rent(directories.Count);
		var isAccessDeniedByDirectory = ArrayPool<bool>.Shared.Rent(directories.Count);
		Array.Clear(hasVisibleFilesByDirectory, 0, directories.Count);
		Array.Clear(isAccessDeniedByDirectory, 0, directories.Count);

		try
		{
			for (var i = 0; i < directories.Count; i++)
				isAccessDeniedByDirectory[i] = directories[i].IsAccessDenied;

			var parallelOptions = ScanParallelismPolicy.CreateOptions(cancellationToken);
			Parallel.For(
				0,
				directories.Count,
				parallelOptions,
				() => new LocalExtensionScanState(),
				(index, _, localState) =>
				{
					parallelOptions.CancellationToken.ThrowIfCancellationRequested();
					var dir = directories[index].Path;
					var relativePath = directories[index].RelativePath;
					var directoryGitIgnoreContext = directories[index].GitIgnoreContext;
					var directoryGitIgnoreCandidateContext = directories[index].GitIgnoreCandidateContext;
					var shouldApplySmartIgnoreForFiles = rules.ShouldApplySmartIgnore(dir, isDirectory: true);

					var hasVisibleFiles = false;
					try
					{
						foreach (var file in FileSystemEntryEnumerator.EnumerateFiles(dir, relativePath))
						{
							parallelOptions.CancellationToken.ThrowIfCancellationRequested();

							if (collectIgnoreOptionCounts)
								AccumulateFileIgnoreOptionCounts(file, ref localState.Counts);

							var fileGitIgnore = useGitIgnore
								? directoryGitIgnoreContext.Evaluate(file.FullPath, file.RelativePath, isDirectory: false, file.Name)
								: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
							var fileGitIgnoreCandidate = rules.GitIgnoreCandidateMatchesActiveRules
								? fileGitIgnore.IsIgnored
								: directoryGitIgnoreCandidateContext
									.Evaluate(file.FullPath, file.RelativePath, isDirectory: false, file.Name)
									.IsIgnored;
							if (fileGitIgnoreCandidate)
								localState.GitIgnoreImpactCount++;
							if (ShouldSkipFileByName(
								    file.Name,
								    file.FullPath,
								    file.IsHidden,
								    file.Length,
								    rules,
								    shouldApplySmartIgnoreForFiles,
								    fileGitIgnore))
								continue;

							hasVisibleFiles = true;
							AddExtensionEntry(file.Name, localState.Extensions);
						}
					}
					catch (OperationCanceledException)
					{
						throw;
					}
					catch (UnauthorizedAccessException)
					{
						Interlocked.Exchange(ref hadAccessDenied, 1);
						isAccessDeniedByDirectory[index] = true;
						return localState;
					}
					catch
					{
						return localState;
					}

					hasVisibleFilesByDirectory[index] = hasVisibleFiles;
					return localState;
				},
				localState =>
				{
					if (localState.Extensions.Count == 0 &&
					    !collectIgnoreOptionCounts &&
					    localState.GitIgnoreImpactCount == 0)
						return;

					lock (mergeLock)
					{
						if (localState.Extensions.Count > 0)
							uniqueExtensions.UnionWith(localState.Extensions);
						if (collectIgnoreOptionCounts)
							fileCounts.Add(localState.Counts);
						gitIgnoreImpactCount += localState.GitIgnoreImpactCount;
					}
				});

			var emptyFolderCount = 0;
			if (collectIgnoreOptionCounts && directories.Count > 0)
			{
				// Bottom-up fold simulates IgnoreEmptyFolders pruning without a second filesystem pass.
				var nonPrunedChildCounts = ArrayPool<int>.Shared.Rent(directories.Count);
				Array.Clear(nonPrunedChildCounts, 0, directories.Count);
				try
				{
					for (var index = directories.Count - 1; index >= 0; index--)
					{
						var hasVisibleFiles = hasVisibleFilesByDirectory[index];
						var hasVisibleChildren = nonPrunedChildCounts[index] > 0;
						var isAccessDenied = isAccessDeniedByDirectory[index];
						var parentIndex = directories[index].ParentIndex;

						var shouldRemain = isAccessDenied || hasVisibleFiles || hasVisibleChildren;
						if (!shouldRemain)
						{
							if (parentIndex >= 0 || includeRootDirectoryInCounts)
								emptyFolderCount++;
							continue;
						}

						if (parentIndex >= 0)
							nonPrunedChildCounts[parentIndex]++;
					}
				}
				finally
				{
					ArrayPool<int>.Shared.Return(nonPrunedChildCounts);
				}
			}

			var counts = collectIgnoreOptionCounts
				? directoryCounts.ToImmutable().Add(fileCounts.ToImmutable()) with { EmptyFolders = emptyFolderCount }
				: IgnoreOptionCounts.Empty;

			return new ScanResult<ExtensionsScanData>(
				new ExtensionsScanData(
					uniqueExtensions,
					counts,
					new IgnoreControllerImpactCounts(GitIgnore: gitIgnoreImpactCount)),
				rootAccessDenied == 1,
				hadAccessDenied == 1);
		}
		finally
		{
			ArrayPool<bool>.Shared.Return(hasVisibleFilesByDirectory);
			ArrayPool<bool>.Shared.Return(isAccessDeniedByDirectory);
		}
	}

	private ScanResult<ExtensionsScanData> ScanRootFilesCore(
		string rootPath,
		IgnoreRules rules,
		bool collectIgnoreOptionCounts,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var useGitIgnore = rules.IsGitIgnoreTraversalEnabled;
		var gitIgnoreContext = rules.CreateGitIgnoreScanContext(rootPath);
		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
		{
			return new ScanResult<ExtensionsScanData>(
				new ExtensionsScanData(exts, IgnoreOptionCounts.Empty),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		if (useGitIgnore &&
		    gitIgnoreContext.RequiresTrackedPathIndex &&
		    GitTrackedPathIndexCache.TryLoadNearest(
			    rootPath,
			    cancellationToken,
			    out var trackedPathIndex))
		{
			gitIgnoreContext = gitIgnoreContext.WithTrackedPathIndex(trackedPathIndex);
		}

		var counts = default(MutableIgnoreOptionCounts);
		var shouldApplySmartIgnoreForFiles = rules.ShouldApplySmartIgnore(rootPath, isDirectory: true);
		try
		{
			foreach (var file in FileSystemEntryEnumerator.EnumerateFiles(rootPath))
			{
				cancellationToken.ThrowIfCancellationRequested();

				if (collectIgnoreOptionCounts)
					AccumulateFileIgnoreOptionCounts(file, ref counts);

				var fileGitIgnore = useGitIgnore
					? gitIgnoreContext.Evaluate(file.FullPath, file.RelativePath, isDirectory: false, file.Name)
					: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
				if (ShouldSkipFileByName(
					    file.Name,
					    file.FullPath,
					    file.IsHidden,
					    file.Length,
					    rules,
					    shouldApplySmartIgnoreForFiles,
					    fileGitIgnore))
					continue;

				AddExtensionEntry(file.Name, exts);
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (UnauthorizedAccessException)
		{
			return new ScanResult<ExtensionsScanData>(
				new ExtensionsScanData(exts, IgnoreOptionCounts.Empty),
				RootAccessDenied: true,
				HadAccessDenied: true);
		}
		catch
		{
			return new ScanResult<ExtensionsScanData>(
				new ExtensionsScanData(exts, IgnoreOptionCounts.Empty),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		return new ScanResult<ExtensionsScanData>(
			new ExtensionsScanData(exts, collectIgnoreOptionCounts ? counts.ToImmutable() : IgnoreOptionCounts.Empty),
			RootAccessDenied: false,
			HadAccessDenied: false);
	}

	private ScanResult<IgnoreOptionCounts> ScanEffectiveRootFileIgnoreOptionCountsCore(
		string rootPath,
		IReadOnlySet<string> allowedExtensions,
		IgnoreRules rules,
		CancellationToken cancellationToken)
	{
		var effectiveCountDiscoveryRules = BuildEffectiveCountDiscoveryRules(rules);
		var scan = ScanRootFileIgnoreSectionSnapshotCore(
			rootPath,
			effectiveCountDiscoveryRules,
			rules,
			CreateExtensionPolicy(allowedExtensions),
			cancellationToken);
		return new ScanResult<IgnoreOptionCounts>(
			scan.Value.EffectiveIgnoreOptionCounts,
			scan.RootAccessDenied,
			scan.HadAccessDenied);
	}

	private ScanResult<IgnoreOptionCounts> ScanEffectiveIgnoreOptionCountsCore(
		string rootPath,
		IReadOnlySet<string> allowedExtensions,
		IgnoreRules rules,
		CancellationToken cancellationToken)
	{
		var effectiveCountDiscoveryRules = BuildEffectiveCountDiscoveryRules(rules);
		var scan = ScanIgnoreSectionSnapshotCore(
			rootPath,
			effectiveCountDiscoveryRules,
			rules,
			CreateExtensionPolicy(allowedExtensions),
			includeRootDirectoryInRawCounts: true,
			cancellationToken);
		return new ScanResult<IgnoreOptionCounts>(
			scan.Value.EffectiveIgnoreOptionCounts,
			scan.RootAccessDenied,
			scan.HadAccessDenied);
	}

	private ScanResult<IgnoreSectionScanData> ScanRootFileIgnoreSectionSnapshotCore(
		string rootPath,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		CancellationToken cancellationToken,
		List<ProjectTreeInventoryEntry>? treeInventoryFiles = null)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var effectiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var rawCounts = default(MutableIgnoreOptionCounts);
		var effectiveCounts = default(MutableIgnoreOptionCounts);
		var controllerImpactCounts = IgnoreControllerImpactCounts.Empty;

		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
		{
			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					extensions,
					IgnoreOptionCounts.Empty,
					IgnoreOptionCounts.Empty,
					IgnoreControllerImpactCounts.Empty,
					effectiveExtensions),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		var gitEvidence = ReadGitWorkspaceEvidenceAtRoot(rootPath);

		// The combined ignore-section path intentionally reuses one file-fact record.
		// This keeps the snapshot cheap enough for live refreshes, so the caller must
		// keep structural ignore semantics aligned between discovery and effective rules.
		var effectiveGitIgnoreContext = effectiveRules.CreateGitIgnoreScanContext(rootPath);
		var effectiveGitIgnoreCandidateContext = effectiveRules.CreateGitIgnoreCandidateScanContext(rootPath);
		if (effectiveRules.IsGitIgnoreTraversalEnabled)
		{
			(effectiveGitIgnoreContext, effectiveGitIgnoreCandidateContext, _) =
				EnterNearestGitTrackedPathIndex(
					rootPath,
					effectiveGitIgnoreContext,
					effectiveGitIgnoreCandidateContext,
					cancellationToken);
		}

		var shouldApplySmartIgnoreForFiles = effectiveRules.ShouldApplySmartIgnore(rootPath, isDirectory: true);
		var shouldApplySmartIgnoreCandidateForFiles =
			effectiveRules.ShouldApplySmartIgnoreCandidate(rootPath, isDirectory: true);

		try
		{
			foreach (var file in FileSystemEntryEnumerator.EnumerateFiles(rootPath))
			{
				cancellationToken.ThrowIfCancellationRequested();
				treeInventoryFiles?.Add(new ProjectTreeInventoryEntry(
					file.Name,
					file.FullPath,
					file.RelativePath,
					parentIndex: 0,
					isDirectory: false,
					file.IsHidden,
					file.Length));

				AccumulateFileIgnoreOptionCounts(file, ref rawCounts);

				var facts = AnalyzeFile(
					file.FullPath,
					file.RelativePath,
					file.Name,
					file.IsHidden,
					file.Length,
					shouldApplySmartIgnoreForFiles,
					shouldApplySmartIgnoreCandidateForFiles,
					effectiveRules,
					effectiveGitIgnoreContext,
					effectiveGitIgnoreCandidateContext);
				var passesDiscovery = PassesExtensionDiscoveryRules(facts, extensionDiscoveryRules);
				if (passesDiscovery)
					AddExtensionEntry(
						facts,
						extensions,
						skipExtensionlessEntry: effectiveRules.IgnoreExtensionlessFiles);
				if (PassesExtensionDiscoveryRules(facts, effectiveRules))
					AddExtensionEntry(
						facts,
						effectiveExtensions,
						skipExtensionlessEntry: effectiveRules.IgnoreExtensionlessFiles);

				var visibility = EvaluateFileVisibilityProfile(
					facts,
					effectiveExtensionPolicy,
					effectiveRules,
					passesDiscovery);
				AccumulateDirectFileDelta(facts.IsHidden, visibility.BaseVisible, visibility.HiddenFilesVisible, ref effectiveCounts.HiddenFiles);
				AccumulateDirectFileDelta(facts.IsDot, visibility.BaseVisible, visibility.DotFilesVisible, ref effectiveCounts.DotFiles);
				AccumulateDirectFileDelta(facts.IsEmpty, visibility.BaseVisible, visibility.EmptyFilesVisible, ref effectiveCounts.EmptyFiles);
				AccumulateDirectFileDelta(
					facts.IsExtensionless,
					visibility.BaseVisible,
					visibility.ExtensionlessFilesVisible,
					ref effectiveCounts.ExtensionlessFiles);
				controllerImpactCounts = controllerImpactCounts.Add(
					CountFileControllerImpact(visibility, effectiveRules.IsGitIgnoreTraversalEnabled));
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (UnauthorizedAccessException)
		{
			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					extensions,
					IgnoreOptionCounts.Empty,
					IgnoreOptionCounts.Empty,
					IgnoreControllerImpactCounts.Empty,
					effectiveExtensions,
					GitEvidence: gitEvidence),
				RootAccessDenied: true,
				HadAccessDenied: true);
		}
		catch
		{
			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					extensions,
					IgnoreOptionCounts.Empty,
					IgnoreOptionCounts.Empty,
					IgnoreControllerImpactCounts.Empty,
					effectiveExtensions,
					GitEvidence: gitEvidence),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		return new ScanResult<IgnoreSectionScanData>(
			new IgnoreSectionScanData(
				extensions,
				rawCounts.ToImmutable(),
				effectiveCounts.ToImmutable(),
				controllerImpactCounts,
				effectiveExtensions,
				GitEvidence: gitEvidence),
			RootAccessDenied: false,
			HadAccessDenied: false);
	}

	private static GitWorkspaceEvidence ReadGitWorkspaceEvidenceAtRoot(string rootPath)
	{
		try
		{
			var attributes = File.GetAttributes(Path.Combine(rootPath, ".git"));
			return attributes.HasFlag(FileAttributes.ReparsePoint)
				? GitWorkspaceEvidence.Empty
				: new GitWorkspaceEvidence(HasRepositoryBoundary: true);
		}
		catch
		{
			// Structural evidence is best-effort; normal access diagnostics remain authoritative.
			return GitWorkspaceEvidence.Empty;
		}
	}

	private ScanResult<IgnoreSectionScanData> ScanIgnoreSectionSnapshotCore(
		string rootPath,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		bool includeRootDirectoryInRawCounts,
		CancellationToken cancellationToken,
		ProjectTreeInventoryCapture? treeInventoryCapture = null)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var discovery = DiscoverEffectiveIgnoreScanNodes(
			rootPath,
			extensionDiscoveryRules,
			effectiveRules,
			effectiveExtensionPolicy,
			cancellationToken);
		var directories = discovery.Value.Nodes;
		if (directories.Count == 0)
		{
			if (treeInventoryCapture is not null)
				treeInventoryCapture.Inventory = null;

			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase),
					IgnoreOptionCounts.Empty,
					IgnoreOptionCounts.Empty,
					IgnoreControllerImpactCounts.Empty,
					GitEvidence: discovery.Value.GitEvidence),
				discovery.RootAccessDenied,
				discovery.HadAccessDenied);
		}

		var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var effectiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var rawCounts = default(MutableIgnoreOptionCounts);
		var fileMetrics = ArrayPool<EffectiveIgnoreNodeFileMetrics>.Shared.Rent(directories.Count);
		var visibilityStates = ArrayPool<EffectiveIgnoreNodeVisibilityState>.Shared.Rent(directories.Count);
		var treeInventoryFiles = treeInventoryCapture is null
			? null
			: new List<ProjectTreeInventoryEntry>?[directories.Count];
		var treeInventoryDirectoryIncluded = treeInventoryCapture is null
			? null
			: ArrayPool<bool>.Shared.Rent(directories.Count);
		Array.Clear(fileMetrics, 0, directories.Count);
		Array.Clear(visibilityStates, 0, directories.Count);
		if (treeInventoryDirectoryIncluded is not null)
			Array.Clear(treeInventoryDirectoryIncluded, 0, directories.Count);
		var hadAccessDenied = discovery.HadAccessDenied ? 1 : 0;
		var mergeLock = new object();
		try
		{
			for (var index = 0; index < directories.Count; index++)
				visibilityStates[index].IsAccessDenied = directories[index].IsAccessDenied;

			if (treeInventoryDirectoryIncluded is not null)
			{
				for (var index = 0; index < directories.Count; index++)
				{
					var node = directories[index];
					treeInventoryDirectoryIncluded[index] =
						node.CanAnyVariantTraverseChildren &&
						(node.ParentIndex < 0 || treeInventoryDirectoryIncluded[node.ParentIndex]);
				}
			}

			for (var index = 0; index < directories.Count; index++)
			{
				var node = directories[index];
				var visibilityState = visibilityStates[index];
				// Discovery visibility tracks whether this subtree is reachable for extension/raw inventory.
				// It intentionally ignores empty-folder pruning and only follows the structural discovery path.
				visibilityState.ExtensionDiscoveryFinalVisible = node.ParentIndex < 0
					? node.ExtensionDiscoveryRuleState.CanTraverseChildren
					: visibilityStates[node.ParentIndex].ExtensionDiscoveryFinalVisible &&
					  node.ExtensionDiscoveryRuleState.CanTraverseChildren;
				visibilityStates[index] = visibilityState;
			}

			EffectiveIgnoreNodeFileMetrics ScanDirectoryFiles(
				int index,
				CancellationToken token,
				HashSet<string> localExtensions,
				HashSet<string> localEffectiveExtensions,
				ref MutableIgnoreOptionCounts localRawCounts)
			{
				token.ThrowIfCancellationRequested();

				var node = directories[index];
				if (!node.CanAnyVariantTraverseChildren)
					return default;

				var shouldApplySmartIgnoreForFiles = effectiveRules.ShouldApplySmartIgnore(node.Path, isDirectory: true);
				var shouldApplySmartIgnoreCandidateForFiles =
					effectiveRules.ShouldApplySmartIgnoreCandidate(node.Path, isDirectory: true);
				var extensionDiscoveryVisible = visibilityStates[index].ExtensionDiscoveryFinalVisible;
				var localMetrics = default(EffectiveIgnoreNodeFileMetrics);

				try
				{
					foreach (var file in FileSystemEntryEnumerator.EnumerateFiles(node.Path, node.RelativePath))
					{
						token.ThrowIfCancellationRequested();
						if (treeInventoryDirectoryIncluded is not null &&
						    treeInventoryDirectoryIncluded[index])
						{
							(treeInventoryFiles![index] ??= []).Add(new ProjectTreeInventoryEntry(
								file.Name,
								file.FullPath,
								file.RelativePath,
								parentIndex: -1,
								isDirectory: false,
								file.IsHidden,
								file.Length));
						}

						var facts = AnalyzeFile(
							file.FullPath,
							file.RelativePath,
							file.Name,
							file.IsHidden,
							file.Length,
							shouldApplySmartIgnoreForFiles,
							shouldApplySmartIgnoreCandidateForFiles,
							effectiveRules,
							node.GitIgnoreContext,
							node.GitIgnoreCandidateContext);
						if (extensionDiscoveryVisible)
							AccumulateFileIgnoreOptionCounts(file, ref localRawCounts);

						// When "all extensions" is selected we still must respect the discovery result.
						// Otherwise a subtree hidden from extension availability could leak back into the
						// effective counts simply because it contains a syntactically valid extension.
						var passesDiscovery = extensionDiscoveryVisible &&
						                      PassesExtensionDiscoveryRules(facts, extensionDiscoveryRules);
						if (passesDiscovery)
						{
							AddExtensionEntry(
								facts,
								localExtensions,
								skipExtensionlessEntry: effectiveRules.IgnoreExtensionlessFiles);
							localMetrics.ExtensionDiscoveryVisibleFiles++;
						}

						if (extensionDiscoveryVisible &&
						    PassesExtensionDiscoveryRules(facts, effectiveRules))
						{
							AddExtensionEntry(
								facts,
								localEffectiveExtensions,
								skipExtensionlessEntry: effectiveRules.IgnoreExtensionlessFiles);
						}

						var visibility = EvaluateFileVisibilityProfile(
							facts,
							effectiveExtensionPolicy,
							effectiveRules,
							passesDiscovery);

						if (visibility.BaseVisible)
							localMetrics.BaseVisibleFiles++;
						if (visibility.HiddenFilesVisible)
							localMetrics.HiddenFilesVisibleFiles++;
						if (visibility.DotFilesVisible)
							localMetrics.DotFilesVisibleFiles++;
						if (visibility.EmptyFilesVisible)
							localMetrics.EmptyFilesVisibleFiles++;
						if (visibility.ExtensionlessFilesVisible)
							localMetrics.ExtensionlessFilesVisibleFiles++;
						if (visibility.ControllerBaselineVisible)
							localMetrics.ControllerBaselineVisibleFiles++;
						if (visibility.GitIgnoreVisible)
							localMetrics.GitIgnoreVisibleFiles++;
						if (visibility.SmartIgnoreVisible)
							localMetrics.SmartIgnoreVisibleFiles++;

						AccumulateToggleTransition(
							facts.IsHidden,
							visibility.BaseVisible,
							visibility.HiddenFilesVisible,
							ref localMetrics.HiddenFilesAppearWhenToggled,
							ref localMetrics.HiddenFilesDisappearWhenToggled);
						AccumulateToggleTransition(
							facts.IsDot,
							visibility.BaseVisible,
							visibility.DotFilesVisible,
							ref localMetrics.DotFilesAppearWhenToggled,
							ref localMetrics.DotFilesDisappearWhenToggled);
						AccumulateToggleTransition(
							facts.IsEmpty,
							visibility.BaseVisible,
							visibility.EmptyFilesVisible,
							ref localMetrics.EmptyFilesAppearWhenToggled,
							ref localMetrics.EmptyFilesDisappearWhenToggled);
						AccumulateToggleTransition(
							facts.IsExtensionless,
							visibility.BaseVisible,
							visibility.ExtensionlessFilesVisible,
							ref localMetrics.ExtensionlessFilesAppearWhenToggled,
							ref localMetrics.ExtensionlessFilesDisappearWhenToggled);
					}
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (UnauthorizedAccessException)
				{
					Interlocked.Exchange(ref hadAccessDenied, 1);
					var visibilityState = visibilityStates[index];
					visibilityState.IsAccessDenied = true;
					visibilityStates[index] = visibilityState;
					return default;
				}
				catch
				{
					return default;
				}

				return localMetrics;
			}

			var parallelOptions = ScanParallelismPolicy.CreateOptions(cancellationToken);
			Parallel.For(
				0,
				directories.Count,
				parallelOptions,
				() => new IgnoreSectionSnapshotLocalState(),
				(index, _, localState) =>
				{
					fileMetrics[index] = ScanDirectoryFiles(
						index,
						parallelOptions.CancellationToken,
						localState.Extensions,
						localState.EffectiveExtensions,
						ref localState.RawCounts);
					return localState;
				},
				localState =>
				{
					if (localState.Extensions.Count == 0 &&
					    localState.EffectiveExtensions.Count == 0 &&
					    localState.RawCounts.IsEmpty)
					{
						return;
					}

					lock (mergeLock)
					{
						rawCounts.Add(localState.RawCounts);
						if (localState.Extensions.Count > 0)
							extensions.UnionWith(localState.Extensions);
						if (localState.EffectiveExtensions.Count > 0)
							effectiveExtensions.UnionWith(localState.EffectiveExtensions);
					}
				});

			for (var index = 0; index < directories.Count; index++)
			{
				var node = directories[index];
				var parentDiscoveryVisible = node.ParentIndex >= 0 &&
				                            visibilityStates[node.ParentIndex].ExtensionDiscoveryFinalVisible;
				var shouldCountNode = node.ParentIndex < 0
					? includeRootDirectoryInRawCounts && visibilityStates[index].ExtensionDiscoveryFinalVisible
					: parentDiscoveryVisible;
				if (!shouldCountNode)
					continue;

				if (node.IsDot)
					rawCounts.DotFolders++;
				if (node.IsHidden)
					rawCounts.HiddenFolders++;
			}

			for (var index = directories.Count - 1; index >= 0; index--)
			{
				var node = directories[index];
				var visibilityState = visibilityStates[index];
				if (!visibilityState.ExtensionDiscoveryFinalVisible)
					continue;

				// Raw empty-folder inventory must match the historical extension scan contract:
				// count only folders that are part of the discovery-visible subtree and become
				// logically empty after discovery rules, without mixing in effective toggle state.
				var hasVisibleContent = visibilityState.IsAccessDenied ||
				                        fileMetrics[index].ExtensionDiscoveryVisibleFiles > 0 ||
				                        visibilityState.RawDiscoveryVisibleChildren > 0;
				if (!hasVisibleContent)
				{
					if (node.ParentIndex >= 0 || includeRootDirectoryInRawCounts)
						rawCounts.EmptyFolders++;
					continue;
				}

				if (node.ParentIndex >= 0)
				{
					var parentVisibilityState = visibilityStates[node.ParentIndex];
					parentVisibilityState.RawDiscoveryVisibleChildren++;
					visibilityStates[node.ParentIndex] = parentVisibilityState;
				}
			}

			var finalizedCounts = FinalizeEffectiveIgnoreCounts(directories, fileMetrics, visibilityStates, effectiveRules);
			var rootVisibility = visibilityStates[0];
			if (treeInventoryCapture is not null)
			{
				treeInventoryCapture.Inventory = BuildSubtreeInventory(
					directories,
					treeInventoryFiles!,
					treeInventoryDirectoryIncluded!,
					discovery.RootAccessDenied,
					hadAccessDenied == 1,
					discovery.Value.DiscoveredGitIgnoreMatchers,
					discovery.Value.DiscoveredGitTrackedPathIndexes);
			}

			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					extensions,
					rawCounts.ToImmutable(),
					finalizedCounts.IgnoreOptionCounts,
					finalizedCounts.ControllerImpactCounts,
					effectiveExtensions,
					HasVisibleTreeStructure: rootVisibility.BaseFinalVisible,
					IsTreeStructureHiddenByEmptyFolders:
						!rootVisibility.BaseFinalVisible && rootVisibility.EmptyFoldersFinalVisible,
					GitEvidence: discovery.Value.GitEvidence),
				discovery.RootAccessDenied,
				hadAccessDenied == 1);
		}
		finally
		{
			ArrayPool<EffectiveIgnoreNodeFileMetrics>.Shared.Return(fileMetrics);
			ArrayPool<EffectiveIgnoreNodeVisibilityState>.Shared.Return(visibilityStates);
			if (treeInventoryDirectoryIncluded is not null)
				ArrayPool<bool>.Shared.Return(treeInventoryDirectoryIncluded);
		}
	}

	private static ProjectTreeInventorySnapshot? BuildSubtreeInventory(
		IReadOnlyList<EffectiveIgnoreScanNode> directories,
		List<ProjectTreeInventoryEntry>?[] treeInventoryFiles,
		bool[] treeInventoryDirectoryIncluded,
		bool rootAccessDenied,
		bool hadAccessDenied,
		IReadOnlyList<ScopedGitIgnoreMatcher> discoveredGitIgnoreMatchers,
		IReadOnlyList<GitTrackedPathIndex> discoveredGitTrackedPathIndexes)
	{
		if (directories.Count == 0 || !treeInventoryDirectoryIncluded[0])
			return null;

		var childDirectories = BuildIncludedChildDirectoryIndex(directories, treeInventoryDirectoryIncluded);
		var entries = new List<ProjectTreeInventoryEntry>(Math.Max(1, directories.Count));

		AddDirectoryShell(sourceIndex: 0, parentIndex: -1);
		PopulateDirectory(sourceIndex: 0, targetIndex: 0);
		return new ProjectTreeInventorySnapshot(
			entries,
			rootAccessDenied,
			hadAccessDenied,
			discoveredGitIgnoreMatchers,
			discoveredGitTrackedPathIndexes);

		int AddDirectoryShell(int sourceIndex, int parentIndex)
		{
			var node = directories[sourceIndex];
			var entry = new ProjectTreeInventoryEntry(
				node.Name,
				node.Path,
				node.RelativePath,
				parentIndex,
				isDirectory: true,
				node.IsHidden,
				length: 0)
			{
				IsAccessDenied = node.IsAccessDenied
			};
			var targetIndex = entries.Count;
			entries.Add(entry);
			return targetIndex;
		}

		void PopulateDirectory(int sourceIndex, int targetIndex)
		{
			var directoryChildren = childDirectories[sourceIndex];
			var fileChildren = treeInventoryFiles[sourceIndex];
			if ((directoryChildren is null || directoryChildren.Count == 0) &&
			    (fileChildren is null || fileChildren.Count == 0))
			{
				return;
			}

			SortDirectoryIndexes(directoryChildren, directories);
			SortInventoryEntries(fileChildren);

			// ProjectTreeInventorySnapshot requires every node's direct children to occupy
			// one contiguous range. Add directory shells and files first, then append each
			// directory subtree after that range exactly like ProjectTreeInventoryScanner.
			var firstChildIndex = entries.Count;
			var childDirectoryPairs = new List<(int SourceIndex, int TargetIndex)>(directoryChildren?.Count ?? 0);
			if (directoryChildren is not null)
			{
				foreach (var childSourceIndex in directoryChildren)
				{
					var childTargetIndex = AddDirectoryShell(childSourceIndex, targetIndex);
					childDirectoryPairs.Add((childSourceIndex, childTargetIndex));
				}
			}

			if (fileChildren is not null)
			{
				foreach (var file in fileChildren)
				{
					entries.Add(new ProjectTreeInventoryEntry(
						file.Name,
						file.FullPath,
						file.RelativePath,
						targetIndex,
						isDirectory: false,
						file.IsHidden,
						file.Length));
				}
			}

			var childCount = entries.Count - firstChildIndex;
			if (childCount > 0)
			{
				var parent = entries[targetIndex];
				parent.FirstChildIndex = firstChildIndex;
				parent.ChildCount = childCount;
				entries[targetIndex] = parent;
			}

			foreach (var (childSourceIndex, childTargetIndex) in childDirectoryPairs)
				PopulateDirectory(childSourceIndex, childTargetIndex);
		}
	}

	private static List<int>?[] BuildIncludedChildDirectoryIndex(
		IReadOnlyList<EffectiveIgnoreScanNode> directories,
		bool[] includedDirectories)
	{
		var childDirectories = new List<int>?[directories.Count];
		for (var index = 1; index < directories.Count; index++)
		{
			if (!includedDirectories[index])
				continue;

			var parentIndex = directories[index].ParentIndex;
			if (parentIndex < 0 || !includedDirectories[parentIndex])
				continue;

			(childDirectories[parentIndex] ??= []).Add(index);
		}

		return childDirectories;
	}

	private static ProjectTreeInventorySnapshot BuildRootSelectionInventory(
		string rootPath,
		List<ProjectTreeInventoryEntry> rootFileEntries,
		List<ProjectTreeInventorySnapshot> subtreeInventories,
		bool rootAccessDenied,
		bool hadAccessDenied,
		GitTrackedPathIndex? rootTrackedPathIndex)
	{
		var rootName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		if (string.IsNullOrEmpty(rootName))
			rootName = rootPath;

		var entries = new List<ProjectTreeInventoryEntry>(
			1 + rootFileEntries.Count + subtreeInventories.Sum(static inventory => inventory.Entries.Count))
		{
			new(
				rootName,
				rootPath,
				relativePath: string.Empty,
				parentIndex: -1,
				isDirectory: true,
				HasHiddenAttribute(rootPath),
				length: 0)
		};

		subtreeInventories.Sort(CompareInventoryRootNames);
		SortInventoryEntries(rootFileEntries);
		var discoveredGitIgnoreMatchers = MergeDiscoveredGitIgnoreMatchers(subtreeInventories);
		var discoveredGitTrackedPathIndexes = MergeDiscoveredGitTrackedPathIndexes(
			subtreeInventories,
			rootTrackedPathIndex);

		var firstChildIndex = entries.Count;
		var rootChildIndexes = new List<int>(subtreeInventories.Count);
		var rootChildRelativePaths = new List<string>(subtreeInventories.Count);
		foreach (var subtree in subtreeInventories)
		{
			ref readonly var subtreeRoot = ref subtree.GetEntryRef(0);
			var rootChildRelativePath = string.IsNullOrEmpty(subtreeRoot.RelativePath)
				? subtreeRoot.Name
				: subtreeRoot.RelativePath;
			var rootChildIndex = entries.Count;
			rootChildIndexes.Add(rootChildIndex);
			rootChildRelativePaths.Add(rootChildRelativePath);
			entries.Add(new ProjectTreeInventoryEntry(
				subtreeRoot.Name,
				subtreeRoot.FullPath,
				rootChildRelativePath,
				parentIndex: 0,
				isDirectory: true,
				subtreeRoot.IsHidden,
				length: 0)
			{
				IsAccessDenied = subtreeRoot.IsAccessDenied
			});
		}

		foreach (var file in rootFileEntries)
		{
			entries.Add(new ProjectTreeInventoryEntry(
				file.Name,
				file.FullPath,
				file.RelativePath,
				parentIndex: 0,
				isDirectory: false,
				file.IsHidden,
				file.Length));
		}

		var rootChildCount = entries.Count - firstChildIndex;
		if (rootChildCount > 0)
		{
			var root = entries[0];
			root.FirstChildIndex = firstChildIndex;
			root.ChildCount = rootChildCount;
			entries[0] = root;
		}

		for (var subtreeIndex = 0; subtreeIndex < subtreeInventories.Count; subtreeIndex++)
		{
			AppendSubtreeInventory(
				entries,
				rootChildIndexes[subtreeIndex],
				rootChildRelativePaths[subtreeIndex],
				subtreeInventories[subtreeIndex]);
		}

		return new ProjectTreeInventorySnapshot(
			entries,
			rootAccessDenied,
			hadAccessDenied,
			discoveredGitIgnoreMatchers,
			discoveredGitTrackedPathIndexes);
	}

	private static IReadOnlyList<ScopedGitIgnoreMatcher> MergeDiscoveredGitIgnoreMatchers(
		IReadOnlyList<ProjectTreeInventorySnapshot> inventories)
	{
		Dictionary<string, ScopedGitIgnoreMatcher>? unique = null;
		foreach (var inventory in inventories)
		{
			foreach (var matcher in inventory.DiscoveredGitIgnoreMatchers)
			{
				unique ??= new Dictionary<string, ScopedGitIgnoreMatcher>(PathComparer.Default);
				unique[matcher.ScopeRootPath] = matcher;
			}
		}

		if (unique is null)
			return [];

		var merged = unique.Values.ToList();
		merged.Sort(CompareScopedGitIgnoreMatchers);
		return merged;
	}

	private static IReadOnlyList<GitTrackedPathIndex> MergeDiscoveredGitTrackedPathIndexes(
		IReadOnlyList<ProjectTreeInventorySnapshot> inventories,
		GitTrackedPathIndex? additionalIndex = null)
	{
		Dictionary<string, GitTrackedPathIndex>? unique = null;
		foreach (var inventory in inventories)
		{
			foreach (var index in inventory.DiscoveredGitTrackedPathIndexes)
			{
				unique ??= new Dictionary<string, GitTrackedPathIndex>(PathComparer.Default);
				unique[index.RepositoryRootPath] = index;
			}
		}

		if (additionalIndex is not null)
		{
			unique ??= new Dictionary<string, GitTrackedPathIndex>(PathComparer.Default);
			unique[additionalIndex.RepositoryRootPath] = additionalIndex;
		}

		if (unique is null)
			return [];

		var merged = unique.Values.ToList();
		merged.Sort(static (left, right) =>
		{
			var depth = left.RepositoryRootPath.Length.CompareTo(right.RepositoryRootPath.Length);
			return depth != 0
				? depth
				: PathComparer.Default.Compare(left.RepositoryRootPath, right.RepositoryRootPath);
		});
		return merged;
	}

	private static GitTrackedPathIndex? TryLoadRootTrackedPathIndex(
		string rootPath,
		IgnoreRules rules,
		CancellationToken cancellationToken)
	{
		if (!rules.IsGitIgnoreTraversalEnabled)
			return null;

		var activeContext = rules.CreateGitIgnoreScanContext(rootPath);
		var candidateContext = rules.CreateGitIgnoreCandidateScanContext(rootPath);
		return (activeContext.RequiresTrackedPathIndex || candidateContext.RequiresTrackedPathIndex) &&
		       GitTrackedPathIndexCache.TryLoadNearest(
			       rootPath,
			       cancellationToken,
			       out var trackedPathIndex)
			? trackedPathIndex
			: null;
	}

	private static void AppendSubtreeInventory(
		List<ProjectTreeInventoryEntry> targetEntries,
		int rootTargetIndex,
		string rootRelativePath,
		ProjectTreeInventorySnapshot subtree)
	{
		ref readonly var sourceRoot = ref subtree.GetEntryRef(0);
		if (subtree.Entries.Count == 1)
		{
			var rootOnly = targetEntries[rootTargetIndex];
			rootOnly.IsAccessDenied = sourceRoot.IsAccessDenied;
			targetEntries[rootTargetIndex] = rootOnly;
			return;
		}

		var targetBaseIndex = targetEntries.Count;
		var root = targetEntries[rootTargetIndex];
		root.IsAccessDenied = sourceRoot.IsAccessDenied;
		root.ChildCount = sourceRoot.ChildCount;
		root.FirstChildIndex = sourceRoot.FirstChildIndex < 0
			? -1
			: targetBaseIndex + sourceRoot.FirstChildIndex - 1;
		targetEntries[rootTargetIndex] = root;

		for (var sourceIndex = 1; sourceIndex < subtree.Entries.Count; sourceIndex++)
		{
			ref readonly var source = ref subtree.GetEntryRef(sourceIndex);
			var parentIndex = source.ParentIndex == 0
				? rootTargetIndex
				: targetBaseIndex + source.ParentIndex - 1;
			var firstChildIndex = source.FirstChildIndex < 0
				? -1
				: targetBaseIndex + source.FirstChildIndex - 1;

			targetEntries.Add(new ProjectTreeInventoryEntry(
				source.Name,
				source.FullPath,
				RebaseSubtreeRelativePath(rootRelativePath, source.RelativePath),
				parentIndex,
				source.IsDirectory,
				source.IsHidden,
				source.Length)
			{
				IsAccessDenied = source.IsAccessDenied,
				FirstChildIndex = firstChildIndex,
				ChildCount = source.ChildCount
			});
		}
	}

	private static string RebaseSubtreeRelativePath(string rootRelativePath, string subtreeRelativePath)
	{
		// Selected-root scans use paths relative to that selected root. The merged
		// workspace inventory must expose paths relative to the opened project root.
		return string.IsNullOrEmpty(subtreeRelativePath)
			? rootRelativePath
			: $"{rootRelativePath}/{subtreeRelativePath}";
	}

	private static void SortDirectoryIndexes(
		List<int>? directoryIndexes,
		IReadOnlyList<EffectiveIgnoreScanNode> directories)
	{
		directoryIndexes?.Sort((left, right) => string.Compare(
			directories[left].Name,
			directories[right].Name,
			StringComparison.OrdinalIgnoreCase));
	}

	private static void SortInventoryEntries(List<ProjectTreeInventoryEntry>? entries)
	{
		entries?.Sort(CompareInventoryEntries);
	}

	private static int CompareInventoryEntries(ProjectTreeInventoryEntry left, ProjectTreeInventoryEntry right)
	{
		if (left.IsDirectory != right.IsDirectory)
			return left.IsDirectory ? -1 : 1;

		return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
	}

	private static int CompareInventoryRootNames(ProjectTreeInventorySnapshot left, ProjectTreeInventorySnapshot right)
	{
		ref readonly var leftRoot = ref left.GetEntryRef(0);
		ref readonly var rightRoot = ref right.GetEntryRef(0);
		return string.Compare(leftRoot.Name, rightRoot.Name, StringComparison.OrdinalIgnoreCase);
	}

	private ScanResult<EffectiveIgnoreScanDiscovery> DiscoverEffectiveIgnoreScanNodes(
		string rootPath,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
		{
			return new ScanResult<EffectiveIgnoreScanDiscovery>(
				new EffectiveIgnoreScanDiscovery([], [], [], GitWorkspaceEvidence.Empty),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		var rootName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		var effectiveGitIgnoreContext = effectiveRules.CreateGitIgnoreScanContext(rootPath);
		var effectiveGitIgnoreCandidateContext = effectiveRules.CreateGitIgnoreCandidateScanContext(rootPath);
		GitTrackedPathIndex? inheritedTrackedPathIndex = null;
		if (effectiveRules.IsGitIgnoreTraversalEnabled)
		{
			(effectiveGitIgnoreContext, effectiveGitIgnoreCandidateContext, inheritedTrackedPathIndex) =
				EnterNearestGitTrackedPathIndex(
					rootPath,
					effectiveGitIgnoreContext,
					effectiveGitIgnoreCandidateContext,
					cancellationToken);
		}

		var rootFacts = AnalyzeDirectory(
			rootPath,
			string.Empty,
			rootName,
			HasHiddenAttribute(rootPath),
			effectiveRules,
			effectiveGitIgnoreContext,
			effectiveGitIgnoreCandidateContext);
		var rootDirectControllerImpactCounts = CountDirectDirectoryControllerImpact(
			rootFacts,
			effectiveRules,
			effectiveExtensionPolicy,
			cancellationToken);
		var rootExtensionDiscoveryRuleState = EvaluateDirectoryRuleState(
			rootFacts,
			extensionDiscoveryRules.IgnoreHiddenFolders,
			extensionDiscoveryRules.IgnoreDotFolders);
		var rootBaseRuleState = EvaluateDirectoryRuleState(rootFacts, effectiveRules.IgnoreHiddenFolders, effectiveRules.IgnoreDotFolders);
		var rootHiddenFoldersRuleState = EvaluateDirectoryRuleState(rootFacts, !effectiveRules.IgnoreHiddenFolders, effectiveRules.IgnoreDotFolders);
		var rootDotFoldersRuleState = EvaluateDirectoryRuleState(
			rootFacts,
			ShouldApplyHiddenFoldersForDotFoldersVariant(rootFacts, effectiveRules),
			!effectiveRules.IgnoreDotFolders);

		if (!CanAnyVariantTraverseChildren(rootExtensionDiscoveryRuleState, rootBaseRuleState, rootHiddenFoldersRuleState, rootDotFoldersRuleState) &&
		    rootDirectControllerImpactCounts == IgnoreControllerImpactCounts.Empty)
		{
			return new ScanResult<EffectiveIgnoreScanDiscovery>(
				new EffectiveIgnoreScanDiscovery([], [], [], GitWorkspaceEvidence.Empty),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		var directories = new List<EffectiveIgnoreScanNode>(capacity: 256);
		var discoveredGitIgnoreMatchers = new List<ScopedGitIgnoreMatcher>();
		var discoveredGitTrackedPathIndexes = new List<GitTrackedPathIndex>();
		var gitEvidence = GitWorkspaceEvidence.Empty;
		if (inheritedTrackedPathIndex is not null)
			discoveredGitTrackedPathIndexes.Add(inheritedTrackedPathIndex);
		var rootAccessDenied = 0;
		var hadAccessDenied = 0;
		var pending =
			new Stack<(
				DirectoryScanFacts Facts,
				int ParentIndex,
				bool IsRootDirectory,
				IgnoreControllerImpactCounts DirectControllerImpactCounts,
				DirectoryToggleRuleState ExtensionDiscoveryRuleState,
				DirectoryToggleRuleState BaseRuleState,
				DirectoryToggleRuleState HiddenFoldersRuleState,
				DirectoryToggleRuleState DotFoldersRuleState,
				IgnoreRules.GitIgnoreScanContext GitIgnoreContext,
				IgnoreRules.GitIgnoreScanContext GitIgnoreCandidateContext)>();
		pending.Push((
			rootFacts,
			ParentIndex: -1,
			IsRootDirectory: true,
			rootDirectControllerImpactCounts,
			rootExtensionDiscoveryRuleState,
			rootBaseRuleState,
			rootHiddenFoldersRuleState,
			rootDotFoldersRuleState,
			effectiveGitIgnoreContext,
			effectiveGitIgnoreCandidateContext));

		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var (
				facts,
				parentIndex,
				isRootDirectory,
				directControllerImpactCounts,
				extensionDiscoveryRuleState,
				baseRuleState,
				hiddenFoldersRuleState,
				dotFoldersRuleState,
				parentGitIgnoreContext,
				parentGitIgnoreCandidateContext) = pending.Pop();
			var currentDirectoryIndex = directories.Count;
			var canTraverseChildren = CanAnyVariantTraverseChildren(
				extensionDiscoveryRuleState,
				baseRuleState,
				hiddenFoldersRuleState,
				dotFoldersRuleState);
			var gitIgnoreContext = parentGitIgnoreContext;
			var gitIgnoreCandidateContext = parentGitIgnoreCandidateContext;
			DirectoryEnumerationBatch directoryBatch = default;

			if (canTraverseChildren)
			{
				try
				{
					directoryBatch = FileSystemEntryEnumerator.ReadDirectoriesAndGitIgnore(
						facts.FullPath,
						facts.RelativePath,
						cancellationToken);
					if (!string.IsNullOrWhiteSpace(directoryBatch.GitMetadataPath))
						gitEvidence = new GitWorkspaceEvidence(HasRepositoryBoundary: true);
					(gitIgnoreContext, gitIgnoreCandidateContext) = EnterGitIgnoreScope(
						facts.FullPath,
						facts.RelativePath,
						directoryBatch.GitIgnorePath,
						directoryBatch.GitMetadataPath,
						gitIgnoreContext,
						gitIgnoreCandidateContext,
						cancellationToken,
						discoveredGitIgnoreMatchers,
						discoveredGitTrackedPathIndexes);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (UnauthorizedAccessException)
				{
					directories.Add(new EffectiveIgnoreScanNode(
						facts.FullPath,
						facts.RelativePath,
						facts.Name,
						parentIndex,
						isAccessDenied: true,
						facts.IsHidden,
						facts.IsDot,
						directControllerImpactCounts,
						extensionDiscoveryRuleState,
						baseRuleState,
						hiddenFoldersRuleState,
						dotFoldersRuleState,
						gitIgnoreContext,
						gitIgnoreCandidateContext));
					Interlocked.Exchange(ref hadAccessDenied, 1);
					if (isRootDirectory)
						Interlocked.Exchange(ref rootAccessDenied, 1);
					continue;
				}
				catch
				{
					directories.Add(new EffectiveIgnoreScanNode(
						facts.FullPath,
						facts.RelativePath,
						facts.Name,
						parentIndex,
						isAccessDenied: false,
						facts.IsHidden,
						facts.IsDot,
						directControllerImpactCounts,
						extensionDiscoveryRuleState,
						baseRuleState,
						hiddenFoldersRuleState,
						dotFoldersRuleState,
						gitIgnoreContext,
						gitIgnoreCandidateContext));
					continue;
				}
			}

			directories.Add(new EffectiveIgnoreScanNode(
				facts.FullPath,
				facts.RelativePath,
				facts.Name,
				parentIndex,
				isAccessDenied: false,
				facts.IsHidden,
				facts.IsDot,
				directControllerImpactCounts,
				extensionDiscoveryRuleState,
				baseRuleState,
				hiddenFoldersRuleState,
				dotFoldersRuleState,
				gitIgnoreContext,
				gitIgnoreCandidateContext));

			if (!canTraverseChildren)
				continue;

			foreach (var childDirectory in directoryBatch.Directories)
			{
				cancellationToken.ThrowIfCancellationRequested();

				var childFacts = AnalyzeDirectory(
						childDirectory.FullPath,
						childDirectory.RelativePath,
						childDirectory.Name,
						childDirectory.IsHidden,
						effectiveRules,
						gitIgnoreContext,
						gitIgnoreCandidateContext);
					var childDirectControllerImpactCounts = CountDirectDirectoryControllerImpact(
						childFacts,
						effectiveRules,
						effectiveExtensionPolicy,
						cancellationToken);
					var childExtensionDiscoveryRuleState = EvaluateDirectoryRuleState(
						childFacts,
						extensionDiscoveryRules.IgnoreHiddenFolders,
						extensionDiscoveryRules.IgnoreDotFolders);
					var childBaseRuleState = EvaluateDirectoryRuleState(
						childFacts,
						effectiveRules.IgnoreHiddenFolders,
						effectiveRules.IgnoreDotFolders);
					var childHiddenFoldersRuleState = EvaluateDirectoryRuleState(
						childFacts,
						!effectiveRules.IgnoreHiddenFolders,
						effectiveRules.IgnoreDotFolders);
					var childDotFoldersRuleState = EvaluateDirectoryRuleState(
						childFacts,
						ShouldApplyHiddenFoldersForDotFoldersVariant(childFacts, effectiveRules),
						!effectiveRules.IgnoreDotFolders);

				if (!CanAnyVariantTraverseChildren(
						    childExtensionDiscoveryRuleState,
						    childBaseRuleState,
						    childHiddenFoldersRuleState,
						    childDotFoldersRuleState) &&
					    childDirectControllerImpactCounts == IgnoreControllerImpactCounts.Empty)
					{
					continue;
				}

				pending.Push((
						childFacts,
						currentDirectoryIndex,
						IsRootDirectory: false,
						childDirectControllerImpactCounts,
						childExtensionDiscoveryRuleState,
						childBaseRuleState,
						childHiddenFoldersRuleState,
						childDotFoldersRuleState,
						gitIgnoreContext,
						gitIgnoreCandidateContext));
			}
		}

		discoveredGitIgnoreMatchers.Sort(CompareScopedGitIgnoreMatchers);
		return new ScanResult<EffectiveIgnoreScanDiscovery>(
			new EffectiveIgnoreScanDiscovery(
				directories,
				discoveredGitIgnoreMatchers,
				discoveredGitTrackedPathIndexes,
				gitEvidence),
			rootAccessDenied == 1,
			hadAccessDenied == 1);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool ShouldApplyHiddenFoldersForDotFoldersVariant(
		DirectoryScanFacts facts,
		IgnoreRules effectiveRules)
	{
		// DotFolders owns dot+hidden overlap while the dot toggle is active. The
		// DotFolders-off variant must therefore expose dot directories instead of
		// letting HiddenFolders keep them hidden and undercounting DotFolders impact.
		if (facts.IsDot)
			return false;

		return IgnoreRuleSemantics.ShouldIgnoreHiddenDirectory(
			effectiveRules.IgnoreHiddenFolders,
			facts.IsHidden,
			facts.IsDot,
			ignoreDotFolders: !effectiveRules.IgnoreDotFolders);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool CanAnyVariantTraverseChildren(
		DirectoryToggleRuleState extensionDiscoveryRuleState,
		DirectoryToggleRuleState baseRuleState,
		DirectoryToggleRuleState hiddenFoldersRuleState,
		DirectoryToggleRuleState dotFoldersRuleState)
	{
		return extensionDiscoveryRuleState.CanTraverseChildren ||
		       baseRuleState.CanTraverseChildren ||
		       hiddenFoldersRuleState.CanTraverseChildren ||
		       dotFoldersRuleState.CanTraverseChildren;
	}

	private static EffectiveIgnoreFinalizeResult FinalizeEffectiveIgnoreCounts(
		IReadOnlyList<EffectiveIgnoreScanNode> directories,
		EffectiveIgnoreNodeFileMetrics[] fileMetrics,
		EffectiveIgnoreNodeVisibilityState[] visibilityStates,
		IgnoreRules effectiveRules)
	{
		for (var index = directories.Count - 1; index >= 0; index--)
		{
			var node = directories[index];
			var visibilityState = visibilityStates[index];
			var metrics = fileMetrics[index];

			var baseHasVisibleContent = visibilityState.IsAccessDenied ||
			                            metrics.BaseVisibleFiles > 0 ||
			                            visibilityState.BaseVisibleChildren > 0;
			var hiddenFoldersHasVisibleContent = visibilityState.IsAccessDenied ||
			                                     metrics.BaseVisibleFiles > 0 ||
			                                     visibilityState.HiddenFoldersVisibleChildren > 0;
			var dotFoldersHasVisibleContent = visibilityState.IsAccessDenied ||
			                                  metrics.BaseVisibleFiles > 0 ||
			                                  visibilityState.DotFoldersVisibleChildren > 0;
			var hiddenFilesHasVisibleContent = visibilityState.IsAccessDenied ||
			                                   metrics.HiddenFilesVisibleFiles > 0 ||
			                                   visibilityState.HiddenFilesVisibleChildren > 0;
			var dotFilesHasVisibleContent = visibilityState.IsAccessDenied ||
			                                metrics.DotFilesVisibleFiles > 0 ||
			                                visibilityState.DotFilesVisibleChildren > 0;
			var emptyFilesHasVisibleContent = visibilityState.IsAccessDenied ||
			                                  metrics.EmptyFilesVisibleFiles > 0 ||
			                                  visibilityState.EmptyFilesVisibleChildren > 0;
			var extensionlessFilesHasVisibleContent = visibilityState.IsAccessDenied ||
			                                          metrics.ExtensionlessFilesVisibleFiles > 0 ||
			                                          visibilityState.ExtensionlessFilesVisibleChildren > 0;
			var emptyFoldersHasVisibleContent = visibilityState.IsAccessDenied ||
			                                    metrics.BaseVisibleFiles > 0 ||
			                                    visibilityState.EmptyFoldersVisibleChildren > 0;
			var controllerBaselineHasVisibleContent = visibilityState.IsAccessDenied ||
			                                          metrics.ControllerBaselineVisibleFiles > 0 ||
			                                          visibilityState.ControllerBaselineVisibleChildren > 0;
			var gitIgnoreHasVisibleContent = visibilityState.IsAccessDenied ||
			                                 metrics.GitIgnoreVisibleFiles > 0 ||
			                                 visibilityState.GitIgnoreVisibleChildren > 0;
			var smartIgnoreHasVisibleContent = visibilityState.IsAccessDenied ||
			                                   metrics.SmartIgnoreVisibleFiles > 0 ||
			                                   visibilityState.SmartIgnoreVisibleChildren > 0;

			visibilityState.BaseLocalVisible = IsDirectoryLocallyVisible(
				node.BaseRuleState,
				effectiveRules.IgnoreEmptyFolders,
				baseHasVisibleContent);
			visibilityState.HiddenFoldersLocalVisible = IsDirectoryLocallyVisible(
				node.HiddenFoldersRuleState,
				effectiveRules.IgnoreEmptyFolders,
				hiddenFoldersHasVisibleContent);
			visibilityState.DotFoldersLocalVisible = IsDirectoryLocallyVisible(
				node.DotFoldersRuleState,
				effectiveRules.IgnoreEmptyFolders,
				dotFoldersHasVisibleContent);
			visibilityState.HiddenFilesLocalVisible = IsDirectoryLocallyVisible(
				node.BaseRuleState,
				effectiveRules.IgnoreEmptyFolders,
				hiddenFilesHasVisibleContent);
			visibilityState.DotFilesLocalVisible = IsDirectoryLocallyVisible(
				node.BaseRuleState,
				effectiveRules.IgnoreEmptyFolders,
				dotFilesHasVisibleContent);
			visibilityState.EmptyFilesLocalVisible = IsDirectoryLocallyVisible(
				node.BaseRuleState,
				effectiveRules.IgnoreEmptyFolders,
				emptyFilesHasVisibleContent);
			visibilityState.ExtensionlessFilesLocalVisible = IsDirectoryLocallyVisible(
				node.BaseRuleState,
				effectiveRules.IgnoreEmptyFolders,
				extensionlessFilesHasVisibleContent);
			visibilityState.EmptyFoldersLocalVisible = IsDirectoryLocallyVisible(
				node.BaseRuleState,
				!effectiveRules.IgnoreEmptyFolders,
				emptyFoldersHasVisibleContent);
			visibilityState.ControllerBaselineLocalVisible = IsDirectoryLocallyVisible(
				node.BaseRuleState,
				effectiveRules.IgnoreEmptyFolders,
				controllerBaselineHasVisibleContent);
			visibilityState.GitIgnoreLocalVisible = IsDirectoryLocallyVisible(
				node.BaseRuleState,
				effectiveRules.IgnoreEmptyFolders,
				gitIgnoreHasVisibleContent);
			visibilityState.SmartIgnoreLocalVisible = IsDirectoryLocallyVisible(
				node.BaseRuleState,
				effectiveRules.IgnoreEmptyFolders,
				smartIgnoreHasVisibleContent);
			visibilityStates[index] = visibilityState;

			if (node.ParentIndex < 0)
				continue;

			var parentVisibilityState = visibilityStates[node.ParentIndex];
			if (visibilityState.BaseLocalVisible)
				parentVisibilityState.BaseVisibleChildren++;
			if (visibilityState.HiddenFoldersLocalVisible)
				parentVisibilityState.HiddenFoldersVisibleChildren++;
			if (visibilityState.DotFoldersLocalVisible)
				parentVisibilityState.DotFoldersVisibleChildren++;
			if (visibilityState.HiddenFilesLocalVisible)
				parentVisibilityState.HiddenFilesVisibleChildren++;
			if (visibilityState.DotFilesLocalVisible)
				parentVisibilityState.DotFilesVisibleChildren++;
			if (visibilityState.EmptyFilesLocalVisible)
				parentVisibilityState.EmptyFilesVisibleChildren++;
			if (visibilityState.ExtensionlessFilesLocalVisible)
				parentVisibilityState.ExtensionlessFilesVisibleChildren++;
			if (visibilityState.EmptyFoldersLocalVisible)
				parentVisibilityState.EmptyFoldersVisibleChildren++;
			if (visibilityState.ControllerBaselineLocalVisible)
				parentVisibilityState.ControllerBaselineVisibleChildren++;
			if (visibilityState.GitIgnoreLocalVisible)
				parentVisibilityState.GitIgnoreVisibleChildren++;
			if (visibilityState.SmartIgnoreLocalVisible)
				parentVisibilityState.SmartIgnoreVisibleChildren++;
			visibilityStates[node.ParentIndex] = parentVisibilityState;
		}

		var effectiveCounts = default(MutableIgnoreOptionCounts);
		var controllerImpactCounts = IgnoreControllerImpactCounts.Empty;
		for (var index = 0; index < directories.Count; index++)
		{
			var node = directories[index];
			var visibilityState = visibilityStates[index];
			var metrics = fileMetrics[index];

			if (node.ParentIndex < 0)
			{
				visibilityState.BaseFinalVisible = visibilityState.BaseLocalVisible;
				visibilityState.HiddenFoldersFinalVisible = visibilityState.HiddenFoldersLocalVisible;
				visibilityState.DotFoldersFinalVisible = visibilityState.DotFoldersLocalVisible;
				visibilityState.HiddenFilesFinalVisible = visibilityState.HiddenFilesLocalVisible;
				visibilityState.DotFilesFinalVisible = visibilityState.DotFilesLocalVisible;
				visibilityState.EmptyFilesFinalVisible = visibilityState.EmptyFilesLocalVisible;
				visibilityState.ExtensionlessFilesFinalVisible = visibilityState.ExtensionlessFilesLocalVisible;
				visibilityState.EmptyFoldersFinalVisible = visibilityState.EmptyFoldersLocalVisible;
				visibilityState.ControllerBaselineFinalVisible = visibilityState.ControllerBaselineLocalVisible;
				visibilityState.GitIgnoreFinalVisible = visibilityState.GitIgnoreLocalVisible;
				visibilityState.SmartIgnoreFinalVisible = visibilityState.SmartIgnoreLocalVisible;
			}
			else
			{
				var parentVisibilityState = visibilityStates[node.ParentIndex];
				visibilityState.BaseFinalVisible = parentVisibilityState.BaseFinalVisible &&
				                                  visibilityState.BaseLocalVisible;
				visibilityState.HiddenFoldersFinalVisible = parentVisibilityState.HiddenFoldersFinalVisible &&
				                                           visibilityState.HiddenFoldersLocalVisible;
				visibilityState.DotFoldersFinalVisible = parentVisibilityState.DotFoldersFinalVisible &&
				                                        visibilityState.DotFoldersLocalVisible;
				visibilityState.HiddenFilesFinalVisible = parentVisibilityState.HiddenFilesFinalVisible &&
				                                         visibilityState.HiddenFilesLocalVisible;
				visibilityState.DotFilesFinalVisible = parentVisibilityState.DotFilesFinalVisible &&
				                                      visibilityState.DotFilesLocalVisible;
				visibilityState.EmptyFilesFinalVisible = parentVisibilityState.EmptyFilesFinalVisible &&
				                                        visibilityState.EmptyFilesLocalVisible;
				visibilityState.ExtensionlessFilesFinalVisible = parentVisibilityState.ExtensionlessFilesFinalVisible &&
				                                                 visibilityState.ExtensionlessFilesLocalVisible;
				visibilityState.EmptyFoldersFinalVisible = parentVisibilityState.EmptyFoldersFinalVisible &&
				                                          visibilityState.EmptyFoldersLocalVisible;
				visibilityState.ControllerBaselineFinalVisible = parentVisibilityState.ControllerBaselineFinalVisible &&
				                                                visibilityState.ControllerBaselineLocalVisible;
				visibilityState.GitIgnoreFinalVisible = parentVisibilityState.GitIgnoreFinalVisible &&
				                                       visibilityState.GitIgnoreLocalVisible;
				visibilityState.SmartIgnoreFinalVisible = parentVisibilityState.SmartIgnoreFinalVisible &&
				                                         visibilityState.SmartIgnoreLocalVisible;
			}

			visibilityStates[index] = visibilityState;

			if (node.IsHidden &&
			    HasDirectoryToggleImpact(
				    node,
				    visibilityStates,
				    node.HiddenFoldersRuleState,
				    visibilityState.BaseFinalVisible,
				    visibilityState.HiddenFoldersFinalVisible))
			{
				effectiveCounts.HiddenFolders++;
			}

			if (node.IsDot &&
			    HasDirectoryToggleImpact(
				    node,
				    visibilityStates,
				    node.DotFoldersRuleState,
				    visibilityState.BaseFinalVisible,
				    visibilityState.DotFoldersFinalVisible))
			{
				effectiveCounts.DotFolders++;
			}

			if (visibilityState.BaseFinalVisible)
			{
				effectiveCounts.HiddenFiles += metrics.HiddenFilesDisappearWhenToggled;
				effectiveCounts.DotFiles += metrics.DotFilesDisappearWhenToggled;
				effectiveCounts.EmptyFiles += metrics.EmptyFilesDisappearWhenToggled;
				effectiveCounts.ExtensionlessFiles += metrics.ExtensionlessFilesDisappearWhenToggled;
			}

			if (visibilityState.HiddenFilesFinalVisible)
				effectiveCounts.HiddenFiles += metrics.HiddenFilesAppearWhenToggled;
			if (visibilityState.DotFilesFinalVisible)
				effectiveCounts.DotFiles += metrics.DotFilesAppearWhenToggled;
			if (visibilityState.EmptyFilesFinalVisible)
				effectiveCounts.EmptyFiles += metrics.EmptyFilesAppearWhenToggled;
			if (visibilityState.ExtensionlessFilesFinalVisible)
				effectiveCounts.ExtensionlessFiles += metrics.ExtensionlessFilesAppearWhenToggled;

			if (visibilityState.BaseFinalVisible != visibilityState.EmptyFoldersFinalVisible)
				effectiveCounts.EmptyFolders++;

			controllerImpactCounts = controllerImpactCounts.Add(node.DirectControllerImpactCounts);
			var smartIgnoreBaselineFiles = effectiveRules.IsGitIgnoreTraversalEnabled
				? metrics.GitIgnoreVisibleFiles
				: metrics.ControllerBaselineVisibleFiles;
			if (visibilityState.ControllerBaselineFinalVisible)
			{
				controllerImpactCounts = controllerImpactCounts.Add(new IgnoreControllerImpactCounts(
					GitIgnore: Math.Abs(metrics.ControllerBaselineVisibleFiles - metrics.GitIgnoreVisibleFiles),
					SmartIgnore: Math.Abs(smartIgnoreBaselineFiles - metrics.SmartIgnoreVisibleFiles)));
			}

			if (visibilityState.ControllerBaselineFinalVisible != visibilityState.GitIgnoreFinalVisible)
				controllerImpactCounts = controllerImpactCounts.Add(new IgnoreControllerImpactCounts(GitIgnore: 1));
			var smartIgnoreBaselineFinalVisible = effectiveRules.IsGitIgnoreTraversalEnabled
				? visibilityState.GitIgnoreFinalVisible
				: visibilityState.ControllerBaselineFinalVisible;
			if (smartIgnoreBaselineFinalVisible != visibilityState.SmartIgnoreFinalVisible)
				controllerImpactCounts = controllerImpactCounts.Add(new IgnoreControllerImpactCounts(SmartIgnore: 1));
		}

		return new EffectiveIgnoreFinalizeResult(
			effectiveCounts.ToImmutable(),
			controllerImpactCounts);
	}

	private readonly record struct EffectiveIgnoreFinalizeResult(
		IgnoreOptionCounts IgnoreOptionCounts,
		IgnoreControllerImpactCounts ControllerImpactCounts);

	private static bool HasDirectoryToggleImpact(
		in EffectiveIgnoreScanNode node,
		EffectiveIgnoreNodeVisibilityState[] visibilityStates,
		in DirectoryToggleRuleState toggledRuleState,
		bool baseFinalVisible,
		bool toggledFinalVisible)
	{
		var parentBaseVisible = node.ParentIndex < 0 ||
		                        visibilityStates[node.ParentIndex].BaseFinalVisible;
		if (!parentBaseVisible)
			return false;

		// A directory-level toggle affects the tree as soon as it changes whether a
		// reachable directory node can be entered. Do not require visible child files:
		// those files may use extensions that are discoverable only after the directory
		// toggle is disabled, and empty-folder filtering must not hide the toggle itself.
		if (node.BaseRuleState.CanTraverseChildren != toggledRuleState.CanTraverseChildren)
			return true;

		return baseFinalVisible != toggledFinalVisible;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void AccumulateDirectFileDelta(
		bool matchesTargetRule,
		bool baseVisible,
		bool toggledVisible,
		ref int count)
	{
		if (matchesTargetRule && baseVisible != toggledVisible)
			count++;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void AccumulateToggleTransition(
		bool matchesTargetRule,
		bool baseVisible,
		bool toggledVisible,
		ref int appearsWhenToggled,
		ref int disappearsWhenToggled)
	{
		if (!matchesTargetRule || baseVisible == toggledVisible)
			return;

		if (toggledVisible)
			appearsWhenToggled++;
		else
			disappearsWhenToggled++;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static IgnoreControllerImpactCounts CountFileControllerImpact(
		in EffectiveFileVisibilityProfile visibility,
		bool useGitIgnore)
	{
		var smartIgnoreBaselineVisible = useGitIgnore
			? visibility.GitIgnoreVisible
			: visibility.ControllerBaselineVisible;
		return new IgnoreControllerImpactCounts(
			GitIgnore: visibility.ControllerBaselineVisible != visibility.GitIgnoreVisible ? 1 : 0,
			SmartIgnore: smartIgnoreBaselineVisible != visibility.SmartIgnoreVisible ? 1 : 0);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void AddExtensionEntry(
		in FileScanFacts facts,
		HashSet<string> extensions,
		bool skipExtensionlessEntry = false)
	{
		if (facts.IsExtensionless)
		{
			if (skipExtensionlessEntry)
				return;

			extensions.Add(facts.Name);
			return;
		}

		AddUniqueExtension(extensions, GetExtensionSpan(facts));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void AddExtensionEntry(string fileName, HashSet<string> extensions)
	{
		if (IsExtensionlessFileName(fileName))
		{
			extensions.Add(fileName);
			return;
		}

		AddUniqueExtension(extensions, Path.GetExtension(fileName.AsSpan()));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void AddUniqueExtension(HashSet<string> extensions, ReadOnlySpan<char> extension)
	{
		if (extension.IsEmpty)
			return;

		Span<char> normalized = extension.Length <= 128
			? stackalloc char[extension.Length]
			: new char[extension.Length];
		for (var index = 0; index < extension.Length; index++)
			normalized[index] = char.ToLowerInvariant(extension[index]);

		// A case-insensitive set otherwise retains whichever casing wins the parallel merge.
		// Canonical values keep settings snapshots and benchmark fingerprints deterministic.
		if (extensions.TryGetAlternateLookup<ReadOnlySpan<char>>(out var lookup) && lookup.Contains(normalized))
			return;

		extensions.Add(normalized.ToString());
	}

	private ScanResult<int> ScanEffectiveEmptyFolderCountCore(
		string rootPath,
		IReadOnlySet<string> allowedExtensions,
		IgnoreRules rules,
		CancellationToken cancellationToken)
	{
		var scan = ScanEffectiveIgnoreOptionCountsCore(rootPath, allowedExtensions, rules, cancellationToken);
		return new ScanResult<int>(scan.Value.EmptyFolders, scan.RootAccessDenied, scan.HadAccessDenied);
	}

	private static RootSelectionScanPlan BuildRootSelectionScanPlan(
		string rootPath,
		IReadOnlyCollection<string> selectedRootFolders,
		IgnoreRules effectiveRules,
		bool includeDirectoryToggleProbeRoots,
		bool includeControllerImpactProbeRoots,
		CancellationToken cancellationToken)
	{
		var selectedRoots = new List<FileSystemDirectoryEntry>(selectedRootFolders.Count);
		var directoryToggleCandidates = new List<FileSystemDirectoryEntry>();
		var controllerImpactCandidates = new List<DirectoryScanFacts>();
		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
		{
			return new RootSelectionScanPlan(
				selectedRoots,
				directoryToggleCandidates,
				controllerImpactCandidates,
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		var selectedNames = new HashSet<string>(PathComparer.Default);
		foreach (var selectedRootFolder in selectedRootFolders)
		{
			if (!IsSafeRelativeRootFolderName(selectedRootFolder) ||
			    PathComparer.Default.Equals(selectedRootFolder, effectiveRules.ExcludedRootFolderName))
				continue;

			selectedNames.Add(selectedRootFolder);
		}

		var effectiveGitIgnoreContext = effectiveRules.CreateGitIgnoreScanContext(rootPath);
		var effectiveGitIgnoreCandidateContext = effectiveRules.CreateGitIgnoreCandidateScanContext(rootPath);
		if (effectiveRules.IsGitIgnoreTraversalEnabled)
		{
			(effectiveGitIgnoreContext, effectiveGitIgnoreCandidateContext, _) =
				EnterNearestGitTrackedPathIndex(
					rootPath,
					effectiveGitIgnoreContext,
					effectiveGitIgnoreCandidateContext,
					cancellationToken);
		}

		try
		{
			foreach (var directory in FileSystemEntryEnumerator.EnumerateDirectories(rootPath))
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (PathComparer.Default.Equals(directory.Name, effectiveRules.ExcludedRootFolderName))
					continue;

				if (selectedNames.Contains(directory.Name))
				{
					selectedRoots.Add(directory);
					continue;
				}

				if (includeDirectoryToggleProbeRoots &&
				    IsPotentialDirectoryToggleCandidate(directory, effectiveRules))
				{
					directoryToggleCandidates.Add(directory);
				}

				if (includeControllerImpactProbeRoots)
				{
					var facts = AnalyzeDirectory(
						directory.FullPath,
						directory.RelativePath,
						directory.Name,
						directory.IsHidden,
						effectiveRules,
						effectiveGitIgnoreContext,
						effectiveGitIgnoreCandidateContext);
					facts = PromoteRootControllerImpactCandidate(facts, effectiveRules);
					if (IsPotentialControllerImpactCandidate(facts))
						controllerImpactCandidates.Add(facts);
				}
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (UnauthorizedAccessException)
		{
			AddSelectedRootsByName(rootPath, selectedNames, selectedRoots);
			return new RootSelectionScanPlan(
				selectedRoots,
				directoryToggleCandidates,
				controllerImpactCandidates,
				RootAccessDenied: true,
				HadAccessDenied: true);
		}
		catch
		{
			AddSelectedRootsByName(rootPath, selectedNames, selectedRoots);
		}

		return new RootSelectionScanPlan(
			selectedRoots,
			directoryToggleCandidates,
			controllerImpactCandidates,
			RootAccessDenied: false,
			HadAccessDenied: false);
	}

	private static void AddSelectedRootsByName(
		string rootPath,
		IReadOnlyCollection<string> selectedNames,
		List<FileSystemDirectoryEntry> selectedRoots)
	{
		if (selectedNames.Count == 0)
			return;

		string normalizedRootPath;
		try
		{
			normalizedRootPath = PathUtility.Normalize(rootPath);
		}
		catch
		{
			return;
		}

		foreach (var selectedName in selectedNames)
		{
			try
			{
				var fullPath = PathUtility.Normalize(Path.Combine(rootPath, selectedName));
				if (PathComparer.Default.Equals(fullPath, normalizedRootPath) ||
				    !PathUtility.IsPathInside(fullPath, normalizedRootPath) ||
				    !Directory.Exists(fullPath) ||
				    IsReparsePointDirectory(fullPath))
				{
					continue;
				}

				selectedRoots.Add(new FileSystemDirectoryEntry(
					selectedName,
					fullPath,
					selectedName,
					HasHiddenAttribute(fullPath)));
			}
			catch
			{
				// Invalid stale root-folder selections are ignored by design.
			}
		}
	}

	private static bool IsSafeRelativeRootFolderName(string? selectedRootFolder)
	{
		return !string.IsNullOrWhiteSpace(selectedRootFolder) &&
		       !Path.IsPathRooted(selectedRootFolder) &&
		       selectedRootFolder.IndexOf(Path.DirectorySeparatorChar) < 0 &&
		       selectedRootFolder.IndexOf(Path.AltDirectorySeparatorChar) < 0;
	}

	private static bool IsPotentialDirectoryToggleCandidate(
		FileSystemDirectoryEntry directory,
		IgnoreRules effectiveRules)
	{
		if (!effectiveRules.IgnoreDotFolders && !effectiveRules.IgnoreHiddenFolders)
			return false;

		var isDotFolder = IgnoreRuleSemantics.IsDotName(directory.Name);
		var isHiddenByCurrentHiddenFolderRule = IgnoreRuleSemantics.ShouldIgnoreHiddenDirectory(
			effectiveRules.IgnoreHiddenFolders,
			directory.IsHidden,
			isDotFolder,
			effectiveRules.IgnoreDotFolders);
		if (isDotFolder &&
		    (effectiveRules.IgnoreDotFolders || !isHiddenByCurrentHiddenFolderRule))
		{
			return true;
		}

		return isHiddenByCurrentHiddenFolderRule;
	}

	private static bool IsPotentialControllerImpactCandidate(
		in DirectoryScanFacts facts)
	{
		if (facts.GitIgnoreCandidateEvaluation.IsIgnored &&
		    !facts.GitIgnoreCandidateEvaluation.ShouldTraverseIgnoredDirectory)
		{
			return true;
		}

		return facts.IsSmartIgnoredCandidate;
	}

	private static DirectoryScanFacts PromoteRootControllerImpactCandidate(
		in DirectoryScanFacts facts,
		IgnoreRules effectiveRules)
	{
		if (facts.IsSmartIgnoredCandidate)
			return facts;

		// Root-folder options are computed before the final selected-root scope is known.
		// If SmartIgnore hides a top-level artifact folder, the later scoped scan can no
		// longer infer that folder from selected roots. Probe artifact signatures here so
		// the controller keeps visible impact evidence instead of hiding its own toggle.
		// This deliberately uses the signature-backed artifact matcher, not broad smart
		// folder names, so source folders named build/vendor/pkg are not promoted by name.
		return effectiveRules.SmartArtifactIgnoreCandidateMatcher.IsIgnoredDirectory(facts.FullPath, facts.Name)
			? facts with { IsSmartIgnoredCandidate = true }
			: facts;
	}

	private static ScanResult<IgnoreOptionCounts> CountRootDirectoryToggleCandidates(
		IReadOnlyList<FileSystemDirectoryEntry> candidateDirectories,
		IgnoreRules effectiveRules,
		CancellationToken cancellationToken)
	{
		if (candidateDirectories.Count == 0 ||
		    (!effectiveRules.IgnoreDotFolders && !effectiveRules.IgnoreHiddenFolders))
		{
			return new ScanResult<IgnoreOptionCounts>(
				IgnoreOptionCounts.Empty,
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		var hiddenFolders = 0;
		var dotFolders = 0;
		var parallelOptions = ScanParallelismPolicy.CreateOptions(cancellationToken);

		Parallel.ForEach(
			candidateDirectories,
			parallelOptions,
			() => new RootDirectoryToggleCandidateAccumulator(),
			(directory, _, localCounts) =>
			{
				parallelOptions.CancellationToken.ThrowIfCancellationRequested();
				var contribution = CountRootDirectoryToggleCandidate(
					directory,
					effectiveRules,
					parallelOptions.CancellationToken);
				localCounts.DotFolders += contribution.DotFolders;
				localCounts.HiddenFolders += contribution.HiddenFolders;

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
			RootAccessDenied: false,
			HadAccessDenied: false);
	}

	private static IgnoreOptionCounts CountRootDirectoryToggleCandidate(
		in FileSystemDirectoryEntry directory,
		IgnoreRules effectiveRules,
		CancellationToken cancellationToken)
	{
		if ((!effectiveRules.IgnoreDotFolders && !effectiveRules.IgnoreHiddenFolders) ||
		    IsSuppressedByNonDirectoryToggleRule(
			    directory.FullPath,
			    directory.Name,
			    effectiveRules,
			    cancellationToken))
		{
			return IgnoreOptionCounts.Empty;
		}

		var isDotFolder = IgnoreRuleSemantics.IsDotName(directory.Name);
		var isHiddenByCurrentHiddenFolderRule = IgnoreRuleSemantics.ShouldIgnoreHiddenDirectory(
			effectiveRules.IgnoreHiddenFolders,
			directory.IsHidden,
			isDotFolder,
			effectiveRules.IgnoreDotFolders);
		var dotFolders = isDotFolder &&
		                 (effectiveRules.IgnoreDotFolders || !isHiddenByCurrentHiddenFolderRule)
			? 1
			: 0;

		// Root probes count evidence owned by a directory toggle only after controller
		// rules have declined it. This keeps projected and direct-scan labels identical.
		return new IgnoreOptionCounts(
			HiddenFolders: isHiddenByCurrentHiddenFolderRule ? 1 : 0,
			DotFolders: dotFolders);
	}

	private static IgnoreControllerImpactCounts CountRootDirectoryControllerImpactCandidate(
		in FileSystemDirectoryEntry directory,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext,
		IgnoreRules.GitIgnoreScanContext gitIgnoreCandidateContext,
		CancellationToken cancellationToken)
	{
		var facts = AnalyzeDirectory(
			directory.FullPath,
			directory.RelativePath,
			directory.Name,
			directory.IsHidden,
			effectiveRules,
			gitIgnoreContext,
			gitIgnoreCandidateContext);
		facts = PromoteRootControllerImpactCandidate(facts, effectiveRules);
		if (!IsPotentialControllerImpactCandidate(facts))
			return IgnoreControllerImpactCounts.Empty;

		return CountDirectDirectoryControllerImpact(
			facts,
			effectiveRules,
			effectiveExtensionPolicy,
			cancellationToken,
			requireVisibleContentWhenEmptyFoldersIgnored: false);
	}

	private static ScanResult<IgnoreControllerImpactCounts> CountRootDirectoryControllerImpactCandidates(
		IReadOnlyList<DirectoryScanFacts> candidateDirectories,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		CancellationToken cancellationToken)
	{
		if (candidateDirectories.Count == 0)
		{
			return new ScanResult<IgnoreControllerImpactCounts>(
				IgnoreControllerImpactCounts.Empty,
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		var counts = IgnoreControllerImpactCounts.Empty;
		var mergeLock = new object();
		var parallelOptions = ScanParallelismPolicy.CreateOptions(cancellationToken);

		Parallel.ForEach(
			candidateDirectories,
			parallelOptions,
			() => IgnoreControllerImpactCounts.Empty,
			(facts, _, localCounts) =>
			{
				parallelOptions.CancellationToken.ThrowIfCancellationRequested();
				return localCounts.Add(CountDirectDirectoryControllerImpact(
					facts,
					effectiveRules,
					effectiveExtensionPolicy,
					parallelOptions.CancellationToken,
					requireVisibleContentWhenEmptyFoldersIgnored: false));
			},
			localCounts =>
			{
				if (localCounts == IgnoreControllerImpactCounts.Empty)
					return;

				lock (mergeLock)
					counts = counts.Add(localCounts);
			});

		return new ScanResult<IgnoreControllerImpactCounts>(
			counts,
			RootAccessDenied: false,
			HadAccessDenied: false);
	}

	private static bool IsSuppressedByNonDirectoryToggleRule(
		string directoryPath,
		string name,
		IgnoreRules rules,
		CancellationToken cancellationToken)
	{
		if (rules.IsSmartIgnoredDirectory(directoryPath, name))
			return true;

		if (!rules.IsGitIgnoreTraversalEnabled)
			return false;

		var gitIgnoreContext = rules.CreateGitIgnoreScanContext(directoryPath);
		if (gitIgnoreContext.RequiresTrackedPathIndex &&
		    GitTrackedPathIndexCache.TryLoadNearest(
			    directoryPath,
			    cancellationToken,
			    out var trackedPathIndex))
		{
			gitIgnoreContext = gitIgnoreContext.WithTrackedPathIndex(trackedPathIndex);
		}

		var gitIgnore = gitIgnoreContext.Evaluate(directoryPath, string.Empty, isDirectory: true, name);
		return gitIgnore.IsIgnored && !gitIgnore.ShouldTraverseIgnoredDirectory;
	}

	private static (
		IgnoreRules.GitIgnoreScanContext Active,
		IgnoreRules.GitIgnoreScanContext Candidate) EnterGitIgnoreScope(
		string directoryPath,
		string directoryRelativePath,
		string? gitIgnorePath,
		string? gitMetadataPath,
		IgnoreRules.GitIgnoreScanContext activeContext,
		IgnoreRules.GitIgnoreScanContext candidateContext,
		CancellationToken cancellationToken,
		List<ScopedGitIgnoreMatcher>? discoveredMatchers = null,
		List<GitTrackedPathIndex>? discoveredTrackedPathIndexes = null)
	{
		ScopedGitIgnoreMatcher? scopedMatcher = null;
		if (!string.IsNullOrWhiteSpace(gitIgnorePath) &&
		    (!activeContext.ContainsScope(directoryPath) ||
		     !candidateContext.ContainsScope(directoryPath) ||
		     discoveredMatchers is not null))
		{
			GitIgnoreMatcherFileCache.TryLoad(directoryPath, gitIgnorePath, out scopedMatcher);
		}

		var requiresTrackedPathIndex =
			activeContext.RequiresTrackedPathIndex ||
			candidateContext.RequiresTrackedPathIndex ||
			scopedMatcher is not null &&
			!ReferenceEquals(scopedMatcher.Matcher, GitIgnoreMatcher.Empty);
		var reachedRepositoryBoundary = !string.IsNullOrWhiteSpace(gitMetadataPath);
		if (requiresTrackedPathIndex && (reachedRepositoryBoundary || scopedMatcher is not null))
		{
			GitTrackedPathIndex? trackedPathIndex = null;
			var loadedTrackedPathIndex = reachedRepositoryBoundary
				? GitTrackedPathIndexCache.TryLoad(
					directoryPath,
					gitMetadataPath!,
					cancellationToken,
					out trackedPathIndex)
				: GitTrackedPathIndexCache.TryLoadNearest(
					directoryPath,
					cancellationToken,
					out trackedPathIndex);
			if (loadedTrackedPathIndex)
			{
				if (!activeContext.ContainsTrackedPathIndex(trackedPathIndex.RepositoryRootPath) ||
				    !candidateContext.ContainsTrackedPathIndex(trackedPathIndex.RepositoryRootPath))
				{
					discoveredTrackedPathIndexes?.Add(trackedPathIndex);
				}

				activeContext = activeContext.WithTrackedPathIndex(trackedPathIndex);
				candidateContext = candidateContext.WithTrackedPathIndex(trackedPathIndex);
			}
			else if (reachedRepositoryBoundary)
			{
				// A repository boundary always ends ownership by an ancestor index. An
				// empty projection keeps tracked-only mode fail-closed when the nested
				// index is missing, unreadable, or being replaced concurrently.
				var unavailableBoundaryIndex = new GitTrackedPathIndex(directoryPath, []);
				discoveredTrackedPathIndexes?.Add(unavailableBoundaryIndex);
				activeContext = activeContext.WithTrackedPathIndex(unavailableBoundaryIndex);
				candidateContext = candidateContext.WithTrackedPathIndex(unavailableBoundaryIndex);
			}
		}

		if (scopedMatcher is null)
			return (activeContext, candidateContext);

		var activeContainsScope = activeContext.ContainsScope(directoryPath);
		var candidateContainsScope = candidateContext.ContainsScope(directoryPath);
		discoveredMatchers?.Add(scopedMatcher);
		return (
			activeContainsScope
				? activeContext
				: activeContext.WithScope(scopedMatcher, directoryRelativePath),
			candidateContainsScope
				? candidateContext
				: candidateContext.WithScope(scopedMatcher, directoryRelativePath));
	}

	private static (
		IgnoreRules.GitIgnoreScanContext Active,
		IgnoreRules.GitIgnoreScanContext Candidate,
		GitTrackedPathIndex? Index) EnterNearestGitTrackedPathIndex(
		string scanRootPath,
		IgnoreRules.GitIgnoreScanContext activeContext,
		IgnoreRules.GitIgnoreScanContext candidateContext,
		CancellationToken cancellationToken)
	{
		if (!activeContext.RequiresTrackedPathIndex && !candidateContext.RequiresTrackedPathIndex)
			return (activeContext, candidateContext, null);

		if (!GitTrackedPathIndexCache.TryLoadNearest(
			    scanRootPath,
			    cancellationToken,
			    out var trackedPathIndex))
		{
			return (activeContext, candidateContext, null);
		}

		return (
			activeContext.WithTrackedPathIndex(trackedPathIndex),
			candidateContext.WithTrackedPathIndex(trackedPathIndex),
			trackedPathIndex);
	}

	private static int CompareScopedGitIgnoreMatchers(
		ScopedGitIgnoreMatcher left,
		ScopedGitIgnoreMatcher right)
	{
		var depth = left.ScopeRootPath.Length.CompareTo(right.ScopeRootPath.Length);
		return depth != 0
			? depth
			: PathComparer.Default.Compare(left.ScopeRootPath, right.ScopeRootPath);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void AccumulateDirectoryIgnoreOptionCounts(
		FileSystemDirectoryEntry entry,
		ref MutableIgnoreOptionCounts counts)
	{
		if (IgnoreRuleSemantics.IsDotName(entry.Name))
			counts.DotFolders++;
		if (entry.IsHidden)
			counts.HiddenFolders++;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void AccumulateFileIgnoreOptionCounts(
		FileSystemFileEntry entry,
		ref MutableIgnoreOptionCounts counts)
	{
		if (IsExtensionlessFileName(entry.Name))
			counts.ExtensionlessFiles++;
		if (entry.Length == 0)
			counts.EmptyFiles++;
		if (IgnoreRuleSemantics.IsDotName(entry.Name))
			counts.DotFiles++;
		if (entry.IsHidden)
			counts.HiddenFiles++;
	}

	private static bool HasHiddenAttribute(string fullPath)
	{
		try
		{
			return File.GetAttributes(fullPath).HasFlag(FileAttributes.Hidden);
		}
		catch
		{
			return false;
		}
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

	private static long GetFileLength(string fullPath)
	{
		try
		{
			return new FileInfo(fullPath).Length;
		}
		catch
		{
			return 0;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool IsExtensionlessFileName(string name) =>
		IgnoreRuleSemantics.IsExtensionlessFileName(name);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static int GetExtensionStart(string name)
	{
		var extension = Path.GetExtension(name.AsSpan());
		return extension.IsEmpty ? -1 : name.Length - extension.Length;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static ReadOnlySpan<char> GetExtensionSpan(in FileScanFacts facts) =>
		facts.ExtensionStart < 0
			? ReadOnlySpan<char>.Empty
			: facts.Name.AsSpan(facts.ExtensionStart);

}
