using System.Globalization;

namespace DevProjex.Mcp;

/// <summary>
/// Reads <c>expand_related</c> and resolves the neighbourhood it names, so one call can pack a
/// file together with the files it statically depends on or that depend on it.
/// </summary>
/// <remarks>
/// Expansion is computed over the dependency index built from the plan's own included files, so
/// every path it can reach was already admissible. It therefore narrows a selection to the seeds
/// and their resolved neighbours and can never add a file the effective filters, the Git scope,
/// or the project root kept out. Only <see cref="ResolutionStatus.Resolved"/> edges travel: an
/// ambiguous or unresolved reference names a file the engine would not commit to, and following
/// it would put a guess in the pack.
/// </remarks>
internal static class McpRelatedExpansion
{
	public const string ParameterName = "expand_related";
	public const int MaximumSeeds = 16;
	public const int MinimumHops = 1;
	public const int MaximumHops = 2;

	/// <summary>
	/// A neighbourhood is not bounded by its hop count: one widely imported file can reach most of
	/// a repository in two hops. This constant is the only bound on the expanded set, and the
	/// response says so on every call where it decided the answer.
	/// </summary>
	public const int MaximumExpandedFiles = 400;

	public static McpRelatedExpansionRequest? Parse(McpJsonArguments arguments)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		if (!arguments.TryGetElement(ParameterName, out var element))
			return null;
		if (element.ValueKind != JsonValueKind.Object)
			throw Invalid("must be an object with 'seeds' and optional 'hops' and 'direction'");
		ValidatePropertyNames(element);
		return new McpRelatedExpansionRequest(ReadSeeds(element), ReadHops(element), ReadDirection(element));
	}

	/// <summary>
	/// Walks the requested hops outward from the seeds and returns the union in pack order: the
	/// seeds as the caller gave them, then each hop in ordinal path order. The traversal stops at
	/// <see cref="MaximumExpandedFiles"/>, and reports that it did.
	/// </summary>
	public static async Task<McpRelatedExpansionResult> ExpandAsync(
		DependencyFactsEngine engine,
		ProjectContextPlan plan,
		IReadOnlyList<string> relativeSeeds,
		McpRelatedExpansionRequest request,
		IProgress<DependencyIndexProgress>? progress,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(engine);
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(relativeSeeds);
		ArgumentNullException.ThrowIfNull(request);

		var admitted = new List<string>(relativeSeeds.Count);
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var seed in relativeSeeds)
		{
			if (seen.Add(seed))
				admitted.Add(seed);
		}

		var seedCount = admitted.Count;
		var hopCounts = new int[MaximumHops];
		var seedsWithoutFacts = 0;
		var limitReached = false;
		IReadOnlyList<string> frontier = admitted.ToArray();

		for (var hop = 0; hop < request.Hops && frontier.Count > 0 && !limitReached; hop++)
		{
			var related = await engine.FindRelatedAsync(
					plan.SourceRoot,
					plan.IncludedFiles,
					frontier,
					request.Direction,
					hop == 0 ? progress : null,
					cancellationToken)
				.ConfigureAwait(false);
			if (hop == 0)
			{
				seedsWithoutFacts = related.Seeds.Count(static seed => seed.NoFactsReason is not null);
			}

			// Ordinal order makes the admitted prefix of a hop deterministic, which is what keeps
			// the limit from turning into an arbitrary choice between neighbours.
			var candidates = new SortedSet<string>(StringComparer.Ordinal);
			foreach (var seed in related.Seeds)
			{
				cancellationToken.ThrowIfCancellationRequested();
				foreach (var neighbour in seed.Dependencies.Concat(seed.Dependents))
				{
					if (neighbour.Status == ResolutionStatus.Resolved &&
					    !seen.Contains(neighbour.Path) &&
					    related.Index.FileByPath.ContainsKey(neighbour.Path))
					{
						candidates.Add(neighbour.Path);
					}
				}
			}

			var reached = new List<string>(candidates.Count);
			foreach (var path in candidates)
			{
				if (admitted.Count >= MaximumExpandedFiles)
				{
					limitReached = true;
					break;
				}

				seen.Add(path);
				admitted.Add(path);
				reached.Add(path);
			}

			hopCounts[hop] = reached.Count;
			frontier = reached;
		}

		return new McpRelatedExpansionResult(
			admitted,
			seedCount,
			hopCounts[0],
			hopCounts[1],
			seedsWithoutFacts,
			limitReached);
	}

	private static IReadOnlyList<string> ReadSeeds(JsonElement element)
	{
		if (!element.TryGetProperty("seeds", out var seeds) || seeds.ValueKind != JsonValueKind.Array)
			throw Invalid("'seeds' must be a non-empty array of project-relative file paths");
		if (seeds.GetArrayLength() == 0)
			throw Invalid("'seeds' must contain at least one file");
		if (seeds.GetArrayLength() > MaximumSeeds)
			throw Invalid($"'seeds' accepts at most {MaximumSeeds.ToString(CultureInfo.InvariantCulture)} files");

		var values = new List<string>(seeds.GetArrayLength());
		foreach (var seed in seeds.EnumerateArray())
		{
			if (seed.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(seed.GetString()))
				throw Invalid("every entry of 'seeds' must be a non-empty string");
			values.Add(seed.GetString()!);
		}
		return values;
	}

	private static int ReadHops(JsonElement element)
	{
		if (!element.TryGetProperty("hops", out var hops))
			return MinimumHops;
		if (hops.ValueKind != JsonValueKind.Number ||
		    !hops.TryGetInt32(out var value) ||
		    value < MinimumHops ||
		    value > MaximumHops)
		{
			throw Invalid(
				$"'hops' must be {MinimumHops.ToString(CultureInfo.InvariantCulture)} or " +
				$"{MaximumHops.ToString(CultureInfo.InvariantCulture)}");
		}
		return value;
	}

	private static DependencyDirection ReadDirection(JsonElement element)
	{
		if (!element.TryGetProperty("direction", out var direction))
			return DependencyDirection.Both;
		return direction.ValueKind == JsonValueKind.String
			? direction.GetString() switch
			{
				"dependencies" => DependencyDirection.Dependencies,
				"dependents" => DependencyDirection.Dependents,
				"both" => DependencyDirection.Both,
				_ => throw Invalid("'direction' must be dependencies, dependents, or both")
			}
			: throw Invalid("'direction' must be dependencies, dependents, or both");
	}

	private static void ValidatePropertyNames(JsonElement element)
	{
		foreach (var property in element.EnumerateObject())
		{
			if (property.Name is not ("seeds" or "hops" or "direction"))
				throw Invalid($"'{property.Name}' is not a recognized property");
		}
	}

	private static McpToolException Invalid(string reason) =>
		new(McpErrorCodes.InvalidArguments, $"{McpErrorCodes.InvalidArguments}: '{ParameterName}' {reason}.");
}

internal sealed record McpRelatedExpansionRequest(
	IReadOnlyList<string> Seeds,
	int Hops,
	DependencyDirection Direction);

internal sealed record McpRelatedExpansionResult(
	IReadOnlyList<string> RelativePaths,
	int SeedCount,
	int FirstHopCount,
	int SecondHopCount,
	int SeedsWithoutFacts,
	bool LimitReached);
