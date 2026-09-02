namespace DevProjex.Tests.Unit;

public sealed class TreeExportServiceMetricsTests
{
	public static IEnumerable<object[]> FullTreeMetricCases()
	{
		for (var rootIsFile = 0; rootIsFile <= 1; rootIsFile++)
		{
			for (var includeRootPath = 0; includeRootPath <= 1; includeRootPath++)
			{
				yield return [rootIsFile != 0, includeRootPath != 0, TreeTextFormat.Ascii, false];
				yield return [rootIsFile != 0, includeRootPath != 0, TreeTextFormat.Ascii, true];
				yield return [rootIsFile != 0, includeRootPath != 0, TreeTextFormat.Markdown, false];
				yield return [rootIsFile != 0, includeRootPath != 0, TreeTextFormat.Json, false];
				yield return [rootIsFile != 0, includeRootPath != 0, TreeTextFormat.Xml, false];
			}
		}
	}

	[Theory]
	[MemberData(nameof(FullTreeMetricCases))]
	public async Task FullTreeMetricsMatchBufferedAndStreamingRenderers(
		bool rootIsFile,
		bool includeRootPath,
		TreeTextFormat format,
		bool plain)
	{
		var service = new TreeExportService();
		var rootPath = Path.Combine(Path.GetTempPath(), "tree-metrics-literal-root");
		var file = new TreeNodeDescriptor(
			"![file](https://attacker.test/file.png)",
			Path.Combine(rootPath, "file.md"),
			false,
			false,
			"markdown",
			[]);
		var root = rootIsFile
			? file
			: new TreeNodeDescriptor(
				"![root](https://attacker.test/root.png)",
				rootPath,
				true,
				false,
				"folder",
				[file]);
		var actualRootPath = rootIsFile ? file.FullPath : rootPath;
		const string displayRootPath = "https://example.test/[repo]/<tag>/&copy;";
		const string displayRootName = "![named](https://attacker.test/root.png)";
		var buffered = plain
			? service.BuildFullTreePlain(
				actualRootPath,
				root,
				displayRootPath,
				displayRootName,
				includeRootPath)
			: service.BuildFullTree(
				actualRootPath,
				root,
				format,
				displayRootPath,
				displayRootName,
				includeRootPath);
		using var destination = new StringWriter(CultureInfo.InvariantCulture);
		if (plain)
		{
			await service.WriteFullTreePlainAsync(
				destination,
				actualRootPath,
				root,
				displayRootPath,
				displayRootName,
				includeRootPath,
				cancellationToken: TestContext.Current.CancellationToken);
		}
		else
		{
			await service.WriteFullTreeAsync(
				destination,
				actualRootPath,
				root,
				format,
				displayRootPath,
				displayRootName,
				includeRootPath,
				TestContext.Current.CancellationToken);
		}

		var metrics = service.CalculateFullTreeMetrics(
			actualRootPath,
			root,
			format,
			displayRootPath,
			displayRootName,
			includeRootPath);

		Assert.Equal(buffered, destination.ToString());
		Assert.Equal(ExportOutputMetricsCalculator.FromText(buffered), metrics);
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public void MarkdownMetricsMatchRendererForRootShapeAndSelection(
		bool rootIsFile,
		bool selected)
	{
		var service = new TreeExportService();
		const string rootPath = "/repo_<tag>`tick`";
		var root = rootIsFile
			? new TreeNodeDescriptor("root.cs", rootPath, false, false, "csharp", [])
			: new TreeNodeDescriptor(
				"repo",
				rootPath,
				true,
				false,
				"folder",
				[new TreeNodeDescriptor("root.cs", rootPath + "/root.cs", false, false, "csharp", [])]);
		var selection = new HashSet<string>(PathComparer.Default) { rootPath };

		var rendered = selected
			? service.BuildSelectedTree(rootPath, root, selection, TreeTextFormat.Markdown)
			: service.BuildFullTree(rootPath, root, TreeTextFormat.Markdown);
		var metrics = selected
			? service.CalculateSelectedTreeMetrics(rootPath, root, selection, TreeTextFormat.Markdown)
			: service.CalculateFullTreeMetrics(rootPath, root, TreeTextFormat.Markdown);

		Assert.Equal(ExportOutputMetricsCalculator.FromText(rendered), metrics);
	}

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
		var expectedCharacters = 7L + childCount * (displayName.Length + 5L);

		var actual = new TreeExportService().CalculateFullTreeMetrics(
			"/root",
			root,
			TreeTextFormat.Ascii);

		Assert.True(actual.Chars > int.MaxValue);
		Assert.Equal(expectedCharacters, actual.Chars);
		Assert.Equal(childCount + 2L, actual.Lines);
		Assert.Equal((expectedCharacters + 3) / 4, actual.Tokens);
	}

	[Fact]
	public void CalculateFullTreeMetricsWithCancellation_StopsDuringTraversal()
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
			new TreeExportService().CalculateFullTreeMetricsWithCancellation(
				"/root",
				root,
				TreeTextFormat.Ascii,
				displayRootPath: null,
				displayRootName: null,
				cancellation.Token));
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

	private sealed class CancelOnReadList<T>(
		IReadOnlyList<T> values,
		CancellationTokenSource cancellation) : IReadOnlyList<T>
	{
		public int Count => values.Count;

		public T this[int index]
		{
			get
			{
				var value = values[index];
				cancellation.Cancel();
				return value;
			}
		}

		public IEnumerator<T> GetEnumerator() => values.GetEnumerator();

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}
}
