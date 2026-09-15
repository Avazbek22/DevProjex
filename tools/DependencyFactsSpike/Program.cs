using System.Text.Json;

namespace DependencyFactsSpike;

internal static class Program
{
	public static int Main(string[] args)
	{
		try
		{
			return Run(args);
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine(exception.Message);
			return 1;
		}
	}

	private static int Run(string[] args)
	{
		if (args.Length == 0 || args[0] is "-h" or "--help")
		{
			PrintHelp();
			return 0;
		}

		return args[0] switch
		{
			"index" => RunIndex(args[1..]),
			"verify-fixtures" => RunFixtures(args[1..]),
			"capture-cbm" => RunCbm(args[1..]),
			"measure-invalidation" => RunInvalidation(args[1..]),
			_ => throw new ArgumentException($"Unknown command '{args[0]}'.")
		};
	}

	private static int RunInvalidation(string[] args)
	{
		var source = Path.GetFullPath(Required(args, "--fixture"));
		var work = Path.GetFullPath(Required(args, "--work"));
		var grammarCache = Path.GetFullPath(Required(args, "--grammar-cache"));
		if (Directory.Exists(work))
			throw new InvalidOperationException($"Invalidation work directory already exists: {work}");
		CopyDirectory(source, work);
		var baselinePath = Path.Combine(work, "..", "invalidation-baseline.json");
		var sourceChangePath = Path.Combine(work, "..", "invalidation-source.json");
		var configChangePath = Path.Combine(work, "..", "invalidation-config.json");
		var engine = new DependencyFactsEngine(grammarCache);
		_ = engine.Index(new IndexOptions(work, baselinePath, "fixture", false, grammarCache));
		File.AppendAllText(Path.Combine(work, "src", "x.ts"), "\nexport const changed = true;\n");
		var sourceChange = engine.Index(new IndexOptions(work, sourceChangePath, "fixture", false, grammarCache, baselinePath));
		var config = Path.Combine(work, "tsconfig.json");
		File.WriteAllText(config, File.ReadAllText(config).Replace("src/exact.ts", "src/x.ts", StringComparison.Ordinal));
		var configChange = engine.Index(new IndexOptions(work, configChangePath, "fixture", false, grammarCache, sourceChangePath));
		Console.WriteLine($"source-change parsed={sourceChange.Metrics.ParsedFiles} reused={sourceChange.Metrics.ReusedFiles} reresolved={sourceChange.Metrics.ReresolvedFiles}");
		Console.WriteLine($"config-change parsed={configChange.Metrics.ParsedFiles} reused={configChange.Metrics.ReusedFiles} reresolved={configChange.Metrics.ReresolvedFiles}");
		return sourceChange.Metrics.ParsedFiles == 1 && configChange.Metrics.ParsedFiles == 0 ? 0 : 1;
	}

	private static void CopyDirectory(string source, string destination)
	{
		Directory.CreateDirectory(destination);
		foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
			Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
		foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
			File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
	}

	private static int RunCbm(string[] args)
	{
		var executable = Path.GetFullPath(Required(args, "--executable"));
		var cache = Path.GetFullPath(Required(args, "--cache"));
		var project = Required(args, "--project");
		var output = Path.GetFullPath(Required(args, "--output"));
		var result = CbmComparatorCapture.Capture(executable, cache, project);
		Directory.CreateDirectory(Path.GetDirectoryName(output)!);
		File.WriteAllBytes(output, JsonSerializer.SerializeToUtf8Bytes(result, SpikeJsonContext.Default.CbmEdgeSet));
		Console.WriteLine($"cbm-file-edges={result.Edges.Count}");
		return 0;
	}

	private static int RunIndex(string[] args)
	{
		var root = Required(args, "--root");
		var output = Required(args, "--output");
		var sha = Optional(args, "--sha") ?? "unknown";
		var grammarCache = Optional(args, "--grammar-cache") ??
		                   Path.Combine(Path.GetTempPath(), "DevProjex.DependencyFactsSpike", "grammars");
		var result = new DependencyFactsEngine(grammarCache).Index(new IndexOptions(
			Path.GetFullPath(root),
			Path.GetFullPath(output),
			sha,
			args.Contains("--reverse", StringComparer.Ordinal),
			grammarCache,
			Optional(args, "--previous-facts")));
		Console.WriteLine(JsonSerializer.Serialize(result.Metrics, SpikeJsonContext.Default.RepositoryMetrics));
		Console.WriteLine($"result-sha256={result.ResultSha256}");
		return 0;
	}

	private static int RunFixtures(string[] args)
	{
		var root = Path.GetFullPath(Optional(args, "--root") ??
		                            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fixtures"));
		var grammarCache = Optional(args, "--grammar-cache") ??
		                   Path.Combine(Path.GetTempPath(), "DevProjex.DependencyFactsSpike", "grammars");
		var failures = FixtureVerifier.Verify(root, grammarCache);
		foreach (var failure in failures)
			Console.Error.WriteLine(failure);
		Console.WriteLine($"fixtures={(failures.Count == 0 ? "PASS" : "FAIL")}; failures={failures.Count}");
		return failures.Count == 0 ? 0 : 1;
	}

	private static string Required(string[] args, string name) =>
		Optional(args, name) ?? throw new ArgumentException($"Missing required option {name}.");

	private static string? Optional(string[] args, string name)
	{
		var index = Array.IndexOf(args, name);
		if (index < 0)
			return null;
		if (index + 1 == args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
			throw new ArgumentException($"Option {name} requires a value.");
		return args[index + 1];
	}

	private static void PrintHelp() => Console.WriteLine("""
		DependencyFactsSpike
		  index --root PATH --output FILE [--sha SHA] [--reverse] [--previous-facts FILE]
		  verify-fixtures [--root PATH]
		  capture-cbm --executable FILE --cache DIR --project NAME --output FILE
		  measure-invalidation --fixture DIR --work DIR --grammar-cache DIR
		""");
}
