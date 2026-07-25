namespace DevProjex.Tests.Unit;

public sealed class TreeAndContentExportServiceTests
{
	// Verifies selected files drive both tree and content exports.
	[Fact]
	public void Build_UsesSelectedTreeAndContentWhenSelectionsProvided()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("file.txt", "Hello");

		var root = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: temp.Path,
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>
			{
				new TreeNodeDescriptor("file.txt", file, false, false, "text", new List<TreeNodeDescriptor>())
			});

		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService(new FileContentAnalyzer()));

		var result = service.Build(temp.Path, root, new HashSet<string> { file });

		Assert.Contains("file.txt", result);
		Assert.Contains("Hello", result);
	}

	// Verifies full tree is used when no selections are provided.
	[Fact]
	public void Build_FallsBackToFullTreeWhenSelectionEmpty()
	{
		var root = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: "/root",
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>());

		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService(new FileContentAnalyzer()));
		var result = service.Build("/root", root, new HashSet<string>());

		Assert.Contains("/root:", result);
	}

	// Verifies tree output is returned when selected content is empty.
	[Fact]
	public void Build_ReturnsTreeWhenSelectedContentEmpty()
	{
		var root = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: "/root",
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>());

		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService(new FileContentAnalyzer()));
		var result = service.Build("/root", root, new HashSet<string> { "/root/missing.txt" });

		Assert.Contains("/root:", result);
		Assert.DoesNotContain("missing.txt:", result);
	}

	// Verifies a selection not in the tree falls back to full tree and all-file content.
	[Fact]
	public void Build_UsesFullTreeWhenSelectionsNotInTree()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("notes.txt", "Note");

		var root = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: temp.Path,
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>
			{
				new TreeNodeDescriptor("notes.txt", file, false, false, "text", new List<TreeNodeDescriptor>())
			});

		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService(new FileContentAnalyzer()));
		var result = service.Build(temp.Path, root, new HashSet<string> { "/missing/file.txt" });

		Assert.Contains("notes.txt", result);
		Assert.Contains("Note", result);
	}

	// Verifies full-tree exports include content for all files when no selections exist.
	[Fact]
	public void Build_UsesAllFilesWhenNoSelection()
	{
		using var temp = new TemporaryDirectory();
		var first = temp.CreateFile("a.txt", "A");
		var second = temp.CreateFile("b.txt", "B");

		var root = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: temp.Path,
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>
			{
				new TreeNodeDescriptor("a.txt", first, false, false, "text", new List<TreeNodeDescriptor>()),
				new TreeNodeDescriptor("b.txt", second, false, false, "text", new List<TreeNodeDescriptor>())
			});

		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService(new FileContentAnalyzer()));
		var result = service.Build(temp.Path, root, new HashSet<string>());

		Assert.Contains("a.txt:", result);
		Assert.Contains("b.txt:", result);
		Assert.Contains("A", result);
		Assert.Contains("B", result);
	}

	[Theory]
	[InlineData(TreeTextFormat.Ascii)]
	[InlineData(TreeTextFormat.Json)]
	[InlineData(TreeTextFormat.Xml)]
	[InlineData(TreeTextFormat.Markdown)]
	public void Build_AllFormatsUseRelativeContentHeadersAndPayloadMetricsStayEfficient(TreeTextFormat format)
	{
		using var temp = new TemporaryDirectory();
		var mainFile = temp.CreateFile(Path.Combine("src", "main.cs"), "class Program {}");
		var readmeFile = temp.CreateFile("README.md", "# Readme");
		var root = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: temp.Path,
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children:
			[
				new TreeNodeDescriptor(
					"src",
					Path.Combine(temp.Path, "src"),
					true,
					false,
					"folder",
					[new TreeNodeDescriptor("main.cs", mainFile, false, false, "text", [])]),
				new TreeNodeDescriptor("README.md", readmeFile, false, false, "text", [])
			]);
		var service = new TreeAndContentExportService(
			new TreeExportService(),
			new SelectedContentExportService(new FileContentAnalyzer()));

		var result = service.Build(temp.Path, root, new HashSet<string>(), format);

		var (_, contentPart) = SplitTreeAndContent(result);
		Assert.Contains("src/main.cs:", contentPart, StringComparison.Ordinal);
		Assert.Contains("README.md:", contentPart, StringComparison.Ordinal);
		Assert.DoesNotContain(mainFile.Replace('\\', '/'), contentPart.Replace('\\', '/'), StringComparison.Ordinal);
		Assert.DoesNotContain(readmeFile.Replace('\\', '/'), contentPart.Replace('\\', '/'), StringComparison.Ordinal);

		var oldAbsolutePayload = result
			.Replace("src/main.cs:", $"{mainFile}:", StringComparison.Ordinal)
			.Replace("README.md:", $"{readmeFile}:", StringComparison.Ordinal);

		var actualMetrics = ExportOutputMetricsCalculator.FromText(result);
		var oldAbsoluteMetrics = ExportOutputMetricsCalculator.FromText(oldAbsolutePayload);
		Assert.Equal(oldAbsoluteMetrics.Lines, actualMetrics.Lines);
		Assert.True(actualMetrics.Chars < oldAbsoluteMetrics.Chars);
		Assert.True(actualMetrics.Tokens <= oldAbsoluteMetrics.Tokens);
	}

	// Verifies clipboard spacing separates tree and content sections.
	[Fact]
	public void Build_IncludesClipboardSpacingBetweenTreeAndContent()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("file.txt", "Hello");


		var root = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: temp.Path,
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>
			{
				new TreeNodeDescriptor("file.txt", file, false, false, "text", new List<TreeNodeDescriptor>())
			});


		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService(new FileContentAnalyzer()));
		var result = service.Build(temp.Path, root, new HashSet<string>());


		var nl = Environment.NewLine;
		Assert.Contains($"\u00A0{nl}\u00A0{nl}", result);
	}

	[Fact]
	public void Build_SelectedDirectoryIncludesDescriptorDescendantContent()
	{
		using var temp = new TemporaryDirectory();
		var folder = temp.CreateFolder("src");
		var file = temp.CreateFile(Path.Combine("src", "main.cs"), "class C {}");

		var root = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: temp.Path,
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>
			{
				new TreeNodeDescriptor(
					"src",
					folder,
					true,
					false,
					"folder",
					new List<TreeNodeDescriptor>
					{
						new TreeNodeDescriptor("main.cs", file, false, false, "text", new List<TreeNodeDescriptor>())
					})
			});

		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService(new FileContentAnalyzer()));
		var result = service.Build(temp.Path, root, new HashSet<string> { folder });

		Assert.Contains("src", result);
		Assert.Contains("src/main.cs:", result);
		Assert.Contains("class C {}", result);
	}

	// Verifies a file root is treated as a file when no selection is provided.
	[Fact]
	public void Build_IncludesFileContentWhenRootIsFile()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("root.txt", "root content");

		var root = new TreeNodeDescriptor(
			DisplayName: "root.txt",
			FullPath: file,
			IsDirectory: false,
			IsAccessDenied: false,
			IconKey: "text",
			Children: new List<TreeNodeDescriptor>());

		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService(new FileContentAnalyzer()));
		var result = service.Build(temp.Path, root, new HashSet<string>());

		Assert.Contains("root.txt:", result);
		Assert.Contains("root content", result);
	}

	// Verifies JSON tree+content export produces JSON tree followed by plain text content.
	[Fact]
	public void Build_WithJsonFormat_ReturnsJsonTreeAndTextContent()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("note.txt", "hello json");

		var root = new TreeNodeDescriptor(
			DisplayName: "root",
			FullPath: temp.Path,
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: new List<TreeNodeDescriptor>
			{
				new TreeNodeDescriptor("note.txt", file, false, false, "text", new List<TreeNodeDescriptor>())
			});

		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService(new FileContentAnalyzer()));
		var result = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		// JSON tree + separator (NBSP) + plain text content
		var separatorIndex = result.IndexOf("\u00A0", StringComparison.Ordinal);
		var jsonPart = result[..separatorIndex].TrimEnd('\r', '\n');
		var contentPart = result[separatorIndex..];

		using var doc = JsonDocument.Parse(jsonPart);
		Assert.Equal(Path.GetFullPath(temp.Path).Replace('\\', '/'), doc.RootElement.GetProperty("rootPath").GetString());
		var tree = JsonTreeExportTestHelper.GetTree(doc);
		Assert.Equal(JsonValueKind.Array, tree.GetProperty("/").ValueKind);
		Assert.Equal(["note.txt"], JsonTreeExportTestHelper.ExtractFilePaths(tree));
		Assert.False(doc.RootElement.TryGetProperty("root", out _));
		Assert.Contains("note.txt", contentPart);
	}

	private static (string TreePart, string ContentPart) SplitTreeAndContent(string export)
	{
		var separatorIndex = export.IndexOf('\u00A0');
		Assert.True(separatorIndex > 0, "Tree + Content export must contain the NBSP separator.");
		return (export[..separatorIndex].TrimEnd('\r', '\n'), export[separatorIndex..]);
	}
}
