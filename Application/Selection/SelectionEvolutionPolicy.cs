namespace DevProjex.Application.Selection;

public static class SelectionEvolutionPolicy
{
	public static SelectionEvolutionResult<T> Reconcile<T>(
		IEnumerable<T> availableItems,
		IReadOnlySet<T> previousSelection,
		IReadOnlyDictionary<T, bool> knownStates,
		Func<T, bool> defaultForNewItem,
		IEqualityComparer<T>? comparer = null)
		where T : notnull
	{
		ArgumentNullException.ThrowIfNull(availableItems);
		ArgumentNullException.ThrowIfNull(previousSelection);
		ArgumentNullException.ThrowIfNull(knownStates);
		ArgumentNullException.ThrowIfNull(defaultForNewItem);

		comparer ??= EqualityComparer<T>.Default;
		var updatedStates = new Dictionary<T, bool>(comparer);
		foreach (var (item, isSelected) in knownStates)
			updatedStates[item] = isSelected;
		foreach (var item in previousSelection)
			updatedStates.TryAdd(item, true);

		var selected = new HashSet<T>(comparer);
		var seen = new HashSet<T>(comparer);
		foreach (var item in availableItems)
		{
			if (!seen.Add(item))
				continue;

			if (!updatedStates.TryGetValue(item, out var isSelected))
			{
				isSelected = defaultForNewItem(item);
				updatedStates[item] = isSelected;
			}

			if (isSelected)
				selected.Add(item);
		}

		return new SelectionEvolutionResult<T>(selected, updatedStates);
	}
}

public sealed record SelectionEvolutionResult<T>(
	IReadOnlySet<T> SelectedItems,
	IReadOnlyDictionary<T, bool> KnownStates)
	where T : notnull;
