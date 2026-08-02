namespace DevProjex.Application.Services;

public sealed class IgnoreOptionsService(LocalizationService localization)
{
	public IReadOnlyList<IgnoreOptionDescriptor> GetOptions(IgnoreOptionsAvailability availability)
	{
		var options = new List<IgnoreOptionDescriptor>();
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

		foreach (var descriptor in ProjectPresentationCatalog.Exclusions)
		{
			var (included, count) = descriptor.Id switch
			{
				ProjectExclusion.SmartIgnore => (availability.IncludeSmartIgnore, 0),
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

		return options;
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
