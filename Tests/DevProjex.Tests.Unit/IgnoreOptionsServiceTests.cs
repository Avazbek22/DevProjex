using DevProjex.Application.Presentation;
using DevProjex.Application.Secrets;

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
				["Settings.Ignore.HideSecrets"] = "Hide secrets",
				["Settings.Ignore.HidePrivateData"] = "Hide private data",
				["Settings.Ignore.CompressCode"] = "Compress code",
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

		Assert.Equal(5 + IgnoreOptionOrder.Count - 1, options.Count);
		Assert.False(options.Single(option => option.Id == IgnoreOptionId.HideSecrets).DefaultChecked);
		Assert.False(options.Single(option => option.Id == IgnoreOptionId.HidePrivateData).DefaultChecked);
		var hideSecretsIndex = options
			.Select(static option => option.Id)
			.ToList()
			.IndexOf(IgnoreOptionId.HideSecrets);
		Assert.Equal(
			IgnoreOptionId.HidePrivateData,
			options[hideSecretsIndex + 1].Id);
		Assert.All(
			options.Where(option => !ProjectPresentationCatalog.ContentTransformationOptionIds.Contains(option.Id)),
			option => Assert.True(option.DefaultChecked));
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
				["Settings.Ignore.HideSecrets"] = "Hide secrets",
				["Settings.Ignore.HidePrivateData"] = "Hide private data",
				["Settings.Ignore.CompressCode"] = "Compress code",
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

		Assert.Equal(IgnoreOptionOrder.ContentTransformations, options.Skip(0).Take(IgnoreOptionOrder.Count).Select(static option => option.Id));
		Assert.Equal(IgnoreOptionId.HiddenFolders, options[IgnoreOptionOrder.Count].Id);
		Assert.Equal(IgnoreOptionId.HiddenFiles, options[IgnoreOptionOrder.Count + 1].Id);
		Assert.Equal(IgnoreOptionId.DotFolders, options[IgnoreOptionOrder.Count + 2].Id);
		Assert.Equal(IgnoreOptionId.DotFiles, options[IgnoreOptionOrder.Count + 3].Id);
	}

	// Verifies localized labels are populated for all options.
	[Fact]
	public void GetOptions_UsesLocalizationForEveryLabel()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Settings.Ignore.HideSecrets"] = "Hide secrets",
				["Settings.Ignore.HidePrivateData"] = "Hide private data",
				["Settings.Ignore.CompressCode"] = "Compress code",
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
				["Settings.Ignore.HideSecrets"] = "Hide secrets",
				["Settings.Ignore.HidePrivateData"] = "Hide private data",
				["Settings.Ignore.CompressCode"] = "Compress code",
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

		Assert.Equal(6 + IgnoreOptionOrder.Count - 1, options.Count);
		Assert.Equal(IgnoreOptionOrder.ContentTransformations, options.Skip(0).Take(IgnoreOptionOrder.Count).Select(static option => option.Id));
		Assert.Equal(IgnoreOptionId.UseGitIgnore, options[IgnoreOptionOrder.Count].Id);
		Assert.Equal("Use GitIgnore", options[IgnoreOptionOrder.Count].Label);
		Assert.True(options[IgnoreOptionOrder.Count].DefaultChecked);
	}

	[Fact]
	public void FormatContentRedactionLabel_KeepsSecretAndPrivacyCountersSeparate()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Settings.Ignore.HideSecrets"] = "Hide secrets",
				["Settings.Ignore.HidePrivateData"] = "Hide private data"
			}
		});
		var service = new IgnoreOptionsService(new LocalizationService(catalog, AppLanguage.En));

		Assert.Equal(
			"Hide secrets (3/2)",
			service.FormatContentRedactionLabel(IgnoreOptionId.HideSecrets, SecretScanState.Completed, 3, 2));
		Assert.Equal(
			"Hide private data (7/5)",
			service.FormatContentRedactionLabel(IgnoreOptionId.HidePrivateData, SecretScanState.Completed, 7, 5));
		Assert.Equal(
			"Hide private data",
			service.FormatContentRedactionLabel(IgnoreOptionId.HidePrivateData, SecretScanState.Completed, 0, 0));
	}
}
