namespace DevProjex.Tests.Integration;

public sealed class FileSystemReparsePointIntegrationTests
{
	[Fact]
	public void TreeBuilder_DirectorySymlinkCycle_DoesNotTraverseReparsePointDirectory()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("real/app.cs", "class App {}");

		if (!TryCreateDirectorySymlink(Path.Combine(temp.Path, "real", "back-to-root"), temp.Path))
			return;

		var tree = new TreeBuilder().Build(
			temp.Path,
			new TreeFilterOptions(
				AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
				AllowedRootFolders: new HashSet<string>(PathComparer.Default) { "real" },
				IgnoreRules: CreateIgnoreRules()));

		var real = Assert.Single(tree.Root.Children, node => string.Equals(node.Name, "real", StringComparison.Ordinal));
		Assert.Contains(real.Children, node => string.Equals(node.Name, "app.cs", StringComparison.Ordinal));
		Assert.DoesNotContain(real.Children, node => string.Equals(node.Name, "back-to-root", StringComparison.Ordinal));
	}

	[Fact]
	public void ScanOptions_DirectorySymlinkRoot_DoesNotBecomeRootFolderOrIgnoreProbeSource()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("real/app.cs", "class App {}");
		temp.CreateFile("real/.idea/workspace.xml", "<project />");

		if (!TryCreateDirectorySymlink(Path.Combine(temp.Path, "linked"), Path.Combine(temp.Path, "real")))
			return;

		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var rules = CreateIgnoreRules() with { IgnoreDotFolders = true };

		var rootFolders = scanOptions.GetRootFolders(temp.Path, rules).Value;
		var snapshot = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			["linked"],
			rules,
			rules,
			effectiveAllowedExtensions: null,
			includeDirectoryToggleProbeRoots: true);

		Assert.DoesNotContain("linked", rootFolders);
		Assert.Empty(snapshot.Value.Extensions);
		Assert.Equal(0, snapshot.Value.EffectiveIgnoreOptionCounts.DotFolders);
	}

	private static IgnoreRules CreateIgnoreRules() => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
		SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

	private static bool TryCreateDirectorySymlink(string linkPath, string targetPath)
	{
		try
		{
			Directory.CreateSymbolicLink(linkPath, targetPath);
			return Directory.Exists(linkPath) &&
			       File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			return false;
		}
	}
}
