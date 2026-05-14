namespace DevProjex.Application.Selection;

public sealed class IgnoreSelectionState
{
	private readonly HashSet<IgnoreOptionId> _selectedOptions = [];
	private readonly Dictionary<IgnoreOptionId, bool> _optionStateCache = [];

	public bool IsInitialized { get; set; }

	public bool? AllPreference { get; set; }

	public IReadOnlySet<IgnoreOptionId> SelectedOptions => _selectedOptions;

	public IReadOnlyDictionary<IgnoreOptionId, bool> OptionStateCache => _optionStateCache;

	public HashSet<IgnoreOptionId> SnapshotSelectedOptions() => new(_selectedOptions);

	public Dictionary<IgnoreOptionId, bool> SnapshotStateCache() => new(_optionStateCache);

	public bool TryGetCachedState(IgnoreOptionId optionId, out bool isChecked) =>
		_optionStateCache.TryGetValue(optionId, out isChecked);

	public void Reset(bool trimExcess)
	{
		IsInitialized = false;
		AllPreference = null;
		_selectedOptions.Clear();
		_optionStateCache.Clear();

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
	}

	public void ReplaceStateCache(IReadOnlyDictionary<IgnoreOptionId, bool> stateCache)
	{
		_optionStateCache.Clear();
		foreach (var (id, isChecked) in stateCache)
			_optionStateCache[id] = isChecked;

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

		RebuildSelectedOptions();
	}

	public void UpdateFromVisibleOptions(
		IEnumerable<(IgnoreOptionId Id, bool IsChecked)> visibleOptions,
		IReadOnlySet<IgnoreOptionId>? preserveMissingFrom,
		IEnumerable<IgnoreOptionId> visibleDescriptorIds)
	{
		if (preserveMissingFrom is not null && preserveMissingFrom.Count > 0)
			PreserveMissingSelections(preserveMissingFrom, visibleDescriptorIds);

		foreach (var (id, isChecked) in visibleOptions)
			_optionStateCache[id] = isChecked;

		RebuildSelectedOptions();
	}

	public void ApplyAllPreferenceToKnownStates(bool isChecked)
	{
		if (_optionStateCache.Count == 0)
			return;

		var knownIds = new List<IgnoreOptionId>(_optionStateCache.Keys);
		foreach (var id in knownIds)
			_optionStateCache[id] = isChecked;

		RebuildSelectedOptions();
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
