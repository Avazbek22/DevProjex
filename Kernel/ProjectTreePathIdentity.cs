namespace DevProjex.Kernel;

/// <summary>
/// Defines the identity and deterministic ordering of entries already discovered in a project tree.
/// Filesystems can expose names that differ only by case even when the host platform normally uses
/// case-insensitive path lookup, so tree entries must never use the platform path comparer as identity.
/// </summary>
public static class ProjectTreePathIdentity
{
	public static StringComparer CanonicalComparer => StringComparer.Ordinal;

	public static StringComparison CanonicalComparison => StringComparison.Ordinal;

	/// <summary>
	/// Resolves a persisted or command-line entry name against names discovered from the
	/// filesystem. Exact identity always wins. Windows keeps compatibility with historical
	/// case-insensitive input only when that input identifies one unambiguous sibling.
	/// </summary>
	public static bool TryResolveAvailableName(
		IReadOnlyList<string> availableNames,
		string requestedName,
		out string resolvedName) =>
		TryResolveAvailableEntry(
			availableNames,
			requestedName,
			static name => name,
			candidateFilter: null,
			out resolvedName);

	public static bool TryResolveAvailableEntry<T>(
		IReadOnlyList<T> availableEntries,
		string requestedName,
		Func<T, string> nameSelector,
		out T resolvedEntry) =>
		TryResolveAvailableEntry(
			availableEntries,
			requestedName,
			nameSelector,
			candidateFilter: null,
			out resolvedEntry);

	public static bool TryResolveAvailableEntry<T>(
		IReadOnlyList<T> availableEntries,
		string requestedName,
		Func<T, string> nameSelector,
		Func<T, bool>? candidateFilter,
		out T resolvedEntry)
	{
		ArgumentNullException.ThrowIfNull(availableEntries);
		ArgumentNullException.ThrowIfNull(requestedName);
		ArgumentNullException.ThrowIfNull(nameSelector);

		for (var index = 0; index < availableEntries.Count; index++)
		{
			var candidate = availableEntries[index];
			if (candidateFilter is not null && !candidateFilter(candidate))
				continue;
			if (!CanonicalComparer.Equals(nameSelector(candidate), requestedName))
				continue;

			resolvedEntry = candidate;
			return true;
		}

		if (!OperatingSystem.IsWindows())
		{
			resolvedEntry = default!;
			return false;
		}

		var hasCompatibleMatch = false;
		var compatibleName = string.Empty;
		var compatibleEntry = default(T)!;
		for (var index = 0; index < availableEntries.Count; index++)
		{
			var candidate = availableEntries[index];
			if (candidateFilter is not null && !candidateFilter(candidate))
				continue;
			var candidateName = nameSelector(candidate);
			if (!StringComparer.OrdinalIgnoreCase.Equals(candidateName, requestedName))
				continue;

			if (hasCompatibleMatch && !CanonicalComparer.Equals(candidateName, compatibleName))
			{
				resolvedEntry = default!;
				return false;
			}

			hasCompatibleMatch = true;
			compatibleName = candidateName;
			compatibleEntry = candidate;
		}

		resolvedEntry = compatibleEntry;
		return hasCompatibleMatch;
	}

	public static IReadOnlyDictionary<string, bool> ResolveAvailableNameStates(
		IReadOnlyList<string> availableNames,
		IReadOnlyDictionary<string, bool> requestedStates,
		bool retainUnmatched)
	{
		ArgumentNullException.ThrowIfNull(availableNames);
		ArgumentNullException.ThrowIfNull(requestedStates);

		var availableSet = availableNames.ToHashSet(CanonicalComparer);
		var resolved = new Dictionary<string, bool>(CanonicalComparer);
		foreach (var (requestedName, state) in requestedStates)
		{
			if (availableSet.Contains(requestedName))
				resolved[requestedName] = state;
		}

		foreach (var (requestedName, state) in requestedStates)
		{
			if (availableSet.Contains(requestedName))
				continue;
			if (TryResolveAvailableName(availableNames, requestedName, out var availableName))
			{
				resolved.TryAdd(availableName, state);
				continue;
			}

			var ambiguous = false;
			if (OperatingSystem.IsWindows())
			{
				var compatibleNames = availableNames
					.Where(name => StringComparer.OrdinalIgnoreCase.Equals(name, requestedName))
					.ToArray();
				ambiguous = compatibleNames.Length > 1;
				if (ambiguous)
				{
					foreach (var compatibleName in compatibleNames)
						resolved.TryAdd(compatibleName, false);
				}
			}

			if (!ambiguous && retainUnmatched)
				resolved[requestedName] = state;
		}

		return resolved;
	}
}
