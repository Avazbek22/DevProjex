namespace DevProjex.Application.Selection;

public sealed class IgnoreSelectionState
{
	private readonly HashSet<IgnoreOptionId> _selectedOptions = [];
	private readonly Dictionary<IgnoreOptionId, bool> _optionStateCache = [];
	private GitFilteringMode _preferredGitFilteringMode;
	private GitFilteringMode _activeGitFilteringMode;

	public bool IsInitialized { get; set; }

	public bool? AllPreference { get; set; }

	public IReadOnlySet<IgnoreOptionId> SelectedOptions => _selectedOptions;

	public IReadOnlyDictionary<IgnoreOptionId, bool> OptionStateCache => _optionStateCache;

	public GitFilteringMode PreferredGitFilteringMode => _preferredGitFilteringMode;
	public GitFilteringMode ActiveGitFilteringMode => _activeGitFilteringMode;

	public HashSet<IgnoreOptionId> SnapshotSelectedOptions() => new(_selectedOptions);

	public Dictionary<IgnoreOptionId, bool> SnapshotStateCache() => new(_optionStateCache);

	public IgnoreSelectionStateSnapshot CaptureSnapshot() =>
		new(
			IsInitialized,
			AllPreference,
			new Dictionary<IgnoreOptionId, bool>(_optionStateCache),
			_preferredGitFilteringMode,
			_activeGitFilteringMode);

	public void RestoreSnapshot(IgnoreSelectionStateSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		IsInitialized = snapshot.IsInitialized;
		AllPreference = snapshot.AllPreference;
		_optionStateCache.Clear();
		foreach (var (id, isChecked) in snapshot.OptionStateCache)
			_optionStateCache[id] = isChecked;

		// Preferred mode is independent state while both Git choices are temporarily off.
		// Re-resolving it from the map would silently change the next "select all" result.
		_preferredGitFilteringMode = snapshot.PreferredGitFilteringMode;
		_activeGitFilteringMode = snapshot.ActiveGitFilteringMode;
		RebuildSelectedOptions();
	}

	public bool TryGetCachedState(IgnoreOptionId optionId, out bool isChecked) =>
		_optionStateCache.TryGetValue(optionId, out isChecked);

	public void Reset(bool trimExcess)
	{
		IsInitialized = false;
		AllPreference = null;
		_selectedOptions.Clear();
		_optionStateCache.Clear();
		_preferredGitFilteringMode = GitFilteringMode.None;
		_activeGitFilteringMode = GitFilteringMode.None;

		if (!trimExcess)
			return;

		_selectedOptions.TrimExcess();
		_optionStateCache.TrimExcess();
	}

	public void RestoreProfileSelection(IEnumerable<IgnoreOptionId> selectedOptions)
	{
		Reset(trimExcess: false);
		IsInitialized = true;
		foreach (var id in selectedOptions)
		{
			_selectedOptions.Add(id);
			_optionStateCache[id] = true;
		}

		GitFilteringModeResolver.Normalize(_optionStateCache);
		_preferredGitFilteringMode = GitFilteringModeResolver.Resolve(_optionStateCache);
		_activeGitFilteringMode = _preferredGitFilteringMode;
		RebuildSelectedOptions();
	}

	public void ReplaceStateCache(IReadOnlyDictionary<IgnoreOptionId, bool> stateCache)
	{
		ReplaceStateCacheCore(stateCache, preserveRuntimePreferences: false);
	}

	public void ReplaceStateCachePreservingRuntimePreferences(
		IReadOnlyDictionary<IgnoreOptionId, bool> stateCache)
	{
		ReplaceStateCacheCore(stateCache, preserveRuntimePreferences: true);
	}

	private void ReplaceStateCacheCore(
		IReadOnlyDictionary<IgnoreOptionId, bool> stateCache,
		bool preserveRuntimePreferences)
	{
		var previousAllPreference = AllPreference;
		var previousPreferredGitFilteringMode = _preferredGitFilteringMode;
		var previousActiveGitFilteringMode = _activeGitFilteringMode;
		AllPreference = null;
		_optionStateCache.Clear();
		foreach (var (id, isChecked) in stateCache)
			_optionStateCache[id] = isChecked;

		GitFilteringModeResolver.Normalize(_optionStateCache);
		var activeGitFilteringMode = GitFilteringModeResolver.Resolve(_optionStateCache);
		var preserveMomentaryMode = preserveRuntimePreferences &&
			GitScopeSelection.IsMomentary(previousActiveGitFilteringMode) &&
			GitScopeSelection.ToUnderlayMode(previousActiveGitFilteringMode) ==
			activeGitFilteringMode;
		if (preserveRuntimePreferences)
			AllPreference = previousAllPreference;
		_preferredGitFilteringMode = preserveRuntimePreferences &&
		                             (preserveMomentaryMode ||
		                              activeGitFilteringMode == GitFilteringMode.None)
			? previousPreferredGitFilteringMode
			: activeGitFilteringMode;
		_activeGitFilteringMode = preserveMomentaryMode
			? previousActiveGitFilteringMode
			: activeGitFilteringMode;
		RebuildSelectedOptions();
		IsInitialized = true;
	}

	public void EnsureDefaults(IReadOnlyList<IgnoreOptionDescriptor> options)
	{
		if (IsInitialized || _selectedOptions.Count > 0)
			return;

		_optionStateCache.Clear();
		foreach (var option in options)
			_optionStateCache[option.Id] = option.DefaultChecked;

		_preferredGitFilteringMode = GitFilteringModeResolver.Resolve(_optionStateCache);
		_activeGitFilteringMode = _preferredGitFilteringMode;
		RebuildSelectedOptions();
	}

	public void UpdateFromVisibleOptions(
		IEnumerable<(IgnoreOptionId Id, bool IsChecked)> visibleOptions,
		IReadOnlySet<IgnoreOptionId>? preserveMissingFrom,
		IEnumerable<IgnoreOptionId> visibleDescriptorIds)
	{
		// Visibility is evidence-driven, not ownership of state. A checkbox disappearing
		// because another rule hid its evidence must not silently disable that rule.
		if (preserveMissingFrom is not null && preserveMissingFrom.Count > 0)
			PreserveMissingSelections(preserveMissingFrom, visibleDescriptorIds);

		foreach (var (id, isChecked) in visibleOptions)
			_optionStateCache[id] = isChecked;

		GitFilteringModeResolver.Normalize(_optionStateCache);
		SynchronizeActiveGitFilteringMode();
		RebuildSelectedOptions();
	}

	public void ApplyAllPreferenceToKnownStates(
		bool isChecked,
		IReadOnlySet<IgnoreOptionId>? excludedOptions = null)
	{
		if (_optionStateCache.Count == 0)
			return;

		SynchronizeActiveGitFilteringMode();
		var knownIds = new List<IgnoreOptionId>(_optionStateCache.Keys);
		foreach (var id in knownIds)
		{
			if (excludedOptions is not null && excludedOptions.Contains(id))
				continue;
			_optionStateCache[id] = isChecked;
		}
		var preserveGitFilteringMode = excludedOptions is not null &&
		                               excludedOptions.Contains(IgnoreOptionId.UseGitIgnore) &&
		                               excludedOptions.Contains(IgnoreOptionId.TrackedGitFilesOnly);
		if (preserveGitFilteringMode)
		{
			RebuildSelectedOptions();
			return;
		}

		if (isChecked &&
		    _optionStateCache.ContainsKey(IgnoreOptionId.UseGitIgnore) &&
		    _optionStateCache.ContainsKey(IgnoreOptionId.TrackedGitFilesOnly))
		{
			// "All off" is a temporary UI state. Restoring all options must not silently
			// replace the user's strict tracked-files mode with the regular .gitignore mode.
			if (_preferredGitFilteringMode == GitFilteringMode.TrackedFilesOnly)
				_optionStateCache[IgnoreOptionId.UseGitIgnore] = false;
			else
				_optionStateCache[IgnoreOptionId.TrackedGitFilesOnly] = false;
		}

		_activeGitFilteringMode = isChecked
			? GitFilteringModeResolver.Resolve(_optionStateCache)
			: GitFilteringMode.None;
		RememberPersistentGitFilteringMode();
		RebuildSelectedOptions();
	}

	public void SetActiveGitFilteringMode(
		GitFilteringMode mode,
		bool rememberPersistentPreference = true)
	{
		var underlay = GitScopeSelection.ToUnderlayMode(mode);
		_optionStateCache[IgnoreOptionId.UseGitIgnore] =
			underlay == GitFilteringMode.RespectGitIgnore;
		_optionStateCache[IgnoreOptionId.TrackedGitFilesOnly] =
			underlay == GitFilteringMode.TrackedFilesOnly;
		_activeGitFilteringMode = mode;
		if (rememberPersistentPreference)
		{
			_preferredGitFilteringMode = GitScopeSelection.ResolvePreferredPersistentMode(
				_preferredGitFilteringMode,
				mode);
		}
		AllPreference = null;
		IsInitialized = true;
		RebuildSelectedOptions();
	}

	private void SynchronizeActiveGitFilteringMode()
	{
		var underlay = GitFilteringModeResolver.Resolve(_optionStateCache);
		if (!GitScopeSelection.IsMomentary(_activeGitFilteringMode) ||
		    GitScopeSelection.ToUnderlayMode(_activeGitFilteringMode) != underlay)
		{
			_activeGitFilteringMode = underlay;
		}
		RememberPersistentGitFilteringMode();
	}

	private void RememberPersistentGitFilteringMode()
	{
		if (GitScopeSelection.IsPersistent(_activeGitFilteringMode) &&
		    _activeGitFilteringMode != GitFilteringMode.None)
		{
			_preferredGitFilteringMode = _activeGitFilteringMode;
		}
	}

	private void PreserveMissingSelections(
		IReadOnlySet<IgnoreOptionId> preserveMissingFrom,
		IEnumerable<IgnoreOptionId> visibleDescriptorIds)
	{
		// Dynamic options can temporarily disappear while another toggle hides their evidence.
		// Preserve their explicit state so availability churn does not erase user intent.
		var visibleIds = new HashSet<IgnoreOptionId>(visibleDescriptorIds);
		foreach (var id in preserveMissingFrom)
		{
			if (!visibleIds.Contains(id) && !_optionStateCache.ContainsKey(id))
				_optionStateCache[id] = true;
		}
	}

	private void RebuildSelectedOptions()
	{
		_selectedOptions.Clear();
		foreach (var (id, isChecked) in _optionStateCache)
		{
			if (isChecked)
				_selectedOptions.Add(id);
		}
	}
}

public sealed record IgnoreSelectionStateSnapshot(
	bool IsInitialized,
	bool? AllPreference,
	IReadOnlyDictionary<IgnoreOptionId, bool> OptionStateCache,
	GitFilteringMode PreferredGitFilteringMode,
	GitFilteringMode ActiveGitFilteringMode = GitFilteringMode.None);
