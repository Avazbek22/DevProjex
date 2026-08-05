namespace DevProjex.Tests.Unit;

public sealed class ScanOptionsUseCaseWorkspaceContractTests
{
	private static readonly IgnoreRules Rules = new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(),
		SmartIgnoredFiles: new HashSet<string>());

	[Fact]
	public void GetProjectWorkspaceSnapshotForRootFolders_ForwardsOneCanonicalRequest()
	{
		var expected = CreateWorkspace(
			extensions: [".cs"],
			rawCounts: new IgnoreOptionCounts(EmptyFiles: 2),
			effectiveCounts: new IgnoreOptionCounts(EmptyFiles: 1));
		var scanner = new RecordingWorkspaceScanner(expected);
		var useCase = new ScanOptionsUseCase(scanner);
		var policy = new ExtensionSetInclusionPolicy(
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" });

		var actual = useCase.GetProjectWorkspaceSnapshotForRootFolders(
			"/project",
			["src", "tests"],
			Rules,
			Rules,
			policy,
			includeDirectoryToggleProbeRoots: true,
			cancellationToken: TestContext.Current.CancellationToken,
			includeControllerImpactProbeRoots: true,
			captureRootScanBreakdown: true,
			captureTreeInventory: true);

		Assert.Same(expected.Value, actual.Value);
		var request = Assert.Single(scanner.Requests);
		Assert.Equal("/project", request.RootPath);
		Assert.Equal(["src", "tests"], request.SelectedRootFolders);
		Assert.Same(Rules, request.ExtensionDiscoveryRules);
		Assert.Same(Rules, request.EffectiveRules);
		Assert.Same(policy, request.EffectiveExtensionPolicy);
		Assert.True(request.IncludeDirectoryToggleProbeRoots);
		Assert.True(request.IncludeControllerImpactProbeRoots);
		Assert.True(request.CaptureRootScanBreakdown);
		Assert.True(request.CaptureTreeInventory);
		Assert.Equal(TestContext.Current.CancellationToken, scanner.CancellationTokens.Single());
	}

	[Fact]
	public void GetIgnoreSectionSnapshotForRootFolders_ProjectsCanonicalSnapshotWithoutRescanning()
	{
		var expected = CreateWorkspace(
			extensions: [".cs", ".json"],
			rawCounts: new IgnoreOptionCounts(DotFiles: 4),
			effectiveCounts: new IgnoreOptionCounts(DotFiles: 2),
			rootAccessDenied: true,
			hadAccessDenied: true);
		var scanner = new RecordingWorkspaceScanner(expected);
		var useCase = new ScanOptionsUseCase(scanner);

		var actual = useCase.GetIgnoreSectionSnapshotForRootFolders(
			"/project",
			["src"],
			Rules,
			Rules,
			effectiveExtensionPolicy: null,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Same(expected.Value.IgnoreSection, actual.Value);
		Assert.True(actual.RootAccessDenied);
		Assert.True(actual.HadAccessDenied);
		Assert.Single(scanner.Requests);
	}

	[Fact]
	public void LegacyConvenienceProjections_UseCanonicalSnapshotValues()
	{
		var expected = CreateWorkspace(
			extensions: [".cs", ".CS", ".json"],
			rawCounts: new IgnoreOptionCounts(EmptyFiles: 7),
			effectiveCounts: new IgnoreOptionCounts(EmptyFiles: 3, EmptyFolders: 2));
		var scanner = new RecordingWorkspaceScanner(expected);
		var useCase = new ScanOptionsUseCase(scanner);

		var extensionScan = useCase.GetExtensionsAndIgnoreCountsForRootFolders(
			"/project",
			["src"],
			Rules,
			TestContext.Current.CancellationToken);
		var effectiveScan = useCase.GetEffectiveIgnoreOptionCountsForRootFolders(
			"/project",
			["src"],
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
			Rules,
			new IgnoreOptionCounts(EmptyFiles: 99),
			cancellationToken: TestContext.Current.CancellationToken);
		var emptyFolders = useCase.GetEffectiveEmptyFolderCountForRootFolders(
			"/project",
			["src"],
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
			Rules,
			TestContext.Current.CancellationToken);

		Assert.Equal(2, extensionScan.Value.Extensions.Count);
		Assert.Equal(7, extensionScan.Value.IgnoreOptionCounts.EmptyFiles);
		Assert.Equal(3, effectiveScan.Value.EmptyFiles);
		Assert.Equal(2, emptyFolders.Value);
		Assert.Equal(3, scanner.Requests.Count);
		Assert.All(scanner.Requests, request => Assert.False(request.CaptureTreeInventory));
	}

	[Fact]
	public void Execute_UsesRootDiscoveryThenOneWorkspaceScanAndSortsPublishedOptions()
	{
		var scanner = new RecordingWorkspaceScanner(
			CreateWorkspace(
				extensions: [".z", ".A", ".m"],
				rawCounts: IgnoreOptionCounts.Empty,
				effectiveCounts: IgnoreOptionCounts.Empty))
		{
			RootFolders = new ScanResult<List<string>>(
				["z", "A", "m"],
				RootAccessDenied: false,
				HadAccessDenied: true)
		};
		var useCase = new ScanOptionsUseCase(scanner);

		var result = useCase.Execute(
			new ScanOptionsRequest("/project", Rules),
			TestContext.Current.CancellationToken);

		Assert.Equal([".A", ".m", ".z"], result.Extensions);
		Assert.Equal(["A", "m", "z"], result.RootFolders);
		Assert.False(result.RootAccessDenied);
		Assert.True(result.HadAccessDenied);
		Assert.Equal(["A", "m", "z"], Assert.Single(scanner.Requests).SelectedRootFolders);
	}

	[Fact]
	public void CanceledRequest_DoesNotInvokeScanner()
	{
		var scanner = new RecordingWorkspaceScanner(
			CreateWorkspace([], IgnoreOptionCounts.Empty, IgnoreOptionCounts.Empty));
		var useCase = new ScanOptionsUseCase(scanner);
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		Assert.Throws<OperationCanceledException>(() =>
			useCase.GetProjectWorkspaceSnapshotForRootFolders(
				"/project",
				["src"],
				Rules,
				Rules,
				effectiveExtensionPolicy: null,
				cancellationToken: cancellation.Token));
		Assert.Empty(scanner.Requests);
	}

	[Fact]
	public void ScannerFailure_IsNotConvertedIntoASecondFallbackPipeline()
	{
		var scanner = new RecordingWorkspaceScanner(
			CreateWorkspace([], IgnoreOptionCounts.Empty, IgnoreOptionCounts.Empty))
		{
			WorkspaceException = new IOException("scan failed")
		};
		var useCase = new ScanOptionsUseCase(scanner);

		var exception = Assert.Throws<IOException>(() =>
			useCase.GetIgnoreSectionSnapshotForRootFolders(
				"/project",
				["src"],
				Rules,
				Rules,
				effectiveExtensionPolicy: null,
				cancellationToken: TestContext.Current.CancellationToken));

		Assert.Equal("scan failed", exception.Message);
		Assert.Single(scanner.Requests);
	}

	private static ScanResult<ProjectWorkspaceScanSnapshot> CreateWorkspace(
		IEnumerable<string> extensions,
		IgnoreOptionCounts rawCounts,
		IgnoreOptionCounts effectiveCounts,
		bool rootAccessDenied = false,
		bool hadAccessDenied = false)
	{
		var extensionSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
		var section = new IgnoreSectionScanData(
			extensionSet,
			rawCounts,
			effectiveCounts,
			IgnoreControllerImpactCounts.Empty,
			new HashSet<string>(extensionSet, StringComparer.OrdinalIgnoreCase));
		return new ScanResult<ProjectWorkspaceScanSnapshot>(
			new ProjectWorkspaceScanSnapshot(section, TreeInventory: null),
			rootAccessDenied,
			hadAccessDenied);
	}

	private sealed class RecordingWorkspaceScanner(
		ScanResult<ProjectWorkspaceScanSnapshot> workspace)
		: IFileSystemScannerProjectWorkspaceScanner
	{
		public List<ProjectWorkspaceScanRequest> Requests { get; } = [];
		public List<CancellationToken> CancellationTokens { get; } = [];
		public Exception? WorkspaceException { get; init; }
		public ScanResult<List<string>> RootFolders { get; init; } = new([], false, false);

		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) =>
			throw new InvalidOperationException("Granular extension scans must not be used.");

		public ScanResult<HashSet<string>> GetRootFileExtensions(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) =>
			throw new InvalidOperationException("Granular root-file scans must not be used.");

		public ScanResult<List<string>> GetRootFolderNames(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) =>
			RootFolders;

		public ScanResult<ProjectWorkspaceScanSnapshot> ScanProjectWorkspace(
			ProjectWorkspaceScanRequest request,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Requests.Add(request);
			CancellationTokens.Add(cancellationToken);
			if (WorkspaceException is not null)
				throw WorkspaceException;

			return workspace;
		}
	}
}
