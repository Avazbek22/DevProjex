using DevProjex.Application.Dependencies;

namespace DevProjex.Application.Ranking;

internal static class FocusRankingEngine
{
	internal const string AlgorithmId = "focus-v1";
	internal const string PersonalizedPageRankAlgorithmId = "focus-ppr";
	internal const string SeedFirstAlgorithmId = "seed-first";
	private const int MaximumReportedHop = 7;
	private const int MaximumTopEntries = 10;
	private const int PersonalizedPageRankIterations = 100;
	private const double PersonalizedPageRankDamping = 0.85;
	private const double PersonalizedPageRankConvergence = 1e-12;

	public static ImportanceRankingReport Apply(
		ImportanceRankingReport importance,
		FocusRankingRequest request,
		RankingGraph graph,
		DependencyIndexSnapshot snapshot,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(importance);
		ArgumentNullException.ThrowIfNull(request);
		if (request.Seeds.Count == 0)
			throw new ArgumentException("At least one focus seed is required.", nameof(request));

		var entriesByFullPath = importance.Entries.ToDictionary(
			static entry => Path.GetFullPath(entry.FullPath),
			PathComparer.Default);
		var relativeByFullPath = entriesByFullPath.ToDictionary(
			static pair => pair.Key,
			static pair => pair.Value.Path,
			PathComparer.Default);
		var seeds = DeduplicateSeeds(request.Seeds, entriesByFullPath, cancellationToken);
		var seedOrder = seeds
			.Select((seed, index) => (Path.GetFullPath(seed.FullPath), index))
			.ToDictionary(static item => item.Item1, static item => item.index, PathComparer.Default);
		if (request.Strategy == FocusRankingStrategy.SeedFirst)
			return ApplySeedFirst(importance, seeds, seedOrder, snapshot, cancellationToken);

		var undirected = graph.BuildUndirected(cancellationToken);
		var distances = CalculateDistances(
			graph,
			undirected,
			seeds,
			relativeByFullPath,
			cancellationToken);
		var viaByPath = SelectParents(graph, undirected, distances, cancellationToken);
		var entries = request.Strategy == FocusRankingStrategy.PersonalizedPageRank
			? OrderByPersonalizedPageRank(
				importance,
				seeds,
				seedOrder,
				graph,
				undirected,
				distances,
				viaByPath,
				relativeByFullPath,
				cancellationToken)
			: OrderByHop(
				importance,
				seedOrder,
				graph,
				distances,
				viaByPath,
				cancellationToken);
		return BuildReport(
			importance,
			request.Strategy == FocusRankingStrategy.PersonalizedPageRank
				? PersonalizedPageRankAlgorithmId
				: AlgorithmId,
			entries,
			BuildSeedReports(seeds, entriesByFullPath, graph, undirected, snapshot),
			cancellationToken);
	}

	internal static IReadOnlyList<FocusRankingSeedRequest> DeduplicateSeeds(
		IReadOnlyList<FocusRankingSeedRequest> seeds,
		IReadOnlyDictionary<string, ImportanceRankingEntry> entriesByFullPath,
		CancellationToken cancellationToken)
	{
		var seen = new HashSet<string>(PathComparer.Default);
		var result = new List<FocusRankingSeedRequest>(seeds.Count);
		foreach (var seed in seeds)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var fullPath = Path.GetFullPath(seed.FullPath);
			if (!entriesByFullPath.ContainsKey(fullPath))
				throw new ArgumentException($"Focus seed is outside the effective selection: {seed.Requested}", nameof(seeds));
			if (seen.Add(fullPath))
				result.Add(seed with { FullPath = fullPath });
		}
		return result;
	}

	internal static int[] CalculateDistances(
		RankingGraph graph,
		int[][] undirected,
		IReadOnlyList<FocusRankingSeedRequest> seeds,
		IReadOnlyDictionary<string, string> relativeByFullPath,
		CancellationToken cancellationToken)
	{
		var distances = new int[graph.Paths.Length];
		Array.Fill(distances, -1);
		var queue = new Queue<int>();
		foreach (var seed in seeds)
		{
			if (!relativeByFullPath.TryGetValue(Path.GetFullPath(seed.FullPath), out var relative) ||
			    !graph.NodeByPath.TryGetValue(relative, out var node) || distances[node] == 0)
				continue;
			distances[node] = 0;
			queue.Enqueue(node);
		}
		while (queue.TryDequeue(out var source))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var nextDistance = distances[source] + 1;
			foreach (var target in undirected[source])
			{
				if (distances[target] >= 0)
					continue;
				distances[target] = nextDistance;
				queue.Enqueue(target);
			}
		}
		return distances;
	}

	private static IReadOnlyDictionary<string, FocusRankingVia> SelectParents(
		RankingGraph graph,
		int[][] undirected,
		IReadOnlyList<int> distances,
		CancellationToken cancellationToken)
	{
		var parents = new Dictionary<string, FocusRankingVia>(StringComparer.Ordinal);
		for (var node = 0; node < graph.Paths.Length; node++)
		{
			if ((node & 255) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			var hop = distances[node];
			if (hop <= 0)
				continue;
			var parent = undirected[node]
				.Where(neighbor => distances[neighbor] == hop - 1)
				.OrderBy(neighbor => graph.Paths[neighbor], StringComparer.Ordinal)
				.First();
			var nodeDependsOnParent = Array.BinarySearch(graph.Outgoing[node], parent) >= 0;
			var parentDependsOnNode = Array.BinarySearch(graph.Outgoing[parent], node) >= 0;
			var relation = nodeDependsOnParent && parentDependsOnNode
				? FocusRankingRelation.LinkedWith
				: nodeDependsOnParent
					? FocusRankingRelation.DependentOf
					: FocusRankingRelation.DependencyOf;
			parents[graph.Paths[node]] = new FocusRankingVia(graph.Paths[parent], relation);
		}
		return parents;
	}

	private static ImportanceRankingEntry[] OrderByHop(
		ImportanceRankingReport importance,
		IReadOnlyDictionary<string, int> seedOrder,
		RankingGraph graph,
		IReadOnlyList<int> distances,
		IReadOnlyDictionary<string, FocusRankingVia> viaByPath,
		CancellationToken cancellationToken) =>
		importance.Entries
			.Select(entry => Decorate(entry, seedOrder, graph, distances, viaByPath))
			.OrderBy(entry => entry.IsFocusSeed ? 0 : entry.Hop is not null ? 1 : 2)
			.ThenBy(entry => entry.IsFocusSeed ? seedOrder[Path.GetFullPath(entry.FullPath)] : entry.Hop ?? int.MaxValue)
			.ThenBy(entry => entry.IsFocusSeed ? 0 : entry.BaseImportancePriority)
			.ThenBy(static entry => entry.Path, StringComparer.Ordinal)
			.Select((entry, index) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				return entry with { Priority = index + 1 };
			})
			.ToArray();

	private static ImportanceRankingEntry[] OrderByPersonalizedPageRank(
		ImportanceRankingReport importance,
		IReadOnlyList<FocusRankingSeedRequest> seeds,
		IReadOnlyDictionary<string, int> seedOrder,
		RankingGraph graph,
		int[][] undirected,
		IReadOnlyList<int> distances,
		IReadOnlyDictionary<string, FocusRankingVia> viaByPath,
		IReadOnlyDictionary<string, string> relativeByFullPath,
		CancellationToken cancellationToken)
	{
		var scores = CalculatePersonalizedPageRank(
			graph,
			undirected,
			seeds,
			relativeByFullPath,
			cancellationToken);
		return importance.Entries
			.Select(entry => Decorate(entry, seedOrder, graph, distances, viaByPath))
			.OrderBy(entry => entry.IsFocusSeed ? 0 : 1)
			.ThenBy(entry => entry.IsFocusSeed ? seedOrder[Path.GetFullPath(entry.FullPath)] : 0)
			.ThenByDescending(entry => graph.NodeByPath.TryGetValue(entry.Path, out var node) ? scores[node] : 0)
			.ThenBy(entry => entry.IsFocusSeed ? 0 : entry.BaseImportancePriority)
			.ThenBy(static entry => entry.Path, StringComparer.Ordinal)
			.Select((entry, index) => entry with { Priority = index + 1 })
			.ToArray();
	}

	private static double[] CalculatePersonalizedPageRank(
		RankingGraph graph,
		int[][] undirected,
		IReadOnlyList<FocusRankingSeedRequest> seeds,
		IReadOnlyDictionary<string, string> relativeByFullPath,
		CancellationToken cancellationToken)
	{
		var seedNodes = seeds
			.Select(seed => relativeByFullPath.GetValueOrDefault(Path.GetFullPath(seed.FullPath)))
			.Where(static path => path is not null)
			.Select(path => graph.NodeByPath[path!])
			.Distinct()
			.Order()
			.ToArray();
		var result = new double[graph.Paths.Length];
		if (seedNodes.Length == 0)
			return result;
		var teleport = new double[graph.Paths.Length];
		foreach (var seed in seedNodes)
			teleport[seed] = 1d / seedNodes.Length;
		var rank = (double[])teleport.Clone();
		var next = new double[rank.Length];
		for (var iteration = 0; iteration < PersonalizedPageRankIterations; iteration++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var dangling = 0d;
			for (var node = 0; node < rank.Length; node++)
			{
				if (undirected[node].Length == 0)
					dangling += rank[node];
			}
			for (var node = 0; node < rank.Length; node++)
				next[node] = (1 - PersonalizedPageRankDamping) * teleport[node] +
				             PersonalizedPageRankDamping * dangling * teleport[node];
			for (var source = 0; source < rank.Length; source++)
			{
				if ((source & 255) == 0)
					cancellationToken.ThrowIfCancellationRequested();
				if (undirected[source].Length == 0)
					continue;
				var share = PersonalizedPageRankDamping * rank[source] / undirected[source].Length;
				foreach (var target in undirected[source])
					next[target] += share;
			}
			var delta = 0d;
			for (var node = 0; node < rank.Length; node++)
				delta += Math.Abs(next[node] - rank[node]);
			(rank, next) = (next, rank);
			if (delta < PersonalizedPageRankConvergence)
				break;
		}
		for (var node = 0; node < rank.Length; node++)
			result[node] = ImportanceRankingService.QuantizePageRank(rank[node]);
		return result;
	}

	private static ImportanceRankingEntry Decorate(
		ImportanceRankingEntry entry,
		IReadOnlyDictionary<string, int> seedOrder,
		RankingGraph graph,
		IReadOnlyList<int> distances,
		IReadOnlyDictionary<string, FocusRankingVia> viaByPath)
	{
		var isSeed = seedOrder.ContainsKey(Path.GetFullPath(entry.FullPath));
		int? hop = isSeed
			? 0
			: graph.NodeByPath.TryGetValue(entry.Path, out var node) && distances[node] >= 0
				? distances[node]
				: null;
		return entry with
		{
			BaseImportancePriority = entry.Priority,
			Hop = hop,
			Via = hop > 0 && viaByPath.TryGetValue(entry.Path, out var via) ? via : null,
			IsFocusSeed = isSeed
		};
	}

	private static ImportanceRankingReport ApplySeedFirst(
		ImportanceRankingReport importance,
		IReadOnlyList<FocusRankingSeedRequest> seeds,
		IReadOnlyDictionary<string, int> seedOrder,
		DependencyIndexSnapshot snapshot,
		CancellationToken cancellationToken)
	{
		var entries = importance.Entries
			.Select(entry => entry with
			{
				BaseImportancePriority = entry.Priority,
				Hop = seedOrder.ContainsKey(Path.GetFullPath(entry.FullPath)) ? 0 : null,
				IsFocusSeed = seedOrder.ContainsKey(Path.GetFullPath(entry.FullPath))
			})
			.OrderBy(entry => entry.IsFocusSeed ? 0 : 1)
			.ThenBy(entry => entry.IsFocusSeed ? seedOrder[Path.GetFullPath(entry.FullPath)] : entry.BaseImportancePriority)
			.ThenBy(static entry => entry.Path, StringComparer.Ordinal)
			.Select((entry, index) => entry with { Priority = index + 1 })
			.ToArray();
		var seedReports = seeds.Select(seed =>
		{
			var path = importance.Entries.Single(entry => PathComparer.Default.Equals(entry.FullPath, seed.FullPath)).Path;
			var facts = snapshot.Files.FirstOrDefault(file => file.Path.Equals(path, StringComparison.Ordinal));
			return new FocusRankingSeed(seed.Requested, path, StateWithoutGraph(facts), facts?.StatusReason);
		}).ToArray();
		return BuildReport(importance, SeedFirstAlgorithmId, entries, seedReports, cancellationToken);
	}

	private static ImportanceRankingReport BuildReport(
		ImportanceRankingReport importance,
		string algorithm,
		IReadOnlyList<ImportanceRankingEntry> entries,
		IReadOnlyList<FocusRankingSeed> seeds,
		CancellationToken cancellationToken)
	{
		var hops = new SortedDictionary<int, int>();
		var beyond = 0;
		var maxHop = 0;
		var unreachable = 0;
		foreach (var entry in entries)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (entry.Hop is not { } hop)
			{
				unreachable++;
				continue;
			}
			maxHop = Math.Max(maxHop, hop);
			if (hop > MaximumReportedHop)
				beyond++;
			else
				hops[hop] = hops.GetValueOrDefault(hop) + 1;
		}
		return importance with
		{
			Algorithm = algorithm,
			Entries = entries,
			TopEntries = entries.Take(MaximumTopEntries).ToArray(),
			Focus = new FocusRankingSummary(
				algorithm,
				ImportanceRankingService.AlgorithmId,
				seeds,
				hops,
				beyond,
				maxHop,
				unreachable)
		};
	}

	private static IReadOnlyList<FocusRankingSeed> BuildSeedReports(
		IReadOnlyList<FocusRankingSeedRequest> seeds,
		IReadOnlyDictionary<string, ImportanceRankingEntry> entriesByFullPath,
		RankingGraph graph,
		int[][] undirected,
		DependencyIndexSnapshot snapshot)
	{
		var factsByPath = snapshot.Files.ToDictionary(static file => file.Path, StringComparer.Ordinal);
		return seeds.Select(seed =>
		{
			var path = entriesByFullPath[Path.GetFullPath(seed.FullPath)].Path;
			factsByPath.TryGetValue(path, out var facts);
			var state = facts?.Status switch
			{
				DependencyFileStatus.ExtractionFailed => FocusSeedState.ExtractionFailed,
				DependencyFileStatus.Unsupported or null => FocusSeedState.Unsupported,
				_ when graph.NodeByPath.TryGetValue(path, out var node) && undirected[node].Length > 0 =>
					FocusSeedState.Resolved,
				_ => FocusSeedState.NoResolvedNeighbors
			};
			var reason = facts?.StatusReason;
			if (state == FocusSeedState.NoResolvedNeighbors &&
			    snapshot.EdgesBySource.TryGetValue(path, out var edges))
			{
				reason = edges.SelectMany(static edge => edge.Reasons).FirstOrDefault();
			}
			return new FocusRankingSeed(seed.Requested, path, state, reason);
		}).ToArray();
	}

	private static FocusSeedState StateWithoutGraph(FileFacts? facts) => facts?.Status switch
	{
		DependencyFileStatus.ExtractionFailed => FocusSeedState.ExtractionFailed,
		DependencyFileStatus.Unsupported or null => FocusSeedState.Unsupported,
		_ => FocusSeedState.NoResolvedNeighbors
	};

}
