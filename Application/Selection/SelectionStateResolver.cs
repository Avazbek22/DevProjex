namespace DevProjex.Application.Selection;

public sealed class SelectionStateResolver(
	IReadOnlySet<string> previousSelections,
	IReadOnlyDictionary<string, bool>? previousStateCache)
{
	public bool Resolve(string name, bool defaultForNewEntry)
	{
		if (previousStateCache is not null)
		{
			if (previousStateCache.TryGetValue(name, out var cachedState))
				return cachedState;

			// Older profiles only persisted checked entries. Keep those selections while
			// letting missing entries use the current product default for new options.
			if (previousSelections.Contains(name))
				return true;

			return defaultForNewEntry;
		}

		return previousSelections.Contains(name);
	}
}
