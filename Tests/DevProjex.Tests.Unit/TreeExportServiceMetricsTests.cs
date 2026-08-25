namespace DevProjex.Tests.Unit;

public sealed class TreeExportServiceMetricsTests
{
	[Theory]
	[InlineData(TreeTextFormat.Ascii)]
	[InlineData(TreeTextFormat.Json)]
	[InlineData(TreeTextFormat.Xml)]
	[InlineData(TreeTextFormat.Markdown)]
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
	[InlineData(TreeTextFormat.Xml)]
	[InlineData(TreeTextFormat.Markdown)]
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
	[InlineData(TreeTextFormat.Xml)]
	[InlineData(TreeTextFormat.Markdown)]
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
	[InlineData(TreeTextFormat.Xml)]
	[InlineData(TreeTextFormat.Markdown)]
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
	[InlineData(TreeTextFormat.Xml)]
	[InlineData(TreeTextFormat.Markdown)]
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
	[InlineData(TreeTextFormat.Xml)]
	[InlineData(TreeTextFormat.Markdown)]
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

	[Fact]
	public void CalculateFullTreeMetrics_PreservesCountsBeyondInt32Range()
	{
		const int childCount = 70_000;
		var displayName = new string('x', 32_768);
		var child = new TreeNodeDescriptor(
			displayName,
			"/root/file",
			IsDirectory: false,
			IsAccessDenied: false,
			IconKey: "file",
			Children: []);
		var root = new TreeNodeDescriptor(
			"root",
			"/root",
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: Enumerable.Repeat(child, childCount).ToArray());
		var expectedCharacters = 17L + childCount * (displayName.Length + 9L);

		var actual = new TreeExportService().CalculateFullTreeMetrics(
			"/root",
			root,
			TreeTextFormat.Ascii);

		Assert.True(actual.Chars > int.MaxValue);
		Assert.Equal(expectedCharacters, actual.Chars);
		Assert.Equal(childCount + 4L, actual.Lines);
		Assert.Equal((expectedCharacters + 3) / 4, actual.Tokens);
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
