using DevProjex.Kernel.Contracts;
using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpTreeDepthProjectionTests
{
	[Theory]
	[InlineData(0, false, false)]
	[InlineData(1, true, false)]
	[InlineData(2, true, true)]
	[InlineData(1000, true, true)]
	public void RequestedDirectoryStartsAtDepthZero(int depth, bool directoryVisible, bool fileVisible)
	{
		var rootPath = Path.GetFullPath("tree");
		TreeNodeDescriptor Node(string path, params TreeNodeDescriptor[] children) =>
			new(Path.GetFileName(path), Path.Combine(rootPath, path), !path.EndsWith(".ts", StringComparison.Ordinal), false, "", children);
		var root = Node("", Node("src", Node("src/router", Node("src/router/trie", Node("src/router/trie/node.ts")))));
		Assert.Equal(2, DevProjexMcpTools.CalculateRequestedTreeDepth(root, ["src/router"], CancellationToken.None));
		var result = DevProjexMcpTools.PruneRequestedTree(root, depth, ["src/router"], CancellationToken.None);
		var router = Assert.Single(Assert.Single(result.Children).Children);
		Assert.Equal(directoryVisible, router.Children.Count > 0);
		Assert.Equal(fileVisible, router.Children.Count > 0 && router.Children[0].Children.Count > 0);
	}

	[Theory]
	[InlineData("src/router/trie/node.ts", 0, true)]
	[InlineData("missing", 1, false)]
	[InlineData(".", 0, false)]
	public void ExplicitFilesMissingPathsAndRootHaveStableBoundaries(string path, int depth, bool visible)
	{
		var rootPath = Path.GetFullPath("tree");
		TreeNodeDescriptor Node(string name, params TreeNodeDescriptor[] children) => new(name, Path.Combine(rootPath, name), !name.EndsWith(".ts", StringComparison.Ordinal), false, "", children);
		var root = Node("", Node("src", Node("src/router", Node("src/router/trie", Node("src/router/trie/node.ts")))));
		var result = DevProjexMcpTools.PruneRequestedTree(root, depth, [path], CancellationToken.None);
		Assert.Equal(visible, result.Children.Count > 0);
	}

	[Fact]
	public void GeneratedRequestedWindowsMatchAnIndependentPathOracle()
	{
		const int seed = 18643;
		var random = new Random(seed);
		var rootPath = Path.GetFullPath("generated-tree");
		for (var sample = 0; sample < 200; sample++)
		{
			var directories = new List<string> { "" };
			var nodes = new List<(string Path, bool Directory)> { ("", true) };
			for (var index = 0; index < 30; index++)
			{
				var parent = directories[random.Next(directories.Count)];
				var directory = random.Next(3) == 0;
				var path = (parent.Length == 0 ? "" : parent + "/") + $"node{index}" + (directory ? "" : ".ts");
				nodes.Add((path, directory));
				if (directory)
					directories.Add(path);
			}
			var requested = Enumerable.Range(0, random.Next(1, 6))
				.Select(_ => nodes[random.Next(nodes.Count)].Path).Append("missing").ToArray();
			var depth = random.Next(4);
			var expected = nodes.Where(node => node.Path.Length == 0 || requested.Any(path =>
				path == node.Path || IsAncestor(node.Path, path) ||
				IsAncestor(path, node.Path) && Parts(node.Path) - Parts(path) <= depth))
				.Select(node => node.Path).Order(StringComparer.Ordinal).ToArray();
			var root = Build("");
			var fullDepth = nodes.Where(node => requested.Any(path => path == node.Path || IsAncestor(path, node.Path)))
				.Select(node => requested.Where(path => path == node.Path || IsAncestor(path, node.Path))
					.Min(path => Parts(node.Path) - Parts(path))).DefaultIfEmpty(0).Max();
			Assert.Equal(fullDepth, DevProjexMcpTools.CalculateRequestedTreeDepth(root,
				requested.Select(path => path.Length == 0 ? "." : path).ToArray(), TestContext.Current.CancellationToken));
			var actual = DevProjexMcpTools.PruneRequestedTree(root, depth,
				requested.Select(path => path.Length == 0 ? "." : path).ToArray(), CancellationToken.None);
			Assert.True(expected.SequenceEqual(Flatten(actual).Order(StringComparer.Ordinal)), $"seed={seed}; sample={sample}; depth={depth}");
			var reversed = DevProjexMcpTools.PruneRequestedTree(root, depth,
				requested.Reverse().Concat(requested).Select(path => path.Length == 0 ? "." : path).ToArray(), CancellationToken.None);
			Assert.Equal(Flatten(actual), Flatten(reversed));

			TreeNodeDescriptor Build(string path) => new(Path.GetFileName(path), Path.Combine(rootPath, path),
				nodes.Single(node => node.Path == path).Directory, false, "",
				nodes.Where(node => node.Path.Length > 0 && Parent(node.Path) == path).Select(node => Build(node.Path)).ToArray());
		}

		IEnumerable<string> Flatten(TreeNodeDescriptor node)
		{
			var relative = Path.GetRelativePath(rootPath, node.FullPath).Replace('\\', '/');
			return new[] { relative == "." ? "" : relative }.Concat(node.Children.SelectMany(Flatten));
		}
		static bool IsAncestor(string parent, string child) => child.Length > 0 && (parent.Length == 0 || child.StartsWith(parent + "/", StringComparison.Ordinal));
		static int Parts(string path) => path.Length == 0 ? 0 : path.Split('/').Length;
		static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
	}

	[Fact]
	public void RequestedProjectionAndDepthCalculationObserveCancellationDuringTraversal()
	{
		foreach (var calculate in new[] { false, true })
		{
			using var cancellation = new CancellationTokenSource();
			var rootPath = Path.GetFullPath("cancel-tree");
			var child = new TreeNodeDescriptor("src", Path.Combine(rootPath, "src"), true, false, "", []);
			var root = new TreeNodeDescriptor("root", rootPath, true, false, "",
				new CancelOnReadList<TreeNodeDescriptor>([child], cancellation));
			Assert.Throws<OperationCanceledException>(() =>
			{
				if (calculate)
					DevProjexMcpTools.CalculateRequestedTreeDepth(root, ["src"], cancellation.Token);
				else
					DevProjexMcpTools.PruneRequestedTree(root, 1, ["src"], cancellation.Token);
			});
		}
	}

	[Fact]
	public void OverlappingRequestsKeepTheDeeperWindow()
	{
		var rootPath = Path.GetFullPath("tree");
		TreeNodeDescriptor Node(string name, params TreeNodeDescriptor[] children) => new(name, Path.Combine(rootPath, name), true, false, "", children);
		var root = Node("", Node("src", Node("src/deep", Node("src/deep/file.ts"))));
		var result = DevProjexMcpTools.PruneRequestedTree(root, 1, ["src", "src/deep"], CancellationToken.None);
		Assert.Single(Assert.Single(Assert.Single(result.Children).Children).Children);
	}

	[Fact]
	public void PruneToDepthPreservesRequestedDepthWithoutUsingTheCallStack()
	{
		const int sourceDepth = 10_000;
		const int requestedDepth = 1_000;
		var node = CreateNode(sourceDepth, []);
		for (var depth = sourceDepth - 1; depth >= 0; depth--)
			node = CreateNode(depth, [node]);

		var projected = DevProjexMcpTools.PruneToDepth(node, requestedDepth);

		var current = projected;
		for (var depth = 0; depth < requestedDepth; depth++)
		{
			Assert.Equal(depth.ToString(CultureInfo.InvariantCulture), current.DisplayName);
			current = Assert.Single(current.Children);
		}
		Assert.Equal(requestedDepth.ToString(CultureInfo.InvariantCulture), current.DisplayName);
		Assert.Empty(current.Children);
	}

	[Fact]
	public void PruneToDepthWithCancellationStopsDuringProjection()
	{
		using var cancellation = new CancellationTokenSource();
		var child = CreateNode(1, []);
		var root = CreateNode(
			0,
			new CancelOnReadList<TreeNodeDescriptor>([child], cancellation));

		Assert.Throws<OperationCanceledException>(() =>
			DevProjexMcpTools.PruneToDepthWithCancellation(root, 1, cancellation.Token));
	}

	private static TreeNodeDescriptor CreateNode(
		int depth,
		IReadOnlyList<TreeNodeDescriptor> children)
	{
		var displayName = depth.ToString(CultureInfo.InvariantCulture);
		return new TreeNodeDescriptor(
			displayName,
			displayName,
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			children);
	}

	private sealed class CancelOnReadList<T>(
		IReadOnlyList<T> items,
		CancellationTokenSource cancellation) : IReadOnlyList<T>
	{
		public int Count => items.Count;

		public T this[int index]
		{
			get
			{
				var item = items[index];
				cancellation.Cancel();
				return item;
			}
		}

		public IEnumerator<T> GetEnumerator()
		{
			for (var index = 0; index < Count; index++)
				yield return this[index];
		}

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}
}
