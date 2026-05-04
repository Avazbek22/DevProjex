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

	private static IgnoreRules CreateIgnoreRules() => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
		SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}
