using System.Text.Json;
using DevProjex.Application.Services;

namespace DevProjex.Tests.Unit;

public sealed class TreeExportServicePathPresentationTests
{
	[Fact]
	public void BuildFullTree_Ascii_UsesDisplayRootPathWhenProvided()
	{
		var service = new TreeExportService();
		var root = CreateSimpleRoot();

		var result = service.BuildFullTree(
			@"C:\repo",
			root,
			TreeTextFormat.Ascii,
			displayRootPath: "https://github.com/user/repo");

		Assert.StartsWith("https://github.com/user/repo:", result, StringComparison.Ordinal);
	}

	[Fact]
	public void BuildFullTree_Json_UsesDisplayRootPathWhenProvided()
	{
		var service = new TreeExportService();
		var root = CreateSimpleRoot();

		var result = service.BuildFullTree(
			@"C:\repo",
			root,
			TreeTextFormat.Json,
			displayRootPath: "https://github.com/user/repo");

		using var doc = JsonDocument.Parse(result);
		Assert.Equal("https://github.com/user/repo", doc.RootElement.GetProperty("rootPath").GetString());
	}

	[Fact]
	public void BuildSelectedTree_Json_UsesDisplayRootPathWhenProvided()
	{
		var service = new TreeExportService();
		var root = CreateSimpleRoot();
		var selected = new HashSet<string>(PathComparer.Default)
		{
			@"C:\repo\src\main.cs"
		};

		var result = service.BuildSelectedTree(
			@"C:\repo",
			root,
			selected,
			TreeTextFormat.Json,
			displayRootPath: "https://github.com/user/repo");

		using var doc = JsonDocument.Parse(result);
		Assert.Equal("https://github.com/user/repo", doc.RootElement.GetProperty("rootPath").GetString());
	}

	private static TreeNodeDescriptor CreateSimpleRoot()
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
							Children: [])
					])
			]);
	}
}

