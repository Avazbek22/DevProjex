namespace DevProjex.Application.Context;

public static class ProjectSelectionAdapter
{
	public static IReadOnlyDictionary<string, bool>? GetLocalProfileExtensionStates(
		ProjectSelectionSpec selection)
	{
		ArgumentNullException.ThrowIfNull(selection);
		var profileState = selection.LocalProfileState;
		var extensionStates = profileState?.Profile.ExtensionStates;
		if (profileState is null || profileState.ExtensionsOverridden || extensionStates is null)
			return null;

		return new Dictionary<string, bool>(extensionStates, StringComparer.OrdinalIgnoreCase);
	}

	public static IReadOnlyCollection<IgnoreOptionId> ToIgnoreOptions(ProjectSelectionSpec selection)
	{
		ArgumentNullException.ThrowIfNull(selection);
		if (selection.GitMode is null || selection.Exclusions is null)
			throw new ArgumentException("The project selection must be fully resolved.", nameof(selection));

		var options = new HashSet<IgnoreOptionId>();
		switch (selection.GitMode.Value)
		{
			case GitFilteringMode.RespectGitIgnore:
				options.Add(IgnoreOptionId.UseGitIgnore);
				break;
			case GitFilteringMode.TrackedFilesOnly:
				options.Add(IgnoreOptionId.TrackedGitFilesOnly);
				break;
		}

		foreach (var exclusion in selection.Exclusions)
			options.Add(ToIgnoreOption(exclusion));
		if (selection.HideSecrets is true)
			options.Add(IgnoreOptionId.HideSecrets);
		if (selection.HidePrivateData is true)
			options.Add(IgnoreOptionId.HidePrivateData);
		if (selection.CompressCode is true)
			options.Add(IgnoreOptionId.CompressCode);
		if (selection.StripComments is true)
			options.Add(IgnoreOptionId.StripComments);
		if (selection.StripBlankLines is true)
			options.Add(IgnoreOptionId.StripBlankLines);

		return options.OrderBy(static option => (int)option).ToArray();
	}

	public static IReadOnlyCollection<ProjectExclusion> ToExclusions(
		IEnumerable<IgnoreOptionId> options)
	{
		ArgumentNullException.ThrowIfNull(options);

		var exclusions = new HashSet<ProjectExclusion>();
		foreach (var option in options)
		{
			// Content transformations are carried as dedicated flags, never as exclusions.
			if (ProjectPresentationCatalog.ContentTransformationOptionIds.Contains(option))
				continue;
			if (TryToExclusion(option, out var exclusion))
				exclusions.Add(exclusion);
		}

		return exclusions.OrderBy(static exclusion => (int)exclusion).ToArray();
	}

	public static ProjectSelectionSpec FromLegacyProfile(
		ProjectSelectionProfile profile,
		ProjectProfileReference source)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(source);

		return new ProjectSelectionSpec(
			Roots: ResolveNullableSelection(profile.SelectedRootFolders, profile.RootFolderStates),
			Extensions: ResolveNullableSelection(profile.SelectedExtensions, profile.ExtensionStates),
			SelectedPaths: profile.SelectedPaths?.ToArray() ?? [],
			GitMode: GitFilteringModeResolver.Resolve(profile.SelectedIgnoreOptions),
			Exclusions: ToExclusions(profile.SelectedIgnoreOptions),
			HideSecrets: profile.SelectedIgnoreOptions.Contains(IgnoreOptionId.HideSecrets),
			HidePrivateData: profile.SelectedIgnoreOptions.Contains(IgnoreOptionId.HidePrivateData),
			CompressCode: profile.SelectedIgnoreOptions.Contains(IgnoreOptionId.CompressCode),
			StripComments: profile.SelectedIgnoreOptions.Contains(IgnoreOptionId.StripComments),
			StripBlankLines: profile.SelectedIgnoreOptions.Contains(IgnoreOptionId.StripBlankLines),
			ProfileSource: source);
	}

	private static IReadOnlyCollection<string>? ResolveNullableSelection(
		IReadOnlyCollection<string> selected,
		IReadOnlyDictionary<string, bool>? states)
	{
		if (states is null || states.Count == 0)
			return selected.Count == 0 ? null : selected.ToArray();

		return states
			.Where(static pair => pair.Value)
			.Select(static pair => pair.Key)
			.ToArray();
	}

	private static IgnoreOptionId ToIgnoreOption(ProjectExclusion exclusion) =>
		exclusion switch
		{
			ProjectExclusion.SmartIgnore => IgnoreOptionId.SmartIgnore,
			ProjectExclusion.HiddenFolders => IgnoreOptionId.HiddenFolders,
			ProjectExclusion.HiddenFiles => IgnoreOptionId.HiddenFiles,
			ProjectExclusion.DotFolders => IgnoreOptionId.DotFolders,
			ProjectExclusion.DotFiles => IgnoreOptionId.DotFiles,
			ProjectExclusion.EmptyFolders => IgnoreOptionId.EmptyFolders,
			ProjectExclusion.EmptyFiles => IgnoreOptionId.EmptyFiles,
			ProjectExclusion.ExtensionlessFiles => IgnoreOptionId.ExtensionlessFiles,
			ProjectExclusion.HideSecrets => IgnoreOptionId.HideSecrets,
			_ => throw new ArgumentOutOfRangeException(nameof(exclusion), exclusion, null)
		};

	private static bool TryToExclusion(IgnoreOptionId option, out ProjectExclusion exclusion)
	{
		switch (option)
		{
			case IgnoreOptionId.SmartIgnore:
				exclusion = ProjectExclusion.SmartIgnore;
				return true;
			case IgnoreOptionId.HiddenFolders:
				exclusion = ProjectExclusion.HiddenFolders;
				return true;
			case IgnoreOptionId.HiddenFiles:
				exclusion = ProjectExclusion.HiddenFiles;
				return true;
			case IgnoreOptionId.DotFolders:
				exclusion = ProjectExclusion.DotFolders;
				return true;
			case IgnoreOptionId.DotFiles:
				exclusion = ProjectExclusion.DotFiles;
				return true;
			case IgnoreOptionId.EmptyFolders:
				exclusion = ProjectExclusion.EmptyFolders;
				return true;
			case IgnoreOptionId.EmptyFiles:
				exclusion = ProjectExclusion.EmptyFiles;
				return true;
			case IgnoreOptionId.ExtensionlessFiles:
				exclusion = ProjectExclusion.ExtensionlessFiles;
				return true;
			case IgnoreOptionId.HideSecrets:
				exclusion = ProjectExclusion.HideSecrets;
				return true;
			default:
				exclusion = default;
				return false;
		}
	}
}
