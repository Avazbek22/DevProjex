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

	[Fact]
	public void Create_CanonicalInputIsDefensivelyCopied()
	{
		string[] paths = ["a.cs", "b.cs"];

		var snapshot = ContentSelectionSnapshot.Create("project", paths);
		paths[0] = "changed.cs";

		Assert.Equal(["a.cs", "b.cs"], snapshot.OrderedPaths);
	}

	[Fact]
	public void Create_PreservesPathsThatDifferOnlyByCase()
	{
		var upperPath = Path.Combine("project", "Foo.cs");
		var lowerPath = Path.Combine("project", "foo.cs");

		var snapshot = ContentSelectionSnapshot.Create(
			"project",
			[lowerPath, upperPath, lowerPath]);
		var upperOnly = ContentSelectionSnapshot.Create("project", [upperPath]);

		Assert.Equal([lowerPath, upperPath], snapshot.OrderedPaths);
		Assert.NotEqual(upperOnly.SelectionFingerprint, snapshot.SelectionFingerprint);
	}

	[Fact]
	public void CreateFromOwnedOrderedUnique_ReusesOwnedArrayAndPreservesCanonicalIdentity()
	{
		string[] ownedPaths = ["a.cs", "b.cs"];
		var expected = ContentSelectionSnapshot.Create("project", ownedPaths);

		var snapshot = ContentSelectionSnapshot.CreateFromOwnedOrderedUnique(
			"project",
			ownedPaths,
			CancellationToken.None);

		Assert.Same(ownedPaths, snapshot.OrderedPaths);
		Assert.Equal(expected.SelectionFingerprint, snapshot.SelectionFingerprint);
		Assert.Equal(expected.OrderedPaths, snapshot.OrderedPaths);
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
