namespace DevProjex.Tests.Unit;

public sealed class ProjectExportServiceStructuredFormatTests
{
	[Theory]
	[InlineData(TreeTextFormat.Xml)]
	[InlineData(TreeTextFormat.Markdown)]
	public async Task BuildAsync_TreeMode_UsesStructuredTreeFormat(TreeTextFormat format)
	{
		using var temp = new TemporaryDirectory();
		var project = CreateProject(temp);
		var service = CreateService();

		var result = await service.BuildAsync(
			project,
			new StartupExportOptions(
				Enabled: true,
				Mode: StartupExportMode.Tree,
				Path: Path.Combine(temp.Path, $"tree.{GetExtension(format)}"),
				Format: format),
			TestContext.Current.CancellationToken);

		AssertStructuredTree(result, format, temp.Path);
		Assert.DoesNotContain("class Program", result, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(TreeTextFormat.Xml)]
	[InlineData(TreeTextFormat.Markdown)]
	public async Task BuildAsync_TreeContentMode_UsesStructuredTreeFormatAndPlainTextContent(TreeTextFormat format)
	{
		using var temp = new TemporaryDirectory();
		var project = CreateProject(temp);
		var service = CreateService();

		var result = await service.BuildAsync(
			project,
			new StartupExportOptions(
				Enabled: true,
				Mode: StartupExportMode.TreeContent,
				Path: Path.Combine(temp.Path, $"context.{GetExtension(format)}"),
				Format: format),
			TestContext.Current.CancellationToken);

		var (treePart, contentPart) = SplitTreeAndContent(result);
		AssertStructuredTree(treePart, format, temp.Path);
		Assert.Contains("src/Program.cs:", contentPart, StringComparison.Ordinal);
		Assert.Contains("README.md:", contentPart, StringComparison.Ordinal);
		Assert.Contains("class Program", contentPart, StringComparison.Ordinal);
		Assert.Contains("# Readme", contentPart, StringComparison.Ordinal);
		Assert.DoesNotContain(temp.Path.Replace('\\', '/'), contentPart.Replace('\\', '/'), StringComparison.Ordinal);
	}

	private static LoadedProjectAnalysisRequest CreateProject(TemporaryDirectory temp)
	{
		var srcPath = Directory.CreateDirectory(Path.Combine(temp.Path, "src")).FullName;
		var programPath = temp.CreateFile(Path.Combine("src", "Program.cs"), "class Program {}");
		var readmePath = temp.CreateFile("README.md", "# Readme");
		var root = new TreeNodeDescriptor(
			"Project",
			temp.Path,
			true,
			false,
			"folder",
			[
				new("src", srcPath, true, false, "folder",
				[
					new("Program.cs", programPath, false, false, "csharp", [])
				]),
				new("README.md", readmePath, false, false, "markdown", [])
			]);

		return new LoadedProjectAnalysisRequest(
			RootPath: temp.Path,
			Tree: new BuildTreeResult(root, RootAccessDenied: false, HadAccessDenied: false, OrderedFilePaths: [programPath, readmePath]),
			AvailableRootFolders: ["src"],
			AvailableExtensions: [".cs", ".md"],
			SelectedRootFolders: ["src"],
			SelectedExtensions: [".cs", ".md"],
			SelectedIgnoreOptions: [],
			RootAccessDenied: false,
			HadAccessDenied: false);
	}

	private static ProjectExportService CreateService()
	{
		var treeExport = new TreeExportService();
		var contentExport = new SelectedContentExportService(new FileContentAnalyzer());
		return new ProjectExportService(
			treeExport,
			contentExport,
			new TreeAndContentExportService(treeExport, contentExport));
	}

	private static void AssertStructuredTree(string tree, TreeTextFormat format, string rootPath)
	{
		if (format == TreeTextFormat.Xml)
		{
			var document = XmlTreeExportTestHelper.Parse(tree);
			XmlTreeExportTestHelper.AssertRootPath(document, JsonTreeExportTestHelper.NormalizeJsonPath(rootPath));
			Assert.Equal(["README.md", "src/Program.cs"], SortPaths(XmlTreeExportTestHelper.ExtractFilePaths(document)));
			return;
		}

		MarkdownTreeExportTestHelper.AssertMarkdownTreeContract(
			tree,
			JsonTreeExportTestHelper.NormalizeJsonPath(rootPath));
		Assert.Equal(["README.md", "src/Program.cs"], SortPaths(MarkdownTreeExportTestHelper.ExtractFilePaths(tree)));
	}

	private static (string TreePart, string ContentPart) SplitTreeAndContent(string export)
	{
		var separatorIndex = export.IndexOf('\u00A0');
		Assert.True(separatorIndex > 0, "Structured tree-content export must contain the NBSP separator.");
		return (export[..separatorIndex].TrimEnd('\r', '\n'), export[separatorIndex..]);
	}

	private static string GetExtension(TreeTextFormat format)
		=> format == TreeTextFormat.Xml ? "xml" : "md";

	private static string[] SortPaths(IEnumerable<string> paths)
		=> paths.OrderBy(static path => path, StringComparer.Ordinal).ToArray();
}
