using System.Diagnostics;
using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyLanguageAdapterPerformanceIntegrationTests(ITestOutputHelper output)
{
	[Fact]
	[Trait("Category", "LocalPerformance")]
	public void JavaExtractionReportsStableIsolatedCost()
	{
		var source = string.Join('\n', Enumerable.Range(0, 1_000)
			.Select(static index => $"class Type{index} {{ Type{(index + 1) % 1000} value; void run() {{ }} }}"));
		var prepared = new PreparedDependencySource(
			"Generated.java",
			"Generated.java",
			"root:java",
			LanguageId.Java,
			"fixture",
			"fixture",
			source);
		using var extractor = new TreeSitterDependencyFactExtractor();
		_ = extractor.Extract(prepared, new DependencyFactsLimits(), TestContext.Current.CancellationToken);
		var samples = new List<(double Milliseconds, long Bytes)>();
		for (var iteration = 0; iteration < 5; iteration++)
		{
			var before = GC.GetTotalAllocatedBytes(precise: false);
			var started = Stopwatch.StartNew();
			var facts = extractor.Extract(
				prepared,
				new DependencyFactsLimits(),
				TestContext.Current.CancellationToken);
			started.Stop();
			var bytes = GC.GetTotalAllocatedBytes(precise: false) - before;
			Assert.Equal(1_000, facts.Declarations.Count);
			Assert.Equal(1_000, facts.References.Count);
			samples.Add((started.Elapsed.TotalMilliseconds, bytes));
		}
		var ordered = samples.OrderBy(static sample => sample.Milliseconds).ToArray();
		var median = ordered[ordered.Length / 2];
		output.WriteLine(
			$"Java extraction: median={median.Milliseconds:F3} ms, allocated={median.Bytes} bytes, " +
			$"range={ordered[0].Milliseconds:F3}-{ordered[^1].Milliseconds:F3} ms");
	}

	[Fact]
	[Trait("Category", "LocalPerformance")]
	public void RustExtractionReportsStableIsolatedCost()
	{
		var source = string.Join('\n', Enumerable.Range(0, 1_000)
			.Select(static index => $"struct Type{index} {{ value: Type{(index + 1) % 1000} }}"));
		var prepared = new PreparedDependencySource(
			"src/generated.rs",
			"src/generated.rs",
			"root:rust",
			LanguageId.Rust,
			"fixture",
			"fixture",
			source);
		using var extractor = new TreeSitterDependencyFactExtractor();
		_ = extractor.Extract(prepared, new DependencyFactsLimits(), TestContext.Current.CancellationToken);
		var samples = new List<(double Milliseconds, long Bytes)>();
		for (var iteration = 0; iteration < 5; iteration++)
		{
			var before = GC.GetTotalAllocatedBytes(precise: false);
			var started = Stopwatch.StartNew();
			var facts = extractor.Extract(
				prepared,
				new DependencyFactsLimits(),
				TestContext.Current.CancellationToken);
			started.Stop();
			var bytes = GC.GetTotalAllocatedBytes(precise: false) - before;
			Assert.Equal(1_000, facts.Declarations.Count);
			Assert.Equal(1_000, facts.References.Count);
			samples.Add((started.Elapsed.TotalMilliseconds, bytes));
		}
		var ordered = samples.OrderBy(static sample => sample.Milliseconds).ToArray();
		var median = ordered[ordered.Length / 2];
		output.WriteLine(
			$"Rust extraction: median={median.Milliseconds:F3} ms, allocated={median.Bytes} bytes, " +
			$"range={ordered[0].Milliseconds:F3}-{ordered[^1].Milliseconds:F3} ms");
	}

	[Fact]
	[Trait("Category", "LocalPerformance")]
	public void KotlinExtractionReportsStableIsolatedCost()
	{
		var source = "package generated\n" + string.Join('\n', Enumerable.Range(0, 1_000)
			.Select(static index => $"class Type{index}(val value: Type{(index + 1) % 1000})"));
		var prepared = new PreparedDependencySource(
			"Generated.kt",
			"Generated.kt",
			"root:kotlin",
			LanguageId.Kotlin,
			"fixture",
			"fixture",
			source);
		using var extractor = new TreeSitterDependencyFactExtractor();
		_ = extractor.Extract(prepared, new DependencyFactsLimits(), TestContext.Current.CancellationToken);
		var samples = new List<(double Milliseconds, long Bytes)>();
		for (var iteration = 0; iteration < 5; iteration++)
		{
			var before = GC.GetTotalAllocatedBytes(precise: false);
			var started = Stopwatch.StartNew();
			var facts = extractor.Extract(
				prepared,
				new DependencyFactsLimits(),
				TestContext.Current.CancellationToken);
			started.Stop();
			var bytes = GC.GetTotalAllocatedBytes(precise: false) - before;
			Assert.Equal(1_000, facts.Declarations.Count);
			Assert.Equal(1_000, facts.References.Count);
			samples.Add((started.Elapsed.TotalMilliseconds, bytes));
		}
		var ordered = samples.OrderBy(static sample => sample.Milliseconds).ToArray();
		var median = ordered[ordered.Length / 2];
		output.WriteLine(
			$"Kotlin extraction: median={median.Milliseconds:F3} ms, allocated={median.Bytes} bytes, " +
			$"range={ordered[0].Milliseconds:F3}-{ordered[^1].Milliseconds:F3} ms");
	}

	[Fact]
	[Trait("Category", "LocalPerformance")]
	public void RubyExtractionReportsStableIsolatedCost()
	{
		var source = string.Join('\n', Enumerable.Range(0, 1_000)
			.Select(static index => $"class Type{index}\n VALUE = Type{(index + 1) % 1000}\nend"));
		var prepared = new PreparedDependencySource(
			"generated.rb", "generated.rb", "root:ruby", LanguageId.Ruby,
			"fixture", "fixture", source);
		using var extractor = new TreeSitterDependencyFactExtractor();
		_ = extractor.Extract(prepared, new DependencyFactsLimits(), TestContext.Current.CancellationToken);
		var samples = new List<(double Milliseconds, long Bytes)>();
		for (var iteration = 0; iteration < 5; iteration++)
		{
			var before = GC.GetTotalAllocatedBytes(precise: false);
			var started = Stopwatch.StartNew();
			var facts = extractor.Extract(prepared, new DependencyFactsLimits(), TestContext.Current.CancellationToken);
			started.Stop();
			var bytes = GC.GetTotalAllocatedBytes(precise: false) - before;
			Assert.Equal(1_000, facts.Declarations.Count);
			Assert.Equal(1_000, facts.References.Count);
			samples.Add((started.Elapsed.TotalMilliseconds, bytes));
		}
		var ordered = samples.OrderBy(static sample => sample.Milliseconds).ToArray();
		var median = ordered[ordered.Length / 2];
		output.WriteLine($"Ruby extraction: median={median.Milliseconds:F3} ms, allocated={median.Bytes} bytes, " +
		                 $"range={ordered[0].Milliseconds:F3}-{ordered[^1].Milliseconds:F3} ms");
	}

	[Fact]
	[Trait("Category", "LocalPerformance")]
	public void CExtractionReportsStableIsolatedCost()
	{
		var source = string.Join('\n', Enumerable.Range(0, 1_000)
			.Select(static index => $"struct Type{index} {{ struct Type{(index + 1) % 1000} *next; }};"));
		var prepared = new PreparedDependencySource("generated.c", "generated.c", "root:c", LanguageId.C,
			"fixture", "fixture", source);
		using var extractor = new TreeSitterDependencyFactExtractor();
		_ = extractor.Extract(prepared, new DependencyFactsLimits(), TestContext.Current.CancellationToken);
		var samples = new List<(double Milliseconds, long Bytes)>();
		for (var iteration = 0; iteration < 5; iteration++)
		{
			var before = GC.GetTotalAllocatedBytes(false); var started = Stopwatch.StartNew();
			var facts = extractor.Extract(prepared, new DependencyFactsLimits(), TestContext.Current.CancellationToken);
			started.Stop(); samples.Add((started.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(false) - before));
			Assert.Equal(1_000, facts.Declarations.Count); Assert.Equal(1_000, facts.References.Count);
		}
		var ordered = samples.OrderBy(static sample => sample.Milliseconds).ToArray(); var median = ordered[2];
		output.WriteLine($"C extraction: median={median.Milliseconds:F3} ms, allocated={median.Bytes} bytes, range={ordered[0].Milliseconds:F3}-{ordered[^1].Milliseconds:F3} ms");
	}

	[Fact]
	[Trait("Category", "LocalPerformance")]
	public void PhpExtractionReportsStableIsolatedCost()
	{
		var source = "<?php\nnamespace Generated;\n" + string.Join('\n', Enumerable.Range(0, 1_000)
			.Select(static index => $"class Type{index} {{ private Type{(index + 1) % 1000} $value; }}"));
		var prepared = new PreparedDependencySource("Generated.php", "Generated.php", "root:php", LanguageId.Php,
			"fixture", "fixture", source);
		using var extractor = new TreeSitterDependencyFactExtractor();
		_ = extractor.Extract(prepared, new DependencyFactsLimits(), TestContext.Current.CancellationToken);
		var samples = new List<(double Milliseconds, long Bytes)>();
		for (var iteration = 0; iteration < 5; iteration++)
		{
			var before = GC.GetTotalAllocatedBytes(false); var started = Stopwatch.StartNew();
			var facts = extractor.Extract(prepared, new DependencyFactsLimits(), TestContext.Current.CancellationToken);
			started.Stop(); samples.Add((started.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(false) - before));
			Assert.Equal(1_000, facts.Declarations.Count); Assert.Equal(1_000, facts.References.Count);
		}
		var ordered = samples.OrderBy(static sample => sample.Milliseconds).ToArray(); var median = ordered[2];
		output.WriteLine($"PHP extraction: median={median.Milliseconds:F3} ms, allocated={median.Bytes} bytes, range={ordered[0].Milliseconds:F3}-{ordered[^1].Milliseconds:F3} ms");
	}

	[Fact]
	public void CSharpExtraction_WorkCountersRemainLinear()
	{
		var small = MeasureWork(2_000);
		var large = MeasureWork(4_000);
		output.WriteLine($"C# adapter work: 2000 facts={small.Facts}, ranges={small.VisitedRanges}, comparisons={small.Comparisons}; " +
		                 $"4000 facts={large.Facts}, ranges={large.VisitedRanges}, comparisons={large.Comparisons}");

		Assert.Equal(4_000, small.Facts);
		Assert.Equal(8_000, large.Facts);
		Assert.InRange(large.VisitedRanges, small.VisitedRanges * 2, small.VisitedRanges * 2 + 8);
		Assert.InRange(large.Comparisons, small.Comparisons * 2, small.Comparisons * 2 + 8);
	}

	[Fact]
	[Trait("Category", "LocalPerformance")]
	public void CSharpExtraction_WithThousandsOfDeclarationsAndReferences_RemainsNearLinear()
	{
		_ = Measure(500);
		var samples = Enumerable.Range(0, 5)
			.SelectMany(static _ => new[] { (Count: 2_000, Elapsed: Measure(2_000)), (Count: 4_000, Elapsed: Measure(4_000)) })
			.ToArray();
		var small = Median(samples.Where(static sample => sample.Count == 2_000).Select(static sample => sample.Elapsed));
		var large = Median(samples.Where(static sample => sample.Count == 4_000).Select(static sample => sample.Elapsed));
		var ratio = large.TotalMilliseconds / small.TotalMilliseconds;

		output.WriteLine($"C# fact extraction: 2000={small.TotalMilliseconds:F3} ms; " +
		                 $"4000={large.TotalMilliseconds:F3} ms; ratio={ratio:F2}x");
		Assert.True(large < TimeSpan.FromSeconds(5));
	}

	private static TimeSpan Median(IEnumerable<TimeSpan> values)
	{
		var samples = values.Order().ToArray();
		return samples[samples.Length / 2];
	}

	private static TimeSpan Measure(int count)
	{
		var declarations = new DependencySyntaxCapture[count];
		var references = new DependencySyntaxCapture[count];
		for (var index = 0; index < count; index++)
		{
			var start = index * 100;
			declarations[index] = new DependencySyntaxCapture(
				"declaration.class", "class_declaration", $"class Type{index} {{ }}", index + 1,
				start, start + 99, $"Type{index}");
			references[index] = new DependencySyntaxCapture(
				"reference.property_type", "identifier", "Target", index + 1,
				start + 20, start + 26);
		}
		var context = new DependencyExtractionContext(
			"Generated.cs", "fixture", LanguageId.CSharp, new string(' ', count * 100), "fixture",
			false, new Dictionary<string, int>(StringComparer.Ordinal), declarations, references);
		var adapter = new CSharpDependencyLanguageAdapter();

		var started = Stopwatch.StartNew();
		var facts = adapter.Extract(context, new DependencyFactsLimits());
		started.Stop();

		Assert.Equal(count, facts.Declarations.Count);
		Assert.Equal(count, facts.References.Count);
		return started.Elapsed;
	}

	private static WorkMeasurement MeasureWork(int count)
	{
		var declarations = new DependencySyntaxCapture[count];
		var references = new DependencySyntaxCapture[count];
		for (var index = 0; index < count; index++)
		{
			var start = index * 100;
			declarations[index] = new DependencySyntaxCapture(
				"declaration.class", "class_declaration", $"class Type{index} {{ }}", index + 1,
				start, start + 99, $"Type{index}");
			references[index] = new DependencySyntaxCapture(
				"reference.property_type", "identifier", "Target", index + 1,
				start + 20, start + 26);
		}
		var context = new DependencyExtractionContext(
			"Generated.cs", "fixture", LanguageId.CSharp, new string(' ', count * 100), "fixture",
			false, new Dictionary<string, int>(StringComparer.Ordinal), declarations, references);
		var facts = new CSharpDependencyLanguageAdapter().Extract(context, new DependencyFactsLimits());
		return new WorkMeasurement(
			facts.Declarations.Count + facts.References.Count,
			context.Work.VisitedRanges,
			context.Work.Comparisons);
	}

	private readonly record struct WorkMeasurement(int Facts, long VisitedRanges, long Comparisons);
}
