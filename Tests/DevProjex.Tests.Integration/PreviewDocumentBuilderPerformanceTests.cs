using System.Diagnostics;
using DevProjex.Application.Preview;

namespace DevProjex.Tests.Integration;

public sealed class PreviewDocumentBuilderPerformanceTests(ITestOutputHelper output)
{
	private const string EnabledVariable = "DEVPROJEX_RUN_PREVIEW_STORAGE_BENCHMARK";

	[Fact]
	public async Task SmallPreviewStorageBenchmark()
	{
		if (!string.Equals(
				Environment.GetEnvironmentVariable(EnabledVariable),
				"1",
				StringComparison.Ordinal))
		{
			Assert.Skip($"Set {EnabledVariable}=1 to run the preview storage benchmark.");
		}

		using var project = new TemporaryDirectory();
		var file = project.CreateFile("sample.cs", string.Join('\n', Enumerable.Repeat("internal sealed class Sample {}", 100)));
		var builder = new PreviewDocumentBuilder(new FileContentAnalyzer());
		const int iterations = 200;
		using (var warmup = await builder.BuildContentDocumentAsync(
			       [file],
			       TestContext.Current.CancellationToken,
			       Path.GetFileName))
		{
			Assert.IsType<InMemoryPreviewTextDocument>(warmup);
		}

		var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
		var stopwatch = Stopwatch.StartNew();
		for (var iteration = 0; iteration < iterations; iteration++)
		{
			using var document = await builder.BuildContentDocumentAsync(
				[file],
				TestContext.Current.CancellationToken,
				Path.GetFileName);
			Assert.IsType<InMemoryPreviewTextDocument>(document);
		}
		stopwatch.Stop();
		var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

		output.WriteLine(
			"iterations={0}, elapsed={1:F2} ms, allocated={2:N0} B",
			iterations,
			stopwatch.Elapsed.TotalMilliseconds,
			allocatedBytes);
	}

	[Fact]
	public async Task FileBackedStreamingBenchmark()
	{
		if (!string.Equals(
			    Environment.GetEnvironmentVariable(EnabledVariable),
			    "1",
			    StringComparison.Ordinal))
		{
			Assert.Skip($"Set {EnabledVariable}=1 to run the preview storage benchmark.");
		}

		using var project = new TemporaryDirectory();
		var file = project.CreateFile("large.txt", new string('x', 600_000));
		using var document = await new PreviewDocumentBuilder(new FileContentAnalyzer())
			.BuildContentDocumentAsync(
				[file],
				TestContext.Current.CancellationToken,
				Path.GetFileName);
		Assert.IsType<FileBackedPreviewTextDocument>(document);
		await document.WriteToAsync(Stream.Null, TestContext.Current.CancellationToken);

		const int iterations = 200;
		var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
		var stopwatch = Stopwatch.StartNew();
		for (var iteration = 0; iteration < iterations; iteration++)
			await document.WriteToAsync(Stream.Null, TestContext.Current.CancellationToken);
		stopwatch.Stop();
		var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

		output.WriteLine(
			"iterations={0}, elapsed={1:F2} ms, allocated={2:N0} B",
			iterations,
			stopwatch.Elapsed.TotalMilliseconds,
			allocatedBytes);
	}
}
