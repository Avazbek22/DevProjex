namespace DevProjex.Tests.Unit;

public sealed class TreeExportServiceCrossPlatformTests
{
	[Fact]
	public void BuildFullTree_Json_UsesAbsoluteRootPath()
	{
		var service = new TreeExportService();
		var rootPath = Path.Combine("..", "tmp", "repo");
		var fullRootPath = Path.GetFullPath(rootPath);
		var root = CreateRoot(fullRootPath);

		var json = service.BuildFullTree(rootPath, root, TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(json);
		Assert.Equal(fullRootPath.Replace('\\', '/'), doc.RootElement.GetProperty("rootPath").GetString());
	}

	[Fact]
	public void BuildFullTree_Json_UsesForwardSlashRelativeKeys()
	{
		var service = new TreeExportService();
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjex", "TreeExportCross");
		var srcPath = Path.Combine(rootPath, "src");
		var filePath = Path.Combine(srcPath, "main.cs");
		var root = CreateRoot(rootPath, srcPath, filePath);

		var json = service.BuildFullTree(rootPath, root, TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(json);
		var paths = JsonTreeExportTestHelper.ExtractFilePaths(JsonTreeExportTestHelper.GetTree(doc));
		Assert.Equal(["src/main.cs"], paths);
	}

	[Fact]
	public void BuildFullTree_Json_DoesNotExposeScannerMetadata()
	{
		var service = new TreeExportService();
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjex", "TreeExportCrossDenied");
		var root = CreateRoot(rootPath);

		var json = service.BuildFullTree(rootPath, root, TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(json);
		JsonTreeExportTestHelper.AssertNoLegacyTreeContract(doc.RootElement);
	}

	[Fact]
	public void BuildSelectedTree_Json_ReturnsEmptyWhenSelectionOutsideTree()
	{
		var service = new TreeExportService();
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjex", "TreeExportCrossSelected");
		var root = CreateRoot(rootPath);
		var selected = new HashSet<string> { Path.Combine(rootPath, "missing.txt") };

		var json = service.BuildSelectedTree(rootPath, root, selected, TreeTextFormat.Json);

		Assert.Equal(string.Empty, json);
	}

	[Fact]
	public void BuildFullTree_Ascii_UsesEnvironmentNewLine()
	{
		var service = new TreeExportService();
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjex", "TreeExportCrossAscii");
		var root = CreateRoot(rootPath);

		var ascii = service.BuildFullTree(rootPath, root, TreeTextFormat.Ascii);

		Assert.Contains(Environment.NewLine, ascii);
		Assert.DoesNotContain("├── Root", ascii, StringComparison.Ordinal);
		Assert.Equal(
			$"{SingleLineTextEscaping.Escape(rootPath)}:{Environment.NewLine}",
			ascii);
	}

	[Fact]
	public void BuildSelectedTree_Ascii_SelectedDirectoryIncludesDescendantFiles()
	{
		var service = new TreeExportService();
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjex", "TreeExportCrossDirectorySelection");
		var srcPath = Path.Combine(rootPath, "src");
		var filePath = Path.Combine(srcPath, "main.cs");
		var root = CreateRoot(rootPath, srcPath, filePath);
		var selected = new HashSet<string> { srcPath };

		var ascii = service.BuildSelectedTree(rootPath, root, selected, TreeTextFormat.Ascii);

		Assert.Contains("src", ascii);
		Assert.Contains("main.cs", ascii);
	}

	[Fact]
	public void BuildFullTree_Json_TreeContainsRootContentsOnly()
	{
		var service = new TreeExportService();
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjex", "TreeExportRootDot");
		var root = CreateRoot(rootPath);

		var json = service.BuildFullTree(rootPath, root, TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(json);
		JsonTreeExportTestHelper.AssertOnlyRootPathAndTree(doc.RootElement);
		Assert.Empty(JsonTreeExportTestHelper.GetTree(doc).EnumerateObject());
	}

	[Fact]
	public void BuildSelectedTree_Json_RootSelectionReturnsFullSubtree()
	{
		var service = new TreeExportService();
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjex", "TreeExportRootOnly");
		var srcPath = Path.Combine(rootPath, "src");
		var filePath = Path.Combine(srcPath, "main.cs");
		var root = CreateRoot(rootPath, srcPath, filePath);
		var selected = new HashSet<string> { rootPath };

		var json = service.BuildSelectedTree(rootPath, root, selected, TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(json);
		var tree = JsonTreeExportTestHelper.GetTree(doc);
		Assert.Equal(JsonValueKind.Array, tree.GetProperty("src").ValueKind);
		Assert.Equal(["src/main.cs"], JsonTreeExportTestHelper.ExtractFilePaths(tree));
	}

	private static TreeNodeDescriptor CreateRoot(string rootPath, string? srcPath = null, string? filePath = null)
	{
		var children = new List<TreeNodeDescriptor>();
		if (!string.IsNullOrWhiteSpace(srcPath) && !string.IsNullOrWhiteSpace(filePath))
		{
			children.Add(new TreeNodeDescriptor(
				"src",
				srcPath,
				true,
				false,
				"folder",
				new List<TreeNodeDescriptor>
				{
					new("main.cs", filePath, false, false, "csharp", new List<TreeNodeDescriptor>())
				}));
		}

		return new TreeNodeDescriptor(
			"Root",
			rootPath,
			true,
			false,
			"folder",
			children);
	}
}
