using System.Collections.Generic;
using DevProjex.Application.Services;
using DevProjex.Kernel.Models;
using DevProjex.Tests.Unit.Helpers;
using Xunit;

namespace DevProjex.Tests.Unit;

public sealed class IgnoreOptionsServiceTests
{
	// Verifies ignore options are localized and default-selected flags are set.
	[Fact]
	public void GetOptions_ReturnsLocalizedOptions()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Settings.Ignore.UseGitIgnore"] = "Use GitIgnore",
				["Settings.Ignore.HiddenFolders"] = "HiddenFolders",
				["Settings.Ignore.HiddenFiles"] = "HiddenFiles",
				["Settings.Ignore.DotFolders"] = "DotFolders",
				["Settings.Ignore.DotFiles"] = "DotFiles"
			}
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var service = new IgnoreOptionsService(localization);

		var options = service.GetOptions();

		Assert.Equal(4, options.Count);
		Assert.All(options, option => Assert.True(option.DefaultChecked));
		Assert.Contains(options, option => option.Id == IgnoreOptionId.HiddenFolders && option.Label == "HiddenFolders");
		Assert.Contains(options, option => option.Id == IgnoreOptionId.DotFiles && option.Label == "DotFiles");
	}

	// Verifies options preserve the expected ordering.
	[Fact]
	public void GetOptions_ReturnsExpectedOrder()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Settings.Ignore.UseGitIgnore"] = "Use GitIgnore",
				["Settings.Ignore.HiddenFolders"] = "HiddenFolders",
				["Settings.Ignore.HiddenFiles"] = "HiddenFiles",
				["Settings.Ignore.DotFolders"] = "DotFolders",
				["Settings.Ignore.DotFiles"] = "DotFiles"
			}
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var service = new IgnoreOptionsService(localization);

		var options = service.GetOptions();

		Assert.Equal(IgnoreOptionId.HiddenFolders, options[0].Id);
		Assert.Equal(IgnoreOptionId.HiddenFiles, options[1].Id);
		Assert.Equal(IgnoreOptionId.DotFolders, options[2].Id);
		Assert.Equal(IgnoreOptionId.DotFiles, options[3].Id);
	}

	// Verifies localized labels are populated for all options.
	[Fact]
	public void GetOptions_UsesLocalizationForEveryLabel()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Settings.Ignore.UseGitIgnore"] = "Use GitIgnore",
				["Settings.Ignore.HiddenFolders"] = "HiddenFolders",
				["Settings.Ignore.HiddenFiles"] = "HiddenFiles",
				["Settings.Ignore.DotFolders"] = "DotFolders",
				["Settings.Ignore.DotFiles"] = "DotFiles"
			}
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var service = new IgnoreOptionsService(localization);

		var options = service.GetOptions();

		Assert.All(options, option => Assert.False(string.IsNullOrWhiteSpace(option.Label)));
	}

	[Fact]
	public void GetOptions_WhenGitIgnoreIncluded_AddsOptionAsFirst()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Settings.Ignore.UseGitIgnore"] = "Use GitIgnore",
				["Settings.Ignore.HiddenFolders"] = "HiddenFolders",
				["Settings.Ignore.HiddenFiles"] = "HiddenFiles",
				["Settings.Ignore.DotFolders"] = "DotFolders",
				["Settings.Ignore.DotFiles"] = "DotFiles"
			}
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var service = new IgnoreOptionsService(localization);

		var options = service.GetOptions(includeGitIgnore: true);

		Assert.Equal(5, options.Count);
		Assert.Equal(IgnoreOptionId.UseGitIgnore, options[0].Id);
		Assert.Equal("Use GitIgnore", options[0].Label);
		Assert.True(options[0].DefaultChecked);
	}
}
