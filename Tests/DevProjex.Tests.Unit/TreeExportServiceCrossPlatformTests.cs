using System.Text.Json;
using DevProjex.Application.Services;

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
		Assert.Equal(fullRootPath, doc.RootElement.GetProperty("rootPath").GetString());
	}

	[Fact]
	public void BuildFullTree_Json_NormalizesChildPathsToForwardSlash()
	{
		var service = new TreeExportService();
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjex", "TreeExportCross");
		var srcPath = Path.Combine(rootPath, "src");
		var filePath = Path.Combine(srcPath, "main.cs");
		var root = CreateRoot(rootPath, srcPath, filePath);

		var json = service.BuildFullTree(rootPath, root, TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(json);
		var src = doc.RootElement.GetProperty("root").GetProperty("dirs")[0];
		Assert.Equal("src", src.GetProperty("path").GetString());
	}

	[Fact]
	public void BuildFullTree_Json_OmitsAccessDeniedWhenFalse()
	{
		var service = new TreeExportService();
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjex", "TreeExportCrossDenied");
		var root = CreateRoot(rootPath);

		var json = service.BuildFullTree(rootPath, root, TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(json);
		var rootElement = doc.RootElement.GetProperty("root");
		Assert.False(rootElement.TryGetProperty("accessDenied", out _));
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
		Assert.Contains("├── Root", ascii);
	}

	[Fact]
	public void BuildSelectedTree_Ascii_SelectedDirectoryIncludesDirectoryWithoutUnselectedFiles()
	{
		var service = new TreeExportService();
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjex", "TreeExportCrossDirectorySelection");
		var srcPath = Path.Combine(rootPath, "src");
		var filePath = Path.Combine(srcPath, "main.cs");
		var root = CreateRoot(rootPath, srcPath, filePath);
		var selected = new HashSet<string> { srcPath };

		var ascii = service.BuildSelectedTree(rootPath, root, selected, TreeTextFormat.Ascii);

		Assert.Contains("src", ascii);
		Assert.DoesNotContain("main.cs", ascii);
	}

	[Fact]
	public void BuildFullTree_Json_RootNodePathIsDot()
	{
		var service = new TreeExportService();
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjex", "TreeExportRootDot");
		var root = CreateRoot(rootPath);

		var json = service.BuildFullTree(rootPath, root, TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(json);
		Assert.Equal(".", doc.RootElement.GetProperty("root").GetProperty("path").GetString());
	}

	[Fact]
	public void BuildSelectedTree_Json_RootSelectionReturnsRootOnly()
	{
		var service = new TreeExportService();
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjex", "TreeExportRootOnly");
		var srcPath = Path.Combine(rootPath, "src");
		var filePath = Path.Combine(srcPath, "main.cs");
		var root = CreateRoot(rootPath, srcPath, filePath);
		var selected = new HashSet<string> { rootPath };

		var json = service.BuildSelectedTree(rootPath, root, selected, TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(json);
		var rootElement = doc.RootElement.GetProperty("root");
		Assert.Equal(".", rootElement.GetProperty("path").GetString());
		Assert.False(rootElement.TryGetProperty("dirs", out _));
		Assert.False(rootElement.TryGetProperty("files", out _));
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
