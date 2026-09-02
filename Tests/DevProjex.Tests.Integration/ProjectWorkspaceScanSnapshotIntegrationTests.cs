using DevProjex.Application.Diagnostics;

namespace DevProjex.Tests.Integration;

public sealed class ProjectWorkspaceScanSnapshotIntegrationTests
{
	[Fact]
	public void WorkspaceSnapshot_ReusesDiscoveryEnumerationForNestedFiles()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/App.cs", "class App {}");
		temp.CreateFile("src/Nested/Feature.cs", "class Feature {}");
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

		using var measurement = IgnorePipelineDiagnostics.BeginMeasurement();
		var snapshot = new ScanOptionsUseCase(new FileSystemScanner())
			.GetProjectWorkspaceSnapshotForRootFolders(
				temp.Path,
				["src"],
				rules,
				rules,
				effectiveExtensionPolicy: null,
				includeDirectoryToggleProbeRoots: false,
				cancellationToken: TestContext.Current.CancellationToken,
				includeControllerImpactProbeRoots: false);
		var diagnostics = measurement.Capture();
		var inventory = Assert.IsType<ProjectTreeInventorySnapshot>(snapshot.Value.TreeInventory);

		Assert.Contains(inventory.Entries, static entry => entry.Name == "App.cs");
		Assert.Contains(inventory.Entries, static entry => entry.Name == "Feature.cs");
		Assert.True(diagnostics.CombinedEntryEnumerations >= 2);
		Assert.True(
			diagnostics.FileEnumerations < diagnostics.CombinedEntryEnumerations,
			$"Nested files were enumerated twice: {diagnostics}.");
	}

	[Fact]
	public void WorkspaceSnapshot_ProjectsSameTreeInventoryAsSeparateTreeScanner()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "logs/\n*.tmp\n");
		temp.CreateFile("root-a.txt", "root");
		temp.CreateFile("root-z.tmp", "ignored by projection");
		temp.CreateFile("src/App.cs", "class App {}");
		temp.CreateFile("src/Models/User.cs", "class User {}");
		temp.CreateFile("docs/readme.md", "# docs");
		temp.CreateFile("bin/Debug/App.dll", "binary");
		temp.CreateFile(".idea/workspace.xml", "<project />");
		temp.CreateFile("logs/runtime.log", "log");
		temp.CreateFile("logs/keep.tmp", "tmp");

		var rules = CreateRules(temp.Path);
		var selectedRoots = new HashSet<string>(["src", "docs", "bin", ".idea", "logs"], PathComparer.Default);
		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>([".cs", ".md", ".txt", ".tmp", ".log", ".xml"], StringComparer.OrdinalIgnoreCase),
			AllowedRootFolders: selectedRoots,
			IgnoreRules: rules);
		var builder = new TreeBuilder();
		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());

		var workspace = scanOptions.GetProjectWorkspaceSnapshotForRootFolders(
			temp.Path,
			selectedRoots,
			extensionDiscoveryRules: rules,
			effectiveRules: rules,
			effectiveExtensionPolicy: null,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.NotNull(workspace.Value.TreeInventory);
		var workspaceInventory = FlattenInventory(workspace.Value.TreeInventory);
		Assert.Contains(workspaceInventory, entry => entry.Contains("|.idea|.idea|", StringComparison.Ordinal));

		var directTree = builder.Build(temp.Path, options, TestContext.Current.CancellationToken);
		var workspaceTree = builder.Build(workspace.Value.TreeInventory, options, TestContext.Current.CancellationToken);
		Assert.Equal(FlattenTree(directTree.Root), FlattenTree(workspaceTree.Root));
		Assert.Contains(workspaceTree.Root.Children, child => child.Name == "src");
		Assert.Contains(workspaceTree.Root.Children, child => child.Name == "docs");
		Assert.DoesNotContain(workspaceTree.Root.Children, child => child.Name == "bin");
		Assert.DoesNotContain(workspaceTree.Root.Children, child => child.Name == ".idea");
		Assert.DoesNotContain(workspaceTree.Root.Children, child => child.Name == "logs");

		var includeDotRules = rules with { IgnoreDotFolders = false };
		var includeDotOptions = options with { IgnoreRules = includeDotRules };
		var directIncludeDotTree = builder.Build(temp.Path, includeDotOptions, TestContext.Current.CancellationToken);
		var workspaceIncludeDotTree = builder.Build(workspace.Value.TreeInventory, includeDotOptions, TestContext.Current.CancellationToken);
		Assert.Equal(FlattenTree(directIncludeDotTree.Root), FlattenTree(workspaceIncludeDotTree.Root));
		Assert.Contains(workspaceIncludeDotTree.Root.Children, child => child.Name == ".idea");
	}

	[Fact]
	public void WorkspaceSnapshot_WithNameFilter_KeepsProjectionEquivalentToDirectBuild()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("api/src/OrderHandler.cs", "class OrderHandler {}");
		temp.CreateFile("api/src/UserHandler.cs", "class UserHandler {}");
		temp.CreateFile("client/src/order-view.ts", "export {}");
		temp.CreateFile("client/src/user-view.ts", "export {}");
		temp.CreateFile("node_modules/pkg/order-noise.ts", "export {}");

		var rules = CreateRules(temp.Path);
		var selectedRoots = new HashSet<string>(["api", "client", "node_modules"], PathComparer.Default);
		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>([".cs", ".ts"], StringComparer.OrdinalIgnoreCase),
			AllowedRootFolders: selectedRoots,
			IgnoreRules: rules,
			NameFilter: "order");
		var builder = new TreeBuilder();
		var workspace = new ScanOptionsUseCase(new FileSystemScanner()).GetProjectWorkspaceSnapshotForRootFolders(
			temp.Path,
			selectedRoots,
			extensionDiscoveryRules: rules,
			effectiveRules: rules,
			effectiveExtensionPolicy: null,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.NotNull(workspace.Value.TreeInventory);
		var directTree = builder.Build(temp.Path, options, TestContext.Current.CancellationToken);
		var workspaceTree = builder.Build(workspace.Value.TreeInventory, options, TestContext.Current.CancellationToken);

		Assert.Equal(FlattenTree(directTree.Root), FlattenTree(workspaceTree.Root));
		Assert.DoesNotContain(FlattenTree(workspaceTree.Root), path => path.Contains("node_modules", StringComparison.OrdinalIgnoreCase));
		Assert.Contains(FlattenTree(workspaceTree.Root), path => path.Contains("OrderHandler.cs", StringComparison.OrdinalIgnoreCase));
		Assert.DoesNotContain(FlattenTree(workspaceTree.Root), path => path.Contains("UserHandler.cs", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void WorkspaceSnapshot_RepeatedScanMatchesIgnoreOnlyCountsAndTreeProjection()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "git-owned/\n.git-owned-*/\n");
		temp.CreateFile("root.txt", "root");
		temp.CreateFile("src/App.cs", "class App {}");
		temp.CreateFile("docs/readme.md", "# docs");
		temp.CreateFile("node_modules/pkg/index.js", "module.exports = {};");
		temp.CreateFile("git-owned/log.txt", "git owned");
		for (var index = 0; index < 8; index++)
		{
			temp.CreateFile(
				Path.Combine($".dot-root-{index:D2}", "payload.txt"),
				$"dot payload {index}");
		}

		var rules = CreateRules(temp.Path);
		var selectedRoots = new HashSet<string>(["src", "docs", "node_modules"], PathComparer.Default);
		var selectedExtensions = new HashSet<string>([".cs", ".js", ".md", ".txt"], StringComparer.OrdinalIgnoreCase);
		var extensionPolicy = new ExtensionSetInclusionPolicy(selectedExtensions);
		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());

		var first = scanOptions.GetProjectWorkspaceSnapshotForRootFolders(
			temp.Path,
			selectedRoots,
			extensionDiscoveryRules: rules,
			effectiveRules: rules,
			effectiveExtensionPolicy: extensionPolicy,
			includeDirectoryToggleProbeRoots: true,
			cancellationToken: TestContext.Current.CancellationToken,
			includeControllerImpactProbeRoots: true);
		var second = scanOptions.GetProjectWorkspaceSnapshotForRootFolders(
			temp.Path,
			selectedRoots,
			extensionDiscoveryRules: rules,
			effectiveRules: rules,
			effectiveExtensionPolicy: extensionPolicy,
			includeDirectoryToggleProbeRoots: true,
			cancellationToken: TestContext.Current.CancellationToken,
			includeControllerImpactProbeRoots: true);
		var ignoreOnly = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			selectedRoots,
			extensionDiscoveryRules: rules,
			effectiveRules: rules,
			effectiveExtensionPolicy: extensionPolicy,
			includeDirectoryToggleProbeRoots: true,
			cancellationToken: TestContext.Current.CancellationToken,
			includeControllerImpactProbeRoots: true);
		var explicitCounts = scanOptions.GetEffectiveIgnoreOptionCountsForRootFolders(
			temp.Path,
			selectedRoots,
			selectedExtensions,
			rules,
			ignoreOnly.Value.RawIgnoreOptionCounts,
			includeDirectoryToggleProbeRoots: true,
			cancellationToken: TestContext.Current.CancellationToken);

		AssertWorkspaceSnapshotEquivalent(first.Value, second.Value);
		Assert.Equal(ignoreOnly.Value.Extensions.Order(StringComparer.OrdinalIgnoreCase), first.Value.IgnoreSection.Extensions.Order(StringComparer.OrdinalIgnoreCase));
		Assert.Equal(ignoreOnly.Value.RawIgnoreOptionCounts, first.Value.IgnoreSection.RawIgnoreOptionCounts);
		Assert.Equal(ignoreOnly.Value.EffectiveIgnoreOptionCounts, first.Value.IgnoreSection.EffectiveIgnoreOptionCounts);
		Assert.Equal(ignoreOnly.Value.ControllerImpactCounts, first.Value.IgnoreSection.ControllerImpactCounts);
		Assert.Equal(ignoreOnly.Value.EffectiveIgnoreOptionCounts, explicitCounts.Value);
		Assert.Equal(8, first.Value.IgnoreSection.EffectiveIgnoreOptionCounts.DotFolders);

		var inventory = Assert.IsType<ProjectTreeInventorySnapshot>(first.Value.TreeInventory);
		Assert.Equal(0, CountRootDotDirectories(inventory));
		AssertTreeProjectionEqualsDirectBuild(temp.Path, selectedRoots, selectedExtensions, rules, inventory);

		var dotFoldersOffRules = rules with { IgnoreDotFolders = false };
		AssertTreeProjectionEqualsDirectBuild(temp.Path, selectedRoots, selectedExtensions, dotFoldersOffRules, inventory);
	}

	[Fact]
	public void ProjectWorkspaceScanRequest_CaptureTreeInventoryOnlyChangesInventoryPayload()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "ignored-by-git/\n");
		temp.CreateFile("root.txt", "root");
		temp.CreateFile("src/App.cs", "class App {}");
		temp.CreateDirectory("src/nested/.git");
		temp.CreateFile("src/.env", "secret");
		temp.CreateFile("docs/readme.md", "# docs");
		temp.CreateFile("node_modules/pkg/index.js", "module.exports = {};");
		temp.CreateFile("ignored-by-git/payload.txt", "ignored");

		var rules = CreateRules(temp.Path);
		var selectedRoots = new HashSet<string>(["src", "docs", "node_modules"], PathComparer.Default);
		var selectedExtensions = new HashSet<string>([".cs", ".md", ".txt"], StringComparer.OrdinalIgnoreCase);
		var extensionPolicy = new ExtensionSetInclusionPolicy(selectedExtensions);
		var scanner = new FileSystemScanner();
		var lightRequest = new ProjectWorkspaceScanRequest(
			temp.Path,
			selectedRoots,
			rules,
			rules,
			extensionPolicy,
			CaptureTreeInventory: false,
			IncludeDirectoryToggleProbeRoots: true,
			IncludeControllerImpactProbeRoots: true);
		var fullRequest = lightRequest with { CaptureTreeInventory = true };

		var light = scanner.ScanProjectWorkspace(lightRequest, TestContext.Current.CancellationToken);
		var full = scanner.ScanProjectWorkspace(fullRequest, TestContext.Current.CancellationToken);
		var useCaseIgnoreOnly = new ScanOptionsUseCase(
			LegacyWorkspaceScannerTestAdapter.Adapt(scanner)).GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			selectedRoots,
			extensionDiscoveryRules: rules,
			effectiveRules: rules,
			effectiveExtensionPolicy: extensionPolicy,
			includeDirectoryToggleProbeRoots: true,
			cancellationToken: TestContext.Current.CancellationToken,
			includeControllerImpactProbeRoots: true);

		Assert.Null(light.Value.TreeInventory);
		var inventory = Assert.IsType<ProjectTreeInventorySnapshot>(full.Value.TreeInventory);
		AssertIgnoreSectionEquivalent(light.Value.IgnoreSection, full.Value.IgnoreSection);
		AssertIgnoreSectionEquivalent(useCaseIgnoreOnly.Value, full.Value.IgnoreSection);
		Assert.True(full.Value.IgnoreSection.GitEvidence.HasRepositoryBoundary);
		AssertTreeProjectionEqualsDirectBuild(temp.Path, selectedRoots, selectedExtensions, rules, inventory);
	}

	[Fact]
	public void Execute_RootOnlyWorkspaceKeepsRootFileExtensions()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("README.md", "# root");
		var rules = CreateRules(temp.Path);

		var result = new ScanOptionsUseCase(new FileSystemScanner()).Execute(
			new ScanOptionsRequest(temp.Path, rules),
			TestContext.Current.CancellationToken);

		Assert.Equal([".md"], result.Extensions);
		Assert.Empty(result.RootFolders);
		Assert.False(result.RootAccessDenied);
		Assert.False(result.HadAccessDenied);
	}

	[Fact]
	public void WorkspaceSnapshot_IgnoresRootedNestedEscapingAndStaleRootSelections()
	{
		using var project = new TemporaryDirectory();
		using var outside = new TemporaryDirectory();
		project.CreateFile("README.md", "# root");
		project.CreateFile("src/App.cs", "class App {}");
		project.CreateFile("docs/guide.txt", "docs");
		outside.CreateFile("Leak.secret", "outside");
		var rules = CreateRules(project.Path);
		var invalidNestedSelection = Path.Combine("src", "nested");

		var result = new ScanOptionsUseCase(new FileSystemScanner())
			.GetProjectWorkspaceSnapshotForRootFolders(
				project.Path,
				["src", "missing", ".", "..", invalidNestedSelection, outside.Path],
				extensionDiscoveryRules: rules,
				effectiveRules: rules,
				effectiveExtensionPolicy: null,
				cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(
			[".cs", ".md"],
			result.Value.IgnoreSection.Extensions.Order(StringComparer.OrdinalIgnoreCase));
		var inventory = Assert.IsType<ProjectTreeInventorySnapshot>(result.Value.TreeInventory);
		Assert.Contains(inventory.Entries, static entry => entry.Name == "src");
		Assert.Contains(inventory.Entries, static entry => entry.Name == "App.cs");
		Assert.Contains(inventory.Entries, static entry => entry.Name == "README.md");
		Assert.DoesNotContain(inventory.Entries, static entry => entry.Name == "docs");
		Assert.DoesNotContain(inventory.Entries, static entry => entry.Name == "Leak.secret");
		Assert.All(inventory.Entries, entry =>
			Assert.True(
				PathComparer.Default.Equals(entry.FullPath, project.Path) ||
				PathUtility.IsPathInside(entry.FullPath, project.Path),
				$"Inventory entry escaped the project root: {entry.FullPath}"));
	}

	private static IgnoreRules CreateRules(string rootPath)
	{
		return new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: true,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(["bin", "obj", "node_modules"], StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			UseSmartIgnore = true,
			UseGitIgnore = true,
			GitIgnoreMatcher = GitIgnoreMatcher.Build(rootPath, File.Exists(Path.Combine(rootPath, ".gitignore"))
				? File.ReadLines(Path.Combine(rootPath, ".gitignore"))
				: []),
			SmartIgnoreScopeRoots = [rootPath]
		};
	}

	private static List<string> FlattenInventory(ProjectTreeInventorySnapshot snapshot)
	{
		var result = new List<string>(snapshot.Entries.Count);
		for (var index = 0; index < snapshot.Entries.Count; index++)
		{
			ref readonly var entry = ref snapshot.GetEntryRef(index);
			result.Add(string.Join(
				"|",
				index,
				entry.Name,
				entry.RelativePath,
				entry.ParentIndex,
				entry.IsDirectory,
				entry.IsHidden,
				entry.Length,
				entry.FirstChildIndex,
				entry.ChildCount,
				entry.IsAccessDenied));
		}

		return result;
	}

	private static void AssertWorkspaceSnapshotEquivalent(
		ProjectWorkspaceScanSnapshot expected,
		ProjectWorkspaceScanSnapshot actual)
	{
		AssertIgnoreSectionEquivalent(expected.IgnoreSection, actual.IgnoreSection);
		Assert.NotNull(expected.TreeInventory);
		Assert.NotNull(actual.TreeInventory);
		Assert.Equal(FlattenInventory(expected.TreeInventory), FlattenInventory(actual.TreeInventory));
	}

	private static void AssertIgnoreSectionEquivalent(
		IgnoreSectionScanData expected,
		IgnoreSectionScanData actual)
	{
		Assert.Equal(expected.Extensions.Order(StringComparer.OrdinalIgnoreCase), actual.Extensions.Order(StringComparer.OrdinalIgnoreCase));
		Assert.Equal(expected.RawIgnoreOptionCounts, actual.RawIgnoreOptionCounts);
		Assert.Equal(expected.EffectiveIgnoreOptionCounts, actual.EffectiveIgnoreOptionCounts);
		Assert.Equal(expected.ControllerImpactCounts, actual.ControllerImpactCounts);
		Assert.Equal(expected.GitEvidence, actual.GitEvidence);
	}

	private static void AssertTreeProjectionEqualsDirectBuild(
		string rootPath,
		IReadOnlySet<string> selectedRoots,
		IReadOnlySet<string> selectedExtensions,
		IgnoreRules rules,
		ProjectTreeInventorySnapshot inventory)
	{
		var options = new TreeFilterOptions(selectedExtensions, selectedRoots, rules);
		var builder = new TreeBuilder();
		var directTree = builder.Build(rootPath, options, TestContext.Current.CancellationToken);
		var inventoryTree = builder.Build(inventory, options, TestContext.Current.CancellationToken);

		Assert.Equal(FlattenTree(directTree.Root), FlattenTree(inventoryTree.Root));
	}

	private static int CountRootDotDirectories(ProjectTreeInventorySnapshot snapshot)
	{
		var count = 0;
		var children = snapshot.GetChildren(0);
		for (var index = 0; index < children.Length; index++)
		{
			var child = children[index];
			if (child.IsDirectory && IgnoreRuleSemantics.IsDotName(child.Name))
				count++;
		}

		return count;
	}

	private static List<string> FlattenTree(FileSystemNode root)
	{
		var paths = new List<string>();
		var pending = new Stack<FileSystemNode>();
		pending.Push(root);
		while (pending.Count > 0)
		{
			var node = pending.Pop();
			paths.Add($"{node.FullPath}|{node.IsDirectory}|{node.IsAccessDenied}");
			for (var index = node.Children.Count - 1; index >= 0; index--)
				pending.Push(node.Children[index]);
		}

		return paths;
	}

}
