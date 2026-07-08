namespace DevProjex.Tests.Unit;

public sealed class TreeExportServiceMarkdownTests
{
	[Fact]
	public void BuildFullTree_MarkdownFormat_WritesContractAndRoundTripsMixedTree()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();

		var result = service.BuildFullTree(fixture.RootPath, fixture.Root, TreeTextFormat.Markdown);

		MarkdownTreeExportTestHelper.AssertMarkdownTreeContract(
			result,
			JsonTreeExportTestHelper.NormalizeJsonPath(fixture.RootPath));
		Assert.Contains("- EmptyFolder/", result, StringComparison.Ordinal);
		Assert.Contains("  - Services/", result, StringComparison.Ordinal);
		Assert.Equal(
			SortPaths(
			[
				"EmptyFolder",
				"Folder/File.cs",
				"src/Services/UserService.cs",
				"src/Program.cs",
				"global.json",
				"README.md"
			]),
			SortPaths(MarkdownTreeExportTestHelper.ExtractFilePaths(result)
				.Concat(MarkdownTreeExportTestHelper.ExtractEmptyFolderPaths(result))));
	}

	[Fact]
	public void BuildFullTree_MarkdownFormat_EscapesLeadingListMarkersWithoutLosingNames()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexMarkdownSpecialNames");
		var root = DirectoryNode("Project", rootPath,
		[
			DirectoryNode("-scripts", Path.Combine(rootPath, "-scripts"),
			[
				FileNode("-build.ps1", Path.Combine(rootPath, "-scripts", "-build.ps1")),
				FileNode("Dockerfile", Path.Combine(rootPath, "-scripts", "Dockerfile")),
				FileNode("file.name.with.dots.cs", Path.Combine(rootPath, "-scripts", "file.name.with.dots.cs"))
			]),
			DirectoryNode("Документы", Path.Combine(rootPath, "docs"),
			[
				FileNode("Файл.cs", Path.Combine(rootPath, "docs", "file.cs")),
				FileNode("My File.cs", Path.Combine(rootPath, "docs", "My File.cs"))
			])
		]);
		var service = new TreeExportService();

		var result = service.BuildFullTree(rootPath, root, TreeTextFormat.Markdown);

		MarkdownTreeExportTestHelper.AssertMarkdownTreeContract(
			result,
			JsonTreeExportTestHelper.NormalizeJsonPath(rootPath));
		Assert.Contains("- \\-scripts/", result, StringComparison.Ordinal);
		Assert.Contains("  - \\-build.ps1", result, StringComparison.Ordinal);
		Assert.Contains("-scripts/-build.ps1", MarkdownTreeExportTestHelper.ExtractFilePaths(result));
		Assert.Contains("Документы/Файл.cs", MarkdownTreeExportTestHelper.ExtractFilePaths(result));
	}

	[Fact]
	public void BuildSelectedTree_MarkdownFormat_FiltersSelectionAndKeepsSelectedEmptyFolder()
	{
		var fixture = CreateFixture();
		var selected = new HashSet<string>(PathComparer.Default)
		{
			Path.Combine(fixture.RootPath, "src", "Services", "UserService.cs"),
			Path.Combine(fixture.RootPath, "EmptyFolder")
		};
		var service = new TreeExportService();

		var result = service.BuildSelectedTree(fixture.RootPath, fixture.Root, selected, TreeTextFormat.Markdown);

		Assert.Equal(
			SortPaths(["EmptyFolder", "src/Services/UserService.cs"]),
			SortPaths(MarkdownTreeExportTestHelper.ExtractEmptyFolderPaths(result)
				.Concat(MarkdownTreeExportTestHelper.ExtractFilePaths(result))));
		Assert.DoesNotContain("Program.cs", result, StringComparison.Ordinal);
		Assert.DoesNotContain("README.md", result, StringComparison.Ordinal);
	}

	[Fact]
	public void BuildFullTree_MarkdownFormat_UsesDisplayRootPathForGitPresentation()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();

		var result = service.BuildFullTree(
			fixture.RootPath,
			fixture.Root,
			TreeTextFormat.Markdown,
			displayRootPath: "https://github.com/Avazbek22/DevProjex",
			displayRootName: "DevProjex");

		Assert.StartsWith("Root: https://github.com/Avazbek22/DevProjex", result, StringComparison.Ordinal);
		Assert.DoesNotContain(JsonTreeExportTestHelper.NormalizeJsonPath(fixture.RootPath), result, StringComparison.Ordinal);
	}

	[Fact]
	public void BuildFullTree_MarkdownFormat_RepeatedExportIsStableForLargeDeepTree()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexMarkdownLargeTree");
		var deep = FileNode("leaf.txt", Path.Combine(rootPath, "deep", "leaf.txt"));
		for (var level = 49; level >= 0; level--)
			deep = DirectoryNode($"level{level:D2}", Path.Combine(rootPath, $"level{level:D2}"), [deep]);

		var wideChildren = new List<TreeNodeDescriptor> { deep };
		for (var folder = 0; folder < 100; folder++)
		{
			var files = Enumerable.Range(0, 10)
				.Select(file => FileNode($"file{file:D2}.txt", Path.Combine(rootPath, $"folder{folder:D3}", $"file{file:D2}.txt")))
				.ToArray();
			wideChildren.Add(DirectoryNode($"folder{folder:D3}", Path.Combine(rootPath, $"folder{folder:D3}"), files));
		}

		var root = DirectoryNode("Project", rootPath, wideChildren);
		var service = new TreeExportService();

		var first = service.BuildFullTree(rootPath, root, TreeTextFormat.Markdown);
		var second = service.BuildFullTree(rootPath, root, TreeTextFormat.Markdown);

		Assert.Equal(first, second);
		MarkdownTreeExportTestHelper.AssertMarkdownTreeContract(
			first,
			JsonTreeExportTestHelper.NormalizeJsonPath(rootPath));
		Assert.Equal(1001, MarkdownTreeExportTestHelper.ExtractFilePaths(first).Length);
		Assert.Contains(
			string.Join('/', Enumerable.Range(0, 50).Select(static level => $"level{level:D2}")) + "/leaf.txt",
			MarkdownTreeExportTestHelper.ExtractFilePaths(first));
	}

	private static ExportFixture CreateFixture()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexMarkdownFixture");
		var root = DirectoryNode("Project", rootPath,
		[
			FileNode("README.md", Path.Combine(rootPath, "README.md")),
			FileNode("global.json", Path.Combine(rootPath, "global.json")),
			DirectoryNode("Folder", Path.Combine(rootPath, "Folder"),
			[
				FileNode("File.cs", Path.Combine(rootPath, "Folder", "File.cs"))
			]),
			DirectoryNode("EmptyFolder", Path.Combine(rootPath, "EmptyFolder"), []),
			DirectoryNode("src", Path.Combine(rootPath, "src"),
			[
				FileNode("Program.cs", Path.Combine(rootPath, "src", "Program.cs")),
				DirectoryNode("Services", Path.Combine(rootPath, "src", "Services"),
				[
					FileNode("UserService.cs", Path.Combine(rootPath, "src", "Services", "UserService.cs"))
				])
			])
		]);

		return new ExportFixture(rootPath, root);
	}

	private static TreeNodeDescriptor DirectoryNode(
		string name,
		string fullPath,
		IReadOnlyList<TreeNodeDescriptor> children)
		=> new(name, fullPath, true, false, "folder", children);

	private static TreeNodeDescriptor FileNode(string name, string fullPath)
		=> new(name, fullPath, false, false, "file", []);

	private static string[] SortPaths(IEnumerable<string> paths)
		=> paths.OrderBy(static path => path, StringComparer.Ordinal).ToArray();

	private sealed record ExportFixture(string RootPath, TreeNodeDescriptor Root);
}
