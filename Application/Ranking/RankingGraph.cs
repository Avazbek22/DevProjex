namespace DevProjex.Application.Ranking;

internal sealed record RankingGraph(
	string[] Paths,
	IReadOnlyDictionary<string, int> NodeByPath,
	int[][] Outgoing,
	double[][] OutgoingWeights,
	int[] Dependents,
	int EdgeCount,
	int FilesWithEdges)
{
	public int[][] BuildUndirected(CancellationToken cancellationToken)
	{
		var neighbors = new HashSet<int>[Paths.Length];
		for (var node = 0; node < neighbors.Length; node++)
			neighbors[node] = [];
		for (var source = 0; source < Outgoing.Length; source++)
		{
			if ((source & 255) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			foreach (var target in Outgoing[source])
			{
				neighbors[source].Add(target);
				neighbors[target].Add(source);
			}
		}
		var result = new int[neighbors.Length][];
		for (var node = 0; node < neighbors.Length; node++)
		{
			if ((node & 255) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			result[node] = neighbors[node].Order().ToArray();
		}
		return result;
	}
}
