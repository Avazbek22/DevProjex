using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using DevProjex.Application.Context;
using DevProjex.Application.Dependencies;
using DevProjex.Application.Selection;
using DevProjex.Kernel;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Contracts;
using DevProjex.Kernel.Models;
using DevProjex.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

if (args.FirstOrDefault() == "search-declarations")
{
	SearchDeclarationBenchmark.Run(args[1..]);
	return;
}

if (args.FirstOrDefault() == "live-roots")
{
	LiveRootRetentionBenchmark.Run(args[1..]);
	return;
}

if (args.FirstOrDefault() == "pack-attribution")
{
	PackAttributionBenchmark.Run(args[1..]);
	return;
}

if (args.FirstOrDefault() == "search-retention")
{
	SearchRetentionBenchmark.Run(args[1..]);
	return;
}

if (args.FirstOrDefault() == "search-merge")
{
	SearchMergeBenchmark.Run(args[1..]);
	return;
}

var options = BenchmarkOptions.Parse(args);
var temporaryCorpus = options.SyntheticFileCount is null ? null : SyntheticCorpus.Create(options.SyntheticFileCount.Value);
var root = temporaryCorpus?.Path ?? options.Root ?? throw new ArgumentException("Specify --root or --synthetic-files.");
try
{
	if (options.ColdFirstSearch)
	{
		await RunColdFirstSearchAsync(root, options);
		return;
	}

	await using var session = await McpProcessSession.StartAsync(options.Host, root, options.Timeout);
	var operations = CreateOperations(root, options)
		.Where(operation => options.Only is null || options.Only.Contains(operation.Name))
		.ToArray();
	if (operations.Length == 0)
		throw new ArgumentException("--only did not match a benchmark operation.");
	Console.WriteLine("operation,median_ms,min_ms,max_ms,spread_ms,median_client_alloc_bytes,min_client_alloc_bytes,max_client_alloc_bytes,response_chars");
	foreach (var operation in operations)
	{
		_ = await operation.Invoke(session.Client, session.Token);
		var samples = new List<Sample>(options.Repetitions);
		for (var repetition = 0; repetition < options.Repetitions; repetition++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			var beforeBytes = GC.GetTotalAllocatedBytes(precise: true);
			var timer = Stopwatch.StartNew();
			var response = await operation.Invoke(session.Client, session.Token);
			timer.Stop();
			var allocated = GC.GetTotalAllocatedBytes(precise: true) - beforeBytes;
			var responseText = ResponseText(response);
			if (response.IsError == true)
				throw new InvalidOperationException($"{operation.Name} failed: {responseText}");
			samples.Add(new Sample(timer.Elapsed.TotalMilliseconds, allocated, responseText.Length));
		}

		var elapsed = samples.Select(static sample => sample.ElapsedMilliseconds).Order().ToArray();
		var allocatedBytes = samples.Select(static sample => sample.ClientAllocatedBytes).Order().ToArray();
		Console.WriteLine(string.Join(',',
			operation.Name,
			Format(Median(elapsed)),
			Format(elapsed[0]),
			Format(elapsed[^1]),
			Format(elapsed[^1] - elapsed[0]),
			MedianLong(allocatedBytes).ToString(CultureInfo.InvariantCulture),
			allocatedBytes[0].ToString(CultureInfo.InvariantCulture),
			allocatedBytes[^1].ToString(CultureInfo.InvariantCulture),
			samples[^1].ResponseCharacters.ToString(CultureInfo.InvariantCulture)));
	}
}
finally
{
	temporaryCorpus?.Dispose();
}

static IReadOnlyList<BenchmarkOperation> CreateOperations(string root, BenchmarkOptions options)
{
	var relativeFile = options.File ?? FindRepresentativeFile(root);
	var seed = options.Seed ?? relativeFile;
	return
	[
		new("list_projects", static (client, token) => Call(client, "list_projects", null, token)),
		new("get_tree", static (client, token) => Call(client, "get_tree",
			new Dictionary<string, object?> { ["format"] = "text", ["max_depth"] = 4 }, token)),
		new("analyze", static (client, token) => Call(client, "analyze",
			new Dictionary<string, object?> { ["top_files"] = 20 }, token)),
		new("search_project", static (client, token) => Call(client, "search_project",
			new Dictionary<string, object?> { ["pattern"] = "DevProjex|synthetic-marker", ["max_results"] = 50 }, token)),
		new("get_file_narrow", (client, token) => Call(client, "get_file",
			new Dictionary<string, object?> { ["path"] = relativeFile, ["start_line"] = 1, ["end_line"] = 20 }, token)),
		new("get_file_wide", (client, token) => Call(client, "get_file",
			new Dictionary<string, object?> { ["path"] = relativeFile }, token)),
		Pack("pack_context_4k", 4_000),
		Pack("pack_context_16k", 16_000),
		Pack("pack_context_64k", 64_000),
		new("related_files", (client, token) => Call(client, "related_files",
			new Dictionary<string, object?> { ["path"] = seed, ["direction"] = "both" }, token)),
		new("read_pack", async (client, token) =>
		{
			var packed = await Call(client, "pack_context",
				new Dictionary<string, object?> { ["view"] = "tree-content", ["format"] = "text", ["max_tokens"] = 64_000 }, token);
			var id = Regex.Match(ResponseText(packed), "Pack stored as '([^']+)'").Groups[1].Value;
			if (id.Length == 0)
				return packed;
			return await Call(client, "read_pack",
				new Dictionary<string, object?> { ["pack_id"] = id, ["start_line"] = 1, ["end_line"] = 1_000 }, token);
		})
	];

	static BenchmarkOperation Pack(string name, long budget) => new(name,
		(client, token) => Call(client, "pack_context",
			new Dictionary<string, object?>
			{
				["view"] = "tree-content",
				["format"] = "text",
				["max_tokens"] = budget
			}, token));
}

static async Task<CallToolResult> Call(
	McpClient client,
	string name,
	IReadOnlyDictionary<string, object?>? arguments,
	CancellationToken cancellationToken) =>
	await client.CallToolAsync(name, arguments, progress: null, options: null, cancellationToken);

static string ResponseText(CallToolResult response) =>
	string.Join('\n', response.Content.OfType<TextContentBlock>().Select(static block => block.Text));

static async Task RunColdFirstSearchAsync(string root, BenchmarkOptions options)
{
	var operations = CreateOperations(root, options).ToDictionary(static operation => operation.Name);
	var search = options.ColdSearchPattern is null
		? operations["search_project"]
		: new BenchmarkOperation("search_project", (client, token) => Call(client, "search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = options.ColdSearchPattern,
				["max_results"] = 50
			}, token));
	Console.WriteLine("run,operation,elapsed_ms,server_cpu_ms,client_alloc_bytes,response_chars,inspected_sources,eligible_sources,server_peak_rss_bytes");
	for (var run = 1; run <= options.Repetitions; run++)
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		var allocatedBeforeStartup = GC.GetTotalAllocatedBytes(precise: true);
		var startupTimer = Stopwatch.StartNew();
		await using var session = await McpProcessSession.StartAsync(options.Host, root, options.Timeout);
		startupTimer.Stop();
		Console.WriteLine(string.Join(',',
			run,
			"mcp_initialize",
			Format(startupTimer.Elapsed.TotalMilliseconds),
			Format(session.ServerCpuTime.TotalMilliseconds),
			(GC.GetTotalAllocatedBytes(precise: true) - allocatedBeforeStartup).ToString(CultureInfo.InvariantCulture),
			0,
			string.Empty,
			string.Empty,
			session.ServerPeakWorkingSetBytes.ToString(CultureInfo.InvariantCulture)));

		await MeasureColdStepAsync(run, "list_projects", operations["list_projects"], session);
		await MeasureColdStepAsync(run, "get_tree", operations["get_tree"], session);
		await MeasureColdStepAsync(run, "search_project_first", search, session);
		await MeasureColdStepAsync(run, "search_project_repeat", search, session);
	}
}

static async Task MeasureColdStepAsync(
	int run,
	string label,
	BenchmarkOperation operation,
	McpProcessSession session)
{
	GC.Collect();
	GC.WaitForPendingFinalizers();
	var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
	var cpuBefore = session.ServerCpuTime;
	var timer = Stopwatch.StartNew();
	var response = await operation.Invoke(session.Client, session.Token);
	timer.Stop();
	var serverCpu = session.ServerCpuTime - cpuBefore;
	var responseText = ResponseText(response);
	if (response.IsError == true)
		throw new InvalidOperationException($"{operation.Name} failed: {responseText}");
	var boundary = Regex.Match(responseText, @"sources inspected=(\d+)/(\d+)");
	Console.WriteLine(string.Join(',',
		run,
		label,
		Format(timer.Elapsed.TotalMilliseconds),
		Format(serverCpu.TotalMilliseconds),
		(GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore).ToString(CultureInfo.InvariantCulture),
		responseText.Length.ToString(CultureInfo.InvariantCulture),
		boundary.Success ? boundary.Groups[1].Value : string.Empty,
		boundary.Success ? boundary.Groups[2].Value : string.Empty,
		session.ServerPeakWorkingSetBytes.ToString(CultureInfo.InvariantCulture)));
}

static string FindRepresentativeFile(string root)
{
	var preferred = Path.Combine(root, "Apps", "Mcp", "DevProjexMcpTools.cs");
	if (File.Exists(preferred))
		return "Apps/Mcp/DevProjexMcpTools.cs";
	var file = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
		.First(path => new FileInfo(path).Length is > 1_000 and < 1_000_000);
	return Path.GetRelativePath(root, file).Replace('\\', '/');
}

static double Median(double[] values) => values[values.Length / 2];
static long MedianLong(long[] values) => values[values.Length / 2];
static string Format(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

internal sealed record BenchmarkOperation(
	string Name,
	Func<McpClient, CancellationToken, Task<CallToolResult>> Invoke);

internal readonly record struct Sample(double ElapsedMilliseconds, long ClientAllocatedBytes, int ResponseCharacters);

internal static class SearchRetentionBenchmark
{
	private const int FileCount = 2_000;
	private const int SourceCharacters = 32 * 1024;
	private const int CandidateCapacity = 5_000;

	public static void Run(string[] arguments)
	{
		var repetitions = 5;
		if (arguments.Length > 0)
		{
			if (arguments.Length != 2 || arguments[0] != "--repetitions")
				throw new ArgumentException("Only --repetitions is supported.");
			repetitions = int.Parse(arguments[1], CultureInfo.InvariantCulture);
		}
		if (repetitions < 3)
			throw new ArgumentOutOfRangeException(nameof(repetitions));

		Console.WriteLine(
			"matching_percent,legacy_median_retained_bytes,legacy_spread_bytes,indexed_median_retained_bytes,indexed_spread_bytes,indexed_peak_bound_bytes");
		foreach (var matchingPercent in new[] { 1, 50, 100 })
		{
			var matchingFiles = checked(FileCount * matchingPercent / 100);
			var legacy = Enumerable.Range(0, repetitions)
				.Select(_ => MeasureLegacy(matchingFiles))
				.Order()
				.ToArray();
			var indexed = Enumerable.Range(0, repetitions)
				.Select(_ => MeasureIndexed(matchingFiles))
				.Order()
				.ToArray();
			var indexedMedian = indexed[indexed.Length / 2];
			Console.WriteLine(string.Join(',',
				matchingPercent.ToString(CultureInfo.InvariantCulture),
				legacy[legacy.Length / 2].ToString(CultureInfo.InvariantCulture),
				(legacy[^1] - legacy[0]).ToString(CultureInfo.InvariantCulture),
				indexedMedian.ToString(CultureInfo.InvariantCulture),
				(indexed[^1] - indexed[0]).ToString(CultureInfo.InvariantCulture),
				checked(indexedMedian + SourceCharacters * sizeof(char)).ToString(CultureInfo.InvariantCulture)));
		}
	}

	private static long MeasureLegacy(int matchingFiles)
	{
		Collect();
		var before = GC.GetTotalMemory(forceFullCollection: false);
		var retained = new Dictionary<string, string>(matchingFiles, StringComparer.Ordinal);
		for (var index = 0; index < matchingFiles; index++)
		{
			var path = $"src/File{index:D5}.cs";
			retained.Add(path, string.Concat(path, new string('x', SourceCharacters - path.Length)));
		}
		Collect();
		var bytes = Math.Max(0, GC.GetTotalMemory(forceFullCollection: false) - before);
		GC.KeepAlive(retained);
		return bytes;
	}

	private static long MeasureIndexed(int matchingFiles)
	{
		Collect();
		var before = GC.GetTotalMemory(forceFullCollection: false);
		var collector = new McpSearchCandidateCollector(CandidateCapacity, int.MaxValue);
		for (var index = 0; index < matchingFiles; index++)
		{
			var path = $"src/File{index:D5}.cs";
			var text = $"needle-{index:D5}";
			collector.Consider(new McpSearchCandidate(
				new McpSearchRenderedGroup(
					path,
					path,
					[1],
					[new McpSearchGroupLine(1, IsMatch: true, text)]),
				1,
				0,
				text,
				text.Length,
				ProtectedLines: [new McpSearchProtectedLine(1, 1)]));
		}
		Collect();
		var bytes = Math.Max(0, GC.GetTotalMemory(forceFullCollection: false) - before);
		GC.KeepAlive(collector);
		return bytes;
	}

	private static void Collect()
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
	}
}

internal static class SearchDeclarationBenchmark
{
	public static void Run(string[] arguments)
	{
		var repetitions = 5;
		for (var index = 0; index < arguments.Length; index++)
		{
			if (arguments[index] != "--repetitions" || index + 1 >= arguments.Length)
				throw new ArgumentException($"Unknown or incomplete argument: {arguments[index]}");
			repetitions = int.Parse(arguments[++index], CultureInfo.InvariantCulture);
		}
		if (repetitions < 3)
			throw new ArgumentOutOfRangeException(nameof(repetitions), "At least three repetitions are required.");

		Console.WriteLine("declarations,median_ms,min_ms,max_ms,spread_ms,median_alloc_bytes,visited_declarations");
		foreach (var count in new[] { 1_000, 5_000, 10_000 })
		{
			var fixture = CreateFixture(count);
			_ = Measure(fixture);
			var samples = Enumerable.Range(0, repetitions).Select(_ => Measure(fixture)).ToArray();
			var elapsed = samples.Select(static sample => sample.ElapsedMilliseconds).Order().ToArray();
			var allocations = samples.Select(static sample => sample.AllocatedBytes).Order().ToArray();
			var visits = samples.Select(static sample => sample.VisitedDeclarations).Distinct().Single();
			Console.WriteLine(string.Join(',',
				count.ToString(CultureInfo.InvariantCulture),
				FormatValue(MedianValue(elapsed)),
				FormatValue(elapsed[0]),
				FormatValue(elapsed[^1]),
				FormatValue(elapsed[^1] - elapsed[0]),
				MedianValue(allocations).ToString(CultureInfo.InvariantCulture),
				visits.ToString(CultureInfo.InvariantCulture)));
		}
	}

	private static SearchDeclarationFixture CreateFixture(int count)
	{
		var content = new System.Text.StringBuilder(count * 40);
		var declarations = new NavigationDeclaration[count];
		var matches = new McpSearchMatchContext[count];
		for (var index = 0; index < count; index++)
		{
			var line = index + 1;
			var text = $"void Method{index:D5}() {{ needle(); }}";
			var offset = content.Length;
			content.AppendLine(text);
			declarations[index] = new NavigationDeclaration(
				$"Fixture.Method{index:D5}",
				NavigationSymbolKind.Method,
				"Fixture",
				line,
				line,
				"benchmark")
			{
				StartIndex = offset,
				EndIndex = content.Length - 1
			};
			matches[index] = new McpSearchMatchContext(
				[line],
				[new McpTextLineRange(line, offset, text.Length)],
				StartsNewGroup: false);
		}
		return new SearchDeclarationFixture(content.ToString(), declarations, matches);
	}

	private static SearchDeclarationSample Measure(SearchDeclarationFixture fixture)
	{
		var visits = 0L;
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var timer = Stopwatch.StartNew();
		var collector = new McpSearchCandidateCollector(50, 2_000_000);
		DevProjexMcpTools.AddSearchCandidates(
			collector,
			"Fixture.cs",
			"Fixture.cs",
			fixture.Content,
			fixture.Matches,
			fixture.Declarations,
			new McpSearchRegex("needle", ignoreCase: false),
			0,
			explicitScope: false,
			declarationVisited: () => visits++);
		timer.Stop();
		return new SearchDeclarationSample(
			timer.Elapsed.TotalMilliseconds,
			GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
			visits);
	}

	private static double MedianValue(double[] values) => values[values.Length / 2];
	private static long MedianValue(long[] values) => values[values.Length / 2];
	private static string FormatValue(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

	private sealed record SearchDeclarationFixture(
		string Content,
		IReadOnlyList<NavigationDeclaration> Declarations,
		IReadOnlyList<McpSearchMatchContext> Matches);

	private readonly record struct SearchDeclarationSample(
		double ElapsedMilliseconds,
		long AllocatedBytes,
		long VisitedDeclarations);
}

internal static class PackAttributionBenchmark
{
	public static void Run(string[] arguments)
	{
		var repetitions = 5;
		if (arguments.Length > 0)
		{
			if (arguments.Length != 2 || arguments[0] != "--repetitions")
				throw new ArgumentException("Only --repetitions is supported.");
			repetitions = int.Parse(arguments[1], CultureInfo.InvariantCulture);
		}
		if (repetitions < 3)
			throw new ArgumentOutOfRangeException(nameof(repetitions));

		Console.WriteLine(
			"files,legacy_median_ms,legacy_spread_ms,indexed_median_ms,indexed_spread_ms,legacy_median_alloc_bytes,indexed_median_alloc_bytes");
		foreach (var count in new[] { 1_000, 10_000, 100_000 })
		{
			var paths = Enumerable.Range(0, count)
				.Select(index => new McpStoredJournalPath(
					$"src/File{index:D6}.cs",
					0,
					0,
					[new McpStoredLineRange(index * 3 + 1, index * 3 + 2)]))
				.ToArray();
			var context = new McpStoredJournalContext("root", null, paths);
			var selected = paths[count / 2];
			var page = $"header\n{selected.RelativePath}\nbody";
			var startLine = count / 2 * 3 + 1;
			_ = MeasureLegacy(paths, page);
			_ = MeasureIndexed(context, startLine);
			var legacy = Enumerable.Range(0, repetitions)
				.Select(_ => MeasureLegacy(paths, page))
				.ToArray();
			var indexed = Enumerable.Range(0, repetitions)
				.Select(_ => MeasureIndexed(context, startLine))
				.ToArray();
			if (legacy.SelectMany(static sample => sample.Paths).Select(static path => path.RelativePath)
					.SequenceEqual(indexed.SelectMany(static sample => sample.Paths).Select(static path => path.RelativePath)) is false)
			{
				throw new InvalidOperationException("Attribution result changed.");
			}
			var legacyElapsed = legacy.Select(static sample => sample.ElapsedMilliseconds).Order().ToArray();
			var indexedElapsed = indexed.Select(static sample => sample.ElapsedMilliseconds).Order().ToArray();
			var legacyAllocations = legacy.Select(static sample => sample.AllocatedBytes).Order().ToArray();
			var indexedAllocations = indexed.Select(static sample => sample.AllocatedBytes).Order().ToArray();
			Console.WriteLine(string.Join(',',
				count.ToString(CultureInfo.InvariantCulture),
				FormatValue(MedianValue(legacyElapsed)),
				FormatValue(legacyElapsed[^1] - legacyElapsed[0]),
				FormatValue(MedianValue(indexedElapsed)),
				FormatValue(indexedElapsed[^1] - indexedElapsed[0]),
				MedianValue(legacyAllocations).ToString(CultureInfo.InvariantCulture),
				MedianValue(indexedAllocations).ToString(CultureInfo.InvariantCulture)));
		}
	}

	private static PackAttributionSample MeasureLegacy(
		IReadOnlyList<McpStoredJournalPath> paths,
		string page)
	{
		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var timer = Stopwatch.StartNew();
		var result = paths.Where(path => page.Contains(path.RelativePath, StringComparison.Ordinal)).ToArray();
		timer.Stop();
		return new PackAttributionSample(
			timer.Elapsed.TotalMilliseconds,
			GC.GetAllocatedBytesForCurrentThread() - allocated,
			result);
	}

	private static PackAttributionSample MeasureIndexed(
		McpStoredJournalContext context,
		int startLine)
	{
		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var timer = Stopwatch.StartNew();
		var result = context.PathsForPage(startLine, startLine + 1);
		timer.Stop();
		return new PackAttributionSample(
			timer.Elapsed.TotalMilliseconds,
			GC.GetAllocatedBytesForCurrentThread() - allocated,
			result);
	}

	private static double MedianValue(double[] values) => values[values.Length / 2];
	private static long MedianValue(long[] values) => values[values.Length / 2];
	private static string FormatValue(double value) => value.ToString("0.000000", CultureInfo.InvariantCulture);

	private readonly record struct PackAttributionSample(
		double ElapsedMilliseconds,
		long AllocatedBytes,
		IReadOnlyList<McpStoredJournalPath> Paths);
}

internal static class LiveRootRetentionBenchmark
{
	public static void Run(string[] arguments)
	{
		var repetitions = 5;
		var nodesPerRoot = 10_000;
		for (var index = 0; index < arguments.Length; index++)
		{
			var option = arguments[index];
			if (index + 1 >= arguments.Length)
				throw new ArgumentException($"Incomplete argument: {option}");
			var value = arguments[++index];
			switch (option)
			{
				case "--repetitions":
					repetitions = int.Parse(value, CultureInfo.InvariantCulture);
					break;
				case "--nodes-per-root":
					nodesPerRoot = int.Parse(value, CultureInfo.InvariantCulture);
					break;
				default:
					throw new ArgumentException($"Unknown argument: {option}");
			}
		}
		if (repetitions < 3)
			throw new ArgumentOutOfRangeException(nameof(repetitions), "At least three repetitions are required.");
		if (nodesPerRoot < 1)
			throw new ArgumentOutOfRangeException(nameof(nodesPerRoot));

		Console.WriteLine("roots,nodes_per_root,median_retained_bytes,min_bytes,max_bytes,spread_bytes");
		foreach (var rootCount in new[] { 1, 8, 50 })
		{
			var samples = Enumerable.Range(0, repetitions)
				.Select(_ => Measure(rootCount, nodesPerRoot))
				.Order()
				.ToArray();
			Console.WriteLine(string.Join(',',
				rootCount.ToString(CultureInfo.InvariantCulture),
				nodesPerRoot.ToString(CultureInfo.InvariantCulture),
				samples[samples.Length / 2].ToString(CultureInfo.InvariantCulture),
				samples[0].ToString(CultureInfo.InvariantCulture),
				samples[^1].ToString(CultureInfo.InvariantCulture),
				(samples[^1] - samples[0]).ToString(CultureInfo.InvariantCulture)));
		}
	}

	private static long Measure(int rootCount, int nodesPerRoot)
	{
		var rootBase = Path.Combine(Path.GetTempPath(), "devprojex-live-retention", Guid.NewGuid().ToString("N"));
		var roots = Enumerable.Range(0, rootCount)
			.Select(index => Path.Combine(rootBase, $"root-{index:D2}"))
			.ToArray();
		try
		{
			foreach (var root in roots)
				Directory.CreateDirectory(root);
			var state = new McpLiveContextState(
				new McpRootRegistry(roots),
				static () => BenchmarkProfileStore.Instance,
				TimeSpan.Zero);
			Collect();
			var before = GC.GetTotalMemory(forceFullCollection: false);
			foreach (var root in roots)
			{
				using var invocation = state.BeginInvocation();
				_ = state.ReadProfile(root);
				RecordPlan(state, root, nodesPerRoot);
			}
			Collect();
			var retained = GC.GetTotalMemory(forceFullCollection: false) - before;
			GC.KeepAlive(state);
			return Math.Max(0, retained);
		}
		finally
		{
			Directory.Delete(rootBase, recursive: true);
		}
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void RecordPlan(McpLiveContextState state, string root, int nodeCount)
	{
		var children = Enumerable.Range(0, nodeCount)
			.Select(index => new TreeNodeDescriptor(
				$"File{index}.cs",
				Path.Combine(root, $"File{index}.cs"),
				false,
				false,
				"csharp",
				[]))
			.ToArray();
		var tree = new TreeNodeDescriptor("project", root, true, false, "folder", children);
		state.RecordPlan(root, CreatePlan(root, tree, nodeCount));
	}

	private static ProjectContextPlan CreatePlan(string root, TreeNodeDescriptor tree, int fileCount) =>
		new(
			root,
			ProjectSelectionSpec.Standard,
			[],
			[],
			[],
			[],
			tree,
			tree,
			new HashSet<string>(PathComparer.Default),
			Enumerable.Range(0, fileCount).Select(index => Path.Combine(root, $"File{index}.cs")).ToArray(),
			[root],
			new ProjectAnalysisReport(
				ProjectAnalysisReport.CurrentSchemaVersion,
				DateTimeOffset.UnixEpoch,
				root,
				new ProjectAnalysisSelectionReport([], [], []),
				new ProjectAnalysisInventoryReport([], [], new ProjectTreeSummaryReport(1, fileCount, 0)),
				new ProjectAnalysisOutputMetricsReport(ProjectOutputMetricsReport.Empty, ProjectOutputMetricsReport.Empty),
				new ProjectAnalysisTimingReport(0, 0, 0),
				new ProjectAnalysisDiagnosticsReport(false, false, [])),
			[],
			new ProjectContextGitReadiness(GitFilteringMode.None, 0, false),
			"live-retention-benchmark");

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void Collect()
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
	}

	private sealed class BenchmarkProfileStore : IProjectProfileStore
	{
		public static BenchmarkProfileStore Instance { get; } = new();

		public ProjectProfileLookupResult LookupProfile(string localProjectPath, TimeSpan lockTimeout) =>
			new(ProjectProfileLookupStatus.Found, new ProjectSelectionProfile([], [], [], SelectedPaths: null));

		public bool EnsureStorageExists() => true;
		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile)
		{
			profile = new ProjectSelectionProfile([], [], [], SelectedPaths: null);
			return true;
		}
		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile) => true;
		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile, DateTimeOffset updatedUtc) => true;
		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile)
		{
		}
		public ProjectProfileClearStatus ClearAllProfiles() => ProjectProfileClearStatus.Cleared;
	}
}

internal sealed record BenchmarkOptions(
	string Host,
	string? Root,
	int? SyntheticFileCount,
	string? File,
	string? Seed,
	IReadOnlySet<string>? Only,
	int Repetitions,
	TimeSpan Timeout,
	bool ColdFirstSearch,
	string? ColdSearchPattern)
{
	public static BenchmarkOptions Parse(string[] arguments)
	{
		string? host = null;
		string? root = null;
		string? file = null;
		string? seed = null;
		IReadOnlySet<string>? only = null;
		int? synthetic = null;
		var repetitions = 5;
		var timeout = TimeSpan.FromMinutes(15);
		var coldFirstSearch = false;
		string? coldSearchPattern = null;
		for (var index = 0; index < arguments.Length; index++)
		{
			var value = index + 1 < arguments.Length ? arguments[index + 1] : null;
			switch (arguments[index])
			{
				case "--host": host = RequireValue(value, arguments[index]); index++; break;
				case "--root": root = Path.GetFullPath(RequireValue(value, arguments[index])); index++; break;
				case "--file": file = RequireValue(value, arguments[index]); index++; break;
				case "--seed": seed = RequireValue(value, arguments[index]); index++; break;
				case "--only": only = RequireValue(value, arguments[index]).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal); index++; break;
				case "--synthetic-files": synthetic = int.Parse(RequireValue(value, arguments[index]), CultureInfo.InvariantCulture); index++; break;
				case "--repetitions": repetitions = int.Parse(RequireValue(value, arguments[index]), CultureInfo.InvariantCulture); index++; break;
				case "--timeout-seconds": timeout = TimeSpan.FromSeconds(int.Parse(RequireValue(value, arguments[index]), CultureInfo.InvariantCulture)); index++; break;
				case "--cold-first-search": coldFirstSearch = true; break;
				case "--cold-search-pattern": coldSearchPattern = RequireValue(value, arguments[index]); index++; break;
				default: throw new ArgumentException($"Unknown argument: {arguments[index]}");
			}
		}
		if (host is null || !System.IO.File.Exists(host))
			throw new ArgumentException("--host must name a built devprojex.dll.");
		if ((root is null) == (synthetic is null))
			throw new ArgumentException("Specify exactly one of --root and --synthetic-files.");
		if (repetitions < 5)
			throw new ArgumentOutOfRangeException(nameof(repetitions), "At least five repetitions are required.");
		if (synthetic is <= 0)
			throw new ArgumentOutOfRangeException(nameof(synthetic));
		if (coldFirstSearch && only is not null)
			throw new ArgumentException("--cold-first-search cannot be combined with --only.");
		if (coldSearchPattern is not null && !coldFirstSearch)
			throw new ArgumentException("--cold-search-pattern requires --cold-first-search.");
		return new BenchmarkOptions(Path.GetFullPath(host!), root, synthetic, file, seed, only, repetitions, timeout,
			coldFirstSearch, coldSearchPattern);
	}

	private static string RequireValue(string? value, string option) =>
		string.IsNullOrEmpty(value) ? throw new ArgumentException($"{option} requires a value.") : value;
}

internal sealed class McpProcessSession : IAsyncDisposable
{
	private readonly Process _process;
	private readonly Task<string> _standardError;
	private readonly CancellationTokenSource _timeout;

	private McpProcessSession(Process process, McpClient client, Task<string> standardError, CancellationTokenSource timeout)
	{
		_process = process;
		Client = client;
		_standardError = standardError;
		_timeout = timeout;
	}

	public McpClient Client { get; }
	public CancellationToken Token => _timeout.Token;
	public TimeSpan ServerCpuTime
	{
		get
		{
			_process.Refresh();
			return _process.TotalProcessorTime;
		}
	}
	public long ServerPeakWorkingSetBytes
	{
		get
		{
			_process.Refresh();
			return _process.PeakWorkingSet64;
		}
	}

	public static async Task<McpProcessSession> StartAsync(string host, string root, TimeSpan timeout)
	{
		var startInfo = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			WorkingDirectory = root
		};
		startInfo.ArgumentList.Add(host);
		startInfo.ArgumentList.Add("mcp");
		startInfo.ArgumentList.Add("--root");
		startInfo.ArgumentList.Add(root);
		startInfo.ArgumentList.Add("--git-mode");
		startInfo.ArgumentList.Add("none");
		var dataRoot = Path.Combine(Path.GetTempPath(), "DevProjex-McpBenchmark", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dataRoot);
		startInfo.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = dataRoot;
		var process = Process.Start(startInfo) ?? throw new InvalidOperationException("MCP process did not start.");
		var cancellation = new CancellationTokenSource(timeout);
		var standardError = process.StandardError.ReadToEndAsync(cancellation.Token);
		var client = await McpClient.CreateAsync(
			new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream),
			clientOptions: null,
			loggerFactory: null,
			cancellation.Token);
		return new McpProcessSession(process, client, standardError, cancellation);
	}

	public async ValueTask DisposeAsync()
	{
		await Client.DisposeAsync();
		_process.StandardInput.Close();
		await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
		var error = await _standardError;
		if (_process.ExitCode != 0 || !string.IsNullOrWhiteSpace(error))
			Console.Error.WriteLine($"MCP exit={_process.ExitCode}: {error}");
		var dataRoot = _process.StartInfo.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"];
		_process.Dispose();
		_timeout.Dispose();
		if (!string.IsNullOrEmpty(dataRoot) && Directory.Exists(dataRoot))
			Directory.Delete(dataRoot, recursive: true);
	}
}

internal sealed class SyntheticCorpus : IDisposable
{
	private SyntheticCorpus(string path) => Path = path;
	public string Path { get; }

	public static SyntheticCorpus Create(int fileCount)
	{
		var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DevProjex-McpBenchmark", Guid.NewGuid().ToString("N"), "corpus");
		Directory.CreateDirectory(root);
		for (var index = 0; index < fileCount; index++)
		{
			var directory = System.IO.Path.Combine(root, $"d{index / 200:D3}");
			Directory.CreateDirectory(directory);
			var extension = index <= 700 || index % 20 == 0 ? ".ts" : ".txt";
			var path = System.IO.Path.Combine(directory, $"f{index:D5}{extension}");
			var content = $"synthetic-marker file {index:D5}\n" + new string('x', 256) + "\n";
			File.WriteAllText(path, content);
		}
		File.WriteAllText(System.IO.Path.Combine(root, "tsconfig.json"),
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}\n");
		var sourceDirectory = System.IO.Path.Combine(root, "d000");
		var imports = new System.Text.StringBuilder();
		for (var index = 1; index <= Math.Min(700, fileCount - 1); index++)
		{
			var target = System.IO.Path.Combine(root, $"d{index / 200:D3}", $"f{index:D5}.ts");
			var relative = System.IO.Path.GetRelativePath(sourceDirectory, target).Replace('\\', '/');
			imports.Append("import './").Append(relative[..^3]).AppendLine(".js';");
		}
		imports.AppendLine("export const syntheticMarker = 'synthetic-marker';");
		File.WriteAllText(System.IO.Path.Combine(sourceDirectory, "f00000.ts"), imports.ToString());
		return new SyntheticCorpus(root);
	}

	public void Dispose()
	{
		var parent = Directory.GetParent(Path)?.FullName;
		if (parent is not null && Directory.Exists(parent))
			Directory.Delete(parent, recursive: true);
	}
}
