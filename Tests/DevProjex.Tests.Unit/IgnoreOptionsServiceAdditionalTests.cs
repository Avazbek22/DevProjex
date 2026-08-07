using DevProjex.Application.Presentation;
using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class IgnoreOptionsServiceAdditionalTests
{
	private static readonly IReadOnlyDictionary<AppLanguage, IReadOnlyDictionary<string, string>> CatalogData =
		new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Settings.Ignore.HideSecrets"] = "Hide secrets",
				["Settings.Ignore.CompressCode"] = "Compress code",
				["Settings.Ignore.UseGitIgnore"] = "Use GitIgnore",
				["Settings.Ignore.HiddenFolders"] = "Ignore hidden folders",
				["Settings.Ignore.HiddenFiles"] = "Ignore hidden files",
				["Settings.Ignore.DotFolders"] = "Ignore dot folders",
				["Settings.Ignore.DotFiles"] = "Ignore dot files"
			}
		};

	[Fact]
	// Verifies the ignore options list contains all expected entries.
	public void GetOptions_ReturnsAllOptions()
	{
		var service = new IgnoreOptionsService(new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En));

		var options = service.GetOptions();

		Assert.Equal(5 + IgnoreOptionOrder.Count - 1, options.Count);
	}

	[Theory]
	// Verifies option IDs are present for all supported ignore settings.
	[InlineData(IgnoreOptionId.HiddenFolders)]
	[InlineData(IgnoreOptionId.HiddenFiles)]
	[InlineData(IgnoreOptionId.DotFolders)]
	[InlineData(IgnoreOptionId.DotFiles)]
	[InlineData(IgnoreOptionId.HideSecrets)]
	public void GetOptions_ContainsExpectedIds(IgnoreOptionId id)
	{
		var service = new IgnoreOptionsService(new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En));

		var options = service.GetOptions();

		Assert.Contains(options, option => option.Id == id);
	}

	[Theory]
	// Verifies option labels are resolved from localization resources.
	[InlineData(IgnoreOptionId.HiddenFolders, "Ignore hidden folders")]
	[InlineData(IgnoreOptionId.HiddenFiles, "Ignore hidden files")]
	[InlineData(IgnoreOptionId.DotFolders, "Ignore dot folders")]
	[InlineData(IgnoreOptionId.DotFiles, "Ignore dot files")]
	[InlineData(IgnoreOptionId.HideSecrets, "Hide secrets")]
	public void GetOptions_ReturnsLocalizedLabels(IgnoreOptionId id, string expectedLabel)
	{
		var service = new IgnoreOptionsService(new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En));

		var options = service.GetOptions();

		Assert.Contains(options, option => option.Id == id && option.Label == expectedLabel);
	}

	[Fact]
	// Hide Secrets is intentionally opt-in; path-only exclusions keep their existing defaults.
	public void GetOptions_UsesSafeDefaults()
	{
		var service = new IgnoreOptionsService(new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En));

		var options = service.GetOptions();

		Assert.False(options.Single(option => option.Id == IgnoreOptionId.HideSecrets).DefaultChecked);
		Assert.All(
			options.Where(option => !ProjectPresentationCatalog.ContentTransformationOptionIds.Contains(option.Id)),
			option => Assert.True(option.DefaultChecked));
	}

	[Fact]
	// Verifies ignore option IDs are unique.
	public void GetOptions_IdsAreUnique()
	{
		var service = new IgnoreOptionsService(new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En));

		var options = service.GetOptions();

		Assert.Equal(options.Count, options.Select(option => option.Id).Distinct().Count());
	}

	[Fact]
	public void GetOptions_WithGitIgnore_IncludesUseGitIgnoreAsFirstOption()
	{
		var service = new IgnoreOptionsService(new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En));

		var options = service.GetOptions(includeGitIgnore: true);

		Assert.Equal(6 + IgnoreOptionOrder.Count - 1, options.Count);
		Assert.Equal(IgnoreOptionOrder.ContentTransformations, options.Skip(0).Take(IgnoreOptionOrder.Count).Select(static option => option.Id));
		Assert.Equal(IgnoreOptionId.UseGitIgnore, options[IgnoreOptionOrder.Count].Id);
		Assert.Equal("Use GitIgnore", options[IgnoreOptionOrder.Count].Label);
		Assert.True(options[IgnoreOptionOrder.Count].DefaultChecked);
	}

	[Theory]
	[InlineData(SecretScanState.Disabled)]
	[InlineData(SecretScanState.Pending)]
	[InlineData(SecretScanState.Failed)]
	public void FormatHideSecretsLabel_WithoutCompletedResult_DoesNotInventZero(SecretScanState state)
	{
		var service = new IgnoreOptionsService(
			new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En));

		Assert.Equal(
			"Hide secrets",
			service.FormatHideSecretsLabel(state, matchedCount: null, redactionCount: null));
	}

	[Fact]
	public void FormatHideSecretsLabel_WhileScanning_StaysShortAndDoesNotInventCount()
	{
		var service = new IgnoreOptionsService(
			new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En));

		Assert.Equal("Hide secrets", service.FormatHideSecretsLabel(
			SecretScanState.Scanning,
			matchedCount: null,
			redactionCount: null));
	}

	[Theory]
	[InlineData(0, 0, "Hide secrets (0/0)")]
	[InlineData(4, 4, "Hide secrets (4/4)")]
	[InlineData(4, 1, "Hide secrets (4/1)")]
	[InlineData(4, 0, "Hide secrets (4/0)")]
	public void FormatHideSecretsLabel_AfterCompletion_ShowsMatchedAndHiddenCounts(
		int matchedCount,
		int redactionCount,
		string expected)
	{
		var service = new IgnoreOptionsService(
			new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En));

		Assert.Equal(
			expected,
			service.FormatHideSecretsLabel(
				SecretScanState.Completed,
				matchedCount,
				redactionCount));
	}
}
