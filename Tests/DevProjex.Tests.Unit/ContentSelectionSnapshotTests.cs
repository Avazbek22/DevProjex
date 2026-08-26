using DevProjex.Application.Compression;

namespace DevProjex.Tests.Unit;

public sealed class ContentSelectionSnapshotTests
{
	[Fact]
	public void CreateWithCancellation_PreservesCallerOrderAndCanonicalFingerprint()
	{
		var ordered = ContentSelectionSnapshot.CreateWithCancellation(
			"project",
			["a.cs", "b.cs"],
			CancellationToken.None);
		var permuted = ContentSelectionSnapshot.CreateWithCancellation(
			"project",
			["b.cs", "a.cs", "b.cs", " "],
			CancellationToken.None);

		Assert.Equal(["b.cs", "a.cs"], permuted.OrderedPaths);
		Assert.Equal(ordered.SelectionFingerprint, permuted.SelectionFingerprint);
	}

	[Fact]
	public void CreateWithCancellation_StopsEnumeratingWhenCanceled()
	{
		using var cancellation = new CancellationTokenSource();
		var paths = new CancelingPathList(cancellation);

		Assert.Throws<OperationCanceledException>(() =>
			ContentSelectionSnapshot.CreateWithCancellation(
				"project",
				paths,
				cancellation.Token));
		Assert.Equal(1, paths.ReadCount);
	}

	private sealed class CancelingPathList(CancellationTokenSource cancellation) : IReadOnlyList<string>
	{
		public int Count => 2;

		public int ReadCount { get; private set; }

		public string this[int index]
		{
			get
			{
				ReadCount++;
				if (index == 0)
					cancellation.Cancel();
				return $"{index}.cs";
			}
		}

		public IEnumerator<string> GetEnumerator() =>
			throw new InvalidOperationException("The snapshot must use indexed access.");

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}
}
