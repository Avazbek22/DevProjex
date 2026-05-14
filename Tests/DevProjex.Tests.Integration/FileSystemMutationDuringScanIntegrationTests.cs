namespace DevProjex.Tests.Integration;

public sealed class FileSystemMutationDuringScanIntegrationTests
{
	[Fact]
	public void IgnoreSectionSnapshot_SelectedRootDeletedBeforeScan_DoesNotThrowOrLeakDeletedEntries()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.cs", "class App {}");
		temp.CreateFile("deleted/generated.ts", "export {}");
		Directory.Delete(Path.Combine(temp.Path, "deleted"), recursive: true);

		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var rules = CreateIgnoreRules();

		var snapshot = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			["src", "deleted"],
			rules,
			rules,
			effectiveAllowedExtensions: null,
			includeDirectoryToggleProbeRoots: true);

		Assert.Contains(".cs", snapshot.Value.Extensions);
		Assert.DoesNotContain(".ts", snapshot.Value.Extensions);
		Assert.False(snapshot.RootAccessDenied);
	}

	[Fact]
	public void RootFolderScan_DirectoryDeletedBetweenSelections_DoesNotPromoteDeletedRoot()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.cs", "class App {}");
		temp.CreateFile("deleted/generated.ts", "export {}");
		Directory.Delete(Path.Combine(temp.Path, "deleted"), recursive: true);

		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var roots = scanOptions.GetRootFolders(temp.Path, CreateIgnoreRules()).Value;

		Assert.Contains("src", roots);
		Assert.DoesNotContain("deleted", roots);
	}

	[Fact]
	public void IgnoreSectionSnapshot_SelectedRootDeletedDuringFolderSnapshot_DoesNotThrowOrLeakEntries()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.cs", "class App {}");
		temp.CreateFile("vanishing/generated.ts", "export {}");

		var scanner = new DeletingFolderSnapshotScanner("vanishing");
		var scanOptions = new ScanOptionsUseCase(scanner);
		var rules = CreateIgnoreRules();

		var snapshot = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			["src", "vanishing"],
			rules,
			rules,
			effectiveAllowedExtensions: null,
			includeDirectoryToggleProbeRoots: true);

		Assert.Contains(".cs", snapshot.Value.Extensions);
		Assert.DoesNotContain(".ts", snapshot.Value.Extensions);
		Assert.False(snapshot.RootAccessDenied);
	}

	private static IgnoreRules CreateIgnoreRules() => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
		SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

	private sealed class DeletingFolderSnapshotScanner(string folderName)
		: IFileSystemScanner, IFileSystemScannerIgnoreSectionSnapshotProvider
	{
		private readonly FileSystemScanner _inner = new();
		private int _deleted;

		public bool CanReadRoot(string rootPath) => _inner.CanReadRoot(rootPath);

		public ScanResult<HashSet<string>> GetExtensions(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) =>
			_inner.GetExtensions(rootPath, rules, cancellationToken);

		public ScanResult<HashSet<string>> GetRootFileExtensions(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) =>
			_inner.GetRootFileExtensions(rootPath, rules, cancellationToken);

		public ScanResult<List<string>> GetRootFolderNames(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) =>
			_inner.GetRootFolderNames(rootPath, rules, cancellationToken);

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IReadOnlySet<string>? effectiveAllowedExtensions,
			CancellationToken cancellationToken = default)
		{
			if (string.Equals(Path.GetFileName(rootPath), folderName, StringComparison.Ordinal) &&
			    Interlocked.Exchange(ref _deleted, 1) == 0 &&
			    Directory.Exists(rootPath))
			{
				Directory.Delete(rootPath, recursive: true);
			}

			return _inner.GetIgnoreSectionSnapshot(
				rootPath,
				extensionDiscoveryRules,
				effectiveRules,
				effectiveAllowedExtensions,
				cancellationToken);
		}

		public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
			string rootPath,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IReadOnlySet<string>? effectiveAllowedExtensions,
			CancellationToken cancellationToken = default) =>
			_inner.GetRootFileIgnoreSectionSnapshot(
				rootPath,
				extensionDiscoveryRules,
				effectiveRules,
				effectiveAllowedExtensions,
				cancellationToken);
	}
}
