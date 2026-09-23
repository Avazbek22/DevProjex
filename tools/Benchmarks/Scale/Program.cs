using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using DevProjex.Avalonia.ViewModels;
using DevProjex.Kernel.Contracts;

if (args.FirstOrDefault() == "sparse-projection")
{
	SparseProjectionBenchmark.Run(args[1..]);
	return;
}

if (args.FirstOrDefault() == "profile-reads")
{
	ProfileReadBenchmark.Run(args[1..]);
	return;
}

if (args.FirstOrDefault() != "tree-realization")
	throw new ArgumentException("Specify tree-realization, profile-reads, or sparse-projection.");

var repetitions = ReadRepetitions(args[1..]);
Console.WriteLine(
	"children,preserved_percent,median_ms,min_ms,max_ms,spread_ms,median_alloc_bytes,legacy_comparisons,indexed_lookups");
foreach (var count in new[] { 1_000, 10_000, 100_000 })
{
	foreach (var percentage in new[] { 0, 1, 50 })
	{
		var samples = new List<Sample>(repetitions);
		for (var repetition = 0; repetition < repetitions; repetition++)
		{
			var root = CreateFixture(count, percentage);
			var allocated = GC.GetAllocatedBytesForCurrentThread();
			var timer = Stopwatch.StartNew();
			var realized = root.Children;
			timer.Stop();
			if (realized.Count != count)
				throw new InvalidOperationException("Tree realization changed the child count.");
			samples.Add(new Sample(timer.Elapsed.TotalMilliseconds,
				GC.GetAllocatedBytesForCurrentThread() - allocated));
		}
		var elapsed = samples.Select(static sample => sample.ElapsedMilliseconds).Order().ToArray();
		var allocations = samples.Select(static sample => sample.AllocatedBytes).Order().ToArray();
		var preserved = checked(count * percentage / 100);
		Console.WriteLine(string.Join(',',
			count.ToString(CultureInfo.InvariantCulture),
			percentage.ToString(CultureInfo.InvariantCulture),
			Format(Median(elapsed)),
			Format(elapsed[0]),
			Format(elapsed[^1]),
			Format(elapsed[^1] - elapsed[0]),
			MedianLong(allocations).ToString(CultureInfo.InvariantCulture),
			((long)count * preserved).ToString(CultureInfo.InvariantCulture),
			count.ToString(CultureInfo.InvariantCulture)));
	}
}

static TreeNodeViewModel CreateFixture(int count, int preservedPercentage)
{
	var descriptors = Enumerable.Range(0, count)
		.Select(index => new TreeNodeDescriptor(
			$"File{index:D6}.cs",
			$@"C:\Scale\File{index:D6}.cs",
			false,
			false,
			"file",
			[]))
		.ToArray();
	var rootDescriptor = new TreeNodeDescriptor("Scale", @"C:\Scale", true, false, "folder", descriptors);
	var root = new TreeNodeViewModel(rootDescriptor, null, null, BuildChildrenFromDescriptor);
	var children = root.Children.ToArray();
	var preserveCount = checked(count * preservedPercentage / 100);
	var setChecked = typeof(TreeNodeViewModel).GetMethod(
		"SetCheckedForTreeStateRestore",
		BindingFlags.Instance | BindingFlags.NonPublic)!;
	for (var index = 0; index < preserveCount; index++)
		setChecked.Invoke(children[index], [true]);
	var release = typeof(TreeNodeViewModel).GetMethod(
		"TryReleaseChildrenToLazyState",
		BindingFlags.Instance | BindingFlags.NonPublic)!;
	if (release.Invoke(root, [null]) is not true)
		throw new InvalidOperationException("Fixture did not return to lazy state.");
	return root;
}

static IReadOnlyList<TreeNodeViewModel> BuildChildrenFromDescriptor(TreeNodeViewModel parent) =>
	parent.Descriptor.Children
		.Select(child => new TreeNodeViewModel(child, parent, null, BuildChildrenFromDescriptor))
		.ToArray();

static int ReadRepetitions(string[] arguments)
{
	if (arguments.Length == 0)
		return 5;
	if (arguments.Length != 2 || arguments[0] != "--repetitions")
		throw new ArgumentException("Only --repetitions is supported.");
	var result = int.Parse(arguments[1], CultureInfo.InvariantCulture);
	return result >= 3 ? result : throw new ArgumentOutOfRangeException(nameof(arguments));
}

static double Median(double[] values) => values[values.Length / 2];
static long MedianLong(long[] values) => values[values.Length / 2];
static string Format(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

internal readonly record struct Sample(double ElapsedMilliseconds, long AllocatedBytes);
