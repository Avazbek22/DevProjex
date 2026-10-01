using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using DevProjex.Application.Context;
using DevProjex.Kernel.Contracts;

internal static class SparseProjectionBenchmark
{
	private static readonly ProjectSelection Project = typeof(ProjectContextPlanner)
		.GetMethod("ResolveSelectionProjection", BindingFlags.NonPublic | BindingFlags.Static)!
		.CreateDelegate<ProjectSelection>();

	public static void Run(string[] arguments)
	{
		var repetitions = arguments.Length == 0 ? 7 :
			arguments.Length == 2 && arguments[0] == "--repetitions"
				? int.Parse(arguments[1], CultureInfo.InvariantCulture)
				: throw new ArgumentException("Only --repetitions is supported.");
		ArgumentOutOfRangeException.ThrowIfLessThan(repetitions, 3);
		Console.WriteLine("files,selected_files,median_ms,min_ms,max_ms,median_alloc_bytes,retained_child_capacity");
		foreach (var count in new[] { 1_000, 10_000, 100_000 })
		{
			var rootPath = Path.Combine(Path.GetTempPath(), "devprojex-projection-benchmark");
			var files = Enumerable.Range(0, count).Select(index => new TreeNodeDescriptor(
				$"File{index:D6}.cs", Path.Combine(rootPath, $"File{index:D6}.cs"), false, false, "file", [])).ToArray();
			var root = new TreeNodeDescriptor("project", rootPath, true, false, "folder", files);
			foreach (var selectedCount in new[] { 1, count / 100, count / 2 })
			{
				var selected = files.Take(selectedCount).Select(static node => node.FullPath).ToHashSet(StringComparer.Ordinal);
				var expectedFiles = selected.Order(StringComparer.Ordinal).ToArray();
				for (var warmup = 0; warmup < 3; warmup++)
					Verify(Project(root, selected, false, null, CancellationToken.None, null), expectedFiles, rootPath);
				var elapsed = new double[repetitions];
				var allocations = new long[repetitions];
				var retainedCapacity = 0;
				for (var repeat = 0; repeat < repetitions; repeat++)
				{
					var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
					var started = Stopwatch.GetTimestamp();
					var result = Project(root, selected, false, null, CancellationToken.None, null);
					elapsed[repeat] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
					allocations[repeat] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
					Verify(result, expectedFiles, rootPath);
					retainedCapacity = result.Tree.Children is List<TreeNodeDescriptor> children
						? children.Capacity : result.Tree.Children.Count;
				}
				Array.Sort(elapsed);
				Array.Sort(allocations);
				Console.WriteLine(string.Join(',', count, selectedCount,
					elapsed[repetitions / 2].ToString("F3", CultureInfo.InvariantCulture),
					elapsed[0].ToString("F3", CultureInfo.InvariantCulture),
					elapsed[^1].ToString("F3", CultureInfo.InvariantCulture),
					allocations[repetitions / 2], retainedCapacity));
			}
		}
	}

	private static void Verify(
		(TreeNodeDescriptor Tree, IReadOnlyList<string> Files, IReadOnlyList<string> Folders) projection,
		IReadOnlyList<string> expectedFiles,
		string rootPath)
	{
		if (!projection.Files.SequenceEqual(expectedFiles, StringComparer.Ordinal) ||
			projection.Folders.Count != 1 || projection.Folders[0] != rootPath ||
			!projection.Tree.Children.Select(static child => child.FullPath).SequenceEqual(expectedFiles, StringComparer.Ordinal))
			throw new InvalidOperationException("Projection changed paths or their ordering.");
	}

	private delegate (TreeNodeDescriptor Tree, IReadOnlyList<string> Files, IReadOnlyList<string> Folders) ProjectSelection(
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths,
		bool selectsNoEffectivePaths,
		IReadOnlyList<string>? knownFullTreeFilePaths,
		CancellationToken cancellationToken,
		StringComparer? pathComparer);
}
