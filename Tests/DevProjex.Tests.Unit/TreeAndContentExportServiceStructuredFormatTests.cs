namespace DevProjex.Tests.Unit;

public sealed class TreeAndContentExportServiceStructuredFormatTests
{
	[Theory]
	[InlineData(TreeTextFormat.Xml)]
	[InlineData(TreeTextFormat.Markdown)]
	public void Build_WithStructuredTreeFormat_KeepsTreeBlockSeparateFromPlainTextContent(TreeTextFormat format)
	{
		using var temp = new TemporaryDirectory();
		var appFile = temp.CreateFile(Path.Combine("src", "App.cs"), "public sealed class App {}");
		var readmeFile = temp.CreateFile("README.md", "# Readme");
		var root = new TreeNodeDescriptor(
			"Project",
			temp.Path,
			true,
			false,
			"folder",
			[
				new("src", Path.Combine(temp.Path, "src"), true, false, "folder",
				[
					new("App.cs", appFile, false, false, "csharp", [])
				]),
				new("README.md", readmeFile, false, false, "markdown", [])
			]);
		var service = CreateService();

		var result = service.Build(temp.Path, root, new HashSet<string>(), format);

		var (treePart, contentPart) = SplitTreeAndContent(result);
		if (format == TreeTextFormat.Xml)
		{
			var document = XmlTreeExportTestHelper.Parse(treePart);
			Assert.Equal(["src/App.cs", "README.md"], XmlTreeExportTestHelper.ExtractFilePaths(document));
			Assert.DoesNotContain("public sealed class App", treePart, StringComparison.Ordinal);
		}
		else
		{
			Assert.Equal(["src/App.cs", "README.md"], MarkdownTreeExportTestHelper.ExtractFilePaths(treePart));
			Assert.DoesNotContain("public sealed class App", treePart, StringComparison.Ordinal);
		}

		Assert.Contains("src/App.cs:", contentPart, StringComparison.Ordinal);
		Assert.Contains("README.md:", contentPart, StringComparison.Ordinal);
		Assert.DoesNotContain(temp.Path.Replace('\\', '/'), contentPart.Replace('\\', '/'), StringComparison.Ordinal);
		Assert.Contains("public sealed class App {}", contentPart, StringComparison.Ordinal);
		Assert.Contains("# Readme", contentPart, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(TreeTextFormat.Xml, "<t ")]
	[InlineData(TreeTextFormat.Markdown, "Root: ")]
	public void Build_WithStructuredTreeFormat_NoTextContentReturnsTreeOnly(TreeTextFormat format, string expectedStart)
	{
		using var temp = new TemporaryDirectory();
		var binary = temp.CreateBinaryFile("image.bin", [0, 1, 2, 3, 4, 255]);
		var root = new TreeNodeDescriptor(
			"Project",
			temp.Path,
			true,
			false,
			"folder",
			[new("image.bin", binary, false, false, "binary", [])]);
		var service = CreateService();

		var result = service.Build(temp.Path, root, new HashSet<string>(), format);

		Assert.StartsWith(expectedStart, result, StringComparison.Ordinal);
		Assert.DoesNotContain("\u00A0", result, StringComparison.Ordinal);
	}

	private static TreeAndContentExportService CreateService()
		=> new(
			new TreeExportService(),
			new SelectedContentExportService(new FileContentAnalyzer()));

	private static (string TreePart, string ContentPart) SplitTreeAndContent(string export)
	{
		var separatorIndex = export.IndexOf("\u00A0", StringComparison.Ordinal);
		if (separatorIndex < 0)
			return (export, string.Empty);

		return (export[..separatorIndex].TrimEnd('\r', '\n'), export[separatorIndex..]);
	}
}
