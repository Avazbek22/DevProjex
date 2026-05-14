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

			return defaultForNewEntry;
		}

		return previousSelections.Contains(name);
	}
}
