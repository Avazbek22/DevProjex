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

		var includedNodes = ProjectTreeSelectionProjection.BuildIncludedNodes(fixture.Root, selected);
		var actual = includedNodes
			.Select(node => Path.GetRelativePath(fixture.Root.FullPath, node.FullPath).Replace('\\', '/'))
			.OrderBy(path => path, StringComparer.Ordinal)
			.ToArray();
		var includedPaths = includedNodes
			.Select(static node => node.FullPath)
			.ToHashSet(PathComparer.Default);
		var projected = ProjectTreeSelectionProjection.BuildProjectedTree(fixture.Root, includedPaths);
		var projectedPaths = EnumerateNodes(projected!)
			.Select(node => Path.GetRelativePath(fixture.Root.FullPath, node.FullPath).Replace('\\', '/'))
			.OrderBy(path => path, StringComparer.Ordinal)
			.ToArray();

		Assert.Equal(expectedRelativePaths.OrderBy(path => path, StringComparer.Ordinal), actual);
		Assert.Equal(expectedRelativePaths.OrderBy(path => path, StringComparer.Ordinal), projectedPaths);
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
			ProjectCopyExportFormat.Folder), TestContext.Current.CancellationToken);
		var checkedRootPlan = builder.Build(new ProjectCopyExportRequest(
			fixture.Root.FullPath,
			"root",
			fixture.Root,
			new HashSet<string>(PathComparer.Default)
			{
				fixture.Root.FullPath
			},
			Path.GetTempPath(),
			ProjectCopyExportFormat.Folder), TestContext.Current.CancellationToken);

		Assert.Equal(implicitPlan.Entries, checkedRootPlan.Entries);
		Assert.Equal(implicitPlan.FileCount, checkedRootPlan.FileCount);
		Assert.Equal(implicitPlan.DirectoryCount, checkedRootPlan.DirectoryCount);
	}

	[Theory]
	[InlineData(ProjectCopyExportFormat.Folder)]
	[InlineData(ProjectCopyExportFormat.Zip)]
	public void ExportPlan_CaseVariantPathsFollowDestinationSemantics(
		ProjectCopyExportFormat format)
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
			format);

		var builder = new ProjectCopyExportPlanBuilder();
		if (OperatingSystem.IsWindows() && format == ProjectCopyExportFormat.Folder)
		{
			var exception = Assert.Throws<ProjectCopyExportException>(() =>
				builder.Build(request, TestContext.Current.CancellationToken));
			Assert.Equal(ProjectCopyExportError.InvalidRequest, exception.Error);
			return;
		}

		var plan = builder.Build(
			request,
			TestContext.Current.CancellationToken);
		Assert.Equal(2, plan.FileCount);
		Assert.Equal(
			["File.txt", "file.txt"],
			plan.Entries
				.Where(static entry => !entry.IsDirectory)
				.Select(static entry => entry.RelativePath));
	}

	[Fact]
	public void SparseSelection_DeepTreeDoesNotDependOnTheCallStack()
	{
		const int depth = 16_000;
		var leafPath = "/root/leaf.txt";
		TreeNodeDescriptor root = new("leaf.txt", leafPath, false, false, "file", []);
		for (var level = depth - 1; level >= 0; level--)
		{
			root = new TreeNodeDescriptor(
				$"level-{level:D4}",
				$"/root/level-{level:D4}",
				true,
				false,
				"folder",
				[root]);
		}
		var selected = new HashSet<string>([leafPath], PathComparer.Default);

		var included = ProjectTreeSelectionProjection.BuildIncludedNodes(root, selected);
		var projected = ProjectTreeSelectionProjection.BuildProjectedTree(
			root,
			included.Select(static node => node.FullPath).ToHashSet(PathComparer.Default));
		var orderedFiles = ProjectTreeSelectionProjection.BuildOrderedSelectedFilePaths(
			root,
			selected,
			ensureExists: false);
		var collected = new HashSet<string>(PathComparer.Default);
		ProjectTreeSelectionProjection.CollectSelectedFilePaths(
			root,
			selected,
			collected,
			maxCount: 1,
			ensureExists: false);

		Assert.Equal(depth + 1, included.Count);
		Assert.Equal(leafPath, included[0].FullPath);
		Assert.Equal(root.FullPath, included[^1].FullPath);
		Assert.Equal(depth + 1, EnumerateNodes(projected!).Count());
		Assert.Equal(leafPath, Assert.Single(orderedFiles));
		Assert.Equal(leafPath, Assert.Single(collected));
	}

	[Fact]
	public void BuildIncludedNodesWithCancellation_StopsDuringTraversal()
	{
		using var cancellation = new CancellationTokenSource();
		var child = new TreeNodeDescriptor("file.txt", "/root/file.txt", false, false, "file", []);
		var root = new TreeNodeDescriptor(
			"root",
			"/root",
			true,
			false,
			"folder",
			new CancelOnReadList<TreeNodeDescriptor>([child], cancellation));

		Assert.Throws<OperationCanceledException>(() =>
			ProjectTreeSelectionProjection.BuildIncludedNodesWithCancellation(
				root,
				new HashSet<string>(PathComparer.Default),
				cancellation.Token));
	}

	[Fact]
	public void ExportPlanWithCancellation_StopsDuringTreeProjection()
	{
		using var cancellation = new CancellationTokenSource();
		var rootPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "project-copy-cancel-root"));
		var child = new TreeNodeDescriptor(
			"file.txt",
			Path.Combine(rootPath, "file.txt"),
			false,
			false,
			"file",
			[]);
		var root = new TreeNodeDescriptor(
			"root",
			rootPath,
			true,
			false,
			"folder",
			new CancelOnReadList<TreeNodeDescriptor>([child], cancellation));
		var request = new ProjectCopyExportRequest(
			rootPath,
			"root",
			root,
			new HashSet<string>(PathComparer.Default),
			Path.GetTempPath(),
			ProjectCopyExportFormat.Folder);

		Assert.Throws<OperationCanceledException>(() =>
			new ProjectCopyExportPlanBuilder().Build(request, cancellation.Token));
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

	private sealed class CancelOnReadList<T>(
		IReadOnlyList<T> items,
		CancellationTokenSource cancellation) : IReadOnlyList<T>
	{
		public int Count => items.Count;

		public T this[int index]
		{
			get
			{
				var item = items[index];
				cancellation.Cancel();
				return item;
			}
		}

		public IEnumerator<T> GetEnumerator() => items.GetEnumerator();

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private static IEnumerable<TreeNodeDescriptor> EnumerateNodes(TreeNodeDescriptor root)
	{
		var stack = new Stack<TreeNodeDescriptor>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			var node = stack.Pop();
			yield return node;
			for (var index = node.Children.Count - 1; index >= 0; index--)
				stack.Push(node.Children[index]);
		}
	}

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
