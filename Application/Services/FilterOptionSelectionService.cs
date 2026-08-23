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
		var ordered = extensions.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
		if (previousStateCache is not null)
		{
			var evolution = SelectionEvolutionPolicy.Reconcile(
				ordered,
				previousSelections,
				previousStateCache,
				static _ => true,
				StringComparer.OrdinalIgnoreCase);
			return ordered
				.Select(extension => new SelectionOption(
					extension,
					evolution.SelectedItems.Contains(extension)))
				.ToArray();
		}

		var list = new List<SelectionOption>(ordered.Count);
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
		var available = rootFolders.ToArray();
		if (previousStateCache is not null)
		{
			var evolution = SelectionEvolutionPolicy.Reconcile(
				available,
				previousSelections,
				previousStateCache,
				name => !IsIgnoredByRules(name, ignoreRules),
				PathComparer.Default);
			return available
				.Select(name => new SelectionOption(
					name,
					evolution.SelectedItems.Contains(name)))
				.ToArray();
		}

		var list = new List<SelectionOption>(available.Length);
		var resolver = new SelectionStateResolver(previousSelections, previousStateCache);
		var hasPrevious = hasPreviousSelections || previousSelections.Count > 0;

		foreach (var name in available)
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
