using System.Diagnostics;
using System.Globalization;
using System.Text;
using DevProjex.Application.Preview;
using DevProjex.Application.Services;

const int lineCount = 200_000;
const int iterations = 7;
const string line = "internal static string Format(int value) => $\"Привет 文書 😀 {value:D6}\"; // preview payload";
var selection = new PreviewSelectionRange(51, 3, lineCount - 50, line.Length - 4);
var selectedLines = selection.EndLine - selection.StartLine + 1L;
var expectedCharacters = selectedLines * line.Length - 7 + selectedLines - 1;
var expected = new ExportOutputMetrics(
	selectedLines,
	expectedCharacters,
	(expectedCharacters + 3) / 4);

using (var document = new InMemoryPreviewTextDocument(string.Concat(Enumerable.Repeat(line + "\r\n", lineCount))))
	Measure("in-memory", document);

var storagePath = Path.Combine(Path.GetTempPath(), $"devprojex-preview-benchmark-{Guid.NewGuid():N}.txt");
try
{
	var encodedLine = Encoding.UTF8.GetBytes(line + "\r\n");
	var offsets = new long[lineCount];
	using (var stream = new FileStream(storagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
	{
		for (var index = 0; index < lineCount; index++)
		{
			offsets[index] = (long)index * encodedLine.Length;
			stream.Write(encodedLine);
		}
	}
	using var document = new FileBackedPreviewTextDocument(
		storagePath,
		offsets,
		(long)encodedLine.Length * lineCount,
		line.Length,
		(long)(line.Length + 2) * lineCount);
	Measure("file-backed", document);
}
finally
{
	File.Delete(storagePath);
}

void Measure(string storage, IPreviewTextDocument document)
{
	Verify(PreviewSelectionMetricsCalculator.Calculate(document, selection));
	var elapsed = new double[iterations];
	var allocations = new long[iterations];
	for (var iteration = 0; iteration < iterations; iteration++)
	{
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var started = Stopwatch.GetTimestamp();
		var actual = PreviewSelectionMetricsCalculator.Calculate(document, selection);
		elapsed[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
		allocations[iteration] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
		Verify(actual);
	}
	Array.Sort(elapsed);
	Array.Sort(allocations);
	Console.WriteLine(FormattableString.Invariant(
		$"{storage}: lines={selectedLines}, chars={expected.Chars}, tokens={expected.Tokens}, median_ms={elapsed[iterations / 2]:F3}, median_allocated_bytes={allocations[iterations / 2]}"));
	Console.WriteLine("runs_ms=" + string.Join(",", elapsed.Select(value => value.ToString("F3", CultureInfo.InvariantCulture))));
}

void Verify(ExportOutputMetrics actual)
{
	if (actual != expected)
		throw new InvalidOperationException($"Selection metrics changed: expected {expected}, actual {actual}.");
}
