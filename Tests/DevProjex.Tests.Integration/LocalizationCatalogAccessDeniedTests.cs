namespace DevProjex.Tests.Integration;

public sealed class LocalizationCatalogAccessDeniedTests
{
	private const string Key = "Msg.AccessDeniedElevationRequired";

	[Fact]
	public void AccessDeniedMessage_ExistsInEnglish()
	{
		AssertKeyPresent(AppLanguage.En);
	}

	[Fact]
	public void AccessDeniedMessage_ExistsInRussian()
	{
		AssertKeyPresent(AppLanguage.Ru);
	}

	[Fact]
	public void AccessDeniedMessage_ExistsInUzbek()
	{
		AssertKeyPresent(AppLanguage.Uz);
	}

	[Fact]
	public void AccessDeniedMessage_ExistsInTajik()
	{
		AssertKeyPresent(AppLanguage.Tg);
	}

	[Fact]
	public void AccessDeniedMessage_ExistsInKazakh()
	{
		AssertKeyPresent(AppLanguage.Kk);
	}

	[Fact]
	public void AccessDeniedMessage_ExistsInFrench()
	{
		AssertKeyPresent(AppLanguage.Fr);
	}

	[Fact]
	public void AccessDeniedMessage_ExistsInGerman()
	{
		AssertKeyPresent(AppLanguage.De);
	}

	[Fact]
	public void AccessDeniedMessage_ExistsInItalian()
	{
		AssertKeyPresent(AppLanguage.It);
	}

	[Fact]
	public void AccessDeniedMessage_RussianIsNotEnglish()
	{
		AssertNotEnglish(AppLanguage.Ru);
	}

	[Fact]
	public void AccessDeniedMessage_UzbekIsNotEnglish()
	{
		AssertNotEnglish(AppLanguage.Uz);
	}

	[Fact]
	public void AccessDeniedMessage_TajikIsNotEnglish()
	{
		AssertNotEnglish(AppLanguage.Tg);
	}

	[Fact]
	public void AccessDeniedMessage_KazakhIsNotEnglish()
	{
		AssertNotEnglish(AppLanguage.Kk);
	}

	[Fact]
	public void AccessDeniedMessage_FrenchIsNotEnglish()
	{
		AssertNotEnglish(AppLanguage.Fr);
	}

	[Fact]
	public void AccessDeniedMessage_GermanIsNotEnglish()
	{
		AssertNotEnglish(AppLanguage.De);
	}

	[Fact]
	public void AccessDeniedMessage_ItalianIsNotEnglish()
	{
		AssertNotEnglish(AppLanguage.It);
	}

	private static void AssertKeyPresent(AppLanguage language)
	{
		var catalog = new JsonLocalizationCatalog();
		var dict = catalog.Get(language);

		Assert.True(dict.TryGetValue(Key, out var value));
		Assert.False(string.IsNullOrWhiteSpace(value));
	}

	private static void AssertNotEnglish(AppLanguage language)
	{
		var catalog = new JsonLocalizationCatalog();
		var english = catalog.Get(AppLanguage.En)[Key];
		var value = catalog.Get(language)[Key];

		Assert.NotEqual(english, value);
	}
}
