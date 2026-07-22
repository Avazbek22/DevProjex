namespace DevProjex.Tests.Integration;

public sealed class LocalizationCatalogAccessDeniedTests
{
	private const string Key = "Msg.AccessDeniedElevationRequired";
	private static readonly AppLanguage[] SupportedLanguages =
	[
		AppLanguage.En,
		AppLanguage.Ru,
		AppLanguage.Uz,
		AppLanguage.Tg,
		AppLanguage.Kk,
		AppLanguage.Fr,
		AppLanguage.De,
		AppLanguage.It,
		AppLanguage.Es,
		AppLanguage.Pt,
		AppLanguage.PtPt
	];

	[Theory]
	[MemberData(nameof(AllLanguageCases))]
	public void AccessDeniedMessage_ExistsInAllLanguages(AppLanguage language)
	{
		AssertKeyPresent(language);
	}

	[Theory]
	[MemberData(nameof(NonEnglishLanguageCases))]
	public void AccessDeniedMessage_NonEnglishLanguagesDifferFromEnglish(AppLanguage language)
	{
		AssertNotEnglish(language);
	}

	public static IEnumerable<object[]> AllLanguageCases()
	{
		foreach (var language in SupportedLanguages)
			yield return [ language ];
	}

	public static IEnumerable<object[]> NonEnglishLanguageCases()
	{
		foreach (var language in SupportedLanguages)
		{
			if (language == AppLanguage.En)
				continue;

			yield return [ language ];
		}
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
