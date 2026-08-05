namespace DevProjex.Application.Services;

public sealed class IgnoreOptionsService(LocalizationService localization)
{
	public IReadOnlyList<IgnoreOptionDescriptor> GetOptions(IgnoreOptionsAvailability availability)
	{
		var options = new List<IgnoreOptionDescriptor>();
		// Content-level protection belongs with the primary controllers, before Git modes.
		// Unlike path filters, Hide Secrets is always offered because availability cannot
		// be known without reading the selected content.
		AppendPrimaryExclusionOptions(options, availability);

		foreach (var descriptor in ProjectPresentationCatalog.GitFiltering)
		{
			if (descriptor.LegacyOptionId is not { } optionId)
				continue;
			var (included, isSelected) = descriptor.Id switch
			{
				GitFilteringMode.RespectGitIgnore =>
					(availability.IncludeGitIgnore, true),
				GitFilteringMode.TrackedFilesOnly =>
					(availability.IncludeTrackedGitFilesOnly, false),
				_ => (false, false)
			};
			if (included)
			{
				options.Add(new IgnoreOptionDescriptor(
					optionId,
					localization[descriptor.LabelKey],
					isSelected));
			}
		}

		AppendPathExclusionOptions(options, availability);
		return options;
	}

	private void AppendPrimaryExclusionOptions(
		List<IgnoreOptionDescriptor> options,
		IgnoreOptionsAvailability availability)
	{
		foreach (var descriptor in ProjectPresentationCatalog.Exclusions)
		{
			if (descriptor.Id is not (ProjectExclusion.SmartIgnore or ProjectExclusion.HideSecrets))
				continue;

			var included = descriptor.Id == ProjectExclusion.HideSecrets ||
			               availability.IncludeSmartIgnore;
			if (!included)
				continue;

			var label = localization[descriptor.LabelKey];
			if (descriptor.Id == ProjectExclusion.HideSecrets &&
			    availability.SecretRedactionsCount is { } redactionCount)
			{
				label = $"{label} ({redactionCount})";
			}

			options.Add(new IgnoreOptionDescriptor(
				descriptor.LegacyOptionId,
				label,
				descriptor.Id == ProjectExclusion.SmartIgnore));
		}
	}

	private void AppendPathExclusionOptions(
		List<IgnoreOptionDescriptor> options,
		IgnoreOptionsAvailability availability)
	{
		foreach (var descriptor in ProjectPresentationCatalog.Exclusions)
		{
			if (descriptor.Id is ProjectExclusion.SmartIgnore or ProjectExclusion.HideSecrets)
				continue;

			var (included, count) = descriptor.Id switch
			{
				ProjectExclusion.HiddenFolders =>
					(availability.IncludeHiddenFolders, availability.HiddenFoldersCount),
				ProjectExclusion.HiddenFiles =>
					(availability.IncludeHiddenFiles, availability.HiddenFilesCount),
				ProjectExclusion.DotFolders =>
					(availability.IncludeDotFolders, availability.DotFoldersCount),
				ProjectExclusion.DotFiles =>
					(availability.IncludeDotFiles, availability.DotFilesCount),
				ProjectExclusion.EmptyFolders =>
					(availability.IncludeEmptyFolders, availability.EmptyFoldersCount),
				ProjectExclusion.EmptyFiles =>
					(availability.IncludeEmptyFiles, availability.EmptyFilesCount),
				ProjectExclusion.ExtensionlessFiles =>
					(availability.IncludeExtensionlessFiles, availability.ExtensionlessFilesCount),
				_ => throw new ArgumentOutOfRangeException()
			};
			if (!included)
				continue;
			options.Add(new IgnoreOptionDescriptor(
				descriptor.LegacyOptionId,
				FormatLabelWithCount(
					localization[descriptor.LabelKey],
					count,
					availability.ShowAdvancedCounts),
				true));
		}
	}

	public IReadOnlyList<IgnoreOptionDescriptor> GetOptions()
	{
		return GetOptions(new IgnoreOptionsAvailability(
			IncludeGitIgnore: false,
			IncludeSmartIgnore: false));
	}

	public IReadOnlyList<IgnoreOptionDescriptor> GetOptions(bool includeGitIgnore)
	{
		return GetOptions(new IgnoreOptionsAvailability(
			IncludeGitIgnore: includeGitIgnore,
			IncludeSmartIgnore: false));
	}

	private static string FormatLabelWithCount(string baseLabel, int count, bool showAdvancedCounts)
	{
		return showAdvancedCounts && count > 0
			? $"{baseLabel} ({count})"
			: baseLabel;
	}
}
