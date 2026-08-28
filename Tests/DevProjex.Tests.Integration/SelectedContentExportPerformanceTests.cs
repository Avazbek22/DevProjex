using System.Diagnostics;
using System.Globalization;
using DevProjex.Application.Compression;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Compression;
using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Tests.Integration;

public sealed class SelectedContentExportPerformanceTests(ITestOutputHelper output)
{
	private const string EnabledVariable = "DEVPROJEX_RUN_SELECTED_CONTENT_BENCHMARK";
	private static readonly int[] ManyFileSizes =
	[
		32 * 1024,
		64 * 1024,
		128 * 1024,
		256 * 1024
	];

	[Fact]
	public async Task MaterializedLargeContentBenchmark()
	{
		if (!string.Equals(
			    Environment.GetEnvironmentVariable(EnabledVariable),
			    "1",
			    StringComparison.Ordinal))
		{
			Assert.Skip($"Set {EnabledVariable}=1 to run the selected-content benchmark.");
		}

		using var project = new TemporaryDirectory();
		var path = project.CreateFile("large.txt", new string('x', 5_000_000));
		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var warmup = await service.BuildAsync(
			[path],
			TestContext.Current.CancellationToken,
			Path.GetFileName);
		Assert.EndsWith(new string('x', 128), warmup, StringComparison.Ordinal);

		var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
		var stopwatch = Stopwatch.StartNew();
		var result = await service.BuildAsync(
			[path],
			TestContext.Current.CancellationToken,
			Path.GetFileName);
		stopwatch.Stop();
		var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

		Assert.Equal(warmup, result);
		output.WriteLine(
			"characters={0:N0}, elapsed={1:F2} ms, allocated={2:N0} B",
			result.Length,
			stopwatch.Elapsed.TotalMilliseconds,
			allocatedBytes);
	}

	[Fact]
	public async Task ManyFileMaterializedAndStreamingBenchmark()
	{
		if (!string.Equals(
		    Environment.GetEnvironmentVariable(EnabledVariable),
		    "1",
		    StringComparison.Ordinal))
		{
			Assert.Skip($"Set {EnabledVariable}=1 to run the selected-content benchmark.");
		}

		using var project = new TemporaryDirectory();
		var paths = CreateManyFileFixture(project);
		var service = new SelectedContentExportService(new FileContentAnalyzer());
		using var compressionSession = CodeCompressionFactory.CreateSession();
		using var redactionSession = new SecretRedactionSession(new GitleaksSecretDetector());
		var modes = new[]
		{
			new BenchmarkMode("raw", null),
			new BenchmarkMode(
				"compression",
				ContentTransformationContext.For(
					new CodeCompressionContext(project.Path, compressionSession),
					redaction: null)),
			new BenchmarkMode(
				"redaction",
				ContentTransformationContext.For(
					compression: null,
					new SecretRedactionContext(project.Path, redactionSession)))
		};

		foreach (var mode in modes)
		{
			var coldMaterialized = await MeasureMaterializedAsync(service, paths, mode.Context);
			var warmMaterialized = await MeasureMaterializedAsync(service, paths, mode.Context);
			var streaming = await MeasureStreamingAsync(service, paths, mode.Context);
			output.WriteLine(
				"mode={0}, files={1:N0}, sourceBytes={2:N0}, coldMaterialized={3:F2} ms, " +
				"warmMaterialized={4:F2} ms, streamNull={5:F2} ms, characters={6:N0}",
				mode.Name,
				paths.Length,
				paths.Sum(static path => new FileInfo(path).Length),
				coldMaterialized.Elapsed.TotalMilliseconds,
				warmMaterialized.Elapsed.TotalMilliseconds,
				streaming.TotalMilliseconds,
				warmMaterialized.Characters);
		}
	}

	private static string[] CreateManyFileFixture(TemporaryDirectory project)
	{
		const int repetitionsPerSize = 6;
		var paths = new string[ManyFileSizes.Length * repetitionsPerSize];
		var index = 0;
		foreach (var size in ManyFileSizes)
		{
			for (var repetition = 0; repetition < repetitionsPerSize; repetition++)
			{
				var relativePath = $"src/Generated{index:D2}.cs";
				paths[index] = project.CreateFile(relativePath, CreateSource(index, size));
				index++;
			}
		}
		return paths;
	}

	private static string CreateSource(int fileIndex, int targetCharacters)
	{
		var source = new StringBuilder(targetCharacters);
		source.Append("namespace SelectedContentBenchmark;\ninternal static class Generated")
			.Append(fileIndex.ToString("D2", CultureInfo.InvariantCulture))
			.Append("\n{\n");
		var member = 0;
		while (source.Length + 180 < targetCharacters)
		{
			source.Append("    public static string Value")
				.Append(member.ToString("D5", CultureInfo.InvariantCulture))
				.Append("()\n    {\n        const string apiToken = \"benchmark-secret-A7d9mQ2xK4vN8sR6tY3uW5zB1cE0fG2h\";\n")
				.Append("        return apiToken;\n    }\n");
			member++;
		}
		source.Append("}\n//");
		source.Append('x', targetCharacters - source.Length);
		return source.ToString();
	}

	private static async Task<MaterializedMeasurement> MeasureMaterializedAsync(
		SelectedContentExportService service,
		IReadOnlyList<string> paths,
		ContentTransformationContext? context)
	{
		var stopwatch = Stopwatch.StartNew();
		var text = await service.BuildAsync(
			paths,
			TestContext.Current.CancellationToken,
			Path.GetFileName,
			context);
		stopwatch.Stop();
		return new MaterializedMeasurement(stopwatch.Elapsed, text.Length);
	}

	private static async Task<TimeSpan> MeasureStreamingAsync(
		SelectedContentExportService service,
		IReadOnlyList<string> paths,
		ContentTransformationContext? context)
	{
		var stopwatch = Stopwatch.StartNew();
		await service.WriteAsync(
			Stream.Null,
			paths,
			TestContext.Current.CancellationToken,
			Path.GetFileName,
			context);
		stopwatch.Stop();
		return stopwatch.Elapsed;
	}

	private sealed record BenchmarkMode(string Name, ContentTransformationContext? Context);

	private readonly record struct MaterializedMeasurement(TimeSpan Elapsed, int Characters);
}
