using System.Collections.Generic;
using System.IO;
using DevProjex.Application.Services;
using DevProjex.Kernel.Contracts;
using DevProjex.Tests.Unit.Helpers;
using Xunit;

namespace DevProjex.Tests.Unit;

public sealed class TreeAndContentExportServiceMarkerTests
{
	[Fact]
	public void Build_FullTree_IncludesMarkersForEmptyAndWhitespace()
	{
		using var temp = new TemporaryDirectory();
		var empty = temp.CreateFile("empty.txt", string.Empty);
		var whitespace = temp.CreateFile("space.txt", " \n ");
		var text = temp.CreateFile("note.txt", "Note");

		var root = BuildTree(temp.Path, empty, whitespace, text);
		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService());

		var output = service.Build(temp.Path, root, new HashSet<string>());

		Assert.Contains($"{empty}:", output);
		Assert.Contains("[No Content, 0 bytes]", output);
		Assert.Contains($"{whitespace}:", output);
		Assert.Contains("[Whitespace, 3 bytes]", output);
		Assert.Contains($"{text}:", output);
		Assert.Contains("Note", output);
	}

	[Fact]
	public void Build_Selected_IncludesMarkersOnlyForSelected()
	{
		using var temp = new TemporaryDirectory();
		var empty = temp.CreateFile("empty.txt", string.Empty);
		var text = temp.CreateFile("note.txt", "Note");

		var root = BuildTree(temp.Path, empty, text);
		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService());
		var selected = new HashSet<string> { empty };

		var output = service.Build(temp.Path, root, selected);

		Assert.Contains($"{empty}:", output);
		Assert.Contains("[No Content, 0 bytes]", output);
		Assert.DoesNotContain($"{text}:", output);
		Assert.DoesNotContain("Note", output);
	}

	[Fact]
	public void Build_Selected_SkipsBinaryContent()
	{
		using var temp = new TemporaryDirectory();
		var binary = temp.CreateBinaryFile("image.bin", new byte[] { 1, 2, 0, 3 });

		var root = BuildTree(temp.Path, binary);
		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService());
		var selected = new HashSet<string> { binary };

		var output = service.Build(temp.Path, root, selected);

		Assert.Contains("├── Root", output);
		Assert.DoesNotContain($"{binary}:", output);
	}

	private static TreeNodeDescriptor BuildTree(string rootPath, params string[] files)
	{
		var children = new List<TreeNodeDescriptor>();
		foreach (var file in files)
		{
			children.Add(new TreeNodeDescriptor(
				DisplayName: Path.GetFileName(file),
				FullPath: file,
				IsDirectory: false,
				IsAccessDenied: false,
				IconKey: "file",
				Children: new List<TreeNodeDescriptor>()));
		}

		return new TreeNodeDescriptor(
			DisplayName: "Root",
			FullPath: rootPath,
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: children);
	}
}
