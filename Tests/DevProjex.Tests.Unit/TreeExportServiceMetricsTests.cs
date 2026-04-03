namespace DevProjex.Tests.Unit;

public sealed class TreeExportServiceMetricsTests
{
	[Theory]
	[InlineData(TreeTextFormat.Ascii)]
	[InlineData(TreeTextFormat.Json)]
	public void CalculateFullTreeMetrics_MatchesRenderedOutput_ForNestedTree(TreeTextFormat format)
	{
		var service = new TreeExportService();
		var root = CreateWorkspaceTree();
		var rootPath = @"C:\repo";

		var rendered = service.BuildFullTree(rootPath, root, format);
		var expected = ExportOutputMetricsCalculator.FromText(rendered);
		var actual = service.CalculateFullTreeMetrics(rootPath, root, format);

		Assert.Equal(expected, actual);
	}

	[Theory]
	[InlineData(TreeTextFormat.Ascii)]
	[InlineData(TreeTextFormat.Json)]
	public void CalculateFullTreeMetrics_MatchesRenderedOutput_WithPathPresentation(TreeTextFormat format)
	{
		var service = new TreeExportService();
		var root = CreateWorkspaceTree();
		const string displayRootPath = "https://github.com/user/repo";
		const string displayRootName = "repo-clean";

		var rendered = service.BuildFullTree(
			@"C:\repo",
			root,
			format,
			displayRootPath,
			displayRootName);
		var expected = ExportOutputMetricsCalculator.FromText(rendered);
		var actual = service.CalculateFullTreeMetrics(
			@"C:\repo",
			root,
			format,
			displayRootPath,
			displayRootName);

		Assert.Equal(expected, actual);
	}

	[Theory]
	[InlineData(TreeTextFormat.Ascii)]
	[InlineData(TreeTextFormat.Json)]
	public void CalculateSelectedTreeMetrics_MatchesRenderedOutput_ForDescendantSelection(TreeTextFormat format)
	{
		var service = new TreeExportService();
		var root = CreateWorkspaceTree();
		var selected = new HashSet<string>(PathComparer.Default)
		{
			@"C:\repo\src\main.cs",
			@"C:\repo\docs\guide.md"
		};

		var rendered = service.BuildSelectedTree(@"C:\repo", root, selected, format);
		var expected = ExportOutputMetricsCalculator.FromText(rendered);
		var actual = service.CalculateSelectedTreeMetrics(@"C:\repo", root, selected, format);

		Assert.Equal(expected, actual);
	}

	[Theory]
	[InlineData(TreeTextFormat.Ascii)]
	[InlineData(TreeTextFormat.Json)]
	public void CalculateSelectedTreeMetrics_MatchesRenderedOutput_ForDirectorySelection(TreeTextFormat format)
	{
		var service = new TreeExportService();
		var root = CreateWorkspaceTree();
		var selected = new HashSet<string>(PathComparer.Default)
		{
			@"C:\repo\src"
		};

		var rendered = service.BuildSelectedTree(@"C:\repo", root, selected, format);
		var expected = ExportOutputMetricsCalculator.FromText(rendered);
		var actual = service.CalculateSelectedTreeMetrics(@"C:\repo", root, selected, format);

		Assert.Equal(expected, actual);
	}

	[Theory]
	[InlineData(TreeTextFormat.Ascii)]
	[InlineData(TreeTextFormat.Json)]
	public void CalculateSelectedTreeMetrics_MatchesRenderedOutput_ForRootSelection(TreeTextFormat format)
	{
		var service = new TreeExportService();
		var root = CreateWorkspaceTree();
		var selected = new HashSet<string>(PathComparer.Default)
		{
			@"C:\repo"
		};

		var rendered = service.BuildSelectedTree(@"C:\repo", root, selected, format);
		var expected = ExportOutputMetricsCalculator.FromText(rendered);
		var actual = service.CalculateSelectedTreeMetrics(@"C:\repo", root, selected, format);

		Assert.Equal(expected, actual);
	}

	[Theory]
	[InlineData(TreeTextFormat.Ascii)]
	[InlineData(TreeTextFormat.Json)]
	public void CalculateSelectedTreeMetrics_MatchesRenderedOutput_ForEmptyDirectorySelection(TreeTextFormat format)
	{
		var service = new TreeExportService();
		var root = CreateWorkspaceTree();
		var selected = new HashSet<string>(PathComparer.Default)
		{
			@"C:\repo\empty"
		};

		var rendered = service.BuildSelectedTree(@"C:\repo", root, selected, format);
		var expected = ExportOutputMetricsCalculator.FromText(rendered);
		var actual = service.CalculateSelectedTreeMetrics(@"C:\repo", root, selected, format);

		Assert.Equal(expected, actual);
	}

	[Fact]
	public void CalculateSelectedTreeMetrics_ReturnsEmpty_WhenSelectionIsOutsideTree()
	{
		var service = new TreeExportService();
		var root = CreateWorkspaceTree();
		var selected = new HashSet<string>(PathComparer.Default)
		{
			@"C:\repo\missing.txt"
		};

		var actual = service.CalculateSelectedTreeMetrics(@"C:\repo", root, selected, TreeTextFormat.Ascii);

		Assert.Equal(ExportOutputMetrics.Empty, actual);
	}

	private static TreeNodeDescriptor CreateWorkspaceTree()
	{
		return new TreeNodeDescriptor(
			DisplayName: "repo",
			FullPath: @"C:\repo",
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children:
			[
				new TreeNodeDescriptor(
					DisplayName: "src",
					FullPath: @"C:\repo\src",
					IsDirectory: true,
					IsAccessDenied: false,
					IconKey: "folder",
					Children:
					[
						new TreeNodeDescriptor(
							DisplayName: "main.cs",
							FullPath: @"C:\repo\src\main.cs",
							IsDirectory: false,
							IsAccessDenied: false,
							IconKey: "csharp",
							Children: []),
						new TreeNodeDescriptor(
							DisplayName: "util.cs",
							FullPath: @"C:\repo\src\util.cs",
							IsDirectory: false,
							IsAccessDenied: false,
							IconKey: "csharp",
							Children: [])
					]),
				new TreeNodeDescriptor(
					DisplayName: "docs",
					FullPath: @"C:\repo\docs",
					IsDirectory: true,
					IsAccessDenied: false,
					IconKey: "folder",
					Children:
					[
						new TreeNodeDescriptor(
							DisplayName: "guide.md",
							FullPath: @"C:\repo\docs\guide.md",
							IsDirectory: false,
							IsAccessDenied: false,
							IconKey: "markdown",
							Children: [])
					]),
				new TreeNodeDescriptor(
					DisplayName: "empty",
					FullPath: @"C:\repo\empty",
					IsDirectory: true,
					IsAccessDenied: false,
					IconKey: "folder",
					Children: [])
			]);
	}
}
