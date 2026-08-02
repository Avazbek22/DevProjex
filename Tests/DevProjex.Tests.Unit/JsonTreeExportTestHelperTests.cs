namespace DevProjex.Tests.Unit;

public sealed class JsonTreeExportTestHelperTests
{
	[Fact]
	public void Helper_RoundTripsRootNestedFilesAndEmptyFolders()
	{
		using var document = JsonDocument.Parse("""
			{
			  "rootPath": "C:/Project",
			  "tree": {
			    "docs": [],
			    "src": {
			      "Services": [
			        "UserService.cs"
			      ],
			      "/": [
			        "Program.cs"
			      ]
			    },
			    "/": [
			      "README.md"
			    ]
			  }
			}
			""");

		JsonTreeExportTestHelper.AssertOnlyRootPathAndTree(document.RootElement);
		JsonTreeExportTestHelper.AssertNoLegacyTreeContract(document.RootElement);
		var tree = JsonTreeExportTestHelper.GetTree(document);

		Assert.Equal(
			["docs", "src/Services/UserService.cs", "src/Program.cs", "README.md"],
			JsonTreeExportTestHelper.ExtractEmptyFolderPaths(tree)
				.Concat(JsonTreeExportTestHelper.ExtractFilePaths(tree))
				.ToArray());
		JsonTreeExportTestHelper.AssertRelativePathsUseForwardSlashes(tree);
	}

	[Theory]
	[InlineData("""{ "rootPath": "C:/Project", "tree": { ".": [] } }""")]
	[InlineData("""{ "rootPath": "C:/Project", "tree": { "/": [] } }""")]
	[InlineData("""{ "rootPath": "C:/Project", "tree": { "/": { "File.cs": [] } } }""")]
	[InlineData("""{ "rootPath": "C:/Project", "tree": { "src": [ "Program.cs", 42 ] } }""")]
	[InlineData("""{ "rootPath": "C:/Project", "tree": { "src": true } }""")]
	[InlineData("""{ "rootPath": "C:/Project", "tree": { "Program.cs": null } }""")]
	public void Helper_RejectsMalformedTreeShapes(string json)
	{
		using var document = JsonDocument.Parse(json);
		var tree = JsonTreeExportTestHelper.GetTree(document);

		Assert.ThrowsAny<Exception>(() => JsonTreeExportTestHelper.AssertJsonTreeStructure(tree));
	}

	[Fact]
	public void Helper_RejectsLegacyVerboseTreeShape()
	{
		using var document = JsonDocument.Parse("""
			{
			  "rootPath": "C:/Project",
			  "tree": {
			    "root": {
			      "name": "src",
			      "path": "src",
			      "dirs": [],
			      "files": [
			        "Program.cs"
			      ]
			    }
			  }
			}
			""");

		Assert.ThrowsAny<Exception>(() => JsonTreeExportTestHelper.AssertNoLegacyTreeContract(document.RootElement));
	}

	[Fact]
	public void Helper_DoesNotRejectFolderNamesThatOnlyLookLikeMetadata()
	{
		using var document = JsonDocument.Parse("""
			{
			  "rootPath": "C:/Project",
			  "tree": {
			    "files": [
			      "files.txt"
			    ],
			    "warnings": [
			      "warnings.md"
			    ]
			  }
			}
			""");

		JsonTreeExportTestHelper.AssertNoLegacyTreeContract(document.RootElement);
		var paths = JsonTreeExportTestHelper.ExtractFilePaths(JsonTreeExportTestHelper.GetTree(document));
		Assert.Equal(["files/files.txt", "warnings/warnings.md"], paths);
	}
}
