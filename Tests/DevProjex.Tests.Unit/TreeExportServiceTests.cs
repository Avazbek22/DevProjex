namespace DevProjex.Tests.Unit;

public sealed class TreeExportServiceTests
{
	// Verifies the full tree export renders ASCII output with the root and children.
	[Fact]
	public void BuildFullTree_ReturnsAsciiTree()
	{
		var root = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: "/root",
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>
			{
				new TreeNodeDescriptor("file.txt", "/root/file.txt", false, false, "text", new List<TreeNodeDescriptor>())
			});

		var service = new TreeExportService();
		var result = service.BuildFullTree("/root", root);

		Assert.Contains("/root:", result);
		Assert.Contains("└── file.txt", result);
	}

	// Verifies selected tree export only includes selected paths.
	[Fact]
	public void BuildSelectedTree_ReturnsOnlySelectedPaths()
	{
		var root = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: "/root",
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>
			{
				new TreeNodeDescriptor("keep.txt", "/root/keep.txt", false, false, "text", new List<TreeNodeDescriptor>()),
				new TreeNodeDescriptor("skip.txt", "/root/skip.txt", false, false, "text", new List<TreeNodeDescriptor>())
			});

		var service = new TreeExportService();
		var selected = new HashSet<string> { "/root/keep.txt" };
		var result = service.BuildSelectedTree("/root", root, selected);

		Assert.Contains("keep.txt", result);
		Assert.DoesNotContain("skip.txt", result);
	}

	// Verifies selected tree export returns empty when nothing is selected.
	[Fact]
	public void BuildSelectedTree_ReturnsEmptyWhenNoSelections()
	{
		var root = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: "/root",
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>
			{
				new TreeNodeDescriptor("file.txt", "/root/file.txt", false, false, "text", new List<TreeNodeDescriptor>())
			});

		var service = new TreeExportService();

		var result = service.BuildSelectedTree("/root", root, new HashSet<string>());

		Assert.Equal(string.Empty, result);
	}

	// Verifies selection matching returns true for a descendant.
	[Fact]
	public void HasSelectedDescendantOrSelf_ReturnsTrueWhenMatch()
	{
		var node = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: "/root",
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>
			{
				new TreeNodeDescriptor("child", "/root/child", false, false, "text", new List<TreeNodeDescriptor>())
			});

		var selected = new HashSet<string> { "/root/child" };

		Assert.True(TreeExportService.HasSelectedDescendantOrSelf(node, selected));
	}

	// Verifies selection matching returns false when nothing is selected.
	[Fact]
	public void HasSelectedDescendantOrSelf_ReturnsFalseWhenNoMatch()
	{
		var node = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: "/root",
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>());

		var selected = new HashSet<string>();

		Assert.False(TreeExportService.HasSelectedDescendantOrSelf(node, selected));
	}

	// Verifies the full tree export renders nested folder structure.
	[Fact]
	public void BuildFullTree_RendersNestedDirectories()
	{
		var root = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: "/root",
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>
			{
				new TreeNodeDescriptor(
					"src",
					"/root/src",
					true,
					false,
					"folder",
					new List<TreeNodeDescriptor>
					{
						new TreeNodeDescriptor("main.cs", "/root/src/main.cs", false, false, "csharp", new List<TreeNodeDescriptor>())
					})
			});

		var service = new TreeExportService();
		var result = service.BuildFullTree("/root", root);

		Assert.Contains("└── src", result);
		Assert.Contains("│       └── main.cs", result);
	}

	// Verifies selected tree includes ancestor directories for selected descendants.
	[Fact]
	public void BuildSelectedTree_IncludesAncestorDirectories()
	{
		var root = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: "/root",
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>
			{
				new TreeNodeDescriptor(
					"src",
					"/root/src",
					true,
					false,
					"folder",
					new List<TreeNodeDescriptor>
					{
						new TreeNodeDescriptor("main.cs", "/root/src/main.cs", false, false, "csharp", new List<TreeNodeDescriptor>())
					})
			});

		var service = new TreeExportService();
		var selected = new HashSet<string> { "/root/src/main.cs" };
		var result = service.BuildSelectedTree("/root", root, selected);

		Assert.Contains("src", result);
		Assert.Contains("main.cs", result);
	}

	// Verifies selecting an empty directory still renders the subtree entry even when
	// content metrics would legitimately remain zero for that selection.
	[Fact]
	public void BuildSelectedTree_EmptyDirectorySelection_RendersDirectoryEntry()
	{
		var root = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: "/root",
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>
			{
				new TreeNodeDescriptor("empty", "/root/empty", true, false, "folder", new List<TreeNodeDescriptor>()),
				new TreeNodeDescriptor("readme.md", "/root/readme.md", false, false, "text", new List<TreeNodeDescriptor>())
			});

		var service = new TreeExportService();
		var selected = new HashSet<string> { "/root/empty" };
		var result = service.BuildSelectedTree("/root", root, selected);

		Assert.Contains("empty", result);
		Assert.DoesNotContain("readme.md", result);
	}

	// Verifies selecting the root includes the full subtree.
	[Fact]
	public void BuildSelectedTree_ReturnsFullSubtreeWhenRootSelected()
	{
		var root = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: "/root",
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>
			{
				new TreeNodeDescriptor("child", "/root/child", false, false, "text", new List<TreeNodeDescriptor>())
			});

		var service = new TreeExportService();
		var selected = new HashSet<string> { "/root" };
		var result = service.BuildSelectedTree("/root", root, selected);

		Assert.Contains("root", result);
		Assert.Contains("child", result);
	}

	// Verifies selecting both root and child includes the child output.
	[Fact]
	public void BuildSelectedTree_IncludesChildWhenRootAndChildSelected()
	{
		var root = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: "/root",
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>
			{
				new TreeNodeDescriptor("child", "/root/child", false, false, "text", new List<TreeNodeDescriptor>())
			});

		var service = new TreeExportService();
		var selected = new HashSet<string> { "/root", "/root/child" };
		var result = service.BuildSelectedTree("/root", root, selected);

		Assert.Contains("child", result);
	}

	// Verifies selected tree retains nested indentation for descendants.
	[Fact]
	public void BuildSelectedTree_RendersNestedIndentForDescendant()
	{
		var root = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: "/root",
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>
			{
				new TreeNodeDescriptor(
					"src",
					"/root/src",
					true,
					false,
					"folder",
					new List<TreeNodeDescriptor>
					{
						new TreeNodeDescriptor("main.cs", "/root/src/main.cs", false, false, "csharp", new List<TreeNodeDescriptor>())
					})
			});

		var service = new TreeExportService();
		var selected = new HashSet<string> { "/root/src/main.cs" };
		var result = service.BuildSelectedTree("/root", root, selected);

		Assert.Contains("└── src", result);
		Assert.Contains("│       └── main.cs", result);
	}

	// Verifies full tree output handles root with no children.
	[Fact]
	public void BuildFullTree_ReturnsRootOnlyWhenNoChildren()
	{
		var root = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: "/root",
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>());

		var service = new TreeExportService();
		var result = service.BuildFullTree("/root", root);

		Assert.Contains("├── root", result);
	}

	// Verifies selection matching returns true when the node itself is selected.
	[Fact]
	public void HasSelectedDescendantOrSelf_ReturnsTrueWhenSelfSelected()
	{
		var node = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: "/root",
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>());

		var selected = new HashSet<string> { "/root" };

		Assert.True(TreeExportService.HasSelectedDescendantOrSelf(node, selected));
	}

	// Verifies JSON export writes the compact Fable-style tree contract.
	[Fact]
	public void BuildFullTree_JsonFormat_ReturnsValidJson()
	{
		var root = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: "/root",
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>
			{
				new TreeNodeDescriptor("src", "/root/src", true, false, "folder", new List<TreeNodeDescriptor>
				{
					new TreeNodeDescriptor("main.cs", "/root/src/main.cs", false, false, "csharp", new List<TreeNodeDescriptor>())
				})
			});

		var service = new TreeExportService();
		var result = service.BuildFullTree("/root", root, TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(result);
		Assert.Equal(Path.GetFullPath("/root").Replace('\\', '/'), doc.RootElement.GetProperty("rootPath").GetString());
		var tree = JsonTreeExportTestHelper.GetTree(doc);
		Assert.Equal(JsonValueKind.Array, tree.GetProperty("src").ValueKind);
		Assert.Equal(["src/main.cs"], JsonTreeExportTestHelper.ExtractFilePaths(tree));
	}

	// Verifies JSON selected export keeps only selected branch with ancestors.
	[Fact]
	public void BuildSelectedTree_JsonFormat_ReturnsFilteredTree()
	{
		var root = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: "/root",
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>
			{
				new TreeNodeDescriptor("keep.txt", "/root/keep.txt", false, false, "text", new List<TreeNodeDescriptor>()),
				new TreeNodeDescriptor("skip.txt", "/root/skip.txt", false, false, "text", new List<TreeNodeDescriptor>())
			});

		var service = new TreeExportService();
		var selected = new HashSet<string> { "/root/keep.txt" };
		var result = service.BuildSelectedTree("/root", root, selected, TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(result);
		var tree = JsonTreeExportTestHelper.GetTree(doc);
		Assert.Equal(["keep.txt"], JsonTreeExportTestHelper.ExtractFilePaths(tree));
	}
}
