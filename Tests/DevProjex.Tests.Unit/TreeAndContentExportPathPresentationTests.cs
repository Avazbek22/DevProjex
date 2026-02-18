using DevProjex.Application.Services;
using DevProjex.Tests.Unit.Helpers;

namespace DevProjex.Tests.Unit;

public sealed class TreeAndContentExportPathPresentationTests
{
	[Fact]
	public async Task BuildAsync_UsesPathPresentationForTreeAndContentBlocks()
	{
		using var temp = new TemporaryDirectory();
		var filePath = temp.CreateFile("src/main.cs", "class Program {}");

		var root = new TreeNodeDescriptor(
			DisplayName: "repo",
			FullPath: temp.Path,
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children:
			[
				new TreeNodeDescriptor(
					DisplayName: "src",
					FullPath: Path.Combine(temp.Path, "src"),
					IsDirectory: true,
					IsAccessDenied: false,
					IconKey: "folder",
					Children:
					[
						new TreeNodeDescriptor(
							DisplayName: "main.cs",
							FullPath: filePath,
							IsDirectory: false,
							IsAccessDenied: false,
							IconKey: "csharp",
							Children: [])
					])
			]);

		var treeExport = new TreeExportService();
		var contentExport = new SelectedContentExportService(new FileContentAnalyzer());
		var service = new TreeAndContentExportService(treeExport, contentExport);
		var presentation = new ExportPathPresentation(
			displayRootPath: "https://github.com/user/repo",
			mapFilePath: _ => "https://github.com/user/repo/src/main.cs",
			displayRootName: "DevProjex");

		var result = await service.BuildAsync(
			temp.Path,
			root,
			new HashSet<string>(PathComparer.Default),
			TreeTextFormat.Ascii,
			CancellationToken.None,
			presentation);

		Assert.Contains("https://github.com/user/repo:", result, StringComparison.Ordinal);
		Assert.Contains("├── DevProjex", result, StringComparison.Ordinal);
		Assert.Contains("https://github.com/user/repo/src/main.cs:", result, StringComparison.Ordinal);
		Assert.DoesNotContain($"{temp.Path}:", result, StringComparison.Ordinal);
	}

	[Fact]
	public async Task BuildAsync_WithJsonFormat_UsesDisplayRootNameInTreeBlock()
	{
		using var temp = new TemporaryDirectory();
		var filePath = temp.CreateFile("src/main.cs", "class Program {}");

		var root = new TreeNodeDescriptor(
			DisplayName: "repo-hash",
			FullPath: temp.Path,
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children:
			[
				new TreeNodeDescriptor(
					DisplayName: "src",
					FullPath: Path.Combine(temp.Path, "src"),
					IsDirectory: true,
					IsAccessDenied: false,
					IconKey: "folder",
					Children:
					[
						new TreeNodeDescriptor(
							DisplayName: "main.cs",
							FullPath: filePath,
							IsDirectory: false,
							IsAccessDenied: false,
							IconKey: "csharp",
							Children: [])
					])
			]);

		var treeExport = new TreeExportService();
		var contentExport = new SelectedContentExportService(new FileContentAnalyzer());
		var service = new TreeAndContentExportService(treeExport, contentExport);
		var presentation = new ExportPathPresentation(
			displayRootPath: "https://github.com/user/repo",
			mapFilePath: _ => "https://github.com/user/repo/src/main.cs",
			displayRootName: "DevProjex");

		var result = await service.BuildAsync(
			temp.Path,
			root,
			new HashSet<string>(PathComparer.Default),
			TreeTextFormat.Json,
			CancellationToken.None,
			presentation);

		var separatorIndex = result.IndexOf('\u00A0');
		Assert.True(separatorIndex > 0);
		var jsonPart = result[..separatorIndex].TrimEnd('\r', '\n');
		using var doc = System.Text.Json.JsonDocument.Parse(jsonPart);
		Assert.Equal("DevProjex", doc.RootElement.GetProperty("root").GetProperty("name").GetString());
		Assert.Contains("https://github.com/user/repo/src/main.cs:", result, StringComparison.Ordinal);
	}

	[Fact]
	public async Task BuildAsync_WithInvalidSelectionFallback_StillUsesDisplayRootName()
	{
		using var temp = new TemporaryDirectory();
		var filePath = temp.CreateFile("a.txt", "content");

		var root = new TreeNodeDescriptor(
			DisplayName: "repo-hash",
			FullPath: temp.Path,
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children:
			[
				new TreeNodeDescriptor(
					DisplayName: "a.txt",
					FullPath: filePath,
					IsDirectory: false,
					IsAccessDenied: false,
					IconKey: "text",
					Children: [])
			]);

		var service = new TreeAndContentExportService(
			new TreeExportService(),
			new SelectedContentExportService(new FileContentAnalyzer()));
		var presentation = new ExportPathPresentation(
			displayRootPath: "https://github.com/user/repo",
			mapFilePath: _ => "https://github.com/user/repo/a.txt",
			displayRootName: "DevProjex");

		var result = await service.BuildAsync(
			temp.Path,
			root,
			new HashSet<string>(PathComparer.Default) { Path.Combine(temp.Path, "missing.txt") },
			TreeTextFormat.Ascii,
			CancellationToken.None,
			presentation);

		Assert.Contains("├── DevProjex", result, StringComparison.Ordinal);
	}
}
