using DevProjex.Application.Ranking;
using DevProjex.Kernel;

namespace DevProjex.RankingEval;

internal sealed record EvaluationRankingGraph(
	string[] Paths,
	IReadOnlyDictionary<string, int> NodeByPath,
	int[][] Undirected)
{
	internal static EvaluationRankingGraph Create(
		RankingGraph graph,
		CancellationToken cancellationToken) =>
		new(graph.Paths, graph.NodeByPath, graph.BuildUndirected(cancellationToken));
}

internal sealed record EvaluationOrder(bool Available, IReadOnlyList<string> Paths);

internal static class EvaluationFocusComparators
{
	private const int PersonalizedPageRankIterations = 100;
	private const double PersonalizedPageRankDamping = 0.85;
	private const double PersonalizedPageRankConvergence = 1e-12;

	internal static EvaluationOrder SeedFirst(
		ImportanceRankingReport importance,
		IReadOnlyList<FocusRankingSeedRequest> seeds)
	{
		var orderedSeeds = ResolveSeeds(importance, seeds);
		var seedPaths = orderedSeeds.Select(static entry => entry.Path).ToHashSet(StringComparer.Ordinal);
		return new EvaluationOrder(
			true,
			orderedSeeds.Select(static entry => entry.Path)
				.Concat(importance.Entries.Where(entry => !seedPaths.Contains(entry.Path)).Select(static entry => entry.Path))
				.ToArray());
	}

	internal static EvaluationOrder PersonalizedPageRank(
		ImportanceRankingReport importance,
		IReadOnlyList<FocusRankingSeedRequest> seeds,
		EvaluationRankingGraph graph,
		CancellationToken cancellationToken)
	{
		var orderedSeeds = ResolveSeeds(importance, seeds);
		var seedNodes = orderedSeeds
			.Select(static entry => entry.Path)
			.Select(path => graph.NodeByPath.TryGetValue(path, out var node) ? (int?)node : null)
			.Where(static node => node is not null)
			.Select(static node => node!.Value)
			.Distinct()
			.Order()
			.ToArray();
		if (seedNodes.Length == 0)
			return new EvaluationOrder(false, []);

		var scores = CalculatePersonalizedPageRank(graph, seedNodes, cancellationToken);
		var seedOrder = orderedSeeds
			.Select((entry, index) => (entry.Path, index))
			.ToDictionary(static item => item.Path, static item => item.index, StringComparer.Ordinal);
		var paths = importance.Entries
			.OrderBy(entry => seedOrder.ContainsKey(entry.Path) ? 0 : 1)
			.ThenBy(entry => seedOrder.TryGetValue(entry.Path, out var index) ? index : 0)
			.ThenByDescending(entry => graph.NodeByPath.TryGetValue(entry.Path, out var node) ? scores[node] : 0)
			.ThenBy(static entry => entry.Priority)
			.ThenBy(static entry => entry.Path, StringComparer.Ordinal)
			.Select(static entry => entry.Path)
			.ToArray();
		return new EvaluationOrder(true, paths);
	}

	private static IReadOnlyList<ImportanceRankingEntry> ResolveSeeds(
		ImportanceRankingReport importance,
		IReadOnlyList<FocusRankingSeedRequest> seeds)
	{
		var entriesByFullPath = importance.Entries.ToDictionary(
			static entry => Path.GetFullPath(entry.FullPath),
			PathComparer.Default);
		var seen = new HashSet<string>(PathComparer.Default);
		var result = new List<ImportanceRankingEntry>(seeds.Count);
		foreach (var seed in seeds)
		{
			var fullPath = Path.GetFullPath(seed.FullPath);
			if (!seen.Add(fullPath))
				continue;
			if (!entriesByFullPath.TryGetValue(fullPath, out var entry))
				throw new ArgumentException($"Focus seed is outside the effective selection: {seed.Requested}", nameof(seeds));
			result.Add(entry);
		}
		return result;
	}

	private static double[] CalculatePersonalizedPageRank(
		EvaluationRankingGraph graph,
		IReadOnlyList<int> seedNodes,
		CancellationToken cancellationToken)
	{
		var teleport = new double[graph.Paths.Length];
		foreach (var seed in seedNodes)
			teleport[seed] = 1d / seedNodes.Count;
		var rank = (double[])teleport.Clone();
		var next = new double[rank.Length];
		for (var iteration = 0; iteration < PersonalizedPageRankIterations; iteration++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var dangling = 0d;
			for (var node = 0; node < rank.Length; node++)
			{
				if (graph.Undirected[node].Length == 0)
					dangling += rank[node];
			}
			for (var node = 0; node < rank.Length; node++)
				next[node] = (1 - PersonalizedPageRankDamping) * teleport[node] +
				             PersonalizedPageRankDamping * dangling * teleport[node];
			for (var source = 0; source < rank.Length; source++)
			{
				if ((source & 255) == 0)
					cancellationToken.ThrowIfCancellationRequested();
				if (graph.Undirected[source].Length == 0)
					continue;
				var share = PersonalizedPageRankDamping * rank[source] / graph.Undirected[source].Length;
				foreach (var target in graph.Undirected[source])
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
			rank[node] = ImportanceRankingService.QuantizePageRank(rank[node]);
		return rank;
	}
}
