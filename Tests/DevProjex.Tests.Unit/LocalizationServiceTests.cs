namespace DevProjex.Tests.Unit;

public sealed class LocalizationServiceTests
{
	// Verifies the indexer returns a localized string for a known key.
	[Fact]
	public void Indexer_ReturnsLocalizedValue()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string> { ["Greeting"] = "Hello" }
		});
		var service = new LocalizationService(catalog, AppLanguage.En);

		Assert.Equal("Hello", service["Greeting"]);
	}

	// Verifies missing localization keys return a readable placeholder.
	[Fact]
	public void Indexer_ReturnsFallbackForMissingKey()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>()
		});
		var service = new LocalizationService(catalog, AppLanguage.En);

		Assert.Equal("[[Missing]]", service["Missing"]);
	}

	// Verifies unknown languages fall back to English resources.
	[Fact]
	public void Get_ReturnsEnglishFallbackWhenLanguageMissing()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string> { ["Key"] = "Value" }
		});
		var service = new LocalizationService(catalog, AppLanguage.Fr);

		Assert.Equal("Value", service["Key"]);
	}

	// Verifies formatted strings use the localized template.
	[Fact]
	public void Format_UsesStringFormat()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string> { ["Format"] = "Hello {0}" }
		});
		var service = new LocalizationService(catalog, AppLanguage.En);

		Assert.Equal("Hello World", service.Format("Format", "World"));
	}

	// Verifies changing language triggers the LanguageChanged event.
	[Fact]
	public void SetLanguage_RaisesEventWhenChanged()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>(),
			[AppLanguage.Ru] = new Dictionary<string, string>()
		});
		var service = new LocalizationService(catalog, AppLanguage.En);
		int called = 0;
		service.LanguageChanged += (_, _) => called++;

		service.SetLanguage(AppLanguage.Ru);

		Assert.Equal(1, called);
		Assert.Equal(AppLanguage.Ru, service.CurrentLanguage);
	}

	// Verifies setting the same language does not raise change events.
	[Fact]
	public void SetLanguage_DoesNothingWhenSame()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>()
		});
		var service = new LocalizationService(catalog, AppLanguage.En);
		int called = 0;
		service.LanguageChanged += (_, _) => called++;

		service.SetLanguage(AppLanguage.En);

		Assert.Equal(0, called);
	}
}
