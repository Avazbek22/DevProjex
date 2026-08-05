namespace DevProjex.Tests.Unit;

public sealed class ProjectTreeSelectionProjectionTests
{
	[Fact]
	public void NormalizeSelectedPaths_ImplicitAndCheckedRootUseCanonicalFullTreeScope()
	{
		var fixture = SelectionFixture.Create();
		var implicitSelection = new HashSet<string>(PathComparer.Default);
		var checkedRoot = new HashSet<string>(PathComparer.Default)
		{
			fixture.Root.FullPath
		};

		var implicitResult =
			ProjectTreeSelectionProjection.NormalizeSelectedPaths(
				fixture.Root,
				implicitSelection);
		var checkedRootResult =
			ProjectTreeSelectionProjection.NormalizeSelectedPaths(
				fixture.Root,
				checkedRoot);

		Assert.Empty(implicitResult);
		Assert.Empty(checkedRootResult);
		Assert.True(ProjectTreeSelectionProjection.CoversWholeTree(
			fixture.Root,
			implicitSelection));
		Assert.True(ProjectTreeSelectionProjection.CoversWholeTree(
			fixture.Root,
			checkedRoot));
	}

	[Fact]
	public void NormalizeSelectedPaths_PartialSelectionPreservesExplicitSet()
	{
		var fixture = SelectionFixture.Create();
		var selected = new HashSet<string>(PathComparer.Default)
		{
			fixture.Paths["src"]
		};

		var result = ProjectTreeSelectionProjection.NormalizeSelectedPaths(
			fixture.Root,
			selected);

		Assert.Same(selected, result);
		Assert.False(ProjectTreeSelectionProjection.CoversWholeTree(
			fixture.Root,
			selected));
	}

	[Theory]
	[MemberData(nameof(SelectionCases))]
	public void BuildIncludedNodes_SelectionMatrixPreservesExactEffectiveSubtree(
		string caseName,
		string[] selectedKeys,
		string[] expectedRelativePaths)
	{
		Assert.False(string.IsNullOrWhiteSpace(caseName));
		var fixture = SelectionFixture.Create();
		var selected = selectedKeys
			.Select(key => fixture.Paths[key])
			.ToHashSet(PathComparer.Default);

		var actual = ProjectTreeSelectionProjection.BuildIncludedNodes(fixture.Root, selected)
			.Select(node => Path.GetRelativePath(fixture.Root.FullPath, node.FullPath).Replace('\\', '/'))
			.OrderBy(path => path, StringComparer.Ordinal)
			.ToArray();

		Assert.Equal(expectedRelativePaths.OrderBy(path => path, StringComparer.Ordinal), actual);
	}

	[Fact]
	public void BuildOrderedSelectedFilePaths_ParentAndChildOverlapDoesNotDuplicateFile()
	{
		var fixture = SelectionFixture.Create();
		var selected = new HashSet<string>(PathComparer.Default)
		{
			fixture.Paths["src"],
			fixture.Paths["program"]
		};

		var actual = ProjectTreeSelectionProjection.BuildOrderedSelectedFilePaths(
			fixture.Root,
			selected,
			ensureExists: false);

		Assert.Equal(2, actual.Count);
		Assert.Equal(2, actual.Distinct(PathComparer.Default).Count());
	}

	[Fact]
	public void ExportPlan_CheckedRootMatchesImplicitFullTree()
	{
		var fixture = SelectionFixture.Create();
		var builder = new ProjectCopyExportPlanBuilder();
		var implicitPlan = builder.Build(new ProjectCopyExportRequest(
			fixture.Root.FullPath,
			"root",
			fixture.Root,
			new HashSet<string>(PathComparer.Default),
			Path.GetTempPath(),
			ProjectCopyExportFormat.Folder));
		var checkedRootPlan = builder.Build(new ProjectCopyExportRequest(
			fixture.Root.FullPath,
			"root",
			fixture.Root,
			new HashSet<string>(PathComparer.Default)
			{
				fixture.Root.FullPath
			},
			Path.GetTempPath(),
			ProjectCopyExportFormat.Folder));

		Assert.Equal(implicitPlan.Entries, checkedRootPlan.Entries);
		Assert.Equal(implicitPlan.FileCount, checkedRootPlan.FileCount);
		Assert.Equal(implicitPlan.DirectoryCount, checkedRootPlan.DirectoryCount);
	}

	[Fact]
	public void ExportPlan_CaseVariantPathsFollowCurrentPlatformFilesystemSemantics()
	{
		var rootPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "project-copy-case-root"));
		var upperPath = Path.Combine(rootPath, "File.txt");
		var lowerPath = Path.Combine(rootPath, "file.txt");
		var tree = new TreeNodeDescriptor("root", rootPath, true, false, "folder",
		[
			new TreeNodeDescriptor("File.txt", upperPath, false, false, "file", []),
			new TreeNodeDescriptor("file.txt", lowerPath, false, false, "file", [])
		]);
		var request = new ProjectCopyExportRequest(
			rootPath,
			"root",
			tree,
			new HashSet<string>(PathComparer.Default),
			Path.GetTempPath(),
			ProjectCopyExportFormat.Folder);

		var plan = new ProjectCopyExportPlanBuilder().Build(request);

		Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2, plan.FileCount);
	}

	public static TheoryData<string, string[], string[]> SelectionCases => new()
	{
		{
			"nothing selected exports complete effective tree",
			[],
			[".", "README.md", "docs", "docs/empty", "docs/guide.md", "src", "src/Program.cs", "src/assets.bin"]
		},
		{
			"checked project root exports complete effective tree",
			["root"],
			[".", "README.md", "docs", "docs/empty", "docs/guide.md", "src", "src/Program.cs", "src/assets.bin"]
		},
		{
			"single file keeps required ancestors",
			["program"],
			[".", "src", "src/Program.cs"]
		},
		{
			"selected directory includes lazy descriptor descendants",
			["src"],
			[".", "src", "src/Program.cs", "src/assets.bin"]
		},
		{
			"partial directory selection excludes siblings",
			["guide"],
			[".", "docs", "docs/guide.md"]
		},
		{
			"multiple root branches preserve both paths",
			["program", "docsEmpty"],
			[".", "docs", "docs/empty", "src", "src/Program.cs"]
		},
		{
			"parent and child overlap remains one subtree",
			["src", "program"],
			[".", "src", "src/Program.cs", "src/assets.bin"]
		}
	};

	private sealed record SelectionFixture(
		TreeNodeDescriptor Root,
		IReadOnlyDictionary<string, string> Paths)
	{
		public static SelectionFixture Create()
		{
			var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "project-selection-root"));
			var paths = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["root"] = root,
				["readme"] = Path.Combine(root, "README.md"),
				["src"] = Path.Combine(root, "src"),
				["program"] = Path.Combine(root, "src", "Program.cs"),
				["asset"] = Path.Combine(root, "src", "assets.bin"),
				["docs"] = Path.Combine(root, "docs"),
				["guide"] = Path.Combine(root, "docs", "guide.md"),
				["docsEmpty"] = Path.Combine(root, "docs", "empty")
			};

			var descriptor = DirectoryNode(paths["root"],
				FileNode(paths["readme"]),
				DirectoryNode(paths["src"], FileNode(paths["program"]), FileNode(paths["asset"])),
				DirectoryNode(paths["docs"], FileNode(paths["guide"]), DirectoryNode(paths["docsEmpty"])));
			return new SelectionFixture(descriptor, paths);
		}

		private static TreeNodeDescriptor DirectoryNode(string path, params TreeNodeDescriptor[] children) =>
			new(Path.GetFileName(path), path, true, false, "folder", children);

		private static TreeNodeDescriptor FileNode(string path) =>
			new(Path.GetFileName(path), path, false, false, "file", []);
	}
}
