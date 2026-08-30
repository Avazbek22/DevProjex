using DevProjex.Application.Context;

namespace DevProjex.Tests.Unit;

public sealed class ProjectContextFileSizeTests
{
	[Fact]
	public void CompleteInventoryUsesSnapshotLengthsForEveryOrderedFile()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}");
		var first = Path.Combine(rootPath, "first.cs");
		var second = Path.Combine(rootPath, "second.cs");
		var root = CreateRoot(rootPath, first, second);
		var inventory = CreateInventory(rootPath, (first, 0), (second, 42));

		var sizes = ProjectContextPlanner.BuildEffectiveFileSizes(
			root,
			[first, second],
			inventory,
			TestContext.Current.CancellationToken);

		Assert.Equal(0, sizes[first]);
		Assert.Equal(42, sizes[second]);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void IncompleteInventoryReadsOnlyUnresolvedFileSizesFromDisk(bool provideOrderedPaths)
	{
		using var temp = new TemporaryDirectory();
		var first = temp.CreateFile("first.cs", "first");
		var second = temp.CreateFile("second.cs", "second-file");
		var root = CreateRoot(temp.Path, first, second);
		var inventory = CreateInventory(temp.Path, (first, 123));

		var sizes = ProjectContextPlanner.BuildEffectiveFileSizes(
			root,
			provideOrderedPaths ? [first, second] : null,
			inventory,
			TestContext.Current.CancellationToken);

		Assert.Equal(123, sizes[first]);
		Assert.Equal(new FileInfo(second).Length, sizes[second]);
	}

	[Fact]
	public void SnapshotSizesPreserveCaseDistinctFilesOnCaseSensitiveVolumes()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), $"case-sensitive-{Guid.NewGuid():N}");
		var upper = Path.Combine(rootPath, "Foo.cs");
		var lower = Path.Combine(rootPath, "foo.cs");
		var root = CreateRoot(rootPath, upper, lower);
		var inventory = CreateInventory(rootPath, (upper, 11), (lower, 22));

		var sizes = ProjectContextPlanner.BuildEffectiveFileSizes(
			root,
			[upper, lower],
			inventory,
			TestContext.Current.CancellationToken);

		Assert.Equal(2, sizes.Count);
		Assert.Equal(11, sizes[upper]);
		Assert.Equal(22, sizes[lower]);
	}

	private static TreeNodeDescriptor CreateRoot(string rootPath, params string[] files) =>
		new(
			Path.GetFileName(rootPath),
			rootPath,
			true,
			false,
			"folder",
			files.Select(static path =>
				new TreeNodeDescriptor(Path.GetFileName(path), path, false, false, "csharp", [])).ToArray());

	private static ProjectTreeInventorySnapshot CreateInventory(
		string rootPath,
		params (string Path, long Length)[] files)
	{
		var entries = new List<ProjectTreeInventoryEntry>
		{
			new(Path.GetFileName(rootPath), rootPath, string.Empty, -1, true, false, 0)
			{
				FirstChildIndex = files.Length == 0 ? -1 : 1,
				ChildCount = files.Length
			}
		};
		entries.AddRange(files.Select(file => new ProjectTreeInventoryEntry(
			Path.GetFileName(file.Path),
			file.Path,
			Path.GetFileName(file.Path),
			parentIndex: 0,
			isDirectory: false,
			isHidden: false,
			file.Length)));
		return new ProjectTreeInventorySnapshot(entries, rootAccessDenied: false, hadAccessDenied: false);
	}
}
