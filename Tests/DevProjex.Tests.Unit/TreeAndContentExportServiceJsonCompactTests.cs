using System.Collections.Generic;
using System.Text.Json;
using DevProjex.Application.Services;
using DevProjex.Kernel.Contracts;
using DevProjex.Kernel.Models;
using DevProjex.Tests.Unit.Helpers;
using Xunit;

namespace DevProjex.Tests.Unit;

public sealed class TreeAndContentExportServiceJsonCompactTests
{
	[Fact]
	public void Build_WithJsonFormat_UsesCompactTreeShape()
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
			new List<TreeNodeDescriptor>
			{
				new("a.txt", first, false, false, "text", new List<TreeNodeDescriptor>()),
				new("b.txt", second, false, false, "text", new List<TreeNodeDescriptor>())
			});

		var service = new TreeAndContentExportService(
			new TreeExportService(),
			new SelectedContentExportService(new FileContentAnalyzer()));

		var result = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(result);
		var tree = doc.RootElement.GetProperty("tree");
		Assert.Equal("root", tree.GetProperty("name").GetString());
		Assert.Equal(".", tree.GetProperty("path").GetString());
		Assert.True(tree.TryGetProperty("files", out _));
		Assert.False(tree.TryGetProperty("fullPath", out _));
		Assert.False(tree.TryGetProperty("children", out _));
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
			new List<TreeNodeDescriptor>
			{
				new("first.txt", first, false, false, "text", new List<TreeNodeDescriptor>()),
				new("second.txt", second, false, false, "text", new List<TreeNodeDescriptor>())
			});

		var service = new TreeAndContentExportService(
			new TreeExportService(),
			new SelectedContentExportService(new FileContentAnalyzer()));
		var selected = new HashSet<string> { first };

		var result = service.Build(temp.Path, root, selected, TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(result);
		var files = doc.RootElement.GetProperty("tree").GetProperty("files");
		var content = doc.RootElement.GetProperty("content").GetString() ?? string.Empty;

		Assert.Equal(1, files.GetArrayLength());
		Assert.Equal("first.txt", files[0].GetString());
		Assert.Contains(first, content);
		Assert.DoesNotContain(second, content);
	}

	[Fact]
	public void Build_WithJsonFormat_NoTextContentReturnsTreeDocumentOnly()
	{
		using var temp = new TemporaryDirectory();
		var binary = temp.CreateBinaryFile("image.bin", new byte[] { 0, 1, 2, 3, 4, 255 });

		var root = new TreeNodeDescriptor(
			"root",
			temp.Path,
			true,
			false,
			"folder",
			new List<TreeNodeDescriptor>
			{
				new("image.bin", binary, false, false, "binary", new List<TreeNodeDescriptor>())
			});

		var service = new TreeAndContentExportService(
			new TreeExportService(),
			new SelectedContentExportService(new FileContentAnalyzer()));

		var result = service.Build(temp.Path, root, new HashSet<string>(), TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(result);
		Assert.True(doc.RootElement.TryGetProperty("root", out _));
		Assert.False(doc.RootElement.TryGetProperty("content", out _));
	}
}
