namespace DevProjex.Tests.Unit;

public sealed class ScanOptionsUseCasePathSemanticsTests
{
	private static readonly IgnoreRules Rules = new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(),
		SmartIgnoredFiles: new HashSet<string>());

	[Fact]
	public void GetRootFolders_SortsUsingPlatformPathSemantics()
	{
		var returnedFolders = new[] { "a-src", "B-src", "C-src" };
		var scanner = new RecordingScanner
		{
			RootFolders = new ScanResult<List<string>>([.. returnedFolders], false, false)
		};

		var result = new ScanOptionsUseCase(scanner).GetRootFolders(
			SyntheticTestPaths.CreateMissingRoot(),
			Rules,
			TestContext.Current.CancellationToken);
		var expected = returnedFolders.ToList();
		expected.Sort(PathComparer.Default);

		Assert.Equal(expected, result.Value);
	}

	[Fact]
	public void WorkspaceProjection_ForwardsLogicalRootNamesWithoutHostSpecificRewriting()
	{
		var scanner = new RecordingScanner();
		var useCase = new ScanOptionsUseCase(scanner);

		_ = useCase.GetExtensionsForRootFolders(
			SyntheticTestPaths.CreateMissingRoot(),
			["Src", "docs", ".config"],
			Rules,
			TestContext.Current.CancellationToken);

		Assert.Equal(["Src", "docs", ".config"], Assert.Single(scanner.Requests).SelectedRootFolders);
	}

	private sealed class RecordingScanner : IFileSystemScannerProjectWorkspaceScanner
	{
		public List<ProjectWorkspaceScanRequest> Requests { get; } = [];
		public ScanResult<List<string>> RootFolders { get; init; } = new([], false, false);

		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) =>
			throw new InvalidOperationException("Granular scans must not be used.");

		public ScanResult<HashSet<string>> GetRootFileExtensions(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) =>
			throw new InvalidOperationException("Granular scans must not be used.");

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
			var section = new IgnoreSectionScanData(
				[],
				IgnoreOptionCounts.Empty,
				IgnoreOptionCounts.Empty);
			return new ScanResult<ProjectWorkspaceScanSnapshot>(
				new ProjectWorkspaceScanSnapshot(section, TreeInventory: null),
				false,
				false);
		}
	}
}
