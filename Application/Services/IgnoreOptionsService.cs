namespace DevProjex.Application.Services;

public sealed class IgnoreOptionsService(LocalizationService localization)
{
	public IReadOnlyList<IgnoreOptionDescriptor> GetOptions(IgnoreOptionsAvailability availability)
	{
		var options = new List<IgnoreOptionDescriptor>();
		if (availability.IncludeSmartIgnore)
		{
			options.Add(new IgnoreOptionDescriptor(
				IgnoreOptionId.SmartIgnore,
				localization["Settings.Ignore.SmartIgnore"],
				true));
		}

		if (availability.IncludeGitIgnore)
		{
			options.Add(new IgnoreOptionDescriptor(
				IgnoreOptionId.UseGitIgnore,
				localization["Settings.Ignore.UseGitIgnore"],
				true));
		}

		if (availability.IncludeEmptyFolders)
		{
			options.Add(new IgnoreOptionDescriptor(
				IgnoreOptionId.EmptyFolders,
				FormatLabelWithCount(localization["Settings.Ignore.EmptyFolders"], availability.EmptyFoldersCount, availability.ShowAdvancedCounts),
				true));
		}

		if (availability.IncludeEmptyFiles)
		{
			options.Add(new IgnoreOptionDescriptor(
				IgnoreOptionId.EmptyFiles,
				FormatLabelWithCount(localization["Settings.Ignore.EmptyFiles"], availability.EmptyFilesCount, availability.ShowAdvancedCounts),
				true));
		}

		if (availability.IncludeHiddenFolders)
		{
			options.Add(new IgnoreOptionDescriptor(
				IgnoreOptionId.HiddenFolders,
				FormatLabelWithCount(localization["Settings.Ignore.HiddenFolders"], availability.HiddenFoldersCount, availability.ShowAdvancedCounts),
				true));
		}

		if (availability.IncludeHiddenFiles)
		{
			options.Add(new IgnoreOptionDescriptor(
				IgnoreOptionId.HiddenFiles,
				FormatLabelWithCount(localization["Settings.Ignore.HiddenFiles"], availability.HiddenFilesCount, availability.ShowAdvancedCounts),
				true));
		}

		if (availability.IncludeDotFolders)
		{
			options.Add(new IgnoreOptionDescriptor(
				IgnoreOptionId.DotFolders,
				FormatLabelWithCount(localization["Settings.Ignore.DotFolders"], availability.DotFoldersCount, availability.ShowAdvancedCounts),
				true));
		}

		if (availability.IncludeDotFiles)
		{
			options.Add(new IgnoreOptionDescriptor(
				IgnoreOptionId.DotFiles,
				FormatLabelWithCount(localization["Settings.Ignore.DotFiles"], availability.DotFilesCount, availability.ShowAdvancedCounts),
				true));
		}

		if (availability.IncludeExtensionlessFiles)
		{
			options.Add(new IgnoreOptionDescriptor(
				IgnoreOptionId.ExtensionlessFiles,
				FormatLabelWithCount(localization["Settings.Ignore.ExtensionlessFiles"], availability.ExtensionlessFilesCount, availability.ShowAdvancedCounts),
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
