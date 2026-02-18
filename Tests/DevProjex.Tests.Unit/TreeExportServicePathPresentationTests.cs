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
	public void BuildFullTree_Ascii_UsesDisplayRootNameWhenProvided()
	{
		var service = new TreeExportService();
		var root = CreateSimpleRoot();

		var result = service.BuildFullTree(
			@"C:\repo",
			root,
			TreeTextFormat.Ascii,
			displayRootPath: "https://github.com/user/repo",
			displayRootName: "repo-clean");

		Assert.Contains("├── repo-clean", result, StringComparison.Ordinal);
		Assert.DoesNotContain("├── repo\r\n", result, StringComparison.Ordinal);
		Assert.DoesNotContain("├── repo\n", result, StringComparison.Ordinal);
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
	public void BuildFullTree_Json_UsesDisplayRootNameWhenProvided()
	{
		var service = new TreeExportService();
		var root = CreateSimpleRoot();

		var result = service.BuildFullTree(
			@"C:\repo",
			root,
			TreeTextFormat.Json,
			displayRootPath: "https://github.com/user/repo",
			displayRootName: "repo-clean");

		using var doc = JsonDocument.Parse(result);
		var rootName = doc.RootElement.GetProperty("root").GetProperty("name").GetString();
		Assert.Equal("repo-clean", rootName);
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

	[Fact]
	public void BuildSelectedTree_Ascii_UsesDisplayRootNameWhenProvided()
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
			TreeTextFormat.Ascii,
			displayRootPath: "https://github.com/user/repo",
			displayRootName: "repo-clean");

		Assert.Contains("├── repo-clean", result, StringComparison.Ordinal);
	}

	[Fact]
	public void BuildSelectedTree_Json_UsesDisplayRootNameWhenProvided()
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
			displayRootPath: "https://github.com/user/repo",
			displayRootName: "repo-clean");

		using var doc = JsonDocument.Parse(result);
		var rootNode = doc.RootElement.GetProperty("root");
		Assert.Equal("repo-clean", rootNode.GetProperty("name").GetString());
		Assert.Equal("src", rootNode.GetProperty("dirs")[0].GetProperty("name").GetString());
	}

	[Fact]
	public void BuildFullTree_Json_KeepsOriginalRootName_WhenDisplayRootNameIsNull()
	{
		var service = new TreeExportService();
		var root = CreateSimpleRoot();

		var result = service.BuildFullTree(
			@"C:\repo",
			root,
			TreeTextFormat.Json,
			displayRootPath: "https://github.com/user/repo",
			displayRootName: null);

		using var doc = JsonDocument.Parse(result);
		Assert.Equal("repo", doc.RootElement.GetProperty("root").GetProperty("name").GetString());
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
