namespace DevProjex.Application.Selection;

public sealed class IgnoreSelectionState
{
	private readonly HashSet<IgnoreOptionId> _selectedOptions = [];
	private readonly Dictionary<IgnoreOptionId, bool> _optionStateCache = [];
	private GitFilteringMode _preferredGitFilteringMode;

	public bool IsInitialized { get; set; }

	public bool? AllPreference { get; set; }

	public IReadOnlySet<IgnoreOptionId> SelectedOptions => _selectedOptions;

	public IReadOnlyDictionary<IgnoreOptionId, bool> OptionStateCache => _optionStateCache;

	public HashSet<IgnoreOptionId> SnapshotSelectedOptions() => new(_selectedOptions);

	public Dictionary<IgnoreOptionId, bool> SnapshotStateCache() => new(_optionStateCache);

	public IgnoreSelectionStateSnapshot CaptureSnapshot() =>
		new(
			IsInitialized,
			AllPreference,
			new Dictionary<IgnoreOptionId, bool>(_optionStateCache),
			_preferredGitFilteringMode);

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
		RebuildSelectedOptions();
	}

	public void ReplaceStateCache(IReadOnlyDictionary<IgnoreOptionId, bool> stateCache)
	{
		AllPreference = null;
		_optionStateCache.Clear();
		foreach (var (id, isChecked) in stateCache)
			_optionStateCache[id] = isChecked;

		GitFilteringModeResolver.Normalize(_optionStateCache);
		_preferredGitFilteringMode = GitFilteringModeResolver.Resolve(_optionStateCache);
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
		RememberActiveGitFilteringMode();
		RebuildSelectedOptions();
	}

	public void ApplyAllPreferenceToKnownStates(
		bool isChecked,
		IReadOnlySet<IgnoreOptionId>? excludedOptions = null)
	{
		if (_optionStateCache.Count == 0)
			return;

		RememberActiveGitFilteringMode();
		var knownIds = new List<IgnoreOptionId>(_optionStateCache.Keys);
		foreach (var id in knownIds)
		{
			if (excludedOptions is not null && excludedOptions.Contains(id))
				continue;
			_optionStateCache[id] = isChecked;
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

		RememberActiveGitFilteringMode();
		RebuildSelectedOptions();
	}

	private void RememberActiveGitFilteringMode()
	{
		var activeMode = GitFilteringModeResolver.Resolve(_optionStateCache);
		if (activeMode != GitFilteringMode.None)
			_preferredGitFilteringMode = activeMode;
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
	GitFilteringMode PreferredGitFilteringMode);
