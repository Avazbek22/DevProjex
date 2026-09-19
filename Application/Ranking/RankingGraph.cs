namespace DevProjex.Application.Ranking;

internal sealed record RankingGraph(
	string[] Paths,
	IReadOnlyDictionary<string, int> NodeByPath,
	int[][] Outgoing,
	double[][] OutgoingWeights,
	double[] OutgoingWeightSums,
	int[] Dependents,
	int EdgeCount,
	int FilesWithEdges)
{
	public int[][] BuildUndirected(CancellationToken cancellationToken)
	{
		var incomingCounts = new int[Paths.Length];
		for (var source = 0; source < Outgoing.Length; source++)
		{
			if ((source & 255) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			foreach (var target in Outgoing[source])
				incomingCounts[target]++;
		}
		var incoming = new int[Paths.Length][];
		for (var node = 0; node < incoming.Length; node++)
			incoming[node] = new int[incomingCounts[node]];
		Array.Clear(incomingCounts);
		for (var source = 0; source < Outgoing.Length; source++)
		foreach (var target in Outgoing[source])
			incoming[target][incomingCounts[target]++] = source;

		var result = new int[Paths.Length][];
		for (var node = 0; node < result.Length; node++)
		{
			if ((node & 255) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			result[node] = MergeNeighbors(Outgoing[node], incoming[node]);
		}
		return result;
	}

	private static int[] MergeNeighbors(IReadOnlyList<int> outgoing, IReadOnlyList<int> incoming)
	{
		if (outgoing.Count == 0)
			return incoming.Count == 0 ? [] : incoming.ToArray();
		if (incoming.Count == 0)
			return outgoing.ToArray();
		var merged = new int[outgoing.Count + incoming.Count];
		var left = 0;
		var right = 0;
		var written = 0;
		while (left < outgoing.Count || right < incoming.Count)
		{
			var next = right >= incoming.Count || left < outgoing.Count && outgoing[left] < incoming[right]
				? outgoing[left++]
				: incoming[right++];
			if (written == 0 || merged[written - 1] != next)
				merged[written++] = next;
		}
		return written == merged.Length ? merged : merged[..written];
	}
}
