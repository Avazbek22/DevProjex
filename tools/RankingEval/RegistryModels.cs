using System.Text.Json;

namespace DevProjex.RankingEval;

public sealed record EvaluationRegistry
{
	public required string Protocol { get; init; }
	public required RegistrySelection Selection { get; init; }
	public required IReadOnlyList<RegistryOrder> Orders { get; init; }
	public required RegistryPerformance Performance { get; init; }
	public required IReadOnlyList<RegistryRepository> Repositories { get; init; }

	public static EvaluationRegistry Load(string path)
	{
		using var stream = File.OpenRead(path);
		return JsonSerializer.Deserialize<EvaluationRegistry>(stream, JsonOptions) ??
		       throw new InvalidDataException($"Registry is empty: {path}");
	}

	internal static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true
	};
}

public sealed record RegistrySelection
{
	public required IReadOnlyList<long> Budgets { get; init; }
}

public sealed record RegistryOrder
{
	public required string Id { get; init; }
	public required string Comparator { get; init; }
}

public sealed record RegistryPerformance
{
	public required int Repetitions { get; init; }
}

public sealed record RegistryRepository
{
	public required string Id { get; init; }
	public required string Url { get; init; }
	public required string Commit { get; init; }
	public required IReadOnlyList<RegistryTask> Tasks { get; init; }
}

public sealed record RegistryTask
{
	public required string Id { get; init; }
	public required string Prompt { get; init; }
	public required string Seed { get; init; }
	public required IReadOnlyList<string> Required { get; init; }
	public required IReadOnlyList<IReadOnlyList<string>> Alternatives { get; init; }

	public IReadOnlyList<IReadOnlyList<string>> SufficientSets => [Required, .. Alternatives];
}
