namespace DevProjex.Mcp;

internal sealed class McpSearchObservedFile(string path)
{
	public string Path { get; } = path;
	public int Count { get; private set; }
	public void Complete(int count) => Count = count;
}

internal sealed class McpWithheldMatchCounts(int maximumWrittenFiles, int maximumListedFiles)
{
	// At most maximumWrittenFiles counts can decrease after the response is selected. Keeping
	// that many extra leaders proves the final top list without retaining every scanned path.
	private readonly SortedSet<McpSearchObservedFile> leaders = new(Comparer<McpSearchObservedFile>.Create(
		(left, right) => left.Count != right.Count ? right.Count.CompareTo(left.Count) : StringComparer.Ordinal.Compare(left.Path, right.Path)));

	public void Observe(McpSearchObservedFile file)
	{
		if (file.Count == 0) return;
		leaders.Add(file);
		if (leaders.Count > maximumWrittenFiles + maximumListedFiles) leaders.Remove(leaders.Max!);
	}

	public (IReadOnlyDictionary<string, int> Counts, int Files, int Matches) Snapshot(
		IReadOnlyList<McpSearchCandidate> candidates, IReadOnlyList<McpSearchHit> written, int totalFiles, int totalMatches)
	{
		var known = leaders.Concat(candidates.Select(static candidate => candidate.ObservedFile).OfType<McpSearchObservedFile>())
			.DistinctBy(static file => file.Path, StringComparer.Ordinal).ToDictionary(static file => file.Path, static file => file.Count, StringComparer.Ordinal);
		var completeFiles = 0;
		foreach (var group in written.GroupBy(static hit => hit.RelativePath, StringComparer.Ordinal))
		{
			known[group.Key] -= group.Count();
			if (known[group.Key] == 0) completeFiles++;
		}
		return (known.Where(static pair => pair.Value > 0).ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
			totalFiles - completeFiles, totalMatches - written.Count);
	}
}
