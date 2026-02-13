using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DevProjex.Application.Services;
using DevProjex.Kernel.Contracts;
using DevProjex.Kernel.Models;
using DevProjex.Tests.Unit.Helpers;
using Xunit;

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

		var separator = $"\u00A0{System.Environment.NewLine}\u00A0{System.Environment.NewLine}";
		Assert.Contains(separator, export);
	}

	[Fact]
	public void Build_Json_ContainsRootPathTreeAndContent()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("notes.txt", "hello");
		var root = CreateRoot(temp.Path, file);
		var service = CreateService();

		var export = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(export);
		Assert.Equal(Path.GetFullPath(temp.Path), doc.RootElement.GetProperty("rootPath").GetString());
		Assert.True(doc.RootElement.TryGetProperty("tree", out _));
		Assert.True(doc.RootElement.TryGetProperty("content", out _));
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

		using var doc = JsonDocument.Parse(export);
		Assert.Equal("notes.txt", doc.RootElement.GetProperty("tree").GetProperty("files")[0].GetString());
		Assert.Contains(file, doc.RootElement.GetProperty("content").GetString() ?? string.Empty);
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

		using var doc = JsonDocument.Parse(export);
		Assert.True(doc.RootElement.TryGetProperty("root", out _));
		Assert.False(doc.RootElement.TryGetProperty("content", out _));
	}

	[Fact]
	public void Build_Json_PreservesUnicodeContent()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("unicode.txt", "Привет, мир!");
		var root = CreateRoot(temp.Path, file);
		var service = CreateService();

		var export = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(export);
		Assert.Contains("Привет, мир!", doc.RootElement.GetProperty("content").GetString() ?? string.Empty);
	}

	private static TreeAndContentExportService CreateService()
	{
		return new TreeAndContentExportService(
			new TreeExportService(),
			new SelectedContentExportService(new FileContentAnalyzer()));
	}

	private static TreeNodeDescriptor CreateRoot(string rootPath, string filePath)
	{
		return new TreeNodeDescriptor(
			"Root",
			rootPath,
			true,
			false,
			"folder",
			new List<TreeNodeDescriptor>
			{
				new("notes.txt", filePath, false, false, "text", new List<TreeNodeDescriptor>())
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
}
