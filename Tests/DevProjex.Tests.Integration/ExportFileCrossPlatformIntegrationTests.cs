using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DevProjex.Application.Services;
using DevProjex.Kernel.Contracts;
using DevProjex.Kernel.Models;
using DevProjex.Tests.Integration.Helpers;
using Xunit;

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
	public async Task ExportJsonTreeAndContentToFile_WritesValidJsonPayload()
	{
		using var temp = new TemporaryDirectory();
		var fixture = CreateSampleFixture(temp);
		var treeAndContent = new TreeAndContentExportService(
			new TreeExportService(),
			new SelectedContentExportService(new FileContentAnalyzer()));
		var fileExport = new TextFileExportService();
		var payload = treeAndContent.Build(temp.Path, fixture.Root, new HashSet<string>(), TreeTextFormat.Json);
		var exportPath = Path.Combine(temp.Path, "tree_content.json");

		await using (var stream = new FileStream(exportPath, FileMode.Create, FileAccess.Write, FileShare.Read))
			await fileExport.WriteAsync(stream, payload);

		using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(exportPath, Encoding.UTF8));
		Assert.Equal(Path.GetFullPath(temp.Path), doc.RootElement.GetProperty("rootPath").GetString());
		Assert.True(doc.RootElement.TryGetProperty("tree", out var tree));
		Assert.Equal("Root", tree.GetProperty("name").GetString());
		Assert.Contains("main.cs", doc.RootElement.GetProperty("content").GetString() ?? string.Empty);
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
}
