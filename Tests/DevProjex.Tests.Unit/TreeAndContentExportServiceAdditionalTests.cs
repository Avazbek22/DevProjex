namespace DevProjex.Tests.Unit;

public sealed class TreeAndContentExportServiceAdditionalTests
{
	[Fact]
	public void CombineTreeAndContent_PreservesBytesWithoutAllocatingAnIntermediatePayload()
	{
		var lineEnding = Environment.NewLine;
		var exact = TreeAndContentExportService.CombineTreeAndContent(
			$"root/路径{lineEnding}{lineEnding}",
			$"file.cs:{lineEnding}Привет\r\n");
		Assert.Equal(
			$"root/路径{lineEnding}\u00A0{lineEnding}\u00A0{lineEnding}file.cs:{lineEnding}Привет\r\n",
			exact);

		_ = TreeAndContentExportService.CombineTreeAndContent("warmup", "warmup");
		var tree = new string('T', 1_000_000) + lineEnding;
		var content = new string('C', 1_000_000);
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

		var combined = TreeAndContentExportService.CombineTreeAndContent(tree, content);

		var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
		Assert.Equal(2_000_000 + lineEnding.Length * 3 + 2, combined.Length);
		Assert.True(
			allocated <= combined.Length * sizeof(char) + 64 * 1024L,
			$"Combining allocated {allocated:N0} bytes for a {combined.Length:N0}-character result.");
	}

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

		Assert.Contains("alpha.txt:", output, StringComparison.Ordinal);
		Assert.Contains("Alpha", output);
		Assert.DoesNotContain($"{file}:", output, StringComparison.Ordinal);
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
		Assert.Contains("alpha.txt:", output, StringComparison.Ordinal);
		Assert.Contains("[No Content, 0 bytes]", output);
		Assert.DoesNotContain($"{file}:", output, StringComparison.Ordinal);
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
