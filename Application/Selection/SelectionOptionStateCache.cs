using DevProjex.Application.Models;

namespace DevProjex.Application.Selection;

public sealed class SelectionOptionStateCache
{
    private readonly StringComparer _comparer;

    public SelectionOptionStateCache(StringComparer comparer)
    {
        _comparer = comparer;
        SelectedNames = new HashSet<string>(_comparer);
        OptionStates = new Dictionary<string, bool>(_comparer);
    }

    public bool IsInitialized { get; private set; }

    public bool HasFullState { get; private set; }

    public HashSet<string> SelectedNames { get; private set; }

    public Dictionary<string, bool> OptionStates { get; }

    public void RestoreProfile(
        IReadOnlyCollection<string> selectedNames,
        IReadOnlyDictionary<string, bool>? optionStates)
    {
        IsInitialized = true;
        SelectedNames = new HashSet<string>(selectedNames, _comparer);
        OptionStates.Clear();
        HasFullState = optionStates is not null;

        if (optionStates is null)
            return;

        foreach (var (name, isChecked) in optionStates)
            OptionStates[name] = isChecked;
    }

    public void RestoreDefaults(bool trimExcess)
    {
        IsInitialized = false;
        HasFullState = false;
        SelectedNames.Clear();
        OptionStates.Clear();

        if (!trimExcess)
            return;

        SelectedNames.TrimExcess();
        OptionStates.TrimExcess();
    }

    public void UpdateFromVisibleOptions(IEnumerable<SelectionOption> options)
    {
        IsInitialized = true;
        HasFullState = true;

        var selected = new HashSet<string>(_comparer);
        foreach (var option in options)
        {
            if (option.IsChecked)
                selected.Add(option.Name);

            // Do not clear previous states here. Hidden options may be temporarily absent
            // because another section hides their evidence, and persistence must keep the
            // user's explicit choice until the option becomes visible again.
            OptionStates[option.Name] = option.IsChecked;
        }

        SelectedNames = selected;
    }

    public HashSet<string> SnapshotSelectedNames() =>
        new(SelectedNames, _comparer);

    public IReadOnlyDictionary<string, bool>? SnapshotOptionStatesOrNull(bool suppressLegacySelectedOnlyState)
    {
        if (!IsInitialized)
            return null;

        // Legacy selected-only profiles intentionally have no full state. During profile
        // restore we must not reinterpret that as an explicit "future items are checked"
        // contract, otherwise new roots/extensions can flip unpredictably after refresh.
        if (suppressLegacySelectedOnlyState && !HasFullState)
            return null;

        return new Dictionary<string, bool>(OptionStates, _comparer);
    }
}
