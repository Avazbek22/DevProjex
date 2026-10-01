namespace DevProjex.Infrastructure.ProjectProfiles;

public enum ProjectProfilePersistenceDisposition
{
	Saved,
	Unchanged,
	Deferred,
	Failed
}

public readonly record struct ProjectProfilePersistenceResult(
	ProjectProfilePersistenceDisposition Disposition,
	string? Reason = null)
{
	public bool Completed => Disposition is
		ProjectProfilePersistenceDisposition.Saved or
		ProjectProfilePersistenceDisposition.Unchanged;

	public static ProjectProfilePersistenceResult Saved() =>
		new(ProjectProfilePersistenceDisposition.Saved);

	public static ProjectProfilePersistenceResult Unchanged() =>
		new(ProjectProfilePersistenceDisposition.Unchanged);

	public static ProjectProfilePersistenceResult Deferred(string reason) =>
		new(ProjectProfilePersistenceDisposition.Deferred, reason);

	public static ProjectProfilePersistenceResult Failed(string reason) =>
		new(ProjectProfilePersistenceDisposition.Failed, reason);
}

[Flags]
public enum ProjectProfileMergeFields
{
	None = 0,
	RootFolders = 1 << 0,
	Extensions = 1 << 1,
	IgnoreOptions = 1 << 2,
	RootFolderStates = 1 << 3,
	ExtensionStates = 1 << 4,
	IgnoreOptionStates = 1 << 5,
	SelectedPaths = 1 << 6,
	AllSelections = RootFolders |
					Extensions |
					IgnoreOptions |
					RootFolderStates |
					ExtensionStates |
					IgnoreOptionStates |
					SelectedPaths
}

public readonly record struct ProjectProfileMergeResult(
	ProjectProfileSaveResult SaveResult,
	ProjectSelectionProfile? PersistedProfile)
{
	public bool Succeeded => SaveResult.Succeeded;
}

public static class ProjectProfileMergeWriter
{
	public static ProjectProfileMergeResult TryMerge(
		IProjectProfileStore store,
		string projectPath,
		ProjectSelectionProfile candidate,
		ProjectSelectionProfile? baseline,
		ProjectProfileMergeFields allowedFields,
		TimeSpan lookupTimeout,
		int maximumAttempts = 4,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(store);
		ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
		ArgumentNullException.ThrowIfNull(candidate);
		ArgumentOutOfRangeException.ThrowIfLessThan(maximumAttempts, 1);

		var changedFields = GetChangedFields(baseline, candidate, allowedFields);
		var ignoreOptionChanges = GetIgnoreOptionChanges(baseline, candidate, allowedFields);
		var extensionChanges = GetSelectionChanges(
			baseline,
			candidate,
			allowedFields,
			ProjectProfileMergeFields.Extensions,
			ProjectProfileMergeFields.ExtensionStates,
			static profile => profile.SelectedExtensions,
			static profile => profile.ExtensionStates,
			StringComparer.OrdinalIgnoreCase);
		var rootFolderChanges = GetSelectionChanges(
			baseline,
			candidate,
			allowedFields,
			ProjectProfileMergeFields.RootFolders,
			ProjectProfileMergeFields.RootFolderStates,
			static profile => profile.SelectedRootFolders,
			static profile => profile.RootFolderStates,
			ProjectTreePathIdentity.CanonicalComparer);
		var wholeFieldChanges = baseline is null
			? changedFields
			: changedFields & ~(
				ProjectProfileMergeFields.RootFolders |
				ProjectProfileMergeFields.Extensions |
				ProjectProfileMergeFields.IgnoreOptions |
				ProjectProfileMergeFields.RootFolderStates |
				ProjectProfileMergeFields.ExtensionStates |
				ProjectProfileMergeFields.IgnoreOptionStates);
		for (var attempt = 0; attempt < maximumAttempts; attempt++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var lookup = store.LookupProfile(projectPath, lookupTimeout);
			if (lookup.Status is not (ProjectProfileLookupStatus.Found or ProjectProfileLookupStatus.Missing))
				return new ProjectProfileMergeResult(new ProjectProfileSaveResult(ProjectProfileSaveStatus.Failed), null);

			var current = lookup.Profile ?? candidate;
			var merged = Apply(current, candidate, wholeFieldChanges);
			if (extensionChanges.Count > 0)
			{
				var extensionSelection = ApplySelectionChanges(
					current.SelectedExtensions,
					current.ExtensionStates,
					candidate,
					extensionChanges,
					static profile => profile.SelectedExtensions,
					static profile => profile.ExtensionStates,
					StringComparer.OrdinalIgnoreCase,
					"extension");
				merged = merged with
				{
					SelectedExtensions = extensionSelection.Selected,
					ExtensionStates = extensionSelection.States
				};
			}
			if (rootFolderChanges.Count > 0)
			{
				var rootSelection = ApplySelectionChanges(
					current.SelectedRootFolders,
					current.RootFolderStates,
					candidate,
					rootFolderChanges,
					static profile => profile.SelectedRootFolders,
					static profile => profile.RootFolderStates,
					ProjectTreePathIdentity.CanonicalComparer,
					"root folder");
				merged = merged with
				{
					SelectedRootFolders = rootSelection.Selected,
					RootFolderStates = rootSelection.States
				};
			}
			if (ignoreOptionChanges.Count > 0)
				merged = ApplyIgnoreOptionChanges(current, merged, candidate, ignoreOptionChanges);
			if (baseline is not null &&
				changedFields.HasFlag(ProjectProfileMergeFields.SelectedPaths) &&
				!NullableSetEquals(
					baseline.SelectedPaths,
					current.SelectedPaths,
					ProjectTreePathIdentity.CanonicalComparer))
			{
				Trace.TraceInformation(
					"Project profile selection frontier conflict; the later user action was retained.");
			}
			if (changedFields == ProjectProfileMergeFields.None)
				return new ProjectProfileMergeResult(new ProjectProfileSaveResult(ProjectProfileSaveStatus.Saved), current);

			var save = store.TrySaveProfileWithResult(projectPath, merged, lookup.UpdatedUtc);
			if (save.Succeeded)
				return new ProjectProfileMergeResult(save, merged);
			if (save.Status != ProjectProfileSaveStatus.Conflict && attempt + 1 == maximumAttempts)
				return new ProjectProfileMergeResult(save, null);
		}

		return new ProjectProfileMergeResult(new ProjectProfileSaveResult(ProjectProfileSaveStatus.Conflict), null);
	}

	private static IReadOnlyList<T> GetSelectionChanges<T>(
		ProjectSelectionProfile? baseline,
		ProjectSelectionProfile candidate,
		ProjectProfileMergeFields allowedFields,
		ProjectProfileMergeFields selectedField,
		ProjectProfileMergeFields stateField,
		Func<ProjectSelectionProfile, IReadOnlyCollection<T>> selected,
		Func<ProjectSelectionProfile, IReadOnlyDictionary<T, bool>?> states,
		IEqualityComparer<T> comparer)
		where T : notnull
	{
		if (baseline is null ||
			!allowedFields.HasFlag(selectedField) && !allowedFields.HasFlag(stateField))
		{
			return [];
		}

		var baselineStates = states(baseline);
		var candidateStates = states(candidate);
		return EnumerateSelectionKeys(baseline, candidate, selected, states, comparer)
			.Where(key => GetSelectionState(baseline, key, selected, states, comparer) !=
						  GetSelectionState(candidate, key, selected, states, comparer) ||
						  allowedFields.HasFlag(stateField) &&
						  HasNewExplicitUncheckedState(baselineStates, candidateStates, key))
			.ToArray();
	}

	private static SelectionMerge<T> ApplySelectionChanges<T>(
		IReadOnlyCollection<T> currentSelected,
		IReadOnlyDictionary<T, bool>? currentStates,
		ProjectSelectionProfile candidate,
		IReadOnlyList<T> changedKeys,
		Func<ProjectSelectionProfile, IReadOnlyCollection<T>> selected,
		Func<ProjectSelectionProfile, IReadOnlyDictionary<T, bool>?> states,
		IEqualityComparer<T> comparer,
		string selectionKind)
		where T : notnull
	{
		var mergedSelected = currentSelected.ToHashSet(comparer);
		var mergedStates = currentStates is null
			? new Dictionary<T, bool>(comparer)
			: new Dictionary<T, bool>(currentStates, comparer);
		foreach (var key in changedKeys)
		{
			var desired = GetSelectionState(candidate, key, selected, states, comparer);
			var current = currentStates?.TryGetValue(key, out var explicitState) == true
				? explicitState
				: mergedSelected.Contains(key);
			if (current != desired)
			{
				Trace.TraceInformation(
					"Project profile {0} conflict for {1}; the later user action was retained.",
					selectionKind,
					key);
			}
			mergedStates[key] = desired;
			if (desired)
				mergedSelected.Add(key);
			else
				mergedSelected.Remove(key);
		}

		return new SelectionMerge<T>(mergedSelected.ToArray(), mergedStates);
	}

	private static IEnumerable<T> EnumerateSelectionKeys<T>(
		ProjectSelectionProfile left,
		ProjectSelectionProfile right,
		Func<ProjectSelectionProfile, IReadOnlyCollection<T>> selected,
		Func<ProjectSelectionProfile, IReadOnlyDictionary<T, bool>?> states,
		IEqualityComparer<T> comparer)
		where T : notnull =>
		selected(left)
			.Concat(selected(right))
			.Concat(states(left)?.Keys ?? [])
			.Concat(states(right)?.Keys ?? [])
			.Distinct(comparer);

	private static bool GetSelectionState<T>(
		ProjectSelectionProfile profile,
		T key,
		Func<ProjectSelectionProfile, IReadOnlyCollection<T>> selected,
		Func<ProjectSelectionProfile, IReadOnlyDictionary<T, bool>?> states,
		IEqualityComparer<T> comparer)
		where T : notnull =>
		states(profile)?.TryGetValue(key, out var value) == true
			? value
			: selected(profile).Contains(key, comparer);

	private static bool HasNewExplicitUncheckedState<T>(
		IReadOnlyDictionary<T, bool>? baselineStates,
		IReadOnlyDictionary<T, bool>? candidateStates,
		T key)
		where T : notnull =>
		candidateStates is not null &&
		candidateStates.TryGetValue(key, out var selected) &&
		!selected &&
		baselineStates?.ContainsKey(key) != true;

	private readonly record struct SelectionMerge<T>(
		IReadOnlyCollection<T> Selected,
		IReadOnlyDictionary<T, bool> States)
		where T : notnull;

	private static IReadOnlyList<IgnoreOptionId> GetIgnoreOptionChanges(
		ProjectSelectionProfile? baseline,
		ProjectSelectionProfile candidate,
		ProjectProfileMergeFields allowedFields)
	{
		if (baseline is null ||
			!allowedFields.HasFlag(ProjectProfileMergeFields.IgnoreOptions) &&
			!allowedFields.HasFlag(ProjectProfileMergeFields.IgnoreOptionStates))
		{
			return [];
		}

		return EnumerateIgnoreOptionIds(baseline, candidate)
			.Where(id => GetIgnoreOptionState(baseline, id) != GetIgnoreOptionState(candidate, id) ||
						 allowedFields.HasFlag(ProjectProfileMergeFields.IgnoreOptionStates) &&
						 HasNewExplicitUncheckedState(
							 baseline.IgnoreOptionStates,
							 candidate.IgnoreOptionStates,
							 id))
			.ToArray();
	}

	private static ProjectSelectionProfile ApplyIgnoreOptionChanges(
		ProjectSelectionProfile current,
		ProjectSelectionProfile merged,
		ProjectSelectionProfile candidate,
		IReadOnlyList<IgnoreOptionId> changedOptions)
	{
		var selected = current.SelectedIgnoreOptions.ToHashSet();
		var states = current.IgnoreOptionStates is null
			? new Dictionary<IgnoreOptionId, bool>()
			: new Dictionary<IgnoreOptionId, bool>(current.IgnoreOptionStates);
		foreach (var option in changedOptions)
		{
			var desired = GetIgnoreOptionState(candidate, option);
			var currentValue = GetIgnoreOptionState(current, option);
			if (currentValue != desired)
			{
				Trace.TraceInformation(
					"Project profile ignore option conflict for {0}; the later user action was retained.",
					option);
			}
			states[option] = desired;
			if (desired)
				selected.Add(option);
			else
				selected.Remove(option);
		}

		return merged with
		{
			SelectedIgnoreOptions = selected.Order().ToArray(),
			IgnoreOptionStates = states
		};
	}

	private static IEnumerable<IgnoreOptionId> EnumerateIgnoreOptionIds(
		ProjectSelectionProfile left,
		ProjectSelectionProfile right) =>
		left.SelectedIgnoreOptions
			.Concat(right.SelectedIgnoreOptions)
			.Concat(left.IgnoreOptionStates?.Keys ?? [])
			.Concat(right.IgnoreOptionStates?.Keys ?? [])
			.Distinct();

	private static bool GetIgnoreOptionState(ProjectSelectionProfile profile, IgnoreOptionId option) =>
		profile.IgnoreOptionStates?.TryGetValue(option, out var value) == true
			? value
			: profile.SelectedIgnoreOptions.Contains(option);

	public static ProjectProfileMergeFields GetChangedFields(
		ProjectSelectionProfile? baseline,
		ProjectSelectionProfile candidate,
		ProjectProfileMergeFields allowedFields)
	{
		ArgumentNullException.ThrowIfNull(candidate);
		if (baseline is null)
			return allowedFields;

		var changed = ProjectProfileMergeFields.None;
		if (allowedFields.HasFlag(ProjectProfileMergeFields.RootFolders) &&
			!SetEquals(baseline.SelectedRootFolders, candidate.SelectedRootFolders, ProjectTreePathIdentity.CanonicalComparer))
			changed |= ProjectProfileMergeFields.RootFolders;
		if (allowedFields.HasFlag(ProjectProfileMergeFields.Extensions) &&
			!SetEquals(baseline.SelectedExtensions, candidate.SelectedExtensions, StringComparer.OrdinalIgnoreCase))
			changed |= ProjectProfileMergeFields.Extensions;
		if (allowedFields.HasFlag(ProjectProfileMergeFields.IgnoreOptions) &&
			!SetEquals(baseline.SelectedIgnoreOptions, candidate.SelectedIgnoreOptions))
			changed |= ProjectProfileMergeFields.IgnoreOptions;
		if (allowedFields.HasFlag(ProjectProfileMergeFields.RootFolderStates) &&
			!DictionaryEquals(baseline.RootFolderStates, candidate.RootFolderStates, ProjectTreePathIdentity.CanonicalComparer))
			changed |= ProjectProfileMergeFields.RootFolderStates;
		if (allowedFields.HasFlag(ProjectProfileMergeFields.ExtensionStates) &&
			!DictionaryEquals(baseline.ExtensionStates, candidate.ExtensionStates, StringComparer.OrdinalIgnoreCase))
			changed |= ProjectProfileMergeFields.ExtensionStates;
		if (allowedFields.HasFlag(ProjectProfileMergeFields.IgnoreOptionStates) &&
			!DictionaryEquals(baseline.IgnoreOptionStates, candidate.IgnoreOptionStates, EqualityComparer<IgnoreOptionId>.Default))
			changed |= ProjectProfileMergeFields.IgnoreOptionStates;
		if (allowedFields.HasFlag(ProjectProfileMergeFields.SelectedPaths) &&
			!NullableSetEquals(baseline.SelectedPaths, candidate.SelectedPaths, ProjectTreePathIdentity.CanonicalComparer))
			changed |= ProjectProfileMergeFields.SelectedPaths;
		return changed;
	}

	public static ProjectSelectionProfile Apply(
		ProjectSelectionProfile current,
		ProjectSelectionProfile candidate,
		ProjectProfileMergeFields fields)
	{
		ArgumentNullException.ThrowIfNull(current);
		ArgumentNullException.ThrowIfNull(candidate);
		return current with
		{
			SelectedRootFolders = fields.HasFlag(ProjectProfileMergeFields.RootFolders)
				? candidate.SelectedRootFolders.ToArray()
				: current.SelectedRootFolders,
			SelectedExtensions = fields.HasFlag(ProjectProfileMergeFields.Extensions)
				? candidate.SelectedExtensions.ToArray()
				: current.SelectedExtensions,
			SelectedIgnoreOptions = fields.HasFlag(ProjectProfileMergeFields.IgnoreOptions)
				? candidate.SelectedIgnoreOptions.ToArray()
				: current.SelectedIgnoreOptions,
			RootFolderStates = fields.HasFlag(ProjectProfileMergeFields.RootFolderStates)
				? Copy(candidate.RootFolderStates, ProjectTreePathIdentity.CanonicalComparer)
				: current.RootFolderStates,
			ExtensionStates = fields.HasFlag(ProjectProfileMergeFields.ExtensionStates)
				? Copy(candidate.ExtensionStates, StringComparer.OrdinalIgnoreCase)
				: current.ExtensionStates,
			IgnoreOptionStates = fields.HasFlag(ProjectProfileMergeFields.IgnoreOptionStates)
				? Copy(candidate.IgnoreOptionStates, EqualityComparer<IgnoreOptionId>.Default)
				: current.IgnoreOptionStates,
			SelectedPaths = fields.HasFlag(ProjectProfileMergeFields.SelectedPaths)
				? candidate.SelectedPaths?.ToArray()
				: current.SelectedPaths
		};
	}

	private static bool NullableSetEquals<T>(
		IReadOnlyCollection<T>? left,
		IReadOnlyCollection<T>? right,
		IEqualityComparer<T> comparer) =>
		left is null == (right is null) &&
		(left is null || SetEquals(left, right!, comparer));

	private static bool SetEquals<T>(
		IReadOnlyCollection<T> left,
		IReadOnlyCollection<T> right,
		IEqualityComparer<T>? comparer = null) =>
		new HashSet<T>(left, comparer).SetEquals(right);

	private static bool DictionaryEquals<TKey>(
		IReadOnlyDictionary<TKey, bool>? left,
		IReadOnlyDictionary<TKey, bool>? right,
		IEqualityComparer<TKey> comparer)
		where TKey : notnull
	{
		if (left is null || right is null)
			return left is null && right is null;
		if (left.Count != right.Count)
			return false;
		var normalized = new Dictionary<TKey, bool>(left, comparer);
		return right.All(pair => normalized.TryGetValue(pair.Key, out var value) && value == pair.Value);
	}

	private static IReadOnlyDictionary<TKey, bool>? Copy<TKey>(
		IReadOnlyDictionary<TKey, bool>? source,
		IEqualityComparer<TKey> comparer)
		where TKey : notnull =>
		source is null ? null : new Dictionary<TKey, bool>(source, comparer);
}
