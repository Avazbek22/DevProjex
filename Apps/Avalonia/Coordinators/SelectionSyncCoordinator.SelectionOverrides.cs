namespace DevProjex.Avalonia.Coordinators;

public sealed partial class SelectionSyncCoordinator
{
	internal bool ApplyHideSecretsOverride(bool? enabled) =>
		ApplyContentTransformationOverride(IgnoreOptionId.HideSecrets, enabled);

	internal bool ApplyHidePrivateDataOverride(bool? enabled) =>
		ApplyContentTransformationOverride(IgnoreOptionId.HidePrivateData, enabled);

	internal bool ApplyCompressCodeOverride(bool? enabled) =>
		ApplyContentTransformationOverride(IgnoreOptionId.CompressCode, enabled);

	internal bool ApplyStripCommentsOverride(bool? enabled) =>
		ApplyContentTransformationOverride(IgnoreOptionId.StripComments, enabled);

	internal bool ApplyStripBlankLinesOverride(bool? enabled) =>
		ApplyContentTransformationOverride(IgnoreOptionId.StripBlankLines, enabled);

	private bool ApplyContentTransformationOverride(IgnoreOptionId optionId, bool? enabled)
	{
		if (enabled is null)
			return false;

		var currentState = _session.IgnoreOptions.TryGetCachedState(
			optionId,
			out var cachedState)
			? cachedState
			: viewModel.IgnoreOptions.FirstOrDefault(
				option => option.Id == optionId)?.IsChecked == true;
		if (currentState == enabled.Value)
			return false;

		var stateCache = _session.IgnoreOptions.SnapshotStateCache();
		stateCache[optionId] = enabled.Value;
		_session.IgnoreOptions.ReplaceStateCache(stateCache);
		_session.IgnoreOptions.IsInitialized = true;

		_suppressIgnoreItemCheck = true;
		try
		{
			var option = viewModel.IgnoreOptions.FirstOrDefault(
				candidate => candidate.Id == optionId);
			if (option is not null)
				option.IsChecked = enabled.Value;
		}
		finally
		{
			_suppressIgnoreItemCheck = false;
		}

		SynchronizeDerivedAggregateSelectionState();

		RequestPendingApplyEvaluation();
		contentTransformationChanged?.Invoke(optionId);
		return true;
	}

    internal bool ApplySelectionOverrides(
        string currentPath,
        IReadOnlyCollection<string>? selectedExtensions,
        IReadOnlySet<IgnoreOptionId>? selectedIgnoreOptions,
        bool ignoreOptionStateIsComplete = false,
		bool resetExtensionSelectionToDefaults = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPath);
        if (IsStalePathRequest(currentPath))
            return false;

        var extensionSelectionChanged = resetExtensionSelectionToDefaults
			? ResetExtensionSelectionToDefaults()
			: ApplyExtensionSelectionOverride(selectedExtensions);
        var ignoreSelectionChanged = ApplyIgnoreSelectionOverrideCore(
            selectedIgnoreOptions,
            ignoreOptionStateIsComplete);
        if (!extensionSelectionChanged && !ignoreSelectionChanged)
            return false;

        _session.AdvanceRevision();
        RequestPendingApplyEvaluation();

        // A combined Desktop request is one logical selection transaction. Queue only the
        // final state so intermediate checkbox states never start competing scans.
        if (ignoreSelectionChanged)
            QueueFullRefresh(currentPath, changedIgnoreOptionId: null);
        else
            QueueLiveOptionsRefresh(currentPath, SelectionRefreshOrigin.Unknown);

		return true;
    }

	private bool ResetExtensionSelectionToDefaults()
	{
		var changed = _session.Extensions.IsInitialized ||
		              _session.ExtensionSelectionIsExplicit ||
		              viewModel.Extensions.Any(static option => !option.IsChecked);
		if (!changed)
			return false;

		_session.Extensions.RestoreDefaults(trimExcess: false);
		_session.ExtensionSelectionIsExplicit = false;
		_suppressExtensionItemCheck = true;
		try
		{
			foreach (var option in viewModel.Extensions)
				option.IsChecked = true;
		}
		finally
		{
			_suppressExtensionItemCheck = false;
		}

		SynchronizeDerivedAggregateSelectionState();
		return true;
	}

    private bool ApplyExtensionSelectionOverride(IReadOnlyCollection<string>? selectedExtensions)
    {
        if (selectedExtensions is null)
            return false;

        var selected = new HashSet<string>(selectedExtensions, StringComparer.OrdinalIgnoreCase);
        var exactStates = BuildExactSelectionStates(
            _session.Extensions.OptionStates,
            viewModel.Extensions.Select(static option => option.Name),
            selected,
            StringComparer.OrdinalIgnoreCase);
        var changed = !_session.ExtensionSelectionIsExplicit ||
                      !_session.Extensions.IsInitialized ||
                      !_session.Extensions.HasFullState ||
                      !SetStatesMatch(_session.Extensions.SelectedNames, selected) ||
                      !DictionaryStatesMatch(_session.Extensions.OptionStates, exactStates);
        _suppressExtensionItemCheck = true;
        try
        {
            foreach (var option in viewModel.Extensions)
            {
                var isChecked = selected.Contains(option.Name);
                option.IsChecked = isChecked;
            }
        }
        finally
        {
            _suppressExtensionItemCheck = false;
        }

        _session.Extensions.RestoreProfile(selected, exactStates);
        _session.ExtensionSelectionIsExplicit = true;
        SyncAllCheckbox(
            viewModel.Extensions,
            ref _suppressExtensionAllCheck,
            value => viewModel.AllExtensionsChecked = value);
        return changed;
    }

    private bool ApplyIgnoreSelectionOverrideCore(
        IReadOnlySet<IgnoreOptionId>? selectedIgnoreOptions,
        bool optionStateIsComplete)
    {
        if (selectedIgnoreOptions is null)
            return false;

        var stateCache = _session.IgnoreOptions.SnapshotStateCache();
        foreach (var optionId in stateCache.Keys.ToArray())
            stateCache[optionId] = selectedIgnoreOptions.Contains(optionId);
        foreach (var option in _ignoreOptions)
            stateCache[option.Id] = selectedIgnoreOptions.Contains(option.Id);
        foreach (var option in viewModel.IgnoreOptions)
            stateCache[option.Id] = selectedIgnoreOptions.Contains(option.Id);

        // Git modes remain explicit even if repository evidence temporarily hides a row.
        stateCache[IgnoreOptionId.UseGitIgnore] =
            selectedIgnoreOptions.Contains(IgnoreOptionId.UseGitIgnore);
        stateCache[IgnoreOptionId.TrackedGitFilesOnly] =
            selectedIgnoreOptions.Contains(IgnoreOptionId.TrackedGitFilesOnly);
        if (optionStateIsComplete)
        {
            foreach (var optionId in Enum.GetValues<IgnoreOptionId>())
                stateCache[optionId] = selectedIgnoreOptions.Contains(optionId);
        }
        GitFilteringModeResolver.Normalize(stateCache);

        var changed = !_session.IgnoreOptions.IsInitialized ||
                      _session.IgnoreOptions.AllPreference is not null ||
                      optionStateIsComplete && !_session.IgnoreOptionStateCacheIsComplete ||
                      !DictionaryStatesMatch(_session.IgnoreOptions.OptionStateCache, stateCache);
        if (!changed)
            return false;

        _session.IgnoreOptions.ReplaceStateCache(stateCache);
        _session.IgnoreOptions.AllPreference = null;
        if (optionStateIsComplete)
            _session.IgnoreOptionStateCacheIsComplete = true;

        _suppressIgnoreItemCheck = true;
        try
        {
            foreach (var option in viewModel.IgnoreOptions)
                option.IsChecked = stateCache.GetValueOrDefault(option.Id);
        }
        finally
        {
            _suppressIgnoreItemCheck = false;
        }

        SyncIgnoreAllCheckbox();
        return true;
    }

    private static Dictionary<string, bool> BuildExactSelectionStates(
        IReadOnlyDictionary<string, bool> cachedStates,
        IEnumerable<string> visibleNames,
        IReadOnlySet<string> selectedNames,
        StringComparer comparer)
    {
        var states = new Dictionary<string, bool>(cachedStates, comparer);
        foreach (var name in visibleNames)
            states[name] = selectedNames.Contains(name);
        foreach (var name in selectedNames)
            states[name] = true;
        foreach (var name in states.Keys.ToArray())
            states[name] = selectedNames.Contains(name);

        return states;
    }
}
