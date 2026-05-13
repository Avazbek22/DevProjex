using DevProjex.Application.Models;

namespace DevProjex.Application.Services;

public sealed class FilterOptionSelectionService
{
	public IReadOnlyList<SelectionOption> BuildExtensionOptions(
		IEnumerable<string> extensions,
		IReadOnlySet<string> previousSelections)
	{
		return BuildExtensionOptions(extensions, previousSelections, previousStateCache: null);
	}

	public IReadOnlyList<SelectionOption> BuildExtensionOptions(
		IEnumerable<string> extensions,
		IReadOnlySet<string> previousSelections,
		IReadOnlyDictionary<string, bool>? previousStateCache)
	{
		var list = new List<SelectionOption>();
		var ordered = extensions.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
		var hasStateCache = previousStateCache is not null;
		foreach (var ext in ordered)
		{
			var isChecked = ResolveSelectionState(
				ext,
				previousSelections,
				previousStateCache,
				hasStateCache,
				defaultForNewEntry: true);

			list.Add(new SelectionOption(ext, isChecked));
		}

		return list;
	}

	public IReadOnlyList<SelectionOption> BuildRootFolderOptions(
		IEnumerable<string> rootFolders,
		IReadOnlySet<string> previousSelections,
		IgnoreRules ignoreRules,
		bool hasPreviousSelections = false,
		IReadOnlyDictionary<string, bool>? previousStateCache = null)
	{
		var list = new List<SelectionOption>();
		var hasStateCache = previousStateCache is not null;
		var hasPrevious = hasPreviousSelections || previousSelections.Count > 0;

		foreach (var name in rootFolders)
		{
			var isChecked = hasStateCache
				? ResolveSelectionState(
					name,
					previousSelections,
					previousStateCache,
					hasStateCache,
					defaultForNewEntry: !IsIgnoredByRules(name, ignoreRules))
				: previousSelections.Contains(name) ||
				  (!hasPrevious && !IsIgnoredByRules(name, ignoreRules));

			list.Add(new SelectionOption(name, isChecked));
		}

		return list;
	}

	private static bool ResolveSelectionState(
		string name,
		IReadOnlySet<string> previousSelections,
		IReadOnlyDictionary<string, bool>? previousStateCache,
		bool hasStateCache,
		bool defaultForNewEntry)
	{
		if (hasStateCache && previousStateCache!.TryGetValue(name, out var cachedState))
			return cachedState;

		if (hasStateCache)
			return defaultForNewEntry;

		return previousSelections.Contains(name);
	}

	private static bool IsIgnoredByRules(string name, IgnoreRules rules)
	{
		if (rules.SmartIgnoredFolders.Contains(name))
			return true;

		if (IgnoreRuleSemantics.ShouldIgnoreDotDirectory(
			    rules.IgnoreDotFolders,
			    IgnoreRuleSemantics.IsDotName(name)))
			return true;

		return false;
	}
}
