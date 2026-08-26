using DevProjex.Application.Selection;

namespace DevProjex.Application.UseCases;

/// <summary>
/// Exposes option-oriented projections of the canonical project workspace scan.
/// Every multi-section result comes from one scanner contract so extensions,
/// ignore counts, and reusable tree inventory cannot drift between fallback paths.
/// </summary>
public sealed class ScanOptionsUseCase(IFileSystemScannerProjectWorkspaceScanner scanner)
{
	public ScanOptionsResult Execute(ScanOptionsRequest request, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var roots = GetRootFolders(request.RootPath, request.IgnoreRules, cancellationToken);
		var workspace = ScanWorkspace(
			request.RootPath,
			roots.Value,
			request.IgnoreRules,
			request.IgnoreRules,
			effectiveExtensionPolicy: null,
			captureTreeInventory: false,
			cancellationToken: cancellationToken);
		var extensions = SortExtensions(workspace.Value.IgnoreSection.Extensions, cancellationToken);

		return new ScanOptionsResult(
			Extensions: extensions,
			RootFolders: roots.Value,
			RootAccessDenied: roots.RootAccessDenied || workspace.RootAccessDenied,
			HadAccessDenied: roots.HadAccessDenied || workspace.HadAccessDenied,
			HadScanFailure: roots.HadScanFailure || workspace.HadScanFailure);
	}

	public ScanResult<List<string>> GetRootFolders(
		string rootPath,
		IgnoreRules ignoreRules,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var scan = scanner.GetRootFolderNames(rootPath, ignoreRules, cancellationToken);
		var rootFolders = new List<string>(scan.Value);
		CancellationAwareSort.Sort(rootFolders, PathComparer.Default, cancellationToken);
		return new ScanResult<List<string>>(
			rootFolders,
			scan.RootAccessDenied,
			scan.HadAccessDenied,
			scan.HadScanFailure);
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
			scan.HadAccessDenied,
			scan.HadScanFailure);
	}

	public ScanResult<ExtensionsScanData> GetExtensionsAndIgnoreCountsForRootFolders(
		string rootPath,
		IReadOnlyCollection<string> rootFolders,
		IgnoreRules ignoreRules,
		CancellationToken cancellationToken = default)
	{
		var scan = ScanWorkspace(
			rootPath,
			rootFolders,
			ignoreRules,
			ignoreRules,
			effectiveExtensionPolicy: null,
			captureTreeInventory: false,
			cancellationToken: cancellationToken);
		var ignoreSection = scan.Value.IgnoreSection;
		return new ScanResult<ExtensionsScanData>(
			new ExtensionsScanData(
				new HashSet<string>(ignoreSection.Extensions, StringComparer.OrdinalIgnoreCase),
				ignoreSection.RawIgnoreOptionCounts,
				ignoreSection.ControllerImpactCounts),
			scan.RootAccessDenied,
			scan.HadAccessDenied,
			scan.HadScanFailure);
	}

	public ScanResult<int> GetEffectiveEmptyFolderCountForRootFolders(
		string rootPath,
		IReadOnlyCollection<string> rootFolders,
		IReadOnlySet<string> allowedExtensions,
		IgnoreRules ignoreRules,
		CancellationToken cancellationToken = default)
	{
		var scan = ScanWorkspace(
			rootPath,
			rootFolders,
			IgnoreRulesProjection.ForExtensionAvailability(ignoreRules),
			ignoreRules,
			new ExtensionSetInclusionPolicy(allowedExtensions),
			captureTreeInventory: false,
			cancellationToken: cancellationToken);
		return new ScanResult<int>(
			scan.Value.IgnoreSection.EffectiveIgnoreOptionCounts.EmptyFolders,
			scan.RootAccessDenied,
			scan.HadAccessDenied,
			scan.HadScanFailure);
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
		var effectiveExtensionPolicy = effectiveAllowedExtensions is null
			? null
			: new ExtensionSetInclusionPolicy(effectiveAllowedExtensions);
		return GetIgnoreSectionSnapshotForRootFolders(
			rootPath,
			rootFolders,
			extensionDiscoveryRules,
			effectiveRules,
			effectiveExtensionPolicy,
			includeDirectoryToggleProbeRoots,
			cancellationToken,
			includeControllerImpactProbeRoots);
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
		var scan = ScanWorkspace(
			rootPath,
			rootFolders,
			extensionDiscoveryRules,
			effectiveRules,
			effectiveExtensionPolicy,
			includeDirectoryToggleProbeRoots,
			includeControllerImpactProbeRoots,
			captureTreeInventory: false,
			cancellationToken);
		return new ScanResult<IgnoreSectionScanData>(
			scan.Value.IgnoreSection,
			scan.RootAccessDenied,
			scan.HadAccessDenied,
			scan.HadScanFailure);
	}

	public ScanResult<ProjectWorkspaceScanSnapshot> GetProjectWorkspaceSnapshotForRootFolders(
		string rootPath,
		IReadOnlyCollection<string> rootFolders,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		bool includeDirectoryToggleProbeRoots = false,
		CancellationToken cancellationToken = default,
		bool includeControllerImpactProbeRoots = false,
		bool captureTreeInventory = true)
	{
		return ScanWorkspace(
			rootPath,
			rootFolders,
			extensionDiscoveryRules,
			effectiveRules,
			effectiveExtensionPolicy,
			includeDirectoryToggleProbeRoots,
			includeControllerImpactProbeRoots,
			captureTreeInventory,
			cancellationToken);
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
		// Kept for source compatibility. The canonical workspace scan now owns both
		// raw and effective counts, so caller-provided raw counts cannot affect the result.
		_ = rawCounts;
		var scan = ScanWorkspace(
			rootPath,
			rootFolders,
			IgnoreRulesProjection.ForExtensionAvailability(ignoreRules),
			ignoreRules,
			new ExtensionSetInclusionPolicy(allowedExtensions),
			includeDirectoryToggleProbeRoots,
			includeControllerImpactProbeRoots: false,
			captureTreeInventory: false,
			cancellationToken);
		return new ScanResult<IgnoreOptionCounts>(
			scan.Value.IgnoreSection.EffectiveIgnoreOptionCounts,
			scan.RootAccessDenied,
			scan.HadAccessDenied,
			scan.HadScanFailure);
	}

	public bool CanReadRoot(string rootPath) => scanner.CanReadRoot(rootPath);

	private ScanResult<ProjectWorkspaceScanSnapshot> ScanWorkspace(
		string rootPath,
		IReadOnlyCollection<string> rootFolders,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		bool includeDirectoryToggleProbeRoots = false,
		bool includeControllerImpactProbeRoots = false,
		bool captureTreeInventory = false,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return scanner.ScanProjectWorkspace(
			new ProjectWorkspaceScanRequest(
				rootPath,
				rootFolders,
				extensionDiscoveryRules,
				effectiveRules,
				effectiveExtensionPolicy,
				captureTreeInventory,
				includeDirectoryToggleProbeRoots,
				includeControllerImpactProbeRoots),
			cancellationToken);
	}

	private static List<string> SortExtensions(
		IReadOnlyCollection<string> extensions,
		CancellationToken cancellationToken)
	{
		var sorted = new List<string>(extensions);
		CancellationAwareSort.Sort(sorted, StringComparer.OrdinalIgnoreCase, cancellationToken);
		return sorted;
	}
}
