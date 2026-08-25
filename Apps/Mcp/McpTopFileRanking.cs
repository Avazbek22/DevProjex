namespace DevProjex.Mcp;

internal readonly record struct McpFileWeight(string Path, long Tokens);

internal sealed class McpTopFileRanking
{
	private readonly int _capacity;
	private readonly List<McpFileWeight> _items;

	public McpTopFileRanking(int capacity)
	{
		if (capacity <= 0)
			throw new ArgumentOutOfRangeException(nameof(capacity));

		_capacity = capacity;
		_items = new List<McpFileWeight>(capacity);
	}

	public IReadOnlyList<McpFileWeight> Items => _items;

	public void Add(string path, long tokens)
	{
		var candidate = new McpFileWeight(path, tokens);
		var index = _items.BinarySearch(candidate, McpFileWeightComparer.Instance);
		if (index < 0)
			index = ~index;
		_items.Insert(index, candidate);
		if (_items.Count > _capacity)
			_items.RemoveAt(_capacity);
	}

	private sealed class McpFileWeightComparer : IComparer<McpFileWeight>
	{
		public static readonly McpFileWeightComparer Instance = new();

		public int Compare(McpFileWeight left, McpFileWeight right)
		{
			var tokenOrder = right.Tokens.CompareTo(left.Tokens);
			return tokenOrder != 0
				? tokenOrder
				: StringComparer.Ordinal.Compare(left.Path, right.Path);
		}
	}
}
