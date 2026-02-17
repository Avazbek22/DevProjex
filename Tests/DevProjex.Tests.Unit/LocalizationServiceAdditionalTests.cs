using DevProjex.Application.Services;
using DevProjex.Tests.Unit.Helpers;

namespace DevProjex.Tests.Unit;

public sealed class LocalizationServiceAdditionalTests
{
	private static readonly IReadOnlyDictionary<AppLanguage, IReadOnlyDictionary<string, string>> CatalogData =
		new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Key.Hello"] = "Hello",
				["Key.World"] = "World",
				["Key.Format0"] = "Value: {0}",
				["Key.Format2"] = "{0} + {1} = {2}",
				["Tree.AccessDenied"] = "Access denied",
				["Tree.AccessDeniedRoot"] = "Access denied root"
			},
			[AppLanguage.Ru] = new Dictionary<string, string>
			{
				["Key.Hello"] = "Привет",
				["Key.World"] = "Мир",
				["Key.Format0"] = "Значение: {0}",
				["Key.Format2"] = "{0} + {1} = {2}",
				["Tree.AccessDenied"] = "Доступ запрещен",
				["Tree.AccessDeniedRoot"] = "Доступ запрещен (корень)"
			}
		};

	[Theory]
	// Verifies the indexer returns localized values for known keys in English.
	[InlineData("Key.Hello", "Hello")]
	[InlineData("Key.World", "World")]
	[InlineData("Tree.AccessDenied", "Access denied")]
	[InlineData("Tree.AccessDeniedRoot", "Access denied root")]
	[InlineData("Key.Format0", "Value: {0}")]
	[InlineData("Key.Format2", "{0} + {1} = {2}")]
	public void Indexer_ReturnsEnglishValues(string key, string expected)
	{
		var service = new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En);

		var value = service[key];

		Assert.Equal(expected, value);
	}

	[Theory]
	// Verifies the indexer returns localized values for known keys in Russian.
	[InlineData("Key.Hello", "Привет")]
	[InlineData("Key.World", "Мир")]
	[InlineData("Tree.AccessDenied", "Доступ запрещен")]
	[InlineData("Tree.AccessDeniedRoot", "Доступ запрещен (корень)")]
	[InlineData("Key.Format0", "Значение: {0}")]
	[InlineData("Key.Format2", "{0} + {1} = {2}")]
	public void Indexer_ReturnsRussianValues(string key, string expected)
	{
		var service = new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.Ru);

		var value = service[key];

		Assert.Equal(expected, value);
	}

	[Theory]
	// Verifies missing keys fall back to the [[key]] placeholder.
	[InlineData("Missing.Key")]
	[InlineData("Tree.Missing")]
	[InlineData("Key.Unknown")]
	[InlineData("Missing.Format")]
	[InlineData("UI.Missing")]
	[InlineData("Settings.Unknown")]
	[InlineData("One.More")]
	[InlineData("Another.Missing")]
	[InlineData("Placeholder.Test")]
	[InlineData("Not.Found")]
	public void Indexer_MissingKeys_ReturnsPlaceholder(string key)
	{
		var service = new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En);

		var value = service[key];

		Assert.Equal($"[[{key}]]", value);
	}

	[Theory]
	// Verifies Format substitutes parameters into localized templates.
	[InlineData(AppLanguage.En, "Key.Format0", "Value: 5", 5)]
	[InlineData(AppLanguage.En, "Key.Format0", "Value: text", "text")]
	[InlineData(AppLanguage.En, "Key.Format2", "1 + 2 = 3", 1, 2, 3)]
	[InlineData(AppLanguage.Ru, "Key.Format0", "Значение: 5", 5)]
	[InlineData(AppLanguage.Ru, "Key.Format0", "Значение: текст", "текст")]
	[InlineData(AppLanguage.Ru, "Key.Format2", "1 + 2 = 3", 1, 2, 3)]
	public void Format_FormatsLocalizedStrings(AppLanguage language, string key, string expected, params object[] args)
	{
		var service = new LocalizationService(new StubLocalizationCatalog(CatalogData), language);

		var value = service.Format(key, args);

		Assert.Equal(expected, value);
	}

	[Fact]
	// Verifies CurrentLanguage returns the initial language passed to the service.
	public void CurrentLanguage_ReturnsInitialLanguage()
	{
		var service = new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.Ru);

		Assert.Equal(AppLanguage.Ru, service.CurrentLanguage);
	}

	[Fact]
	// Verifies SetLanguage updates the current language when different.
	public void SetLanguage_UpdatesCurrentLanguage()
	{
		var service = new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En);

		service.SetLanguage(AppLanguage.Ru);

		Assert.Equal(AppLanguage.Ru, service.CurrentLanguage);
	}

	[Fact]
	// Verifies SetLanguage raises LanguageChanged when language changes.
	public void SetLanguage_RaisesLanguageChanged()
	{
		var service = new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En);
		int callCount = 0;
		service.LanguageChanged += (_, _) => callCount++;

		service.SetLanguage(AppLanguage.Ru);

		Assert.Equal(1, callCount);
	}

	[Fact]
	// Verifies SetLanguage does not raise LanguageChanged when unchanged.
	public void SetLanguage_DoesNotRaiseWhenUnchanged()
	{
		var service = new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En);
		int callCount = 0;
		service.LanguageChanged += (_, _) => callCount++;

		service.SetLanguage(AppLanguage.En);

		Assert.Equal(0, callCount);
	}

	[Fact]
	// Verifies localized values update after switching languages.
	public void SetLanguage_UpdatesLocalizationLookup()
	{
		var service = new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En);

		Assert.Equal("Hello", service["Key.Hello"]);

		service.SetLanguage(AppLanguage.Ru);

		Assert.Equal("Привет", service["Key.Hello"]);
	}
}
