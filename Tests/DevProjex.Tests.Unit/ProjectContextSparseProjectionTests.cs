using DevProjex.Application.Context;

namespace DevProjex.Tests.Unit;

public sealed class ProjectContextSparseProjectionTests
{
	[Theory]
	[InlineData("file")]
	[InlineData("directory")]
	[InlineData("mixed")]
	[InlineData("empty-directory")]
	[InlineData("missing")]
	[InlineData("root")]
	[InlineData("all")]
	public void ProjectionPreservesPathsOrderingAndNodeMetadata(string selectionKind)
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "sparse-projection");
		TreeNodeDescriptor FileNode(string relativePath) => new(
			Path.GetFileName(relativePath), Path.Combine(rootPath, relativePath), false, false, "source", []);
		TreeNodeDescriptor Folder(string name, params TreeNodeDescriptor[] children) => new(
			name, Path.Combine(rootPath, name), true, false, "directory", children);
		var sharedFile = FileNode(Path.Combine("src", "Shared.cs"));
		var root = new TreeNodeDescriptor("project", rootPath, true, false, "root",
		[
			Folder("src", FileNode(Path.Combine("src", "Z.cs")), sharedFile, sharedFile,
				FileNode(Path.Combine("src", "a.cs")), FileNode(Path.Combine("src", "A.cs"))),
			Folder("empty"),
			Folder("tests", FileNode(Path.Combine("tests", "Test.cs"))),
			FileNode("readme.md")
		]);
		var selected = new HashSet<string>(StringComparer.Ordinal);
		switch (selectionKind)
		{
			case "file": selected.Add(sharedFile.FullPath); break;
			case "directory": selected.Add(root.Children[0].FullPath); break;
			case "mixed":
				selected.Add(sharedFile.FullPath);
				selected.Add(root.Children[2].FullPath);
				selected.Add(root.Children[1].FullPath);
				break;
			case "empty-directory": selected.Add(root.Children[1].FullPath); break;
			case "missing": selected.Add(Path.Combine(rootPath, "Missing.cs")); break;
			case "root": selected.Add(rootPath); break;
		}

		var includedNodes = ProjectTreeSelectionProjection.BuildIncludedNodes(root, selected);
		var expectedFiles = ProjectTreeSelectionProjection.BuildOrderedSelectedFilePaths(root, selected, ensureExists: false);
		var expectedFolders = includedNodes.Where(static node => node.IsDirectory)
			.Select(static node => node.FullPath).Order(StringComparer.Ordinal).ToArray();
		var expectedTree = ProjectContextPlanner.ResolveProjectedTree(
			root, selected, includedNodes, false, TestContext.Current.CancellationToken);
		var actual = ProjectContextPlanner.ResolveSelectionProjection(
			root, selected, false, null, TestContext.Current.CancellationToken);

		Assert.Equal(expectedFiles, actual.IncludedFiles);
		Assert.Equal(expectedFolders, actual.IncludedFolders);
		Assert.Equal(Flatten(expectedTree), Flatten(actual.ProjectedTree));
	}

	[Fact]
	public void AlreadyCanceledProjectionDoesNotReadChildren()
	{
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		var root = new TreeNodeDescriptor("project", "/project", true, false, "root", []);
		Assert.ThrowsAny<OperationCanceledException>(() => ProjectContextPlanner.ResolveSelectionProjection(
			root, new HashSet<string>(["/project/file.cs"], StringComparer.Ordinal), false, null, cancellation.Token));
	}

	private static IEnumerable<(string Name, string Path, bool Directory, bool AccessDenied, string Icon)> Flatten(
		TreeNodeDescriptor root)
	{
		var pending = new Stack<TreeNodeDescriptor>();
		pending.Push(root);
		while (pending.TryPop(out var node))
		{
			yield return (node.DisplayName, node.FullPath, node.IsDirectory, node.IsAccessDenied, node.IconKey);
			for (var index = node.Children.Count - 1; index >= 0; index--)
				pending.Push(node.Children[index]);
		}
	}
}
