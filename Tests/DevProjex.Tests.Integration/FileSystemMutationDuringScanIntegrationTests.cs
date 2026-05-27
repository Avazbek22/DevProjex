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
			includeDirectoryToggleProbeRoots: true, cancellationToken: TestContext.Current.CancellationToken);

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
		var roots = scanOptions.GetRootFolders(temp.Path, CreateIgnoreRules(), cancellationToken: TestContext.Current.CancellationToken).Value;

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
			includeDirectoryToggleProbeRoots: true, cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(".cs", snapshot.Value.Extensions);
		Assert.DoesNotContain(".ts", snapshot.Value.Extensions);
		Assert.False(snapshot.RootAccessDenied);
	}

	[Fact]
	public void IgnoreSectionSnapshot_FileAndFolderCreatedDuringFolderSnapshot_IncludesStableCreatedEntries()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.cs", "class App {}");

		var scanner = new MutatingFolderSnapshotScanner(
			"src",
			srcPath =>
			{
				File.WriteAllText(Path.Combine(srcPath, "runtime.log"), "created during scan");
				Directory.CreateDirectory(Path.Combine(srcPath, "generated"));
				File.WriteAllText(Path.Combine(srcPath, "generated", "feature.ts"), "export {}");
			});
		var scanOptions = new ScanOptionsUseCase(scanner);
		var rules = CreateIgnoreRules();

		var snapshot = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			["src"],
			rules,
			rules,
			effectiveAllowedExtensions: null,
			includeDirectoryToggleProbeRoots: true, cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(".cs", snapshot.Value.Extensions);
		Assert.Contains(".log", snapshot.Value.Extensions);
		Assert.Contains(".ts", snapshot.Value.Extensions);
		Assert.False(snapshot.RootAccessDenied);
	}

	[Fact]
	public void IgnoreSectionSnapshot_GitIgnoreChangedDuringFolderSnapshot_UsesCurrentRulesAndNextScanUsesUpdatedRules()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "# initially no ignored files\n");
		temp.CreateFile("generated/report.log", "visible in current scan");

		var ignoreRulesService = new IgnoreRulesService(new SmartIgnoreService([]));
		var rulesBeforeMutation = ignoreRulesService.Build(
			temp.Path,
			[IgnoreOptionId.UseGitIgnore],
			selectedRootFolders: ["generated"]);
		var scanner = new MutatingFolderSnapshotScanner(
			"generated",
			_ => File.WriteAllText(Path.Combine(temp.Path, ".gitignore"), "*.log\n"));
		var scanOptions = new ScanOptionsUseCase(scanner);

		var currentSnapshot = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			["generated"],
			rulesBeforeMutation,
			rulesBeforeMutation,
			effectiveAllowedExtensions: null,
			includeDirectoryToggleProbeRoots: true, cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(".log", currentSnapshot.Value.Extensions);
		Assert.False(currentSnapshot.RootAccessDenied);

		var rulesAfterMutation = ignoreRulesService.Build(
			temp.Path,
			[IgnoreOptionId.UseGitIgnore],
			selectedRootFolders: ["generated"]);
		var nextSnapshot = new ScanOptionsUseCase(new FileSystemScanner()).GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			["generated"],
			rulesAfterMutation,
			rulesAfterMutation,
			effectiveAllowedExtensions: null,
			includeDirectoryToggleProbeRoots: true, cancellationToken: TestContext.Current.CancellationToken);

		Assert.DoesNotContain(".log", nextSnapshot.Value.Extensions);
	}

	[Fact]
	public void IgnoreSectionSnapshot_AccessDeniedDuringFolderSnapshot_PreservesReadableDataAndReportsDenied()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.cs", "class App {}");
		temp.CreateFile("restricted/secret.ts", "export {}");

		var scanner = new AccessDeniedFolderSnapshotScanner("restricted");
		var scanOptions = new ScanOptionsUseCase(scanner);
		var rules = CreateIgnoreRules();

		var snapshot = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			["src", "restricted"],
			rules,
			rules,
			effectiveAllowedExtensions: null,
			includeDirectoryToggleProbeRoots: true, cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(".cs", snapshot.Value.Extensions);
		Assert.DoesNotContain(".ts", snapshot.Value.Extensions);
		Assert.False(snapshot.RootAccessDenied);
		Assert.True(snapshot.HadAccessDenied);
	}

	[Fact]
	public void IgnoreSectionSnapshot_PathTypeSwapsDuringFolderSnapshot_DoesNotThrowOrReuseStaleEntryShape()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/stable.cs", "class Stable {}");
		temp.CreateFile("src/file-to-folder.tmp", "old file shape");
		temp.CreateFile("src/folder-to-file/nested.ts", "export {}");

		var scanner = new MutatingFolderSnapshotScanner(
			"src",
			srcPath =>
			{
				File.Delete(Path.Combine(srcPath, "file-to-folder.tmp"));
				Directory.CreateDirectory(Path.Combine(srcPath, "file-to-folder.tmp"));
				File.WriteAllText(Path.Combine(srcPath, "file-to-folder.tmp", "created.ts"), "export {}");

				Directory.Delete(Path.Combine(srcPath, "folder-to-file"), recursive: true);
				File.WriteAllText(Path.Combine(srcPath, "folder-to-file"), "new extensionless file shape");
			});
		var scanOptions = new ScanOptionsUseCase(scanner);
		var rules = CreateIgnoreRules();

		var snapshot = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			["src"],
			rules,
			rules,
			effectiveAllowedExtensions: null,
			includeDirectoryToggleProbeRoots: true, cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(".cs", snapshot.Value.Extensions);
		Assert.Contains(".ts", snapshot.Value.Extensions);
		Assert.DoesNotContain(".tmp", snapshot.Value.Extensions);
		Assert.True(snapshot.Value.RawIgnoreOptionCounts.ExtensionlessFiles >= 1);
		Assert.False(snapshot.RootAccessDenied);
	}

	private static IgnoreRules CreateIgnoreRules() => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
		SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

	private sealed class MutatingFolderSnapshotScanner(string folderName, Action<string> mutate)
		: IFileSystemScanner, IFileSystemScannerIgnoreSectionSnapshotProvider
	{
		private readonly FileSystemScanner _inner = new();
		private int _mutated;

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
			    Interlocked.Exchange(ref _mutated, 1) == 0)
			{
				// Mutate immediately before the real scanner enumerates the folder. This
				// exercises the same recovery path as external tools changing the project
				// while the refresh pipeline is already running.
				mutate(rootPath);
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

	private sealed class AccessDeniedFolderSnapshotScanner(string folderName)
		: IFileSystemScanner, IFileSystemScannerIgnoreSectionSnapshotProvider
	{
		private readonly FileSystemScanner _inner = new();

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
			if (!string.Equals(Path.GetFileName(rootPath), folderName, StringComparison.Ordinal))
			{
				return _inner.GetIgnoreSectionSnapshot(
					rootPath,
					extensionDiscoveryRules,
					effectiveRules,
					effectiveAllowedExtensions,
					cancellationToken);
			}

			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase),
					IgnoreOptionCounts.Empty,
					IgnoreOptionCounts.Empty),
				RootAccessDenied: false,
				HadAccessDenied: true);
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
