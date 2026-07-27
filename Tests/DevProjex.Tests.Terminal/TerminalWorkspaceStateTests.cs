namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspaceStateTests
{
	[Fact]
	public void CompleteSelectionUsesCanonicalEmptySelectedPaths()
	{
		var state = new TerminalWorkspaceState(CreatePlan());

		Assert.Empty(state.BuildSelectedRelativePaths());
		Assert.Equal(2, state.SelectedFileCount);
		Assert.Equal(3, state.SelectedFolderCount);
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
