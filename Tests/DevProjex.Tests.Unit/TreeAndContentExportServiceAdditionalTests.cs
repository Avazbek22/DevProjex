using System.Collections.Generic;
using System.IO;
using DevProjex.Application.Services;
using DevProjex.Kernel.Contracts;
using DevProjex.Tests.Unit.Helpers;
using Xunit;

namespace DevProjex.Tests.Unit;

public sealed class TreeAndContentExportServiceAdditionalTests
{
	[Fact]
	// Verifies full tree export is used when no selection exists.
	public void Build_NoSelection_UsesFullTree()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("alpha.txt", "Alpha");
		var root = BuildTree(temp.Path, file);
		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService(new FileContentAnalyzer()));

		var output = service.Build(temp.Path, root, new HashSet<string>());

		Assert.Contains($"{temp.Path}:", output);
		Assert.Contains("├── Root", output);
	}

	[Fact]
	// Verifies selected export includes selected file content.
	public void Build_WithSelection_IncludesContent()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("alpha.txt", "Alpha");
		var root = BuildTree(temp.Path, file);
		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService(new FileContentAnalyzer()));
		var selected = new HashSet<string> { file };

		var output = service.Build(temp.Path, root, selected);

		Assert.Contains($"{file}:", output);
		Assert.Contains("Alpha", output);
	}

	[Fact]
	// Verifies selection that only includes missing files falls back to full tree.
	public void Build_WithMissingSelection_FallsBackToFullTree()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("alpha.txt", "Alpha");
		var root = BuildTree(temp.Path, file);
		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService(new FileContentAnalyzer()));
		var selected = new HashSet<string> { Path.Combine(temp.Path, "missing.txt") };

		var output = service.Build(temp.Path, root, selected);

		Assert.Contains("├── Root", output);
		Assert.DoesNotContain("missing.txt:", output);
	}

	[Fact]
	// Verifies selected export omits content when files are empty.
	public void Build_WithEmptyFileContent_ReturnsTreeOnly()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("alpha.txt", string.Empty);
		var root = BuildTree(temp.Path, file);
		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService(new FileContentAnalyzer()));
		var selected = new HashSet<string> { file };

		var output = service.Build(temp.Path, root, selected);

		Assert.Contains("├── Root", output);
		Assert.Contains($"{file}:", output);
		Assert.Contains("[No Content, 0 bytes]", output);
	}

	[Fact]
	// Verifies selected export uses selected tree when selection exists.
	public void Build_WithSelection_UsesSelectedTree()
	{
		using var temp = new TemporaryDirectory();
		var alpha = temp.CreateFile("alpha.txt", "Alpha");
		var beta = temp.CreateFile("beta.txt", "Beta");
		var root = BuildTree(temp.Path, alpha, beta);
		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService(new FileContentAnalyzer()));
		var selected = new HashSet<string> { beta };

		var output = service.Build(temp.Path, root, selected);

		Assert.Contains("Beta", output);
		Assert.DoesNotContain("Alpha", output);
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
