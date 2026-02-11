using System.Collections.Generic;
using DevProjex.Kernel.Models;

namespace DevProjex.Application.Services;

public sealed class IgnoreOptionsService
{
	private readonly LocalizationService _localization;

	public IgnoreOptionsService(LocalizationService localization)
	{
		_localization = localization;
	}

	public IReadOnlyList<IgnoreOptionDescriptor> GetOptions(bool includeGitIgnore = false)
	{
		var options = new List<IgnoreOptionDescriptor>();
		if (includeGitIgnore)
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

		return options;
	}
}
