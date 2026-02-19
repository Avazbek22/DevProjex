namespace DevProjex.Tests.Unit;

public sealed class TreeAndContentExportServiceCrossPlatformTests
{
	[Fact]
	public void Build_Ascii_IncludesTreeAndContentSeparator()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("notes.txt", "hello");
		var root = CreateRoot(temp.Path, file);
		var service = CreateService();

		var export = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Ascii);

		var separator = $"\u00A0{Environment.NewLine}\u00A0{Environment.NewLine}";
		Assert.Contains(separator, export);
	}

	[Fact]
	public void Build_Json_ContainsJsonTreeAndTextContent()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("notes.txt", "hello");
		var root = CreateRoot(temp.Path, file);
		var service = CreateService();

		var export = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		// JSON tree + separator + text content
		var (jsonPart, contentPart) = SplitJsonAndContent(export);

		using var doc = JsonDocument.Parse(jsonPart);
		Assert.Equal(Path.GetFullPath(temp.Path), doc.RootElement.GetProperty("rootPath").GetString());
		Assert.True(doc.RootElement.TryGetProperty("root", out _));

		Assert.Contains("notes.txt", contentPart);
		Assert.Contains("hello", contentPart);
	}

	[Fact]
	public void Build_Json_SelectionOutsideTreeFallsBackToFullTreeAndAllContent()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("notes.txt", "hello");
		var root = CreateRoot(temp.Path, file);
		var selected = new HashSet<string> { Path.Combine(temp.Path, "missing.txt") };
		var service = CreateService();

		var export = service.Build(temp.Path, root, selected, TreeTextFormat.Json);

		var (jsonPart, contentPart) = SplitJsonAndContent(export);

		using var doc = JsonDocument.Parse(jsonPart);
		Assert.Equal("notes.txt", doc.RootElement.GetProperty("root").GetProperty("files")[0].GetString());
		Assert.Contains(file, contentPart);
	}

	[Fact]
	public void Build_Ascii_SelectionOutsideTreeFallsBackToFullTreeAndAllContent()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("notes.txt", "hello");
		var root = CreateRoot(temp.Path, file);
		var selected = new HashSet<string> { Path.Combine(temp.Path, "missing.txt") };
		var service = CreateService();

		var export = service.Build(temp.Path, root, selected, TreeTextFormat.Ascii);

		Assert.Contains("notes.txt", export);
		Assert.Contains("hello", export);
	}

	[Fact]
	public void Build_Json_DirectorySelectionReturnsTreeWithoutContentWhenNoFilesSelected()
	{
		using var temp = new TemporaryDirectory();
		var srcFolder = temp.CreateFolder("src");
		var file = temp.CreateFile(Path.Combine("src", "main.cs"), "class C {}");
		var root = CreateRootWithDirectory(temp.Path, srcFolder, file);
		var selected = new HashSet<string> { srcFolder };
		var service = CreateService();

		var export = service.Build(temp.Path, root, selected, TreeTextFormat.Json);

		// When no files selected, only tree JSON is returned (no content part)
		using var doc = JsonDocument.Parse(export);
		Assert.True(doc.RootElement.TryGetProperty("root", out _));
	}

	[Fact]
	public void Build_Json_PreservesUnicodeContent()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("unicode.txt", "Привет, мир!");
		var root = CreateRoot(temp.Path, file);
		var service = CreateService();

		var export = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		// Content is plain text after JSON tree
		Assert.Contains("Привет, мир!", export);
	}

	[Fact]
	public void Build_Json_ContentWithBracesIsNotConfusedWithJson()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("code.cs", "class Test { void Method() { if (true) { } } }");
		var root = CreateRoot(temp.Path, file, "code.cs");
		var service = CreateService();

		var export = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		var (jsonPart, contentPart) = SplitJsonAndContent(export);

		// JSON part should be valid and parseable
		using var doc = JsonDocument.Parse(jsonPart);
		Assert.True(doc.RootElement.TryGetProperty("root", out _));

		// Content part should contain the code with braces
		Assert.Contains("class Test", contentPart);
		Assert.Contains("{ void Method()", contentPart);
	}

	[Fact]
	public void Build_Json_MultipleFilesAllIncludedInContent()
	{
		using var temp = new TemporaryDirectory();
		var file1 = temp.CreateFile("a.txt", "content A");
		var file2 = temp.CreateFile("b.txt", "content B");
		var file3 = temp.CreateFile("c.txt", "content C");

		var root = new TreeNodeDescriptor(
			"Root", temp.Path, true, false, "folder",
			new List<TreeNodeDescriptor>
			{
				new("a.txt", file1, false, false, "text", new List<TreeNodeDescriptor>()),
				new("b.txt", file2, false, false, "text", new List<TreeNodeDescriptor>()),
				new("c.txt", file3, false, false, "text", new List<TreeNodeDescriptor>())
			});

		var service = CreateService();
		var export = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		var (jsonPart, contentPart) = SplitJsonAndContent(export);

		// All files in JSON tree
		using var doc = JsonDocument.Parse(jsonPart);
		var files = doc.RootElement.GetProperty("root").GetProperty("files");
		Assert.Equal(3, files.GetArrayLength());

		// All content in text part
		Assert.Contains("content A", contentPart);
		Assert.Contains("content B", contentPart);
		Assert.Contains("content C", contentPart);
	}

	[Fact]
	public void Build_AsciiAndJson_ProduceSameContentForSameFiles()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("test.txt", "same content");
		var root = CreateRoot(temp.Path, file, "test.txt");
		var service = CreateService();

		var asciiExport = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Ascii);
		var jsonExport = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		var (_, jsonContentPart) = SplitJsonAndContent(jsonExport);

		// Both should contain the same file content
		Assert.Contains("same content", asciiExport);
		Assert.Contains("same content", jsonContentPart);

		// Both should have the file path marker
		Assert.Contains("test.txt:", asciiExport);
		Assert.Contains("test.txt:", jsonContentPart);
	}

	[Fact]
	public void Build_Json_SelectedFileOnlyInContent()
	{
		using var temp = new TemporaryDirectory();
		var file1 = temp.CreateFile("selected.txt", "I am selected");
		var file2 = temp.CreateFile("not_selected.txt", "I am NOT selected");

		var root = new TreeNodeDescriptor(
			"Root", temp.Path, true, false, "folder",
			new List<TreeNodeDescriptor>
			{
				new("selected.txt", file1, false, false, "text", new List<TreeNodeDescriptor>()),
				new("not_selected.txt", file2, false, false, "text", new List<TreeNodeDescriptor>())
			});

		var service = CreateService();
		var selected = new HashSet<string> { file1 };
		var export = service.Build(temp.Path, root, selected, TreeTextFormat.Json);

		var (_, contentPart) = SplitJsonAndContent(export);

		Assert.Contains("I am selected", contentPart);
		Assert.DoesNotContain("I am NOT selected", contentPart);
	}

	private static (string JsonPart, string ContentPart) SplitJsonAndContent(string export)
	{
		// Find separator (NBSP = \u00A0) which separates JSON tree from text content
		var separatorIndex = export.IndexOf("\u00A0", StringComparison.Ordinal);
		if (separatorIndex < 0)
			return (export, string.Empty);

		var jsonPart = export[..separatorIndex].TrimEnd('\r', '\n');
		var contentPart = export[separatorIndex..];
		return (jsonPart, contentPart);
	}

	private static TreeAndContentExportService CreateService()
	{
		return new TreeAndContentExportService(
			new TreeExportService(),
			new SelectedContentExportService(new FileContentAnalyzer()));
	}

	private static TreeNodeDescriptor CreateRoot(string rootPath, string filePath, string fileName = "notes.txt")
	{
		return new TreeNodeDescriptor(
			"Root",
			rootPath,
			true,
			false,
			"folder",
			new List<TreeNodeDescriptor>
			{
				new(fileName, filePath, false, false, "text", new List<TreeNodeDescriptor>())
			});
	}

	private static TreeNodeDescriptor CreateRootWithDirectory(string rootPath, string directoryPath, string filePath)
	{
		return new TreeNodeDescriptor(
			"Root",
			rootPath,
			true,
			false,
			"folder",
			new List<TreeNodeDescriptor>
			{
				new(
					"src",
					directoryPath,
					true,
					false,
					"folder",
					new List<TreeNodeDescriptor>
					{
						new("main.cs", filePath, false, false, "csharp", new List<TreeNodeDescriptor>())
					})
			});
	}

	[Fact]
	public void Build_Json_DeepNestedStructurePreservesHierarchy()
	{
		using var temp = new TemporaryDirectory();
		var level1 = temp.CreateFolder("src");
		var level2 = temp.CreateFolder(Path.Combine("src", "components"));
		var level3 = temp.CreateFolder(Path.Combine("src", "components", "ui"));
		var deepFile = temp.CreateFile(Path.Combine("src", "components", "ui", "Button.tsx"), "export const Button = () => <button/>;");

		var root = new TreeNodeDescriptor(
			"Root", temp.Path, true, false, "folder",
			new List<TreeNodeDescriptor>
			{
				new("src", level1, true, false, "folder",
					new List<TreeNodeDescriptor>
					{
						new("components", level2, true, false, "folder",
							new List<TreeNodeDescriptor>
							{
								new("ui", level3, true, false, "folder",
									new List<TreeNodeDescriptor>
									{
										new("Button.tsx", deepFile, false, false, "typescript", new List<TreeNodeDescriptor>())
									})
							})
					})
			});

		var service = CreateService();
		var export = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		var (jsonPart, contentPart) = SplitJsonAndContent(export);

		using var doc = JsonDocument.Parse(jsonPart);
		var srcDir = doc.RootElement.GetProperty("root").GetProperty("dirs")[0];
		Assert.Equal("src", srcDir.GetProperty("name").GetString());

		var componentsDir = srcDir.GetProperty("dirs")[0];
		Assert.Equal("components", componentsDir.GetProperty("name").GetString());

		var uiDir = componentsDir.GetProperty("dirs")[0];
		Assert.Equal("ui", uiDir.GetProperty("name").GetString());
		Assert.Equal("Button.tsx", uiDir.GetProperty("files")[0].GetString());

		Assert.Contains("Button.tsx", contentPart);
		Assert.Contains("export const Button", contentPart);
	}

	[Fact]
	public void Build_Ascii_DeepNestedStructureShowsCorrectIndentation()
	{
		using var temp = new TemporaryDirectory();
		var level1 = temp.CreateFolder("src");
		var level2 = temp.CreateFolder(Path.Combine("src", "lib"));
		var deepFile = temp.CreateFile(Path.Combine("src", "lib", "utils.ts"), "export function util() {}");

		var root = new TreeNodeDescriptor(
			"Root", temp.Path, true, false, "folder",
			new List<TreeNodeDescriptor>
			{
				new("src", level1, true, false, "folder",
					new List<TreeNodeDescriptor>
					{
						new("lib", level2, true, false, "folder",
							new List<TreeNodeDescriptor>
							{
								new("utils.ts", deepFile, false, false, "typescript", new List<TreeNodeDescriptor>())
							})
					})
			});

		var service = CreateService();
		var export = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Ascii);

		Assert.Contains("src", export);
		Assert.Contains("lib", export);
		Assert.Contains("utils.ts", export);
		Assert.Contains("export function util", export);
	}

	[Fact]
	public void Build_Json_EmptyFileShowsNoContentMarker()
	{
		using var temp = new TemporaryDirectory();
		var emptyFile = temp.CreateFile("empty.txt", "");
		var root = CreateRoot(temp.Path, emptyFile, "empty.txt");
		var service = CreateService();

		var export = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		var (jsonPart, contentPart) = SplitJsonAndContent(export);

		using var doc = JsonDocument.Parse(jsonPart);
		Assert.Equal("empty.txt", doc.RootElement.GetProperty("root").GetProperty("files")[0].GetString());
		Assert.Contains("[No Content, 0 bytes]", contentPart);
	}

	[Fact]
	public void Build_Ascii_EmptyFileShowsNoContentMarker()
	{
		using var temp = new TemporaryDirectory();
		var emptyFile = temp.CreateFile("empty.txt", "");
		var root = CreateRoot(temp.Path, emptyFile, "empty.txt");
		var service = CreateService();

		var export = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Ascii);

		Assert.Contains("empty.txt", export);
		Assert.Contains("[No Content, 0 bytes]", export);
	}

	[Fact]
	public void Build_Json_WhitespaceOnlyFileShowsWhitespaceMarker()
	{
		using var temp = new TemporaryDirectory();
		var whitespaceFile = temp.CreateFile("spaces.txt", "   \n\t\t\n   ");
		var root = CreateRoot(temp.Path, whitespaceFile, "spaces.txt");
		var service = CreateService();

		var export = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		var (_, contentPart) = SplitJsonAndContent(export);
		Assert.Contains("[Whitespace,", contentPart);
		Assert.Contains("bytes]", contentPart);
	}

	[Fact]
	public void Build_Json_MixedSelectionIncludesOnlySelectedFiles()
	{
		using var temp = new TemporaryDirectory();
		var srcDir = temp.CreateFolder("src");
		var file1 = temp.CreateFile(Path.Combine("src", "a.ts"), "const a = 1;");
		var file2 = temp.CreateFile(Path.Combine("src", "b.ts"), "const b = 2;");
		var file3 = temp.CreateFile("root.ts", "const root = 0;");

		var root = new TreeNodeDescriptor(
			"Root", temp.Path, true, false, "folder",
			new List<TreeNodeDescriptor>
			{
				new("src", srcDir, true, false, "folder",
					new List<TreeNodeDescriptor>
					{
						new("a.ts", file1, false, false, "typescript", new List<TreeNodeDescriptor>()),
						new("b.ts", file2, false, false, "typescript", new List<TreeNodeDescriptor>())
					}),
				new("root.ts", file3, false, false, "typescript", new List<TreeNodeDescriptor>())
			});

		var service = CreateService();
		var selected = new HashSet<string> { file1, file3 };
		var export = service.Build(temp.Path, root, selected, TreeTextFormat.Json);

		var (jsonPart, contentPart) = SplitJsonAndContent(export);

		Assert.Contains("const a = 1", contentPart);
		Assert.Contains("const root = 0", contentPart);
		Assert.DoesNotContain("const b = 2", contentPart);

		using var doc = JsonDocument.Parse(jsonPart);
		var rootFiles = doc.RootElement.GetProperty("root").GetProperty("files");
		Assert.Equal("root.ts", rootFiles[0].GetString());
	}

	[Fact]
	public void Build_Json_SameFileNameInDifferentDirectoriesBothIncluded()
	{
		using var temp = new TemporaryDirectory();
		var dir1 = temp.CreateFolder("moduleA");
		var dir2 = temp.CreateFolder("moduleB");
		var file1 = temp.CreateFile(Path.Combine("moduleA", "index.ts"), "export const A = 'A';");
		var file2 = temp.CreateFile(Path.Combine("moduleB", "index.ts"), "export const B = 'B';");

		var root = new TreeNodeDescriptor(
			"Root", temp.Path, true, false, "folder",
			new List<TreeNodeDescriptor>
			{
				new("moduleA", dir1, true, false, "folder",
					new List<TreeNodeDescriptor>
					{
						new("index.ts", file1, false, false, "typescript", new List<TreeNodeDescriptor>())
					}),
				new("moduleB", dir2, true, false, "folder",
					new List<TreeNodeDescriptor>
					{
						new("index.ts", file2, false, false, "typescript", new List<TreeNodeDescriptor>())
					})
			});

		var service = CreateService();
		var export = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		var (jsonPart, contentPart) = SplitJsonAndContent(export);

		using var doc = JsonDocument.Parse(jsonPart);
		var dirs = doc.RootElement.GetProperty("root").GetProperty("dirs");
		Assert.Equal(2, dirs.GetArrayLength());

		Assert.Contains("export const A = 'A'", contentPart);
		Assert.Contains("export const B = 'B'", contentPart);
		Assert.Contains("moduleA", contentPart);
		Assert.Contains("moduleB", contentPart);
	}

	[Fact]
	public void Build_Json_SpecialCharactersInFileNamePreserved()
	{
		using var temp = new TemporaryDirectory();
		var specialFile = temp.CreateFile("файл (копия) [1].txt", "содержимое файла");
		var root = CreateRoot(temp.Path, specialFile, "файл (копия) [1].txt");
		var service = CreateService();

		var export = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		var (jsonPart, contentPart) = SplitJsonAndContent(export);

		using var doc = JsonDocument.Parse(jsonPart);
		Assert.Equal("файл (копия) [1].txt", doc.RootElement.GetProperty("root").GetProperty("files")[0].GetString());
		Assert.Contains("содержимое файла", contentPart);
	}

	[Fact]
	public void Build_Json_BinaryFileExcludedFromContent()
	{
		using var temp = new TemporaryDirectory();
		var textFile = temp.CreateFile("readme.txt", "Hello");
		var binaryFile = temp.CreateBinaryFile("image.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

		var root = new TreeNodeDescriptor(
			"Root", temp.Path, true, false, "folder",
			new List<TreeNodeDescriptor>
			{
				new("readme.txt", textFile, false, false, "text", new List<TreeNodeDescriptor>()),
				new("image.png", binaryFile, false, false, "image", new List<TreeNodeDescriptor>())
			});

		var service = CreateService();
		var export = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		var (jsonPart, contentPart) = SplitJsonAndContent(export);

		using var doc = JsonDocument.Parse(jsonPart);
		var files = doc.RootElement.GetProperty("root").GetProperty("files");
		Assert.Equal(2, files.GetArrayLength());

		Assert.Contains("Hello", contentPart);
		Assert.DoesNotContain("image.png:", contentPart);
	}

	[Fact]
	public void Build_Json_EmptyDirectoryIncludedInTree()
	{
		using var temp = new TemporaryDirectory();
		var emptyDir = temp.CreateFolder("empty");

		var root = new TreeNodeDescriptor(
			"Root", temp.Path, true, false, "folder",
			new List<TreeNodeDescriptor>
			{
				new("empty", emptyDir, true, false, "folder", new List<TreeNodeDescriptor>())
			});

		var service = CreateService();
		var export = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(export);
		var dirs = doc.RootElement.GetProperty("root").GetProperty("dirs");
		Assert.Equal(1, dirs.GetArrayLength());
		Assert.Equal("empty", dirs[0].GetProperty("name").GetString());
	}

	[Fact]
	public void Build_Json_RootIsSingleFileReturnsFileContent()
	{
		using var temp = new TemporaryDirectory();
		var singleFile = temp.CreateFile("standalone.cs", "class Standalone {}");

		var root = new TreeNodeDescriptor(
			"standalone.cs", singleFile, false, false, "csharp", new List<TreeNodeDescriptor>());

		var service = CreateService();
		var export = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		var (jsonPart, contentPart) = SplitJsonAndContent(export);

		using var doc = JsonDocument.Parse(jsonPart);
		Assert.Equal("standalone.cs", doc.RootElement.GetProperty("root").GetProperty("name").GetString());
		Assert.Contains("class Standalone", contentPart);
	}

	[Fact]
	public async Task BuildAsync_CancellationTokenRespected()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("test.txt", "content");
		var root = CreateRoot(temp.Path, file, "test.txt");
		var service = CreateService();

		using var cts = new CancellationTokenSource();
		cts.Cancel();

		await Assert.ThrowsAsync<OperationCanceledException>(async () =>
			await service.BuildAsync(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json, cts.Token));
	}

	[Fact]
	public void Build_Json_AccessDeniedNodeIncludedWithFlag()
	{
		using var temp = new TemporaryDirectory();

		var root = new TreeNodeDescriptor(
			"Root", temp.Path, true, false, "folder",
			new List<TreeNodeDescriptor>
			{
				new("protected", Path.Combine(temp.Path, "protected"), true, true, "folder", new List<TreeNodeDescriptor>())
			});

		var service = CreateService();
		var export = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(export);
		var protectedDir = doc.RootElement.GetProperty("root").GetProperty("dirs")[0];
		Assert.Equal("protected", protectedDir.GetProperty("name").GetString());
		Assert.True(protectedDir.GetProperty("accessDenied").GetBoolean());
	}

	[Fact]
	public void Build_Json_LargeFileContentIncluded()
	{
		using var temp = new TemporaryDirectory();
		var largeContent = new string('x', 100_000);
		var largeFile = temp.CreateFile("large.txt", largeContent);
		var root = CreateRoot(temp.Path, largeFile, "large.txt");
		var service = CreateService();

		var export = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		var (jsonPart, contentPart) = SplitJsonAndContent(export);

		using var doc = JsonDocument.Parse(jsonPart);
		Assert.Equal("large.txt", doc.RootElement.GetProperty("root").GetProperty("files")[0].GetString());
		Assert.Contains(new string('x', 1000), contentPart);
	}

	[Fact]
	public void Build_Json_NewlinesInContentPreserved()
	{
		using var temp = new TemporaryDirectory();
		var multilineContent = "line1\nline2\r\nline3\rline4";
		var file = temp.CreateFile("multiline.txt", multilineContent);
		var root = CreateRoot(temp.Path, file, "multiline.txt");
		var service = CreateService();

		var export = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		var (_, contentPart) = SplitJsonAndContent(export);
		Assert.Contains("line1", contentPart);
		Assert.Contains("line2", contentPart);
		Assert.Contains("line3", contentPart);
		Assert.Contains("line4", contentPart);
	}

	[Fact]
	public void Build_Ascii_SelectingDirectoryIncludesAllDescendantFilesInContent()
	{
		using var temp = new TemporaryDirectory();
		var srcDir = temp.CreateFolder("src");
		var file1 = temp.CreateFile(Path.Combine("src", "a.cs"), "class A {}");
		var file2 = temp.CreateFile(Path.Combine("src", "b.cs"), "class B {}");
		var outsideFile = temp.CreateFile("outside.cs", "class Outside {}");

		var root = new TreeNodeDescriptor(
			"Root", temp.Path, true, false, "folder",
			new List<TreeNodeDescriptor>
			{
				new("src", srcDir, true, false, "folder",
					new List<TreeNodeDescriptor>
					{
						new("a.cs", file1, false, false, "csharp", new List<TreeNodeDescriptor>()),
						new("b.cs", file2, false, false, "csharp", new List<TreeNodeDescriptor>())
					}),
				new("outside.cs", outsideFile, false, false, "csharp", new List<TreeNodeDescriptor>())
			});

		var service = CreateService();
		var selected = new HashSet<string> { srcDir };
		var export = service.Build(temp.Path, root, selected, TreeTextFormat.Ascii);

		Assert.Contains("src", export);
		Assert.DoesNotContain("class A", export);
		Assert.DoesNotContain("class B", export);
		Assert.DoesNotContain("class Outside", export);
	}

	[Fact]
	public void Build_Json_TabsAndSpecialWhitespacePreserved()
	{
		using var temp = new TemporaryDirectory();
		var content = "\tindented\n\t\tdouble indented\n    spaces";
		var file = temp.CreateFile("indented.py", content);
		var root = CreateRoot(temp.Path, file, "indented.py");
		var service = CreateService();

		var export = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		var (_, contentPart) = SplitJsonAndContent(export);
		Assert.Contains("\tindented", contentPart);
		Assert.Contains("\t\tdouble indented", contentPart);
		Assert.Contains("    spaces", contentPart);
	}
}
