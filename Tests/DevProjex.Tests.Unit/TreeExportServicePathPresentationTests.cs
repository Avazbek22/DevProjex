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
		Assert.DoesNotContain("C:/repo", result, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(TreeTextFormat.Xml)]
	[InlineData(TreeTextFormat.Markdown)]
	public void BuildFullTree_StructuredFormat_UsesDisplayRootPathWhenProvided(TreeTextFormat format)
	{
		var service = new TreeExportService();
		var root = CreateSimpleRoot();

		var result = service.BuildFullTree(
			@"C:\repo",
			root,
			format,
			displayRootPath: "https://github.com/user/repo");

		if (format == TreeTextFormat.Xml)
		{
			var document = XmlTreeExportTestHelper.Parse(result);
			Assert.Equal("https://github.com/user/repo", document.Root!.Attribute("r")?.Value);
		}
		else
		{
			Assert.StartsWith("Root: https://github.com/user/repo", result, StringComparison.Ordinal);
		}

		Assert.DoesNotContain("C:/repo", result, StringComparison.Ordinal);
	}

	[Fact]
	public void BuildFullTree_Json_DoesNotWriteRootDisplayNameMetadata()
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
		JsonTreeExportTestHelper.AssertOnlyRootPathAndTree(doc.RootElement);
		var tree = JsonTreeExportTestHelper.GetTree(doc);
		Assert.Equal(JsonValueKind.Array, tree.GetProperty("src").ValueKind);
		Assert.Equal(["src/main.cs"], JsonTreeExportTestHelper.ExtractFilePaths(tree));
		Assert.False(doc.RootElement.TryGetProperty("root", out _));
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
		Assert.DoesNotContain("C:/repo", result, StringComparison.Ordinal);
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
	public void BuildSelectedTree_Json_DoesNotWriteDisplayRootNameMetadata()
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
		var tree = JsonTreeExportTestHelper.GetTree(doc);
		Assert.Equal(["src/main.cs"], JsonTreeExportTestHelper.ExtractFilePaths(tree));
		Assert.False(doc.RootElement.TryGetProperty("root", out _));
	}

	[Fact]
	public void BuildFullTree_Json_UsesTreeContentsWithoutRootNode_WhenDisplayRootNameIsNull()
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
		var tree = JsonTreeExportTestHelper.GetTree(doc);
		Assert.Equal(JsonValueKind.Array, tree.GetProperty("src").ValueKind);
		Assert.Equal(["src/main.cs"], JsonTreeExportTestHelper.ExtractFilePaths(tree));
		Assert.False(tree.TryGetProperty("repo", out _));
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
