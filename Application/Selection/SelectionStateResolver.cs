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

			// A complete settings-island map is open-world: known rows are authoritative,
			// while rows first discovered after the save use the current product default.
			// The selected set remains a compatibility bridge for partially migrated data.
			if (previousSelections.Contains(name))
				return true;

			return defaultForNewEntry;
		}

		// No state map means the legacy selected-only contract. Missing names are
		// unchecked; callers decide separately whether a legacy fallback is warranted.
		return previousSelections.Contains(name);
	}
}
