using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit;

public sealed class FileSystemScannerFailureTests
{
	[Fact]
	public void InventoryNameOrder_DistinguishesCaseDistinctFilesystemEntries()
	{
		Assert.True(ProjectInventoryNameComparer.Compare("A.cs", "a.cs") < 0);
		Assert.True(ProjectInventoryNameComparer.Compare("a.cs", "A.cs") > 0);
		Assert.Equal(0, ProjectInventoryNameComparer.Compare("A.cs", "A.cs"));
	}

	[Fact]
	public void ScanProjectWorkspace_WhenNestedEnumerationFails_ReturnsPartialDataMarkedIncomplete()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("src/app.cs", "class App {}\n");
		project.CreateFile("src/nested/readme.md", "# Readme\n");
		var scanner = new FileSystemScanner((point, path) =>
		{
			if (point == FileSystemScanEnumerationPoint.DirectoryDiscovery &&
			    Path.GetFileName(path).Equals("nested", StringComparison.OrdinalIgnoreCase))
			{
				throw new IOException("Simulated transient enumeration failure.");
			}
		});

		var result = scanner.ScanProjectWorkspace(
			CreateRequest(project.Path, ["src"]),
			TestContext.Current.CancellationToken);

		Assert.True(result.HadScanFailure);
		Assert.False(result.RootAccessDenied);
		Assert.False(result.HadAccessDenied);
		Assert.Contains(".cs", result.Value.IgnoreSection.Extensions);
	}

	[Fact]
	public void ScanProjectWorkspace_WhenDirectoryFileEnumerationFails_MarksSnapshotIncomplete()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("src/app.cs", "class App {}\n");
		var scanner = new FileSystemScanner((point, path) =>
		{
			if (point == FileSystemScanEnumerationPoint.DirectoryFiles &&
			    Path.GetFileName(path).Equals("src", StringComparison.OrdinalIgnoreCase))
			{
				throw new IOException("Simulated file enumeration failure.");
			}
		});

		var result = scanner.ScanProjectWorkspace(
			CreateRequest(project.Path, ["src"]),
			TestContext.Current.CancellationToken);

		Assert.True(result.HadScanFailure);
		Assert.False(result.RootAccessDenied);
	}

	[Fact]
	public void GetRootFolderNames_WhenExpectedIoFails_DoesNotReportEmptySuccess()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("src/app.cs", "class App {}\n");
		var scanner = new FileSystemScanner((point, _) =>
		{
			if (point == FileSystemScanEnumerationPoint.RootDirectories)
				throw new IOException("Simulated root enumeration failure.");
		});

		var result = scanner.GetRootFolderNames(
			project.Path,
			CreateRules(),
			TestContext.Current.CancellationToken);

		Assert.True(result.HadScanFailure);
		Assert.False(result.RootAccessDenied);
		Assert.False(result.HadAccessDenied);
	}

	[Fact]
	public void GetRootFolderNames_WhenUnexpectedFailureOccurs_PropagatesException()
	{
		using var project = new TemporaryDirectory();
		var scanner = new FileSystemScanner((point, _) =>
		{
			if (point == FileSystemScanEnumerationPoint.RootDirectories)
				throw new InvalidOperationException("Unexpected scanner failure.");
		});

		var exception = Assert.Throws<InvalidOperationException>(() => scanner.GetRootFolderNames(
			project.Path,
			CreateRules(),
			TestContext.Current.CancellationToken));

		Assert.Equal("Unexpected scanner failure.", exception.Message);
	}

	[Fact]
	public void ScanProjectWorkspace_WhenFolderIsEmpty_ReturnsCompleteEmptySnapshot()
	{
		using var project = new TemporaryDirectory();
		var scanner = new FileSystemScanner();

		var result = scanner.ScanProjectWorkspace(
			CreateRequest(project.Path, []),
			TestContext.Current.CancellationToken);

		Assert.False(result.HadScanFailure);
		Assert.False(result.RootAccessDenied);
		Assert.False(result.HadAccessDenied);
		Assert.Empty(result.Value.IgnoreSection.Extensions);
	}

	private static ProjectWorkspaceScanRequest CreateRequest(
		string rootPath,
		IReadOnlyCollection<string> selectedRoots)
	{
		var rules = CreateRules();
		return new ProjectWorkspaceScanRequest(
			rootPath,
			selectedRoots,
			rules,
			rules,
			EffectiveExtensionPolicy: null,
			CaptureTreeInventory: true,
			IncludeDirectoryToggleProbeRoots: true,
			IncludeControllerImpactProbeRoots: true);
	}

	private static IgnoreRules CreateRules() => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(),
		SmartIgnoredFiles: new HashSet<string>());
}
