using System.Diagnostics;
using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyLanguageAdapterPerformanceIntegrationTests(ITestOutputHelper output)
{
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
