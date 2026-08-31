namespace DevProjex.Tests.Unit;

public sealed class TreeExportServiceMarkdownTests
{
	[Fact]
	public async Task MarkdownTreeTreatsRootAndNodeNamesAsLiteralTextAcrossAllPaths()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexMarkdownLiteralNames");
		var image = FileNode(
			"![image](https://attacker.test/image.png)",
			Path.Combine(rootPath, "image.md"));
		var link = FileNode(
			"prefix [link](https://attacker.test)",
			Path.Combine(rootPath, "link.md"));
		var html = FileNode("<img src=x>", Path.Combine(rootPath, "html.md"));
		var extensions = FileNode(
			"`code` **bold** &copy; user@example.com www.example.com",
			Path.Combine(rootPath, "extensions.md"));
		var ordered = FileNode("1. nested", Path.Combine(rootPath, "ordered.md"));
		var indentedHeading = FileNode("  # heading", Path.Combine(rootPath, "heading.md"));
		var indentedOrdered = FileNode("  2) nested", Path.Combine(rootPath, "ordered-parenthesis.md"));
		var control = FileNode("line\nbreak", Path.Combine(rootPath, "control.md"));
		var normal = FileNode("Program.cs", Path.Combine(rootPath, "Program.cs"));
		var root = DirectoryNode(
			"![named](https://attacker.test/root.png)",
			rootPath,
			[image, link, html, extensions, ordered, indentedHeading, indentedOrdered, control, normal]);
		const string displayRootPath = "https://example.test/[repo]/<tag>/&copy;";
		var selected = new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer)
		{
			image.FullPath,
			link.FullPath
		};
		var service = new TreeExportService();

		var full = service.BuildFullTree(
			rootPath,
			root,
			TreeTextFormat.Markdown,
			displayRootPath);
		var named = service.BuildFullTree(
			rootPath,
			root,
			TreeTextFormat.Markdown,
			displayRootPath,
			root.DisplayName,
			includeRootPath: false);
		var selectedTree = service.BuildSelectedTree(
			rootPath,
			root,
			selected,
			TreeTextFormat.Markdown,
			displayRootPath);
		using var streamed = new StringWriter(CultureInfo.InvariantCulture);
		await service.WriteFullTreeAsync(
			streamed,
			rootPath,
			root,
			TreeTextFormat.Markdown,
			displayRootPath,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(full, streamed.ToString());
		Assert.Contains(@"https://example.test/\[repo\]/\<tag\>/\&copy;", full, StringComparison.Ordinal);
		Assert.Contains(@"- \!\[image\](https://attacker.test/image.png)", full, StringComparison.Ordinal);
		Assert.Contains(@"- prefix \[link\](https://attacker.test)", full, StringComparison.Ordinal);
		Assert.Contains(@"- \<img src=x\>", full, StringComparison.Ordinal);
		Assert.Contains(@"- \`code\` \*\*bold\*\* \&copy; user@example.com www.example.com", full, StringComparison.Ordinal);
		Assert.Contains(@"- 1\. nested", full, StringComparison.Ordinal);
		Assert.Contains(@"-   \# heading", full, StringComparison.Ordinal);
		Assert.Contains(@"-   2\) nested", full, StringComparison.Ordinal);
		Assert.Contains(@"- line\\nbreak", full, StringComparison.Ordinal);
		Assert.Contains("- Program.cs", full, StringComparison.Ordinal);
		Assert.Contains(@"- \!\[named\](https://attacker.test/root.png)/", named, StringComparison.Ordinal);
		Assert.Contains(@"- \!\[image\](https://attacker.test/image.png)", selectedTree, StringComparison.Ordinal);
		Assert.Contains(@"- prefix \[link\](https://attacker.test)", selectedTree, StringComparison.Ordinal);
		Assert.DoesNotContain("<img src=x>", full, StringComparison.Ordinal);
		Assert.DoesNotContain("- <img src=x>", full, StringComparison.Ordinal);
		Assert.DoesNotContain(" **bold** ", full, StringComparison.Ordinal);
		Assert.Equal(
			ExportOutputMetricsCalculator.FromText(full),
			service.CalculateFullTreeMetrics(
				rootPath,
				root,
				TreeTextFormat.Markdown,
				displayRootPath));
		Assert.Equal(
			ExportOutputMetricsCalculator.FromText(named),
			service.CalculateFullTreeMetrics(
				rootPath,
				root,
				TreeTextFormat.Markdown,
				displayRootPath,
				root.DisplayName,
				includeRootPath: false));
		Assert.Equal(
			ExportOutputMetricsCalculator.FromText(selectedTree),
			service.CalculateSelectedTreeMetrics(
				rootPath,
				root,
				selected,
				TreeTextFormat.Markdown,
				displayRootPath));
	}

	[Fact]
	public void BuildFullTree_MarkdownFormat_EmptyRootWritesOnlyHeader()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexMarkdownEmptyRoot");
		var root = DirectoryNode("Project", rootPath, []);
		var service = new TreeExportService();

		var result = service.BuildFullTree(rootPath, root, TreeTextFormat.Markdown);

		MarkdownTreeExportTestHelper.AssertMarkdownTreeContract(
			result,
			JsonTreeExportTestHelper.NormalizeJsonPath(rootPath));
		Assert.Empty(MarkdownTreeExportTestHelper.ExtractFilePaths(result));
		Assert.Empty(MarkdownTreeExportTestHelper.ExtractEmptyFolderPaths(result));
		Assert.All(GetTreeLines(result), static line => Assert.Equal(string.Empty, line));
	}

	[Fact]
	public void BuildFullTree_MarkdownFormat_RootFileWritesSingleRootLevelFile()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexMarkdownRootFile", "README.md");
		var root = FileNode("README.md", rootPath);
		var service = new TreeExportService();

		var result = service.BuildFullTree(rootPath, root, TreeTextFormat.Markdown);

		MarkdownTreeExportTestHelper.AssertMarkdownTreeContract(
			result,
			JsonTreeExportTestHelper.NormalizeJsonPath(rootPath));
		Assert.Equal(["README.md"], MarkdownTreeExportTestHelper.ExtractFilePaths(result));
		Assert.Equal(["- README.md"], GetTreeLines(result).Where(static line => line.Length > 0).ToArray());
	}

	[Fact]
	public void BuildFullTree_MarkdownFormat_WritesContractAndRoundTripsMixedTree()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();

		var result = service.BuildFullTree(fixture.RootPath, fixture.Root, TreeTextFormat.Markdown);

		MarkdownTreeExportTestHelper.AssertMarkdownTreeContract(
			result,
			JsonTreeExportTestHelper.NormalizeJsonPath(fixture.RootPath));
		Assert.Contains("- EmptyFolder/", result, StringComparison.Ordinal);
		Assert.Contains("  - Services/", result, StringComparison.Ordinal);
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
			SortPaths(MarkdownTreeExportTestHelper.ExtractFilePaths(result)
				.Concat(MarkdownTreeExportTestHelper.ExtractEmptyFolderPaths(result))));
	}

	[Fact]
	public void BuildFullTree_MarkdownFormat_WritesFolderShapesAndDeterministicOrder()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexMarkdownShapes");
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

		var result = service.BuildFullTree(rootPath, root, TreeTextFormat.Markdown);

		MarkdownTreeExportTestHelper.AssertMarkdownTreeContract(
			result,
			JsonTreeExportTestHelper.NormalizeJsonPath(rootPath));
		Assert.Equal(
			[
				"- EmptyFolder/",
				"- Mixed/",
				"  - Services/",
				"    - UserService.cs",
				"  - Program.cs",
				"  - README.md",
				"- OnlyFiles/",
				"  - Alpha.txt",
				"  - beta.txt",
				"- OnlySubfolders/",
				"  - Models/",
				"    - User.cs",
				"  - Services/",
				"    - UserService.cs",
				"- Alpha.md",
				"- zeta.txt",
				string.Empty
			],
			GetTreeLines(result));
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
			SortPaths(MarkdownTreeExportTestHelper.ExtractFilePaths(result)));
		Assert.Equal(["EmptyFolder"], MarkdownTreeExportTestHelper.ExtractEmptyFolderPaths(result));
	}

	[Fact]
	public void BuildFullTree_MarkdownFormat_EscapesLeadingListMarkersWithoutLosingNames()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexMarkdownSpecialNames");
		var root = DirectoryNode("Project", rootPath,
		[
			DirectoryNode("-scripts", Path.Combine(rootPath, "-scripts"),
			[
				FileNode("-build.ps1", Path.Combine(rootPath, "-scripts", "-build.ps1")),
				FileNode("*glob.md", Path.Combine(rootPath, "-scripts", "*glob.md")),
				FileNode("+plus.md", Path.Combine(rootPath, "-scripts", "+plus.md")),
				FileNode("[draft].md", Path.Combine(rootPath, "-scripts", "[draft].md")),
				FileNode("tab\tfile.txt", Path.Combine(rootPath, "-scripts", "tab-file.txt")),
				FileNode("line\nbreak.txt", Path.Combine(rootPath, "-scripts", "line-break.txt")),
				FileNode("Dockerfile", Path.Combine(rootPath, "-scripts", "Dockerfile")),
				FileNode("file.name.with.dots.cs", Path.Combine(rootPath, "-scripts", "file.name.with.dots.cs"))
			]),
			DirectoryNode("Документы", Path.Combine(rootPath, "docs"),
			[
				FileNode("Файл.cs", Path.Combine(rootPath, "docs", "file.cs")),
				FileNode("My File.cs", Path.Combine(rootPath, "docs", "My File.cs"))
			])
		]);
		var service = new TreeExportService();

		var result = service.BuildFullTree(rootPath, root, TreeTextFormat.Markdown);

		MarkdownTreeExportTestHelper.AssertMarkdownTreeContract(
			result,
			JsonTreeExportTestHelper.NormalizeJsonPath(rootPath));
		Assert.Contains("- \\-scripts/", result, StringComparison.Ordinal);
		Assert.Contains("  - \\*glob.md", result, StringComparison.Ordinal);
		Assert.Contains("  - \\+plus.md", result, StringComparison.Ordinal);
		Assert.Contains("  - \\[draft\\].md", result, StringComparison.Ordinal);
		Assert.Contains("  - \\-build.ps1", result, StringComparison.Ordinal);
		Assert.Contains(@"  - tab\\tfile.txt", result, StringComparison.Ordinal);
		Assert.Contains(@"  - line\\nbreak.txt", result, StringComparison.Ordinal);
		Assert.Contains("-scripts/-build.ps1", MarkdownTreeExportTestHelper.ExtractFilePaths(result));
		Assert.Contains("-scripts/*glob.md", MarkdownTreeExportTestHelper.ExtractFilePaths(result));
		Assert.Contains("-scripts/+plus.md", MarkdownTreeExportTestHelper.ExtractFilePaths(result));
		Assert.Contains("-scripts/[draft].md", MarkdownTreeExportTestHelper.ExtractFilePaths(result));
		Assert.Contains("-scripts/tab\tfile.txt", MarkdownTreeExportTestHelper.ExtractFilePaths(result));
		Assert.Contains("-scripts/line\nbreak.txt", MarkdownTreeExportTestHelper.ExtractFilePaths(result));
		Assert.Contains("Документы/Файл.cs", MarkdownTreeExportTestHelper.ExtractFilePaths(result));
	}

	[Fact]
	public void BuildSelectedTree_MarkdownFormat_FiltersSelectionAndKeepsSelectedEmptyFolder()
	{
		var fixture = CreateFixture();
		var selected = new HashSet<string>(PathComparer.Default)
		{
			Path.Combine(fixture.RootPath, "src", "Services", "UserService.cs"),
			Path.Combine(fixture.RootPath, "EmptyFolder")
		};
		var service = new TreeExportService();

		var result = service.BuildSelectedTree(fixture.RootPath, fixture.Root, selected, TreeTextFormat.Markdown);

		Assert.Equal(
			SortPaths(["EmptyFolder", "src/Services/UserService.cs"]),
			SortPaths(MarkdownTreeExportTestHelper.ExtractEmptyFolderPaths(result)
				.Concat(MarkdownTreeExportTestHelper.ExtractFilePaths(result))));
		Assert.DoesNotContain("Program.cs", result, StringComparison.Ordinal);
		Assert.DoesNotContain("README.md", result, StringComparison.Ordinal);
	}

	[Fact]
	public void BuildSelectedTree_MarkdownFormat_RootSelectionReturnsFullTreeAndNoSelectionReturnsEmpty()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();

		var selectedRootResult = service.BuildSelectedTree(
			fixture.RootPath,
			fixture.Root,
			new HashSet<string>(PathComparer.Default) { fixture.RootPath },
			TreeTextFormat.Markdown);
		var noSelectionResult = service.BuildSelectedTree(
			fixture.RootPath,
			fixture.Root,
			new HashSet<string>(PathComparer.Default),
			TreeTextFormat.Markdown);

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
			SortPaths(MarkdownTreeExportTestHelper.ExtractFilePaths(selectedRootResult)
				.Concat(MarkdownTreeExportTestHelper.ExtractEmptyFolderPaths(selectedRootResult))));
		Assert.Equal(string.Empty, noSelectionResult);
	}

	[Fact]
	public void BuildFullTree_MarkdownFormat_UsesDisplayRootPathForGitPresentation()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();

		var result = service.BuildFullTree(
			fixture.RootPath,
			fixture.Root,
			TreeTextFormat.Markdown,
			displayRootPath: "https://github.com/Avazbek22/DevProjex",
			displayRootName: "DevProjex");

		Assert.StartsWith("Root: https://github.com/Avazbek22/DevProjex", result, StringComparison.Ordinal);
		Assert.DoesNotContain(JsonTreeExportTestHelper.NormalizeJsonPath(fixture.RootPath), result, StringComparison.Ordinal);
	}

	[Fact]
	public void BuildFullTree_MarkdownFormat_RepeatedExportIsStableForLargeDeepTree()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexMarkdownLargeTree");
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

		var first = service.BuildFullTree(rootPath, root, TreeTextFormat.Markdown);
		var second = service.BuildFullTree(rootPath, root, TreeTextFormat.Markdown);

		Assert.Equal(first, second);
		MarkdownTreeExportTestHelper.AssertMarkdownTreeContract(
			first,
			JsonTreeExportTestHelper.NormalizeJsonPath(rootPath));
		Assert.Equal(1001, MarkdownTreeExportTestHelper.ExtractFilePaths(first).Length);
		Assert.Contains(
			string.Join('/', Enumerable.Range(0, 50).Select(static level => $"level{level:D2}")) + "/leaf.txt",
			MarkdownTreeExportTestHelper.ExtractFilePaths(first));
	}

	private static ExportFixture CreateFixture()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjexMarkdownFixture");
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

	private static string[] GetTreeLines(string markdown)
		=> markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Skip(2).ToArray();

	private sealed record ExportFixture(string RootPath, TreeNodeDescriptor Root);
}
