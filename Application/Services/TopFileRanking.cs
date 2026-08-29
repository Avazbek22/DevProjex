namespace DevProjex.Application.Services;

public readonly record struct TopFileMetric(string Path, long Tokens);

public sealed class TopFileRanking
{
	private readonly int _capacity;
	private readonly List<TopFileMetric> _items;

	public TopFileRanking(int capacity)
	{
		if (capacity <= 0)
			throw new ArgumentOutOfRangeException(nameof(capacity));

		_capacity = capacity;
		_items = new List<TopFileMetric>(capacity);
	}

	public IReadOnlyList<TopFileMetric> Items => _items;

	public TResult[] Project<TResult>(Func<TopFileMetric, TResult> projection)
	{
		ArgumentNullException.ThrowIfNull(projection);
		var result = new TResult[_items.Count];
		for (var index = 0; index < _items.Count; index++)
			result[index] = projection(_items[index]);
		return result;
	}

	public void Add(string path, long tokens)
	{
		var candidate = new TopFileMetric(path, tokens);
		var index = _items.BinarySearch(candidate, TopFileMetricComparer.Instance);
		if (index < 0)
			index = ~index;
		_items.Insert(index, candidate);
		if (_items.Count > _capacity)
			_items.RemoveAt(_capacity);
	}

	private sealed class TopFileMetricComparer : IComparer<TopFileMetric>
	{
		public static readonly TopFileMetricComparer Instance = new();

		public int Compare(TopFileMetric left, TopFileMetric right)
		{
			var tokenOrder = right.Tokens.CompareTo(left.Tokens);
			return tokenOrder != 0
				? tokenOrder
				: StringComparer.Ordinal.Compare(left.Path, right.Path);
		}
	}
}
