using DevProjex.Kernel.Abstractions;

namespace DevProjex.Tests.Shared.ProjectLoadWorkflow;

/// <summary>
/// Keeps focused coordinator test doubles usable after production moved to the
/// canonical workspace-scan contract. New scanner tests should implement that
/// contract directly instead of relying on this test-only adapter.
/// </summary>
internal sealed class LegacyWorkspaceScannerTestAdapter(IFileSystemScanner scanner)
	: IFileSystemScannerProjectWorkspaceScanner
{
	public static IFileSystemScannerProjectWorkspaceScanner Adapt(IFileSystemScanner scanner) =>
		scanner as IFileSystemScannerProjectWorkspaceScanner ??
		new LegacyWorkspaceScannerTestAdapter(scanner);

	public bool CanReadRoot(string rootPath) => scanner.CanReadRoot(rootPath);

	public ScanResult<HashSet<string>> GetExtensions(
		string rootPath,
		IgnoreRules rules,
		CancellationToken cancellationToken = default) =>
		scanner.GetExtensions(rootPath, rules, cancellationToken);

	public ScanResult<HashSet<string>> GetRootFileExtensions(
		string rootPath,
		IgnoreRules rules,
		CancellationToken cancellationToken = default) =>
		scanner.GetRootFileExtensions(rootPath, rules, cancellationToken);

	public ScanResult<List<string>> GetRootFolderNames(
		string rootPath,
		IgnoreRules rules,
		CancellationToken cancellationToken = default) =>
		scanner.GetRootFolderNames(rootPath, rules, cancellationToken);

	public ScanResult<ProjectWorkspaceScanSnapshot> ScanProjectWorkspace(
		ProjectWorkspaceScanRequest request,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (scanner is IFileSystemScannerProjectWorkspaceSnapshotProvider workspaceProvider)
		{
			return workspaceProvider.GetProjectWorkspaceSnapshotForRootSelection(
				request.RootPath,
				request.SelectedRootFolders,
				request.ExtensionDiscoveryRules,
				request.EffectiveRules,
				request.EffectiveExtensionPolicy,
				request.IncludeDirectoryToggleProbeRoots,
				cancellationToken,
				request.IncludeControllerImpactProbeRoots);
		}

		var ignoreSection = scanner is IFileSystemScannerRootSelectionSnapshotProvider rootSelectionProvider
			? rootSelectionProvider.GetIgnoreSectionSnapshotForRootSelection(
				request.RootPath,
				request.SelectedRootFolders,
				request.ExtensionDiscoveryRules,
				request.EffectiveRules,
				request.EffectiveExtensionPolicy,
				request.IncludeDirectoryToggleProbeRoots,
				cancellationToken,
				request.IncludeControllerImpactProbeRoots)
			: AggregateLegacySnapshots(request, cancellationToken);

		return new ScanResult<ProjectWorkspaceScanSnapshot>(
			new ProjectWorkspaceScanSnapshot(ignoreSection.Value, TreeInventory: null),
			ignoreSection.RootAccessDenied,
			ignoreSection.HadAccessDenied);
	}

	private ScanResult<IgnoreSectionScanData> AggregateLegacySnapshots(
		ProjectWorkspaceScanRequest request,
		CancellationToken cancellationToken)
	{
		var rootFiles = ReadRootFileSnapshot(request, cancellationToken);
		var extensions = new HashSet<string>(rootFiles.Value.Extensions, StringComparer.OrdinalIgnoreCase);
		var visibleExtensions = new HashSet<string>(
			rootFiles.Value.VisibleExtensions,
			StringComparer.OrdinalIgnoreCase);
		var rawCounts = rootFiles.Value.RawIgnoreOptionCounts;
		var effectiveCounts = rootFiles.Value.EffectiveIgnoreOptionCounts;
		var controllerCounts = rootFiles.Value.ControllerImpactCounts;
		var gitEvidence = rootFiles.Value.GitEvidence;
		var rootAccessDenied = rootFiles.RootAccessDenied;
		var hadAccessDenied = rootFiles.HadAccessDenied;

		foreach (var rootFolder in request.SelectedRootFolders)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var folder = ReadFolderSnapshot(
				Path.Combine(request.RootPath, rootFolder),
				request,
				cancellationToken);
			extensions.UnionWith(folder.Value.Extensions);
			visibleExtensions.UnionWith(folder.Value.VisibleExtensions);
			rawCounts = rawCounts.Add(folder.Value.RawIgnoreOptionCounts);
			effectiveCounts = effectiveCounts.Add(folder.Value.EffectiveIgnoreOptionCounts);
			controllerCounts = controllerCounts.Add(folder.Value.ControllerImpactCounts);
			gitEvidence = gitEvidence.Add(folder.Value.GitEvidence);
			rootAccessDenied |= folder.RootAccessDenied;
			hadAccessDenied |= folder.HadAccessDenied;
		}

		return new ScanResult<IgnoreSectionScanData>(
			new IgnoreSectionScanData(
				extensions,
				rawCounts,
				effectiveCounts,
				controllerCounts,
				visibleExtensions,
				GitEvidence: gitEvidence),
			rootAccessDenied,
			hadAccessDenied);
	}

	private ScanResult<IgnoreSectionScanData> ReadRootFileSnapshot(
		ProjectWorkspaceScanRequest request,
		CancellationToken cancellationToken)
	{
		if (scanner is IFileSystemScannerExtensionPolicySnapshotProvider policyProvider)
		{
			return policyProvider.GetRootFileIgnoreSectionSnapshot(
				request.RootPath,
				request.ExtensionDiscoveryRules,
				request.EffectiveRules,
				request.EffectiveExtensionPolicy,
				cancellationToken);
		}

		if (scanner is IFileSystemScannerIgnoreSectionSnapshotProvider snapshotProvider)
		{
			return snapshotProvider.GetRootFileIgnoreSectionSnapshot(
				request.RootPath,
				request.ExtensionDiscoveryRules,
				request.EffectiveRules,
				effectiveAllowedExtensions: null,
				cancellationToken);
		}

		var scan = scanner.GetRootFileExtensions(
			request.RootPath,
			request.ExtensionDiscoveryRules,
			cancellationToken);
		return CreateExtensionOnlySnapshot(scan);
	}

	private ScanResult<IgnoreSectionScanData> ReadFolderSnapshot(
		string folderPath,
		ProjectWorkspaceScanRequest request,
		CancellationToken cancellationToken)
	{
		if (scanner is IFileSystemScannerExtensionPolicySnapshotProvider policyProvider)
		{
			return policyProvider.GetIgnoreSectionSnapshot(
				folderPath,
				request.ExtensionDiscoveryRules,
				request.EffectiveRules,
				request.EffectiveExtensionPolicy,
				cancellationToken);
		}

		if (scanner is IFileSystemScannerIgnoreSectionSnapshotProvider snapshotProvider)
		{
			return snapshotProvider.GetIgnoreSectionSnapshot(
				folderPath,
				request.ExtensionDiscoveryRules,
				request.EffectiveRules,
				effectiveAllowedExtensions: null,
				cancellationToken);
		}

		var scan = scanner.GetExtensions(folderPath, request.ExtensionDiscoveryRules, cancellationToken);
		return CreateExtensionOnlySnapshot(scan);
	}

	private static ScanResult<IgnoreSectionScanData> CreateExtensionOnlySnapshot(
		ScanResult<HashSet<string>> scan)
	{
		var extensions = new HashSet<string>(scan.Value, StringComparer.OrdinalIgnoreCase);
		return new ScanResult<IgnoreSectionScanData>(
			new IgnoreSectionScanData(
				extensions,
				IgnoreOptionCounts.Empty,
				IgnoreOptionCounts.Empty,
				IgnoreControllerImpactCounts.Empty,
				new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase)),
			scan.RootAccessDenied,
			scan.HadAccessDenied);
	}
}
