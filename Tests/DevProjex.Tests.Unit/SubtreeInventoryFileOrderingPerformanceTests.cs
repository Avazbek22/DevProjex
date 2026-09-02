using System.Collections.ObjectModel;
using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit;

public sealed class SubtreeInventoryFileOrderingPerformanceTests(ITestOutputHelper output)
{
	[Fact]
	public void OrderingMutatesOnlyACompleteOperationList()
	{
		var mutableFiles = CreateFiles("z.cs", "A.cs", "a.cs");
		var mutableResult = FileSystemScanner.OrderSubtreeInventoryFiles(
			mutableFiles,
			mutableFiles.Count,
			TestContext.Current.CancellationToken);

		Assert.Same(mutableFiles, mutableResult);
		Assert.Equal(["A.cs", "a.cs", "z.cs"], mutableFiles.Select(static file => file.Name));

		var readOnlySource = CreateFiles("z.cs", "A.cs", "a.cs");
		IReadOnlyList<FileSystemFileEntry> readOnlyFiles = new ReadOnlyCollection<FileSystemFileEntry>(readOnlySource);
		var readOnlyResult = FileSystemScanner.OrderSubtreeInventoryFiles(
			readOnlyFiles,
			readOnlyFiles.Count,
			TestContext.Current.CancellationToken);

		Assert.NotSame(readOnlyFiles, readOnlyResult);
		Assert.Equal(["z.cs", "A.cs", "a.cs"], readOnlySource.Select(static file => file.Name));
		Assert.Equal(["A.cs", "a.cs", "z.cs"], readOnlyResult.Select(static file => file.Name));

		var partialSource = CreateFiles("z.cs", "A.cs", "a.cs", "tail.cs");
		var partialResult = FileSystemScanner.OrderSubtreeInventoryFiles(
			partialSource,
			processedFileCount: 3,
			TestContext.Current.CancellationToken);

		Assert.NotSame(partialSource, partialResult);
		Assert.Equal(["z.cs", "A.cs", "a.cs", "tail.cs"], partialSource.Select(static file => file.Name));
		Assert.Equal(["A.cs", "a.cs", "z.cs"], partialResult.Select(static file => file.Name));
	}

	[Fact]
	[Trait("Category", "LocalPerformance")]
	public void CompleteOperationListAvoidsTheFullOrderingCopy()
	{
		if (!string.Equals(
		    Environment.GetEnvironmentVariable("DEVPROJEX_RUN_LARGE_PERF_TESTS"),
		    "1",
		    StringComparison.Ordinal))
		{
			Assert.Skip("Set DEVPROJEX_RUN_LARGE_PERF_TESTS=1 for the pre-release performance gate.");
		}

		const int fileCount = 100_000;
		var mutableFiles = CreateDescendingFiles(fileCount);
		var readOnlyBacking = CreateDescendingFiles(fileCount);
		IReadOnlyList<FileSystemFileEntry> readOnlyFiles =
			new ReadOnlyCollection<FileSystemFileEntry>(readOnlyBacking);

		_ = FileSystemScanner.OrderSubtreeInventoryFiles(
			CreateFiles("b.cs", "a.cs"),
			processedFileCount: 2,
			CancellationToken.None);
		_ = FileSystemScanner.OrderSubtreeInventoryFiles(
			new ReadOnlyCollection<FileSystemFileEntry>(CreateFiles("b.cs", "a.cs")),
			processedFileCount: 2,
			CancellationToken.None);

		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var fallback = FileSystemScanner.OrderSubtreeInventoryFiles(
			readOnlyFiles,
			fileCount,
			CancellationToken.None);
		var fallbackAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

		allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var inPlace = FileSystemScanner.OrderSubtreeInventoryFiles(
			mutableFiles,
			fileCount,
			CancellationToken.None);
		var inPlaceAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

		Assert.Same(mutableFiles, inPlace);
		Assert.Equal(FlattenInventory(fallback), FlattenInventory(inPlace));
		Assert.True(
			inPlaceAllocatedBytes + (fileCount * IntPtr.Size) < fallbackAllocatedBytes,
			$"In-place ordering allocated {inPlaceAllocatedBytes:N0} B; " +
			$"fallback ordering allocated {fallbackAllocatedBytes:N0} B.");

		output.WriteLine(
			$"Subtree inventory file ordering allocations: fallback {fallbackAllocatedBytes:N0} B, " +
			$"in-place {inPlaceAllocatedBytes:N0} B for {fileCount:N0} files.");
	}

	private static List<FileSystemFileEntry> CreateFiles(params string[] names)
	{
		var files = new List<FileSystemFileEntry>(names.Length);
		foreach (var name in names)
			files.Add(CreateFile(name));
		return files;
	}

	private static List<FileSystemFileEntry> CreateDescendingFiles(int count)
	{
		var files = new List<FileSystemFileEntry>(count);
		for (var index = count - 1; index >= 0; index--)
			files.Add(CreateFile($"file-{index:D6}.cs"));
		return files;
	}

	private static FileSystemFileEntry CreateFile(string name) =>
		new(
			name,
			$"C:/benchmark/project/{name}",
			name,
			IsHidden: false,
			Length: name.Length);

	private static List<ProjectTreeInventoryEntry> FlattenInventory(
		IReadOnlyList<FileSystemFileEntry> files)
	{
		var entries = new List<ProjectTreeInventoryEntry>(files.Count + 1)
		{
			new(
				"project",
				"C:/benchmark/project",
				string.Empty,
				parentIndex: -1,
				isDirectory: true,
				isHidden: false,
				length: 0)
			{
				FirstChildIndex = 1,
				ChildCount = files.Count
			}
		};
		foreach (var file in files)
		{
			entries.Add(new ProjectTreeInventoryEntry(
				file.Name,
				file.FullPath,
				file.RelativePath,
				parentIndex: 0,
				isDirectory: false,
				file.IsHidden,
				file.Length));
		}

		return entries;
	}
}
