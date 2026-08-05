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
				["Settings.Secrets.Status.Scanning"] = "Scanning selected text files…",
				["Settings.Secrets.Status.Failed"] = "The scan could not be completed.",
				["Settings.Secrets.Status.NoMatches"] = "The rules matched nothing.",
				["Settings.Secrets.Status.Applied"] = "Matches: {0}. Hidden: {1}.",
				["Settings.Secrets.Status.AllKept"] = "Matches: {0}. All values are kept as-is.",
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

		Assert.Equal(5, options.Count);
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
			options.Where(option => option.Id != IgnoreOptionId.HideSecrets),
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

		Assert.Equal(6, options.Count);
		Assert.Equal(IgnoreOptionId.HideSecrets, options[0].Id);
		Assert.Equal(IgnoreOptionId.UseGitIgnore, options[1].Id);
		Assert.Equal("Use GitIgnore", options[1].Label);
		Assert.True(options[1].DefaultChecked);
	}

	[Theory]
	[InlineData(SecretScanState.Disabled)]
	[InlineData(SecretScanState.Pending)]
	[InlineData(SecretScanState.Failed)]
	public void FormatHideSecretsLabel_WithoutCompletedResult_DoesNotInventZero(SecretScanState state)
	{
		var service = new IgnoreOptionsService(
			new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En));

		Assert.Equal("Hide secrets", service.FormatHideSecretsLabel(state, redactionCount: null));
	}

	[Fact]
	public void FormatHideSecretsLabel_WhileScanning_StaysShortAndDoesNotInventCount()
	{
		var service = new IgnoreOptionsService(
			new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En));

		Assert.Equal("Hide secrets", service.FormatHideSecretsLabel(
			SecretScanState.Scanning,
			redactionCount: null));
	}

	[Theory]
	[InlineData(0, "Hide secrets")]
	[InlineData(4, "Hide secrets (4)")]
	public void FormatHideSecretsLabel_AfterCompletion_ShowsMeasuredCount(int count, string expected)
	{
		var service = new IgnoreOptionsService(
			new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En));

		Assert.Equal(expected, service.FormatHideSecretsLabel(SecretScanState.Completed, count));
	}

	[Theory]
	[InlineData(SecretScanState.Disabled, null, null, "")]
	[InlineData(SecretScanState.Pending, null, null, "")]
	[InlineData(SecretScanState.Scanning, null, null, "Scanning selected text files…")]
	[InlineData(SecretScanState.Failed, null, null, "The scan could not be completed.")]
	[InlineData(SecretScanState.Completed, 0, 0, "The rules matched nothing.")]
	[InlineData(SecretScanState.Completed, 2, 2, "Matches: 2. Hidden: 2.")]
	[InlineData(SecretScanState.Completed, 2, 1, "Matches: 2. Hidden: 1.")]
	[InlineData(SecretScanState.Completed, 2, 0, "Matches: 2. All values are kept as-is.")]
	public void FormatHideSecretsStatus_DistinguishesNoMatchesFromUserDecisions(
		SecretScanState state,
		int? matchedCount,
		int? redactionCount,
		string expected)
	{
		var service = new IgnoreOptionsService(
			new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En));

		Assert.Equal(
			expected,
			service.FormatHideSecretsStatus(state, matchedCount, redactionCount));
	}
}
