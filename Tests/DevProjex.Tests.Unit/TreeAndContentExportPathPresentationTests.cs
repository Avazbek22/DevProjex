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
			mapFilePath: _ => "https://github.com/user/repo/src/main.cs");

		var result = await service.BuildAsync(
			temp.Path,
			root,
			new HashSet<string>(PathComparer.Default),
			TreeTextFormat.Ascii,
			CancellationToken.None,
			presentation);

		Assert.Contains("https://github.com/user/repo:", result, StringComparison.Ordinal);
		Assert.Contains("https://github.com/user/repo/src/main.cs:", result, StringComparison.Ordinal);
		Assert.DoesNotContain($"{temp.Path}:", result, StringComparison.Ordinal);
	}
}

