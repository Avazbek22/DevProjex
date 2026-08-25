namespace DevProjex.Tests.Unit;

public sealed class TreeAndContentExportServiceCrossPlatformTests
{
	[Fact]
	public void MapRelativeContentHeaderPath_DotPrefixedDirectoryRemainsInsideRoot()
	{
		using var temp = new TemporaryDirectory();
		var file = Path.Combine(temp.Path, "..cache", "nested", "file.txt");

		var mapped = TreeAndContentExportService.MapRelativeContentHeaderPath(temp.Path, file);

		Assert.Equal("..cache/nested/file.txt", mapped);
		Assert.DoesNotContain('\\', mapped);
	}

	[Fact]
	public void MapRelativeContentHeaderPath_PathOutsideRootFallsBackToFileName()
	{
		using var temp = new TemporaryDirectory();
		var parent = Directory.GetParent(temp.Path)!.FullName;
		var outsideFile = Path.Combine(parent, "outside", "secret.txt");

		var mapped = TreeAndContentExportService.MapRelativeContentHeaderPath(temp.Path, outsideFile);

		Assert.Equal("secret.txt", mapped);
		Assert.DoesNotContain(parent.Replace('\\', '/'), mapped, StringComparison.Ordinal);
	}

	[Fact]
	public void MapRelativeContentHeaderPath_WhitespaceOnlyUnixFileNameRemainsRelative()
	{
		using var temp = new TemporaryDirectory();
		var file = Path.Combine(temp.Path, " ");

		var mapped = TreeAndContentExportService.MapRelativeContentHeaderPath(temp.Path, file);

		Assert.Equal(" ", mapped);
	}

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
		Assert.Equal(Path.GetFullPath(temp.Path).Replace('\\', '/'), doc.RootElement.GetProperty("rootPath").GetString());
		var tree = JsonTreeExportTestHelper.GetTree(doc);
		Assert.Equal(JsonValueKind.Array, tree.GetProperty("/").ValueKind);
		Assert.Equal(["notes.txt"], JsonTreeExportTestHelper.ExtractFilePaths(tree));

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
		Assert.Equal(["notes.txt"], JsonTreeExportTestHelper.ExtractFilePaths(JsonTreeExportTestHelper.GetTree(doc)));
		Assert.Contains("notes.txt:", contentPart, StringComparison.Ordinal);
		Assert.DoesNotContain(file, contentPart, StringComparison.Ordinal);
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
	public void Build_Json_DirectorySelectionIncludesDescendantContent()
	{
		using var temp = new TemporaryDirectory();
		var srcFolder = temp.CreateFolder("src");
		var file = temp.CreateFile(Path.Combine("src", "main.cs"), "class C {}");
		var root = CreateRootWithDirectory(temp.Path, srcFolder, file);
		var selected = new HashSet<string> { srcFolder };
		var service = CreateService();

		var export = service.Build(temp.Path, root, selected, TreeTextFormat.Json);

		var (jsonPart, contentPart) = SplitJsonAndContent(export);
		using var doc = JsonDocument.Parse(jsonPart);
		var tree = JsonTreeExportTestHelper.GetTree(doc);
		Assert.Equal(JsonValueKind.Array, tree.GetProperty("src").ValueKind);
		Assert.Equal(["src/main.cs"], JsonTreeExportTestHelper.ExtractFilePaths(tree));
		Assert.Contains("src/main.cs:", contentPart, StringComparison.Ordinal);
		Assert.Contains("class C {}", contentPart, StringComparison.Ordinal);
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
		var tree = JsonTreeExportTestHelper.GetTree(doc);
		Assert.Equal(JsonValueKind.Array, tree.GetProperty("/").ValueKind);
		Assert.Equal(["code.cs"], JsonTreeExportTestHelper.ExtractFilePaths(tree));

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
		Assert.Equal(3, JsonTreeExportTestHelper.CountFiles(JsonTreeExportTestHelper.GetTree(doc)));

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
		var tree = JsonTreeExportTestHelper.GetTree(doc);
		Assert.Equal(JsonValueKind.Array, tree.GetProperty("src").GetProperty("components").GetProperty("ui").ValueKind);
		Assert.Equal(["src/components/ui/Button.tsx"], JsonTreeExportTestHelper.ExtractFilePaths(tree));

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
		var tree = JsonTreeExportTestHelper.GetTree(doc);
		Assert.Equal(JsonValueKind.Array, tree.GetProperty("/").ValueKind);
		Assert.Equal(["empty.txt"], JsonTreeExportTestHelper.ExtractFilePaths(tree));
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
		var paths = JsonTreeExportTestHelper.ExtractFilePaths(JsonTreeExportTestHelper.GetTree(doc));
		Assert.Contains("root.ts", paths);
		Assert.Contains("src/a.ts", paths);
		Assert.DoesNotContain("src/b.ts", paths);
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
		var paths = JsonTreeExportTestHelper.ExtractFilePaths(JsonTreeExportTestHelper.GetTree(doc));
		Assert.Contains("moduleA/index.ts", paths);
		Assert.Contains("moduleB/index.ts", paths);

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
		var tree = JsonTreeExportTestHelper.GetTree(doc);
		Assert.Equal(JsonValueKind.Array, tree.GetProperty("/").ValueKind);
		Assert.Equal(["файл (копия) [1].txt"], JsonTreeExportTestHelper.ExtractFilePaths(tree));
		Assert.Contains("содержимое файла", contentPart);
	}

	[Fact]
	public void Build_Json_BinaryFileExcludedFromContent()
	{
		using var temp = new TemporaryDirectory();
		var textFile = temp.CreateFile("readme.txt", "Hello");
		var binaryFile = temp.CreateBinaryFile("image.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

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
		Assert.Equal(2, JsonTreeExportTestHelper.CountFiles(JsonTreeExportTestHelper.GetTree(doc)));

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
		var empty = JsonTreeExportTestHelper.GetTree(doc).GetProperty("empty");
		Assert.Equal(JsonValueKind.Array, empty.ValueKind);
		Assert.Empty(empty.EnumerateArray());
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
		var tree = JsonTreeExportTestHelper.GetTree(doc);
		Assert.Equal(JsonValueKind.Array, tree.GetProperty("/").ValueKind);
		Assert.Equal(["standalone.cs"], JsonTreeExportTestHelper.ExtractFilePaths(tree));
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
	public void Build_Json_AccessDeniedScannerMetadataIsNotExported()
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
		var protectedDir = JsonTreeExportTestHelper.GetTree(doc).GetProperty("protected");
		Assert.Equal(JsonValueKind.Array, protectedDir.ValueKind);
		Assert.Empty(protectedDir.EnumerateArray());
		Assert.False(export.Contains("accessDenied", StringComparison.Ordinal));
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
		var tree = JsonTreeExportTestHelper.GetTree(doc);
		Assert.Equal(JsonValueKind.Array, tree.GetProperty("/").ValueKind);
		Assert.Equal(["large.txt"], JsonTreeExportTestHelper.ExtractFilePaths(tree));
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
		Assert.Contains("class A", export);
		Assert.Contains("class B", export);
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
