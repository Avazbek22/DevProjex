using DevProjex.Application.Models;

namespace DevProjex.Application.Selection;

public static class ProjectSelectionProfileBuilder
{
    public static ProjectSelectionProfile Create(
        IEnumerable<SelectionOption> visibleExtensions,
        IEnumerable<IgnoreSelectionOption> visibleIgnoreOptions,
        IReadOnlyDictionary<string, bool>? cachedExtensionStates,
        IReadOnlyDictionary<IgnoreOptionId, bool>? cachedIgnoreOptionStates,
        IReadOnlyCollection<IgnoreOptionId> selectedIgnoreOptions,
        StringComparer extensionComparer,
		IReadOnlyCollection<MarkedSecretProfileEntry>? markedSecrets = null)
    {
        // Selected arrays are the effective projection used by older readers. Full state
        // maps are the durable local-profile contract because unchecked and temporarily
        // hidden rows carry user intent just as strongly as visible checked rows.
        var extensionOptions = MaterializeSelectionOptions(visibleExtensions);
        var ignoreOptions = MaterializeIgnoreOptions(visibleIgnoreOptions);
        var selectedIgnoreOptionSet = new HashSet<IgnoreOptionId>(selectedIgnoreOptions);
        var preferredGitMode = GitFilteringModeResolver.Resolve(selectedIgnoreOptionSet);
        GitFilteringModeResolver.Normalize(selectedIgnoreOptionSet, preferredGitMode);
        var ignoreOptionStates = MergeIgnoreStates(cachedIgnoreOptionStates, ignoreOptions);
        GitFilteringModeResolver.Normalize(ignoreOptionStates, preferredGitMode);

        return new ProjectSelectionProfile(
            // TODO(cli): Remove the legacy root-selection fields from portable and CLI profile
            // contracts when the public --root option is revised. Desktop no longer persists them.
            SelectedRootFolders: [],
            SelectedExtensions: CollectCheckedNames(extensionOptions, extensionComparer),
            SelectedIgnoreOptions: selectedIgnoreOptionSet.ToArray(),
            RootFolderStates: null,
            ExtensionStates: MergeSelectionStates(cachedExtensionStates, extensionOptions, extensionComparer),
            IgnoreOptionStates: ignoreOptionStates,
			MarkedSecrets: markedSecrets?.ToArray() ?? []);
    }

    public static ProjectSelectionProfile Clone(ProjectSelectionProfile profile)
    {
        var selectedIgnoreOptions = new HashSet<IgnoreOptionId>(profile.SelectedIgnoreOptions);
        var preferredGitMode = GitFilteringModeResolver.Resolve(selectedIgnoreOptions);
        GitFilteringModeResolver.Normalize(selectedIgnoreOptions, preferredGitMode);
        var ignoreOptionStates = profile.IgnoreOptionStates is null
            ? null
            : new Dictionary<IgnoreOptionId, bool>(profile.IgnoreOptionStates);
        if (ignoreOptionStates is not null)
            GitFilteringModeResolver.Normalize(ignoreOptionStates, preferredGitMode);

        return new ProjectSelectionProfile(
            SelectedRootFolders: profile.SelectedRootFolders.ToArray(),
            SelectedExtensions: profile.SelectedExtensions.ToArray(),
            SelectedIgnoreOptions: selectedIgnoreOptions.ToArray(),
            RootFolderStates: profile.RootFolderStates is null
                ? null
                : new Dictionary<string, bool>(profile.RootFolderStates, PathComparer.Default),
            ExtensionStates: profile.ExtensionStates is null
                ? null
                : new Dictionary<string, bool>(profile.ExtensionStates, StringComparer.OrdinalIgnoreCase),
            IgnoreOptionStates: ignoreOptionStates,
			SelectedPaths: profile.SelectedPaths?.ToArray(),
			MarkedSecrets: profile.MarkedSecrets?.ToArray());
    }

    private static List<SelectionOption> MaterializeSelectionOptions(IEnumerable<SelectionOption> options)
    {
        return options switch
        {
            List<SelectionOption> list => list,
            SelectionOption[] array => [..array],
            _ => options.ToList()
        };
    }

    private static List<IgnoreSelectionOption> MaterializeIgnoreOptions(IEnumerable<IgnoreSelectionOption> options)
    {
        return options switch
        {
            List<IgnoreSelectionOption> list => list,
            IgnoreSelectionOption[] array => [..array],
            _ => options.ToList()
        };
    }

    private static HashSet<string> CollectCheckedNames(
        IEnumerable<SelectionOption> options,
        StringComparer comparer)
    {
        var selected = new HashSet<string>(comparer);
        foreach (var option in options)
        {
            if (option.IsChecked)
                selected.Add(option.Name);
        }

        return selected;
    }

    private static Dictionary<string, bool> MergeSelectionStates(
        IReadOnlyDictionary<string, bool>? cachedStates,
        IEnumerable<SelectionOption> visibleOptions,
        StringComparer comparer)
    {
        var states = cachedStates is null
            ? new Dictionary<string, bool>(comparer)
            : new Dictionary<string, bool>(cachedStates, comparer);

        foreach (var option in visibleOptions)
            states[option.Name] = option.IsChecked;

        return states;
    }

    private static Dictionary<IgnoreOptionId, bool> MergeIgnoreStates(
        IReadOnlyDictionary<IgnoreOptionId, bool>? cachedStates,
        IEnumerable<IgnoreSelectionOption> visibleOptions)
    {
        var states = cachedStates is null
            ? []
            : new Dictionary<IgnoreOptionId, bool>(cachedStates);

        foreach (var option in visibleOptions)
            states[option.Id] = option.IsChecked;

        return states;
    }
}
