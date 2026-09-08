using System.Diagnostics;
using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyLanguageAdapterPerformanceIntegrationTests(ITestOutputHelper output)
{
	[Fact]
	[Trait("Category", "LocalPerformance")]
	public void CSharpExtraction_WithThousandsOfDeclarationsAndReferences_RemainsNearLinear()
	{
		_ = Measure(500);
		var small = MeasureMedian(2_000, 5);
		var large = MeasureMedian(4_000, 5);
		var ratio = large.TotalMilliseconds / small.TotalMilliseconds;

		output.WriteLine($"C# fact extraction: 2000={small.TotalMilliseconds:F3} ms; " +
		                 $"4000={large.TotalMilliseconds:F3} ms; ratio={ratio:F2}x");
		Assert.True(large < TimeSpan.FromSeconds(5));
		Assert.True(ratio < 3,
			$"Doubling declarations and references took {ratio:F2}x; expected sub-quadratic scaling.");
	}

	private static TimeSpan MeasureMedian(int count, int repetitions)
	{
		var samples = Enumerable.Range(0, repetitions)
			.Select(_ => Measure(count))
			.OrderBy(static elapsed => elapsed)
			.ToArray();
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
}
