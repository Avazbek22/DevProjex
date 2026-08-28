using System.Collections.Specialized;
using DevProjex.Application.Preview;
using Terminal.Gui.Text;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspaceStateTests
{
	[Fact]
	public void VisibleTreeRebuildPublishesOneResetAndKeepsCachedRowText()
	{
		using var state = new TerminalWorkspaceState(CreatePlan());
		var collectionEvents = new List<NotifyCollectionChangedAction>();
		state.VisibleRows.CollectionChanged += (_, eventArgs) =>
			collectionEvents.Add(eventArgs.Action);
		var sourceRow = FindRow(state, "src");

		state.Expand(sourceRow);

		Assert.Equal([NotifyCollectionChangedAction.Reset], collectionEvents);
		var row = state.VisibleRows[sourceRow];
		Assert.Same(row.ToString(), row.ToString());
		Assert.Equal(row.ToString().GetColumns(), row.DisplayWidth);
	}

	[Fact]
	public void PreviewRefreshPublishesDocumentAndExactOutputMetricsTogether()
	{
		using var state = new TerminalWorkspaceState(CreatePlan());
		const string payload = "tree\r\nПривет 🙂\rcontent";
		var metrics = ExportOutputMetricsCalculator.FromText(payload);
		var document = new InMemoryPreviewTextDocument(payload);

		var applied = state.TrySetPreviewDocument(document, metrics, state.Revision);

		Assert.True(applied);
		Assert.Same(document, state.PreviewDocument);
		Assert.Equal(metrics, state.PreviewOutputMetrics);
		Assert.Equal(metrics.Tokens, TerminalWorkspaceSession.ResolveDisplayedTokenCount(state));
	}

	[Fact]
	public void EmptyEffectiveTreeRetainsOnlyTheRealRoot()
	{
		var root = CreateSyntheticRoot("empty-effective-tree");
		var tree = new TreeNodeDescriptor("project", root, true, false, "folder", []);
		using var state = new TerminalWorkspaceState(CreatePlan(tree, [], [root]));

		var row = Assert.Single(state.VisibleRows);
		Assert.Same(tree, row.Node);
		Assert.False(state.HasVisibleTreeItems);
		Assert.Empty(state.Plan.IncludedFiles);
	}

	[Fact]
	public void TreePreviewEscapesControlCharactersInDisplayNames()
	{
		var root = CreateSyntheticRoot("unsafe-tree-preview");
		var filePath = System.IO.Path.Combine(root, "file.cs");
		var file = new TreeNodeDescriptor("line\nbreak\t\u001B.cs", filePath, false, false, "file", []);
		var tree = new TreeNodeDescriptor("project\rname", root, true, false, "folder", [file]);
		using var state = new TerminalWorkspaceState(CreatePlan(tree, [filePath], [root]));

		Assert.Equal(
			$"+ project\\rname{Environment.NewLine}  - line\\nbreak\\t\\u001B.cs",
			state.PreviewText);
	}

	[Fact]
	public void CompleteSelectionUsesCanonicalEmptySelectedPaths()
	{
		var state = new TerminalWorkspaceState(CreatePlan());

		Assert.Empty(state.BuildSelectedRelativePaths());
		Assert.Equal(2, state.SelectedFileCount);
		Assert.Equal(3, state.SelectedFolderCount);
	}

	[Fact]
	public void PersistedSelectionUsesMinimalRootsWithoutLosingAllOrNone()
	{
		using var state = new TerminalWorkspaceState(CreatePlan());
		Assert.Equal(["."], state.BuildPersistedSelectedRelativePaths());

		state.SelectNone();
		Assert.Empty(state.BuildPersistedSelectedRelativePaths());

		state.RestoreSelectedRelativePaths(["src"]);
		Assert.Equal(["src"], state.BuildPersistedSelectedRelativePaths());
		Assert.Equal(2, state.SelectedFileCount);

		state.RestoreSelectedRelativePaths(["."]);
		Assert.Equal(["."], state.BuildPersistedSelectedRelativePaths());
		Assert.Equal(2, state.SelectedFileCount);
	}

	[Fact]
	public void DeselectingDirectoryBuildsMinimalSiblingSelection()
	{
		var state = new TerminalWorkspaceState(CreatePlan());
		var sourceRow = FindRow(state, "src");

		state.ToggleSelection(sourceRow);

		Assert.Equal(["empty"], state.BuildSelectedRelativePaths());
		Assert.Equal(0, state.SelectedFileCount);
		Assert.Equal(2, state.SelectedFolderCount);
	}

	[Fact]
	public void EmptyDirectoryCanBeDeselectedAndReselected()
	{
		var state = new TerminalWorkspaceState(CreatePlan());
		var emptyRow = FindRow(state, "empty");

		state.ToggleSelection(emptyRow);
		Assert.Equal(["src"], state.BuildSelectedRelativePaths());
		Assert.Equal(2, state.SelectedFolderCount);

		state.ToggleSelection(emptyRow);
		Assert.Empty(state.BuildSelectedRelativePaths());
		Assert.Equal(3, state.SelectedFolderCount);
	}

	[Fact]
	public void PartialSelectionSetsAncestorsIndeterminate()
	{
		var state = new TerminalWorkspaceState(CreatePlan());
		state.Expand(FindRow(state, "src"));
		state.ToggleSelection(FindRow(state, "a.cs"));

		var root = state.VisibleRows.Single(static row => row.Node.DisplayName == "project");
		var src = state.VisibleRows.Single(static row => row.Node.DisplayName == "src");
		Assert.Equal(TerminalTreeCheckState.Indeterminate, root.CheckState);
		Assert.Equal(TerminalTreeCheckState.Indeterminate, src.CheckState);
		Assert.Equal(["empty", "src/b.cs"], state.BuildSelectedRelativePaths());
	}

	[Fact]
	public void RepeatedLeafTogglesKeepAncestorStatesAndFolderCountsExact()
	{
		using var state = new TerminalWorkspaceState(CreatePlan());
		state.Expand(FindRow(state, "src"));
		var firstFileRow = FindRow(state, "a.cs");
		var secondFileRow = FindRow(state, "b.cs");

		state.ToggleSelection(firstFileRow);
		Assert.Equal(TerminalTreeCheckState.Indeterminate, state.VisibleRows[0].CheckState);
		Assert.Equal(TerminalTreeCheckState.Indeterminate, state.VisibleRows[1].CheckState);
		Assert.Equal(3, state.SelectedFolderCount);

		state.ToggleSelection(secondFileRow);
		Assert.Equal(TerminalTreeCheckState.Indeterminate, state.VisibleRows[0].CheckState);
		Assert.Equal(TerminalTreeCheckState.Unchecked, state.VisibleRows[1].CheckState);
		Assert.Equal(2, state.SelectedFolderCount);

		state.ToggleSelection(firstFileRow);
		state.ToggleSelection(secondFileRow);
		Assert.Equal(TerminalTreeCheckState.Checked, state.VisibleRows[0].CheckState);
		Assert.Equal(TerminalTreeCheckState.Checked, state.VisibleRows[1].CheckState);
		Assert.Equal(3, state.SelectedFolderCount);
		Assert.Empty(state.BuildSelectedRelativePaths());
	}

	[Fact]
	public void SearchWrapsWithoutMutatingTree()
	{
		var state = new TerminalWorkspaceState(CreatePlan());
		state.Expand(FindRow(state, "src"));
		var originalRows = state.VisibleRows.Select(static row => row.Node.FullPath).ToArray();

		var first = state.FindNext(".cs", -1);
		var second = state.FindNext(".cs", first);
		var wrapped = state.FindNext(".cs", second);

		Assert.NotEqual(first, second);
		Assert.Equal(first, wrapped);
		Assert.Equal(originalRows, state.VisibleRows.Select(static row => row.Node.FullPath));
	}

	[Fact]
	public void SearchFindsCollapsedDescendantAndExpandsOnlyItsAncestors()
	{
		var state = new TerminalWorkspaceState(CreatePlan());
		Assert.DoesNotContain(
			state.VisibleRows,
			static row => row.Node.DisplayName == "b.cs");

		var match = state.FindNext("b.cs", -1);

		Assert.InRange(match, 0, state.VisibleRows.Count - 1);
		Assert.Equal("b.cs", state.VisibleRows[match].Node.DisplayName);
		Assert.Contains(
			state.VisibleRows,
			static row => row.Node.DisplayName == "a.cs");
		Assert.Contains(
			state.VisibleRows,
			static row => row.Node.DisplayName == "empty");
	}

	[Fact]
	public void KeyboardOrMouseActivationTogglesOnlyAValidVisibleRow()
	{
		var state = new TerminalWorkspaceState(CreatePlan());
		var initialSelection = state.SelectedFileCount;

		Assert.False(TerminalWorkspace.TryToggleTreeRow(state, selectedRow: null));
		Assert.False(TerminalWorkspace.TryToggleTreeRow(state, selectedRow: -1));
		Assert.False(TerminalWorkspace.TryToggleTreeRow(state, selectedRow: state.VisibleRows.Count));
		Assert.Equal(initialSelection, state.SelectedFileCount);

		Assert.True(TerminalWorkspace.TryToggleTreeRow(state, selectedRow: 0));
		Assert.NotEqual(initialSelection, state.SelectedFileCount);
	}

	[Fact]
	public void SelectionReprojectionPreservesCollapsedRoot()
	{
		var state = new TerminalWorkspaceState(CreatePlan());
		state.Collapse(0);
		var revision = state.Revision;

		var applied = state.TryReplacePlan(CreatePlan(), revision);

		Assert.True(applied);
		var root = Assert.Single(state.VisibleRows);
		Assert.False(root.IsExpanded);
		Assert.Equal("project", root.Node.DisplayName);
	}

	[Fact]
	public void DeepFilteredProjectionPreservesPreorderDepthAndExactMatchCount()
	{
		const int nodeCount = 768;
		var rootPath = CreateSyntheticRoot("deep-filter");
		var paths = new string[nodeCount];
		var names = new string[nodeCount];
		for (var index = 0; index < nodeCount; index++)
		{
			paths[index] = index == 0
				? rootPath
				: Path.Combine(rootPath, $"node-{index:D4}");
			names[index] = index % 11 == 0 || index == nodeCount - 1
				? $"target-{index:D4}"
				: $"node-{index:D4}";
		}

		TreeNodeDescriptor tree = new(
			names[^1],
			paths[^1],
			IsDirectory: false,
			IsAccessDenied: false,
			"file",
			[]);
		for (var index = nodeCount - 2; index >= 0; index--)
		{
			tree = new TreeNodeDescriptor(
				names[index],
				paths[index],
				IsDirectory: true,
				IsAccessDenied: false,
				"folder",
				[tree]);
		}

		using var state = new TerminalWorkspaceState(CreatePlan(
			tree,
			includedFiles: [paths[^1]],
			includedFolders: paths[..^1]));

		state.ApplyTreeFilter("target");

		Assert.Equal(names.Count(static name => name.Contains("target", StringComparison.Ordinal)), state.TreeFilterMatchCount);
		Assert.Equal(nodeCount, state.VisibleRows.Count);
		for (var index = 0; index < nodeCount; index++)
		{
			var row = state.VisibleRows[index];
			Assert.Equal(paths[index], row.Node.FullPath);
			Assert.Equal(index, row.Depth);
			Assert.Equal(index < nodeCount - 1, row.IsExpanded);
		}
	}

	[Fact]
	public void WideFilteredProjectionPreservesExactPreorderAndOmitsNonMatchingLeaves()
	{
		const int branchCount = 96;
		const int leavesPerBranch = 96;
		const int matchInterval = 12;
		var rootPath = CreateSyntheticRoot("wide-filter");
		var branches = new List<TreeNodeDescriptor>(branchCount);
		var includedFiles = new List<string>(branchCount * leavesPerBranch);
		var expectedPaths = new List<string>(1 + branchCount + branchCount * (leavesPerBranch / matchInterval))
		{
			rootPath
		};

		for (var branchIndex = 0; branchIndex < branchCount; branchIndex++)
		{
			var branchPath = Path.Combine(rootPath, $"branch-{branchIndex:D3}");
			var leaves = new List<TreeNodeDescriptor>(leavesPerBranch);
			expectedPaths.Add(branchPath);
			for (var leafIndex = 0; leafIndex < leavesPerBranch; leafIndex++)
			{
				var isMatch = leafIndex % matchInterval == 0;
				var name = $"{(isMatch ? "target" : "other")}-{branchIndex:D3}-{leafIndex:D3}.cs";
				var path = Path.Combine(branchPath, name);
				leaves.Add(new TreeNodeDescriptor(
					name,
					path,
					IsDirectory: false,
					IsAccessDenied: false,
					"file",
					[]));
				includedFiles.Add(path);
				if (isMatch)
					expectedPaths.Add(path);
			}

			branches.Add(new TreeNodeDescriptor(
				$"branch-{branchIndex:D3}",
				branchPath,
				IsDirectory: true,
				IsAccessDenied: false,
				"folder",
				leaves));
		}

		var tree = new TreeNodeDescriptor(
			"project",
			rootPath,
			IsDirectory: true,
			IsAccessDenied: false,
			"folder",
			branches);
		using var state = new TerminalWorkspaceState(CreatePlan(
			tree,
			includedFiles,
			[rootPath, .. branches.Select(static branch => branch.FullPath)]));

		state.ApplyTreeFilter("target");

		var expectedMatchCount = branchCount * (leavesPerBranch / matchInterval);
		Assert.Equal(expectedMatchCount, state.TreeFilterMatchCount);
		Assert.Equal(expectedPaths, state.VisibleRows.Select(static row => row.Node.FullPath));
		Assert.True(state.VisibleRows[0].IsExpanded);
		Assert.All(
			state.VisibleRows.Where(static row => row.Depth == 1),
			static row => Assert.True(row.IsExpanded));
		Assert.All(
			state.VisibleRows.Where(static row => row.Depth == 2),
			static row => Assert.False(row.IsExpanded));
	}

	[Fact]
	public void LargeSelectedSubtreeBuildsOneCanonicalRelativePathAndKeepsMixedStates()
	{
		const int selectedFileCount = 12_000;
		var rootPath = CreateSyntheticRoot("large-selection");
		var selectedPath = Path.Combine(rootPath, "selected");
		var selectedFiles = new List<TreeNodeDescriptor>(selectedFileCount);
		var includedFiles = new List<string>(selectedFileCount);
		for (var index = 0; index < selectedFileCount; index++)
		{
			var path = Path.Combine(selectedPath, $"file-{index:D5}.cs");
			selectedFiles.Add(new TreeNodeDescriptor(
				Path.GetFileName(path),
				path,
				IsDirectory: false,
				IsAccessDenied: false,
				"file",
				[]));
			includedFiles.Add(path);
		}

		var selectedDirectory = new TreeNodeDescriptor(
			"selected",
			selectedPath,
			IsDirectory: true,
			IsAccessDenied: false,
			"folder",
			selectedFiles);
		var unselectedFile = new TreeNodeDescriptor(
			"unselected.cs",
			Path.Combine(rootPath, "unselected.cs"),
			IsDirectory: false,
			IsAccessDenied: false,
			"file",
			[]);
		var tree = new TreeNodeDescriptor(
			"project",
			rootPath,
			IsDirectory: true,
			IsAccessDenied: false,
			"folder",
			[selectedDirectory, unselectedFile]);
		using var state = new TerminalWorkspaceState(CreatePlan(
			tree,
			includedFiles,
			[rootPath, selectedPath]));

		var selectedRelativePaths = state.BuildSelectedRelativePaths();

		Assert.Equal(["selected"], selectedRelativePaths);
		Assert.Equal(selectedFileCount, state.SelectedFileCount);
		Assert.Equal(TerminalTreeCheckState.Indeterminate, state.VisibleRows[0].CheckState);
		Assert.Equal(TerminalTreeCheckState.Checked, state.VisibleRows[1].CheckState);
		Assert.Equal(TerminalTreeCheckState.Unchecked, state.VisibleRows[2].CheckState);
	}

	[Fact]
	public void DeepTreeInteractionDoesNotDependOnTheCallStack()
	{
		const int depth = 16_000;
		var rootPath = CreateSyntheticRoot("deep-interaction");
		var targetPath = Path.Combine(rootPath, "target.cs");
		TreeNodeDescriptor tree = new(
			"target.cs",
			targetPath,
			IsDirectory: false,
			IsAccessDenied: false,
			"file",
			[]);
		for (var index = depth - 1; index >= 0; index--)
		{
			var siblingPath = Path.Combine(rootPath, $"unselected-{index:D5}.cs");
			var sibling = new TreeNodeDescriptor(
				Path.GetFileName(siblingPath),
				siblingPath,
				IsDirectory: false,
				IsAccessDenied: false,
				"file",
				[]);
			var directoryPath = Path.Combine(rootPath, $"directory-{index:D5}");
			tree = new TreeNodeDescriptor(
				Path.GetFileName(directoryPath),
				directoryPath,
				IsDirectory: true,
				IsAccessDenied: false,
				"folder",
				[tree, sibling]);
		}
		tree = new TreeNodeDescriptor(
			"project",
			rootPath,
			IsDirectory: true,
			IsAccessDenied: false,
			"folder",
			[tree]);

		using var state = new TerminalWorkspaceState(CreatePlan(
			tree,
			includedFiles: [targetPath],
			includedFolders: []));

		Assert.Equal(["target.cs"], state.BuildSelectedRelativePaths());
		state.ApplyTreeFilter("target.cs");
		Assert.Equal(depth + 2, state.VisibleRows.Count);
		state.ApplyTreeFilter(null);
		var match = state.FindNext("target.cs", startIndex: -1);
		Assert.Equal(targetPath, state.VisibleRows[match].Node.FullPath);
	}

	[Theory]
	[InlineData(59, 20, TerminalWorkspaceLayoutMode.TooSmall)]
	[InlineData(60, 20, TerminalWorkspaceLayoutMode.Compact)]
	[InlineData(80, 24, TerminalWorkspaceLayoutMode.Tabbed)]
	[InlineData(120, 30, TerminalWorkspaceLayoutMode.Split)]
	public void LayoutUsesStableResponsiveBoundaries(
		int width,
		int height,
		TerminalWorkspaceLayoutMode expected)
	{
		Assert.Equal(expected, TerminalWorkspaceLayout.Resolve(width, height));
	}

	[Fact]
	public void BulkTreeOperationsAndRevealPreserveCanonicalState()
	{
		using var state = new TerminalWorkspaceState(CreatePlan());
		state.CollapseAll();
		Assert.Single(state.VisibleRows);

		var revealed = state.Reveal("src/a.cs");
		Assert.True(revealed >= 0);
		Assert.Equal("a.cs", state.VisibleRows[revealed].Node.DisplayName);

		state.SelectNone();
		Assert.Equal(0, state.SelectedFileCount);
		state.SelectAll();
		Assert.Equal(2, state.SelectedFileCount);

		state.ExpandAll();
		Assert.Equal(5, state.VisibleRows.Count);
		Assert.Contains("src", state.BuildExpandedRelativePaths());
	}

	private static int FindRow(TerminalWorkspaceState state, string name) =>
		state.VisibleRows
			.Select((row, index) => (row, index))
			.Single(tuple => tuple.row.Node.DisplayName == name)
			.index;

	private static ProjectContextPlan CreatePlan()
	{
		var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			"DevProjexTerminalState",
			"project"));
		var a = Node(root, "src/a.cs", isDirectory: false);
		var b = Node(root, "src/b.cs", isDirectory: false);
		var src = Node(root, "src", isDirectory: true, a, b);
		var empty = Node(root, "empty", isDirectory: true);
		var tree = new TreeNodeDescriptor("project", root, true, false, "folder", [src, empty]);
		var analysis = new ProjectAnalysisReport(
			1,
			DateTimeOffset.UnixEpoch,
			root,
			new ProjectAnalysisSelectionReport(["src", "empty"], [".cs"], []),
			new ProjectAnalysisInventoryReport(
				["src", "empty"],
				[".cs"],
				new ProjectTreeSummaryReport(3, 2, 0)),
			new ProjectAnalysisOutputMetricsReport(
				ProjectOutputMetricsReport.Empty,
				new ProjectOutputMetricsReport(2, 10, 3)),
			new ProjectAnalysisTimingReport(0, 0, 0),
			new ProjectAnalysisDiagnosticsReport(false, false, []));
		var files = new[] { a.FullPath, b.FullPath };
		var folders = new[] { root, src.FullPath, empty.FullPath };
		return new ProjectContextPlan(
			root,
			ProjectSelectionSpec.Standard,
			["empty", "src"],
			["empty", "src"],
			[".cs"],
			[".cs"],
			tree,
			tree,
			new HashSet<string>(PathComparer.Default),
			files,
			folders,
			analysis,
			[],
			new ProjectContextGitReadiness(GitFilteringMode.RespectGitIgnore, 1, true),
			"fingerprint");
	}

	private static ProjectContextPlan CreatePlan(
		TreeNodeDescriptor tree,
		IReadOnlyList<string> includedFiles,
		IReadOnlyList<string> includedFolders)
	{
		var analysis = new ProjectAnalysisReport(
			1,
			DateTimeOffset.UnixEpoch,
			tree.FullPath,
			new ProjectAnalysisSelectionReport([], [], []),
			new ProjectAnalysisInventoryReport(
				[],
				[],
				new ProjectTreeSummaryReport(includedFolders.Count, includedFiles.Count, 0)),
			new ProjectAnalysisOutputMetricsReport(
				ProjectOutputMetricsReport.Empty,
				ProjectOutputMetricsReport.Empty),
			new ProjectAnalysisTimingReport(0, 0, 0),
			new ProjectAnalysisDiagnosticsReport(false, false, []));
		return new ProjectContextPlan(
			tree.FullPath,
			ProjectSelectionSpec.Standard,
			[],
			[],
			[],
			[],
			tree,
			tree,
			new HashSet<string>(PathComparer.Default),
			includedFiles,
			includedFolders,
			analysis,
			[],
			new ProjectContextGitReadiness(GitFilteringMode.None, 0, true),
			"synthetic-fingerprint");
	}

	private static string CreateSyntheticRoot(string scenario) =>
		Path.GetFullPath(Path.Combine(
			Path.GetTempPath(),
			"DevProjexTerminalState",
			scenario,
			Guid.NewGuid().ToString("N")));

	private static TreeNodeDescriptor Node(
		string root,
		string relativePath,
		bool isDirectory,
		params TreeNodeDescriptor[] children) =>
		new(
			System.IO.Path.GetFileName(relativePath),
			System.IO.Path.Combine(root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)),
			isDirectory,
			false,
			isDirectory ? "folder" : "file",
			children);
}
