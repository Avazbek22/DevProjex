using System.Diagnostics;
using DevProjex.Application.Preview;

namespace DevProjex.Tests.Unit;

public sealed class PreviewTextDocumentSearchPerformanceTests(ITestOutputHelper output)
{
	[Fact]
	[Trait("Category", "LocalPerformance")]
	public void FileBackedFullScanBenchmark()
	{
		if (!string.Equals(
			    Environment.GetEnvironmentVariable("DEVPROJEX_RUN_LARGE_PERF_TESTS"),
			    "1",
			    StringComparison.Ordinal))
		{
			Assert.Skip("Set DEVPROJEX_RUN_LARGE_PERF_TESTS=1 for the pre-release performance gate.");
		}

		const int lineCount = 200_000;
		using var temp = new TemporaryDirectory();
		using var document = CreateDocument(temp.Path, lineCount);
		_ = PreviewTextDocumentSearch.FindAll(
			document,
			"absent-needle",
			TestContext.Current.CancellationToken);

		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var stopwatch = Stopwatch.StartNew();
		var matches = PreviewTextDocumentSearch.FindAll(
			document,
			"absent-needle",
			TestContext.Current.CancellationToken);
		stopwatch.Stop();

		Assert.Empty(matches);
		output.WriteLine(
			$"File-backed preview search: {stopwatch.Elapsed.TotalMilliseconds:F3} ms / " +
			$"{GC.GetAllocatedBytesForCurrentThread() - allocatedBefore:N0} B for {lineCount:N0} lines.");
	}

	private static FileBackedPreviewTextDocument CreateDocument(string directory, int lineCount)
	{
		var storagePath = Path.Combine(directory, "search.preview.txt");
		var offsets = new long[lineCount];
		long byteOffset = 0;
		long characterCount = 0;
		using (var stream = new FileStream(storagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
		{
			for (var lineIndex = 0; lineIndex < lineCount; lineIndex++)
			{
				offsets[lineIndex] = byteOffset;
				var line = $"line-{lineIndex:D6} payload 文書\n";
				var bytes = Encoding.UTF8.GetBytes(line);
				stream.Write(bytes);
				byteOffset += bytes.Length;
				characterCount += line.Length;
			}
		}

		return new FileBackedPreviewTextDocument(
			storagePath,
			offsets,
			byteOffset,
			maxLineLength: 22,
			characterCount);
	}
}
