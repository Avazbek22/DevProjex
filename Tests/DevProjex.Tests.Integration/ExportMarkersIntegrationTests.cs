namespace DevProjex.Tests.Integration;

public sealed class ExportMarkersIntegrationTests
{
	[Fact]
	public void Export_FullTree_IncludesMarkersAndSkipsBinary()
	{
		using var temp = new TemporaryDirectory();
		var empty = temp.CreateFile("empty.json", string.Empty);
		var whitespace = temp.CreateFile("space.txt", " \n ");
		var binary = Path.Combine(temp.Path, "image.bin");
		File.WriteAllBytes(binary, new byte[] { 1, 2, 0, 3 });
		var text = temp.CreateFile("note.txt", "Hello");

		var root = BuildPresentedTree(temp.Path, ".json", ".txt", ".bin");
		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService(new FileContentAnalyzer()));

		var output = service.Build(temp.Path, root, new HashSet<string>());

		Assert.Contains("empty.json:", output);
		Assert.Contains("[No Content, 0 bytes]", output);
		Assert.Contains("space.txt:", output);
		Assert.Contains("[Whitespace, 3 bytes]", output);
		Assert.Contains("note.txt:", output);
		Assert.Contains("Hello", output);
		Assert.DoesNotContain($"{binary}:", output);
	}

	[Fact]
	public void Export_Selected_IncludesMarkersForSelectedFiles()
	{
		using var temp = new TemporaryDirectory();
		var empty = temp.CreateFile("empty.json", string.Empty);
		var text = temp.CreateFile("note.txt", "Hello");

		var root = BuildPresentedTree(temp.Path, ".json", ".txt");
		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService(new FileContentAnalyzer()));
		var selected = new HashSet<string> { empty };

		var output = service.Build(temp.Path, root, selected);

		Assert.Contains("empty.json:", output);
		Assert.Contains("[No Content, 0 bytes]", output);
		Assert.DoesNotContain($"{text}:", output);
	}

	[Fact]
	public void Export_Selected_SkipsBinaryContent()
	{
		using var temp = new TemporaryDirectory();
		var binary = Path.Combine(temp.Path, "image.bin");
		File.WriteAllBytes(binary, new byte[] { 1, 2, 0, 3 });

		var root = BuildPresentedTree(temp.Path, ".bin");
		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService(new FileContentAnalyzer()));
		var selected = new HashSet<string> { binary };

		var output = service.Build(temp.Path, root, selected);

		Assert.StartsWith(
			$"{temp.Path}:{Environment.NewLine}└── image.bin",
			output,
			StringComparison.Ordinal);
		Assert.DoesNotContain($"{binary}:", output);
	}

	[Fact]
	public void Export_AccessDeniedNodes_PreserveNamesAcrossFormats()
	{
		using var temp = new TemporaryDirectory();
		var deniedFilePath = Path.Combine(temp.Path, "secrets", "keys.json");
		var sourceRoot = new FileSystemNode(
			"project",
			temp.Path,
			isDirectory: true,
			isAccessDenied: true,
			[
				new FileSystemNode(
					"secrets",
					Path.Combine(temp.Path, "secrets"),
					isDirectory: true,
					isAccessDenied: true,
					[
						new FileSystemNode(
							"keys.json",
							deniedFilePath,
							isDirectory: false,
							isAccessDenied: true,
							FileSystemNode.EmptyChildren)
					])
			]);
		var presenter = new TreeNodePresentationService(
			new LocalizationService(new FakeLocalizationCatalog(), AppLanguage.En),
			new FakeIconMapper());
		var root = presenter.Build(sourceRoot);
		var service = new TreeExportService();

		var ascii = service.BuildFullTree(temp.Path, root, TreeTextFormat.Ascii);
		var json = service.BuildFullTree(temp.Path, root, TreeTextFormat.Json);
		var xml = service.BuildFullTree(temp.Path, root, TreeTextFormat.Xml);
		var markdown = service.BuildFullTree(temp.Path, root, TreeTextFormat.Markdown);

		Assert.StartsWith(
			$"{temp.Path}:{Environment.NewLine}└── secrets [access denied]",
			ascii,
			StringComparison.Ordinal);
		Assert.DoesNotContain("├── project [access denied]", ascii, StringComparison.Ordinal);
		Assert.Contains("secrets [access denied]", ascii, StringComparison.Ordinal);
		Assert.Contains("keys.json [access denied]", ascii, StringComparison.Ordinal);

		using var jsonDocument = JsonDocument.Parse(json);
		var jsonFolder = JsonTreeExportTestHelper.GetTree(jsonDocument)
			.GetProperty("secrets [access denied]");
		Assert.Equal(["keys.json [access denied]"], jsonFolder.EnumerateArray()
			.Select(static item => item.GetString()!)
			.ToArray());

		var xmlDocument = System.Xml.Linq.XDocument.Parse(xml);
		var xmlFolder = Assert.Single(xmlDocument.Descendants("d"));
		Assert.Equal("secrets [access denied]", xmlFolder.Attribute("n")?.Value);
		Assert.Equal("keys.json [access denied]", Assert.Single(xmlDocument.Descendants("f")).Value);

		Assert.Contains("- secrets [access denied]/", markdown, StringComparison.Ordinal);
		Assert.Contains("  - keys.json [access denied]", markdown, StringComparison.Ordinal);
		Assert.DoesNotContain("⛔", ascii + json + xml + markdown, StringComparison.Ordinal);
	}

	private static TreeNodeDescriptor BuildPresentedTree(string rootPath, params string[] extensions)
	{
		var allowedExtensions = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
		var options = new TreeFilterOptions(
			AllowedExtensions: allowedExtensions,
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			IgnoreRules: new IgnoreRules(IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
				IgnoreDotFiles: false,
				SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

		var treeBuilder = new TreeBuilder();
		var presenter = new TreeNodePresentationService(
			new LocalizationService(new FakeLocalizationCatalog(), AppLanguage.En),
			new FakeIconMapper());
		var useCase = new BuildTreeUseCase(treeBuilder, presenter);

		var result = useCase.Execute(new BuildTreeRequest(rootPath, options));
		return result.Root;
	}

	private sealed class FakeLocalizationCatalog : ILocalizationCatalog
	{
		public IReadOnlyDictionary<string, string> Get(AppLanguage language)
		{
			return new Dictionary<string, string>
			{
				{ "Tree.AccessDenied", "access denied" }
			};
		}
	}

	private sealed class FakeIconMapper : IIconMapper
	{
		public string GetIconKey(FileSystemNode node) => node.IsDirectory ? "folder" : "file";
	}
}




