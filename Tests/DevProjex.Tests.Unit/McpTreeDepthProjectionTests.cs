using DevProjex.Kernel.Contracts;
using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpTreeDepthProjectionTests
{
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
