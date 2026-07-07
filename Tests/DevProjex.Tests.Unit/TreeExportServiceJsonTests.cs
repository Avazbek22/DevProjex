namespace DevProjex.Tests.Unit;

public sealed class TreeExportServiceJsonTests
{
	[Fact]
	public void BuildFullTree_JsonTree_WritesOnlyRootPathAndTree()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();

		var result = service.BuildFullTree(fixture.RootPath, fixture.Root, TreeTextFormat.Json);

		using var document = JsonDocument.Parse(result);
		JsonTreeExportTestHelper.AssertOnlyRootPathAndTree(document.RootElement);
		Assert.Equal(JsonTreeExportTestHelper.NormalizeJsonPath(fixture.RootPath), document.RootElement.GetProperty("rootPath").GetString());
		Assert.DoesNotContain("\\", document.RootElement.GetProperty("rootPath").GetString(), StringComparison.Ordinal);
	}

	[Fact]
	public void BuildFullTree_JsonTree_SerializesFolderWithOnlyFilesAsArray()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();

		var result = service.BuildFullTree(fixture.RootPath, fixture.Root, TreeTextFormat.Json);

		using var document = JsonDocument.Parse(result);
		var tree = JsonTreeExportTestHelper.GetTree(document);
		var folder = tree.GetProperty("Folder");
		Assert.Equal(JsonValueKind.Array, folder.ValueKind);
		Assert.Equal(["File.cs"], folder.EnumerateArray().Select(static item => item.GetString()!).ToArray());
	}

	[Fact]
	public void BuildFullTree_JsonTree_RepresentsEmptyFolderAsEmptyArray()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();

		var result = service.BuildFullTree(fixture.RootPath, fixture.Root, TreeTextFormat.Json);

		using var document = JsonDocument.Parse(result);
		var emptyFolder = JsonTreeExportTestHelper.GetTree(document).GetProperty("EmptyFolder");
		Assert.Equal(JsonValueKind.Array, emptyFolder.ValueKind);
		Assert.Empty(emptyFolder.EnumerateArray());
		Assert.Contains("EmptyFolder", JsonTreeExportTestHelper.ExtractEmptyFolderPaths(JsonTreeExportTestHelper.GetTree(document)));
	}

	[Fact]
	public void BuildFullTree_JsonTree_WritesRootLevelFilesInsideTree()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();

		var result = service.BuildFullTree(fixture.RootPath, fixture.Root, TreeTextFormat.Json);

		using var document = JsonDocument.Parse(result);
		var tree = JsonTreeExportTestHelper.GetTree(document);
		var rootFiles = tree.GetProperty("/");
		Assert.Equal(JsonValueKind.Array, rootFiles.ValueKind);
		Assert.Contains(rootFiles.EnumerateArray(), item => item.GetString() == "README.md");
	}

	[Fact]
	public void BuildFullTree_JsonTree_FolderWithOnlySubfoldersUsesObjectWithoutSlash()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();

		var result = service.BuildFullTree(fixture.RootPath, fixture.Root, TreeTextFormat.Json);

		using var document = JsonDocument.Parse(result);
		var src = JsonTreeExportTestHelper.GetTree(document).GetProperty("src");
		Assert.Equal(JsonValueKind.Object, src.ValueKind);
		Assert.True(src.TryGetProperty("features", out _));
		Assert.False(src.TryGetProperty("/", out _));
	}

	[Fact]
	public void BuildFullTree_JsonTree_FolderWithFilesAndSubfoldersUsesSlashForCurrentFolderFiles()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexJsonMixedFolderFixture");
		var root = new TreeNodeDescriptor(
			"Root",
			rootPath,
			true,
			false,
			"folder",
			[
				new(
					"src",
					Path.Combine(rootPath, "src"),
					true,
					false,
					"folder",
					[
						new("Program.cs", Path.Combine(rootPath, "src", "Program.cs"), false, false, "csharp", []),
						new(
							"Services",
							Path.Combine(rootPath, "src", "Services"),
							true,
							false,
							"folder",
							[
								new("UserService.cs", Path.Combine(rootPath, "src", "Services", "UserService.cs"), false, false, "csharp", [])
							])
					])
			]);
		var service = new TreeExportService();

		var result = service.BuildFullTree(rootPath, root, TreeTextFormat.Json);

		using var document = JsonDocument.Parse(result);
		var src = JsonTreeExportTestHelper.GetTree(document).GetProperty("src");
		Assert.Equal(JsonValueKind.Object, src.ValueKind);
		Assert.Equal(["Services", "/"], src.EnumerateObject().Select(static property => property.Name).ToArray());
		Assert.Equal(["Program.cs"], src.GetProperty("/").EnumerateArray().Select(static item => item.GetString()!).ToArray());
		Assert.Equal(["src/Services/UserService.cs", "src/Program.cs"], JsonTreeExportTestHelper.ExtractFilePaths(JsonTreeExportTestHelper.GetTree(document)));
	}

	[Fact]
	public void BuildFullTree_JsonTree_RootWithOnlyFilesUsesSlashArrayInsideTreeObject()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexJsonRootFilesFixture");
		var root = new TreeNodeDescriptor(
			"Root",
			rootPath,
			true,
			false,
			"folder",
			[
				new("README.md", Path.Combine(rootPath, "README.md"), false, false, "markdown", []),
				new("global.json", Path.Combine(rootPath, "global.json"), false, false, "json", [])
			]);
		var service = new TreeExportService();

		var result = service.BuildFullTree(rootPath, root, TreeTextFormat.Json);

		using var document = JsonDocument.Parse(result);
		var tree = JsonTreeExportTestHelper.GetTree(document);
		Assert.Equal(["/"], tree.EnumerateObject().Select(static property => property.Name).ToArray());
		Assert.Equal(["global.json", "README.md"], tree.GetProperty("/").EnumerateArray().Select(static item => item.GetString()!).ToArray());
	}

	[Fact]
	public void BuildFullTree_JsonTree_PreservesMixedAndDeepHierarchy()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();

		var result = service.BuildFullTree(fixture.RootPath, fixture.Root, TreeTextFormat.Json);

		using var document = JsonDocument.Parse(result);
		var tree = JsonTreeExportTestHelper.GetTree(document);
		var authFiles = tree
			.GetProperty("src")
			.GetProperty("features")
			.GetProperty("auth");

		Assert.Equal(JsonValueKind.Array, authFiles.ValueKind);
		Assert.Equal(["Login.cs"], authFiles.EnumerateArray().Select(static item => item.GetString()!).ToArray());
		Assert.Contains("src/features/auth/Login.cs", JsonTreeExportTestHelper.ExtractFilePaths(tree));
	}

	[Fact]
	public void BuildFullTree_JsonTree_DoesNotWriteLegacyContractFields()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();

		var result = service.BuildFullTree(fixture.RootPath, fixture.Root, TreeTextFormat.Json);

		using var document = JsonDocument.Parse(result);
		JsonTreeExportTestHelper.AssertNoLegacyTreeContract(document.RootElement);
		JsonTreeExportTestHelper.AssertJsonTreeStructure(JsonTreeExportTestHelper.GetTree(document));
	}

	[Fact]
	public void BuildFullTree_JsonTree_NormalizesWindowsRootPathToForwardSlashes()
	{
		var rootPath = @"C:\Users\name\Project";
		var root = new TreeNodeDescriptor(
			"Project",
			rootPath,
			true,
			false,
			"folder",
			[
				new("Program.cs", @"C:\Users\name\Project\Program.cs", false, false, "csharp", [])
			]);
		var service = new TreeExportService();

		var result = service.BuildFullTree(rootPath, root, TreeTextFormat.Json);

		using var document = JsonDocument.Parse(result);
		var rootPathValue = document.RootElement.GetProperty("rootPath").GetString();
		Assert.EndsWith("C:/Users/name/Project", rootPathValue, StringComparison.Ordinal);
		Assert.DoesNotContain(@"C:\\Users", result, StringComparison.Ordinal);
	}

	[Fact]
	public void BuildFullTree_JsonTree_PreservesUnicodeAndSpecialJsonCharacters()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexJsonSpecialFixture");
		var root = new TreeNodeDescriptor(
			"Root",
			rootPath,
			true,
			false,
			"folder",
			[
				new(
					"папка с пробелами",
					Path.Combine(rootPath, "папка с пробелами"),
					true,
					false,
					"folder",
					[
						new("файл \"quote\".cs", Path.Combine(rootPath, "папка с пробелами", "файл quote.cs"), false, false, "csharp", []),
						new("literal\\backslash.txt", Path.Combine(rootPath, "папка с пробелами", "literal-backslash.txt"), false, false, "text", []),
						new("README", Path.Combine(rootPath, "папка с пробелами", "README"), false, false, "text", [])
					])
			]);
		var service = new TreeExportService();

		var result = service.BuildFullTree(rootPath, root, TreeTextFormat.Json);

		using var document = JsonDocument.Parse(result);
		var tree = JsonTreeExportTestHelper.GetTree(document);
		var folder = tree.GetProperty("папка с пробелами");
		var files = folder.EnumerateArray().Select(static item => item.GetString()!).ToArray();
		Assert.Contains("файл \"quote\".cs", files);
		Assert.Contains("literal\\backslash.txt", files);
		Assert.Contains("README", files);
	}

	[Fact]
	public void BuildFullTree_JsonTree_PreservesCaseDistinctFileNames()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexJsonCaseFixture");
		var root = new TreeNodeDescriptor(
			"Root",
			rootPath,
			true,
			false,
			"folder",
			[
				new("README.md", Path.Combine(rootPath, "README.md"), false, false, "markdown", []),
				new("readme.md", Path.Combine(rootPath, "readme.md"), false, false, "markdown", [])
			]);
		var service = new TreeExportService();

		var result = service.BuildFullTree(rootPath, root, TreeTextFormat.Json);

		using var document = JsonDocument.Parse(result);
		var files = JsonTreeExportTestHelper.ExtractFilePaths(JsonTreeExportTestHelper.GetTree(document));
		Assert.Contains("README.md", files);
		Assert.Contains("readme.md", files);
		Assert.Equal(2, files.Length);
	}

	[Fact]
	public void BuildFullTree_JsonTree_UsesDeterministicFoldersFirstOrdering()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexJsonOrderingFixture");
		var root = new TreeNodeDescriptor(
			"Root",
			rootPath,
			true,
			false,
			"folder",
			[
				new("z-file.txt", Path.Combine(rootPath, "z-file.txt"), false, false, "text", []),
				new("b-dir", Path.Combine(rootPath, "b-dir"), true, false, "folder", []),
				new("a-file.txt", Path.Combine(rootPath, "a-file.txt"), false, false, "text", []),
				new("a-dir", Path.Combine(rootPath, "a-dir"), true, false, "folder", [])
			]);
		var service = new TreeExportService();

		var first = service.BuildFullTree(rootPath, root, TreeTextFormat.Json);
		var second = service.BuildFullTree(rootPath, root, TreeTextFormat.Json);

		Assert.Equal(first, second);
		using var document = JsonDocument.Parse(first);
		var propertyNames = JsonTreeExportTestHelper.GetTree(document)
			.EnumerateObject()
			.Select(static property => property.Name)
			.ToArray();
		Assert.Equal(["a-dir", "b-dir", "/"], propertyNames);
		Assert.Equal(["a-file.txt", "z-file.txt"], JsonTreeExportTestHelper.GetTree(document)
			.GetProperty("/")
			.EnumerateArray()
			.Select(static item => item.GetString()!)
			.ToArray());
	}

	[Fact]
	public void BuildFullTree_JsonTree_UsesSameCaseInsensitiveOrderingOnEveryPlatform()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexJsonPlatformOrderingFixture");
		var root = new TreeNodeDescriptor(
			"Root",
			rootPath,
			true,
			false,
			"folder",
			[
				new("README.md", Path.Combine(rootPath, "README.md"), false, false, "markdown", []),
				new("global.json", Path.Combine(rootPath, "global.json"), false, false, "json", []),
				new("Api", Path.Combine(rootPath, "Api"), true, false, "folder", []),
				new("app", Path.Combine(rootPath, "app"), true, false, "folder", []),
				new("README.local.md", Path.Combine(rootPath, "README.local.md"), false, false, "markdown", [])
			]);
		var service = new TreeExportService();

		var result = service.BuildFullTree(rootPath, root, TreeTextFormat.Json);

		using var document = JsonDocument.Parse(result);
		var tree = JsonTreeExportTestHelper.GetTree(document);
		Assert.Equal(["Api", "app", "/"], tree.EnumerateObject().Select(static property => property.Name).ToArray());
		Assert.Equal(["global.json", "README.local.md", "README.md"], tree.GetProperty("/")
			.EnumerateArray()
			.Select(static item => item.GetString()!)
			.ToArray());
	}

	[Fact]
	public void BuildFullTree_JsonTree_EmptyRootWritesEmptyTree()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexJsonEmptyRootFixture");
		var root = new TreeNodeDescriptor("Root", rootPath, true, false, "folder", []);
		var service = new TreeExportService();

		var result = service.BuildFullTree(rootPath, root, TreeTextFormat.Json);

		using var document = JsonDocument.Parse(result);
		var tree = JsonTreeExportTestHelper.GetTree(document);
		Assert.Empty(tree.EnumerateObject());
	}

	[Fact]
	public void BuildFullTree_JsonTree_LargeTreeSmokeProducesValidStructure()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexJsonLargeFixture");
		var bigFolderChildren = new List<TreeNodeDescriptor>();
		for (var index = 0; index < 300; index++)
		{
			bigFolderChildren.Add(new TreeNodeDescriptor(
				$"file{index:D3}.cs",
				Path.Combine(rootPath, "big", $"file{index:D3}.cs"),
				false,
				false,
				"csharp",
				[]));
		}

		var root = new TreeNodeDescriptor(
			"Root",
			rootPath,
			true,
			false,
			"folder",
			[
				new("big", Path.Combine(rootPath, "big"), true, false, "folder", bigFolderChildren),
				new(
					"deep",
					Path.Combine(rootPath, "deep"),
					true,
					false,
					"folder",
					[
						new(
							"nested",
							Path.Combine(rootPath, "deep", "nested"),
							true,
							false,
							"folder",
							[
								new("leaf.txt", Path.Combine(rootPath, "deep", "nested", "leaf.txt"), false, false, "text", [])
							])
					])
			]);
		var service = new TreeExportService();

		var result = service.BuildFullTree(rootPath, root, TreeTextFormat.Json);

		using var document = JsonDocument.Parse(result);
		var tree = JsonTreeExportTestHelper.GetTree(document);
		JsonTreeExportTestHelper.AssertJsonTreeStructure(tree);
		Assert.Equal(301, JsonTreeExportTestHelper.CountFiles(tree));
		Assert.True(JsonTreeExportTestHelper.ContainsFilePath(tree, "deep/nested/leaf.txt"));
	}

	[Fact]
	public void BuildSelectedTree_JsonTree_FileSelectionKeepsAncestorsOnly()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();
		var selected = new HashSet<string>(PathComparer.Default) { fixture.LoginFilePath };

		var result = service.BuildSelectedTree(fixture.RootPath, fixture.Root, selected, TreeTextFormat.Json);

		using var document = JsonDocument.Parse(result);
		var paths = JsonTreeExportTestHelper.ExtractFilePaths(JsonTreeExportTestHelper.GetTree(document));
		Assert.Equal(["src/features/auth/Login.cs"], paths);
	}

	[Fact]
	public void BuildSelectedTree_JsonTree_EmptyFolderSelectionWritesEmptyArray()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();
		var selected = new HashSet<string>(PathComparer.Default) { fixture.EmptyFolderPath };

		var result = service.BuildSelectedTree(fixture.RootPath, fixture.Root, selected, TreeTextFormat.Json);

		using var document = JsonDocument.Parse(result);
		var emptyFolder = JsonTreeExportTestHelper.GetTree(document).GetProperty("EmptyFolder");
		Assert.Equal(JsonValueKind.Array, emptyFolder.ValueKind);
		Assert.Empty(emptyFolder.EnumerateArray());
		Assert.Equal(["EmptyFolder"], JsonTreeExportTestHelper.ExtractEmptyFolderPaths(JsonTreeExportTestHelper.GetTree(document)));
	}

	[Fact]
	public void BuildSelectedTree_JsonTree_RootSelectionReturnsFullTreeContents()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();
		var selected = new HashSet<string>(PathComparer.Default) { fixture.RootPath };

		var result = service.BuildSelectedTree(fixture.RootPath, fixture.Root, selected, TreeTextFormat.Json);

		using var document = JsonDocument.Parse(result);
		var tree = JsonTreeExportTestHelper.GetTree(document);
		Assert.True(JsonTreeExportTestHelper.ContainsFilePath(tree, "Folder/File.cs"));
		Assert.True(JsonTreeExportTestHelper.ContainsFilePath(tree, "README.md"));
	}

	[Fact]
	public void BuildSelectedTree_JsonTree_NoSelectionReturnsEmptyString()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();

		var result = service.BuildSelectedTree(
			fixture.RootPath,
			fixture.Root,
			new HashSet<string>(PathComparer.Default),
			TreeTextFormat.Json);

		Assert.Equal(string.Empty, result);
	}

	private static (
		string RootPath,
		TreeNodeDescriptor Root,
		string LoginFilePath,
		string EmptyFolderPath
	) CreateFixture()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexJsonFixture");
		var folderPath = Path.Combine(rootPath, "Folder");
		var srcPath = Path.Combine(rootPath, "src");
		var featuresPath = Path.Combine(srcPath, "features");
		var authPath = Path.Combine(featuresPath, "auth");
		var loginPath = Path.Combine(authPath, "Login.cs");
		var emptyFolderPath = Path.Combine(rootPath, "EmptyFolder");

		var root = new TreeNodeDescriptor(
			"DevProjex",
			rootPath,
			true,
			false,
			"folder",
			[
				new(
					"Folder",
					folderPath,
					true,
					false,
					"folder",
					[
						new("File.cs", Path.Combine(folderPath, "File.cs"), false, false, "csharp", [])
					]),
				new(
					"src",
					srcPath,
					true,
					false,
					"folder",
					[
						new(
							"features",
							featuresPath,
							true,
							false,
							"folder",
							[
								new(
									"auth",
									authPath,
									true,
									false,
									"folder",
									[
										new("Login.cs", loginPath, false, false, "csharp", [])
									])
							])
					]),
				new("EmptyFolder", emptyFolderPath, true, false, "folder", []),
				new("README.md", Path.Combine(rootPath, "README.md"), false, false, "markdown", [])
			]);

		return (rootPath, root, loginPath, emptyFolderPath);
	}
}
