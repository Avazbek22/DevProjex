using System.Runtime.ExceptionServices;

namespace DevProjex.Kernel;

public static class CancellationAwareSort
{
	private const uint CancellationCheckMask = 0x3FFu;

	public static void Sort<T>(
		List<T> items,
		IComparer<T> comparer,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(items);
		ArgumentNullException.ThrowIfNull(comparer);
		if (!cancellationToken.CanBeCanceled)
		{
			items.Sort(comparer);
			return;
		}

		var comparisons = 0u;
		SortCore(
			() => items.Sort((left, right) =>
			{
				ThrowIfCancellationRequestedPeriodically(cancellationToken, ref comparisons);
				return comparer.Compare(left, right);
			}),
			cancellationToken);
	}

	public static void Sort<T>(
		List<T> items,
		Comparison<T> comparison,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(items);
		ArgumentNullException.ThrowIfNull(comparison);
		if (!cancellationToken.CanBeCanceled)
		{
			items.Sort(comparison);
			return;
		}

		var comparisons = 0u;
		SortCore(
			() => items.Sort((left, right) =>
			{
				ThrowIfCancellationRequestedPeriodically(cancellationToken, ref comparisons);
				return comparison(left, right);
			}),
			cancellationToken);
	}

	public static void Sort<T>(
		T[] items,
		IComparer<T> comparer,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(items);
		ArgumentNullException.ThrowIfNull(comparer);
		if (!cancellationToken.CanBeCanceled)
		{
			Array.Sort(items, comparer);
			return;
		}

		var comparisons = 0u;
		SortCore(
			() => Array.Sort(items, (left, right) =>
			{
				ThrowIfCancellationRequestedPeriodically(cancellationToken, ref comparisons);
				return comparer.Compare(left, right);
			}),
			cancellationToken);
	}

	private static void SortCore(Action sort, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		try
		{
			sort();
		}
		catch (InvalidOperationException exception)
			when (exception.InnerException is OperationCanceledException cancellation)
		{
			ExceptionDispatchInfo.Capture(cancellation).Throw();
		}

		cancellationToken.ThrowIfCancellationRequested();
	}

	private static void ThrowIfCancellationRequestedPeriodically(
		CancellationToken cancellationToken,
		ref uint comparisons)
	{
		if ((++comparisons & CancellationCheckMask) == 0)
			cancellationToken.ThrowIfCancellationRequested();
	}
}
