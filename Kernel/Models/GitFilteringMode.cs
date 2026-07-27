namespace DevProjex.Kernel.Models;

public enum GitFilteringMode
{
	None,
	RespectGitIgnore,
	TrackedFilesOnly
}

public static class GitFilteringModeResolver
{
	public static GitFilteringMode Resolve(IEnumerable<IgnoreOptionId> selectedOptions)
	{
		ArgumentNullException.ThrowIfNull(selectedOptions);

		var useGitIgnore = false;
		foreach (var optionId in selectedOptions)
		{
			if (optionId == IgnoreOptionId.TrackedGitFilesOnly)
				return GitFilteringMode.TrackedFilesOnly;

			if (optionId == IgnoreOptionId.UseGitIgnore)
				useGitIgnore = true;
		}

		return useGitIgnore
			? GitFilteringMode.RespectGitIgnore
			: GitFilteringMode.None;
	}

	public static GitFilteringMode Resolve(
		IReadOnlyDictionary<IgnoreOptionId, bool> optionStates)
	{
		ArgumentNullException.ThrowIfNull(optionStates);

		if (optionStates.TryGetValue(IgnoreOptionId.TrackedGitFilesOnly, out var trackedOnly) &&
		    trackedOnly)
		{
			return GitFilteringMode.TrackedFilesOnly;
		}

		return optionStates.TryGetValue(IgnoreOptionId.UseGitIgnore, out var useGitIgnore) &&
		       useGitIgnore
			? GitFilteringMode.RespectGitIgnore
			: GitFilteringMode.None;
	}

	public static bool IsGitFilteringOption(IgnoreOptionId optionId) =>
		optionId is IgnoreOptionId.UseGitIgnore or IgnoreOptionId.TrackedGitFilesOnly;

	public static void Normalize(
		IDictionary<IgnoreOptionId, bool> optionStates,
		GitFilteringMode preferredMode = GitFilteringMode.None)
	{
		ArgumentNullException.ThrowIfNull(optionStates);

		var useGitIgnore = optionStates.TryGetValue(IgnoreOptionId.UseGitIgnore, out var useGitIgnoreState) &&
		                   useGitIgnoreState;
		var trackedOnly = optionStates.TryGetValue(IgnoreOptionId.TrackedGitFilesOnly, out var trackedOnlyState) &&
		                  trackedOnlyState;
		if (!useGitIgnore || !trackedOnly)
			return;

		// Runtime toggles provide an explicit preference. Corrupt or legacy states without
		// one use the stricter mode so an impossible pair cannot expose untracked files.
		if (preferredMode == GitFilteringMode.RespectGitIgnore)
			optionStates[IgnoreOptionId.TrackedGitFilesOnly] = false;
		else
			optionStates[IgnoreOptionId.UseGitIgnore] = false;
	}

	public static void Normalize(
		ISet<IgnoreOptionId> selectedOptions,
		GitFilteringMode preferredMode = GitFilteringMode.None)
	{
		ArgumentNullException.ThrowIfNull(selectedOptions);

		if (!selectedOptions.Contains(IgnoreOptionId.UseGitIgnore) ||
		    !selectedOptions.Contains(IgnoreOptionId.TrackedGitFilesOnly))
		{
			return;
		}

		if (preferredMode == GitFilteringMode.RespectGitIgnore)
			selectedOptions.Remove(IgnoreOptionId.TrackedGitFilesOnly);
		else
			selectedOptions.Remove(IgnoreOptionId.UseGitIgnore);
	}
}
