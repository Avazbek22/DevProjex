using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using DevProjex.Application.Preview;
using DevProjex.Application.Services;
using DevProjex.Avalonia.Controls;

AppBuilder.Configure<global::Avalonia.Application>()
	.UseHeadless(new AvaloniaHeadlessPlatformOptions())
	.SetupWithoutStarting();

const int lineCount = 100_000;
const int iterations = 5;
const string line = "internal static string Format(int value) => $\"Привет 文書 😀 {value:D6}\"; // preview payload";
var source = string.Join("\r\n", Enumerable.Repeat(line, lineCount));
var expectedSelection = string.Join("\n", Enumerable.Repeat(line, lineCount));
var expectedClipboard = string.Join(Environment.NewLine, Enumerable.Repeat(line, lineCount));
var copyMethod = typeof(VirtualizedPreviewTextControl).GetMethod(
	"CopySelectionToClipboardUsingAsync",
	BindingFlags.Instance | BindingFlags.NonPublic)
	?? throw new MissingMethodException("The preview clipboard entry point was not found.");

using (var document = new InMemoryPreviewTextDocument(source))
	Measure("in-memory", document);
using (var document = new PreviewDocumentBuilder(new FileContentAnalyzer()).CreateDocument(source))
	Measure("file-backed", document);

void Measure(string storage, IPreviewTextDocument document)
{
	var control = new VirtualizedPreviewTextControl { Document = document };
	control.SelectAll();
	string? clipboardPayload = null;
	Func<string, Task> writeText = text =>
	{
		clipboardPayload = text;
		return Task.CompletedTask;
	};
	control.CopyingToClipboard += (_, _) =>
	{
		if (ReadPendingCount(control) != expectedClipboard.Length)
			throw new InvalidOperationException("Clipboard admission length changed.");
	};
	MeasureOperation("selection", control.GetSelectedText, expectedSelection);
	MeasureOperation("clipboard", () =>
	{
		clipboardPayload = null;
		var copy = (Task?)copyMethod.Invoke(control, [writeText])
			?? throw new InvalidOperationException("Clipboard copy did not return a task.");
		copy.GetAwaiter().GetResult();
		return clipboardPayload ?? throw new InvalidOperationException("Clipboard copy did not publish text.");
	}, expectedClipboard);

	void MeasureOperation(string operation, Func<string> execute, string expected)
	{
		Verify(execute(), expected);
		var elapsed = new double[iterations];
		var allocations = new long[iterations];
		for (var iteration = 0; iteration < iterations; iteration++)
		{
			var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
			var started = Stopwatch.GetTimestamp();
			var actual = execute();
			elapsed[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
			allocations[iteration] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
			Verify(actual, expected);
		}
		Array.Sort(elapsed);
		Array.Sort(allocations);
		Console.WriteLine(FormattableString.Invariant(
			$"{storage}/{operation}: chars={expected.Length}, median_ms={elapsed[iterations / 2]:F3}, median_allocated_bytes={allocations[iterations / 2]}"));
		Console.WriteLine("runs_ms=" + string.Join(",", elapsed.Select(value => value.ToString("F3", CultureInfo.InvariantCulture))));
	}
}

static long ReadPendingCount(VirtualizedPreviewTextControl control) =>
	(long)(typeof(VirtualizedPreviewTextControl).GetProperty(
		"PendingClipboardCharacterCount",
		BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(control)
		?? throw new MissingMemberException("The preview clipboard admission count was not found."));

static void Verify(string actual, string expected)
{
	if (!string.Equals(actual, expected, StringComparison.Ordinal))
		throw new InvalidOperationException("The selected text or clipboard line endings changed.");
}
