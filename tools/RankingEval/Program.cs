using System.Text.Json;

namespace DevProjex.RankingEval;

internal static class Program
{
	public static async Task<int> Main(string[] args)
	{
		try
		{
			if (args.Length == 0 || args[0] is "-h" or "--help")
			{
				PrintHelp();
				return 0;
			}
			return args[0] switch
			{
				"run" => await RunAsync(args[1..]).ConfigureAwait(false),
				"measure-one" => await MeasureOneAsync(args[1..]).ConfigureAwait(false),
				"hang" => await HangAsync().ConfigureAwait(false),
				_ => throw new ArgumentException($"Unknown command '{args[0]}'.")
			};
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine(exception);
			return 1;
		}
	}

	private static async Task<int> RunAsync(string[] args)
	{
		var registryPath = Path.GetFullPath(Required(args, "--registry"));
		var productRoot = Path.GetFullPath(Required(args, "--product-root"));
		var workspace = Path.GetFullPath(Required(args, "--workspace"));
		var output = Path.GetFullPath(Required(args, "--output"));
		var registry = EvaluationRegistry.Load(registryPath);
		var runner = new EvaluationRunner(registry, registryPath, productRoot, workspace);
		var result = await runner.RunAsync(CancellationToken.None).ConfigureAwait(false);
		Directory.CreateDirectory(Path.GetDirectoryName(output)!);
		await File.WriteAllTextAsync(
			output,
			JsonSerializer.Serialize(result, EvaluationRegistry.JsonOptions) + Environment.NewLine).ConfigureAwait(false);
		Console.WriteLine($"criterion={(result.Criterion.Passed ? "PASS" : "FAIL")}; eligible={result.Criterion.EligibleCells}; delta={result.Criterion.OverallRecallNewDelta:F4}");
		return 0;
	}

	private static async Task<int> MeasureOneAsync(string[] args)
	{
		var registry = EvaluationRegistry.Load(Path.GetFullPath(Required(args, "--registry")));
		var result = await EvaluationRunner.MeasureOneAsync(
			registry,
			Required(args, "--repository"),
			Path.GetFullPath(Required(args, "--root")),
			Path.GetFullPath(Required(args, "--data")),
			Required(args, "--mode"),
			Required(args, "--order"),
			CancellationToken.None).ConfigureAwait(false);
		Console.Write(JsonSerializer.Serialize(result, EvaluationRegistry.JsonOptions));
		return 0;
	}

	private static string Required(string[] args, string name)
	{
		var index = Array.IndexOf(args, name);
		if (index < 0 || index + 1 >= args.Length)
			throw new ArgumentException($"Missing required option {name}.");
		return args[index + 1];
	}

	private static async Task<int> HangAsync()
	{
		await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
		return 0;
	}

	private static void PrintHelp() => Console.WriteLine("""
		RankingEval
		  run --registry FILE --product-root PATH --workspace PATH --output FILE
		""");
}
