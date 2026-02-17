namespace DevProjex.Application.Services;

public sealed class IgnoreOptionsService
{
	private readonly LocalizationService _localization;

	public IgnoreOptionsService(LocalizationService localization)
	{
		_localization = localization;
	}

	public IReadOnlyList<IgnoreOptionDescriptor> GetOptions(IgnoreOptionsAvailability availability)
	{
		var options = new List<IgnoreOptionDescriptor>();
		if (availability.IncludeSmartIgnore)
		{
			options.Add(new IgnoreOptionDescriptor(
				IgnoreOptionId.SmartIgnore,
				_localization["Settings.Ignore.SmartIgnore"],
				true));
		}

		if (availability.IncludeGitIgnore)
		{
			options.Add(new IgnoreOptionDescriptor(
				IgnoreOptionId.UseGitIgnore,
				_localization["Settings.Ignore.UseGitIgnore"],
				true));
		}

		options.AddRange(new[]
		{
			new IgnoreOptionDescriptor(IgnoreOptionId.HiddenFolders, _localization["Settings.Ignore.HiddenFolders"], true),
			new IgnoreOptionDescriptor(IgnoreOptionId.HiddenFiles, _localization["Settings.Ignore.HiddenFiles"], true),
			new IgnoreOptionDescriptor(IgnoreOptionId.DotFolders, _localization["Settings.Ignore.DotFolders"], true),
			new IgnoreOptionDescriptor(IgnoreOptionId.DotFiles, _localization["Settings.Ignore.DotFiles"], true)
		});

		if (availability.IncludeExtensionlessFiles)
		{
			options.Add(new IgnoreOptionDescriptor(
				IgnoreOptionId.ExtensionlessFiles,
				_localization["Settings.Ignore.ExtensionlessFiles"],
				false));
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
}
