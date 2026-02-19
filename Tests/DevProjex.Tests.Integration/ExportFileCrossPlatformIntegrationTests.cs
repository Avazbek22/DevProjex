namespace DevProjex.Tests.Integration;

public sealed class ExportFileCrossPlatformIntegrationTests
{
	[Fact]
	public async Task ExportAsciiTreeToFile_WritesUtf8WithoutBomAndRoundTrips()
	{
		using var temp = new TemporaryDirectory();
		var fixture = CreateSampleFixture(temp);
		var treeExport = new TreeExportService();
		var fileExport = new TextFileExportService();
		var treeText = treeExport.BuildFullTree(temp.Path, fixture.Root, TreeTextFormat.Ascii);
		var exportPath = Path.Combine(temp.Path, "tree.txt");

		await using (var stream = new FileStream(exportPath, FileMode.Create, FileAccess.Write, FileShare.Read))
			await fileExport.WriteAsync(stream, treeText);

		var bytes = await File.ReadAllBytesAsync(exportPath);
		Assert.False(StartsWithUtf8Bom(bytes));
		Assert.Equal(treeText, Encoding.UTF8.GetString(bytes));
		Assert.Contains(Environment.NewLine, treeText);
	}

	[Fact]
	public async Task ExportJsonTreeToFile_WritesValidJsonWithoutBom()
	{
		using var temp = new TemporaryDirectory();
		var fixture = CreateSampleFixture(temp);
		var treeExport = new TreeExportService();
		var fileExport = new TextFileExportService();
		var json = treeExport.BuildFullTree(temp.Path, fixture.Root, TreeTextFormat.Json);
		var exportPath = Path.Combine(temp.Path, "tree.json");

		await using (var stream = new FileStream(exportPath, FileMode.Create, FileAccess.Write, FileShare.Read))
			await fileExport.WriteAsync(stream, json);

		var bytes = await File.ReadAllBytesAsync(exportPath);
		Assert.False(StartsWithUtf8Bom(bytes));

		using var doc = JsonDocument.Parse(bytes);
		Assert.Equal(Path.GetFullPath(temp.Path), doc.RootElement.GetProperty("rootPath").GetString());
		Assert.Equal(".", doc.RootElement.GetProperty("root").GetProperty("path").GetString());
	}

	[Fact]
	public void ExportJsonTree_UsesForwardSlashInAllTreePaths()
	{
		using var temp = new TemporaryDirectory();
		var fixture = CreateSampleFixture(temp);
		var treeExport = new TreeExportService();
		var json = treeExport.BuildFullTree(temp.Path, fixture.Root, TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement.GetProperty("root");
		foreach (var path in EnumerateTreePaths(root))
			Assert.DoesNotContain("\\", path);
	}

	[Fact]
	public async Task ExportJsonTreeAndContentToFile_WritesJsonTreeAndTextContent()
	{
		using var temp = new TemporaryDirectory();
		var fixture = CreateSampleFixture(temp);
		var treeAndContent = new TreeAndContentExportService(
			new TreeExportService(),
			new SelectedContentExportService(new FileContentAnalyzer()));
		var fileExport = new TextFileExportService();
		var payload = treeAndContent.Build(temp.Path, fixture.Root, new HashSet<string>(), TreeTextFormat.Json);
		var exportPath = Path.Combine(temp.Path, "tree_content.txt");

		await using (var stream = new FileStream(exportPath, FileMode.Create, FileAccess.Write, FileShare.Read))
			await fileExport.WriteAsync(stream, payload);

		var content = await File.ReadAllTextAsync(exportPath, Encoding.UTF8);

		// JSON tree + separator (NBSP lines) + text content
		// Find separator (NBSP = \u00A0)
		var separatorIndex = content.IndexOf("\u00A0", StringComparison.Ordinal);
		Assert.True(separatorIndex > 0, "Separator not found");

		var jsonPart = content[..separatorIndex].TrimEnd('\r', '\n');
		var textPart = content[separatorIndex..];

		using var doc = JsonDocument.Parse(jsonPart);
		Assert.Equal(Path.GetFullPath(temp.Path), doc.RootElement.GetProperty("rootPath").GetString());
		Assert.True(doc.RootElement.TryGetProperty("root", out var tree));
		Assert.Equal("Root", tree.GetProperty("name").GetString());
		Assert.Contains("main.cs", textPart);
	}

	[Fact]
	public async Task ExportAsciiTreeAndContentToFile_PreservesPlatformLineEndings()
	{
		using var temp = new TemporaryDirectory();
		var fixture = CreateSampleFixture(temp);
		var treeAndContent = new TreeAndContentExportService(
			new TreeExportService(),
			new SelectedContentExportService(new FileContentAnalyzer()));
		var fileExport = new TextFileExportService();
		var payload = treeAndContent.Build(temp.Path, fixture.Root, new HashSet<string>(), TreeTextFormat.Ascii);
		var exportPath = Path.Combine(temp.Path, "tree_content.txt");

		await using (var stream = new FileStream(exportPath, FileMode.Create, FileAccess.Write, FileShare.Read))
			await fileExport.WriteAsync(stream, payload);

		var written = await File.ReadAllTextAsync(exportPath, Encoding.UTF8);
		Assert.Equal(payload, written);
		Assert.Contains(Environment.NewLine, written);
	}

	[Fact]
	public void ExportJsonSelectedTree_ContainsOnlySelectedFile()
	{
		using var temp = new TemporaryDirectory();
		var fixture = CreateSampleFixture(temp);
		var treeExport = new TreeExportService();
		var selected = new HashSet<string> { fixture.MainFilePath };
		var json = treeExport.BuildSelectedTree(temp.Path, fixture.Root, selected, TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement.GetProperty("root");
		var srcDir = root.GetProperty("dirs")[0];
		var files = srcDir.GetProperty("files").EnumerateArray().Select(x => x.GetString()).ToArray();
		Assert.Single(files);
		Assert.Equal("main.cs", files[0]);
	}

	[Fact]
	public void ExportAsciiSelectedTree_ContainsOnlySelectedBranch()
	{
		using var temp = new TemporaryDirectory();
		var fixture = CreateSampleFixture(temp);
		var treeExport = new TreeExportService();
		var selected = new HashSet<string> { fixture.MainFilePath };
		var ascii = treeExport.BuildSelectedTree(temp.Path, fixture.Root, selected, TreeTextFormat.Ascii);

		Assert.Contains("main.cs", ascii);
		Assert.DoesNotContain("README.md", ascii);
	}

	[Fact]
	public void ExportAsciiAndJson_ContainSameFileNames()
	{
		using var temp = new TemporaryDirectory();
		var fixture = CreateSampleFixture(temp);
		var treeExport = new TreeExportService();
		var ascii = treeExport.BuildFullTree(temp.Path, fixture.Root, TreeTextFormat.Ascii);
		var json = treeExport.BuildFullTree(temp.Path, fixture.Root, TreeTextFormat.Json);

		Assert.Contains("main.cs", ascii);
		Assert.Contains("README.md", ascii);

		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement.GetProperty("root");
		var rootFiles = root.GetProperty("files").EnumerateArray().Select(x => x.GetString()).ToArray();
		var srcFiles = root.GetProperty("dirs")[0].GetProperty("files").EnumerateArray().Select(x => x.GetString()).ToArray();

		Assert.Contains("README.md", rootFiles);
		Assert.Contains("main.cs", srcFiles);
	}

	[Fact]
	public async Task FileExport_OverwritesExistingFileAndRemovesTailBytes()
	{
		using var temp = new TemporaryDirectory();
		var exportPath = Path.Combine(temp.Path, "output.txt");
		await File.WriteAllTextAsync(exportPath, "old content that should disappear", Encoding.UTF8);

		var fileExport = new TextFileExportService();
		await using (var stream = new FileStream(exportPath, FileMode.Open, FileAccess.Write, FileShare.Read))
			await fileExport.WriteAsync(stream, "new");

		Assert.Equal("new", await File.ReadAllTextAsync(exportPath, Encoding.UTF8));
	}

	[Fact]
	public async Task FileExport_WorksWithUnicodeFileNameAndUnicodeContent()
	{
		using var temp = new TemporaryDirectory();
		var exportPath = Path.Combine(temp.Path, "отчет_дерево.json");
		const string content = "Формат дерева: JSON";

		var fileExport = new TextFileExportService();
		await using (var stream = new FileStream(exportPath, FileMode.Create, FileAccess.Write, FileShare.Read))
			await fileExport.WriteAsync(stream, content);

		Assert.Equal(content, await File.ReadAllTextAsync(exportPath, Encoding.UTF8));
	}

	private static IEnumerable<string> EnumerateTreePaths(JsonElement node)
	{
		if (node.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String)
		{
			var value = pathElement.GetString();
			if (!string.IsNullOrWhiteSpace(value))
				yield return value;
		}

		if (!node.TryGetProperty("dirs", out var dirsElement) || dirsElement.ValueKind != JsonValueKind.Array)
			yield break;

		foreach (var childDir in dirsElement.EnumerateArray())
		{
			foreach (var childPath in EnumerateTreePaths(childDir))
				yield return childPath;
		}
	}

	private static bool StartsWithUtf8Bom(byte[] bytes)
	{
		return bytes.Length >= 3 &&
		       bytes[0] == 0xEF &&
		       bytes[1] == 0xBB &&
		       bytes[2] == 0xBF;
	}

	private static ExportFixture CreateSampleFixture(TemporaryDirectory temp)
	{
		var srcPath = temp.CreateDirectory("src");
		var mainPath = temp.CreateFile(Path.Combine("src", "main.cs"), "class C {}\n");
		var readmePath = temp.CreateFile("README.md", "# title\n");

		var root = new TreeNodeDescriptor(
			"Root",
			temp.Path,
			true,
			false,
			"folder",
			new List<TreeNodeDescriptor>
			{
				new(
					"src",
					srcPath,
					true,
					false,
					"folder",
					new List<TreeNodeDescriptor>
					{
						new("main.cs", mainPath, false, false, "csharp", new List<TreeNodeDescriptor>())
					}),
				new("README.md", readmePath, false, false, "markdown", new List<TreeNodeDescriptor>())
			});

		return new ExportFixture(root, mainPath);
	}

	private sealed record ExportFixture(TreeNodeDescriptor Root, string MainFilePath);

	[Fact]
	public async Task ExportJsonTreeAndContent_DeeplyNestedStructure_PreservesFullHierarchy()
	{
		using var temp = new TemporaryDirectory();
		var level1 = temp.CreateDirectory("src");
		var level2 = temp.CreateDirectory(Path.Combine("src", "features"));
		var level3 = temp.CreateDirectory(Path.Combine("src", "features", "auth"));
		var level4 = temp.CreateDirectory(Path.Combine("src", "features", "auth", "hooks"));
		var deepFile = temp.CreateFile(Path.Combine("src", "features", "auth", "hooks", "useAuth.ts"), "export const useAuth = () => {};");

		var root = new TreeNodeDescriptor(
			"Root", temp.Path, true, false, "folder",
			new List<TreeNodeDescriptor>
			{
				new("src", level1, true, false, "folder",
					new List<TreeNodeDescriptor>
					{
						new("features", level2, true, false, "folder",
							new List<TreeNodeDescriptor>
							{
								new("auth", level3, true, false, "folder",
									new List<TreeNodeDescriptor>
									{
										new("hooks", level4, true, false, "folder",
											new List<TreeNodeDescriptor>
											{
												new("useAuth.ts", deepFile, false, false, "typescript", new List<TreeNodeDescriptor>())
											})
									})
							})
					})
			});

		var treeAndContent = new TreeAndContentExportService(
			new TreeExportService(),
			new SelectedContentExportService(new FileContentAnalyzer()));
		var fileExport = new TextFileExportService();
		var payload = treeAndContent.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);
		var exportPath = Path.Combine(temp.Path, "deep_export.txt");

		await using (var stream = new FileStream(exportPath, FileMode.Create, FileAccess.Write, FileShare.Read))
			await fileExport.WriteAsync(stream, payload);

		var content = await File.ReadAllTextAsync(exportPath, Encoding.UTF8);
		var separatorIndex = content.IndexOf("\u00A0", StringComparison.Ordinal);
		var jsonPart = content[..separatorIndex].TrimEnd('\r', '\n');

		using var doc = JsonDocument.Parse(jsonPart);
		var srcDir = doc.RootElement.GetProperty("root").GetProperty("dirs")[0];
		var featuresDir = srcDir.GetProperty("dirs")[0];
		var authDir = featuresDir.GetProperty("dirs")[0];
		var hooksDir = authDir.GetProperty("dirs")[0];

		Assert.Equal("src/features/auth/hooks", hooksDir.GetProperty("path").GetString());
		Assert.Equal("useAuth.ts", hooksDir.GetProperty("files")[0].GetString());
	}

	[Fact]
	public async Task ExportJsonTreeAndContent_MultipleFilesWithSameName_AllIncluded()
	{
		using var temp = new TemporaryDirectory();
		var mod1 = temp.CreateDirectory("module1");
		var mod2 = temp.CreateDirectory("module2");
		var file1 = temp.CreateFile(Path.Combine("module1", "index.ts"), "// Module 1");
		var file2 = temp.CreateFile(Path.Combine("module2", "index.ts"), "// Module 2");

		var root = new TreeNodeDescriptor(
			"Root", temp.Path, true, false, "folder",
			new List<TreeNodeDescriptor>
			{
				new("module1", mod1, true, false, "folder",
					new List<TreeNodeDescriptor>
					{
						new("index.ts", file1, false, false, "typescript", new List<TreeNodeDescriptor>())
					}),
				new("module2", mod2, true, false, "folder",
					new List<TreeNodeDescriptor>
					{
						new("index.ts", file2, false, false, "typescript", new List<TreeNodeDescriptor>())
					})
			});

		var treeAndContent = new TreeAndContentExportService(
			new TreeExportService(),
			new SelectedContentExportService(new FileContentAnalyzer()));

		var payload = treeAndContent.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		Assert.Contains("// Module 1", payload);
		Assert.Contains("// Module 2", payload);
		Assert.Contains("module1", payload);
		Assert.Contains("module2", payload);
	}

	[Fact]
	public async Task ExportJsonTreeAndContent_EmptyAndWhitespaceFiles_ShowsMarkers()
	{
		using var temp = new TemporaryDirectory();
		var emptyFile = temp.CreateFile("empty.txt", "");
		var whitespaceFile = temp.CreateFile("whitespace.txt", "   \n\t\n   ");
		var normalFile = temp.CreateFile("normal.txt", "Hello World");

		var root = new TreeNodeDescriptor(
			"Root", temp.Path, true, false, "folder",
			new List<TreeNodeDescriptor>
			{
				new("empty.txt", emptyFile, false, false, "text", new List<TreeNodeDescriptor>()),
				new("whitespace.txt", whitespaceFile, false, false, "text", new List<TreeNodeDescriptor>()),
				new("normal.txt", normalFile, false, false, "text", new List<TreeNodeDescriptor>())
			});

		var treeAndContent = new TreeAndContentExportService(
			new TreeExportService(),
			new SelectedContentExportService(new FileContentAnalyzer()));
		var fileExport = new TextFileExportService();
		var payload = treeAndContent.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);
		var exportPath = Path.Combine(temp.Path, "markers_export.txt");

		await using (var stream = new FileStream(exportPath, FileMode.Create, FileAccess.Write, FileShare.Read))
			await fileExport.WriteAsync(stream, payload);

		var content = await File.ReadAllTextAsync(exportPath, Encoding.UTF8);

		Assert.Contains("[No Content, 0 bytes]", content);
		Assert.Contains("[Whitespace,", content);
		Assert.Contains("Hello World", content);
	}

	[Fact]
	public async Task ExportAsciiTreeAndContent_LargeFile_PreservesAllContent()
	{
		using var temp = new TemporaryDirectory();
		var largeContent = string.Join("\n", Enumerable.Range(1, 10000).Select(i => $"Line {i}: Some content here"));
		var largeFile = temp.CreateFile("large.log", largeContent);

		var root = new TreeNodeDescriptor(
			"Root", temp.Path, true, false, "folder",
			new List<TreeNodeDescriptor>
			{
				new("large.log", largeFile, false, false, "text", new List<TreeNodeDescriptor>())
			});

		var treeAndContent = new TreeAndContentExportService(
			new TreeExportService(),
			new SelectedContentExportService(new FileContentAnalyzer()));
		var fileExport = new TextFileExportService();
		var payload = treeAndContent.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Ascii);
		var exportPath = Path.Combine(temp.Path, "large_export.txt");

		await using (var stream = new FileStream(exportPath, FileMode.Create, FileAccess.Write, FileShare.Read))
			await fileExport.WriteAsync(stream, payload);

		var content = await File.ReadAllTextAsync(exportPath, Encoding.UTF8);

		Assert.Contains("Line 1:", content);
		Assert.Contains("Line 5000:", content);
		Assert.Contains("Line 10000:", content);
	}

	[Fact]
	public async Task ExportJsonTree_SelectionOfMultipleScatteredFiles_IncludesOnlySelected()
	{
		using var temp = new TemporaryDirectory();
		var srcDir = temp.CreateDirectory("src");
		var testDir = temp.CreateDirectory("tests");

		var srcFile1 = temp.CreateFile(Path.Combine("src", "app.ts"), "const app = 1;");
		var srcFile2 = temp.CreateFile(Path.Combine("src", "utils.ts"), "const utils = 2;");
		var testFile1 = temp.CreateFile(Path.Combine("tests", "app.test.ts"), "test('app', () => {});");
		var testFile2 = temp.CreateFile(Path.Combine("tests", "utils.test.ts"), "test('utils', () => {});");

		var root = new TreeNodeDescriptor(
			"Root", temp.Path, true, false, "folder",
			new List<TreeNodeDescriptor>
			{
				new("src", srcDir, true, false, "folder",
					new List<TreeNodeDescriptor>
					{
						new("app.ts", srcFile1, false, false, "typescript", new List<TreeNodeDescriptor>()),
						new("utils.ts", srcFile2, false, false, "typescript", new List<TreeNodeDescriptor>())
					}),
				new("tests", testDir, true, false, "folder",
					new List<TreeNodeDescriptor>
					{
						new("app.test.ts", testFile1, false, false, "typescript", new List<TreeNodeDescriptor>()),
						new("utils.test.ts", testFile2, false, false, "typescript", new List<TreeNodeDescriptor>())
					})
			});

		var treeExport = new TreeExportService();
		var selected = new HashSet<string> { srcFile1, testFile2 };
		var json = treeExport.BuildSelectedTree(temp.Path, root, selected, TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(json);
		var rootNode = doc.RootElement.GetProperty("root");
		var dirs = rootNode.GetProperty("dirs").EnumerateArray().ToList();

		var srcNode = dirs.First(d => d.GetProperty("name").GetString() == "src");
		var srcFiles = srcNode.GetProperty("files").EnumerateArray().Select(f => f.GetString()).ToList();
		Assert.Single(srcFiles);
		Assert.Equal("app.ts", srcFiles[0]);

		var testsNode = dirs.First(d => d.GetProperty("name").GetString() == "tests");
		var testFiles = testsNode.GetProperty("files").EnumerateArray().Select(f => f.GetString()).ToList();
		Assert.Single(testFiles);
		Assert.Equal("utils.test.ts", testFiles[0]);
	}

	[Fact]
	public async Task ExportJsonTreeAndContent_UnicodeInPathsAndContent_PreservedCorrectly()
	{
		using var temp = new TemporaryDirectory();
		var unicodeDir = temp.CreateDirectory("проект");
		var unicodeFile = temp.CreateFile(Path.Combine("проект", "файл.txt"), "Содержимое на русском языке 中文 日本語");

		var root = new TreeNodeDescriptor(
			"Root", temp.Path, true, false, "folder",
			new List<TreeNodeDescriptor>
			{
				new("проект", unicodeDir, true, false, "folder",
					new List<TreeNodeDescriptor>
					{
						new("файл.txt", unicodeFile, false, false, "text", new List<TreeNodeDescriptor>())
					})
			});

		var treeAndContent = new TreeAndContentExportService(
			new TreeExportService(),
			new SelectedContentExportService(new FileContentAnalyzer()));
		var fileExport = new TextFileExportService();
		var payload = treeAndContent.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);
		var exportPath = Path.Combine(temp.Path, "unicode_export.txt");

		await using (var stream = new FileStream(exportPath, FileMode.Create, FileAccess.Write, FileShare.Read))
			await fileExport.WriteAsync(stream, payload);

		var content = await File.ReadAllTextAsync(exportPath, Encoding.UTF8);

		var separatorIndex = content.IndexOf("\u00A0", StringComparison.Ordinal);
		var jsonPart = content[..separatorIndex].TrimEnd('\r', '\n');

		using var doc = JsonDocument.Parse(jsonPart);
		var projectDir = doc.RootElement.GetProperty("root").GetProperty("dirs")[0];
		Assert.Equal("проект", projectDir.GetProperty("name").GetString());
		Assert.Equal("файл.txt", projectDir.GetProperty("files")[0].GetString());

		Assert.Contains("Содержимое на русском языке", content);
		Assert.Contains("中文", content);
		Assert.Contains("日本語", content);
	}

	[Fact]
	public async Task ExportJsonTree_ManyFiles_PerformanceReasonable()
	{
		using var temp = new TemporaryDirectory();
		var children = new List<TreeNodeDescriptor>();

		for (int i = 0; i < 500; i++)
		{
			var filePath = temp.CreateFile($"file{i:D4}.txt", $"Content of file {i}");
			children.Add(new TreeNodeDescriptor($"file{i:D4}.txt", filePath, false, false, "text", new List<TreeNodeDescriptor>()));
		}

		var root = new TreeNodeDescriptor("Root", temp.Path, true, false, "folder", children);

		var treeExport = new TreeExportService();
		var stopwatch = Stopwatch.StartNew();
		var json = treeExport.BuildFullTree(temp.Path, root, TreeTextFormat.Json);
		stopwatch.Stop();

		using var doc = JsonDocument.Parse(json);
		var files = doc.RootElement.GetProperty("root").GetProperty("files");
		Assert.Equal(500, files.GetArrayLength());
		Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"Export took too long: {stopwatch.ElapsedMilliseconds}ms");
	}

	[Fact]
	public async Task ExportAsciiAndJson_SameStructure_ProducesConsistentFileList()
	{
		using var temp = new TemporaryDirectory();
		var srcDir = temp.CreateDirectory("src");
		var file1 = temp.CreateFile(Path.Combine("src", "main.cs"), "class Main {}");
		var file2 = temp.CreateFile("README.md", "# Title");

		var root = new TreeNodeDescriptor(
			"Root", temp.Path, true, false, "folder",
			new List<TreeNodeDescriptor>
			{
				new("src", srcDir, true, false, "folder",
					new List<TreeNodeDescriptor>
					{
						new("main.cs", file1, false, false, "csharp", new List<TreeNodeDescriptor>())
					}),
				new("README.md", file2, false, false, "markdown", new List<TreeNodeDescriptor>())
			});

		var treeExport = new TreeExportService();
		var ascii = treeExport.BuildFullTree(temp.Path, root, TreeTextFormat.Ascii);
		var json = treeExport.BuildFullTree(temp.Path, root, TreeTextFormat.Json);

		Assert.Contains("main.cs", ascii);
		Assert.Contains("README.md", ascii);
		Assert.Contains("src", ascii);

		using var doc = JsonDocument.Parse(json);
		var rootNode = doc.RootElement.GetProperty("root");
		Assert.Equal("README.md", rootNode.GetProperty("files")[0].GetString());
		Assert.Equal("main.cs", rootNode.GetProperty("dirs")[0].GetProperty("files")[0].GetString());
	}

	[Fact]
	public async Task ExportToFile_FileModifiedAfterExport_IndependentContent()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("mutable.txt", "Original content");

		var root = new TreeNodeDescriptor(
			"Root", temp.Path, true, false, "folder",
			new List<TreeNodeDescriptor>
			{
				new("mutable.txt", file, false, false, "text", new List<TreeNodeDescriptor>())
			});

		var treeAndContent = new TreeAndContentExportService(
			new TreeExportService(),
			new SelectedContentExportService(new FileContentAnalyzer()));
		var fileExport = new TextFileExportService();
		var payload = treeAndContent.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Ascii);
		var exportPath = Path.Combine(temp.Path, "snapshot.txt");

		await using (var stream = new FileStream(exportPath, FileMode.Create, FileAccess.Write, FileShare.Read))
			await fileExport.WriteAsync(stream, payload);

		await File.WriteAllTextAsync(file, "Modified content");

		var exported = await File.ReadAllTextAsync(exportPath, Encoding.UTF8);
		Assert.Contains("Original content", exported);
		Assert.DoesNotContain("Modified content", exported);
	}

	[Fact]
	public async Task ExportTreeAndContent_BinaryAndTextMixed_OnlyTextInContent()
	{
		using var temp = new TemporaryDirectory();
		var textFile = temp.CreateFile("readme.txt", "Hello World");
		var binaryPath = Path.Combine(temp.Path, "image.png");
		await File.WriteAllBytesAsync(binaryPath, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00 });

		var root = new TreeNodeDescriptor(
			"Root", temp.Path, true, false, "folder",
			new List<TreeNodeDescriptor>
			{
				new("readme.txt", textFile, false, false, "text", new List<TreeNodeDescriptor>()),
				new("image.png", binaryPath, false, false, "image", new List<TreeNodeDescriptor>())
			});

		var treeAndContent = new TreeAndContentExportService(
			new TreeExportService(),
			new SelectedContentExportService(new FileContentAnalyzer()));

		var payload = treeAndContent.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		var separatorIndex = payload.IndexOf("\u00A0", StringComparison.Ordinal);
		var jsonPart = payload[..separatorIndex].TrimEnd('\r', '\n');
		var contentPart = payload[separatorIndex..];

		using var doc = JsonDocument.Parse(jsonPart);
		var files = doc.RootElement.GetProperty("root").GetProperty("files");
		var fileNames = files.EnumerateArray().Select(f => f.GetString()).ToList();
		Assert.Contains("readme.txt", fileNames);
		Assert.Contains("image.png", fileNames);

		Assert.Contains("Hello World", contentPart);
		Assert.DoesNotContain("image.png:", contentPart);
	}

	[Fact]
	public async Task ConcurrentExports_MultipleThreads_NoDataCorruption()
	{
		using var temp = new TemporaryDirectory();
		var files = new List<string>();
		var children = new List<TreeNodeDescriptor>();

		for (int i = 0; i < 10; i++)
		{
			var filePath = temp.CreateFile($"file{i}.txt", $"Content {i}");
			files.Add(filePath);
			children.Add(new TreeNodeDescriptor($"file{i}.txt", filePath, false, false, "text", new List<TreeNodeDescriptor>()));
		}

		var root = new TreeNodeDescriptor("Root", temp.Path, true, false, "folder", children);

		var tasks = Enumerable.Range(0, 5).Select(async threadId =>
		{
			var service = new TreeAndContentExportService(
				new TreeExportService(),
				new SelectedContentExportService(new FileContentAnalyzer()));

			var result = await service.BuildAsync(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json, CancellationToken.None);

			for (int i = 0; i < 10; i++)
			{
				Assert.Contains($"Content {i}", result);
			}

			return result;
		}).ToList();

		var results = await Task.WhenAll(tasks);
		Assert.Equal(5, results.Length);
		Assert.All(results, r => Assert.Contains("file0.txt", r));
	}
}
