using System.Xml.Linq;

namespace DevProjex.Tests.Unit;

public sealed class TreeExportServiceXmlTests
{
	[Fact]
	public void BuildFullTree_XmlFormat_EmptyRootWritesRootElementOnly()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexXmlEmptyRoot");
		var root = DirectoryNode("Project", rootPath, []);
		var service = new TreeExportService();

		var result = service.BuildFullTree(rootPath, root, TreeTextFormat.Xml);

		var document = XmlTreeExportTestHelper.Parse(result);
		XmlTreeExportTestHelper.AssertRootPath(document, JsonTreeExportTestHelper.NormalizeJsonPath(rootPath));
		XmlTreeExportTestHelper.AssertXmlTreeContract(document);
		Assert.Empty(document.Root!.Elements());
		Assert.Empty(XmlTreeExportTestHelper.ExtractFilePaths(document));
		Assert.Empty(XmlTreeExportTestHelper.ExtractEmptyFolderPaths(document));
	}

	[Fact]
	public void BuildFullTree_XmlFormat_RootFileWritesFileInsideRootElement()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexXmlRootFile", "README.md");
		var root = FileNode("README.md", rootPath);
		var service = new TreeExportService();

		var result = service.BuildFullTree(rootPath, root, TreeTextFormat.Xml);

		var document = XmlTreeExportTestHelper.Parse(result);
		XmlTreeExportTestHelper.AssertXmlTreeContract(document);
		Assert.Equal(["README.md"], XmlTreeExportTestHelper.ExtractFilePaths(document));
		Assert.Equal("f", Assert.Single(document.Root!.Elements()).Name.LocalName);
	}

	[Fact]
	public void BuildFullTree_XmlFormat_WritesContractAndRoundTripsMixedTree()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();

		var result = service.BuildFullTree(fixture.RootPath, fixture.Root, TreeTextFormat.Xml);

		var document = XmlTreeExportTestHelper.Parse(result);
		XmlTreeExportTestHelper.AssertRootPath(document, JsonTreeExportTestHelper.NormalizeJsonPath(fixture.RootPath));
		XmlTreeExportTestHelper.AssertXmlTreeContract(document);
		Assert.Equal(
			SortPaths(
			[
				"EmptyFolder",
				"Folder/File.cs",
				"src/Services/UserService.cs",
				"src/Program.cs",
				"global.json",
				"README.md"
			]),
			SortPaths(XmlTreeExportTestHelper.ExtractFilePaths(document)
				.Concat(XmlTreeExportTestHelper.ExtractEmptyFolderPaths(document))));
	}

	[Fact]
	public void BuildFullTree_XmlFormat_WritesFolderShapesAndDeterministicOrder()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexXmlShapes");
		var root = DirectoryNode("Project", rootPath,
		[
			FileNode("zeta.txt", Path.Combine(rootPath, "zeta.txt")),
			DirectoryNode("OnlySubfolders", Path.Combine(rootPath, "OnlySubfolders"),
			[
				DirectoryNode("Services", Path.Combine(rootPath, "OnlySubfolders", "Services"),
				[
					FileNode("UserService.cs", Path.Combine(rootPath, "OnlySubfolders", "Services", "UserService.cs"))
				]),
				DirectoryNode("Models", Path.Combine(rootPath, "OnlySubfolders", "Models"),
				[
					FileNode("User.cs", Path.Combine(rootPath, "OnlySubfolders", "Models", "User.cs"))
				])
			]),
			DirectoryNode("OnlyFiles", Path.Combine(rootPath, "OnlyFiles"),
			[
				FileNode("beta.txt", Path.Combine(rootPath, "OnlyFiles", "beta.txt")),
				FileNode("Alpha.txt", Path.Combine(rootPath, "OnlyFiles", "Alpha.txt"))
			]),
			FileNode("Alpha.md", Path.Combine(rootPath, "Alpha.md")),
			DirectoryNode("Mixed", Path.Combine(rootPath, "Mixed"),
			[
				FileNode("README.md", Path.Combine(rootPath, "Mixed", "README.md")),
				DirectoryNode("Services", Path.Combine(rootPath, "Mixed", "Services"),
				[
					FileNode("UserService.cs", Path.Combine(rootPath, "Mixed", "Services", "UserService.cs"))
				]),
				FileNode("Program.cs", Path.Combine(rootPath, "Mixed", "Program.cs"))
			]),
			DirectoryNode("EmptyFolder", Path.Combine(rootPath, "EmptyFolder"), [])
		]);
		var service = new TreeExportService();

		var result = service.BuildFullTree(rootPath, root, TreeTextFormat.Xml);

		var document = XmlTreeExportTestHelper.Parse(result);
		XmlTreeExportTestHelper.AssertXmlTreeContract(document);
		Assert.Equal(
			[
				"d:EmptyFolder",
				"d:Mixed",
				"d:OnlyFiles",
				"d:OnlySubfolders",
				"f:Alpha.md",
				"f:zeta.txt"
			],
			DescribeChildren(document.Root!));

		Assert.Empty(GetDirectory(document, "EmptyFolder").Elements());
		Assert.Equal(["f:Alpha.txt", "f:beta.txt"], DescribeChildren(GetDirectory(document, "OnlyFiles")));
		Assert.Equal(["d:Models", "d:Services"], DescribeChildren(GetDirectory(document, "OnlySubfolders")));
		Assert.Equal(["d:Services", "f:Program.cs", "f:README.md"], DescribeChildren(GetDirectory(document, "Mixed")));
		Assert.Equal(
			SortPaths(
			[
				"Alpha.md",
				"zeta.txt",
				"OnlyFiles/Alpha.txt",
				"OnlyFiles/beta.txt",
				"OnlySubfolders/Models/User.cs",
				"OnlySubfolders/Services/UserService.cs",
				"Mixed/Program.cs",
				"Mixed/README.md",
				"Mixed/Services/UserService.cs"
			]),
			SortPaths(XmlTreeExportTestHelper.ExtractFilePaths(document)));
	}

	[Fact]
	public void BuildFullTree_XmlFormat_EscapesSpecialCharactersAndDoesNotUseNamesAsTags()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexXmlSpecialNames");
		var root = DirectoryNode("Project", rootPath,
		[
			DirectoryNode(".github", Path.Combine(rootPath, ".github"),
			[
				FileNode("workflow & <main> \"ci\".yml", Path.Combine(rootPath, ".github", "workflow.yml"))
			]),
			DirectoryNode("Документы & <draft> \"Q\"", Path.Combine(rootPath, "docs"),
			[
				FileNode("Файл & <x> \"quote\".cs", Path.Combine(rootPath, "docs", "file.cs")),
				FileNode("App.axaml.cs", Path.Combine(rootPath, "docs", "App.axaml.cs"))
			])
		]);
		var service = new TreeExportService();

		var result = service.BuildFullTree(rootPath, root, TreeTextFormat.Xml);

		var document = XmlTreeExportTestHelper.Parse(result);
		XmlTreeExportTestHelper.AssertXmlTreeContract(document);
		Assert.DoesNotContain("<.github", result, StringComparison.Ordinal);
		Assert.DoesNotContain("<App.axaml.cs", result, StringComparison.Ordinal);
		Assert.Contains(".github/workflow & <main> \"ci\".yml", XmlTreeExportTestHelper.ExtractFilePaths(document));
		Assert.Contains("Документы & <draft> \"Q\"/Файл & <x> \"quote\".cs", XmlTreeExportTestHelper.ExtractFilePaths(document));
		Assert.Contains("&amp;", result, StringComparison.Ordinal);
		Assert.Contains("&lt;", result, StringComparison.Ordinal);
	}

	[Fact]
	public void BuildSelectedTree_XmlFormat_FiltersSelectionAndKeepsSelectedEmptyFolder()
	{
		var fixture = CreateFixture();
		var selected = new HashSet<string>(PathComparer.Default)
		{
			Path.Combine(fixture.RootPath, "src", "Services", "UserService.cs"),
			Path.Combine(fixture.RootPath, "EmptyFolder")
		};
		var service = new TreeExportService();

		var result = service.BuildSelectedTree(fixture.RootPath, fixture.Root, selected, TreeTextFormat.Xml);

		var document = XmlTreeExportTestHelper.Parse(result);
		Assert.Equal(
			SortPaths(["EmptyFolder", "src/Services/UserService.cs"]),
			SortPaths(XmlTreeExportTestHelper.ExtractEmptyFolderPaths(document)
				.Concat(XmlTreeExportTestHelper.ExtractFilePaths(document))));
		Assert.DoesNotContain("Program.cs", result, StringComparison.Ordinal);
		Assert.DoesNotContain("README.md", result, StringComparison.Ordinal);
	}

	[Fact]
	public void BuildSelectedTree_XmlFormat_RootSelectionReturnsFullTreeAndNoSelectionReturnsEmpty()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();

		var selectedRootResult = service.BuildSelectedTree(
			fixture.RootPath,
			fixture.Root,
			new HashSet<string>(PathComparer.Default) { fixture.RootPath },
			TreeTextFormat.Xml);
		var noSelectionResult = service.BuildSelectedTree(
			fixture.RootPath,
			fixture.Root,
			new HashSet<string>(PathComparer.Default),
			TreeTextFormat.Xml);

		var document = XmlTreeExportTestHelper.Parse(selectedRootResult);
		Assert.Equal(
			SortPaths(
			[
				"EmptyFolder",
				"Folder/File.cs",
				"src/Services/UserService.cs",
				"src/Program.cs",
				"global.json",
				"README.md"
			]),
			SortPaths(XmlTreeExportTestHelper.ExtractFilePaths(document)
				.Concat(XmlTreeExportTestHelper.ExtractEmptyFolderPaths(document))));
		Assert.Equal(string.Empty, noSelectionResult);
	}

	[Fact]
	public void BuildFullTree_XmlFormat_UsesDisplayRootPathForGitPresentation()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();

		var result = service.BuildFullTree(
			fixture.RootPath,
			fixture.Root,
			TreeTextFormat.Xml,
			displayRootPath: "https://github.com/Avazbek22/DevProjex",
			displayRootName: "DevProjex");

		var document = XmlTreeExportTestHelper.Parse(result);
		Assert.Equal("https://github.com/Avazbek22/DevProjex", document.Root!.Attribute("r")?.Value);
		Assert.DoesNotContain(JsonTreeExportTestHelper.NormalizeJsonPath(fixture.RootPath), result, StringComparison.Ordinal);
	}

	[Fact]
	public void BuildFullTree_XmlFormat_RepeatedExportIsStableForLargeDeepTree()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexXmlLargeTree");
		var deep = FileNode("leaf.txt", Path.Combine(rootPath, "deep", "leaf.txt"));
		for (var level = 49; level >= 0; level--)
			deep = DirectoryNode($"level{level:D2}", Path.Combine(rootPath, $"level{level:D2}"), [deep]);

		var wideChildren = new List<TreeNodeDescriptor> { deep };
		for (var folder = 0; folder < 100; folder++)
		{
			var files = Enumerable.Range(0, 10)
				.Select(file => FileNode($"file{file:D2}.txt", Path.Combine(rootPath, $"folder{folder:D3}", $"file{file:D2}.txt")))
				.ToArray();
			wideChildren.Add(DirectoryNode($"folder{folder:D3}", Path.Combine(rootPath, $"folder{folder:D3}"), files));
		}

		var root = DirectoryNode("Project", rootPath, wideChildren);
		var service = new TreeExportService();

		var first = service.BuildFullTree(rootPath, root, TreeTextFormat.Xml);
		var second = service.BuildFullTree(rootPath, root, TreeTextFormat.Xml);

		Assert.Equal(first, second);
		var document = XmlTreeExportTestHelper.Parse(first);
		Assert.Equal(1001, XmlTreeExportTestHelper.ExtractFilePaths(document).Length);
		Assert.Contains(
			string.Join('/', Enumerable.Range(0, 50).Select(static level => $"level{level:D2}")) + "/leaf.txt",
			XmlTreeExportTestHelper.ExtractFilePaths(document));
	}

	private static ExportFixture CreateFixture()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexXmlFixture");
		var root = DirectoryNode("Project", rootPath,
		[
			FileNode("README.md", Path.Combine(rootPath, "README.md")),
			FileNode("global.json", Path.Combine(rootPath, "global.json")),
			DirectoryNode("Folder", Path.Combine(rootPath, "Folder"),
			[
				FileNode("File.cs", Path.Combine(rootPath, "Folder", "File.cs"))
			]),
			DirectoryNode("EmptyFolder", Path.Combine(rootPath, "EmptyFolder"), []),
			DirectoryNode("src", Path.Combine(rootPath, "src"),
			[
				FileNode("Program.cs", Path.Combine(rootPath, "src", "Program.cs")),
				DirectoryNode("Services", Path.Combine(rootPath, "src", "Services"),
				[
					FileNode("UserService.cs", Path.Combine(rootPath, "src", "Services", "UserService.cs"))
				])
			])
		]);

		return new ExportFixture(rootPath, root);
	}

	private static TreeNodeDescriptor DirectoryNode(
		string name,
		string fullPath,
		IReadOnlyList<TreeNodeDescriptor> children)
		=> new(name, fullPath, true, false, "folder", children);

	private static TreeNodeDescriptor FileNode(string name, string fullPath)
		=> new(name, fullPath, false, false, "file", []);

	private static string[] SortPaths(IEnumerable<string> paths)
		=> paths.OrderBy(static path => path, StringComparer.Ordinal).ToArray();

	private static XElement GetDirectory(XDocument document, string name)
		=> Assert.Single(document.Root!.Elements("d"), element => element.Attribute("n")?.Value == name);

	private static string[] DescribeChildren(XElement element)
		=> element.Elements()
			.Select(static child => child.Name.LocalName == "d"
				? $"d:{child.Attribute("n")?.Value}"
				: $"f:{child.Value}")
			.ToArray();

	private sealed record ExportFixture(string RootPath, TreeNodeDescriptor Root);
}
