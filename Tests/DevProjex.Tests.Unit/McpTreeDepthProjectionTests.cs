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
		TreeNodeDescriptor Node(string name, params TreeNodeDescriptor[] children) => new(name, Path.Combine(rootPath, name), true, false, "", children);
		var root = Node("", Node("src", Node("src/router", Node("src/router/trie", Node("src/router/trie/node.ts")))));
		var result = DevProjexMcpTools.PruneRequestedTree(root, depth, [path], CancellationToken.None);
		Assert.Equal(visible, result.Children.Count > 0);
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

		public IEnumerator<T> GetEnumerator() => items.GetEnumerator();

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}
}
