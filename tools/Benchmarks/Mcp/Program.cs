using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var options = BenchmarkOptions.Parse(args);
var temporaryCorpus = options.SyntheticFileCount is null ? null : SyntheticCorpus.Create(options.SyntheticFileCount.Value);
var root = temporaryCorpus?.Path ?? options.Root ?? throw new ArgumentException("Specify --root or --synthetic-files.");
try
{
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

internal sealed record BenchmarkOptions(
	string Host,
	string? Root,
	int? SyntheticFileCount,
	string? File,
	string? Seed,
	IReadOnlySet<string>? Only,
	int Repetitions,
	TimeSpan Timeout)
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
		return new BenchmarkOptions(Path.GetFullPath(host!), root, synthetic, file, seed, only, repetitions, timeout);
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
