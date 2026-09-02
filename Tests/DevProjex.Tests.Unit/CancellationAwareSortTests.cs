namespace DevProjex.Tests.Unit;

public sealed class CancellationAwareSortTests
{
	[Fact]
	public void CancellationDuringListAndArraySortRemainsOperationCanceledException()
	{
		AssertCancellationIsNotWrapped((values, comparer, token) =>
			CancellationAwareSort.Sort(values, comparer, token));
		AssertCancellationIsNotWrapped((values, comparer, token) =>
			CancellationAwareSort.Sort(values.ToArray(), comparer, token));
	}

	private static void AssertCancellationIsNotWrapped(
		Action<List<int>, IComparer<int>, CancellationToken> sort)
	{
		using var cancellation = new CancellationTokenSource();
		var values = Enumerable.Range(0, 8_192).Reverse().ToList();
		var comparer = new CancelingComparer(cancellation);

		var exception = Assert.Throws<OperationCanceledException>(() =>
			sort(values, comparer, cancellation.Token));

		Assert.Equal(cancellation.Token, exception.CancellationToken);
	}

	private sealed class CancelingComparer(CancellationTokenSource cancellation) : IComparer<int>
	{
		private int _comparisonCount;

		public int Compare(int left, int right)
		{
			if (Interlocked.Increment(ref _comparisonCount) == 1)
				cancellation.Cancel();
			return left.CompareTo(right);
		}
	}
}
