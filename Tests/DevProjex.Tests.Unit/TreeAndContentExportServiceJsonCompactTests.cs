namespace DevProjex.Tests.Unit;

public sealed class TreeAndContentExportServiceJsonCompactTests
{
	[Fact]
	public void Build_WithJsonFormat_UsesCompactTreeShapeAndPlainTextContent()
	{
		using var temp = new TemporaryDirectory();
		var first = temp.CreateFile("a.txt", "A");
		var second = temp.CreateFile("b.txt", "B");

		var root = new TreeNodeDescriptor(
			"root",
			temp.Path,
			true,
			false,
			"folder",
			[
				new("a.txt", first, false, false, "text", []),
				new("b.txt", second, false, false, "text", [])
			]);

		var service = CreateService();

		var result = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		var (jsonPart, contentPart) = SplitJsonAndContent(result);
		using var document = JsonDocument.Parse(jsonPart);
		var tree = JsonTreeExportTestHelper.GetTree(document);

		JsonTreeExportTestHelper.AssertOnlyRootPathAndTree(document.RootElement);
		Assert.Equal(["a.txt", "b.txt"], tree.GetProperty("/").EnumerateArray().Select(static item => item.GetString()!).ToArray());
		Assert.False(document.RootElement.TryGetProperty("root", out _));
		Assert.Contains("a.txt:", contentPart, StringComparison.Ordinal);
		Assert.Contains("A", contentPart, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_WithJsonFormat_SelectionFiltersTreeAndContent()
	{
		using var temp = new TemporaryDirectory();
		var first = temp.CreateFile("first.txt", "first");
		var second = temp.CreateFile("second.txt", "second");

		var root = new TreeNodeDescriptor(
			"root",
			temp.Path,
			true,
			false,
			"folder",
			[
				new("first.txt", first, false, false, "text", []),
				new("second.txt", second, false, false, "text", [])
			]);

		var service = CreateService();
		var selected = new HashSet<string>(PathComparer.Default) { first };

		var result = service.Build(temp.Path, root, selected, TreeTextFormat.Json);

		var (jsonPart, contentPart) = SplitJsonAndContent(result);
		using var document = JsonDocument.Parse(jsonPart);
		var paths = JsonTreeExportTestHelper.ExtractFilePaths(JsonTreeExportTestHelper.GetTree(document));

		Assert.Equal(["first.txt"], paths);
		Assert.Contains("first.txt:", contentPart, StringComparison.Ordinal);
		Assert.Contains("first", contentPart, StringComparison.Ordinal);
		Assert.DoesNotContain("second.txt:", contentPart, StringComparison.Ordinal);
		Assert.DoesNotContain("second", contentPart, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_WithJsonFormat_NoTextContentReturnsTreeDocumentOnly()
	{
		using var temp = new TemporaryDirectory();
		var binary = temp.CreateBinaryFile("image.bin", [0, 1, 2, 3, 4, 255]);

		var root = new TreeNodeDescriptor(
			"root",
			temp.Path,
			true,
			false,
			"folder",
			[
				new("image.bin", binary, false, false, "binary", [])
			]);

		var service = CreateService();

		var result = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		using var document = JsonDocument.Parse(result);
		var tree = JsonTreeExportTestHelper.GetTree(document);
		Assert.Equal(["image.bin"], tree.GetProperty("/").EnumerateArray().Select(static item => item.GetString()!).ToArray());
	}

	[Fact]
	public void Build_WithJsonFormat_ContentHeadersUseRelativeForwardSlashPaths()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}");
		var root = new TreeNodeDescriptor(
			"root",
			temp.Path,
			true,
			false,
			"folder",
			[
				new(
					"src",
					Path.Combine(temp.Path, "src"),
					true,
					false,
					"folder",
					[
						new("App.cs", file, false, false, "csharp", [])
					])
			]);

		var service = CreateService();

		var result = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		var (_, contentPart) = SplitJsonAndContent(result);
		Assert.Contains("src/App.cs:", contentPart, StringComparison.Ordinal);
		Assert.DoesNotContain(temp.Path.Replace('\\', '/'), contentPart.Replace('\\', '/'), StringComparison.Ordinal);
	}

	private static TreeAndContentExportService CreateService()
		=> new(
			new TreeExportService(),
			new SelectedContentExportService(new FileContentAnalyzer()));

	private static (string JsonPart, string ContentPart) SplitJsonAndContent(string export)
	{
		var separatorIndex = export.IndexOf("\u00A0", StringComparison.Ordinal);
		if (separatorIndex < 0)
			return (export, string.Empty);

		var jsonPart = export[..separatorIndex].TrimEnd('\r', '\n');
		var contentPart = export[separatorIndex..];
		return (jsonPart, contentPart);
	}
}
