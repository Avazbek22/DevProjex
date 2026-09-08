using DevProjex.Application.Dependencies;

namespace DevProjex.Application.Ranking;

internal static class FocusRankingEngine
{
	internal const string AlgorithmId = "focus-v1";
	private const int MaximumReportedHop = 7;
	private const int MaximumTopEntries = 10;

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
		var undirected = graph.BuildUndirected(cancellationToken);
		var distances = CalculateDistances(
			graph,
			undirected,
			seeds,
			relativeByFullPath,
			cancellationToken);
		var viaByPath = SelectParents(graph, undirected, distances, cancellationToken);
		var entries = OrderByHop(
			importance,
			seedOrder,
			graph,
			distances,
			viaByPath,
			cancellationToken);
		return BuildReport(
			importance,
			AlgorithmId,
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
			var parent = undirected[node].First(neighbor => distances[neighbor] == hop - 1);
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
		var factsByPath = snapshot.FileByPath.Count == snapshot.Files.Count
			? snapshot.FileByPath
			: snapshot.Files.ToDictionary(static file => file.Path, StringComparer.Ordinal);
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

}
