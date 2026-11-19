namespace DevProjex.Mcp;

internal sealed class McpOutlineReadCache<T>(int capacity)
{
	private readonly object sync = new();
	private readonly Dictionary<string, LinkedListNode<(string Path, T Value)>> entries = new(PathComparer.Default);
	private readonly LinkedList<(string Path, T Value)> order = new();

	internal int Count { get { lock (sync) return entries.Count; } }

	public T AddOrUpdate<TArg>(string path, Func<string, TArg, T> create, Func<string, T, TArg, T> update, TArg argument)
	{
		lock (sync)
		{
			var exists = entries.TryGetValue(path, out var node);
			var value = exists ? update(path, node!.Value.Value, argument) : create(path, argument);
			var replacement = new LinkedListNode<(string Path, T Value)>((path, value));
			entries[path] = replacement;
			if (node is not null) order.Remove(node);
			order.AddFirst(replacement);
			while (entries.Count > capacity && order.Last is { } oldest)
			{
				entries.Remove(oldest.Value.Path);
				order.RemoveLast();
			}
			return value;
		}
	}
}
