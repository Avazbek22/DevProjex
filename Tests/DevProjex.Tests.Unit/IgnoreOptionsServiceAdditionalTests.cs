namespace DevProjex.Tests.Unit;

public sealed class IgnoreOptionsServiceAdditionalTests
{
	private static readonly IReadOnlyDictionary<AppLanguage, IReadOnlyDictionary<string, string>> CatalogData =
		new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
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

		Assert.Equal(4, options.Count);
	}

	[Theory]
	// Verifies option IDs are present for all supported ignore settings.
	[InlineData(IgnoreOptionId.HiddenFolders)]
	[InlineData(IgnoreOptionId.HiddenFiles)]
	[InlineData(IgnoreOptionId.DotFolders)]
	[InlineData(IgnoreOptionId.DotFiles)]
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
	public void GetOptions_ReturnsLocalizedLabels(IgnoreOptionId id, string expectedLabel)
	{
		var service = new IgnoreOptionsService(new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En));

		var options = service.GetOptions();

		Assert.Contains(options, option => option.Id == id && option.Label == expectedLabel);
	}

	[Fact]
	// Verifies all ignore options default to checked.
	public void GetOptions_DefaultChecked_IsTrue()
	{
		var service = new IgnoreOptionsService(new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En));

		var options = service.GetOptions();

		Assert.All(options, option => Assert.True(option.DefaultChecked));
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

		Assert.Equal(5, options.Count);
		Assert.Equal(IgnoreOptionId.UseGitIgnore, options[0].Id);
		Assert.Equal("Use GitIgnore", options[0].Label);
		Assert.True(options[0].DefaultChecked);
	}
}
