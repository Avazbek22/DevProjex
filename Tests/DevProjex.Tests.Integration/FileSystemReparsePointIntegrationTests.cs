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
		var extensionScan = scanOptions.GetExtensionsForRootFolders(temp.Path, ["linked"], rules);
		var effectiveCounts = scanOptions.GetEffectiveIgnoreOptionCountsForRootFolders(
			temp.Path,
			["linked"],
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
			rules,
			IgnoreOptionCounts.Empty,
			includeDirectoryToggleProbeRoots: true);

		Assert.DoesNotContain("linked", rootFolders);
		Assert.Empty(snapshot.Value.Extensions);
		Assert.Equal(0, snapshot.Value.EffectiveIgnoreOptionCounts.DotFolders);
		Assert.Empty(extensionScan.Value);
		Assert.Equal(IgnoreOptionCounts.Empty, effectiveCounts.Value);
	}

	[Fact]
	public void TreeBuilder_DanglingDirectorySymlink_DoesNotAppearAsTraversableFolder()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.cs", "class App {}");

		var linkPath = Path.Combine(temp.Path, "dangling");
		if (!TryCreateDanglingDirectorySymlink(linkPath, Path.Combine(temp.Path, "missing-target")))
			return;

		var tree = new TreeBuilder().Build(
			temp.Path,
			new TreeFilterOptions(
				AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
				AllowedRootFolders: new HashSet<string>(PathComparer.Default) { "src", "dangling" },
				IgnoreRules: CreateIgnoreRules()));

		Assert.Contains(tree.Root.Children, node => string.Equals(node.Name, "src", StringComparison.Ordinal));
		Assert.DoesNotContain(tree.Root.Children, node => string.Equals(node.Name, "dangling", StringComparison.Ordinal));
	}

	[Fact]
	public void TreeBuilder_FileSymlink_DoesNotAppearAsRegularFile()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("real/app.cs", "class App {}");

		if (!TryCreateFileSymlink(Path.Combine(temp.Path, "linked.cs"), Path.Combine(temp.Path, "real", "app.cs")))
			return;

		var tree = new TreeBuilder().Build(
			temp.Path,
			new TreeFilterOptions(
				AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
				AllowedRootFolders: new HashSet<string>(PathComparer.Default) { "real" },
				IgnoreRules: CreateIgnoreRules()));

		Assert.Contains(tree.Root.Children, node => string.Equals(node.Name, "real", StringComparison.Ordinal));
		Assert.DoesNotContain(tree.Root.Children, node => string.Equals(node.Name, "linked.cs", StringComparison.Ordinal));
	}

	[Fact]
	public void NestedDirectorySymlink_IsNotTraversedByTreeOrIgnoreSectionScan()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("real/app.cs", "class App {}");
		temp.CreateFile("external/generated.ts", "export {}");
		Directory.CreateDirectory(Path.Combine(temp.Path, "real", "nested"));

		if (!TryCreateDirectorySymlink(
			    Path.Combine(temp.Path, "real", "nested", "linked-external"),
			    Path.Combine(temp.Path, "external")))
		{
			return;
		}

		var rules = CreateIgnoreRules();
		var tree = new TreeBuilder().Build(
			temp.Path,
			new TreeFilterOptions(
				AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".ts" },
				AllowedRootFolders: new HashSet<string>(PathComparer.Default) { "real" },
				IgnoreRules: rules));
		var snapshot = new ScanOptionsUseCase(new FileSystemScanner()).GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			["real"],
			rules,
			rules,
			effectiveAllowedExtensions: null,
			includeDirectoryToggleProbeRoots: true);

		var real = Assert.Single(tree.Root.Children, node => string.Equals(node.Name, "real", StringComparison.Ordinal));
		var nested = Assert.Single(real.Children, node => string.Equals(node.Name, "nested", StringComparison.Ordinal));
		Assert.DoesNotContain(nested.Children, node => string.Equals(node.Name, "linked-external", StringComparison.Ordinal));
		Assert.Contains(".cs", snapshot.Value.Extensions);
		Assert.DoesNotContain(".ts", snapshot.Value.Extensions);
		Assert.False(snapshot.RootAccessDenied);
	}

	[Fact]
	public void ScanOptions_DanglingDirectorySymlinkSelectedRoot_DoesNotReportExtensionsOrAccessDenied()
	{
		using var temp = new TemporaryDirectory();

		if (!TryCreateDanglingDirectorySymlink(
			    Path.Combine(temp.Path, "dangling"),
			    Path.Combine(temp.Path, "missing-target")))
		{
			return;
		}

		var rules = CreateIgnoreRules();
		var snapshot = new ScanOptionsUseCase(new FileSystemScanner()).GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			["dangling"],
			rules,
			rules,
			effectiveAllowedExtensions: null,
			includeDirectoryToggleProbeRoots: true);

		Assert.Empty(snapshot.Value.Extensions);
		Assert.False(snapshot.RootAccessDenied);
		Assert.False(snapshot.HadAccessDenied);
	}

	[Fact]
	public void ScanOptions_WindowsJunctionRoot_DoesNotBecomeRootFolderOrSelectedScanSource()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		temp.CreateFile("real/app.cs", "class App {}");
		temp.CreateFile("target/generated.ts", "export {}");

		if (!TryCreateDirectoryJunction(
			    Path.Combine(temp.Path, "junction"),
			    Path.Combine(temp.Path, "target")))
		{
			return;
		}

		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var rules = CreateIgnoreRules();
		var rootFolders = scanOptions.GetRootFolders(temp.Path, rules).Value;
		var snapshot = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			["junction"],
			rules,
			rules,
			effectiveAllowedExtensions: null,
			includeDirectoryToggleProbeRoots: true);

		Assert.DoesNotContain("junction", rootFolders);
		Assert.Empty(snapshot.Value.Extensions);
		Assert.False(snapshot.RootAccessDenied);
	}

	[Fact]
	public void SymlinkedGitIgnore_IsParsedWithoutTraversingSymlinkedContentAsProjectFiles()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("gitignore-target", "ignored/\n*.log\n");
		temp.CreateFile("src/app.cs", "class App {}");
		temp.CreateFile("ignored/noise.cs", "class Noise {}");
		temp.CreateFile("logs/runtime.log", "ignored log");

		if (!TryCreateFileSymlink(
			    Path.Combine(temp.Path, ".gitignore"),
			    Path.Combine(temp.Path, "gitignore-target")))
		{
			return;
		}

		var rules = new IgnoreRulesService(new SmartIgnoreService([])).Build(
			temp.Path,
			[IgnoreOptionId.UseGitIgnore],
			selectedRootFolders: ["src", "ignored", "logs"]);
		var tree = new TreeBuilder().Build(
			temp.Path,
			new TreeFilterOptions(
				AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".log" },
				AllowedRootFolders: new HashSet<string>(PathComparer.Default) { "src", "ignored", "logs" },
				IgnoreRules: rules));

		Assert.True(rules.UseGitIgnore);
		Assert.Contains(tree.Root.Children, node => string.Equals(node.Name, "src", StringComparison.Ordinal));
		Assert.DoesNotContain(tree.Root.Children, node => string.Equals(node.Name, "ignored", StringComparison.Ordinal));
		Assert.DoesNotContain(tree.Root.Children, node => string.Equals(node.Name, "logs", StringComparison.Ordinal));
	}

	[Fact]
	public void SymlinkedGitIgnore_TargetMutationInvalidatesMatcherCache()
	{
		using var temp = new TemporaryDirectory();
		var targetPath = Path.Combine(temp.Path, "gitignore-target");
		temp.CreateFile("gitignore-target", "ignored/\n");
		temp.CreateFile("src/app.cs", "class App {}");
		temp.CreateFile("ignored/noise.cs", "class Noise {}");

		if (!TryCreateFileSymlink(Path.Combine(temp.Path, ".gitignore"), targetPath))
			return;

		var ignoreRulesService = new IgnoreRulesService(new SmartIgnoreService([]));

		var firstRules = ignoreRulesService.Build(
			temp.Path,
			[IgnoreOptionId.UseGitIgnore],
			selectedRootFolders: ["src", "ignored"]);
		var firstTree = BuildGitIgnoreSymlinkTree(temp.Path, firstRules);
		Assert.DoesNotContain(firstTree.Root.Children, node => string.Equals(node.Name, "ignored", StringComparison.Ordinal));

		File.WriteAllText(targetPath, "# no ignores now\n");
		File.SetLastWriteTimeUtc(targetPath, DateTime.UtcNow.AddMinutes(1));

		var secondRules = ignoreRulesService.Build(
			temp.Path,
			[IgnoreOptionId.UseGitIgnore],
			selectedRootFolders: ["src", "ignored"]);
		var secondTree = BuildGitIgnoreSymlinkTree(temp.Path, secondRules);

		Assert.Contains(secondTree.Root.Children, node => string.Equals(node.Name, "ignored", StringComparison.Ordinal));
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

	private static bool TryCreateDanglingDirectorySymlink(string linkPath, string targetPath)
	{
		try
		{
			Directory.CreateSymbolicLink(linkPath, targetPath);
			return File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			return false;
		}
	}

	private static bool TryCreateFileSymlink(string linkPath, string targetPath)
	{
		try
		{
			File.CreateSymbolicLink(linkPath, targetPath);
			return IsReadableFileSymlink(linkPath);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			return false;
		}
	}

	private static bool IsReadableFileSymlink(string linkPath)
	{
		try
		{
			if (!File.Exists(linkPath))
				return false;

			if (!File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint))
				return false;

			var linkInfo = new FileInfo(linkPath);
			if (string.IsNullOrWhiteSpace(linkInfo.LinkTarget))
				return false;

			if (linkInfo.ResolveLinkTarget(returnFinalTarget: true) is not FileInfo { Exists: true })
				return false;

			// The production path reads the symlink as .gitignore. Validate that exact
			// operation before running assertions so unsupported platforms exit cleanly.
			using var stream = File.Open(linkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			return stream.CanRead;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			return false;
		}
	}

	private static TreeBuildResult BuildGitIgnoreSymlinkTree(string rootPath, IgnoreRules rules) =>
		new TreeBuilder().Build(
			rootPath,
			new TreeFilterOptions(
				AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
				AllowedRootFolders: new HashSet<string>(PathComparer.Default) { "src", "ignored" },
				IgnoreRules: rules));

	private static bool TryCreateDirectoryJunction(string linkPath, string targetPath)
	{
		try
		{
			var startInfo = new ProcessStartInfo(
				"cmd.exe",
				$"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
			{
				CreateNoWindow = true,
				UseShellExecute = false,
				RedirectStandardError = true,
				RedirectStandardOutput = true
			};
			using var process = Process.Start(startInfo);
			if (process is null)
				return false;

			if (!process.WaitForExit(5000))
			{
				process.Kill(entireProcessTree: true);
				return false;
			}

			return process.ExitCode == 0 &&
			       Directory.Exists(linkPath) &&
			       File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint);
		}
		catch
		{
			return false;
		}
	}
}
