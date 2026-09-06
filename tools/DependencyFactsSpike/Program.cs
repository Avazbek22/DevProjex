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
			_ => throw new ArgumentException($"Unknown command '{args[0]}'.")
		};
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
		""");
}
