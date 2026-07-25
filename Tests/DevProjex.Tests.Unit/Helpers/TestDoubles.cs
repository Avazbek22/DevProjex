namespace DevProjex.Tests.Unit.Helpers;

internal sealed class StubLocalizationCatalog(
	IReadOnlyDictionary<AppLanguage, IReadOnlyDictionary<string, string>> data)
	: ILocalizationCatalog
{
	public IReadOnlyDictionary<string, string> Get(AppLanguage language)
	{
		return data.TryGetValue(language, out var dict) ? dict : data[AppLanguage.En];
	}
}

internal sealed class StubFileSystemScanner : IFileSystemScannerProjectWorkspaceScanner
{
	public Func<string, IgnoreRules, ScanResult<HashSet<string>>> GetExtensionsHandler { get; set; } =
		(_, _) => new ScanResult<HashSet<string>>([], false, false);

	public Func<string, IgnoreRules, ScanResult<HashSet<string>>> GetRootFileExtensionsHandler { get; set; } =
		(_, _) => new ScanResult<HashSet<string>>([], false, false);

	public Func<string, IgnoreRules, ScanResult<List<string>>> GetRootFolderNamesHandler { get; set; } =
		(_, _) => new ScanResult<List<string>>([], false, false);

	public Func<string, bool> CanReadRootHandler { get; set; } = _ => true;

	public Func<ProjectWorkspaceScanRequest, CancellationToken, ScanResult<ProjectWorkspaceScanSnapshot>>?
		ScanProjectWorkspaceHandler { get; set; }

	public bool CanReadRoot(string rootPath) => CanReadRootHandler(rootPath);

	public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default) =>
		GetExtensionsHandler(rootPath, rules);

	public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default) =>
		GetRootFileExtensionsHandler(rootPath, rules);

	public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default) =>
		GetRootFolderNamesHandler(rootPath, rules);

	public ScanResult<ProjectWorkspaceScanSnapshot> ScanProjectWorkspace(
		ProjectWorkspaceScanRequest request,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (ScanProjectWorkspaceHandler is not null)
			return ScanProjectWorkspaceHandler(request, cancellationToken);

		var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var rootFiles = GetRootFileExtensionsHandler(request.RootPath, request.ExtensionDiscoveryRules);
		extensions.UnionWith(rootFiles.Value);
		var rootSnapshots = request.CaptureRootScanBreakdown
			? new Dictionary<string, ProjectWorkspaceRootScanSnapshot>(PathComparer.Default)
			: null;
		var rootAccessDenied = rootFiles.RootAccessDenied;
		var hadAccessDenied = rootFiles.HadAccessDenied;

		foreach (var rootFolder in request.SelectedRootFolders)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var result = GetExtensionsHandler(
				Path.Combine(request.RootPath, rootFolder),
				request.ExtensionDiscoveryRules);
			extensions.UnionWith(result.Value);
			rootAccessDenied |= result.RootAccessDenied;
			hadAccessDenied |= result.HadAccessDenied;

			if (rootSnapshots is not null)
			{
				var section = CreateIgnoreSection(result.Value);
				rootSnapshots[rootFolder] = new ProjectWorkspaceRootScanSnapshot(
					section,
					IgnoreOptionCounts.Empty,
					IgnoreControllerImpactCounts.Empty,
					result.RootAccessDenied,
					result.HadAccessDenied);
			}
		}

		var ignoreSection = CreateIgnoreSection(extensions);
		var breakdown = rootSnapshots is null
			? null
			: new ProjectWorkspaceScanBreakdown(
				CreateIgnoreSection(rootFiles.Value),
				rootSnapshots,
				IgnoreOptionCounts.Empty,
				IgnoreControllerImpactCounts.Empty,
				request.IncludeDirectoryToggleProbeRoots,
				request.IncludeControllerImpactProbeRoots,
				RootEnumerationAccessDenied: false,
				RootEnumerationHadAccessDenied: false,
				rootFiles.RootAccessDenied,
				rootFiles.HadAccessDenied);
		return new ScanResult<ProjectWorkspaceScanSnapshot>(
			new ProjectWorkspaceScanSnapshot(ignoreSection, TreeInventory: null, breakdown),
			rootAccessDenied,
			hadAccessDenied);
	}

	private static IgnoreSectionScanData CreateIgnoreSection(IEnumerable<string> extensions)
	{
		var values = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
		return new IgnoreSectionScanData(
			values,
			IgnoreOptionCounts.Empty,
			IgnoreOptionCounts.Empty,
			IgnoreControllerImpactCounts.Empty,
			new HashSet<string>(values, StringComparer.OrdinalIgnoreCase));
	}
}

internal sealed class StubTreeBuilder : ITreeBuilder
{
	public TreeBuildResult Result { get; set; } = new(
		new FileSystemNode("root", "root", true, false, new List<FileSystemNode>()),
		false,
		false);

	public TreeBuildResult Build(string rootPath, TreeFilterOptions options, CancellationToken cancellationToken = default) => Result;
}

internal sealed class StubIconMapper : IIconMapper
{
	public string IconKey { get; set; } = "icon";
	public string GetIconKey(FileSystemNode node) => IconKey;
}

internal sealed class StubSmartIgnoreRule(SmartIgnoreResult result) : ISmartIgnoreRule
{
	public SmartIgnoreResult Evaluate(string rootPath) => result;
}
