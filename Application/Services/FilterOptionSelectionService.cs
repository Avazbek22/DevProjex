using DevProjex.Application.Selection;
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
		var resolver = new SelectionStateResolver(previousSelections, previousStateCache);
		foreach (var ext in ordered)
		{
			var isChecked = resolver.Resolve(ext, defaultForNewEntry: true);

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
		var resolver = new SelectionStateResolver(previousSelections, previousStateCache);
		var hasPrevious = hasPreviousSelections || previousSelections.Count > 0;

		foreach (var name in rootFolders)
		{
			var isChecked = previousStateCache is not null
				? resolver.Resolve(name, defaultForNewEntry: !IsIgnoredByRules(name, ignoreRules))
				: previousSelections.Contains(name) || (!hasPrevious && !IsIgnoredByRules(name, ignoreRules));

			list.Add(new SelectionOption(name, isChecked));
		}

		return list;
	}

	private static bool IsIgnoredByRules(string name, IgnoreRules rules)
	{
		if (rules.UseSmartIgnore && rules.SmartIgnoredFolders.Contains(name))
			return true;

		if (IgnoreRuleSemantics.ShouldIgnoreDotDirectory(
			    rules.IgnoreDotFolders,
			    IgnoreRuleSemantics.IsDotName(name)))
			return true;

		return false;
	}
}
